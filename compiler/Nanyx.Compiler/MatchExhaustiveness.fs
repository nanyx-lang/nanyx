namespace NyxCompiler

open Parser.Program

/// The error for a match that leaves out a value of its scrutinees, with the fix that adds the arms it is missing.
module MatchExhaustiveness =
    /// The scrutinees of a match that are what a custom pattern returns (see `CustomPatterns`), by position.
    let patternColumns (scrutinees: Expression list) : Set<int> =
        scrutinees
        |> List.indexed
        |> List.choose (fun (index, scrutinee) ->
            match scrutinee.Kind with
            | IdentifierExpr name when PatternSyntax.isResultName name -> Some index
            | _ -> None)
        |> Set.ofList

    /// What a match is missing where a custom pattern's result is matched, when `witness` is what it returns that no
    /// arm handles: some of its tags, or what a partial pattern returns for `_`, `PatternSyntax.noMatchTag` or `false`.
    let private patternCaseMissing (witness: PatternSpace.Witness) =
        let tags =
            match witness with
            | PatternSpace.OneOfTags tags -> tags
            | PatternSpace.TagCase(tag, _) -> [ tag ]
            | PatternSpace.BoolCase false -> [ PatternSyntax.noMatchTag ]
            | _ -> []
        let written = tags |> List.filter ((<>) PatternSyntax.noMatchTag)
        let noMatch = "a `_` arm for the values the pattern doesn't match"
        match written, tags.Length > written.Length with
        | [], false -> $"missing case: {PatternSpace.show witness}"
        | [], true -> $"missing {noMatch}"
        | _, isPartial ->
            let missing = $"""{written |> List.map (fun tag -> $"`#{tag}`") |> Diagnostics.listed}, which the pattern returns too"""
            if isPartial then $"missing {missing}, and {noMatch}" else $"missing {missing}"

    /// `text` with its first letter a capital, as a message starts.
    let private capitalised (text: string) =
        if text = "" then text else string (System.Char.ToUpperInvariant text[0]) + text.Substring 1

    /// The error for the match whose arms are `arms`, reported at `headSpan`, given `witnesses`, a value no arm matches
    /// for each scrutinee (see `PatternSpace.missingCase`), if there is one, and the scrutinees' types `scrutineeTypes`.
    /// The value is described as the one that makes the match incomplete, or as the combination of values. The
    /// scrutinees at `columns` are what custom patterns return: what is missing there is said as the pattern's tags,
    /// and the others are counted without them. A match with any gets no fix.
    let ofWitnesses (arms: MatchArm list) (columns: Set<int>) (scrutineeTypes: Ty list) (headSpan: SourceSpan) (witnesses: PatternSpace.Witness list option) : Diagnostic list =
        match witnesses with
        | None -> []
        | Some witnesses ->
            let written = witnesses |> List.indexed |> List.filter (fun (index, _) -> not (columns.Contains index))
            let missingResult = witnesses |> List.indexed |> List.tryFind (fun (index, witness) -> columns.Contains index && witness <> PatternSpace.Anything)
            let diagnostic =
                match missingResult with
                | Some (_, witness) -> Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan (capitalised (patternCaseMissing witness))
                | None ->
                    match written |> List.filter (fun (_, witness) -> witness <> PatternSpace.Anything) with
                    | [ index, witness ] ->
                        let prefix = if written.Length > 1 then $"In scrutinee {index + 1}, " else ""
                        Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan (capitalised (prefix + PatternSpace.whatIsMissing witness))
                        |> Diagnostics.about (PatternSpace.show witness)
                    | _ ->
                        let missing = written |> List.map (snd >> PatternSpace.show) |> String.concat ", "
                        Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan $"Missing case: {missing}"
            let fixes =
                if columns.IsEmpty then MatchArmFixes.unplaced arms (PatternSpace.missingArms (arms |> List.map fst) (scrutineeTypes |> List.map Some))
                else []
            [ { diagnostic with Fixes = fixes } ]

    /// The error for the match whose arms are `arms`, reported at `headSpan`, if they leave out a value of the types
    /// `scrutineeTypes`.
    let check (arms: MatchArm list) (scrutineeTypes: Ty list) (headSpan: SourceSpan) : Diagnostic list =
        PatternSpace.missingCase (arms |> List.map fst) (scrutineeTypes |> List.map Some) |> ofWitnesses arms Set.empty scrutineeTypes headSpan