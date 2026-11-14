/// Formatting part of a file, for an editor's "Format Selection": the file is formatted whole, with the same safety
/// check as `nanyx format`, and only the top-level items the selection touches are replaced with their formatted text.
module Nanyx.Formatter.RangeFormatting

open Parser.Program
open Nanyx.Formatter.FormatCommand

/// A change to the text: what is from `Start` to `End`, offsets into the text with its line endings made `\n`, becomes
/// `Text`, whose line endings are `\n`.
type Edit = { Start: int; End: int; Text: string }

/// The lines of `text` (with `\n` line endings) from the one `items`' first starts on to the one their last ends on:
/// the offsets where they start and end, and their text.
let private linesOf (text: string) (items: TopLevelItem list) =
    let start =
        match (List.head items).Span.Start with
        | 0 -> 0
        | itemStart -> text.LastIndexOf('\n', itemStart - 1) + 1
    let finish =
        match text.IndexOf('\n', (List.last items).Span.End) with
        | -1 -> text.Length
        | lineEnd -> lineEnd
    start, finish, text.Substring(start, finish - start)

/// The edits that format the top-level items of `text`, the source of the file at `path`, that the range from `start`
/// to `finish` touches, offsets into the text with its line endings made `\n`: the lines from the first one's to the
/// last one's become the same items' lines in the formatted file. The comments and blank lines around them are left
/// as they are. The whole file is formatted when the formatter changes how many items there are, as it does when it
/// joins imports. None when the range touches no item or they are formatted already; the reason, as `formatText`
/// gives it, when the file does not parse or could not be formatted safely.
let formatRange (options: Formatter.Options) (path: string) (text: string) (start: int) (finish: int) : Result<Edit list, Formatting> =
    match formatText options path text with
    | Formatted formatted ->
        let original = text.Replace("\r\n", "\n").Replace('\r', '\n')
        let formatted = formatted.Replace("\r\n", "\n")
        let itemsOf (source: string) =
            match parseModuleWithTrivia path source with
            | Result.Ok parsed -> parsed.Items
            | Result.Error _ -> []
        let originalItems, formattedItems = itemsOf original, itemsOf formatted
        let touched =
            originalItems |> List.indexed |> List.filter (fun (_, item) -> item.Span.Start <= finish && start <= item.Span.End) |> List.map fst
        let edit =
            match touched with
            | [] -> None
            | _ when originalItems.Length <> formattedItems.Length ->
                if formatted = original then None else Some { Start = 0; End = original.Length; Text = formatted }
            | indices ->
                let first, last = List.head indices, List.last indices
                let between (items: TopLevelItem list) = items[first..last]
                let originalStart, originalEnd, originalLines = linesOf original (between originalItems)
                let _, _, formattedLines = linesOf formatted (between formattedItems)
                if originalLines = formattedLines then None
                else Some { Start = originalStart; End = originalEnd; Text = formattedLines }
        Result.Ok (Option.toList edit)
    | failed -> Result.Error failed
