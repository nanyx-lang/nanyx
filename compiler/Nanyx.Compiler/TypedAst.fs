namespace NyxCompiler

open Parser.Program

/// Identifies a declaration. Ids are unique within a process, like `NodeId`, so the symbols of the modules a
/// compilation imports never clash with its own.
type SymbolId = int64

module SymbolId =
    let private last = ref 0L
    let next () : SymbolId = System.Threading.Interlocked.Increment(&last.contents)

    /// Makes `first` the next id, if no id from it on has been given (see `NodeId.startAt`).
    let startAt (first: SymbolId) = System.Threading.Interlocked.Exchange(&last.contents, max last.Value (first - 1L)) |> ignore

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
    /// A unit of measure, declared by a `unit` line. A number written with it, `2km`, refers to it, and so does one in
    /// another unit's definition.
    | UnitOfMeasure

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

module ModuleOrigin =
    /// The name of the standard library module every other module imports without saying so (`nanyx/prelude`).
    let preludeName = "prelude"

    /// The name of the standard library module whose context, `HostRuntime`, the toolchain provides only to hosts
    /// (`nanyx/host.runtime`).
    let hostRuntimeName = "host.runtime"

    /// The name of the standard library module whose context, `Async`, a function that can wait requires
    /// (`nanyx/async`).
    let asyncName = "async"

/// The call the compiler makes the body of each def that starts a task into (see `TaskScopes`): `nanyx/async`'s
/// `Async.concurrent`, imported under an alias no code can write.
module TaskScopeSyntax =
    /// The scoped context a task belongs to, which `Async.concurrent` supplies.
    let context = "TaskScope"
    let alias = "__async"
    let scopeCall = alias + ".Async.concurrent"
    let private inCallersScopePrefix = "__inScope_"
    /// The made-up name of the def that runs the body of the scope def `name` in its caller's scope, which a scope def
    /// that calls `name` last calls instead (see `TaskScopes`).
    let inCallersScope (name: string) = inCallersScopePrefix + name
    /// The scope def whose body the def of a made-up name runs in its caller's scope (see `inCallersScope`).
    let (|InCallersScope|_|) (name: string) =
        if name.StartsWith inCallersScopePrefix then Some (name.Substring inCallersScopePrefix.Length) else None

/// What makes a nominal type the type it is: the module that defines it, and the name it is defined under there
/// (`Person`, or `money.Money` in a `module money =` block). Two modules that each define a `Person` define two
/// types.
type NominalId =
    { Home: ModuleOrigin
      Name: string }

/// The order a record type's named fields are written in, for showing it as written: `(string, start: int, length: int)`
/// rather than in the order of the names. It is no part of the type: two records with the same fields are one type
/// whatever the order of each, so an order equals every other. A record whose fields were never written in an order
/// (one inference builds from fields read) has none, and is shown in the order of the names.
[<CustomEquality; CustomComparison>]
type FieldOrder =
    | FieldOrder of string list

    member this.Names = let (FieldOrder names) = this in names

    override _.Equals(other) = other :? FieldOrder
    override _.GetHashCode() = 0

    interface System.IComparable with
        member _.CompareTo(_) = 0

module FieldOrder =
    /// No order: the fields are shown in the order of their names.
    let none = FieldOrder []

    /// The fields of a record written in the order of `fields`.
    let ofList (fields: (string * 'T) list) = FieldOrder(fields |> List.map fst)

    /// `order` less the fields not in `fields`.
    let within (fields: Map<string, 'T>) (FieldOrder names) = FieldOrder(names |> List.filter fields.ContainsKey)


/// What a type variable stands for: any type, or a scoped context's scope (see `FunctionContexts.scoped`).
type TyVarKind =
    | Ordinary
    /// The scope a call that supplies a scoped context starts, which stays the call's (see `ScopedCalls.enter`).
    | CallScope
    /// The scope of the scoped context of this name that a signature writes, shared by every one of that name in it.
    | SignatureScope of context: string

/// A dimension a measured value is of: base dimensions, each with its exponent, sorted by dimension and none with the
/// exponent 0, so that two equal dimensions are equal lists. A dimension declared with `dimension Time` is `Time` with
/// the exponent 1; the first step of units of measure writes no other (see `Units`).
type Dimension = (NominalId * int) list

/// Internal types used during type inference.
type TyVar =
    { Id: int
      Name: string option
      Kind: TyVarKind }

module TyVar =
    /// The variable `id`, named `name` if it is written in a type, that stands for any type.
    let ordinary (id: int) (name: string option) : TyVar = { Id = id; Name = name; Kind = Ordinary }

    /// The name of the `n`th context variable the compiler infers for an unannotated def, from 1: `'1`, `'2`, ... A
    /// call and an import find a def's context variables by name, so an inferred one needs a name while checking, but
    /// this one is not an identifier, so no annotation can write it. It is never shown: a reader sees `κ`, `λ`, ... (or
    /// `c`, `d`, ... in ASCII), given when the type is printed (see `Unifier.displayRenaming`).
    let inferredContextName (n: int) : string = $"'{n}"

    /// Whether `name` is one `inferredContextName` gives.
    let isInferredContextName (name: string) : bool =
        name.Length > 1 && name[0] = '\'' && name |> Seq.skip 1 |> Seq.forall System.Char.IsDigit

    /// The letters a context variable the compiler inferred is written with where Latin letters are, in order: c, d,
    /// ..., , as an annotation would name it.
    let inferredContextLetters = "cdefghijklmnopqrstuvwxyzab"

    /// The name a type that is printed on its own, with no other variables to keep clear of, gives the context variable
    /// named `name` (see `inferredContextName`): `c` for `'1`, `d` for `'2`, ..., then `c1`, ...
    let inferredContextFallback (name: string) : string =
        let letters = inferredContextLetters
        let index = int (name.Substring 1) - 1
        let round = index / letters.Length
        if round = 0 then string letters[index] else $"{letters[index % letters.Length]}{round}"

    /// Whether `v` is a context variable the compiler inferred (see `inferredContextName`).
    let isInferredContext (v: TyVar) : bool = v.Name |> Option.exists isInferredContextName

type Ty =
    | TyVar of TyVar
    | TyPrimitive of string
    /// A nominal type: its identity, the arguments it is applied to, the type it is made from (its definition's, with
    /// those arguments for its parameters), and whether that is private to its home module. It is written by its own
    /// name and its arguments, `Person` or `Box(int)`.
    | TyNominal of NominalId * Ty list * Ty * bool
    /// A function: its input, its result, and the contexts it requires where it is called (see `FunctionContexts`).
    /// Each context is one applied to its arguments, `TyApply("Console", [])` or `TyApply("Show", [a])`, or a context
    /// variable, `c` in `[c] a -> b`, which stands for any contexts. A function that requires none is pure.
    | TyFunc of Ty * Ty * Ty list
    /// A record: its fields, by name (a positional field's is its index, `0`), and the order its named ones are written
    /// in, which only showing it reads (see `FieldOrder`).
    | TyRecord of Map<string, Ty> * FieldOrder
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
    /// What a recursive alias stands for, closed: its definition, in which its parameters are `TyParam`s and it names
    /// itself as `TySelf`, so that it has no type variables, and the arguments it is applied to. It is only ever what
    /// a `TyAlias` stands for (see `RecursiveAliases`).
    | TyRecursive of body: Ty * args: Ty list
    /// The recursive alias being defined, named inside its own definition, with its arguments.
    | TySelf of Ty list
    /// The parameter at this position of the recursive alias whose definition this is in.
    | TyParam of int
    /// A scoped context's scope, a type variable, as the context and a cell's type hold it (see
    /// `FunctionContexts.scoped`). A reader never sees it.
    | TyScope of Ty
    /// The contexts a type generic in contexts carries, as the last argument of its nominal type (see
    /// `FunctionContexts.carried`). A reader writes them in front of the type, `[FileSystem] Stream(string)`.
    | TyContexts of Ty list
    /// A measured value: a number of the storage type with a dimension, `Time(int)`, which is its storage at run time,
    /// measured in the dimension's default storage unit (see `Units`).
    | TyMeasure of Dimension * storage: Ty

/// The types a type is made of, one level down, for walks that treat most cases alike: such a walk matches the cases it
/// treats differently, and hands the others to `Ty.map`, `Ty.fold` or `Ty.exists`.
module Ty =
    /// The types `ty` is made of, one level down: a nominal's or an alias's arguments then what it stands for, a function's input, result and contexts, a record's fields in order of their names, an open record's fields
    /// then its row as a variable, a tag's payload, an applied type's arguments, a union's members, the contexts a type
    /// carries, a closed recursive definition then its arguments, the arguments of a reference to the alias inside
    /// one, and a measured value's storage.
    let children (ty: Ty) : Ty list =
        match ty with
        | TyVar _
        | TyPrimitive _
        | TyParam _ -> []
        | TyRecursive(body, args) -> body :: args
        | TySelf args -> args
        | TyNominal(_, args, underlying, _) -> args @ [ underlying ]
        | TyAlias(_, args, underlying) -> args @ [ underlying ]
        | TyFunc(input, output, contexts) -> input :: output :: contexts
        | TyRecord(fields, _) -> fields |> Map.toList |> List.map snd
        | TyOpenRecord(fields, row) -> (fields |> Map.toList |> List.map snd) @ [ TyVar row ]
        | TyTag(_, payload) -> Option.toList payload
        | TyApply(_, args)
        | TyUnion args
        | TyContexts args -> args
        | TyScope scope -> [ scope ]
        | TyMeasure(_, storage) -> [ storage ]

    /// `ty` with `f` applied to each of the types it is made of, one level down (see `children`). An open record's row
    /// is kept as it is.
    let map (f: Ty -> Ty) (ty: Ty) : Ty =
        match ty with
        | TyVar _
        | TyPrimitive _
        | TyParam _ -> ty
        | TyRecursive(body, args) -> TyRecursive(f body, List.map f args)
        | TySelf args -> TySelf(List.map f args)
        | TyNominal(id, args, underlying, isPrivate) -> TyNominal(id, List.map f args, f underlying, isPrivate)
        | TyAlias(name, args, underlying) -> TyAlias(name, List.map f args, f underlying)
        | TyFunc(input, output, contexts) -> TyFunc(f input, f output, List.map f contexts)
        | TyRecord(fields, order) -> TyRecord(Map.map (fun _ field -> f field) fields, order)
        | TyOpenRecord(fields, row) -> TyOpenRecord(Map.map (fun _ field -> f field) fields, row)
        | TyTag(name, payload) -> TyTag(name, Option.map f payload)
        | TyApply(name, args) -> TyApply(name, List.map f args)
        | TyUnion items -> TyUnion(List.map f items)
        | TyScope scope -> TyScope(f scope)
        | TyContexts contexts -> TyContexts(List.map f contexts)
        | TyMeasure(dimension, storage) -> TyMeasure(dimension, f storage)

    /// `f` folded over the types `ty` is made of, one level down, in the order `children` gives them.
    let fold (f: 'State -> Ty -> 'State) (state: 'State) (ty: Ty) : 'State = children ty |> List.fold f state

    /// Whether `predicate` holds for one of the types `ty` is made of, one level down.
    let exists (predicate: Ty -> bool) (ty: Ty) : bool = children ty |> List.exists predicate

module Dimension =
    /// The dimension `id` declares, alone.
    let ofBase (id: NominalId) : Dimension = [ id, 1 ]

    /// The base dimensions of `parts`, each once, with the sum of its exponents, but for those that sum to none, in
    /// order: so a dimension is one list however it is written, and `Length * Time / Time` is `Length`.
    let private normalized (parts: (NominalId * int) list) : Dimension =
        parts
        |> List.groupBy fst
        |> List.map (fun (id, each) -> id, each |> List.sumBy snd)
        |> List.filter (fun (_, exponent) -> exponent <> 0)
        |> List.sortBy fst

    /// The dimension of a product of a value of `left` and one of `right`: their exponents added.
    let multiply (left: Dimension) (right: Dimension) : Dimension = normalized (left @ right)

    /// The dimension of a value of `left` divided by one of `right`: `right`'s exponents taken off `left`'s.
    let divide (left: Dimension) (right: Dimension) : Dimension =
        normalized (left @ (right |> List.map (fun (id, exponent) -> id, -exponent)))

    /// Whether `dimension` is a product or quotient of dimensions, not one alone.
    let isDerived (dimension: Dimension) : bool =
        match dimension with
        | [ _, 1 ] -> false
        | _ -> true

    /// The base dimensions `dimension` multiplies by, each as many times as its exponent says, and those it divides by.
    let parts (dimension: Dimension) : NominalId list * NominalId list =
        let repeated (sign: int) =
            dimension |> List.collect (fun (id, exponent) -> if sign * exponent > 0 then List.replicate (abs exponent) id else [])
        repeated 1, repeated -1

    /// `dimension` written with `written` for each base dimension, joined by `times` and `over`: `Length / Time`,
    /// `Time * Time`, or `Length / (Time * Time)`, with a divisor of more than one part in brackets. A dimension of
    /// nothing above a divisor is written `1`.
    let writtenWith (written: NominalId -> string) (times: string) (over: string) (dimension: Dimension) : string =
        let above, below = parts dimension
        let product (ids: NominalId list) = ids |> List.map written |> String.concat times
        let dividend = if above.IsEmpty then "1" else product above
        match below with
        | [] -> dividend
        | [ single ] -> dividend + over + written single
        | _ -> $"{dividend}{over}({product below})"

    /// `dimension` as it is written: `Time`, or a product and quotient of dimensions, `Length / Time`.
    let text (dimension: Dimension) : string = writtenWith _.Name " * " " / " dimension

    /// `dimension` written where it is applied to a number, as a measured value is written by `dbg`: `Time`, or a
    /// derived dimension in brackets, `(Length / Time)`, so that `(Length / Time)(1.0)` doesn't read as `Length` over
    /// `Time(1.0)` (#2970).
    let applied (dimension: Dimension) : string = if isDerived dimension then $"({text dimension})" else text dimension

    /// A value of `dimension` stored as `storage`: a measured value, or the plain number a dimension of nothing is, as
    /// `Length / Length` is.
    let measured (dimension: Dimension) (storage: Ty) : Ty =
        if dimension.IsEmpty then storage else TyMeasure(dimension, storage)

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
/// the type is defined under in `Home`, the module that defines it, whatever it is written as elsewhere (`m.Email`
/// under an alias): the two are a nominal type's identity (see `TypeDefInfo.nominalId`).
/// An attribute written before a field of a record type's definition, `@json(name = "user_id")`: its name, and its
/// argument's values as text, each by its field's name, or by its position in a tuple (`"0"` for a lone value). A
/// type's description gives them to a library that reads the field (see `TypeValues.descriptionMembers`).
type FieldAttribute =
    { Name: string
      Arguments: (string * string) list }

type TypeDefInfo =
    { Name: string
      Home: ModuleOrigin
      Parameters: TyVar list
      Underlying: Ty
      /// Declared `protected` or `private`: a nominal type, as opposed to an alias of its shape.
      IsNominal: bool
      IsPrivate: bool
      IsContext: bool
      /// Declared `scoped context`.
      IsScoped: bool
      /// The scoped context whose scope each value of the type belongs to, as `type Cursor = [Tx] protected (...)`
      /// declares it: the value carries the scope as a last argument its type is applied to, which is never shown.
      ScopedBy: string option
      /// For a type in a scoped context's scope, the variable `Underlying` writes that scope as: what the type's
      /// representation holds of the scope is of the value's own (see `TypeResolution.undeclaredHeldScopes`).
      Scope: TyVar option
      /// For a type generic in contexts, as `type Stream(a) = [k] protected (...)` declares it, the context variable
      /// `Underlying` writes its contexts as. A value carries its contexts as a last argument its type is applied to (see
      /// `FunctionContexts.carried`), after the scope of a type in a scoped context's scope, if any.
      ContextVariable: TyVar option
      /// A context's associated types, by name: the last of its `Parameters`, which a signature does not write (see
      /// `AssociatedTypes`). Empty for any other type.
      AssociatedTypes: string list
      Symbol: SymbolId option
      Members: Map<string, SymbolId>
      /// The doc comment the definition has, as Markdown, or empty: what the type's description says of it.
      Doc: string
      /// The attributes written before each field of a record type's definition, by the field's name.
      FieldAttributes: Map<string, FieldAttribute list>
      /// Declared `dimension Time` or `dimension Bytes(int)`: a kind of quantity, a private nominal type generic in the
      /// number that stores it unless constrained to one (see `Units`).
      IsDimension: bool }

module TypeDefInfo =
    /// The identity of the nominal type `info` defines.
    let nominalId (info: TypeDefInfo) : NominalId = { Home = info.Home; Name = info.Name }

/// An attribute that marks a unit: `@storageUnit`, which sets its dimension's default storage unit by hand, or
/// `@baseUnit`, which marks its default display unit (see `Units`).
type UnitMark =
    | StorageUnitMark
    | DisplayUnitMark

/// A unit of measure, `unit ms: Time = 1_000us`: its dimension's identity, how many of the dimension's default storage
/// units it is, exactly, and the module it is declared in, which exports it if `IsExport` (see `Units`). A unit's name
/// is of a kind of its own, apart from values and types.
type UnitInfo =
    { Name: string
      /// What it is a unit of: a dimension, `Time`, or a derived dimension, `Length / Time`.
      Dimension: Dimension
      /// How many of its dimension's default storage units it is; for a unit of a derived dimension, how many of its
      /// parts' default storage units, multiplied and divided as the parts are.
      Scale: Rational
      Home: ModuleOrigin
      IsExport: bool
      /// What it is declared as, as written: a multiple of another unit, `1_000us`, or of its parts' units, `1km / 1hr`;
      /// none for the unit its dimension's others are defined from.
      Multiple: Expression option
      /// The attributes written before it, `@storageUnit` and `@baseUnit`.
      Marks: UnitMark list
      /// Whether it is its dimension's default display unit: the one `@baseUnit` marks, or, if none is marked, its
      /// default storage unit.
      IsDisplay: bool
      /// The symbol of its name, which a number written with it refers to (see `UnitOfMeasure`).
      Symbol: SymbolId option }

module UnitInfo =
    /// Whether `info` is its dimension's default storage unit, the one its values are stored as a number of.
    let isStorage (info: UnitInfo) = info.Scale = Rational.one

/// A definition marked `@component`, which a component built from the program exports, in the WIT interface its module
/// names (see `ComponentInterface`).
type ComponentExport =
    { /// The module it is defined in, whose name is the interface's.
      Module: string
      /// Its name in that module.
      Name: string
      /// The name the program knows it by: its own in its module, or the one an import inlines it under.
      Definition: string
      /// Whether it can wait: the type written for it requires `Async`. It is then an `async func`.
      Waits: bool }

/// A definition marked `@test`, which a test build runs (see `TypedModule.Tests`).
/// The crash a test marked `@test(#crashes)` expects, which it passes only by reaching (#3078).
type ExpectedCrash =
    /// Any crash: `@test(#crashes)`.
    | AnyCrash
    /// A crash whose message contains the text: `@test(#crashes("too big"))`.
    | CrashContaining of text: string

type Test =
    { /// The module it is defined in, which groups it with the module's other tests.
      Module: string
      /// Its name in that module: a test is reported as `` geometry.`adds points` ``.
      Name: string
      /// The name the program knows it by: its own in its module, or the one an import inlines it under.
      Definition: string
      /// Where it is written.
      Span: SourceSpan
      /// Whether it is marked `@test(#io)`, and so runs with the console host's real `Console`, `Env` and `FileSystem`
      /// (see `Hosts.ioTestContexts`).
      Io: bool
      /// The crash it is marked to expect, if it is: it passes only by reaching one.
      Crashes: ExpectedCrash option }

/// A member of an ambient context (one the runtime supplies, as the host's prelude's definitions are its `Console`) and the
/// top-level definition that stands in for it (see `TypedModule.AmbientMembers`).
type AmbientMember =
    { Context: string
      Member: string
      Definition: string }

module AmbientMember =
    /// The name an import gives the definition that stands in for `memberName` of the ambient context it imports as
    /// `context`: the member's own name, or under the alias of an aliased import (`c.println` for `c.Console`).
    let definitionName (context: string) (memberName: string) =
        match context.LastIndexOf '.' with
        | dot when dot > 0 -> $"{context.Substring(0, dot)}.{memberName}"
        | _ -> memberName

/// What provides a context where a call requires it, such as `Console`, or `Sum(Point)` from an instance in scope (see
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

/// Where a call finds an instance of a parameterised context its callee requires, such as `Sum(Point)` for a callee
/// that requires `[Sum(a)]`.
type ContextInstance =
    /// An instance in scope where the call is: one a `use` loads, or one the type of the function around the call
    /// declares or, where it does not write its type, was inferred to require. The context is as it is written there
    /// (`Sum(Point)`, `Sum(a)`), with what put it there.
    | InstanceInScope of TypeExpr * ContextProvider
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
/// has no name to record it by, and is left out. Before those come the parameterised contexts the call finds an
/// instance of in scope (`InstanceInScope` in `ContextInstances`), each as written where it is put in scope, over
/// variables of their own that keep its names (`Sum(a)`); one the compiler builds, derives or writes itself has no
/// provider, and is left out.
///
/// `UseContexts` holds the instance of a parameterised context each `use` loads, by the node of the value it loads:
/// `Sum(int)` for `use addInt`, where `def addInt: Sum(int) = (...)`. It is written as `InstanceInScope` writes it, so
/// that the backend knows the members the `use` loads by the same names a call finds them by.
///
/// `InferredContextTypes` holds the parameterised contexts each def that does not write its type was inferred to
/// require, top-level or local, by the def's symbol, as types over the variables of its type in `SymbolTypes`: `Show(a)` is
/// `TyApply("Show", [a])`. Resolved for the root module only, as `SymbolTypes` is.
///
/// `ContextTypes` holds the contexts each of the module's own top-level defs requires, written or inferred, and those
/// each local def requires, written or inferred, by the def's symbol, as types over the variables of its type in
/// `SymbolTypes`: those in `InferredContextTypes`, then the ones it writes or needs of the runtime (`Console`). Resolved
/// for the root module only, as `SymbolTypes` is. `LocalContexts` holds the same contexts of each local def as written,
/// or as the type checker writes those it infers: as an instance in scope in the def's value has them (`InstanceInScope`).
/// `ImportedContextTypes` holds the same for each def an import brings in, by its symbol, with the def's type they are
/// over: resolved as the module that declares it resolved them, and with variables of their own that keep the names its
/// annotation gives them. A def that requires no contexts has none.
///
/// `AmbientContexts` are the names of the imported contexts the runtime supplies, as the host's prelude's definitions are
/// its `Console`, in order: the top level of the module runs in them.
///
/// `AmbientMembers` holds the definition in `Module` that stands in for each member of an ambient context: of those in
/// `AmbientContexts`, and of those carried in with the imported functions that require them, which the module cannot
/// name (a module that imports a function requiring `Console` without importing `nanyx.cli/console`, and that sees no host's prelude). The backend passes
/// these where nothing else supplies the context.
///
/// `ImportedFiles` holds the module each of the module's imports brings in, by the import's symbol (see `ImportedFile`).
type TypedModule =
    { Module: Module
      Types: Map<string, Ty>
      TypeDefs: Map<string, TypeDefInfo>
      /// The module's items, typed. Lazy, since only tools ask for them: a module read from the checked library reads
      /// them only then (see `CheckedLibrary`).
      Items: Lazy<TypedTopLevelItem list>
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
      /// What the associated types of the contexts each call's callee requires are at the call, by the call's node and by
      /// the names the callee's signature gives them: `elem` is `int` where `countAll: [Iter(c)] c -> int` is called with a
      /// `Bag` whose `next` gives `int`s. The backend specialises the callee with them, since its type need not write them.
      AssociatedArguments: Map<NodeId, Map<string, Ty>>
      InferredContextTypes: Map<SymbolId, Ty list>
      ContextTypes: Map<SymbolId, Ty list>
      LocalContexts: Map<SymbolId, TypeExpr list>
      ImportedContextTypes: Map<SymbolId, Ty * Ty list>
      AmbientContexts: string list
      AmbientMembers: AmbientMember list
      /// The definitions a component built from the module exports: its own marked `@component`, and those of the
      /// modules it imports, by the names they are inlined under (see `ComponentExport`).
      ComponentExports: ComponentExport list
      /// The module's tests, its own marked `@test` and those of the modules it imports, by the names they are inlined
      /// under (see `Test`).
      Tests: Test list
      ImportedFiles: Map<SymbolId, ImportedFile>
      InlinedExprTypes: Map<NodeId, Ty>
      InlinedCalleeTypes: Map<NodeId, Ty>
      /// A script's values that need a context, which run in its entry point (see `InferState.ScriptEntryValues`).
      ScriptEntryValues: Set<Identifier>
      /// The units of measure the module can write, by name: its own and those its imports bring in (see `Units`).
      Units: Map<string, UnitInfo>
      /// The names the module's own nodes declare, as the parser recorded them, each where it is written: a record's
      /// fields and a context's members among them, which have no nodes of their own (see `DeclaredNames`).
      Declared: DeclaredNames }
