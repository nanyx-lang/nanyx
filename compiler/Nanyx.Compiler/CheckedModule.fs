namespace NyxCompiler

open Parser.Program

type CompilationPhase =
    | Parsed
    | Desugared
    | Typed
    | Lowered
    | Emitted

type CompileResult =
    { Phase: CompilationPhase
      Diagnostics: Diagnostic list
      /// The module typed with what did check: present when it parsed and its imports checked, even if it has type
      /// or name errors of its own (see `TypeChecked`).
      Typed: TypedModule option
      /// Whether the module type-checked: it has no type or name errors. A linter rule configured as an error does
      /// not stop it type-checking.
      TypeChecked: bool
      /// Whether each type or name error in the module is compiled to a trap in `Typed` where it was found, so that a
      /// debug build can run it: the trap writes the error and stops the program if it is reached. True when there
      /// are none.
      ErrorsDeferred: bool
      /// The root module as parsed, before desugaring: the syntax the user wrote, whose nodes the typed module's
      /// `ExprTypes` and `CalleeTypes` are keyed by. Present when `Typed` is.
      Syntax: Module option
      /// The expressions desugaring and `@entry` made up for each node of `Syntax`, by its node, in the order they were
      /// made: how the type of a workflow bind is found, through `ExprTypes`. Empty when `Syntax` is absent.
      Lowered: Map<NodeId, NodeId list> }

/// A type-checkedModule module together with what an importer needs from it.
type internal CheckedModule =
    { Typed: TypedModule
      /// The errors found checking the module itself. A module with errors is still typed, with what did check,
      /// for the editor's features; it cannot be run or imported.
      Errors: Diagnostic list
      /// Those of `Errors` that are not compiled to a trap in `Own`, so a debug build cannot run the module.
      Undeferred: Diagnostic list
      /// The module's own items (imports removed), as the backend will see them.
      Own: Module
      /// Items inlined from the modules it imports, already renamed and ready for the backend.
      Inlined: TopLevelItem list
      /// The declared module name, if any.
      Name: string option
      /// The module as parsed, before desugaring.
      Syntax: Module
      /// The expressions made up for each node of `Syntax` (see `CompileResult.Lowered`).
      Lowered: Map<NodeId, NodeId list>
      /// The substitution unification found, which resolves the types inference recorded by node.
      Subst: Subst
      /// The types inference recorded by node, unresolved: only the root module's are resolved (see `compileWith`).
      RawExprTypes: Map<NodeId, Ty>
      RawCalleeTypes: Map<NodeId, Ty>
      RawSymbolTypes: Map<SymbolId, Ty>
      /// The symbols of the module's own exported definitions, by name: not those of what it imports.
      Exports: Map<string, Symbol>
      /// The contexts each of the module's own exported definitions requires, written or inferred, by name.
      ContextRequirements: Map<string, TypeExpr list>
      /// The type each of those definitions declares, without its contexts, where it writes one.
      ContextSignatures: Map<string, TypeExpr>
      /// The rests of the unions each of the module's own top-level functions compares values of, which a call to it
      /// checks a well-known context of, by its symbol (see `InferState.CheckedRests`).
      CheckedRests: Map<SymbolId, Map<Identifier, int list>>
      /// The ambient contexts (the runtime's, as `Console`) by the names the module knows them by, with their
      /// definitions: an importer that cannot name one still needs it where it calls a function that requires it.
      AmbientContexts: Map<string, TypeDefInfo>
      /// What the module re-exports (`export m (a, b)`), each with the module it re-exports from, for its importers to
      /// import too.
      ReExports: ReExported list }

/// A re-export, `export m (a, b)`: the import of `m` that selects what is exported again, and `m` as checked.
and internal ReExported =
    { Origin: ModuleOrigin
      Item: ImportItem
      Module: CheckedModule }
