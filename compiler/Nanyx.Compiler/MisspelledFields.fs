/// A record written with a field that its type doesn't have, where the type has a field of a close name in its place
/// (#2078): `(first = "a", lsat = "b")` given for `P`, whose fields are `first` and `last`. The mismatch is reported at
/// the misspelled field, as the field it is likely to be, with a fix that writes that.
module internal NyxCompiler.MisspelledFields

open Parser.Program
open NyxCompiler.InferState

/// The error for the mismatch at `span` between two records that differ in one field each, `onlyLeft` and `onlyRight`
/// (see `MisspelledField`), at the field of a record value in `module'` within `span` that is written with one of them,
/// if there is one. The state's `Declared` has where the parser saw each field's name written.
let diagnose (state: InferState) (module': Module) (span: SourceSpan) (onlyLeft: string, rightRecord: string, onlyRight: string, leftRecord: string) : Diagnostic option =
    let within (inner: SourceSpan) = inner.File = span.File && span.Start <= inner.Start && inner.End <= span.End
    module'
    |> List.collect SyntaxTree.ofItem
    |> List.collect SyntaxTree.expressionsWithin
    |> List.tryPick (fun expr ->
        match expr.Kind with
        | RecordExpr fields when within expr.Span ->
            fields
            |> List.tryPick (function
                | NamedField(name, value) when name = onlyLeft -> Some (value, onlyLeft, rightRecord, onlyRight)
                | NamedField(name, value) when name = onlyRight -> Some (value, onlyRight, leftRecord, onlyLeft)
                | _ -> None)
        | _ -> None)
    |> Option.bind (fun (value, written, record, meant) ->
        state.Declared
        |> Map.tryFind (value.Id, NamedFieldValue)
        |> Option.bind (List.tryFind (fun (name, _) -> name = written))
        |> Option.map (fun (_, nameSpan) ->
            let diagnostic =
                Diagnostics.at (MemoryCells.mismatchCode state span) nameSpan $"'{written}' is not a field of {record}: did you mean '{meant}'?"
            { diagnostic with Fixes = [ { Title = $"Write `{meant}`"; Edits = [ { Span = nameSpan; NewText = meant } ] } ] }))
