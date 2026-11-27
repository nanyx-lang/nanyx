/// A record written with a field that its type doesn't have, where the type has a field of a close name in its place
/// (#2078): `(first = "a", lsat = "b")` given for `P`, whose fields are `first` and `last`. The mismatch is reported at
/// the misspelled field, as the field it is likely to be, with a fix that writes that. A field the type has no place
/// for, close to none of its fields, is reported at its name too (see `writtenField`).
module internal NyxCompiler.MisspelledFields

open Parser.Program
open NyxCompiler.InferState

/// The first field of a record value or a record pattern in `module'` within `span` that is written with one of `names`:
/// its name, the node that declares it, and where the name is written, which the state's `Declared` has.
let private writtenWith (state: InferState) (module': Module) (span: SourceSpan) (names: string list) : (string * SourceSpan) option =
    let within (inner: SourceSpan) = inner.File = span.File && span.Start <= inner.Start && inner.End <= span.End
    let written (fields: (Identifier * NodeId) list) = fields |> List.tryFind (fun (name, _) -> List.contains name names)
    let expressions = module' |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
    let inValues () =
        expressions
        |> List.tryPick (fun expr ->
            match expr.Kind with
            | RecordExpr fields when within expr.Span ->
                fields |> List.choose (function NamedField(name, value) -> Some (name, value.Id) | PositionalField _ -> None) |> written
            | _ -> None)
    let inPatterns () =
        expressions
        |> List.collect (fun expr ->
            match expr.Kind with
            | Match(_, arms)
            | Except(_, arms) -> arms |> List.collect (fst >> List.collect Pattern.within)
            | _ -> [])
        |> List.tryPick (fun pattern ->
            match pattern.Kind with
            | RecordMemberPattern(members, _) when within pattern.Span -> members |> List.map (fun (name, value) -> name, value.Id) |> written
            | _ -> None)
    inValues ()
    |> Option.orElseWith inPatterns
    |> Option.bind (fun (name, node) ->
        state.Declared
        |> Map.tryFind (node, NamedFieldValue)
        |> Option.bind (List.tryFind (fun (declared, _) -> declared = name)))

/// The error for the mismatch at `span` between two records that differ in one field each, `onlyLeft` and `onlyRight`
/// (see `MisspelledField`), at the field of a record value or a record pattern in `module'` within `span` that is
/// written with one of them, if there is one.
let diagnose (state: InferState) (module': Module) (span: SourceSpan) (onlyLeft: string, rightRecord: string, onlyRight: string, leftRecord: string) : Diagnostic option =
    writtenWith state module' span [ onlyLeft; onlyRight ]
    |> Option.map (fun (written, nameSpan) ->
        let record, meant = if written = onlyLeft then rightRecord, onlyRight else leftRecord, onlyLeft
        let diagnostic =
            Diagnostics.at (MemoryCells.mismatchCode state span) nameSpan $"'{written}' is not a field of {record}: did you mean '{meant}'?"
        { diagnostic with Fixes = [ { Title = $"Write `{meant}`"; Edits = [ { Span = nameSpan; NewText = meant } ] } ] })

/// Where the mismatch at `span` between two records whose fields differ otherwise is reported (see `FieldsDiffer`): at
/// the first field of a record value or pattern within it written with a name only one of them has, `onlyLeft` or
/// `onlyRight`, as the field the other has no place for; at `span` if none is, as when a field is left out (#2718).
let writtenField (state: InferState) (module': Module) (span: SourceSpan) (onlyLeft: string list, onlyRight: string list) : SourceSpan =
    writtenWith state module' span (onlyLeft @ onlyRight) |> Option.map snd |> Option.defaultValue span
