/// Printing imports and re-exports.
module internal Nanyx.Formatter.ImportPrinter

open Parser.Program
open Nanyx.Formatter.SourceText
open Nanyx.Formatter.CommentPlacement

/// Prints imports, with the comments among their names put back by `placement`.
type ImportPrinter(indentSize: int, sourceText: SourceText, placement: CommentPlacement) =
    let text = Doc.text
    let cat = Doc.concat
    let hard = Doc.hardLine
    let nest doc = Doc.nest indentSize doc
    let unplacedIn start finish = placement.UnplacedIn(start, finish)
    let hasCommentsIn start finish = placement.HasCommentsIn(start, finish)

    let importSource (source: ImportSource) : Doc =
        match source with
        | ModuleImport m -> text m
        | PathImport p -> text ("\"" + p + "\"")

    /// The names an import or a re-export selects, on one line: `(sqrt, pi)`.
    let importSelection (names: (Identifier * SourceSpan) list) : Doc =
        text ("(" + (names |> List.map (fst >> Names.written) |> String.concat ", ") + ")")

    /// An import, its names sorted (see `ImportOrder`) within each run of them that no comment on a line of its own
    /// interrupts. A comment at the end of a name's line moves with the name; a comment on a line of its own stays
    /// where it is, ahead of the names after it.
    let importDoc (start: int) (finish: int) (items: ImportItem list) : Doc =
        let item (i: ImportItem) =
            cat [
                importSource i.Source
                (match i.Alias, i.Selection with
                 | Some alias, Some names -> cat [ text $" as {alias} & "; importSelection names ]
                 | Some alias, None -> text $" as {alias}"
                 | None, Some names -> cat [ text " as "; importSelection names ]
                 | None, None -> Doc.empty)
            ]
        match items with
        | [ single ] -> cat [ text "import "; item single ]
        | _ ->
            let items = Array.ofList items
            // Where a comment at the end of the line `items[k]` ends on would have to start before.
            let lineFinish k =
                let afterLine = sourceText.LineEnd (max items[k].Span.Start (items[k].Span.End - 1)) + 1
                if k + 1 < items.Length then min items[k + 1].Span.Start afterLine else afterLine
            let runs =
                [ 0 .. items.Length - 1 ]
                |> List.fold
                    (fun runs k ->
                        match runs with
                        | run :: rest when not (hasCommentsIn (lineFinish (k - 1)) items[k].Span.Start) -> (k :: run) :: rest
                        | _ -> [ k ] :: runs)
                    []
                |> List.rev
                |> List.map List.rev
            let entries = ResizeArray<StackEntry>()
            let placeBetween a b =
                for i in unplacedIn a b do
                    entries.Add(StackComment(fst (placement.Place i), true))
            let mutable previous = start
            for run in runs do
                placeBetween previous items[List.head run].Span.Start
                for k in run |> List.sortWith (fun a b -> ImportOrder.compare items[a] items[b]) do
                    placeBetween items[k].Span.Start items[k].Span.End
                    match unplacedIn items[k].Span.End (lineFinish k) with
                    | i :: _ ->
                        entries.Add(StackItem(cat [ item items[k]; Doc.trailingComment (fst (placement.Place i)) ], false))
                    | [] -> entries.Add(StackItem(item items[k], false))
                previous <- lineFinish (List.last run)
            placeBetween previous finish
            cat [ text "import ("; nest (cat [ hard; Stack(false, List.ofSeq entries) ]); hard; text ")" ]

    member _.ImportSource(source: ImportSource) : Doc = importSource source

    member _.ImportSelection(names: (Identifier * SourceSpan) list) : Doc = importSelection names

    /// The import made of `items`, between the offsets `start` and `finish`.
    member _.ImportDoc (start: int) (finish: int) (items: ImportItem list) : Doc = importDoc start finish items
