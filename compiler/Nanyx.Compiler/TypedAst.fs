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

/// Something a name can refer to, and where it is declared. A symbol from an imported module is declared in
/// that module's file.
type Symbol =
    { Id: SymbolId
      Name: string
      Kind: SymbolKind
      Declaration: SourceSpan
      IsExported: bool }

/// Internal types used during type inference.
type TyVar =
    { Id: int
      Name: string option }

type Ty =
    | TyVar of TyVar
    | TyPrimitive of string
    | TyNominal of string * Ty * bool
    | TyFunc of Ty * Ty
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
    /// the name the user gave it (`Customer`, not `(name: string)`).
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
    | TypedUseStatement of UseBinding * TypedExpr option

type TypedDefinition =
    | TypedValueDef of bool * Identifier * TypeExpr option * TypedExpr
    | TypedTypeDef of Identifier * TypeDefModifier list * (Identifier * TypeExpr option) list * TypeExpr

type TypedTopLevelItem =
    | TypedModuleDecl of ModuleName
    | TypedImport of ImportItem list
    | TypedDef of TypedDefinition
    | TypedExprItem of TypedExpr

type TypeDefInfo = { Parameters: TyVar list; Underlying: Ty; IsPrivate: bool }

/// `ExprTypes` holds the resolved type of every expression inference typed, by node; `CalleeTypes` the type of
/// the function at each call or pipe, by the call's node; `SymbolTypes` the type of each declaration the module
/// makes itself, by its symbol. Type variables left in them are generalised ones. All three are filled in for the
/// root module of a compile only.
///
/// `Symbols` holds what the module declares and the imported definitions it refers to. `References` says which
/// symbol each name resolved to, by the node of the identifier, call, pipe or qualified name; `ImportReferences`
/// says which import a name brought in by an import came through. `AnnotatedSymbols` are the declarations that
/// write their own type: an annotated def or lambda parameter.
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
      ImportReferences: Map<NodeId, SymbolId> }
