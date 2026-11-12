namespace NyxCompiler

open Parser.Program

/// Identifies a declaration. Ids are unique within a process, like `NodeId`, so the symbols of the modules a
/// compilation imports never clash with its own.
type SymbolId = int64

module SymbolId =
    let private last = ref 0L
    let next () : SymbolId = System.Threading.Interlocked.Increment(&last.contents)

type SymbolKind =
    /// A def at the top level of a module.
    | TopLevelValue
    /// A def in a block.
    | LocalValue
    | Parameter
    /// A name a pattern binds, in a match arm or an `if ... is`.
    | PatternVariable
    /// An import: the module it brings in, under its alias if it has one.
    | ImportedModule
    /// A type definition or a context. A name written in a type refers to it; it is not a value in scope.
    | TypeDefinition
    /// A member of a context, declared in the context's definition. A name in a def that requires the context, or
    /// a qualified `Context.member`, refers to it.
    | ContextMember

/// A property a built-in type has, read with a dot as a record's field is: `s.length`. A dot on a value reads only
/// these and a record's fields, never a definition attached to the value's type; that is a pipe's.
type BuiltInProperty =
    /// A string's `length`: its number of bytes, as `string.length` gives it.
    | StringLength
    /// A list's `length`: its number of elements, as `list.length` gives it.
    | ListLength

/// Something a name can refer to, and where it is declared. A symbol from an imported module is declared in
/// that module's file.
type Symbol =
    { Id: SymbolId
      Name: string
      Kind: SymbolKind
      Declaration: SourceSpan
      IsExported: bool }

/// Where a name can be written and mean a symbol: the text from its declaration to the end of what declares it.
module Scope =
    /// The scope of a name visible throughout a module, wherever it is declared: a top-level def or an import.
    let wholeModule (file: string) : SourceSpan =
        { File = file; Start = 0; End = System.Int32.MaxValue; StartLine = 0; StartColumn = 0 }

    /// The scope of a name declared at `declaration` and visible until `scopeEnd`: the end of the block for a local,
    /// of the lambda for a parameter, of the arm for a pattern variable. A local def is in scope in its own value, so
    /// it can call itself.
    let until (scopeEnd: int) (declaration: SourceSpan) : SourceSpan =
        { declaration with End = max scopeEnd declaration.End }

/// Where a module's source came from, which decides how its imports are resolved and what is in scope.
type ModuleOrigin =
    /// A file on disk: imports by path are relative to its directory.
    | SourceFile of fullPath: string
    /// A module of the embedded standard library, checkedModule with the compiler intrinsics in scope.
    | StandardLibraryModule of name: string
    /// Source given directly (e.g. by tests): only standard-library imports can be resolved.
    | InMemory

/// What makes a nominal type the type it is: the module that defines it, and the name it is defined under there
/// (`$Person`, or `money.$Money` in a `module money =` block). Two modules that each define a `$Person` define two
/// types.
type NominalId =
    { Home: ModuleOrigin
      Name: string }

/// Internal types used during type inference.
type TyVar =
    { Id: int
      Name: string option }

type Ty =
    | TyVar of TyVar
    | TyPrimitive of string
    /// A nominal type: its identity, the type it is made from, and whether that is private to its home module. It is
    /// written by its own name, `$Person`.
    | TyNominal of NominalId * Ty * bool
    /// A function: its input, its result, and the contexts it requires where it is called (see `FunctionContexts`).
    /// Each context is one applied to its arguments, `TyApply("Console", [])` or `TyApply("Show", [a])`, or a context
    /// variable, `c` in `[c] a -> b`, which stands for any contexts. A function that requires none is pure.
    | TyFunc of Ty * Ty * Ty list
    | TyRecord of Map<string, Ty>
    /// A record known to have at least these fields, its others standing for the row variable: what reading `p.name`
    /// tells inference about a `p` of no other known type. The variable is solved to a record (or another open one)
    /// with the remaining fields, as a union's rest variable is to its remaining tags.
    | TyOpenRecord of Map<string, Ty> * TyVar
    | TyTag of string * Ty option
    | TyApply of string * Ty list
    | TyUnion of Ty list
    /// A type alias by name, with its arguments, and the type it stands for. It is that type in every way but how it
    /// is written: inference, unification and codegen look straight through it, and a type shown to a reader keeps
    /// the name the user gave it (`Customer`, not `(name: string)`). An alias that names itself holds its definition
    /// closed instead, and is unfolded where it is looked through (see `RecursiveAliases`).
    | TyAlias of string * Ty list * Ty

/// A simple typed expression placeholder for future expansion. `Arguments` are a call's arguments (a pipe's
/// include the piped value first), so a lambda passed to a call keeps its inferred type.
type TypedExpr =
    { Expr: Expression; Type: Ty; Body: TypedExpr option; Statements: TypedStatement list option; MatchArms: TypedMatchArm list option
      Arguments: TypedExpr list option }

and TypedPattern =
    { Pattern: Pattern; Type: Ty }

and TypedMatchArm = TypedPattern list * TypedExpr

and TypedStatement =
    | TypedDefStatement of bool * Identifier * TypeExpr option * TypedExpr
    | TypedExprStatement of TypedExpr
    | TypedImportStatement of ImportItem list
    | TypedTypeDefStatement of Identifier * TypeDefModifier list * (Identifier * TypeExpr option) list * TypeExpr
    | TypedUseStatement of TypedExpr

type TypedDefinition =
    | TypedValueDef of bool * Identifier * TypeExpr option * TypedExpr
    | TypedTypeDef of Identifier * TypeDefModifier list * (Identifier * TypeExpr option) list * TypeExpr

type TypedTopLevelItem =
    | TypedModuleDecl of ModuleName
    | TypedImport of ImportItem list
    | TypedDef of TypedDefinition
    | TypedExprItem of TypedExpr

/// A type definition: its parameters, the type it stands for, and the symbol it declares (`None` for one the
/// compiler made up, which has no name written in the source). Each parameter's variable is named as the parameter
/// is written. A context's `Members` are its members' symbols, by name; any other type has none. `Name` is the name
/// the type is defined under in `Home`, the module that defines it, whatever it is written as elsewhere (`m.$Email`
/// under an alias): the two are a nominal type's identity (see `TypeDefInfo.nominalId`).
type TypeDefInfo =
    { Name: string
      Home: ModuleOrigin
      Parameters: TyVar list
      Underlying: Ty
      IsPrivate: bool
      IsContext: bool
      Symbol: SymbolId option
      Members: Map<string, SymbolId> }

module TypeDefInfo =
    /// The identity of the nominal type `info` defines.
    let nominalId (info: TypeDefInfo) : NominalId = { Home = info.Home; Name = info.Name }

/// Where a call finds an instance of a parameterised context its callee requires, such as `Sum(Point)` for a callee
/// that requires `[Sum(a)]`.
type ContextInstance =
    /// An instance in scope where the call is: one a `use` loads, or one the type of the function around the call
    /// declares. The context is as it is written there (`Sum(Point)`, `Sum(a)`).
    | InstanceInScope of TypeExpr
    /// An instance the compiler builds from the definitions attached to the type: each member's name, and the
    /// definition it is (`+`, `Point.+`).
    | InstanceSynthesized of (string * string) list
    /// An instance the compiler derives from the parts of a type with type variables in it (see
    /// `StructuralInstances`): its one member, `memberName`, is the definition `definition`, which requires the context
    /// of each variable and finds them as a call at the node `at` would (`TypedModule.ContextInstances`). The backend
    /// passes it as a closure over the definition and those instances.
    | InstanceDerived of memberName: string * definition: string * at: NodeId
    /// The compiler's own `ToString` of a built-in type, at an interpolation hole (see `InterpolationHoles`): the
    /// backend writes the value itself.
    | InstanceBuiltIn

/// What provides a context that is not parameterised, such as `Console`, where a call requires it (see
/// `TypedModule.ContextProviders`).
type ContextProvider =
    /// The type of the def around the call declares it: the context as the annotation writes it.
    | FromSignature of SourceSpan
    /// A `use` around the call loads it: the node of the value the `use` loads, as `TypedModule.UseContexts` has it.
    | FromUse of NodeId
    /// The def around the call does not write its type, and is inferred to require the context itself.
    | FromInference
    /// The runtime supplies it, at the top level of the module (see `TypedModule.AmbientContexts`).
    | FromRuntime

/// A context a call requires, as a type (`Console` is `TyApply("Console", [])`), and what provides it there.
type ProvidedContext = { Context: Ty; Provider: ContextProvider }

/// The module an import brings in: the file it is read from, as its spans name it (a full path, or a standard-library
/// module's import path, `nanyx/text.nyx`), and the name its `module` line declares, if it has one.
type ImportedFile = { File: string; Name: string option }

/// `ExprTypes` holds the resolved type of every expression inference typed, and of every `_` written in a type that
/// it read, by node; `CalleeTypes` the type of
/// the function at each call or pipe, by the call's node; `SymbolTypes` the type of each declaration the module
/// makes itself, by its symbol. Type variables left in them are generalised ones. All three are filled in for the
/// root module of a compile only.
///
/// `Symbols` holds what the module declares and the imported definitions it refers to. `References` says which
/// symbol each name resolved to, by the node of the identifier, call, pipe or qualified name; `ImportReferences`
/// says which import a name brought in by an import came through, a type's name in a type expression among them. `AnnotatedSymbols` are the declarations that
/// write their own type: an annotated def or lambda parameter. `Scopes` says where each name can be written
/// unqualified (see `Scope`): the module's own declarations, the aliases of its imports, and the names its imports
/// and the prelude bring in unqualified, less those the module declares itself.
///
/// `ModuleNames` are the names that can be written anywhere in the module, dotted or not, and the symbol each
/// means: the module's own top-level defs (less those a `module name =` block keeps to itself) and what its imports
/// and the prelude bring in, under the import's alias where it has one (`m.foo`), less what the module declares
/// itself. `ImportedTypes` holds the type each imported definition among them has, as its module gave it.
///
/// `InlinedExprTypes` and `InlinedCalleeTypes` are as `ExprTypes` and `CalleeTypes`, for the nodes of the definitions
/// inlined from the modules the module imports (see `Module`): each resolved as the module it came from resolved it, so
/// that the backend can compile an imported generic definition from its types as it does one of the module's own.
/// Filled in for the root module only, and kept apart from its own types so that what reads those sees only them.
///
/// `ContextInstances` says, by the node of each call (or pipe) to a function that requires a parameterised context,
/// where the call finds each context the callee's type declares, in the order it declares them: `None` for one that is
/// not parameterised, or whose instance is left to be found by its members' names.
///
/// `ContextProviders` says, by the node of each call (or pipe), what provides each context found by its members'
/// names that the call requires: each such context the callee's type declares or was inferred to require, those a
/// function value's type requires, and the context of a member the call calls (`println` is `Console`'s). A context
/// whose members a `use` or the def's signature binds is provided by that; one the ambient instance supplies, by the
/// runtime at the top level, or by inference in a def that does not write its type. A context written out as a record
/// has no name to record it by, and is left out.
///
/// `UseContexts` holds the instance of a parameterised context each `use` loads, by the node of the value it loads:
/// `Sum(int)` for `use addInt`, where `def addInt: Sum(int) = (...)`. It is written as `InstanceInScope` writes it, so
/// that the backend knows the members the `use` loads by the same names a call finds them by.
///
/// `InferredContextTypes` holds the parameterised contexts each top-level def that does not write its type was
/// inferred to require, by the def's symbol, as types over the variables of its type in `SymbolTypes`: `Show(a)` is
/// `TyApply("Show", [a])`. Resolved for the root module only, as `SymbolTypes` is.
///
/// `ContextTypes` holds the contexts each of the module's own top-level defs requires, written or inferred, and those
/// each local def that writes its type writes, by the def's symbol, as types over the variables of its type in `SymbolTypes`: those in `InferredContextTypes`, then the
/// ones it writes or needs of the runtime (`Console`). Resolved for the root module only, as `SymbolTypes` is.
/// `ImportedContextTypes` holds the same for each def an import brings in, by its symbol, with the def's type they are
/// over: resolved as the module that declares it resolved them, and with variables of their own that keep the names its
/// annotation gives them. A def that requires no contexts has none.
///
/// `AmbientContexts` are the names of the imported contexts the runtime supplies, as `nanyx/console`'s definitions are
/// its `Console`, in order: the top level of the module runs in them.
///
/// `ImportedFiles` holds the module each of the module's imports brings in, by the import's symbol (see `ImportedFile`).
type TypedModule =
    { Module: Module
      Types: Map<string, Ty>
      TypeDefs: Map<string, TypeDefInfo>
      Items: TypedTopLevelItem list
      ExprTypes: Map<NodeId, Ty>
      CalleeTypes: Map<NodeId, Ty>
      SymbolTypes: Map<SymbolId, Ty>
      Symbols: Map<SymbolId, Symbol>
      AnnotatedSymbols: Set<SymbolId>
      References: Map<NodeId, SymbolId>
      ImportReferences: Map<NodeId, SymbolId>
      Scopes: Map<SymbolId, SourceSpan>
      ModuleNames: Map<string, SymbolId>
      ImportedTypes: Map<SymbolId, Ty>
      ContextInstances: Map<NodeId, ContextInstance option list>
      ContextProviders: Map<NodeId, ProvidedContext list>
      UseContexts: Map<NodeId, TypeExpr>
      InferredContextTypes: Map<SymbolId, Ty list>
      ContextTypes: Map<SymbolId, Ty list>
      ImportedContextTypes: Map<SymbolId, Ty * Ty list>
      AmbientContexts: string list
      ImportedFiles: Map<SymbolId, ImportedFile>
      InlinedExprTypes: Map<NodeId, Ty>
      InlinedCalleeTypes: Map<NodeId, Ty> }
