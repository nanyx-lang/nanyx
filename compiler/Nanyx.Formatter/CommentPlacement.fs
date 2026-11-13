/// Putting a module's comments back as the formatter lays it out.
module internal Nanyx.Formatter.CommentPlacement

open System
open Parser.Program
open Nanyx.Formatter.SourceText

/// The comments of `parsed` and the author's blank lines, and which comments the layout has placed so far. Comments are
/// placed once each; whatever no construct places goes before the line it was in. A doc comment's continuation lines
/// are set `indentSize` past its first line. The comments in the header, before `headerEnd`, are kept with it.
type CommentPlacement(parsed: SourceModule, indentSize: int, sourceText: SourceText, headerEnd: int) =
    let cat = Doc.concat

    let comments =
        parsed.Trivia
        |> List.choose (function
            | LineComment(commentText, _, span) when commentText.StartsWith "--|" ->
                Some(DocComments.relaidOut indentSize commentText, span)
            | LineComment(commentText, _, span) -> Some(commentText.TrimEnd(), span)
            | BlankLines _ -> None)
        |> Array.ofList

    let placed = comments |> Array.map (fun (_, span) -> span.Start < headerEnd)

    let blankRuns =
        parsed.Trivia
        |> List.choose (function
            | BlankLines(_, span) -> Some span
            | LineComment _ -> None)

    let unplacedIn (start: int) (finish: int) =
        [ for i in 0 .. comments.Length - 1 do
              let span = snd comments[i]
              if not placed[i] && start <= span.Start && span.Start < finish then yield i ]

    let sequenceOf (blankLines: bool) (start: int) (finish: int) (children: (int * int * bool * (unit -> Doc)) list) : Doc =
        let entries = ResizeArray<StackEntry>()

        let placeComment leading i =
            placed[i] <- true
            entries.Add(StackComment(fst comments[i], leading))

        // The comments, and the author's blank lines, between the offsets `a` and `b`, in order.
        let between a b =
            let blanks =
                if blankLines then
                    blankRuns |> List.filter (fun span -> a <= span.Start && span.Start < b) |> List.map (fun span -> span.Start, None)
                else
                    []
            let inBetween = unplacedIn a b |> List.map (fun i -> (snd comments[i]).Start, Some i)
            for _, entry in List.sortBy fst (blanks @ inBetween) do
                match entry with
                | Some i -> placeComment true i
                | None -> entries.Add StackBlank

        let mutable previous = start
        for childStart, childEnd, isImport, build in children do
            let childStart = max childStart previous
            let childEnd = max childEnd childStart
            between previous childStart
            let placedBefore = Array.copy placed
            let doc = build ()
            let firstPlacedByChild =
                seq { for i in 0 .. comments.Length - 1 do if placed[i] && not placedBefore[i] then yield (snd comments[i]).Start }
                |> Seq.fold min Int32.MaxValue
            let orphans = unplacedIn childStart childEnd
            let before, after = orphans |> List.partition (fun i -> (snd comments[i]).Start < firstPlacedByChild)
            for i in before do
                placeComment true i
            let trailing =
                match unplacedIn childEnd (sourceText.LineEnd (max childStart (childEnd - 1)) + 1) with
                | i :: _ when childEnd > childStart -> Some i
                | _ -> None
            match trailing with
            | Some i when after.IsEmpty ->
                placed[i] <- true
                entries.Add(StackItem(cat [ doc; Doc.lineSuffix (fst comments[i]) ], isImport))
            | _ ->
                entries.Add(StackItem(doc, isImport))
                for i in after do
                    placeComment false i
                trailing |> Option.iter (placeComment false)
            previous <- childEnd
        between previous finish
        Stack(blankLines, List.ofSeq entries)

    /// The spans of the runs of blank lines the author wrote.
    member _.BlankRuns = blankRuns

    /// The comments not yet placed that start between the offsets `start` and `finish`, by index.
    member _.UnplacedIn(start, finish) = unplacedIn start finish

    /// Whether a comment not yet placed starts between the offsets `start` and `finish`.
    member _.HasCommentsIn(start, finish) = not (List.isEmpty (unplacedIn start finish))

    /// Marks the comment at `index` placed, and gives its text and span.
    member _.Place(index: int) =
        placed[index] <- true
        comments[index]

    /// Children laid out one per line between the offsets `start` and `finish`, each given as its start and end offsets
    /// and a function building its document. The comments among them are put back: a comment after a child on its
    /// last line stays at the end of that line, and any other on a line of its own. A comment inside a child that the
    /// child does not place itself goes on a line of its own before it, or after it when the child placed comments
    /// that come before it, so that comments stay in order.
    ///
    /// With `blankLines`, for where the parser allows blank lines between children (the top level, a module block and
    /// a block of statements, but not a list of imports): a child that spans more than one line gets a blank line
    /// before it (and its leading comments) and after it, except at the start and end and between two imports;
    /// between children that each fit on one line, a blank line (however many there were) is kept, and none is added.
    /// Which children span lines is decided when the document is laid out (see `Doc.Stack`), so a child that breaks
    /// because it is long counts too.
    member _.Sequence (blankLines: bool) (start: int) (finish: int) (children: (int * int * (unit -> Doc)) list) : Doc =
        sequenceOf blankLines start finish (children |> List.map (fun (childStart, childEnd, build) -> childStart, childEnd, false, build))

    /// `Sequence`, with each child saying whether it is an import.
    member _.SequenceOf (blankLines: bool) (start: int) (finish: int) (children: (int * int * bool * (unit -> Doc)) list) : Doc =
        sequenceOf blankLines start finish children
