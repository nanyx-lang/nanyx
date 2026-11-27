namespace NyxCompiler

open Parser.Program

/// The root module a host builds a WASI 0.3 component from, beside the root it runs core modules from, and the world it
/// is written against (see the Components page of the docs).
type HostComponent =
    { /// The component root, which exports `run` as the core root does: `cli.host.wasi`.
      Root: string
      /// The world, as WIT names it: `wasi:cli/command@0.3.0`. Its WIT is the package in the host's `wit` directory, with
      /// the packages it uses under `wit/deps`.
      World: string
      /// The world a library component, a package with `@component` defs and no `@entry`, is built in, if the host
      /// has one: `nanyx:console/library`, which imports what `World` does and exports nothing.
      Library: string option
      /// The host's core root, beside which the component root is built: its `run` provides every context the
      /// component root's does (see `Hosts.componentRootErrors`).
      CoreRoot: string }

/// A host: a package whose manifest declares that it is one (see `HostPackages`), with the module that runs the
/// program's entry point, under `use`s of the contexts it provides, and the prelude that brings what it provides into
/// scope in every module of the program (see the Hosts page of the docs). The program's `@entry` is passed to the
/// host's `run`, which is the module's start.
type Host =
    { /// The package a manifest's `host` names it by: `nanyx.cli.host`.
      Package: string
      /// The host's root module, which exports `run`: `cli.host`.
      Module: string
      /// The host's prelude, if it has one: a module of the package whose exports every module of the program sees
      /// without importing them, as `println` and `Console`.
      Prelude: string option
      /// The directory of the host's package: beside the compiler for one it ships (see `ShippedPackages`).
      Directory: string
      /// The settings it takes, each with its default: a program's manifest may set them in its `host`'s `settings`,
      /// and may set no others. The HTTP host's `port` is one.
      Settings: Parser.Program.DataValues.DataField list
      /// The root it builds a component from, if it has one. A host whose `Module` is this root is the host as a
      /// component build sees it (see `asComponent`).
      Component: HostComponent option
      /// The root whose `run` runs the entry point with its files in memory, over the real ones, if it has one: what
      /// `nanyx run --files-in-memory` builds a program from (see `InMemory`).
      FilesInMemory: string option }

    /// The host's root module as an import names it: `nanyx.cli.host/cli.host`.
    member host.Import = $"{host.Package}/{host.Module}"

    /// The host's prelude as an import names it, if it has one: `nanyx.cli.host/cli.prelude`.
    member host.PreludeImport = host.Prelude |> Option.map (fun prelude -> $"{host.Package}/{prelude}")

    /// The host as a component build sees it, if it has a component root: its `run` is the component root's.
    member host.AsComponent = host.Component |> Option.map (fun built -> { host with Module = built.Root })

    /// The host as `nanyx run --files-in-memory` sees it, if it has a root for that: its `run` is that root's, which keeps
    /// what the program writes in memory. A program built without it is built from the host's own root, unchanged.
    member host.InMemory = host.FilesInMemory |> Option.map (fun root -> { host with Module = root })

    /// Whether this is the host as a component build sees it (see `AsComponent`).
    member host.IsComponent = host.Component |> Option.exists (fun built -> built.Root = host.Module)

    /// The directory of the WIT the host's component root is written against.
    member host.WitDirectory = System.IO.Path.Combine(host.Directory, "wit")
/// A definition marked `@entry`, and the call of its host's `run` with it that `Attributes.applyAttributes` made up.
type EntryPoint =
    { /// The node of the `@entry` attribute.
      Attribute: NodeId
      /// The name of the definition.
      Name: string
      Host: Host
      /// Where the entry point is passed to `run`: the attribute's span, which nothing else is checked at.
      Argument: SourceSpan
      /// The nodes of the call.
      Call: NodeId list }

module Hosts =
    /// The function each host exports that takes the entry point and runs it.
    let runFunction = "run"

    /// The function each host's root may export that calls a function under the contexts it provides, and gives back
    /// what it returns: a component's export that requires contexts is called through it (see `providedExportName`).
    let provideFunction = "provide"

    /// The def `Attributes.applyAttributes` makes up for the export marked `@component` named `name` whose type
    /// requires contexts, which calls it through the host's `provide`, and which the component exports in its place.
    let providedExportName (name: string) = $"__component_{name}"

    /// The alias the module that declares the entry point imports the host under, which no name written in Nanyx can
    /// clash with.
    let importAlias = "__host"

    /// The standard-library modules a host is written on, whose functions all require `HostRuntime`: the one that
    /// declares it, `nanyx/host.memory` and `nanyx/host.async`.
    let private runtimeModules = [ ModuleOrigin.hostRuntimeName; "host.memory"; "host.async" ]

    /// What the name of every host package ends in, `nanyx.cli.host`: the library a host runs programs against takes the
    /// name without it, `nanyx.cli`. A manifest is checked for it (`NYX0053`).
    let nameSuffix = ".host"

    /// The package of the console host the compiler ships: what a script runs under, unless it names another, and what
    /// `nanyx new` names.
    let consolePackage = "nanyx.cli.host"

    /// The package of the playground host the compiler ships, a stand-in for the console host in a web page: what a
    /// script runs under in the compiler built for the browser (`Nanyx.Browser`), unless it names another.
    let playgroundPackage = "nanyx.playground.host"

    /// The package of the test host the compiler ships, which a test build runs one test of a program's under (see
    /// `Package.testBuild`).
    let testPackage = "nanyx.test.host"

    /// The contexts the test host provides a test, by name: what its `run` passes a test build's entry point. A test
    /// may require only these (see `TestChecks`). `Async` is the host's own, so a test waits as a program does, and can
    /// call code that reads files through a `FileSystem` it loads itself.
    let testContexts = [ "Async"; "Console"; "Random" ]

    /// The contexts the test host provides a test marked `@test(#io)`: the console host's real `Console`, `Env` and
    /// `FileSystem`, with `Async` and the seeded `Random`, as every test has them (see `Attributes.ioTestArgument`).
    let ioTestContexts = [ "Async"; "Console"; "Env"; "FileSystem"; "Random" ]

    /// The module of the foreign functions a component's core module defines itself for its host's component root,
    /// reserved for it: `argument` and `result`, through which a `wasi:http/service` component's root takes the request
    /// it answers and gives the response (see the Components page of the docs). Only a host's package, or a host library
    /// it depends on, may declare one (see `reservedExternErrors`).
    let componentModule = "nanyx:component"

    /// The context a host is written on: raw reads and writes, which only a host may require.
    let runtimeContext = "HostRuntime"

    /// Whether the module `origin` may require `HostRuntime`: a module of the build host's own package, or of a host
    /// library it depends on, whose files `isHost` tells apart, or a module of the standard library a host is written on
    /// (see `runtimeModules`).
    let mayRequireRuntime (isHost: string -> bool) (origin: ModuleOrigin) =
        match origin with
        | StandardLibraryModule name -> List.contains name runtimeModules
        | SourceFile path -> isHost path
        | InMemory -> false

    /// The names of the contexts `contexts`, as the module names them, with the type variables among them left out.
    let private contextNames (contexts: Ty list) =
        contexts |> List.filter (function TyVar _ -> false | _ -> true) |> Unifier.tysToDisplayStrings Source

    /// `names`, each in quotes, joined by commas: `'Console', 'Random'`.
    let private quoted (names: string list) = names |> List.map (fun name -> $"'{name}'") |> String.concat ", "

    /// The error for an entry point that does not fit its host's `run`: `found` is the entry point's type, `required`
    /// the contexts it requires, and `expected` the type of `run`'s parameter, where they are known. It names each context
    /// the entry point requires that `run` doesn't provide.
    let entryMismatch (entry: EntryPoint) (found: Ty option) (required: Ty list) (expected: Ty option) =
        let unknown = TyPrimitive "?error"
        // The entry point's type with the contexts it requires, which its type in the module leaves to the requirements
        // recorded beside it: a context the host doesn't provide is why it doesn't fit (#2163).
        let withRequired =
            found
            |> Option.map (fun ty ->
                match Unifier.unalias ty with
                | TyFunc(input, output, contexts) -> TyFunc(input, output, contexts @ required |> List.distinct)
                | _ -> ty)
        // Shown together, so that the variables are named alike in both.
        let shown = Unifier.tysToDisplayStrings Source [ defaultArg expected unknown; defaultArg withRequired unknown ]
        // The contexts the entry point requires that the host's `run` doesn't provide, by name.
        let contextsOf (ty: Ty option) =
            match ty |> Option.map Unifier.unalias with
            | Some(TyFunc(_, _, contexts)) -> contexts
            | _ -> []
        let provided = contextNames (contextsOf expected) |> Set.ofList
        let missing = contextNames (contextsOf found @ required) |> List.distinct |> List.filter (provided.Contains >> not)
        let missingLines =
            if missing.IsEmpty then ""
            else $"\n  it doesn't provide {quoted missing}"
        Diagnostics.at
            DiagnosticCodes.EntryDoesNotFitHost
            entry.Argument
            $"It doesn't fit the host '{entry.Host.Import}'\n  expected: {shown.[0]}\n  found:    {shown.[1]}{missingLines}"
        |> Diagnostics.about entry.Name

    /// The error for an export marked `@component`, `export`, that requires contexts its host's root doesn't provide:
    /// `required` is the contexts it requires, and `provides` the type of the parameter of the root's `provide`, if it
    /// has one.
    let exportMismatch (export: EntryPoint) (required: Ty list) (provides: Ty option) =
        let provided =
            match provides |> Option.map Unifier.unalias with
            | Some(TyFunc(_, _, contexts)) -> contextNames contexts |> Set.ofList
            | _ -> Set.empty
        let missing = contextNames required |> List.distinct |> List.filter (provided.Contains >> not)
        let said = if missing.IsEmpty then "contexts" else quoted missing
        Diagnostics.at
            DiagnosticCodes.EntryDoesNotFitHost
            export.Argument
            $"'{export.Name}' requires {said}, which the host '{export.Host.Import}' doesn't provide: a component's export runs under the contexts its host's root provides"
        |> Diagnostics.about export.Name

    /// The error for an export marked `@component`, `export`, that requires contexts, under `host`, whose root has no
    /// `provide` to run it under them.
    let unprovidedExport (host: Host) (export: EntryPoint) =
        Diagnostics.at
            DiagnosticCodes.EntryDoesNotFitHost
            export.Argument
            $"'{export.Name}' requires contexts, and the host '{host.Import}' provides none to a component's exports: its root has no '{provideFunction}'"
        |> Diagnostics.about export.Name

    /// The errors for a host's component root whose `run`, of type `componentRun`, declared at `span`, doesn't provide
    /// the contexts its core root's `run`, of type `coreRun`, does: one more, or one fewer. A program that runs under the
    /// one would not run under the other.
    let componentRootErrors (host: Host) (span: SourceSpan) (componentRun: Ty) (coreRun: Ty) =
        let provided (run: Ty) =
            match Unifier.unalias run with
            | TyFunc(parameter, _, _) ->
                match Unifier.unalias parameter with
                | TyFunc(_, _, contexts) -> contextNames contexts
                | _ -> []
            | _ -> []
        let core = provided coreRun
        let built = provided componentRun
        let root = host.Component |> Option.map _.CoreRoot |> Option.defaultValue host.Module
        let differences =
            [ match built |> List.filter (fun name -> not (List.contains name core)) with
              | [] -> ()
              | extra -> $"provides {quoted extra}, which the core root '{host.Package}/{root}' doesn't"
              match core |> List.filter (fun name -> not (List.contains name built)) with
              | [] -> ()
              | missing -> $"doesn't provide {quoted missing}, which the core root '{host.Package}/{root}' does" ]
        match differences with
        | [] -> []
        | _ ->
            let said = differences |> String.concat ", and "
            [ Diagnostics.at
                  DiagnosticCodes.ComponentRootContextsDiffer
                  span
                  $"The component root's '{runFunction}' {said}: a component root provides the contexts its core root does" ]

    /// The errors for each foreign function of `componentModule` that the module `origin` declares, unless it may require
    /// `HostRuntime` (see `mayRequireRuntime`, with `isHost`): the module is the compiler's own.
    let reservedExternErrors (isHost: string -> bool) (origin: ModuleOrigin) (module': Module) : Diagnostic list =
        if mayRequireRuntime isHost origin then
            []
        else
            let rec externs (items: TopLevelItem list) =
                items
                |> List.collect (fun item ->
                    match item.Kind with
                    | Def (ValueDef(_, name, _, Kind (ExternSyntax.Import(importModule, _)))) when importModule = componentModule -> [ name, item.Span ]
                    | ModuleBlock(_, blockItems) -> externs blockItems
                    | _ -> [])
            externs module'
            |> List.map (fun (name, span) ->
                Diagnostics.at
                    DiagnosticCodes.ReservedExternModule
                    span
                    $"'{name}' imports from \"{componentModule}\", which is the compiler's own, for a host's component root: only a host's package, and the host libraries it depends on, may")

    /// Whether `ty` is the context `HostRuntime`, as the module names it (`HostRuntime`, or `r.HostRuntime` under an
    /// alias).
    let private isRuntime (ty: Ty) =
        let named (name: string) = name = runtimeContext || name.EndsWith("." + runtimeContext)
        match ty with
        | TyAlias(name, _, _) when named name -> true
        | _ ->
            match Unifier.unalias ty with
            | TyApply(name, _) -> named name
            | TyNominal(id, _, _, _) -> named id.Name
            | _ -> false

    let private runtimeError (span: SourceSpan) (what: string) =
        Diagnostics.at
            DiagnosticCodes.HostRuntimeOutsideHost
            span
            $"{what} requires the context '{runtimeContext}', which only a host's package, and the host libraries it depends on, may require: use the contexts the host provides, such as 'Console'"

    /// The errors for each place a module that is not a host (see `mayRequireRuntime`, with `isHost`) requires `HostRuntime`: each of
    /// its top-level defs that requires it, as `contextTypes` (by symbol) has its contexts or a call in it does, other
    /// than its foreign functions, which any module may declare (see `ExternSyntax`), and each call at its top level that
    /// requires it, other than the calls of the host's `run` that its entry points make, and of its `provide` that the
    /// defs an export that requires contexts is called through make (`entries`), and those defs (`wrappers`). A call requires it
    /// when the runtime provides it there (`providers`, by the call's node), or when the def it calls requires it
    /// (`calleeContexts`, by the call's node): `HostRuntime` has no members, so nothing else records where it is needed.
    let runtimeErrors
        (isHost: string -> bool)
        (origin: ModuleOrigin)
        (entries: EntryPoint list)
        (wrappers: Set<string>)
        (symbols: Map<SymbolId, Symbol>)
        (contextTypes: Map<SymbolId, Ty list>)
        (providers: Map<NodeId, ProvidedContext list>)
        (calleeContexts: NodeId -> Ty list)
        (module': Module)
        : Diagnostic list =
        if mayRequireRuntime isHost origin then
            []
        else
            let startCalls = entries |> List.collect _.Call |> Set.ofList
            let needsRuntime (expr: Expression) =
                not (startCalls.Contains expr.Id)
                && (calleeContexts expr.Id |> List.exists isRuntime
                    || providers
                       |> Map.tryFind expr.Id
                       |> Option.exists (List.exists (fun provided -> provided.Provider = FromRuntime && isRuntime provided.Context)))
            let rec defs (items: TopLevelItem list) =
                items
                |> List.collect (fun item ->
                    match item.Kind with
                    | Def (ValueDef(_, _, _, Kind (ExternSyntax.Import _))) -> []
                    | Def (ValueDef(_, name, _, _)) when wrappers.Contains name -> []
                    | Def (ValueDef(_, name, _, value)) -> [ name, item, value ]
                    | ModuleBlock(_, blockItems) -> defs blockItems
                    | _ -> [])
            let declared = symbols |> Map.values |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue) |> List.ofSeq
            let defErrors =
                defs module'
                |> List.choose (fun (name, item, value) ->
                    declared
                    |> List.tryFind (fun symbol -> symbol.Name = name && SourceSpan.contains item.Span symbol.Declaration)
                    |> Option.filter (fun symbol ->
                        contextTypes |> Map.tryFind symbol.Id |> Option.exists (List.exists isRuntime)
                        || SyntaxTree.expressionsWithin value |> List.exists needsRuntime))
                |> List.sortBy _.Declaration.Start
                |> List.map (fun symbol -> runtimeError symbol.Declaration $"'{symbol.Name}'")
            let callErrors =
                module'
                |> List.choose (function
                    | Kind (Expr _) as item -> Some item
                    | _ -> None)
                |> List.collect SyntaxTree.ofItem
                |> List.collect SyntaxTree.expressionsWithin
                |> List.filter needsRuntime
                |> List.map (fun expr ->
                    match expr.Kind with
                    | FunctionCall(name, _, _)
                    | Pipe(_, name, _, _) -> runtimeError expr.Span $"'{name}'"
                    | _ -> runtimeError expr.Span "This call")
            defErrors @ callErrors

/// Host libraries: packages that hosts are written on, which may require `HostRuntime` (see `field`).
module HostLibraries =
    open Parser.Program.DataValues

    /// The field of a package's `package` that declares it a host library: `hostLibrary = true`. A host library's
    /// modules may require `HostRuntime`, as a host's own do, so that hosts share what they are written on: the build
    /// host's package and the host libraries it depends on are the code that reaches the outside world (see the Hosts
    /// page of the docs).
    let field = "hostLibrary"

    /// Whether the package whose manifest is `manifest` declares itself a host library.
    let declares (manifest: DataValue) : bool =
        match manifest.Data with
        | DataRecord top ->
            match DataValue.field "package" top with
            | Some { DataField.Value = { DataValue.Data = DataRecord package } } ->
                match DataValue.field field package with
                | Some { DataField.Value = { DataValue.Data = DataBool declared } } -> declared
                | _ -> false
            | _ -> false
        | _ -> false

