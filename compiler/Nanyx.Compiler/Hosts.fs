namespace NyxCompiler

open Parser.Program

/// A host: a package whose manifest declares that it is one (see `HostPackages`), with the module that runs the
/// program's entry point, under `use`s of the contexts it provides, and the prelude that brings what it provides into
/// scope in every module of the program (see the Hosts page of the docs). The program's `@entry` is passed to the
/// host's `run`, which is the module's start.
type Host =
    { /// The package a manifest's `host` names it by: `nanyx.console`.
      Package: string
      /// The host's root module, which exports `run`: `console.host`.
      Module: string
      /// The host's prelude, if it has one: a module of the package whose exports every module of the program sees
      /// without importing them, as `println` and `Console`.
      Prelude: string option
      /// The directory of the host's package: beside the compiler for one it ships (see `ShippedPackages`).
      Directory: string }

    /// The host's root module as an import names it: `nanyx.console/console.host`.
    member host.Import = $"{host.Package}/{host.Module}"

    /// The host's prelude as an import names it, if it has one: `nanyx.console/console.prelude`.
    member host.PreludeImport = host.Prelude |> Option.map (fun prelude -> $"{host.Package}/{prelude}")
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

    /// The alias the module that declares the entry point imports the host under, which no name written in Nanyx can
    /// clash with.
    let importAlias = "__host"

    /// The standard-library module whose context, `HostRuntime`, the toolchain provides only to hosts.
    let runtimeModule = "host.runtime"

    /// The standard-library modules a host is written on, whose functions all require `HostRuntime`: the one that
    /// declares it, and `nanyx/host.memory`.
    let private runtimeModules = [ runtimeModule; "host.memory" ]

    /// The package of the console host the compiler ships: what a script runs under, unless it names another, and what
    /// `nanyx new` names.
    let consolePackage = "nanyx.console"

    /// The context a host is written on: raw reads and writes, which only a host may require.
    let runtimeContext = "HostRuntime"

    /// Whether the module `origin` may require `HostRuntime`: the host, whose files `isHost` tells apart, or a module a
    /// host is written on (see `runtimeModules`).
    let mayRequireRuntime (isHost: string -> bool) (origin: ModuleOrigin) =
        match origin with
        | StandardLibraryModule name -> List.contains name runtimeModules
        | SourceFile path -> isHost path
        | InMemory -> false

    /// The error for an entry point that does not fit its host's `run`: `found` is the entry point's type, `expected`
    /// the type of `run`'s parameter, where they are known.
    let entryMismatch (entry: EntryPoint) (found: Ty option) (expected: Ty option) =
        let unknown = TyPrimitive "?error"
        // Shown together, so that the variables are named alike in both.
        let shown = Unifier.tysToDisplayStrings Source [ defaultArg expected unknown; defaultArg found unknown ]
        Diagnostics.at
            DiagnosticCodes.EntryDoesNotFitHost
            entry.Argument
            $"The entry point '{entry.Name}' doesn't fit the host '{entry.Host.Import}'\n  expected: {shown.[0]}\n  found:    {shown.[1]}"

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
            $"{what} requires the context '{runtimeContext}', which only a host may require. Use the contexts the host provides, such as 'Console'."

    /// The errors for each place a module that is not a host (see `mayRequireRuntime`, with `isHost`) requires `HostRuntime`: each of
    /// its top-level defs that requires it, as `contextTypes` (by symbol) has its contexts or a call in it does, other
    /// than its foreign functions, which any module may declare (see `ExternSyntax`), and each call at its top level that
    /// requires it, other than the calls of the host's `run` that its entry points make (`entries`). A call requires it
    /// when the runtime provides it there (`providers`, by the call's node), or when the def it calls requires it
    /// (`calleeContexts`, by the call's node): `HostRuntime` has no members, so nothing else records where it is needed.
    let runtimeErrors
        (isHost: string -> bool)
        (origin: ModuleOrigin)
        (entries: EntryPoint list)
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