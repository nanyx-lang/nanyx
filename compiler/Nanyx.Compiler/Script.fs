namespace NyxCompiler

open System.IO
open Parser.Program

/// What a script's `::` directives say: what a package's manifest would, for a file run on its own, which has none.
type ScriptDirectives =
    { /// The linters the `::linter` directives turn on and the severities they set, as a `[linters]` table
      /// would. None when there is no `::linter` directive: then no linter runs, as for a package whose manifest
      /// has no `[linters]` table.
      Linters: LinterOptions option
      /// What is wrong with the directives, each at its line.
      Diagnostics: Diagnostic list }

module Script =
    /// The directives there are. `::formatter` is the formatter's, which reads it itself.
    let private names = [ "linter"; "formatter"; "package" ]

    let private quoted (names: string seq) = names |> Seq.map (fun name -> $"'{name}'") |> String.concat ", "

    let private directiveList = quoted (names |> List.map ((+) "::"))

    /// What a `::linter` directive says: a linter to run, and the severity it sets for one of its rules, if any.
    let private linterSetting (directive: Directive) : Result<Linter * (string * Severity option) option, string> =
        let linter (name: string) =
            match Linters.all |> List.tryFind (fun linter -> linter.Name = name) with
            | Some linter -> Result.Ok linter
            | None -> Result.Error $"unknown linter '{name}'; the linters are {quoted (Linters.all |> List.map _.Name)}"
        match directive.Arguments with
        | [ name ] when not (name.Contains '.') -> linter name |> Result.map (fun linter -> linter, None)
        | [ rule; severity ] when rule.Contains '.' ->
            let dot = rule.IndexOf '.'
            let ruleId = rule.Substring(dot + 1)
            linter (rule.Substring(0, dot))
            |> Result.bind (fun linter ->
                let known = linter.Rules @ [ Linters.failedRule ] |> List.map _.Id
                if not (List.contains ruleId known) then
                    Result.Error $"unknown rule '{ruleId}' of the linter '{linter.Name}'; the rules are {quoted known}"
                else
                    match Manifest.severities |> List.tryFind (fst >> (=) severity) with
                    | Some (_, level) -> Result.Ok(linter, Some(Linters.code linter ruleId, level))
                    | None -> Result.Error $"::linter {rule} must be {quoted (List.map fst Manifest.severities)}, not '{severity}'")
        | _ ->
            Result.Error "::linter takes a linter, or one of its rules and a severity: ::linter default, or ::linter default.unusedBindings error"

    /// The directives at the top of `source`, the text of the file at `filePath` (empty for one not on disk). In a
    /// file of a package each is an error, since the package's manifest says these things, and they give no linters.
    let read (filePath: string) (source: string) : ScriptDirectives =
        let filePath = if filePath = "" then "" else Path.GetFullPath filePath
        let directives = Directives.read filePath source
        let manifest =
            match filePath with
            | "" -> None
            | _ when not (Directory.Exists(Path.GetDirectoryName filePath)) -> None
            | _ -> Manifest.tryFind (Path.GetDirectoryName filePath) |> Result.defaultValue None
        match manifest with
        | _ when directives.IsEmpty -> { Linters = None; Diagnostics = [] }
        | Some manifest -> { Linters = None; Diagnostics = Package.directiveErrors manifest filePath source }
        | None ->
            let invalid (directive: Directive) message = Result.Error(Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span message)
            let read (directive: Directive) =
                match directive.Name, directive.Arguments with
                | "linter", _ -> linterSetting directive |> Result.map Some |> Result.mapError (Diagnostics.at DiagnosticCodes.InvalidDirective directive.Span)
                | "formatter", _ -> Result.Ok None
                | "package", [ _; _ ] ->
                    Result.Error(
                        Diagnostics.at
                            DiagnosticCodes.UnsupportedDirective
                            directive.Span
                            "::package is not supported yet: a script can only use the standard library and the files it imports")
                | "package", _ -> invalid directive "::package takes a package and its version: ::package acme.json 1.2"
                | "", _ -> invalid directive $"a directive needs a name after '::'; the directives are {directiveList}"
                | name, _ -> invalid directive $"unknown directive ::{name}; the directives are {directiveList}"
            let results = directives |> List.map read
            let settings = results |> List.choose (function Result.Ok (Some setting) -> Some setting | _ -> None)
            { Linters =
                if settings.IsEmpty then
                    None
                else
                    // In the order the compiler has them, as for a manifest.
                    Some
                        { Linters = Linters.all |> List.filter (fun linter -> settings |> List.exists (fun (a, _) -> a.Name = linter.Name))
                          Severities = settings |> List.choose snd |> Map.ofList }
              Diagnostics = results |> List.choose (function Result.Error diagnostic -> Some diagnostic | Result.Ok _ -> None) }

    /// `result` with the problems with `directives` before its own diagnostics. An error in a directive is not
    /// deferred: it stops a debug run too.
    let withDirectives (directives: ScriptDirectives) (result: CompileResult) : CompileResult =
        { result with
            Diagnostics = directives.Diagnostics @ result.Diagnostics
            ErrorsDeferred = result.ErrorsDeferred && not (directives.Diagnostics |> List.exists Diagnostics.isError) }

    /// Compiles `source` as the script at `filePath`, with the linters its directives turn on (none if they turn none
    /// on), as `nanyx run` does.
    let compileSource (filePath: string) (source: string) : CompileResult =
        let directives = read filePath source
        Compiler.compileSourceWith (directives.Linters |> Option.defaultValue Linters.none) filePath source
        |> withDirectives directives

    /// `compileSource` for the file at `filePath` as it is on disk.
    let compileFile (filePath: string) : CompileResult =
        if File.Exists filePath then compileSource filePath (File.ReadAllText filePath) else Compiler.compileFile filePath
