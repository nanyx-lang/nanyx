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
      /// The module typed with what did check: present when its imports checked, even if it has type or name errors
      /// of its own (see `TypeChecked`), or syntax errors, when it has the items that parsed.
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
      /// The name the root module's file declares with `module name`, if it does. None when `Syntax` is absent.
      ModuleName: string option
      /// The expressions desugaring and `@entry` made up for each node of `Syntax`, by its node, in the order they were
      /// made: how the type of a workflow bind is found, through `ExprTypes`. Empty when `Syntax` is absent.
      Lowered: Map<NodeId, NodeId list>
      /// The doc comments of the root module's file, each with what it documents, found the first time they are asked
      /// for. None when it did not parse.
      DocComments: Lazy<FileDocComments> }

/// A type-checked module together with what an importer needs from it.
type internal CheckedModule =
    { Typed: TypedModule
      /// The errors found checking the module itself. A module with errors is still typed, with what did check,
      /// for the editor's features; it cannot be run or imported.
      Errors: Diagnostic list
      /// Those of `Errors` that are not compiled to a trap in `Own`, so a debug build cannot run the module.
      Undeferred: Diagnostic list
      /// The errors of the modules it imports, directly or through others, each through the import here that brings
      /// it in, and each once: they are all compiled to traps in their modules, which are imported as clean ones are,
      /// so a debug build runs up to them. A module with an error that can't be deferred fails its importers instead
      /// (#2967).
      ImportedErrors: Diagnostic list
      /// The warnings found checking the module itself, such as a type parameter written the old way (NYX0282). A
      /// module with warnings is run and imported as one without; only the module compiled reports them.
      Warnings: Diagnostic list
      /// The module's own items (imports removed), as the backend will see them.
      Own: Module
      /// Items inlined from the modules it imports, already renamed and ready for the backend.
      Inlined: TopLevelItem list
      /// The module's own top-level definitions named like a definition of the library inlined into it, each with the
      /// name the backend knows it by in `Typed` (`main.ignore`), so that the library's code keeps calling the library's
      /// (#3221). `Own`, `Exports` and the module's requirements keep the names it writes, which its importers read.
      OwnRenames: Map<string, string>
      /// The declared module name, if any.
      Name: string option
      /// The module as parsed, before desugaring.
      Syntax: Module
      /// The expressions made up for each node of `Syntax` (see `CompileResult.Lowered`).
      Lowered: Map<NodeId, NodeId list>
      /// The doc comments of the module's file (see `CompileResult.DocComments`).
      DocComments: Lazy<FileDocComments>
      /// The substitution unification found, which resolves the types inference recorded by node.
      Subst: Subst
      /// The types inference recorded by node, unresolved: only the root module's are resolved (see `compileChecked`).
      RawExprTypes: Map<NodeId, Ty>
      /// The pipes whose type name says nothing (see `InferState.RedundantTypeNames`), for the linters.
      RedundantTypeNames: Set<NodeId>
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
      /// Where each of the module's defs writes the types of its parameters, by the def's symbol (see
      /// `InferState.ParameterTypes`): a call from a module that imports it points back at them (#3109).
      ParameterTypes: Map<SymbolId, SourceSpan option list>
      /// The ambient contexts (the runtime's, as `Console`) by the names the module knows them by, with their
      /// definitions: an importer that cannot name one still needs it where it calls a function that requires it.
      AmbientContexts: Map<string, TypeDefInfo>
      /// What the module re-exports (`export m (a, b)`), each with the module it re-exports from, for its importers to
      /// import too.
      ReExports: ReExported list
      /// The names it gives its importers from its own `module name =` blocks, by re-exporting them at its top level
      /// (`export shapes`), each with the definition or type of the block it stands for: `area` for `shapes.area`.
      BlockReExports: (string * string) list
      /// The kinds of the workflow keywords its context declarations mark with `@customKeywordType`, and those of the
      /// modules it re-exports from, by the member's name: how its importers desugar them (see `WorkflowDesugaring`).
      KeywordKinds: Map<Identifier, KeywordKind> }

/// A re-export, `export m (a, b)`: the import of `m` that selects what is exported again, and `m` as checked.
and internal ReExported =
    { Origin: ModuleOrigin
      Item: ImportItem
      Module: CheckedModule }
