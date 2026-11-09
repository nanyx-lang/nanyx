namespace NyxCompiler

open System.IO
open Parser.Program

/// What a script's `#:` directives say: what a package's manifest would, for a file run on its own, which has none.
type ScriptDirectives =
    { /// The analyzers the `#:analyzer` directives turn on and the severities they set, as an `[analyzers]` table
      /// would. None when there is no `#:analyzer` directive: then no analyzer runs, as for a package whose manifest
      /// has no `[analyzers]` table.
      Analyzers: AnalyzerOptions option
      /// What is wrong with the directives, each at its line.
      Diagnostics: Diagnostic list }

module Script =
    /// The directives there are. `#:formatter` is the formatter's, which reads it itself.
    let private names = [ "analyzer"; "formatter"; "package" ]

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    let private directiveList = quoted (names |> List.map ((+) "#:"))

    /// What an `#:analyzer` directive says: an analyzer to run, and the severity it sets for one of its rules, if any.
    let private analyzerSetting (directive: Directive) : Result<Analyzer * (string * Severity option) option, string> =
        let analyzer (name: string) =
            match Analyzers.all |> List.tryFind (fun analyzer -> analyzer.Name = name) with
            | Some analyzer -> Result.Ok analyzer
            | None -> Result.Error $"unknown analyzer '{name}'; the analyzers are {quoted (Analyzers.all |> List.map _.Name)}"
        match directive.Arguments with
        | [ name ] when not (name.Contains '.') -> analyzer name |> Result.map (fun analyzer -> analyzer, None)
        | [ rule; severity ] when rule.Contains '.' ->
            let dot = rule.IndexOf '.'
            let ruleId = rule.Substring(dot + 1)
            analyzer (rule.Substring(0, dot))
            |> Result.bind (fun analyzer ->
                let known = analyzer.Rules @ [ Analyzers.failedRule ] |> List.map _.Id
                if not (List.contains ruleId known) then
                    Result.Error $"unknown rule '{ruleId}' of the analyzer '{analyzer.Name}'; the rules are {quoted known}"
                else
                    match Manifest.severities |> List.tryFind (fst >> (=) severity) with
                    | Some (_, level) -> Result.Ok(analyzer, Some(Analyzers.code analyzer ruleId, level))
                    | None -> Result.Error $"#:analyzer {rule} must be {quoted (List.map fst Manifest.severities)}, not '{severity}'")
        | _ ->
            Result.Error "#:analyzer takes an analyzer, or one of its rules and a severity: #:analyzer default, or #:analyzer default.unusedBindings error"

    /// The directives at the top of `source`, the text of the file at `filePath` (empty for one not on disk). In a
    /// file of a package each is an error, since the package's manifest says these things, and they give no analyzers.
    let read (filePath: string) (source: string) : ScriptDirectives =
        let filePath = if filePath = "" then "" else Path.GetFullPath filePath
        let directives = Directives.read filePath source
        let manifest =
            match filePath with
            | "" -> None
            | _ when not (Directory.Exists(Path.GetDirectoryName filePath)) -> None
            | _ -> Manifest.tryFind (Path.GetDirectoryName filePath) |> Result.defaultValue None
        match manifest with
        | _ when directives.IsEmpty -> { Analyzers = None; Diagnostics = [] }
        | Some manifest -> { Analyzers = None; Diagnostics = Package.directiveErrors manifest filePath source }
        | None ->
            let invalid (directive: Directive) message = Result.Error(Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span message)
            let read (directive: Directive) =
                match directive.Name, directive.Arguments with
                | "analyzer", _ -> analyzerSetting directive |> Result.map Some |> Result.mapError (Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span)
                | "formatter", _ -> Result.Ok None
                | "package", [ _; _ ] ->
                    Result.Error(
                        Diagnostics.at
                            DiagnosticCodes.UnsupportedDirective
                            directive.Span
                            "#:package is not supported yet: a script can only use the standard library and the files it imports")
                | "package", _ -> invalid directive "#:package takes a package and its version: #:package acme.json 1.2"
                | "", _ -> invalid directive $"a directive needs a name after '#:'; the directives are {directiveList}"
                | name, _ -> invalid directive $"unknown directive #:{name}; the directives are {directiveList}"
            let results = directives |> List.map read
            let settings = results |> List.choose (function Result.Ok (Some setting) -> Some setting | _ -> None)
            { Analyzers =
                if settings.IsEmpty then
                    None
                else
                    // In the order the compiler has them, as for a manifest.
                    Some
                        { Analyzers = Analyzers.all |> List.filter (fun analyzer -> settings |> List.exists (fun (a, _) -> a.Name = analyzer.Name))
                          Severities = settings |> List.choose snd |> Map.ofList }
              Diagnostics = results |> List.choose (function Result.Error diagnostic -> Some diagnostic | Result.Ok _ -> None) }

    /// `result` with the problems with `directives` before its own diagnostics. An error in a directive is not
    /// deferred: it stops a debug run too.
    let withDirectives (directives: ScriptDirectives) (result: CompileResult) : CompileResult =
        { result with
            Diagnostics = directives.Diagnostics @ result.Diagnostics
            ErrorsDeferred = result.ErrorsDeferred && not (directives.Diagnostics |> List.exists Diagnostics.isError) }

    /// Compiles `source` as the script at `filePath`, with the analyzers its directives turn on (none if they turn none
    /// on), as `nanyx run` does.
    let compileSource (filePath: string) (source: string) : CompileResult =
        let directives = read filePath source
        Compiler.compileSourceWith (directives.Analyzers |> Option.defaultValue Analyzers.none) filePath source
        |> withDirectives directives

    /// `compileSource` for the file at `filePath` as it is on disk.
    let compileFile (filePath: string) : CompileResult =
        if File.Exists filePath then compileSource filePath (File.ReadAllText filePath) else Compiler.compileFile filePath
