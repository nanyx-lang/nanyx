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
    /// The value of the top-level def `name` of a named module, which is built where there are no contexts (#956).
    | ValueScope of name: Identifier
    /// The value of the top-level def `name` of a script, which is built in the ambient instances as the top level is.
    /// One that needs any runs in the script's entry point (see `Scripts`).
    | ScriptValueScope of name: Identifier

/// A use of the unannotated top-level def `Callee`, or of a script's value (see `InferState.ScriptValues`), whose contexts are only known once every def has been inferred:
/// `Available` are the ambient contexts something nearer than the ambient instance supplies at `Span`. A call records,
/// by its node `Call`, what provides each ambient context there (see `TypedModule.ContextProviders`).
type DeferredContextUse =
    { Caller: ContextScope
      Callee: Identifier
      Span: SourceSpan
      Available: Set<Identifier>
      Call: NodeId option
      Providers: Map<Identifier, ContextProvider> }

/// A call, in the unannotated top-level def `Def`, that requires the parameterised context `Context` at `ArgTys`,
/// types the call alone does not settle and for which no instance is in scope: `Show(t)` where `t` is a parameter's
/// type. Once the def is inferred, the context is found for the types they turned out to be, or, where they are
/// variables of the def's type, required by the def itself. `Index` is the context's place among the `Count` the
/// callee requires, and `Errors` what to report if neither can be done. A call that `ChecksOnly` is passed no
/// instance: its callee is passed nothing for the context, which only has to exist (see `CheckedRests` and
/// `WellKnownContexts.checkLocalCall`).
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

/// An arithmetic operator or `++`, in the unannotated top-level def `InDef`, on operands of a type, `OperandTy`, that
/// was still a variable where the operator is written, at `At`: `x + y` on two parameters. `MemberName` is the
/// context member the operator reads: itself, or `negate` for prefix `-`. Once the def is inferred, a type that
/// turned out to be one of the def's own variables is one the def requires the prelude's context with that member of,
/// and any other is a number, or for `++` is left as it is (see `ArithmeticInference`). `ResultTy` is the type the
/// operator gives: its operands', or for `/` their `fraction` (see `Fractions`).
type PendingArithmetic =
    { InDef: Identifier
      Operator: NodeId
      MemberName: Identifier
      OperandTy: Ty
      ResultTy: Ty
      At: SourceSpan }

/// A `/` whose operands' type, `OperandTy`, was still a variable where it is written, at `At`, and so whose result's
/// type, `ResultTy`, is a variable too: the operands' `fraction` once they are a number (see `Fractions`).
type PendingFraction =
    { OperandTy: Ty
      ResultTy: Ty
      At: SourceSpan }

/// What an arithmetic operator in a def that writes its type needs of a type variable of that def, should its operands
/// turn out to be of one: the prelude's context it reads (`Add` for `+`), and what it does, as a diagnostic says it.
type OperandRequirement =
    { Def: Identifier
      Context: Identifier
      Does: string }

/// A number literal, whose type is a variable of its own until the code around it settles it (see `NumericLiterals`).
type NumericLiteral =
    { /// The literal's type: its variable, until the constraints are solved.
      Type: Ty
      /// The literal, an `IntLit` or a `FloatLit`.
      Value: Literal
      At: SourceSpan
      /// The expression the literal is, where it is not a pattern.
      Node: NodeId option
      /// For the operands of an arithmetic operator rather than a literal: the operator, and the expression it is at
      /// `At`. Their type was still a variable when the operator was read, and they are `int`, as `Value` is, if
      /// nothing settles them.
      Operator: (string * NodeId) option
      /// For the operands of an operator in a def that writes its type: what the operator needs of them, if they turn
      /// out to be of one of the def's own variables, an associated type its signature names.
      Requires: OperandRequirement option }

/// A call that leaves out a type argument its callee takes, reported once the constraints say which type it should pass
/// (see `TypeArguments`).
type LeftOutTypeArgument =
    { Call: NodeId
      At: SourceSpan
      Callee: string
      /// The named parameter left out, or None for a positional one.
      Parameter: Identifier option
      /// The type the argument would stand for: a variable, until the constraints are solved.
      StandsFor: Ty }

/// An import of a `module name =` block written in the same file (`import money`), rather than of another file.
type BlockImport =
    { Import: ImportItem
      /// The block's full name, which its definitions are qualified with: `money`, or `app.money` in `module app`.
      Prefix: string
      /// Where the block is written.
      Block: SourceSpan
      /// Whether it is a re-export, `export money`, which imports the block too.
      IsReExport: bool
      /// The names it brings in, each with the name of the definition or type it stands for (see
      /// `Visibility.blockAliases`): none until inference finds them.
      Names: Map<string, string> }

/// An unannotated local def whose value is being inferred, which may call itself (see `LambdaContexts.enterSelf`).
type SelfDef =
    { /// How many lambdas are being inferred once the def's own is.
      Depth: int
      /// The contexts of the type of each of its calls to itself so far: a variable that stands for the def's own.
      Calls: Ty list }

/// A match whose scrutinee's type was not known when its arms were checked, so that nothing could be said yet about
/// what they leave out (see `WorkflowRests.noteOpenMatch`).
type OpenMatch =
    { Match: NodeId
      Arms: MatchArm list
      /// The scrutinees' types as they were then.
      Scrutinees: Ty list
      /// Where the match's head is written, which an error about it is reported at.
      Head: SourceSpan }

/// The instance a lambda names with `context l`: the context, each of its members as the lambda's body has it where
/// the body starts, the node of the body (the `ContextNames` the parser makes), and for `Memory`, the scope of the
/// `memory` block it is. `use l` loads those members again, and makes the block's scope the one a `mut` takes.
type ContextName = { Context: Identifier; Members: (Identifier * TypeScheme) list; Body: NodeId; Scope: Ty option }

/// What the variable an annotation stands for is kept under while the annotation is read (see `InferState.TypeVars`).
type TypeVarKey =
    /// A type variable the annotation writes, by its name: `a`.
    | WrittenVar of string
    /// The scope of the scoped context of this name, shared by every one of that name the signature writes (see
    /// `TyVarKind.SignatureScope`).
    | ScopeOf of context: string

/// A `#delay` keyword's call, `k! e`, whose keyword names a local def, a parameter or a pattern's variable rather than
/// the keyword's def (see `HiddenKeywords`): the keyword, where it is written, the local, its type, and the def the
/// keyword would call, if one is in scope.
type HiddenKeyword =
    { Keyword: Identifier
      At: SourceSpan
      Local: Symbol
      LocalType: Ty
      KeywordDef: Identifier option }

type InferState =
        { NextId: int;
            Constraints: ConstraintSet;
            TypeVars: Map<TypeVarKey, TyVar>;
            TypeDefs: Map<string, TypeDefInfo>;
            /// The names in `TypeDefs` that cannot be written: the types only an aliased import brings in, which are
            /// written with the alias (`m.Email`). They stay in `TypeDefs` for what the imported definitions' own
            /// signatures name.
            HiddenTypeNames: Set<string>;
            ContextDefs: Map<string, TypeExpr>;
            ContextRequirements: Map<string, TypeExpr list>;
            /// The type each function in `ContextRequirements` declares, without its contexts, where it writes one:
            /// what a call reads the type arguments of a parameterised context from (`a` in `[Sum(a)] list(a) -> a`).
            ContextSignatures: Map<string, TypeExpr>;
            /// The type each def that writes its type writes, contexts and all, by the def's name: where a fix adds a
            /// context the def needs and does not declare (see `ContextFixes`).
            WrittenTypes: Map<Identifier, TypeExpr>;
            /// The imported definitions that are an ambient context instance's members, by name: the definition's
            /// symbol and the context's name. `println` is the runtime's `Console`'s, which `@entry` imports.
            AmbientMembers: Map<string, SymbolId * Identifier>;
            /// The ambient contexts an imported function requires that this module cannot name itself, by their
            /// names in `TypeDefs` (hidden ones): `Console`, required by a function of a module that imports
            /// `nanyx.cli/console`, in a module that neither imports it nor sees a host's prelude. Like any ambient context they are the top level's, and a
            /// def has them only if it declares them or infers them.
            CarriedAmbientContexts: Set<Identifier>;
            /// The contexts an imported function requires that the module's host does not provide, by name, each with
            /// the host's root module as an import names it: `Console`, required by a library's function, under a host
            /// whose prelude does not bring it in. A call to such a function is an error (see `Contexts.ensureContextAvailable`).
            UnprovidedContexts: Map<Identifier, string>;
            /// The contexts a host provides, by the names the module knows them by (`Console`, `c.Console`): a function
            /// named as a value carries those of them it was inferred to require in its type (see `Contexts.inferredAmbientContexts`).
            HostContexts: Set<Identifier>;
            ContextScope: ContextScope;
            /// The ambient contexts each unannotated top-level def uses itself, by the def's name.
            InferredContexts: Map<Identifier, Set<Identifier>>;
            /// The module's unannotated top-level functions, by name, with their symbols.
            UnannotatedDefs: Map<Identifier, SymbolId>;
            /// The lambda a script's statements are, its entry point, if the module is a script (see `Scripts`).
            ScriptEntry: NodeId option;
            /// The top-level defs of a script whose values are not functions, by name, with their symbols: those that
            /// turn out to need an ambient context run in the entry point (`ScriptEntryValues`).
            ScriptValues: Map<Identifier, SymbolId>;
            /// The script's values that need an ambient context, worked out once the module is inferred: they run in the
            /// entry point, in the order written, rather than at the top level.
            ScriptEntryValues: Set<Identifier>;
            /// The units of measure the module can write, by name: those its imports bring in, then its own (see
            /// `Units`).
            Units: Map<string, UnitInfo>;
            /// The units two of the module's imports bring in under one name, which a literal or a unit's definition
            /// can't write, with each unit of the name: an error only where the name is written (see `Units`).
            AmbiguousUnits: Map<string, UnitInfo list>;
            DeferredContextUses: DeferredContextUse list;
            /// For each call from a lambda of an unannotated top-level def not inferred yet there, which is in a cycle
            /// with the def the lambda is in, the def's name, the context variable the lambda uses for it and the call's
            /// span. Bound to the ambient contexts the def requires once the module is inferred (see
            /// `Contexts.pendingCallContexts`).
            PendingCallContexts: (Identifier * Ty * SourceSpan) list;
            /// The calls whose instances are found once the def around them is inferred (see `PendingContextInstance`).
            PendingContextInstances: PendingContextInstance list;
            /// The nodes of the functions named as values among `PendingContextInstances`: one whose type is still open
            /// once it is resolved is never called at that type, and needs no instance (see
            /// `ContextInstances.resolvePendingInstances`).
            PendingReferences: Set<NodeId>;
            /// The arithmetic operators left to be read once the def around them is inferred (see `PendingArithmetic`).
            PendingArithmetic: PendingArithmetic list;
            /// The `/`s whose result's type waits on their operands' (see `PendingFraction`).
            PendingFractions: PendingFraction list;
            /// The number literals of the module so far, the latest first, to be checked against their types once those
            /// are known (see `NumericLiterals`).
            NumericLiterals: NumericLiteral list;
            /// The number literals no def's generalising has settled yet, the latest first.
            UnsettledLiterals: NumericLiteral list;
            /// The calls of the module that leave out a type argument, the latest first (see `TypeArguments`).
            LeftOutTypeArguments: LeftOutTypeArgument list;
            /// The names of the prelude's contexts, as they are in `TypeDefs`: the only ones an operator on a value of
            /// a type variable is read through (see `OperatorContexts`). A module checked without the prelude has none.
            PreludeContexts: Set<Identifier>;
            /// Where each interpolation hole among `PendingContextInstances` is, by the hole's node: the environment
            /// there, whose instances it finds its `ToString` among once its type is known, and the def that writes
            /// its type it is in, if it is in one. A `==` or `!=` among them has its place here too, for its `Eq`
            /// (see `EqualityOperators`).
            HoleScopes: Map<NodeId, TypeEnv * Identifier option>;
            /// The type variables each local function compares or orders structures of, as `[a] == [b]`, among those it
            /// is generic in, by the function's symbol and then by the well-known context it needs of them (`Eq`, `Ord`):
            /// a call to it needs that context of the types they are at the call (see `WellKnownContexts.checkLocalCall`).
            /// One that compares values of the variable itself requires the context instead (see
            /// `ContextInference.resolveLocal`).
            LocalComparisons: Map<SymbolId, Map<Identifier, Set<int>>>;
            /// The rests of the unions in its type each top-level function compares values of (`r` in `#none | r`),
            /// by the function's symbol and then by the well-known context it needs of them, each by its place among
            /// `ContextInference.unionRests` of the function's type, which an import renumbers. The function is passed
            /// nothing for them: a call to it needs that context of the types they are at the call, as a call to a
            /// local function does (see `ContextInference.checkRestsAtCalls`).
            CheckedRests: Map<SymbolId, Map<Identifier, int list>>;
            /// The parameterised contexts each unannotated top-level def requires, as types over its own type's
            /// variables (`Show(a)` as `TyApply("Show", [a])`), by the def's symbol.
            InferredContextTypes: Map<SymbolId, Ty list>;
            /// The contexts each local def that writes its type requires, as written, by the def's symbol: what
            /// `ContextRequirements` holds for it by name only until its block ends, and a def of the same name
            /// elsewhere can stand for.
            LocalContextRequirements: Map<SymbolId, TypeExpr list>;
            /// The type each local def in `LocalContextRequirements` declares without its contexts, by its symbol, as
            /// `ContextSignatures` holds it by name until its block ends.
            LocalContextSignatures: Map<SymbolId, TypeExpr>;
            /// The variable of the def around it that each context variable a local def in `LocalContextRequirements`
            /// writes stands for, by the local def's symbol and the variable's name: `c` in `def go: [c] int -> int`
            /// inside a `[c]` def is that def's `c`, which its own type does not name.
            LocalContextVariables: Map<SymbolId, Map<Identifier, TyVar>>;
            /// The module being checked: the home of each nominal type it defines (see `NominalId`).
            Home: ModuleOrigin;
            LocalNominals: Set<string>;
            LocalPrivateNominals: Set<string>;
            /// The type of every expression inferred so far, and of every `_` written in a type, by node. These are
            /// the types as inference first saw
            /// them; the substitution unification finds turns them into resolved types.
            ExprTypes: Map<NodeId, Ty>;
            /// The function type each lambda a lambda gives back is checked against, by the inner lambda's node: the
            /// outer one's expected result, `b -> c` for `{ a -> { b -> ... } }` checked against `a -> b -> c` (#1749).
            /// Also that of each lambda in a list literal checked against a list of functions (see
            /// `ExpectedLiterals.intoLambdas`).
            ExpectedLambdaTypes: Map<NodeId, Ty>;
            /// The type a typed def is declared at, by the node of the last statement of the block that is its value:
            /// a square-bracket literal there builds that type, as one written on the def's line does (#1776).
            ExpectedBlockResults: Map<NodeId, Ty>;
            /// The type a literal is checked against, by its node, where what it is written in expects one: a typed def's
            /// value, an argument, and inside them an element, a tag's payload or a map's value (see `ExpectedLiterals`).
            ExpectedTypes: Map<NodeId, Ty>;
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
            /// `x \trim` on a string calls `string.trim`.
            PipeTargets: Map<NodeId, Identifier>;
            /// The standard-library modules that export each value, type and context, as their import paths
            /// (`nanyx/text`), by the name they export it under: where the error for a name that is not in scope, or
            /// for a pipe that could not reach a definition, says to find it.
            StandardLibraryExports: Lazy<Map<string, string list>>;
            /// The operators that call a definition, by the operator's node, with the name they call it by: `x + y`
            /// in a function that requires `[Sum(a)]` calls the context's `` `+` ``, and on a `Vector` it calls
            /// `` Vector.`+` `` when that is attached.
            OperatorCalls: Map<NodeId, Identifier>;
            /// The instance in scope whose member each of those operators calls, by the operator's node, as written
            /// where it is put in scope, where the operator calls one: in a function that requires
            /// `[Add(a), Add(b)]`, `x + y` on two `a`s calls the `` `+` `` of `Add(a)`, not of `Add(b)`.
            OperatorInstances: Map<NodeId, TypeExpr>;
            /// The expressions inference read as another, by the node of the one written, each with what it is written
            /// as: `0` in `x + 0`, where `x` is of a type variable `a` of a function that requires `Add(a)`, is `zero`,
            /// and `x ** n` is `__pow(x, n)` (see `ArithmeticOperators`).
            ReadAs: Map<NodeId, Expression>;
            /// The calls of the built-in `assert`, by node, which the compiler writes out with what their message needs
            /// once the module is typed (see `Asserts`).
            Asserts: Set<NodeId>;
            /// The expressions inferred already, by node, that a call infers again as its argument: a pipe's value when it
            /// is itself a pipe, which its call is given typed rather than inferring the chain to its left once more for
            /// each stage (see `Infer.inferPipe`). `inferExpr` gives the typed expression and drops it.
            Inferred: Map<NodeId, TypedExpr>;
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
            /// The names imports bring in that a module gives from its own blocks (see `CheckedModule.BlockReExports`),
            /// as written (`area`, or `g.area` under `as g`), each with the symbol of the definition it stands for and
            /// the name that definition is inlined under.
            ImportedBlockNames: Map<string, SymbolId * string>;
            /// The imports each imported definition is brought in by, as where they are written: a `module name =`
            /// block's imports are its own. An import the compiler made up (the prelude's) has no place.
            ImportSites: Map<SymbolId, SourceSpan list>;
            /// Where each call to a function that requires a parameterised context finds its instances, by the call's
            /// node (see `TypedModule.ContextInstances`).
            ContextInstances: Map<NodeId, ContextInstance option list>;
            /// What the associated types of the contexts each call's callee requires are at the call, by the call's node and
            /// by the names the callee's signature gives them (see `TypedModule.AssociatedArguments`), as read.
            AssociatedArguments: Map<NodeId, Map<string, Ty>>;
            /// The type variables the module's defs' signatures name their contexts' associated types by, which keep their
            /// names when the constraints are solved, as a type a def declares does: the backend binds them by name (see
            /// `TypedModule.AssociatedArguments`).
            AssociatedVariables: TyVar list;
            /// What provides each context found by its members' names that a call requires, by the call's node (see
            /// `TypedModule.ContextProviders`). Those of parameterised contexts are added from `ContextInstances` once
            /// the module is inferred.
            ContextProviders: Map<NodeId, ProvidedContext list>;
            /// The instance of a parameterised context each `use` loads, by the node of the value it loads (see
            /// `TypedModule.UseContexts`).
            UseContexts: Map<NodeId, TypeExpr>;
            /// The type of the instance each `use` in `UseContexts` loads, as it was when the `use` was checked. What
            /// the rest of the module settles in it is written into `UseContexts` once the module is inferred (#2550).
            UseContextTypes: Map<NodeId, Ty>;
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
            /// Each read of a member of a builder's context that desugaring made up, by its node: where to say so if no
            /// context in scope there has the member, and the builder's name. A keyword's call is said to be at the
            /// keyword, `zero` at the end of a block that falls off without `return`, and `for` at a loop whose body
            /// uses a keyword (see `WorkflowMembers`).
            WorkflowMemberPlaces: Map<NodeId, SourceSpan * Identifier>;
            /// Each `#delay` keyword's call, `k! e` made `k({ e })`, by its span, with where the keyword is written and
            /// the keyword (see `WorkflowDesugaring`). Passes after desugaring make the call's node again, in its place.
            DelayCalls: Map<SourceSpan, SourceSpan * Identifier>;
            /// The `#delay` keyword calls whose keyword names a local rather than the keyword's def, by the call's span
            /// (see `HiddenKeywords`).
            HiddenKeywords: Map<SourceSpan, HiddenKeyword>;
            /// The rest variables `joinBranchTypes` made up for a branch of a type not known yet. A union with one
            /// of them, joined again, has the rest extended with the other branches' tags rather than shared.
            JoinRests: Set<int>;
            /// The types of the arguments each call to a workflow block's rest has passed it so far, by the rest's name (see
            /// `WorkflowRests`).
            RestArguments: Map<string, Ty list>;
            /// The matches whose scrutinees' types were not known yet when they were checked, the latest first.
            OpenMatches: OpenMatch list;
            /// The definitions the compiler has written as derived instances (see `StructuralInstances`), by the
            /// context and the type each is an instance for, as an annotation writes it: the definition's name.
            DerivedInstances: Map<Identifier * TypeExpr, Identifier>;
            /// The derived instances' definitions written and not yet inferred, the earliest first.
            DerivedDefs: TopLevelItem list;
            /// The holes that call a derived instance's definition in place of the hole function, and the comparisons
            /// that call a derived `==` or the `==` attached to their type in place of the compiler's own, by the
            /// node of the hole or the comparison: the definition's name, and where the call finds each context
            /// that definition requires.
            DerivedCalls: Map<NodeId, Identifier * ContextInstance option list>;
            /// The local defs that are cells, made with `mut`, each with the scope of the `memory` block it belongs to (see
            /// `MemoryCells`).
            Cells: Map<SymbolId, Ty>;
            /// Where a mismatch of a call's arguments, written as one tuple, is reported instead of at the tuple, by the
            /// tuple's span: worked out only if one is (see `LambdaArguments.reportedSpan`).
            ArgumentSpans: Map<SourceSpan, Lazy<SourceSpan>>;
            /// The work variables (see `TaskWork`), each with where the def that starts tasks it is of was named.
            TaskWork: Map<int, SourceSpan>;
            /// The scoped contexts each call supplies to its arguments, by the call's node, each with the scope the call
            /// starts, the scopes the signatures around the call write, and the names bound around the call whose types
            /// are not yet known in full (see `ScopedCalls`).
            SupplyingCalls: Map<NodeId, (string * Ty) list * Ty list * (Identifier * TypeScheme) list>;
            /// The scoped contexts a call has been reported to let something that requires them escape (see
            /// `ScopedCalls.leave`): what is then found to require them where nothing supplies them is that escape.
            EscapedContexts: Set<Identifier>;
            /// The scopes of the calls whose arguments are being checked, innermost first (see `ScopedCalls`).
            OpenScopes: Ty list;
            /// Where each `:=` is written: a type mismatch there is the assignment's (see `MemoryCells.mismatchCode`).
            CellAssignments: Set<SourceSpan>;
            /// The contexts each lambda being inferred uses so far, the innermost first (see `LambdaContexts`).
            LambdaContexts: Ty list list;
            /// The first type variable made inside each lambda being inferred, the innermost first, and where the lambda
            /// is written: a variable made earlier, or a name declared outside it, belongs to something the lambda
            /// captures, or to the code around it (see `LambdaContexts.leave`).
            LambdaStarts: (int * SourceSpan) list;
            /// How many lambdas deep each call that supplies a scoped context is, by the variable of the scope it
            /// starts (see `ScopedCalls.enter`).
            CallDepths: Map<int, int>;
            /// The lambdas that are a def's own body: the def's contexts are its, not its type's (see `LambdaContexts`).
            BodyLambdas: Set<NodeId>;
            /// How many lambdas were being inferred around the body of each def, by the def's name: a lambda inferred
            /// within a def's body is one of more (see `LambdaContexts.withinLambda`).
            BodyDepths: Map<Identifier, int>;
            /// The scoped contexts each unannotated top-level def's body requires outside any lambda, by the def's
            /// name, each once, which its type requires in turn (see `LambdaContexts.record`).
            InferredScopedContexts: Map<Identifier, Ty list>;
            /// The instances a `use` loads that are written at it, by the node of each: their members' lambdas may
            /// need more contexts than the members' types declare (see `UseDischarge`).
            UseInstances: Set<NodeId>;
            /// The name each bare name that reaches a def attached to a context stands for, by its node: `start` for
            /// `Async.start` where `Async` is available (see `AttachedContextDefs`).
            AttachedByBareName: Map<NodeId, Identifier>;
            /// How many lambdas were being inferred around each `use`, by the node of the value it loads (see
            /// `LambdaContexts.enterUse`).
            UseDepths: Map<NodeId, int>;
            /// The unannotated local defs whose values are being inferred, by the id of the variable each is bound at
            /// meanwhile (see `LambdaContexts.enterSelf`).
            SelfTypes: Map<int, SelfDef>;
            /// The context variables each unannotated top-level def uses so far, by the def's name: those of the
            /// functions it is given that it calls or passes on (see `ContextVariables`).
            InferredContextVars: Map<Identifier, Ty list>;
            /// The names lambdas give the instances of contexts they are given, `context l`, by each name's symbol (see
            /// `ContextNames`).
            ContextNames: Map<SymbolId, ContextName>;
            /// The type each name that stands for a field, a case or a type argument of a description opened its
            /// packaged type at, by the name's symbol (see `TypeDescriptions.memberType`).
            OpenedPackages: Map<SymbolId, Ty> }

let emptyState =
        { NextId = 0;
            Constraints = [];
            TypeVars = Map.empty;
            TypeDefs = Map.empty;
            HiddenTypeNames = Set.empty;
            ContextDefs = Map.empty;
            ContextRequirements = Map.empty;
            ContextSignatures = Map.empty;
            WrittenTypes = Map.empty;
            AmbientMembers = Map.empty;
            CarriedAmbientContexts = Set.empty;
            UnprovidedContexts = Map.empty;
            HostContexts = Set.empty;
            ContextScope = AmbientScope;
            InferredContexts = Map.empty;
            UnannotatedDefs = Map.empty;
            ScriptEntry = None;
            ScriptValues = Map.empty;
            ScriptEntryValues = Set.empty;
            Units = Map.empty;
            AmbiguousUnits = Map.empty;
            DeferredContextUses = [];
            PendingCallContexts = [];
            PendingContextInstances = [];
            PendingReferences = Set.empty;
            PendingArithmetic = [];
            PendingFractions = [];
            NumericLiterals = [];
            UnsettledLiterals = [];
            LeftOutTypeArguments = [];
            PreludeContexts = Set.empty;
            HoleScopes = Map.empty;
            LocalComparisons = Map.empty;
            CheckedRests = Map.empty;
            InferredContextTypes = Map.empty;
            LocalContextRequirements = Map.empty;
            LocalContextSignatures = Map.empty;
            LocalContextVariables = Map.empty;
            Home = InMemory;
            LocalNominals = Set.empty;
            LocalPrivateNominals = Set.empty;
            ExprTypes = Map.empty;
            ExpectedLambdaTypes = Map.empty;
            ExpectedBlockResults = Map.empty;
            ExpectedTypes = Map.empty;
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
            StandardLibraryExports = lazy Map.empty;
            OperatorCalls = Map.empty;
            OperatorInstances = Map.empty;
            ReadAs = Map.empty;
            Asserts = Set.empty;
            Inferred = Map.empty;
            BuiltInProperties = Map.empty;
            WorkflowBlocks = Set.empty;
            Origin = SourceSpan.none;
            BlockImports = [];
            BlockAliases = Map.empty;
            ImportedBlockNames = Map.empty;
            ImportSites = Map.empty;
            ContextInstances = Map.empty;
            AssociatedArguments = Map.empty;
            AssociatedVariables = [];
            ContextProviders = Map.empty;
            UseContexts = Map.empty;
            UseContextTypes = Map.empty;
            FailedExprs = [];
            UnhandledMatches = [];
            UnknownTypeUses = [];
            WorkflowMemberPlaces = Map.empty;
            DelayCalls = Map.empty;
            HiddenKeywords = Map.empty;
            JoinRests = Set.empty;
            RestArguments = Map.empty;
            OpenMatches = [];
            DerivedInstances = Map.empty;
            DerivedDefs = [];
            DerivedCalls = Map.empty;
            Cells = Map.empty;
            ArgumentSpans = Map.empty;
            TaskWork = Map.empty;
            SupplyingCalls = Map.empty;
            EscapedContexts = Set.empty;
            OpenScopes = [];
            CellAssignments = Set.empty;
            LambdaContexts = [];
            LambdaStarts = [];
            CallDepths = Map.empty;
            BodyLambdas = Set.empty;
            BodyDepths = Map.empty;
            InferredScopedContexts = Map.empty;
            UseInstances = Set.empty;
            AttachedByBareName = Map.empty;
            UseDepths = Map.empty;
            SelfTypes = Map.empty;
            InferredContextVars = Map.empty;
            ContextNames = Map.empty;
            OpenedPackages = Map.empty }

/// A new type variable, and `state` with it taken.
let internal freshTyVar (state: InferState) : TyVar * InferState =
    TyVar.ordinary state.NextId None, { state with NextId = state.NextId + 1 }

/// A new type variable as a type (see `freshTyVar`), and `state` with it taken.
let internal freshVar (state: InferState) =
    let var, next = freshTyVar state
    TyVar var, next

/// The scope of the scoped context `context` the signature being read writes, shared by every one of that name in it:
/// the one it already has, or a new one (see `TyVarKind.SignatureScope`).
let internal signatureScope (state: InferState) (context: string) : Ty * InferState =
    match state.TypeVars |> Map.tryFind (ScopeOf context) with
    | Some existing -> TyVar existing, state
    | None ->
        let var: TyVar = { Id = state.NextId; Name = Some context; Kind = SignatureScope context }
        TyVar var, { state with NextId = state.NextId + 1; TypeVars = state.TypeVars |> Map.add (ScopeOf context) var }

/// A fresh variable for each of `items`, in order.
let internal freshVars (items: 'a list) (state: InferState) =
    items |> List.mapFold (fun current _ -> freshVar current) state

/// A record with at least `fields`, its others a fresh row variable: what reading a field, or matching a pattern that
/// names some fields, tells of a value. A closed record would say it has no others.
let internal openRecordWith (fields: Map<string, Ty>) (state: InferState) =
    let row = TyVar.ordinary state.NextId None
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
                | TypeDefBody
                | MeasureDeclaration -> List.tryHead names
                | LambdaParameters
                | ArmBody
                | RecordTypeFields
                | NamedFieldValue -> names |> List.tryFind (fun (declaredName, _) -> declaredName = name))
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

/// `state` without the contexts the top-level def `name` was recorded to require (see `registerContextRequirement`): a
/// def that failed to check is of the error type, so a call to it requires nothing, and reports no missing context.
let internal forgetContextRequirement (state: InferState) (name: Identifier) =
    { state with
        ContextRequirements = state.ContextRequirements |> Map.remove name
        ContextSignatures = state.ContextSignatures |> Map.remove name }

/// Records that the local function `name`, declared as `symbol`, requires `contexts`, as `registerContextRequirement`
/// does, and by its symbol too (see `LocalContextRequirements`).
let internal registerLocalContextRequirement state (symbol: SymbolId option) name contexts signature =
    let registered = registerContextRequirement state name contexts signature
    match symbol with
    | Some symbol ->
        { registered with
            LocalContextRequirements = registered.LocalContextRequirements |> Map.add symbol contexts
            LocalContextSignatures =
                match signature with
                | Some signature -> registered.LocalContextSignatures |> Map.add symbol signature
                | None -> registered.LocalContextSignatures
            LocalContextVariables =
                let outer =
                    contexts
                    |> List.choose (fun (context: TypeExpr) ->
                        match context.Kind with
                        | TypeVar name -> state.TypeVars |> Map.tryFind (WrittenVar name) |> Option.map (fun var -> name, var)
                        | _ -> None)
                    |> Map.ofList
                registered.LocalContextVariables |> Map.add symbol outer }
    | None -> registered

/// `state` with what `ContextRequirements` and `ContextSignatures` hold for each of `names`, the local defs of a block
/// that has ended, back to what they held in `outer`, before the block: a top-level def of the same name requires its
/// own contexts again.
let internal leaveLocalContextRequirements (outer: InferState) (names: Identifier list) (state: InferState) =
    let restore name (inner: Map<Identifier, 'T>) (before: Map<Identifier, 'T>) =
        match before |> Map.tryFind name with
        | Some value -> inner |> Map.add name value
        | None -> inner |> Map.remove name
    names
    |> List.fold (fun (current: InferState) name ->
        { current with
            ContextRequirements = restore name current.ContextRequirements outer.ContextRequirements
            ContextSignatures = restore name current.ContextSignatures outer.ContextSignatures }) state

/// Records `written` as the type the def `name` writes, if it writes one (see `WrittenTypes`).
let internal recordWrittenType (name: Identifier) (written: TypeExpr option) (state: InferState) =
    match written with
    | Some written -> { state with WrittenTypes = state.WrittenTypes |> Map.add name written }
    | None -> state

/// Records `ty` as the type of the declaration `symbol`, if the parser saw one.
let internal recordSymbolType (symbol: SymbolId option) (ty: Ty) (state: InferState) =
    match symbol with
    | Some symbol -> { state with SymbolTypes = state.SymbolTypes |> Map.add symbol ty }
    | None -> state

/// The definition of the nominal type `nominal` among those `state` knows, by whichever name it is known.
let internal tryFindNominalDef (nominal: NominalId) (state: InferState) : TypeDefInfo option =
    state.TypeDefs |> Map.tryPick (fun _ info -> if TypeDefInfo.nominalId info = nominal then Some info else None)

/// Records the type inference gave an expression, so it can be looked up by node once unification has resolved it.
let internal recordExprType (result: Result<TypedExpr * InferState, Diagnostic list>) =
    result
    |> Result.map (fun (typedExpr, state) ->
        typedExpr, { state with ExprTypes = state.ExprTypes |> Map.add typedExpr.Expr.Id typedExpr.Type })

/// Records that the declaration `symbol` writes its own type, if `isAnnotated`.
let internal recordAnnotated (symbol: SymbolId option) (isAnnotated: bool) (state: InferState) =
    match symbol with
    | Some symbol when isAnnotated -> { state with AnnotatedSymbols = state.AnnotatedSymbols |> Set.add symbol }
    | _ -> state

/// The `module name =` block innermost around `span`, with its name, if `span` is in one.
let blockAround (moduleBlocks: (ModuleName * SourceSpan) list) (span: SourceSpan) : (ModuleName * SourceSpan) option =
    moduleBlocks
    |> List.filter (fun (_, block) -> SourceSpan.contains block span)
    |> List.sortBy (fun (_, block) -> block.End - block.Start)
    |> List.tryHead

/// `state.ImportReferences`, with the node `node`, written at `span`, recorded as naming `symbol` through its import
/// if it was imported. Of the imports that bring `symbol` in, it is the one written in the innermost `module name =`
/// block around `span`, a block's imports being its own; among imports in the same place, the last.
let private importReferenceOf (node: NodeId) (span: SourceSpan) (symbol: SymbolId) (state: InferState) =
    let reaches (import: SymbolId) =
        match state.Symbols |> Map.tryFind import |> Option.map (fun symbol -> blockAround state.ModuleBlocks symbol.Declaration |> Option.map snd) with
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

/// Records that `expr`, a number with a unit such as `2km`, is written with the unit `info`, if it declares a symbol, and
/// the import it came through if it was imported.
let internal recordUnitReference (expr: Expression) (info: UnitInfo) (state: InferState) =
    match info.Symbol with
    | Some symbol ->
        { state with
            References = state.References |> Map.add expr.Id symbol
            ImportReferences = importReferenceOf expr.Id expr.Span symbol state }
    | None -> state

/// Records that `expr`, a call such as `Point(x = 1)`, makes a value of the type definition `info`, if it declares a
/// symbol, and the import it came through if it was imported.
let internal recordConstructorReference (expr: Expression) (info: TypeDefInfo) (state: InferState) =
    match info.Symbol with
    | Some symbol ->
        { state with
            References = state.References |> Map.add expr.Id symbol
            ImportReferences = importReferenceOf expr.Id expr.Span symbol state }
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
        let ty = Unifier.apply substitutions scheme.Type
        // Where a def that starts tasks is named, what its type is instantiated with is a task's work (see `TaskWork`).
        let seeded =
            match scheme.Symbol |> Option.bind current.Symbols.TryFind with
            | Some symbol when TaskWork.startsTasks symbol -> TaskWork.variables ty
            | _ -> []
        let carried =
            scheme.Quantified
            |> Seq.filter current.TaskWork.ContainsKey
            |> Seq.collect (fun id -> TypeEnv.freeTyVars substitutions[id])
            |> List.ofSeq
        let current =
            match seeded @ carried with
            | [] -> current
            | work -> { current with TaskWork = work |> List.fold (fun marked id -> marked |> Map.add id current.Origin) current.TaskWork }
        ty, current

let internal mkTypedExpr expr ty body statements matchArms : TypedExpr =
    { Expr = expr; Type = ty; Body = body; Statements = statements; MatchArms = matchArms; Arguments = None }

/// `expr` inferred by `infer` as the call of `callee` with `args` that it is read as, and recorded in `ReadAs` to be
/// written as it: `x ** n` is `__pow(x, n)`. The call keeps the node of `expr`, which its types are recorded by.
let internal inferAsCall (infer: InferState -> Expression -> Result<'Typed * InferState, 'Error>) (state: InferState) (expr: Expression) (callee: Identifier) (args: Expression list) =
    let call = { expr with Kind = FunctionCall(callee, None, args) }
    infer state call |> Result.map (fun (typed, next) -> typed, { next with ReadAs = next.ReadAs |> Map.add call.Id call })

/// The fields of a record type, looking through aliases and a nominal wrapper.
let internal recordFieldsOf (ty: Ty) : Map<string, Ty> option =
    match Unifier.unalias ty with
    | TyRecord(fields, _) -> Some fields
    | RecursiveNominals.Nominal(_, _, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord(fields, _) -> Some fields
        | _ -> None
    | _ -> None

/// The fields of a record type, looking through a nominal wrapper. Member access and `with` need a record's
/// fields while inference is still running, so a type that is still a variable here (a call's result, or a
/// local bound to one) is first resolved against the constraints gathered so far.
let internal recordFieldsSoFar (state: InferState) (ty: Ty) : Map<string, Ty> option =
    let resolved =
        match ty with
        | TyVar _ -> Unifier.applySolved (fst (Solving.solve state.Constraints)) ty
        | _ -> ty
    recordFieldsOf resolved
/// `ty` as far as the constraints gathered so far determine it; a type variable stays one when nothing does.
/// Variables inside it are resolved too: a call's `#some(b) | r` may already be `#some(string) | #notFound`.
let internal resolveSoFar (state: InferState) (ty: Ty) : Ty =
    match ty with
    | TyPrimitive _ -> ty
    // A conflict elsewhere in the module does not stop the rest of the constraints saying what `ty` is.
    | _ -> Unifier.applySolved (fst (Solving.solve state.Constraints)) ty

/// `ty`, resolved, with each variable that an associated type a signature names stands for (see `AssociatedVariables`)
/// given that name, as a diagnostic writes it: the element of a `for` over an `[Iterable(i)] i` is a variable inference
/// made up, which the constraints solve the signature's `elem` to.
let internal withAssociatedNames (state: InferState) (ty: Ty) : Ty =
    let names =
        state.AssociatedVariables
        |> List.choose (fun written ->
            match resolveSoFar state (TyVar written) with
            | TyVar({ Name = None } as solved) -> Some (solved.Id, written.Name)
            | _ -> None)
        |> Map.ofList
    let rec rename (ty: Ty) =
        match ty with
        | TyVar v when names.ContainsKey v.Id -> TyVar { v with Name = names.[v.Id] }
        | _ -> Ty.map rename ty
    rename (resolveSoFar state ty)

/// The type variables of the types of what is in scope in `env`, each resolved by `subst`: those a def's type shares
/// with its surroundings, which generalising it must leave alone. Only the variables of names whose schemes have free
/// variables are looked at, each once (see `TypeEnv.UnquantifiedVars`), and a variable `subst` does not bind stands for
/// itself, so the many names in scope that are generalised, and the defs of the module not yet checked, cost little
/// (#1266).
let internal envVarsUnder (subst: Subst) (env: TypeEnv) : System.Collections.Generic.HashSet<int> =
    let found = System.Collections.Generic.HashSet<int>()
    let add (id: int) =
        if subst.ContainsKey id then found.UnionWith(TypeEnv.freeTyVars (Unifier.apply subst (TyVar (TyVar.ordinary id None))))
        else found.Add id |> ignore
    for id in env.UnquantifiedVars.Keys do
        add id
    for instance in env.Instances do
        Set.iter add (TypeEnv.freeTyVars instance.Context)
    env.BlockScope |> Option.iter (TypeEnv.freeTyVars >> Set.iter add)
    found
