namespace NyxCompiler

open Parser.Program

/// A host: the standard-library module that runs the program's entry point, under `use`s of the contexts it provides
/// (see the Hosts page of the docs). The program's `@entry` is passed to the host's `run`, which is the module's start.
type Host =
    { /// The package a manifest's `host` names it by: `nanyx.console`.
      Package: string
      /// The standard-library module that is the host, as it is imported: `nanyx/console.host`.
      Module: string
      /// The modules of the contexts the host provides to the entry point: `nanyx/console` for `Console` and
      /// `nanyx/env` for `Env`.
      ProvidedModules: string list
      /// The provided modules that the module declaring the entry point sees without importing them: `nanyx/console`,
      /// so that `println` is in scope. A program imports the others itself, as `import nanyx/env`.
      ImplicitModules: string list }

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

    /// The context a host is written on: raw reads and writes, which only a host may require.
    let runtimeContext = "HostRuntime"

    /// The host a program runs under when it names none: a command-line program, with `Console` and `Env`.
    let console =
        { Package = "nanyx.console"
          Module = StandardLibrary.importPrefix + "console.host"
          ProvidedModules = [ StandardLibrary.importPrefix + "console"; StandardLibrary.importPrefix + "env" ]
          ImplicitModules = [ StandardLibrary.importPrefix + "console" ] }

    /// The hosts the standard library ships.
    let all = [ console ]

    /// The hosts that can be chosen, as a message lists them.
    let private choices () = all |> List.map (fun host -> $"'{host.Module}'") |> String.concat ", "

    /// The host that is the module `name`, as `--host` writes it (`nanyx/console.host`), or
    /// why there is none: no such module, or a module that isn't a host. Until packages resolve, only the standard
    /// library's hosts can be chosen.
    let tryFind (name: string) : Result<Host, string> =
        match all |> List.tryFind (fun host -> host.Module = name) with
        | Some host -> Ok host
        | None when not (name.StartsWith StandardLibrary.importPrefix) ->
            Error $"'{name}' is not a host: until packages resolve, a host is one of the standard library's, {choices ()}"
        | None when StandardLibrary.moduleNames |> List.contains (name.Substring StandardLibrary.importPrefix.Length) ->
            Error $"'{name}' is not a host: it has no '{runFunction}' that runs an entry point. The hosts are {choices ()}"
        | None -> Error $"There is no host '{name}': the standard library has no such module. The hosts are {choices ()}"

    /// Whether the module `origin` declares contexts a host provides, as `nanyx/console` declares `Console`.
    let providesContexts (origin: ModuleOrigin) =
        match origin with
        | StandardLibraryModule name -> all |> List.exists (fun host -> host.ProvidedModules |> List.contains (StandardLibrary.importPrefix + name))
        | SourceFile _
        | InMemory -> false

    /// Whether the module `origin` may require `HostRuntime`: a host, or the module that declares it.
    let mayRequireRuntime (origin: ModuleOrigin) =
        match origin with
        | StandardLibraryModule name ->
            name = runtimeModule || all |> List.exists (fun host -> host.Module = StandardLibrary.importPrefix + name)
        | SourceFile _
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
            $"The entry point '{entry.Name}' doesn't fit the host '{entry.Host.Module}'\n  expected: {shown.[0]}\n  found:    {shown.[1]}"

    /// Whether `ty` is the context `HostRuntime`, as the module names it (`HostRuntime`, or `r.HostRuntime` under an
    /// alias).
    let private isRuntime (ty: Ty) =
        let named (name: string) = name = runtimeContext || name.EndsWith("." + runtimeContext)
        match Unifier.unalias ty with
        | TyApply(name, _) -> named name
        | TyNominal(id, _, _) -> named id.Name
        | _ -> false

    let private runtimeError (span: SourceSpan) (what: string) =
        Diagnostics.at
            DiagnosticCodes.HostRuntimeOutsideHost
            span
            $"{what} requires the context '{runtimeContext}', which only a host may require. Use the contexts the host provides, such as 'Console'."

    /// The errors for each place a module that is not a host requires `HostRuntime` (see `mayRequireRuntime`): each of
    /// its top-level defs whose type requires it (`contextTypes`, by symbol), other than its foreign functions, which
    /// any module may declare (see `ExternSyntax`), and each call at its top level that the runtime provides it to
    /// (`providers`, by the call's node), other than the calls of the host's `run` that its entry points make
    /// (`entries`).
    let runtimeErrors
        (origin: ModuleOrigin)
        (entries: EntryPoint list)
        (symbols: Map<SymbolId, Symbol>)
        (contextTypes: Map<SymbolId, Ty list>)
        (providers: Map<NodeId, ProvidedContext list>)
        (module': Module)
        : Diagnostic list =
        if mayRequireRuntime origin then
            []
        else
            let startCalls = entries |> List.collect _.Call |> Set.ofList
            let externs =
                module'
                |> List.choose (function
                    | Kind (Def (ValueDef(_, name, _, Kind (ExternSyntax.Import _)))) -> Some name
                    | _ -> None)
                |> Set.ofList
            let defErrors =
                contextTypes
                |> Map.toList
                |> List.filter (fun (_, contexts) -> contexts |> List.exists isRuntime)
                |> List.choose (fun (id, _) -> symbols |> Map.tryFind id)
                |> List.filter (fun symbol -> not (externs.Contains symbol.Name))
                |> List.sortBy _.Declaration.Start
                |> List.map (fun symbol -> runtimeError symbol.Declaration $"'{symbol.Name}'")
            let callErrors =
                module'
                |> List.choose (function
                    | Kind (Expr _) as item -> Some item
                    | _ -> None)
                |> List.collect SyntaxTree.ofItem
                |> List.collect SyntaxTree.expressionsWithin
                |> List.filter (fun expr ->
                    not (startCalls.Contains expr.Id)
                    && providers
                       |> Map.tryFind expr.Id
                       |> Option.exists (List.exists (fun provided -> provided.Provider = FromRuntime && isRuntime provided.Context)))
                |> List.map (fun expr ->
                    match expr.Kind with
                    | FunctionCall(name, _, _)
                    | Pipe(_, name, _, _) -> runtimeError expr.Span $"'{name}'"
                    | _ -> runtimeError expr.Span "This call")
            defErrors @ callErrors
