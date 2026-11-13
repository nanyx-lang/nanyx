namespace NyxCompiler

open Parser.Program

/// The fix for a match that is not exhaustive: it adds the arms it is missing after its last arm, each with `???` for
/// a body, so that the bodies can be written. Inference knows which arms are missing but not how the match is laid
/// out, so it makes the fix with each arm on a line of its own (`unplaced`), and `place` lays them out as the match's
/// last arm is, given the source.
module MatchArmFixes =
    /// The fix that adds the arms with the patterns `missing` (as written, one per value matched) to a match whose arms
    /// are `arms`: an insertion at the end of the last arm's body of each arm after a line break, unindented.
    let unplaced (arms: MatchArm list) (missing: string list list) : Fix list =
        match List.tryLast arms, missing with
        | Some (_, body), _ :: _ when not (SourceSpan.isNone body.Span) ->
            let lines = missing |> List.map (fun patterns -> $"""{'\n'}| {String.concat ", " patterns} -> {Expressions.notImplemented}""")
            [ { Title = (if missing.Length = 1 then "Add the missing arm" else "Add the missing arms")
                Edits = [ { Span = { body.Span with Start = body.Span.End }; NewText = String.concat "" lines } ] } ]
        | _ -> []

    /// What goes before each added arm, given the match's last arm, whose first pattern starts at `patternStart` in
    /// `source`: a line break and the indentation of the last arm's `|` when it starts its line, or a space when it
    /// follows other code on its line. `None` if the arm has no `|` before it, as a lambda's pattern parameters do.
    let private separator (source: string) (patternStart: int) : string option =
        let mutable bar = patternStart - 1
        while bar >= 0 && (source.[bar] = ' ' || source.[bar] = '\t') do
            bar <- bar - 1
        if bar < 0 || source.[bar] <> '|' then
            None
        else
            let lineStart = source.LastIndexOf('\n', max 0 (bar - 1)) + 1
            let indent = source.Substring(lineStart, bar - lineStart)
            Some (if System.String.IsNullOrWhiteSpace indent then "\n" + indent else " ")

    /// `diagnostics` with the fixes `unplaced` made laid out as the matches they are for, in the module `syntax`, whose
    /// text is `source` (with its line endings made `\n`). A fix for a match whose arms have no `|`, or for another
    /// module, is left out.
    let place (source: string) (syntax: Module) (diagnostics: Diagnostic list) : Diagnostic list =
        let file = syntax |> List.tryPick (fun item -> if SourceSpan.isNone item.Span then None else Some item.Span.File)
        let lastArms =
            lazy (
                syntax
                |> List.collect SyntaxTree.ofItem
                |> List.collect SyntaxTree.expressionsWithin
                |> List.choose (fun expr ->
                    match expr.Kind with
                    | Match(_, arms) ->
                        match List.tryLast arms with
                        | Some (first :: _, body) when Some body.Span.File = file -> Some (body.Span.End, first.Span.Start)
                        | _ -> None
                    | _ -> None)
                |> Map.ofList)
        let laidOut (fix: Fix) : Fix option =
            match fix.Edits with
            | [ edit ] when Some edit.Span.File = file && edit.Span.Start <= source.Length ->
                lastArms.Value
                |> Map.tryFind edit.Span.Start
                |> Option.bind (separator source)
                |> Option.map (fun before ->
                    let text = edit.NewText.Replace("\n", before)
                    { fix with Edits = [ { Span = TextEdit.spanBetween source edit.Span edit.Span.Start edit.Span.Start; NewText = text } ] })
            | _ -> None
        diagnostics
        |> List.map (fun diagnostic ->
            if diagnostic.Code = DiagnosticCodes.NonExhaustiveMatch.Code && not diagnostic.Fixes.IsEmpty then
                { diagnostic with Fixes = diagnostic.Fixes |> List.choose laidOut }
            else
                diagnostic)
