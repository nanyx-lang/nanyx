namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open NyxCompiler.ExpressionRewrites
open NyxCompiler.ModuleOrigins
open NyxCompiler.ImportMerging

module Compiler =
    let private normalizeSource (source: string) =
        source.Replace("\r\n", "\n").Replace("\r", "\n")

    /// The diagnostic for a failed parse.
    let syntaxError (error: ParseError) = Diagnostics.ofParseError error

    /// The diagnostics for the parse of `source`, the file `filePath`, failing with `error`: each character that looks
    /// like one Nanyx reads and isn't that it fails at, and then the syntax error it fails with next, if any (see
    /// `LookAlikes`).
    let private syntaxErrors (filePath: string) (source: string) (error: ParseError) =
        let lookAlikes, rest = LookAlikes.diagnose filePath source error
        lookAlikes @ (rest |> Option.map syntaxError |> Option.toList)

    /// Whether `diagnostic` reports a mistake in the syntax: a syntax error or a look-alike character.
    let private isSyntaxError (diagnostic: Diagnostic) =
        diagnostic.Code = DiagnosticCodes.SyntaxError.Code || diagnostic.Code = DiagnosticCodes.LookAlikeCharacter.Code

    /// Whether the text `result` is the compile of has mistakes in its syntax, so the items they are in were left out
    /// of what was typed.
    let hasSyntaxErrors (result: CompileResult) = result.Diagnostics |> List.exists isSyntaxError

    /// The source of the file at `filePath` parsed past its mistakes, with its doc comments and the diagnostics for the
    /// syntax errors found, which may be none (see `parseModuleFileWithDocsRecovering`), or the errors for contexts
    /// joined with `&` or `+` rather than separated by `,` (see `ContextSeparators`) and keywords written in an old
    /// order (see `KeywordOrder`), after the syntax errors.
    let private parseWithDocs (filePath: string) (source: string) =
        let (items, _, _) as parsedModule, docComments, errors = parseModuleFileWithDocsRecovering filePath source
        let parsed = parsedModule, docComments
        // An error at a character an earlier look-alike's fix rewrites, as the second of a pair of curly quotes
        // is, is that diagnostic's.
        let fixedBy (diagnostic: Diagnostic) (error: ParseError) =
            diagnostic.Fixes |> List.exists (fun fix -> fix.Edits |> List.exists (fun edit -> edit.Span.Start <= error.Span.Start && error.Span.Start < edit.Span.End))
        let syntax =
            errors
            |> List.fold
                (fun (found: Diagnostic list) error ->
                    if found |> List.exists (fun diagnostic -> fixedBy diagnostic error) then found
                    else found @ syntaxErrors filePath source error)
                []
            |> List.distinctBy (fun diagnostic -> diagnostic.Code, diagnostic.Span)
        match ContextSeparators.check (normalizeSource source) items @ KeywordOrder.check (normalizeSource source) items with
        | [] -> Ok (parsed, syntax)
        | oldForms -> Error (syntax @ oldForms)

    /// The source of the file at `filePath` parsed, or the errors it has (see `parseWithDocs`).
    let private parseWithDiagnostics (filePath: string) (source: string) : Result<Module * DeclaredNames * (ModuleName * SourceSpan) list, Diagnostic list> =
        match parseWithDocs filePath source with
        | Ok ((parsed, _), []) -> Ok parsed
        | Ok (_, errors)
        | Error errors -> Error errors

    /// Applies the substitution found by unification to everything inference produced.
    /// A definition's type is reported with the rest variables that only stand for tags it gives out closed (see
    /// `Unifier.outputOnlyRestVars`): a union widened by a call, as a `proc` block's is by each `try`, is left
    /// open for more tags, but no more can come once the definition is done. A rest a context the definition requires
    /// is applied to is left open (`contexts` has each one's contexts): an instance can add tags to it, as
    /// `FromString(int)`'s `#invalidInt` does to the `failure` of `[FromString(a)] string -> #some(a) | failure`.
    let private resolveTypes (subst: Subst) (types: Map<string, Ty>) (contexts: Map<string, TypeExpr list>) (typeDefs: Map<string, TypeDefInfo>) (items: TypedTopLevelItem list) =
        let closeOutputRests name ty =
            let contextVars = contexts |> Map.tryFind name |> Option.defaultValue [] |> List.collect NyxCompiler.TypeResolution.annotationVarNames |> Set.ofList
            let closed =
                Unifier.outputOnlyRestVars ty
                |> List.filter (fun v -> not (v.Name |> Option.exists contextVars.Contains))
                |> List.map (fun v -> v.Id, TyUnion [])
                |> Map.ofList
            if closed.IsEmpty then ty else Unifier.apply closed ty
        let resolvedTypes = types |> Map.map (fun name ty -> Unifier.applySolved subst ty |> closeOutputRests name)
        let resolvedTypeDefs =
            typeDefs
            |> Map.map (fun _ info ->
                // A definition's type variables are its own: its parameters stand for the types it is applied to, and a
                // context's member's own variables are instantiated at each call. They mean nothing the module found
                // for a variable of the same id: an imported definition's are numbered by the module it came from.
                let ownSubst = TypeEnv.freeTyVars info.Underlying |> Set.fold (fun acc id -> acc |> Map.remove id) subst
                { info with Underlying = Unifier.apply ownSubst info.Underlying })
        let rec applyTypedExpr (typedExpr: TypedExpr) =
            { typedExpr with
                Type = Unifier.applySolved subst typedExpr.Type
                Body = typedExpr.Body |> Option.map applyTypedExpr
                Statements = typedExpr.Statements |> Option.map (List.map applyTypedStatement)
                MatchArms = typedExpr.MatchArms |> Option.map (List.map applyTypedMatchArm)
                Arguments = typedExpr.Arguments |> Option.map (List.map applyTypedExpr) }
        and applyTypedStatement statement =
            match statement with
            | TypedDefStatement(isExport, name, typeOpt, expr) ->
                TypedDefStatement(isExport, name, typeOpt, applyTypedExpr expr)
            | TypedExprStatement expr -> TypedExprStatement (applyTypedExpr expr)
            | TypedUseStatement expr -> TypedUseStatement (applyTypedExpr expr)
            | TypedImportStatement _
            | TypedTypeDefStatement _ -> statement
        and applyTypedMatchArm (patterns, expr) =
            let mappedPatterns =
                patterns |> List.map (fun pattern -> { pattern with Type = Unifier.applySolved subst pattern.Type })
            (mappedPatterns, applyTypedExpr expr)
        let resolvedItems =
            items
            |> List.map (fun item ->
                match item with
                | TypedExprItem expr -> TypedExprItem (applyTypedExpr expr)
                | TypedDef (TypedValueDef(isExport, name, typeOpt, expr)) ->
                    TypedDef (TypedValueDef(isExport, name, typeOpt, applyTypedExpr expr))
                | TypedDef (TypedTypeDef _)
                | TypedImport _
                | TypedModuleDecl _ -> item)
        resolvedTypes, resolvedTypeDefs, resolvedItems

    /// `expr` with each builder block that uses no workflow keyword (`blocks`, by the node of the call it is written
    /// as) replaced by its block, which runs where it is written.
    let private inlineWorkflowBlocks (blocks: Set<NodeId>) (expr: Expression) : Expression =
        if blocks.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind with
                | FunctionCall(_, _, [ Kind (Lambda([], body, _)) ]) when blocks.Contains current.Id -> body.Kind
                | other -> other)

    /// The module as the backend will see it: the typed items turned back into syntax, with each pipe to an
    /// attached function, and each hole that calls a derived definition, written with the name it resolved to (`pipeTargets`), each operator that calls a definition as
    /// that call (`operatorCalls`, through the instance `operatorInstances` gives where it has one), each expression inference read as another as that one (`readAs`),
    /// each read of a built-in property as a call (`properties`), each builder block that uses no keyword as its block
    /// (`workflowBlocks`), and without import statements. `defer` compiles the module's errors to traps first, for a
    /// debug build, and gives the diagnostics it placed, which are returned with the module.
    let private moduleFromTypedItems (defer: Expression -> Expression * Diagnostic list) (pipeTargets: Map<NodeId, Identifier>) (operatorCalls: Map<NodeId, Identifier>) (operatorInstances: Map<NodeId, TypeExpr>) (readAs: Map<NodeId, Expression>) (properties: Map<NodeId, BuiltInProperty>) (workflowBlocks: Set<NodeId>) (items: TypedTopLevelItem list) : Module * Diagnostic list =
        let lower (expr: Expression) =
            let deferred, placed = defer expr
            // An expression an error made a trap stays one, though inference read it as another, as it does a unit
            // literal (#2966). A module without errors is given back as it is, with none.
            let readAs =
                if readAs.IsEmpty || LanguagePrimitives.PhysicalEquality deferred expr then readAs
                else
                    let trapped =
                        SyntaxTree.expressionsWithin deferred
                        |> List.choose (fun within -> match within.Kind with CrashSyntax.Call _ -> Some within.Id | _ -> None)
                        |> Set.ofList
                    readAs |> Map.filter (fun node _ -> not (trapped.Contains node))
            deferred |> retargetPipes pipeTargets |> callOperators operatorCalls operatorInstances |> writeAsRead readAs |> callBuiltInProperties properties |> inlineWorkflowBlocks workflowBlocks, placed
        let lowered =
            items
            |> List.choose (function
                | TypedModuleDecl name -> Some (ModuleDecl(false, name), [])
                | TypedImport _ -> None
                | TypedDef (TypedValueDef(isExport, name, typeOpt, expr)) ->
                    let value, placed = lower expr.Expr
                    Some (Def (ValueDef(isExport, name, typeOpt, value)), placed)
                | TypedDef (TypedTypeDef(name, modifiers, parameters, body)) ->
                    Some (Def (TypeDef(name, modifiers, parameters, body)), [])
                | TypedExprItem expr ->
                    let value, placed = lower expr.Expr
                    Some (Expr value, placed))
        lowered |> List.map (fst >> TopLevelItem.synthetic), lowered |> List.collect snd

    /// `typeDefs` with each type `origin` defines given the doc comment its definition has in `docs`, the doc comments of
    /// `origin`'s file, which a type's description says of it (see `TypeDefInfo.Doc`).
    let private withDocs (origin: ModuleOrigin) (docs: Lazy<FileDocComments>) (typeDefs: Map<string, TypeDefInfo>) =
        if typeDefs |> Map.exists (fun _ info -> info.Home = origin && not info.IsContext) then
            let docs = docs.Value
            let declaredAt = docs.TopLevel |> Map.ofList
            let textAt (span: SourceSpan) =
                docs.Comments |> List.tryPick (fun comment -> match comment.Target with DocumentsDeclaration at when at = span -> Some comment.Text | _ -> None)
            typeDefs
            |> Map.map (fun name info ->
                match declaredAt |> Map.tryFind name |> Option.bind textAt with
                | Some text when info.Home = origin -> { info with Doc = text }
                | _ -> info)
        else typeDefs

    /// The modules one compilation has checked, by `originKey`, so that a module imported along several paths is
    /// checked once and every importer sees the same symbols; with the package whose files' imports are resolved
    /// through its module table, if the compilation is of a package's file; with the standard-library file open in
    /// the editor, if the compilation is of one (see `compileStandardLibraryFile`); and the listener each module's check
    /// is reported to; and how the compilation reads the text of a source file other than the one it compiles, given its
    /// full path (from disk, or an editor's unsaved buffer).
    type internal CheckedModules(package: PackageModules option, edited: EditedLibraryFile option, listener: CompileListener, readFile: string -> string) =
        inherit System.Collections.Generic.Dictionary<string, Result<CheckedModule, CompilationPhase * Diagnostic list>>()
        /// The modules an import brought in with it that have not been needed yet: those read from the checked library are
        /// read only when something needs them (see `CheckedLibrary`).
        let pending = System.Collections.Generic.Dictionary<string, Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list>>>()
        new(package, edited, listener) = CheckedModules(package, edited, listener, File.ReadAllText)
        new(package) = CheckedModules(package, None, CompileListener.none)
        member _.Package = package

        /// Adds the module `key`, which an import brought in with it, unless the compilation has it already.
        member this.AddImported(key: string, imported: Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list>>) =
            if not (this.ContainsKey key) then
                if imported.IsValueCreated then this.TryAdd(key, imported.Value) |> ignore
                else pending.TryAdd(key, imported) |> ignore

        /// The module `key`, if the compilation has it, read now if an import brought it in and nothing needed it before.
        member this.TryGetChecked(key: string, [<System.Runtime.InteropServices.Out>] checkedModule: byref<Result<CheckedModule, CompilationPhase * Diagnostic list>>) =
            match pending.TryGetValue key with
            | true, imported when not (this.ContainsKey key) ->
                this.[key] <- imported.Value
                pending.Remove key |> ignore
            | _ -> ()
            this.TryGetValue(key, &checkedModule)

        /// Every module the compilation has, each read only when it is asked for: those it checked or needed, and those
        /// imports brought in that nothing has needed yet.
        member this.Imported : (string * Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list>>) list =
            // A forced `lazy`: F#'s `Lazy.CreateFromValue` says its value is not created.
            let ready checkedModule = let read = lazy checkedModule in read.Force() |> ignore; read
            [ for KeyValue(key, checkedModule) in this -> key, ready checkedModule
              for KeyValue(key, imported) in pending do
                  if not (this.ContainsKey key) then yield key, imported ]
        member _.Listener = listener
        member _.Edited = edited
        /// The checked file of the package this compilation checks to write it (see `CheckedPackages`), if it is one:
        /// that package's own files are checked from their source.
        member val Writing: string option = None with get, set
        member _.ReadFile(path: string) = readFile path

        /// The file the syntax nodes of the module from `origin` are reported in: the editor's, for the file it has open.
        member _.FileName(origin: ModuleOrigin) =
            match edited with
            | Some edited when origin = StandardLibraryModule edited.Module -> edited.Path
            | _ -> sourceFileName origin

    /// The standard-library modules this process has checked, by `originKey` and source, each with the modules it
    /// imports, which a compilation that imports it needs too (`compileFrom` resolves inlined definitions' types by
    /// them). A checked module is never changed, and sharing one is safe: its type variables are numbered by the module
    /// itself, as they would be if it were checked again, and its syntax nodes and symbols keep ids no other module has.
    let private checkedStandardLibrary =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list> * (string * Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list>>) list>>()

    /// A source file's module as a compilation that imported it checked it (see `checkedSourceFiles`).
    type private CheckedSourceFile =
        { /// The package whose module table its imports were resolved through.
          Package: PackageModules option
          /// Every source file its check read, with the text it read: its own, its other fragments', and those of the
          /// modules it imports, all the way down.
          Read: (string * string) list
          Module: CheckedModule
          /// The modules it imports, all the way down, as `checkedStandardLibrary` keeps them.
          Imports: (string * Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list>>) list }

    /// The source files' modules this process has checked as imports, by `originKey`, each kept while every file its
    /// check read still reads the same, so the language server does not check a large module again each time a file
    /// that imports it changes. Only a module that checked without errors is kept: an error can depend on which file
    /// the compilation started from (an import cycle is reported where that file enters it).
    let private checkedSourceFiles = System.Collections.Concurrent.ConcurrentDictionary<string, CheckedSourceFile>()

    /// The checked files of the packages in the package cache this process has read, or written, by path; none for one
    /// it can't use (see `CheckedPackages`).
    let private checkedPackages = System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<CheckedPackage option>>()

    /// Forgets the checked files of packages this process has read, so that the next compilation reads them again, as a
    /// new process does.
    let internal forgetCheckedPackages () = checkedPackages.Clear()

    /// The build's host: the package's (see `PackageModules.Host`), none for a library, the one a script's `::host`
    /// names, or the console host for a loose script or source; with its package's modules, where the import of the
    /// host, and the host's own imports, resolve.
    let private buildHostOf (checkedModules: CheckedModules) : Host option * PackageModules option =
        match checkedModules.Package with
        | Some package -> package.Host, package.HostPackage
        | None ->
            CheckedLibrary.knowShippedDeclarations.Force()
            Some HostPackages.console.Value, ShippedPackages.tryModules Hosts.consolePackage

    /// The test host's package's modules, in a test build (see `PackageModules.TestHost`).
    let private testHostModulesOf (checkedModules: CheckedModules) : PackageModules option =
        checkedModules.Package |> Option.bind _.TestHost |> Option.map snd

    /// The host of the module from `origin`, with its package's modules: the build's (see `buildHostOf`), but in a test
    /// build for its root, and for a module of the test host's own package, which run under the test host.
    let private hostOf (checkedModules: CheckedModules) (origin: ModuleOrigin) : Host option * PackageModules option =
        match origin, checkedModules.Package with
        | SourceFile path, Some ({ TestHost = Some(testHost, testModules) } as package)
            when package.TestRoot |> Option.exists (PackageModules.samePath path) || (PackageModules.ownerOf testModules path).IsSome ->
            Some testHost, Some testModules
        | _ -> buildHostOf checkedModules

    /// Parses, desugars and type-checks one module, resolving and checking the modules it imports first. An
    /// error says which phase it came from. The check is reported to the listener of `checkedModules` as it starts
    /// and ends, by the module's `originKey`, with the imports' checks inside it.
    let rec internal typecheckModule (checkedModules: CheckedModules) (origin: ModuleOrigin) (source: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        checkedModules.Listener.ModuleStarted key
        let stopwatch = System.Diagnostics.Stopwatch.StartNew()
        let checkedModule = checkModule checkedModules origin source visited
        checkedModules.Listener.ModuleFinished(key, stopwatch.Elapsed)
        checkedModule

    /// `typecheckModule`, unreported.
    and private checkModule (checkedModules: CheckedModules) (origin: ModuleOrigin) (source: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        let buildHost, hostModules = hostOf checkedModules origin
        // A file of the host's package, or of a package it depends on, such as the interface package it implements.
        let inHostPackage (path: string) = hostModules |> Option.exists (fun modules -> (PackageModules.ownerOf modules path).IsSome)
        // A file that may require `HostRuntime`: one of a host's own package, its roots and the modules they share, or of
        // a host library the host's package depends on (see `HostLibraries.field`). The host is the build's, and the
        // package being built when it is a host itself, as when it is checked or packed on its own; so is a host library
        // being built on its own.
        let hostPackages =
            Option.toList hostModules
            @ (checkedModules.Package |> Option.filter _.DeclaresHost |> Option.toList)
        let isHost (path: string) =
            hostPackages
            |> List.exists (fun modules ->
                modules.Files |> List.exists (PackageModules.samePath path)
                || PackageModules.ownerOf modules path |> Option.exists _.HostLibrary)
            || checkedModules.Package
               |> Option.exists (fun package -> package.HostLibrary && package.Files |> List.exists (PackageModules.samePath path))
        // The module's own host: the build's, for a module of the program itself, which sees the host's prelude and may
        // declare its `@entry`. A file of a package the program depends on, or of the host's package or one it depends on,
        // is a library's, and a module of the standard library is the standard library's: none.
        let host =
            match origin with
            | StandardLibraryModule _ -> None
            | SourceFile path when inHostPackage path -> None
            | SourceFile path when checkedModules.Package |> Option.exists (fun package -> not (PackageModules.isProgramFile package path)) -> None
            | SourceFile _
            | InMemory -> buildHost
        let preludeKey = originKey (StandardLibraryModule StandardLibrary.preludeModuleName)
        // The imports of `module'`, whose `module name =` blocks are `moduleBlocks`. Every module but the prelude imports
        // it first, so that anything it imports or defines itself wins. An import of a `module name =` block of this file
        // is not of another file: inference resolves it.
        let importsOf moduleBlocks (module': Module) =
            let blockImported = NyxCompiler.Infer.blockImports moduleBlocks module' |> List.map _.Import
            let fileImports = SyntaxTree.collectImports module' |> List.filter (fun item -> not (List.contains item blockImported))
            // Then the host's prelude, in a module of the program (see `Host.Prelude`).
            let hostPrelude =
                host
                |> Option.bind _.PreludeImport
                |> Option.map (fun prelude -> ImportItem.synthetic (ModuleImport prelude) None)
                |> Option.toList
            if key = preludeKey then
                fileImports
            else
                ImportItem.synthetic (ModuleImport (StandardLibrary.importPrefix + StandardLibrary.preludeModuleName)) None
                :: hostPrelude
                @ fileImports
        // The module an import brings in, checked, with where it is from; each import is checked once.
        let importedModules = System.Collections.Generic.Dictionary<ImportItem, Result<ModuleOrigin * CheckedModule, Diagnostic list>>(HashIdentity.Structural)
        let importModule (item: ImportItem) : Result<ModuleOrigin * CheckedModule, Diagnostic list> =
            match importedModules.TryGetValue item with
            | true, imported -> imported
            | false, _ ->
                let imported = checkImportItem checkedModules origin key hostModules inHostPackage visited item
                importedModules.[item] <- imported
                imported
        // The kinds of the workflow keywords the modules `imports` bring in declare (see `CheckedModule.KeywordKinds`).
        let importedKeywordKinds (imports: ImportItem list) =
            imports
            |> List.choose (fun item -> match importModule item with Ok (_, imported) -> Some imported.KeywordKinds | Error _ -> None)
            |> List.fold (fun kinds imported -> Attributes.withKeywordKinds imported kinds) Map.empty
        let resolved =
            // A module with syntax errors is checked with the items that read, for the editor's features, and the errors
            // are its own: it cannot be run or imported.
            parseWithDocs (checkedModules.FileName origin) source
            // A module split across files is checked as one: its other files' items join this one's.
            |> Result.bind (fun ((parsed, docComments), syntaxDiagnostics) ->
                let packages = Option.toList checkedModules.Package @ Option.toList hostModules
                PartialModules.assemble parseWithDiagnostics (PartialModules.otherFragments checkedModules.ReadFile packages checkedModules.Edited origin) parsed
                |> Result.map (fun assembled -> (let ast, _, _ = parsed in ast), docComments, assembled, syntaxDiagnostics)
                |> Result.mapError (fun diagnostics -> syntaxDiagnostics @ diagnostics))
            |> Result.mapError (fun diagnostics -> Parsed, diagnostics)
            // A record that names a field twice is reported before anything is inferred from it (see `DuplicateFields`).
            |> Result.bind (fun ((_, _, (items, declared, _), syntaxDiagnostics) as parsed) ->
                match DuplicateFields.check declared items with
                | [] -> Ok parsed
                | errors -> Error (Parsed, syntaxDiagnostics @ errors))
            |> Result.bind (fun (ast, docComments, (items, declared, moduleBlocks), syntaxDiagnostics) ->
                match WorkflowDesugaring.desugarWithDiagnostics (importedKeywordKinds (importsOf moduleBlocks items)) items with
                | _, (_ :: _ as errors), _, _, _ -> Error (Desugared, syntaxDiagnostics @ errors)
                | desugaredAst, [], memberPlaces, lowered, delayCalls ->
                    // A test build's program files, but its root, run their tests through the dispatcher it gives
                    // them, and have no entry point.
                    let testBuild =
                        match origin, checkedModules.Package with
                        | SourceFile path, Some ({ TestRoot = Some root } as package) ->
                            PackageModules.isProgramFile package path && not (PackageModules.samePath path root)
                        | _ -> false
                    Attributes.applyAttributes host testBuild desugaredAst
                    |> Result.map (fun (desugared, entries, components) ->
                        // A module's statements run as an entry point of their own (see `Scripts`), but in a test build,
                        // which runs its tests and nothing else.
                        let desugared, script =
                            match origin, entries with
                            | (SourceFile _ | InMemory), _ when testBuild ->
                                desugared |> List.filter (function Kind (Expr _) -> false | _ -> true), None
                            | (SourceFile _ | InMemory), _ when host.IsSome -> Scripts.asEntry host.Value (fun span -> (NyxCompiler.InferState.blockAround moduleBlocks span).IsSome) entries desugared
                            | _ -> desugared, None
                        let declared = Attributes.withDispatcherDeclared declared desugared
                        // Each module-level def that starts a task is a scope for its tasks (see `TaskScopes`).
                        let desugared, declared =
                            match origin with
                            | StandardLibraryModule _ -> desugared, declared
                            | SourceFile _
                            | InMemory -> TaskScopes.wrap declared (fun call -> script |> Option.exists (fun (entry, _) -> entry.Call |> List.contains call.Id)) desugared
                        let desugared, nameErrors = BuiltinTypeNames.replace declared desugared
                        let nameErrors = TypeNames.definitionErrors declared @ nameErrors
                        let lowered = entries |> List.fold (fun acc entry -> Map.add entry.Attribute entry.Call acc) lowered
                        // The keywords it declares, read before `applyAttributes` takes a def's attribute away.
                        let ownKeywordKinds = Attributes.keywordKinds items
                        (ast, docComments), declared, moduleBlocks, desugared, (memberPlaces, delayCalls), lowered, entries, components, script, syntaxDiagnostics @ nameErrors, ownKeywordKinds)
                    |> Result.mapError (fun diagnostics -> Desugared, syntaxDiagnostics @ diagnostics))
        match resolved with
        | Result.Error failure -> Error failure
        | Result.Ok ((ast, docComments), declared, moduleBlocks, desugared, (memberPlaces, delayCalls), lowered, entries, components, script, nameErrors, ownKeywordKinds) ->
            let imports = importsOf moduleBlocks desugared
            let initial =
                { Env =
                    match origin with
                    | StandardLibraryModule _ -> Intrinsics.typeEnv
                    | SourceFile _
                    | InMemory -> TypeEnv.empty
                  State =
                    { NyxCompiler.Infer.emptyState with
                        Declared = declared
                        ModuleBlocks = moduleBlocks
                        WorkflowMemberPlaces = memberPlaces
                        DelayCalls = delayCalls
                        Home = origin
                        ScriptEntry = script |> Option.map snd
                        StandardLibraryExports = CheckedLibrary.standardLibraryExportPaths }
                  Errors = []
                  DeferredImportErrors = []
                  Inlined = []
                  PreludeInlined = []
                  PreludeTypeNames = Set.empty
                  InlinedTypes = Map.empty
                  InlinedInstances = Map.empty
                  InlinedUseContexts = Map.empty
                  InlinedAssociatedArguments = Map.empty
                  ImportedContextTypes = Map.empty
                  ProvidesContexts = providesContexts host hostModules
                  HostImport = host |> Option.map _.Import
                  CarriedAmbientMembers = []
                  ComponentExports = []
                  Tests = []
                  UnqualifiedSources = Map.empty
                  ImportedFiles = Map.empty
                  ReExports = []
                  Merged = Set.empty
                  // A module of a host's package, or of a package the program depends on, isn't the program's own: its
                  // imports' definitions are inlined under qualified names, so none collides with one of the program's.
                  QualifiesImports =
                    match origin with
                    | SourceFile path ->
                        inHostPackage path
                        || checkedModules.Package |> Option.exists (fun package -> not (PackageModules.isProgramFile package path))
                    | StandardLibraryModule _
                    | InMemory -> false }
            // A re-export written in a block counts for the block's exports, not the file's (see `Visibility.blockExports`).
            let reExportItems =
                ModuleLinking.reExportsOf desugared |> List.filter (fun item -> (NyxCompiler.InferState.blockAround moduleBlocks item.Span).IsNone)
            let merged =
                imports
                |> List.fold (fun (merged: MergedImports) item ->
                    match importModule item with
                    | Error diagnostics -> { merged with Errors = merged.Errors @ diagnostics }
                    | Ok (importOrigin, imported) ->
                        // A module whose errors are all deferred is imported, its errors with it, through this import.
                        let deferred =
                            imported.Errors @ imported.ImportedErrors |> List.map (fun diagnostic -> { diagnostic with Import = Some item.Span })
                        let merged = { merged with DeferredImportErrors = merged.DeferredImportErrors @ deferred }
                        mergeWritten preludeKey reExportItems importOrigin item imported merged) initial
            // Then the definitions owned by the types the imports bring in, from their modules (see `OwnedDefinitions`).
            // The standard library imports what it uses itself.
            let merged =
                match origin with
                | StandardLibraryModule _ -> merged
                | SourceFile _
                | InMemory when not merged.Errors.IsEmpty -> merged
                | SourceFile _
                | InMemory ->
                    OwnedDefinitions.owners originKey (merged.Merged.Add key) merged.Env merged.State
                    |> List.filter (fun owner -> not (visited.Contains (originKey owner)))
                    |> List.fold (fun (merged: MergedImports) owner ->
                        let readSource () =
                            match owner with
                            | StandardLibraryModule name -> StandardLibrary.tryLoadSource name |> Option.defaultValue ""
                            | SourceFile path -> checkedModules.ReadFile path
                            | InMemory -> ""
                        match typecheckImport checkedModules owner readSource (visited.Add key) with
                        | Ok ownerModule when ownerModule.Undeferred.IsEmpty ->
                            match OwnedDefinitions.selection owner ownerModule.Typed.TypeDefs merged.Env merged.State ownerModule.Exports.Keys with
                            | [] -> merged
                            | names ->
                                let source =
                                    match owner with
                                    | StandardLibraryModule name -> ModuleImport (StandardLibrary.importPrefix + name)
                                    | SourceFile _
                                    | InMemory -> PathImport (originKey owner)
                                let item = { Source = source; Alias = None; Selection = Some (names |> List.map (fun name -> name, SourceSpan.none)); Span = SourceSpan.none }
                                mergeWritten preludeKey [] owner item ownerModule merged
                        | _ -> merged) merged
            let merged = { merged with Errors = merged.Errors @ ModuleLinking.reExportClashes desugared merged.ReExports }
            let env = merged.Env
            // A name two imports bring in is an error only where it is used, and only if nothing nearer binds it.
            let ambiguousImports =
                merged.UnqualifiedSources
                |> Map.toSeq
                |> Seq.choose (fun (name, sources) ->
                    match sources, TypeEnv.tryFind name env |> Option.bind (fun scheme -> scheme.Symbol) with
                    | _ :: _ :: _, Some symbol -> Some (name, (symbol, sources |> List.map (fun (_, moduleName) -> $"{moduleName}.{name}")))
                    | _ -> None)
                |> Map.ofSeq
            let state =
                { merged.State with
                    AmbiguousImports = ambiguousImports
                    PreludeContexts = OperatorContexts.inModule (key = preludeKey) desugared merged.State.PreludeContexts }
            if not merged.Errors.IsEmpty then
                // A module imported along several paths fails each importer with the same diagnostics, each through
                // the first import that brings it in.
                Error (Typed, List.distinctBy Diagnostics.inItsModule merged.Errors)
            else
                // An export that requires contexts runs under the host's `provide`: under a host whose root has none, it
                // can't, and its def that calls `provide` is left out (see `Attributes.applyAttributes`).
                let desugared, provided, unprovidedErrors =
                    match components.Provided, host with
                    | _ :: _, Some host when (TypeEnv.tryFind $"{Hosts.importAlias}.{Hosts.provideFunction}" env).IsNone ->
                        let wrappers = components.Provided |> List.map (_.Name >> Hosts.providedExportName) |> Set.ofList
                        let kept = desugared |> List.filter (function Kind (Def (ValueDef(_, name, _, _))) -> not (wrappers.Contains name) | _ -> true)
                        kept, [], components.Provided |> List.map (Hosts.unprovidedExport host)
                    | provided, _ -> desugared, provided, []
                // A capitalised tag is a custom pattern's, and a match on one runs the pattern (see `CustomPatterns`).
                let patternScope = CustomPatterns.scopeOf env ambiguousImports state.ModuleBlocks desugared
                let desugared, rewriteErrors = CustomPatterns.rewrite patternScope desugared
                let patternErrors = CustomPatterns.check patternScope desugared @ rewriteErrors
                // A parameter that takes a type is written as one of type `Type(a)` (see `TypeArguments`), and a
                // context's associated types as its last parameters (see `AssociatedTypes`).
                // A signature that names a dimension is read with the storages it leaves out, each of which requires `Num`
                // (see `DimensionSignatures`).
                let desugared, signatureErrors = DimensionSignatures.ofModule state.TypeDefs desugared
                let desugared, typeArgumentErrors = TypeArguments.rewrite desugared
                let desugared, associatedErrors = AssociatedTypes.rewrite state.TypeDefs desugared
                let associatedErrors = signatureErrors @ typeArgumentErrors @ associatedErrors
                let types, items, (state: NyxCompiler.Infer.InferState), inferErrors = NyxCompiler.InferModule.inferModuleWithEnv env state desugared
                let subst, mismatches = Solving.solveLeavingUnknown state.Constraints |> fun (subst, mismatches) -> Unifier.keepWrittenNames (Seq.append types.Values (state.AssociatedVariables |> Seq.map TyVar)) subst, mismatches
                // Every error in the module is reported: the items that failed to check, and each conflict among the
                // constraints of those that did, in the order they are in the source rather than the order solving
                // found them. The module is typed all the same, with what did check.
                // The module's own top-level defs.
                let ownTopLevel =
                    state.Scopes
                    |> Map.keys
                    |> Seq.map (fun id -> state.Symbols.[id])
                    |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue)
                    |> List.ofSeq
                let contextTypes = RequiredContexts.ofDefs state subst ownTopLevel
                // A mismatch where an entry point is passed to its host's `run` is the entry point not fitting the host.
                let entryMismatch (entry: EntryPoint) =
                    let expected =
                        TypeEnv.tryFind $"{Hosts.importAlias}.{Hosts.runFunction}" env
                        |> Option.bind (fun scheme -> match Unifier.unalias scheme.Type with TyFunc(parameter, _, _) -> Some parameter | _ -> None)
                    let required =
                        ownTopLevel
                        |> List.tryFind (fun symbol -> symbol.Name = entry.Name)
                        |> Option.bind (fun symbol -> contextTypes |> Map.tryFind symbol.Id)
                        |> Option.defaultValue []
                    Hosts.entryMismatch entry (types |> Map.tryFind entry.Name |> Option.map (Unifier.apply subst)) required expected
                // A mismatch where an export is passed to its host's `provide` is a context it requires that the host
                // doesn't provide.
                let exportMismatch (export: EntryPoint) =
                    let provides =
                        TypeEnv.tryFind $"{Hosts.importAlias}.{Hosts.provideFunction}" env
                        |> Option.bind (fun scheme -> match Unifier.unalias scheme.Type with TyFunc(parameter, _, _) -> Some parameter | _ -> None)
                    let required =
                        ownTopLevel
                        |> List.tryFind (fun symbol -> symbol.Name = export.Name)
                        |> Option.bind (fun symbol -> contextTypes |> Map.tryFind symbol.Id)
                        |> Option.defaultValue []
                    Hosts.exportMismatch export required provides
                // As is a context missing where an export is called through `provide`, which is at the attribute.
                let inferErrors =
                    inferErrors
                    |> List.map (fun diagnostic ->
                        match provided |> List.tryFind (fun export -> diagnostic.Span = Some export.Argument) with
                        | Some export when diagnostic.Code = DiagnosticCodes.MissingContextMembers.Code || diagnostic.Code = DiagnosticCodes.UndeclaredContext.Code ->
                            exportMismatch export
                        | _ -> diagnostic)
                    |> List.distinct
                let mismatchErrors =
                    mismatches
                    // A mismatch of a call's arguments is reported at the one that doesn't fit (see `ArgumentSpans`).
                    |> List.map (fun (text, span, kind) ->
                        match state.ArgumentSpans |> Map.tryFind span with
                        | Some reported -> text, reported.Value, kind
                        | None -> text, span, kind)
                    |> List.sortBy (fun (_, span, _) -> span.Start)
                    |> List.map (fun (text, span, kind) ->
                        let message = Unifier.messageText state.TypeDefs text |> CharLiterals.withHint kind
                        match entries |> List.tryFind (fun entry -> entry.Argument = span), provided |> List.tryFind (fun export -> export.Argument = span) with
                        // A keyword's call that calls a local in the keyword's def's place says so (see `HiddenKeywords`).
                        | _ when state.HiddenKeywords.ContainsKey span ->
                            HiddenKeywords.diagnostic (Unifier.apply subst >> Unifier.tyToStringIn state.TypeDefs) state.HiddenKeywords.[span]
                        | Some entry, _ -> entryMismatch entry
                        | None, Some export -> exportMismatch export
                        | None, None ->
                            match kind with
                            // Outside a nominal type's home module, a value of its shape is no value of it: the error says
                            // why, and how to make one.
                            | NominalShape(nominal, isPrivate, shape) when not (Visibility.inNominalHome state nominal span) ->
                                let names = (Map.toList env.Names) @ (types |> Map.toList |> List.map (fun (name, ty) -> name, TypeEnv.mono (Unifier.apply subst ty)))
                                ProtectedShapes.shapeError state names (normalizeSource source) desugared nominal isPrivate shape span
                            | ContextsNotAllowed -> Diagnostics.at DiagnosticCodes.ContextsNotAllowed span message
                            | MisspelledField(onlyLeft, rightRecord, onlyRight, leftRecord) ->
                                MisspelledFields.diagnose state desugared span (onlyLeft, rightRecord, onlyRight, leftRecord)
                                |> Option.defaultWith (fun () -> Diagnostics.at (MemoryCells.mismatchCode state span) span message)
                            | TypesDiffer
                            | CharAndString
                            | NominalShape _ -> Diagnostics.at (MemoryCells.mismatchCode state span) span message)
                    |> List.distinct
                // What each call requires of the def it calls, by the call's node: `HostRuntime`, which has no members,
                // is found by it.
                // A function that is not a lambda, as a foreign function is, has the contexts it requires in its type.
                let calleeContexts (call: NodeId) =
                    let ofDef =
                        match state.References |> Map.tryFind call with
                        | Some symbol ->
                            match merged.ImportedContextTypes |> Map.tryFind symbol, contextTypes |> Map.tryFind symbol with
                            | Some (_, contexts), _
                            | None, Some contexts -> contexts
                            | None, None -> []
                        | None -> []
                    let ofType =
                        match state.CalleeTypes |> Map.tryFind call |> Option.map (Unifier.apply subst >> Unifier.unalias) with
                        | Some (TyFunc(_, _, contexts)) -> contexts
                        | _ -> []
                    ofDef @ ofType
                // A task holds nothing of a `memory` block (see `TaskCaptures`).
                let taskErrors =
                    TaskCaptures.captureErrors state.TaskWork (Unifier.apply subst) desugared
                    @ TaskCaptures.escapeErrors
                        (fun name ->
                            match types |> Map.tryFind name |> Option.map (Unifier.apply subst >> Unifier.unalias) with
                            | Some (TyFunc(_, result, _)) -> Some result
                            | _ -> None)
                        desugared
                    @ TaskCaptures.unscopedErrors
                        (fun name -> types |> Map.tryFind name |> Option.map (Unifier.apply subst))
                        (fun call -> state.CalleeTypes |> Map.tryFind call |> Option.map (Unifier.apply subst))
                        desugared
                let runtimeErrors =
                    Hosts.runtimeErrors isHost origin (entries @ provided @ (script |> Option.map fst |> Option.toList)) (provided |> List.map (_.Name >> Hosts.providedExportName) |> Set.ofList) state.Symbols contextTypes state.ContextProviders calleeContexts desugared
                // A script whose statements need `HostRuntime` is told so where they need it, one whose statements use
                // what escaped a scoped call is told of the escape (NYX0233), and one that calls a def that starts a task
                // it can't keep is told where (see `TaskCaptures`): that its entry point, which has no span, then doesn't
                // fit the host's `run` says nothing more.
                let mismatchErrors =
                    match script with
                    | Some _ when not runtimeErrors.IsEmpty || not state.EscapedContexts.IsEmpty || not taskErrors.IsEmpty ->
                        mismatchErrors |> List.filter (fun diagnostic -> not (diagnostic.Code = DiagnosticCodes.ContextsNotAllowed.Code && diagnostic.Span.IsNone))
                    | _ -> mismatchErrors
                // What the file gives from its own blocks by re-exporting them, and what those blocks re-export of other
                // files' modules, which is a re-export of each of those that selects what the block gives of it.
                let blockReExports = ModuleLinking.blockReExports state desugared
                let blockModuleReExports =
                    blockReExports
                    |> List.collect (fun (blockImport, own) ->
                        let ownNames = own |> List.map fst |> Set.ofList
                        let given = blockImport.Names.Keys |> Seq.filter (ownNames.Contains >> not) |> Set.ofSeq
                        Visibility.moduleReExportsIn moduleBlocks desugared blockImport
                        |> List.choose (fun reExported ->
                            match importModule reExported with
                            | Ok (reExportedOrigin, imported) ->
                                let reExport = { Origin = reExportedOrigin; Item = reExported; Module = imported }
                                match (ModuleLinking.reExportSources reExport).Keys |> Seq.filter given.Contains |> List.ofSeq with
                                | [] -> None
                                | names ->
                                    let selection = names |> List.map (fun name -> name, blockImport.Import.Span)
                                    Some { reExport with Item = { reExported with Selection = Some selection; Span = blockImport.Import.Span } }
                            | Error _ -> None))
                let blockReExportErrors =
                    ModuleLinking.blockReExportClashes desugared merged.ReExports (blockReExports |> List.map (fun (blockImport, own) -> blockImport.Import, own))
                // A host's component root, checked in a component build, provides the contexts its core root does.
                let componentRootErrors =
                    match buildHost, hostModules, origin with
                    | Some buildHost, Some modules, SourceFile path when buildHost.IsComponent && (modules.Modules |> Map.tryFind buildHost.Module |> Option.exists (List.exists (PackageModules.samePath path))) ->
                        let coreRoot = ImportItem.synthetic (ModuleImport $"{buildHost.Package}/{buildHost.Component.Value.CoreRoot}") None
                        let ownRun = ownTopLevel |> List.tryFind (fun symbol -> symbol.Name = Hosts.runFunction)
                        match importModule coreRoot, ownRun, types |> Map.tryFind Hosts.runFunction with
                        | Ok(_, core), Some run, Some runType ->
                            match core.Exports |> Map.tryFind Hosts.runFunction |> Option.bind (fun symbol -> core.RawSymbolTypes |> Map.tryFind symbol.Id) with
                            | Some coreRun -> Hosts.componentRootErrors buildHost run.Declaration (Unifier.apply subst runType) (Unifier.apply core.Subst coreRun)
                            | None -> []
                        | _ -> []
                    | _ -> []
                // A def marked `@component` has a type that can cross a component's boundary.
                // One that requires contexts is run under the host's (see `provided`), so they don't cross it.
                let runUnderHost = components.Provided |> List.map _.Name |> Set.ofList
                let componentErrors =
                    ComponentInterface.errors
                        (fun name ->
                            types |> Map.tryFind name |> Option.map (Unifier.apply subst)
                            |> Option.map (if runUnderHost.Contains name then ComponentInterface.withoutContexts else id))
                        (fun name -> not (runUnderHost.Contains name) && state.ContextRequirements |> Map.tryFind name |> Option.exists (List.isEmpty >> not))
                        desugared
                        components.Exports
                        components.Imports
                    @ unprovidedErrors
                // A test is a test's type, needs only the contexts the test host provides, and only a test refers to it.
                let testErrors =
                    TestChecks.errors
                        components.Tests
                        (fun name -> types |> Map.tryFind name |> Option.map (Unifier.apply subst))
                        (fun name ->
                            ownTopLevel
                            |> List.tryFind (fun symbol -> symbol.Name = name)
                            |> Option.bind (fun symbol -> contextTypes |> Map.tryFind symbol.Id)
                            |> Option.defaultValue [])
                        state.References
                        state.Symbols
                        desugared
                let moduleDiagnostics = testErrors @ componentRootErrors @ componentErrors @ Hosts.reservedExternErrors isHost origin desugared @ blockReExportErrors @ nameErrors @ patternErrors @ associatedErrors @ CustomPatterns.withoutUnknownNoMatch patternErrors inferErrors @ mismatchErrors @ runtimeErrors @ taskErrors @ (if key = preludeKey then OperatorContexts.ambiguityErrors state else [])
                // A warning doesn't stop the module running or being imported (see `CheckedModule.Warnings`).
                let moduleErrors, moduleWarnings = moduleDiagnostics |> List.partition Diagnostics.isError
                // A message the program writes names a file as a crash's place does: from the package's root, or by its
                // name alone in a script (see `PackageModules.shownFile`).
                let sourceRoot = checkedModules.Package |> Option.map _.Root
                // Each error that can be is compiled to a trap where it was found, for a debug build to run.
                let defer =
                    if moduleErrors.IsEmpty then fun expr -> expr, []
                    else DeferredErrors.deferErrors (DeferredErrors.sitesOf state mismatchErrors sourceRoot)
                let resolvedTypes, resolvedTypeDefs, resolvedItems = resolveTypes subst types state.ContextRequirements state.TypeDefs items
                let own, placed = moduleFromTypedItems defer (StructuralInstances.callTargets state) state.OperatorCalls state.OperatorInstances state.ReadAs state.BuiltInProperties state.WorkflowBlocks resolvedItems
                let own = own |> ModuleLinking.writeBlockAliases state |> ModuleLinking.writeMemberAliases state |> ModuleLinking.writeAttachedNames state |> writeCells state |> writeContextNames state |> nameIgnoredParameters state.ExprTypes subst
                // Each `assert` is written out with its message's source text and values (see `Asserts`).
                let isFunction (node: NodeId) =
                    match state.ExprTypes |> Map.tryFind node |> Option.map (Unifier.apply subst >> Unifier.unalias) with
                    | Some (TyFunc _) -> true
                    | _ -> false
                let own = Asserts.writeOut (normalizeSource source) sourceRoot state isFunction own
                // A script's entry point is a def of its own, with the values that need a context in it (see `Scripts`),
                // which the call of the host's `run` names: typed as the entry point's lambda is.
                let own, resolvedTypes, state =
                    match script with
                    | Some (entry, lambda) ->
                        let entryTy =
                            state.ExprTypes |> Map.tryFind lambda |> Option.map (Unifier.apply subst) |> Option.defaultValue Unifier.errorType
                            |> Scripts.withAmbientContextsOnly (Contexts.ambientContexts state)
                        let own, named = Scripts.asDefinition entry state.ScriptEntryValues entryTy own
                        own, resolvedTypes |> Map.add Scripts.definitionName entryTy, { state with ExprTypes = state.ExprTypes |> Map.add named entryTy }
                    | None -> own, resolvedTypes, state

                // An error is deferred when the diagnostic itself was placed, its place included.
                let placed = Set.ofList placed
                let undeferred = moduleErrors |> List.filter (placed.Contains >> not)
                // The module's own definitions win over imported ones of the same name, a script's values that run in
                // its entry point among them.
                let exported = SyntaxTree.exportedValueNames own
                let ownNames =
                    exported
                    |> Set.union (own |> List.choose (function Kind (Def (ValueDef(false, name, _, _))) -> Some name | _ -> None) |> Set.ofList)
                    |> Set.union state.ScriptEntryValues
                let instances = merged.InlinedInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) state.ContextInstances
                // The module's name, which its component exports' interface and its tests are named by: the name its file
                // declares, or else its file's, or `main` when it has none.
                let moduleName =
                    FileImports.declaredName moduleBlocks desugared
                    |> Option.orElse (Some (System.IO.Path.GetFileNameWithoutExtension(checkedModules.FileName origin)))
                    |> Option.filter (fun name -> ComponentInterface.witName name <> "")
                    |> Option.defaultValue "main"
                let inlined =
                    merged.Inlined @ merged.PreludeInlined
                    |> List.filter (function
                        | Kind (Def (ValueDef(_, name, _, _))) -> not (ownNames.Contains name)
                        | _ -> true)
                    |> ModuleLinking.distinctDefinitions
                    |> ModuleLinking.reachedItems instances own (merged.ComponentExports |> List.map _.Definition)
                // The names the imports and the prelude bring in, as written (`foo`, or `m.foo` under an
                // alias), each meaning the definition the environment settled on (an explicit import's over
                // the prelude's), less the names the module declares at the top level itself.
                let declaredHere = ownTopLevel |> List.map (fun symbol -> symbol.Name) |> Set.ofList
                let imported =
                    env.Names
                    |> Map.toList
                    |> List.choose (fun (name, scheme) ->
                        match scheme.Symbol with
                        | Some id when state.Symbols.ContainsKey id && not (declaredHere.Contains name) -> Some (name, id, scheme.Type)
                        | _ -> None)
                // Only the undotted ones are in scope as names: a dotted one is reached through its prefix.
                let scopes =
                    imported
                    |> List.filter (fun (name, _, _) -> not (name.Contains '.'))
                    |> List.fold (fun acc (_, id, _) -> acc |> Map.add id (Scope.wholeModule (checkedModules.FileName origin))) state.Scopes
                // A def a `module name =` block does not export can only be named inside the block.
                let keptToItsBlock (symbol: Symbol) =
                    not symbol.IsExported
                    && state.ModuleBlocks |> List.exists (fun (_, block) -> SourceSpan.contains block symbol.Declaration)
                let moduleNames =
                    ownTopLevel
                    |> List.filter (keptToItsBlock >> not)
                    |> List.fold (fun acc symbol -> acc |> Map.add symbol.Name symbol.Id) (imported |> List.map (fun (name, id, _) -> name, id) |> Map.ofList)
                Ok { Typed =
                        { Module = inlined @ own
                          // The types of the imported definitions inlined for the backend, then the module's own.
                          Types = resolvedTypes |> Map.fold (fun acc name ty -> acc |> Map.add name ty) merged.InlinedTypes
                          TypeDefs = withDocs origin docComments resolvedTypeDefs
                          Items = lazy resolvedItems
                          // Filled in for the root module only, by `compileChecked`.
                          ExprTypes = Map.empty
                          CalleeTypes = Map.empty
                          SymbolTypes = Map.empty
                          Symbols = state.Symbols
                          AnnotatedSymbols = state.AnnotatedSymbols
                          References = state.References
                          ImportReferences = state.ImportReferences
                          Scopes = scopes
                          ModuleNames = moduleNames
                          ImportedTypes = imported |> List.map (fun (_, id, ty) -> id, ty) |> Map.ofList
                          ContextInstances = state.ContextInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) merged.InlinedInstances
                          ContextProviders = state.ContextProviders
                          UseContexts = ContextInstances.settledUseContexts state subst |> Map.fold (fun acc id context -> acc |> Map.add id context) merged.InlinedUseContexts
                          AssociatedArguments =
                            state.AssociatedArguments
                            |> Map.fold (fun acc id named -> acc |> Map.add id (named |> Map.map (fun _ ty -> Unifier.apply subst ty))) merged.InlinedAssociatedArguments
                          // Resolved for the root module only, by `compileChecked`.
                          InferredContextTypes = state.InferredContextTypes
                          ContextTypes = contextTypes
                          LocalContexts = state.LocalContextRequirements
                          ImportedContextTypes = merged.ImportedContextTypes
                          AmbientContexts = Contexts.ambientContexts state |> Set.toList
                          AmbientMembers = ambientMembers state merged.CarriedAmbientMembers
                          ComponentExports =
                            let calledThroughProvide = provided |> List.map _.Name |> Set.ofList
                            let own =
                                components.Exports
                                |> List.map (fun (name, _) ->
                                    { Module = moduleName
                                      Name = name
                                      Definition = if calledThroughProvide.Contains name then Hosts.providedExportName name else name
                                      Waits =
                                        calledThroughProvide.Contains name
                                        && ownTopLevel
                                           |> List.tryFind (fun symbol -> symbol.Name = name)
                                           |> Option.bind (fun symbol -> contextTypes |> Map.tryFind symbol.Id)
                                           |> Option.exists ComponentInterface.requiresAsync })
                            own @ merged.ComponentExports |> List.distinctBy (fun export -> export.Module, export.Name)
                          Tests =
                            let own = components.Tests |> List.map (fun test -> { Module = moduleName; Name = test.Name; Definition = test.Name; Span = test.Span; Io = test.Io })
                            own @ merged.Tests |> List.distinctBy (fun test -> test.Module, test.Name)
                          ImportedFiles = merged.ImportedFiles
                          // Filled in for the root module only, by `compileChecked`.
                          InlinedExprTypes = Map.empty
                          InlinedCalleeTypes = Map.empty
                          ScriptEntryValues = state.ScriptEntryValues
                          Units = state.Units
                          Declared = state.Declared }
                     Errors = moduleErrors
                     Undeferred = undeferred
                     ImportedErrors = merged.DeferredImportErrors |> List.distinctBy Diagnostics.inItsModule
                     Warnings = moduleWarnings
                     Own = own
                     Inlined = inlined
                     Name = FileImports.declaredName moduleBlocks desugared
                     Syntax = ast
                     DocComments = docComments
                     Lowered = lowered
                     Subst = subst
                     RawExprTypes = state.ExprTypes
                     RawCalleeTypes = state.CalleeTypes
                     RawSymbolTypes = state.SymbolTypes
                     // Only declarations the module made itself are in `DeclarationSymbols`.
                     Exports =
                        state.DeclarationSymbols
                        |> Map.values
                        |> Seq.map (fun id -> state.Symbols.[id])
                        |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue && symbol.IsExported)
                        |> Seq.map (fun symbol -> symbol.Name, symbol)
                        |> Map.ofSeq
                     ContextRequirements = state.ContextRequirements |> Map.filter (fun name _ -> exported.Contains name)
                     ContextSignatures = state.ContextSignatures |> Map.filter (fun name _ -> exported.Contains name)
                     CheckedRests = state.CheckedRests
                     AmbientContexts =
                        Contexts.ambientContexts state
                        |> Set.union state.CarriedAmbientContexts
                        |> Seq.choose (fun name -> state.TypeDefs |> Map.tryFind name |> Option.map (fun info -> name, info))
                        |> Map.ofSeq
                     ReExports = merged.ReExports @ blockModuleReExports
                     BlockReExports = blockReExports |> List.collect snd
                     KeywordKinds =
                        merged.ReExports @ blockModuleReExports
                        |> List.fold (fun kinds reExported -> Attributes.withKeywordKinds reExported.Module.KeywordKinds kinds) Map.empty
                        |> Attributes.withKeywordKinds ownKeywordKinds }

    /// The module `item` imports into the module `origin` (whose `originKey` is `key`), checked, with where it is from;
    /// or why it cannot be imported. `hostModules` are the build's host package's modules, and `inHostPackage` says
    /// whether a file is of that package or one it depends on.
    and private checkImportItem (checkedModules: CheckedModules) (origin: ModuleOrigin) (key: string) (hostModules: PackageModules option) (inHostPackage: string -> bool) (visited: Set<string>) (item: ImportItem) : Result<ModuleOrigin * CheckedModule, Diagnostic list> =
        // An import of a module that is still being checked, because it imports this one, is a cycle.
        let checkImport (importOrigin: ModuleOrigin) (readSource: unit -> string) =
            let importKey = originKey importOrigin
            if importKey = key || visited.Contains importKey then
                Error [ Diagnostics.at DiagnosticCodes.ImportCycle item.Span $"'{importKey}' imports this module, directly or through others, so the imports make a cycle" |> Diagnostics.about importKey ]
            else
                // The imported module's errors name this import as the one that brings them in, in place
                // of the one in that module that brought them into it.
                let throughThis = List.map (fun (diagnostic: Diagnostic) -> { diagnostic with Import = Some item.Span })
                match typecheckImport checkedModules importOrigin readSource (visited.Add key) with
                // A module with an error that can't be deferred fails each importer with its errors. One whose errors
                // are all compiled to traps is imported, and its errors come with it (see `CheckedModule.ImportedErrors`).
                | Ok imported when not imported.Undeferred.IsEmpty -> Error (throughThis imported.Errors)
                | Error (_, diagnostics) -> Error (throughThis diagnostics)
                | Ok imported -> Ok (importOrigin, imported)
        // The import of the host is of its package's modules, and an import in a file of the host's package, or
        // of a package it depends on, is resolved in that package.
        let inHostPackage =
            match hostModules, item.Source, origin with
            | Some modules, ModuleImport name, _ when name.StartsWith(modules.Package + "/") -> Some (PackageModules.resolveWithin modules item name)
            | Some modules, ModuleImport _, SourceFile path when inHostPackage path && StandardLibrary.tryModuleName item = None ->
                Some (PackageModules.resolveImport (Some modules) path item)
            | _ -> None
        match StandardLibrary.tryModuleName item, inHostPackage with
        | None, Some resolved ->
            match resolved with
            | Error diag -> Error [ diag ]
            | Ok importPath -> checkImport (SourceFile importPath) (fun () -> checkedModules.ReadFile importPath)
        | Some moduleName, _ ->
            match StandardLibrary.tryLoadSource moduleName with
            | None ->
                let available = Diagnostics.listed (StandardLibrary.moduleNames |> List.map (fun name -> $"'{StandardLibrary.importPrefix}{name}'"))
                Error [ Diagnostics.at DiagnosticCodes.UnknownStandardLibraryModule item.Span $"The modules are {available}" |> Diagnostics.about $"{StandardLibrary.importPrefix}{moduleName}" ]
            | Some moduleSource ->
                match checkImport (StandardLibraryModule moduleName) (fun () -> moduleSource) with
                | Ok (_, imported) when imported.Name <> Some moduleName ->
                    let declared = imported.Name |> Option.defaultValue "(none)"
                    Error [ Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule item.Span $"It declares the module name '{declared}'" |> Diagnostics.about $"{StandardLibrary.importPrefix}{moduleName}" ]
                | checkedImport -> checkedImport
        | None, None ->
            match origin with
            | SourceFile fullPath ->
                match FileImports.missingAlias item, PackageModules.resolveImport checkedModules.Package fullPath item with
                | Some diag, _
                | None, Error diag -> Error [ diag ]
                | None, Ok importPath ->
                    match checkImport (SourceFile importPath) (fun () -> checkedModules.ReadFile importPath) with
                    | Ok (_, imported) as checkedImport ->
                        // A test build's root imports a script to run its tests, whose statements a test build leaves out.
                        let isTestRoot = checkedModules.Package |> Option.bind _.TestRoot |> Option.exists (PackageModules.samePath fullPath)
                        match FileImports.importedFile item imported.Name imported.Syntax |> List.filter (fun diag -> not (isTestRoot && diag.Code = DiagnosticCodes.EntrypointImported.Code)) with
                        | [] -> checkedImport
                        | diags -> Error diags
                    | failed -> failed
            | StandardLibraryModule name ->
                Error [ Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule item.Span "It imports a module outside the standard library: import only other standard library modules" |> Diagnostics.about name ]
            | InMemory ->
                let shown =
                    match item.Source with
                    | PathImport path -> path
                    | ModuleImport name -> name
                let message =
                    match item.Source with
                    | ModuleImport name -> RenamedPackages.inImport name
                    | PathImport _ -> None
                    |> Option.defaultValue "Source that has no file can import only standard library modules"
                Error [ Diagnostics.at DiagnosticCodes.ImportNotFound item.Span message |> Diagnostics.about shown ]

    /// An imported module, checked the first time this compilation imports it; its source is only read then. A
    /// standard-library module is checked once per process (see `checkedStandardLibrary`), and a source file's module
    /// again only when a file its check read has changed (see `checkedSourceFiles`); the modules either imports come into
    /// this compilation with it. A module already checked is reported to the listener as reused.
    and private typecheckImport (checkedModules: CheckedModules) (origin: ModuleOrigin) (readSource: unit -> string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        match checkedModules.TryGetChecked key with
        | true, checkedModule ->
            checkedModules.Listener.ModuleReused key
            checkedModule
        | false, _ ->
            let checkedModule =
                match origin with
                | StandardLibraryModule _ ->
                    let source = readSource ()
                    // The checked library has the module, unless its source has changed since (see `CheckedLibrary`): a
                    // module read from it is reused, as one this process checked already is.
                    let check _ =
                        match CheckedLibrary.tryFind key source with
                        | Some checkedModule ->
                            let read = lazy checkedModule
                            read.Force() |> ignore
                            read
                        | None ->
                            lazy
                                (let own = CheckedModules(None, None, checkedModules.Listener)
                                 let checkedModule = typecheckModule own origin source visited
                                 checkedModule, own.Imported)
                    let checking = checkedStandardLibrary.GetOrAdd((key, source), check)
                    if checking.IsValueCreated then checkedModules.Listener.ModuleReused key
                    let checkedModule, imports = checking.Value
                    for importKey, imported in imports do
                        checkedModules.AddImported(importKey, imported)
                    checkedModule
                | SourceFile path when checkedModules.Edited.IsNone -> typecheckSourceFile checkedModules origin path visited
                | SourceFile _
                | InMemory -> typecheckModule checkedModules origin (readSource ()) visited
            checkedModules.[key] <- checkedModule
            checkedModule

    /// The module of the source file at `path`, imported from `origin`: as this process last checked it, if every file
    /// that check read reads the same now; else, for a file of the build's host package or a package it depends on, as
    /// the checked library has it (see `CheckedLibrary`), under the same condition, which is how the writer checked it;
    /// and else checked again on its own, recording what it reads.
    and private typecheckSourceFile (checkedModules: CheckedModules) (origin: ModuleOrigin) (path: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        match fromCheckedPackage checkedModules key path visited with
        | Some checkedModule -> checkedModule
        | None -> typecheckSourceFileAgain checkedModules key path visited

    /// The module of the file at `path`, of a package in the package cache, as the package's checked file has it, written
    /// first if there is none (see `CheckedPackages`), with the package's other modules; none for a file of any other
    /// package, of the package this compilation writes the checked file of, or of one whose checked file can't be used
    /// in this compilation, because a module its checks imported is here as another checked file has it, or as this
    /// process checked it itself.
    and private fromCheckedPackage (checkedModules: CheckedModules) (key: string) (path: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> option =
        let owner =
            [ checkedModules.Package; snd (buildHostOf checkedModules); testHostModulesOf checkedModules ]
            |> List.choose id
            |> List.tryPick (fun modules -> PackageModules.ownerOf modules path)
        match owner with
        | Some({ CheckedFile = Some file } as owner) when checkedModules.Writing <> Some file ->
            let package =
                checkedPackages.GetOrAdd(file, fun file ->
                    lazy (CheckedPackages.tryRead file owner |> Option.orElseWith (fun () -> writeCheckedPackage checkedModules.Listener owner file)))
            match package.Value with
            | Some package when package.Checked.ContainsKey key && importsMatch checkedModules package visited ->
                checkedModules.Listener.ModuleReused key
                for KeyValue(other, result) in package.Checked do
                    if other <> key then checkedModules.AddImported(other, result)
                Some package.Checked[key].Value
            | _ -> None
        | _ -> None

    /// Whether the modules the checks of `package` imported are here as the checked files it was written against have
    /// them, each imported now if it isn't yet.
    and private importsMatch (checkedModules: CheckedModules) (package: CheckedPackage) (visited: Set<string>) : bool =
        package.File.Imports
        |> List.forall (fun (importKey, token) ->
            let imported =
                if importKey.StartsWith(StandardLibrary.importPrefix, StringComparison.Ordinal) then
                    let name = importKey.Substring StandardLibrary.importPrefix.Length
                    typecheckImport checkedModules (StandardLibraryModule name) (fun () -> StandardLibrary.tryLoadSource name |> Option.defaultValue "") visited
                else
                    typecheckImport checkedModules (SourceFile importKey) (fun () -> checkedModules.ReadFile importKey) visited
            match imported with
            | Ok checkedModule -> CheckedFileOrigins.tryFind checkedModule = Some token
            | Error _ -> false)

    /// Checks every module of `owner`, a package in the package cache, from its source, and writes them to its checked
    /// file, `file`; gives the file as a later compilation reads it, or none if the package has errors or the file can't
    /// be written (see `CheckedPackages.write`). The modules of other packages its modules import are read from their
    /// own checked files, written first if need be, so that this one can name them.
    and private writeCheckedPackage (listener: CompileListener) (owner: PackageModules) (file: string) : CheckedPackage option =
        let own = CheckedModules(Some owner, None, listener, File.ReadAllText, Writing = Some file)
        let first = CheckedPackages.startIds ()
        let checkedFiles =
            [ for path in owner.Modules |> Map.values |> Seq.choose List.tryHead |> Seq.distinct ->
                  originKey (SourceFile path), typecheckImport own (SourceFile path) (fun () -> own.ReadFile path) Set.empty ]
        let last = CheckedPackages.lastId ()
        let ownKeys = checkedFiles |> List.map fst |> Set.ofList
        let imports =
            [ for importKey, imported in own.Imported do
                  if not (ownKeys.Contains importKey) then
                      importKey, (match imported.Value with Ok checkedModule -> CheckedFileOrigins.tryFind checkedModule | Error _ -> None) ]
        let clean = checkedFiles |> List.forall (fun (_, result) -> match result with Ok checkedModule -> checkedModule.Errors.IsEmpty | Error _ -> false)
        if clean && imports |> List.forall (snd >> Option.isSome) then
            CheckedPackages.write file owner (first, last) checkedFiles (imports |> List.map (fun (importKey, token) -> importKey, token.Value))
        else
            None

    /// The module of the source file at `path`, as `typecheckSourceFile` finds it when no checked file of a package has
    /// it.
    and private typecheckSourceFileAgain (checkedModules: CheckedModules) (key: string) (path: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let origin = SourceFile path
        // A package checked to write its checked file is checked from its source, and isn't kept: its ids are moved.
        let writing = checkedModules.Writing.IsSome
        let readsTheSame (file: string, text: string) =
            try checkedModules.ReadFile file = text with _ -> false
        let shipped () =
            match [ snd (buildHostOf checkedModules); testHostModulesOf checkedModules ] |> List.choose id with
            | hostPackages when hostPackages |> List.exists (fun hostModules -> (PackageModules.ownerOf hostModules path).IsSome) ->
                CheckedLibrary.tryFindShipped key checkedModules.ReadFile
                |> Option.map (fun shipped ->
                    let kept = { Package = checkedModules.Package; Read = shipped.Read; Module = shipped.Module; Imports = shipped.Imports }
                    checkedSourceFiles.[key] <- kept
                    kept)
            | _ -> None
        let kept =
            match checkedSourceFiles.TryGetValue key with
            | _ when writing -> None
            | true, kept when kept.Package = checkedModules.Package && List.forall readsTheSame kept.Read -> Some kept
            | _ -> shipped ()
        match kept with
        | Some kept ->
            checkedModules.Listener.ModuleReused key
            for importKey, imported in kept.Imports do
                checkedModules.AddImported(importKey, imported)
            Ok kept.Module
        | None ->
            let read = ResizeArray<string * string>()
            let reading (file: string) =
                let text = checkedModules.ReadFile file
                read.Add((file, text))
                text
            let own = CheckedModules(checkedModules.Package, None, checkedModules.Listener, reading, Writing = checkedModules.Writing)
            let checkedModule = typecheckModule own origin (reading path) visited
            let imports = own.Imported
            for importKey, imported in imports do
                checkedModules.AddImported(importKey, imported)
            match checkedModule with
            | _ when writing -> ()
            | Ok checked when checked.Errors.IsEmpty ->
                checkedSourceFiles.[key] <- { Package = checkedModules.Package; Read = List.ofSeq read; Module = checked; Imports = imports }
            | _ -> checkedSourceFiles.TryRemove key |> ignore
            checkedModule

    /// Checks every module of the standard library, and of the console host's package and the packages it depends on,
    /// as an import of it from a loose script does, and writes them to the file at `path` (see `CheckedLibrary`), with the
    /// library's exports and the module each file of those packages declares. Only the
    /// build of the compiler does, in a process of its own, before it checks anything else: the ids of what it checks
    /// start at `CheckedLibrary.firstId`.
    let writeCheckedLibrary (path: string) =
        CheckedLibrary.reading <- false
        NodeId.startAt CheckedLibrary.firstId
        SymbolId.startAt CheckedLibrary.firstId
        Nanyx.LargeStack.run (fun () ->
            let checkedModules = CheckedModules(None)
            for name in StandardLibrary.preludeModuleName :: StandardLibrary.moduleNames do
                let source = StandardLibrary.tryLoadSource name |> Option.defaultValue ""
                typecheckImport checkedModules (StandardLibraryModule name) (fun () -> source) Set.empty |> ignore
            let rec packages (modules: PackageModules) = modules :: List.collect packages modules.Dependencies
            let shippedPackages = buildHostOf checkedModules |> snd |> Option.map packages |> Option.defaultValue []
            let shippedFiles =
                shippedPackages
                // A module in several files is checked from its first, which brings in the others.
                |> List.collect (fun modules -> modules.Modules |> Map.values |> Seq.choose List.tryHead |> List.ofSeq)
                |> List.distinct
            for file in shippedFiles do
                typecheckImport checkedModules (SourceFile file) (fun () -> checkedModules.ReadFile file) Set.empty |> ignore
            let shipped =
                [ for file in shippedFiles do
                      match checkedSourceFiles.TryGetValue(originKey (SourceFile file)) with
                      | true, kept -> { CheckedShippedModule.Key = originKey (SourceFile file); Read = kept.Read; Module = kept.Module; Imports = kept.Imports }
                      | false, _ -> () ]
            let declarations =
                [ for file in shippedPackages |> List.collect _.Files |> List.distinct do
                      match PackageModules.declaration file with
                      | Some declares -> { CheckedShippedFile.Path = file; Text = File.ReadAllText file; Declares = declares }
                      | None -> () ]
            CheckedLibrary.write
                path
                [ for KeyValue((key, source), checking) in checkedStandardLibrary -> key, source, checking.Value ]
                shipped
                ImportFixes.standardLibraryExports.Value
                declarations)

    let parse (source: string) : Result<Module, Diagnostic list> =
        match parseModuleFileWithNames "" source with
        | Result.Ok (ast, _, _) -> Ok ast
        | Result.Error err -> Error (syntaxErrors "" source err)

    /// The compile of the module from `origin`, which `typecheckModule` gave as `checked` with `checkedModules`, the linters
    /// `options` names run over it if it type-checked. A module with type or name errors is typed all the same, with what did
    /// check, beside its errors. Only the module being compiled is analyzed: an imported module is analyzed when it is
    /// compiled itself, so its diagnostics are not repeated in every module that imports it, and the standard library
    /// never is (but by tests).
    let internal compileChecked (checkedModules: CheckedModules) (options: LinterOptions) (origin: ModuleOrigin) (source: string) checked : CompileResult =
        match checked with
        | Result.Error (phase, diagnostics) ->
            { Phase = phase
              Diagnostics = diagnostics
              Typed = None
              TypeChecked = false
              ErrorsDeferred = false
              Syntax = None
              ModuleName = None
              Lowered = Map.empty
              DocComments = lazy DocComments.none }
        | Result.Ok checkedModule ->
            // Types by node are only read for the root module, so only its are resolved: not those of every
            // imported module (the standard library included) on every compile.
            let resolve = Map.map (fun _ ty -> Unifier.applySolved checkedModule.Subst ty)
            // Nor are an imported module's resolved but for the nodes of its definitions that are inlined here, each by the
            // substitution of the module it came from.
            let inlinedNodes = System.Collections.Generic.HashSet<NodeId>()
            for item in checkedModule.Inlined do
                match item.Kind with
                | Def (ValueDef(_, _, _, expr)) ->
                    expr
                    |> mapExpressionKinds (fun node kind ->
                        inlinedNodes.Add node.Id |> ignore
                        // An arm of a match on a `Type` has the type it narrows `a` to at its pattern (see `TypeMatches`).
                        match kind with
                        | Match(_, arms) -> for patterns, _ in arms do for pattern in patterns do inlinedNodes.Add pattern.Id |> ignore
                        | _ -> ()
                        kind)
                    |> ignore
                | _ -> ()
            // Each module the compilation has is read only if a node inlined here is not in those before it.
            let imported = checkedModules.Imported |> List.map snd
            let resolveInlined (raw: CheckedModule -> Map<NodeId, Ty>) =
                inlinedNodes
                |> Seq.choose (fun id ->
                    imported
                    |> List.tryPick (fun read ->
                        match read.Value with
                        | Result.Ok m -> raw m |> Map.tryFind id |> Option.map (fun ty -> id, Unifier.apply m.Subst ty)
                        | Result.Error _ -> None))
                |> Map.ofSeq
            let typed =
                { checkedModule.Typed with
                    ExprTypes = resolve checkedModule.RawExprTypes
                    CalleeTypes = resolve checkedModule.RawCalleeTypes
                    SymbolTypes = resolve checkedModule.RawSymbolTypes
                    InferredContextTypes =
                        checkedModule.Typed.InferredContextTypes |> Map.map (fun _ tys -> tys |> List.map (Unifier.applySolved checkedModule.Subst))
                    ContextTypes = checkedModule.Typed.ContextTypes |> Map.map (fun _ tys -> tys |> List.map (Unifier.applySolved checkedModule.Subst))
                    InlinedExprTypes = resolveInlined _.RawExprTypes
                    InlinedCalleeTypes = resolveInlined _.RawCalleeTypes }
            // Callers that stop at errors (`nanyx run`, say) look at the diagnostics, and the typed module is still
            // there for the editor's features. The linters only look at a module that type-checked; a rule
            // configured as an error fails the compile as a compiler error does.
            { Phase = Typed
              Diagnostics =
                // A module with syntax errors is reported by those alone: what the items left out of it would have
                // declared is missing, and the errors that follow from aren't the user's to fix.
                let syntaxErrors = checkedModule.Errors |> List.filter isSyntaxError
                // An imported module's errors are reported at the import that brings them in, as they are where that
                // module can't be imported, and the linters wait for them as for the module's own; they are deferred
                // (#2967).
                let imported = checkedModule.ImportedErrors
                if not syntaxErrors.IsEmpty then syntaxErrors
                elif checkedModule.Errors.IsEmpty && imported.IsEmpty then
                    checkedModule.Warnings
                    @ Linters.run options (SemanticModel.ofTyped (checkedModules.FileName origin) source checkedModule.Syntax checkedModule.Lowered checkedModule.DocComments typed)
                else
                    // A name another module of the package, or of a package it depends on, exports gets its import too.
                    let packageExports =
                        match checkedModules.Package, origin with
                        | Some package, SourceFile path when PackageModules.contains package path -> PackageExports.importable package checkedModules.ReadFile path
                        | _ -> []
                    imported
                    @ (ImportFixes.addFrom packageExports CheckedLibrary.standardLibraryExportPaths.Value (normalizeSource source) checkedModule.Syntax checkedModule.Errors
                       |> MatchArmFixes.place (normalizeSource source) checkedModule.Syntax)
                    @ checkedModule.Warnings
              Typed = Some typed
              TypeChecked = checkedModule.Errors.IsEmpty && checkedModule.ImportedErrors.IsEmpty
              ErrorsDeferred = checkedModule.Undeferred.IsEmpty
              Syntax = Some checkedModule.Syntax
              ModuleName = checkedModule.Name
              Lowered = checkedModule.Lowered
              DocComments = checkedModule.DocComments }

    /// The module from `origin`, in no package, checked and compiled (see `compileChecked`), with each module's check
    /// reported to `listener`, and the other source files it imports read by `readFile`, given each one's full path.
    let compileFromReporting (listener: CompileListener) (readFile: string -> string) (options: LinterOptions) (origin: ModuleOrigin) (source: string) : CompileResult =
        let checkedModules = CheckedModules(None, None, listener, readFile)
        Nanyx.LargeStack.run (fun () -> compileChecked checkedModules options origin source (typecheckModule checkedModules origin source Set.empty))

    /// `compileFromReporting`, telling nobody, with the files it imports read from disk.
    let compileFrom (options: LinterOptions) (origin: ModuleOrigin) (source: string) : CompileResult =
        compileFromReporting CompileListener.none File.ReadAllText options origin source

    /// The compile of the standard-library module `moduleName` as an import of it checks it (see `typecheckImport`):
    /// from the checked library when its source is the same, else as this process checked it, once. So the tools that
    /// read the library's modules, its reference and its search, check none of them again. Each module's check, or
    /// its reuse, is reported to `listener`.
    let compileStandardLibraryModuleReporting (listener: CompileListener) (moduleName: string) : CompileResult =
        let origin = StandardLibraryModule moduleName
        let source = (StandardLibrary.tryLoadSource moduleName).Value
        let checkedModules = CheckedModules(None, None, listener, File.ReadAllText)
        Nanyx.LargeStack.run (fun () ->
            compileChecked checkedModules Linters.none origin source (typecheckImport checkedModules origin (fun () -> source) Set.empty))

    /// `compileSourceWith` for a source file of the package `package`, whose imports name its modules, with each
    /// module's check reported to `listener`, and the package's other files it needs read by `readFile`.
    let compileSourceInReporting (listener: CompileListener) (readFile: string -> string) (package: PackageModules) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        let origin, checkedModules = SourceFile (Path.GetFullPath filePath), CheckedModules(Some package, None, listener, readFile)
        Nanyx.LargeStack.run (fun () -> compileChecked checkedModules options origin source (typecheckModule checkedModules origin source Set.empty))

    /// `compileSourceInReporting`, telling nobody, with the package's files read from disk.
    let compileSourceIn (package: PackageModules) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileSourceInReporting CompileListener.none File.ReadAllText package options filePath source

    /// `compileFile` with the linters and severities `options` gives.
    let compileFileWith (options: LinterOptions) (filePath: string) : CompileResult =
        if not (File.Exists filePath) then
            { Phase = Parsed
              Diagnostics = [ Diagnostics.general DiagnosticCodes.FileNotFound "There is no file at this path" |> Diagnostics.about filePath ]
              Typed = None
              TypeChecked = false
              ErrorsDeferred = false
              Syntax = None
              ModuleName = None
              Lowered = Map.empty
              DocComments = lazy DocComments.none }
        else
            compileFrom options (SourceFile (Path.GetFullPath filePath)) (File.ReadAllText filePath)

    /// Compiles the file at `filePath` up to the typed module, resolving its imports (standard-library modules
    /// from the embedded library, everything else relative to the file). It runs no linters: `compileFileWith` runs those it is given.
    let compileFile (filePath: string) : CompileResult =
        compileFileWith Linters.none filePath

    /// `compileSource` with the linters and severities `options` gives.
    let compileSourceWith (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileFrom options (SourceFile (Path.GetFullPath filePath)) source

    /// Compiles `source`, the editor's text of the standard-library file at `filePath` (`stdlib/nanyx/text.nyx` in a
    /// checkout, or the copy the editor opens a definition in), as the library module it is a file of, as a build of the
    /// library checks it: with the intrinsics in scope, without importing the prelude if it is the prelude, and with the
    /// module's other files if it is split across files. Its syntax nodes are reported in `filePath`. None if the file is
    /// not one of the library's (see `StandardLibrary.isModuleFile`). Each module's check is reported to `listener`.
    let compileStandardLibraryFileReporting (listener: CompileListener) (options: LinterOptions) (filePath: string) (source: string) : CompileResult option =
        let resource = StandardLibrary.importPrefix + Path.GetFileName filePath
        match StandardLibrary.tryModuleOfFile resource with
        | Some moduleName when StandardLibrary.isModuleFile filePath ->
            let origin = StandardLibraryModule moduleName
            let checkedModules = CheckedModules(None, Some { Module = moduleName; Resource = resource; Path = Path.GetFullPath filePath }, listener)
            Some (Nanyx.LargeStack.run (fun () -> compileChecked checkedModules options origin source (typecheckModule checkedModules origin source Set.empty)))
        | _ -> None

    /// `compileStandardLibraryFileReporting`, telling nobody.
    let compileStandardLibraryFile (options: LinterOptions) (filePath: string) (source: string) : CompileResult option =
        compileStandardLibraryFileReporting CompileListener.none options filePath source

    /// Compiles `source` as if it were the contents of the file at `filePath` (which need not exist, or may hold
    /// older text: an editor's unsaved buffer, say). Imports are resolved exactly as `compileFile` resolves them.
    let compileSource (filePath: string) (source: string) : CompileResult =
        compileSourceWith Linters.none filePath source

    /// `compileWith`, with each module's check reported to `listener`.
    let compileReporting (listener: CompileListener) (options: LinterOptions) (source: string) : CompileResult =
        compileFromReporting listener File.ReadAllText options InMemory source

    /// `compile` with the linters and severities `options` gives.
    let compileWith (options: LinterOptions) (source: string) : CompileResult =
        compileFrom options InMemory source

    /// Compiles source given directly, up to the typed module, without linters. Standard-library
    /// imports are resolved; imports by path cannot be, since there is no file for them to be relative to.
    let compile (source: string) : CompileResult =
        compileWith Linters.none source
