/// The state type inference threads through a module, and the helpers that read and extend it.
module NyxCompiler.InferState

open Parser.Program
open NyxCompiler

/// Which contexts the code being inferred runs in.
type ContextScope =
    /// The top level of a module, which runs in the ambient instances (the runtime's `Console`).
    | AmbientScope
    /// The body of the def `name`. One that writes its type has only the contexts the type declares; one that does
    /// not (`inferring`) needs whichever ambient contexts its body uses, which are inferred.
    | DefScope of name: Identifier * inferring: bool

/// A use of the unannotated top-level def `Callee`, whose contexts are only known once every def has been inferred:
/// `Available` are the ambient contexts something nearer than the ambient instance supplies at `Span`.
type DeferredContextUse =
    { Caller: ContextScope
      Callee: Identifier
      Span: SourceSpan
      Available: Set<Identifier> }

/// A call, in the unannotated top-level def `Def`, that requires the parameterised context `Context` at `ArgTys`,
/// types the call alone does not settle and for which no instance is in scope: `Show(t)` where `t` is a parameter's
/// type. Once the def is inferred, the context is found for the types they turned out to be, or, where they are
/// variables of the def's type, required by the def itself. `Index` is the context's place among the `Count` the
/// callee requires, and `Errors` what to report if neither can be done. A call that `ChecksOnly` is passed no
/// instance: its callee is a local function, which requires no contexts, and the context only has to exist (see
/// `EqualityOperators.checkLocalCall`).
type PendingContextInstance =
    { Def: Identifier
      Call: NodeId
      Callee: Identifier
      Index: int
      Count: int
      Context: Identifier
      ArgTys: Ty list
      Span: SourceSpan
      Errors: Diagnostic list
      ChecksOnly: bool }

/// An import of a `module name =` block written in the same file (`import money`), rather than of another file.
type BlockImport =
    { Import: ImportItem
      /// The block's full name, which its definitions are qualified with: `money`, or `app.money` in `module app`.
      Prefix: string
      /// Where the block is written.
      Block: SourceSpan }

type InferState =
        { NextId: int;
            Constraints: ConstraintSet;
            TypeVars: Map<string, TyVar>;
            TypeDefs: Map<string, TypeDefInfo>;
            /// The names in `TypeDefs` that cannot be written: the types only an aliased import brings in, which are
            /// written with the alias (`m.$Email`). They stay in `TypeDefs` for what the imported definitions' own
            /// signatures name.
            HiddenTypeNames: Set<string>;
            ContextDefs: Map<string, TypeExpr>;
            ContextRequirements: Map<string, TypeExpr list>;
            /// The type each function in `ContextRequirements` declares, without its contexts, where it writes one:
            /// what a call reads the type arguments of a parameterised context from (`a` in `[Sum(a)] list(a) -> a`).
            ContextSignatures: Map<string, TypeExpr>;
            /// The imported definitions that are an ambient context instance's members, by name: the definition's
            /// symbol and the context's name. `println` is the runtime's `Console`'s, which `@entry` imports.
            AmbientMembers: Map<string, SymbolId * Identifier>;
            /// The ambient contexts an imported function requires that this module cannot name itself, by their
            /// names in `TypeDefs` (hidden ones): `Console`, required by a function of a module that imports
            /// `nanyx/console`, in a module that does not. Like any ambient context they are the top level's, and a
            /// def has them only if it declares them or infers them.
            CarriedAmbientContexts: Set<Identifier>;
            ContextScope: ContextScope;
            /// The ambient contexts each unannotated top-level def uses itself, by the def's name.
            InferredContexts: Map<Identifier, Set<Identifier>>;
            /// The module's unannotated top-level functions, by name, with their symbols.
            UnannotatedDefs: Map<Identifier, SymbolId>;
            DeferredContextUses: DeferredContextUse list;
            /// The calls whose instances are found once the def around them is inferred (see `PendingContextInstance`).
            PendingContextInstances: PendingContextInstance list;
            /// Where each interpolation hole among `PendingContextInstances` is, by the hole's node: the environment
            /// there, whose instances it finds its `ToString` among once its type is known, and the def that writes
            /// its type it is in, if it is in one. A `==` or `!=` among them has its place here too, for its `Eq`
            /// (see `EqualityOperators`).
            HoleScopes: Map<NodeId, TypeEnv * Identifier option>;
            /// The type variables each local function compares or orders values of, among those it is generic in, by
            /// the function's symbol and then by the well-known context it needs of them (`Eq`, `Ord`): a call to it
            /// needs that context of the types they are at the call (see `WellKnownContexts.checkLocalCall`).
            LocalComparisons: Map<SymbolId, Map<Identifier, Set<int>>>;
            /// The parameterised contexts each unannotated top-level def requires, as types over its own type's
            /// variables (`Show(a)` as `TyApply("Show", [a])`), by the def's symbol.
            InferredContextTypes: Map<SymbolId, Ty list>;
            LocalNominals: Set<string>;
            LocalPrivateNominals: Set<string>;
            /// The type of every expression inferred so far, and of every `_` written in a type, by node. These are
            /// the types as inference first saw
            /// them; the substitution unification finds turns them into resolved types.
            ExprTypes: Map<NodeId, Ty>;
            /// The callee's type at each call (and pipe), by the call's node: the function's type instantiated
            /// at that call site, which a call's own type (its result) does not show.
            CalleeTypes: Map<NodeId, Ty>;
            /// Where the module's declarations are written, as the parser found them.
            Declared: DeclaredNames;
            /// The text of each `module name =` block in the module, with the block's name: what a block does not
            /// export can only be named inside it.
            ModuleBlocks: (ModuleName * SourceSpan) list;
            /// The symbol made for each declaration, by its declaring node, how that node declares it and its
            /// name, so that a declaration inferred more than once keeps one symbol.
            DeclarationSymbols: Map<NodeId * Declarer * string, SymbolId>;
            Symbols: Map<SymbolId, Symbol>;
            /// Where each name the module declares, or an import aliases, can be written: see `Scope`.
            Scopes: Map<SymbolId, SourceSpan>;
            /// The type of each declaration the module makes, by its symbol, as inference first saw it.
            SymbolTypes: Map<SymbolId, Ty>;
            /// The declarations that write their own type: an annotated def or lambda parameter.
            AnnotatedSymbols: Set<SymbolId>;
            /// The symbol each name resolved to, by the node of the name.
            References: Map<NodeId, SymbolId>;
            /// The imports that brought each imported definition into scope, in the order they are written, by the
            /// definition's symbol: a value, a type or context, or a context's member. A file and a `module name =`
            /// block in it can both import a module.
            ImportedThrough: Map<SymbolId, SymbolId list>;
            /// The import each name brought in by an import came through, by the node of the name: an expression,
            /// or a type expression that names an imported type.
            ImportReferences: Map<NodeId, SymbolId>;
            /// The names more than one import brings in unqualified: the symbol the environment holds for the name,
            /// and each candidate written as `module.name`. A reference that still resolves to that symbol is
            /// ambiguous; one to a definition of the module's own, or to a local, is not.
            AmbiguousImports: Map<string, SymbolId * string list>;
            /// The attached definition each pipe calls, by the pipe's node, where that is not the name written:
            /// `x \trim` on a string calls `String.trim`.
            PipeTargets: Map<NodeId, Identifier>;
            /// The operators that call a definition, by the operator's node, with the name they call it by: `x + y`
            /// in a function that requires `[Sum(a)]` calls the context's `` `+` ``, and on a `$Vector` it calls
            /// `` $Vector.`+` `` when that is attached.
            OperatorCalls: Map<NodeId, Identifier>;
            /// The expressions inference read as another, by the node of the one written, each with what it is written
            /// as: `0` in `x + 0`, where `x` is of a type variable `a` of a function that requires `Add(a)`, is `zero`,
            /// and `x ** n` is `__pow(x, n)` (see `ArithmeticOperators`).
            ReadAs: Map<NodeId, Expression>;
            /// The built-in property each member access reads, by its node, where it reads one rather than a
            /// record's field: `s.length` on a string.
            BuiltInProperties: Map<NodeId, BuiltInProperty>;
            /// The builder blocks that use no workflow keyword, `proc { ... }` with no `try` or `return`, by the node
            /// of the call they are written as: each runs as its block, where it is written.
            WorkflowBlocks: Set<NodeId>;
            /// Where a constraint added now is reported if it cannot be satisfied: the expression being inferred.
            Origin: SourceSpan;
            /// The imports of `module name =` blocks in this file (see `BlockImport`).
            BlockImports: BlockImport list;
            /// The names a block import brings in, each with the qualified name of the definition or type it stands
            /// for: `zero` (or `m.zero`, under `as m`) for `money.zero`.
            BlockAliases: Map<string, string>;
            /// The imports each imported definition is brought in by, as where they are written: a `module name =`
            /// block's imports are its own. An import the compiler made up (the prelude's) has no place.
            ImportSites: Map<SymbolId, SourceSpan list>;
            /// Where each call to a function that requires a parameterised context finds its instances, by the call's
            /// node (see `TypedModule.ContextInstances`).
            ContextInstances: Map<NodeId, ContextInstance option list>;
            /// The instances of parameterised contexts that `use` statements and the types of functions put in scope,
            /// as written there, by the name they are bound under in the environment (see `bindInstance`).
            InstanceContexts: Map<string, TypeExpr>;
            /// The instance of a parameterised context each `use` loads, by the node of the value it loads (see
            /// `TypedModule.UseContexts`).
            UseContexts: Map<NodeId, TypeExpr>;
            /// The expressions that failed to check, each by its node with its diagnostics, the latest first. Each is
            /// typed as anything, and checking carries on around it.
            FailedExprs: (NodeId * Diagnostic list) list;
            /// The matches that do not handle every case, each by its node with its diagnostics, the latest first.
            /// Each is typed as if it did.
            UnhandledMatches: (NodeId * Diagnostic list) list;
            /// The expressions that do something with a value of the error type that needs its type, the latest first:
            /// piping it to a name attached to no type, or reading a member of it. Each is typed as the error type
            /// without another error, since the error that left the value's type unknown has been reported; a debug
            /// build cannot run it, and traps there.
            UnknownTypeUses: NodeId list;
            /// Where each builder block falls off its end without `return`, by the node of the made-up read of the
            /// builder's `zero` desugaring put there: where to say so if the builder has no `zero`.
            WorkflowZeros: Map<NodeId, SourceSpan>;
            /// The rest variables `joinBranchTypes` made up for a branch of a type not known yet. A union with one
            /// of them, joined again, has the rest extended with the other branches' tags rather than shared.
            JoinRests: Set<int>;
            /// The definitions the compiler has written as derived instances (see `StructuralInstances`), by the
            /// context and the type each is an instance for, as an annotation writes it: the definition's name.
            DerivedInstances: Map<Identifier * TypeExpr, Identifier>;
            /// The derived instances' definitions written and not yet inferred, the earliest first.
            DerivedDefs: TopLevelItem list;
            /// The holes that call a derived instance's definition in place of the hole function, and the comparisons
            /// that call a derived `==` or the `==` attached to their type in place of the compiler's own, by the
            /// node of the hole or the comparison: the definition's name, and where the call finds each context
            /// that definition requires.
            DerivedCalls: Map<NodeId, Identifier * ContextInstance option list> }

let emptyState =
        { NextId = 0;
            Constraints = [];
            TypeVars = Map.empty;
            TypeDefs = Map.empty;
            HiddenTypeNames = Set.empty;
            ContextDefs = Map.empty;
            ContextRequirements = Map.empty;
            ContextSignatures = Map.empty;
            AmbientMembers = Map.empty;
            CarriedAmbientContexts = Set.empty;
            ContextScope = AmbientScope;
            InferredContexts = Map.empty;
            UnannotatedDefs = Map.empty;
            DeferredContextUses = [];
            PendingContextInstances = [];
            HoleScopes = Map.empty;
            LocalComparisons = Map.empty;
            InferredContextTypes = Map.empty;
            LocalNominals = Set.empty;
            LocalPrivateNominals = Set.empty;
            ExprTypes = Map.empty;
            CalleeTypes = Map.empty;
            Declared = Map.empty;
            ModuleBlocks = [];
            DeclarationSymbols = Map.empty;
            Symbols = Map.empty;
            Scopes = Map.empty;
            SymbolTypes = Map.empty;
            AnnotatedSymbols = Set.empty;
            References = Map.empty;
            ImportedThrough = Map.empty;
            ImportReferences = Map.empty;
            AmbiguousImports = Map.empty;
            PipeTargets = Map.empty;
            OperatorCalls = Map.empty;
            ReadAs = Map.empty;
            BuiltInProperties = Map.empty;
            WorkflowBlocks = Set.empty;
            Origin = SourceSpan.none;
            BlockImports = [];
            BlockAliases = Map.empty;
            ImportSites = Map.empty;
            ContextInstances = Map.empty;
            InstanceContexts = Map.empty;
            UseContexts = Map.empty;
            FailedExprs = [];
            UnhandledMatches = [];
            UnknownTypeUses = [];
            WorkflowZeros = Map.empty;
            JoinRests = Set.empty;
            DerivedInstances = Map.empty;
            DerivedDefs = [];
            DerivedCalls = Map.empty }

let internal freshVar (state: InferState) =
    let var = { Id = state.NextId; Name = None }
    TyVar var, { state with NextId = state.NextId + 1 }

/// A record with at least `fields`, its others a fresh row variable: what reading a field, or matching a pattern that
/// names some fields, tells of a value. A closed record would say it has no others.
let internal openRecordWith (fields: Map<string, Ty>) (state: InferState) =
    let row = { Id = state.NextId; Name = None }
    TyOpenRecord(fields, row), { state with NextId = state.NextId + 1 }

/// The symbol for `name` as `holder` declares it, made the first time it is asked for. `None` when the parser
/// saw no such declaration: the compiler made the name up (a destructured tuple, a `for` loop's element).
/// `scopeEnd` is where the name stops being visible (see `Scope.until`), or `None` for the whole module.
let internal declareSymbol (declarer: Declarer) (holder: NodeId) (name: string) (kind: SymbolKind) (isExported: bool) (scopeEnd: int option) (state: InferState) : SymbolId option * InferState =
    let key = holder, declarer, name
    match state.DeclarationSymbols |> Map.tryFind key with
    | Some symbol -> Some symbol, state
    | None ->
        let declared =
            state.Declared
            |> Map.tryFind (holder, declarer)
            |> Option.bind (fun names ->
                match declarer with
                // A def's value declares only that def, whose name a module block has qualified since.
                | DefValue
                | TypeDefBody -> List.tryHead names
                | LambdaParameters
                | ArmBody
                | RecordTypeFields -> names |> List.tryFind (fun (declaredName, _) -> declaredName = name))
        match declared with
        | None -> None, state
        | Some (_, span) ->
            let symbol = { Id = SymbolId.next (); Name = name; Kind = kind; Declaration = span; IsExported = isExported }
            let scope =
                match scopeEnd with
                | Some scopeEnd -> Scope.until scopeEnd span
                | None -> Scope.wholeModule span.File
            Some symbol.Id,
            { state with
                DeclarationSymbols = state.DeclarationSymbols |> Map.add key symbol.Id
                Symbols = state.Symbols |> Map.add symbol.Id symbol
                Scopes = state.Scopes |> Map.add symbol.Id scope }

/// Records that the function `name` requires `contexts`, and the type it declares without them, if it writes one.
let internal registerContextRequirement (state: InferState) (name: Identifier) (contexts: TypeExpr list) (signature: TypeExpr option) =
    { state with
        ContextRequirements = state.ContextRequirements |> Map.add name contexts
        ContextSignatures =
            match signature with
            | Some signature -> state.ContextSignatures |> Map.add name signature
            | None -> state.ContextSignatures |> Map.remove name }

/// Records `ty` as the type of the declaration `symbol`, if the parser saw one.
let internal recordSymbolType (symbol: SymbolId option) (ty: Ty) (state: InferState) =
    match symbol with
    | Some symbol -> { state with SymbolTypes = state.SymbolTypes |> Map.add symbol ty }
    | None -> state

/// Records that the declaration `symbol` writes its own type, if `isAnnotated`.
let internal recordAnnotated (symbol: SymbolId option) (isAnnotated: bool) (state: InferState) =
    match symbol with
    | Some symbol when isAnnotated -> { state with AnnotatedSymbols = state.AnnotatedSymbols |> Set.add symbol }
    | _ -> state

/// `state.ImportReferences`, with the node `node`, written at `span`, recorded as naming `symbol` through its import
/// if it was imported. Of the imports that bring `symbol` in, it is the one written in the innermost `module name =`
/// block around `span`, a block's imports being its own; among imports in the same place, the last.
let private importReferenceOf (node: NodeId) (span: SourceSpan) (symbol: SymbolId) (state: InferState) =
    let blockAround (inner: SourceSpan) =
        state.ModuleBlocks
        |> List.map snd
        |> List.filter (fun block -> SourceSpan.contains block inner)
        |> List.sortBy (fun block -> block.End - block.Start)
        |> List.tryHead
    let reaches (import: SymbolId) =
        match state.Symbols |> Map.tryFind import |> Option.map (fun symbol -> blockAround symbol.Declaration) with
        | Some (Some block) -> if SourceSpan.contains block span then Some (block.End - block.Start) else None
        | Some None
        | None -> Some System.Int32.MaxValue
    match state.ImportedThrough |> Map.tryFind symbol |> Option.defaultValue [] with
    | [] -> state.ImportReferences
    | imports ->
        let import =
            imports
            |> List.rev
            |> List.choose (fun import -> reaches import |> Option.map (fun size -> import, size))
            |> List.sortBy snd
            |> List.tryHead
            |> Option.map fst
            |> Option.defaultValue (List.last imports)
        state.ImportReferences |> Map.add node import

/// Records that the type expression `typeExpr` names the type definition `info`, if it declares a symbol, and the
/// import it came through if it was imported.
let internal recordTypeReference (typeExpr: TypeExpr) (info: TypeDefInfo) (state: InferState) =
    match info.Symbol with
    | Some symbol ->
        { state with
            References = state.References |> Map.add typeExpr.Id symbol
            ImportReferences = importReferenceOf typeExpr.Id typeExpr.Span symbol state }
    | None -> state

/// Records that `expr`, a qualified `Context.member`, names that member of the context `ctxType` names, and that
/// `ctxType` names the context.
let internal recordContextMemberReference (expr: Expression) (ctxType: TypeExpr) (memberName: Identifier) (state: InferState) =
    match ctxType.Kind with
    | TypeName name
    | TypeApply(name, _) ->
        match state.TypeDefs |> Map.tryFind name with
        | Some info ->
            let state = recordTypeReference ctxType info state
            match info.Members |> Map.tryFind memberName with
            | Some symbol -> { state with References = state.References |> Map.add expr.Id symbol }
            | None -> state
        | None -> state
    | _ -> state

/// Records that `expr` names what `scheme` is bound for, and the import it came through if it was imported.
let internal recordReference (expr: Expression) (scheme: TypeScheme) (state: InferState) =
    match scheme.Symbol with
    | None -> state
    | Some symbol ->
        { state with
            References = state.References |> Map.add expr.Id symbol
            ImportReferences = importReferenceOf expr.Id expr.Span symbol state }

let internal addConstraint left right (state: InferState) =
    { state with Constraints = (left, right, Equal, state.Origin) :: state.Constraints }

let internal addAssignableConstraint left right (state: InferState) =
    { state with Constraints = (left, right, Assignable, state.Origin) :: state.Constraints }

/// `addConstraint`, reported at `span` rather than at the expression being inferred.
let internal addConstraintAt (span: SourceSpan) left right (state: InferState) =
    let origin = if SourceSpan.isNone span then state.Origin else span
    { state with Constraints = (left, right, Equal, origin) :: state.Constraints }

/// `addAssignableConstraint`, reported at `span` rather than at the expression being inferred.
let internal addAssignableConstraintAt (span: SourceSpan) left right (state: InferState) =
    let origin = if SourceSpan.isNone span then state.Origin else span
    { state with Constraints = (left, right, Assignable, origin) :: state.Constraints }

/// Where the declaration `holder` makes as `declarer` writes its name, or `holder`'s own span if the parser saw none.
let internal declarationSpan (state: InferState) (declarer: Declarer) (holder: Expression) : SourceSpan =
    state.Declared
    |> Map.tryFind (holder.Id, declarer)
    |> Option.bind List.tryHead
    |> Option.map snd
    |> Option.defaultValue holder.Span

let internal instantiate (state: InferState) (scheme: TypeScheme) : Ty * InferState =
    if scheme.Quantified.IsEmpty then
        scheme.Type, state
    else
        let mutable current = state
        let substitutions =
            scheme.Quantified
            |> Seq.fold (fun subst id ->
                let tyVar, next = freshVar current
                current <- next
                subst |> Map.add id tyVar) Map.empty
        Unifier.apply substitutions scheme.Type, current

let internal mkTypedExpr expr ty body statements matchArms : TypedExpr =
    { Expr = expr; Type = ty; Body = body; Statements = statements; MatchArms = matchArms; Arguments = None }

/// The fields of a record type, looking through aliases and a nominal wrapper.
let internal recordFieldsOf (ty: Ty) : Map<string, Ty> option =
    match Unifier.unalias ty with
    | TyRecord fields -> Some fields
    | TyNominal(_, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord fields -> Some fields
        | _ -> None
    | _ -> None

/// The fields of a record type, looking through a nominal wrapper. Member access and `with` need a record's
/// fields while inference is still running, so a type that is still a variable here (a call's result, or a
/// local bound to one) is first resolved against the constraints gathered so far.
let internal recordFieldsSoFar (state: InferState) (ty: Ty) : Map<string, Ty> option =
    let resolved =
        match ty with
        | TyVar _ -> Unifier.apply (fst (Unifier.solve state.Constraints)) ty
        | _ -> ty
    recordFieldsOf resolved
/// `ty` as far as the constraints gathered so far determine it; a type variable stays one when nothing does.
/// Variables inside it are resolved too: a call's `#some(b) | r` may already be `#some(string) | #notFound`.
let internal resolveSoFar (state: InferState) (ty: Ty) : Ty =
    match ty with
    | TyPrimitive _ -> ty
    // A conflict elsewhere in the module does not stop the rest of the constraints saying what `ty` is.
    | _ -> Unifier.apply (fst (Unifier.solve state.Constraints)) ty
