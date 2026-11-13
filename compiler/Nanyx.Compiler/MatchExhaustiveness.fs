namespace NyxCompiler

open Parser.Program

/// The error for a match that leaves out a value of its scrutinees, with the fix that adds the arms it is missing.
module MatchExhaustiveness =
    /// The error for the match whose arms are `arms`, reported at `headSpan`, given `witnesses`, a value no arm matches
    /// for each scrutinee (see `PatternSpace.missingCase`), if there is one, and the scrutinees' types `scrutineeTypes`.
    /// The value is described as the one that makes the match incomplete, or as the combination of values.
    let ofWitnesses (arms: MatchArm list) (scrutineeTypes: Ty list) (headSpan: SourceSpan) (witnesses: PatternSpace.Witness list option) : Diagnostic list =
        match witnesses with
        | None -> []
        | Some witnesses ->
            let diagnostic =
                match witnesses |> List.indexed |> List.filter (fun (_, witness) -> witness <> PatternSpace.Anything) with
                | [ index, witness ] ->
                    let suffix = if scrutineeTypes.Length > 1 then $" (scrutinee {index + 1})" else ""
                    Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan $"Non-exhaustive match{suffix}, {PatternSpace.whatIsMissing witness}"
                | _ ->
                    let missing = witnesses |> List.map PatternSpace.show |> String.concat ", "
                    Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan $"Non-exhaustive match, missing case: {missing}"
            [ { diagnostic with Fixes = MatchArmFixes.unplaced arms (PatternSpace.missingArms (arms |> List.map fst) (scrutineeTypes |> List.map Some)) } ]

    /// The error for the match whose arms are `arms`, reported at `headSpan`, if they leave out a value of the types
    /// `scrutineeTypes`.
    let check (arms: MatchArm list) (scrutineeTypes: Ty list) (headSpan: SourceSpan) : Diagnostic list =
        PatternSpace.missingCase (arms |> List.map fst) (scrutineeTypes |> List.map Some) |> ofWitnesses arms scrutineeTypes headSpan
