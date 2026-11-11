namespace Nanyx.Formatter

open System.Collections.Generic
open System.Text

/// A document for the formatter's printer: text, and the places where it may break. `Doc.render` lays it out to fit a
/// line width. This is Wadler's "prettier printer", the approach Prettier and Elm's formatter take: each construct is
/// described once as a document, and the printer decides where the lines break.
type Doc =
    /// Text with no line breaks.
    | Text of string
    /// A space when its group is laid out flat, a line break when the group is broken.
    | Line
    /// Nothing when its group is laid out flat, a line break when the group is broken.
    | SoftLine
    /// Always a line break. A group containing one cannot be flat, so it breaks, and so does every group around it.
    | HardLine
    /// Indents the document's line breaks by this many more columns than the enclosing indent. Indent is measured from
    /// the start of the line, never from a column inside it.
    | Nest of int * Doc
    /// Laid out flat if it fits in the rest of the line, otherwise with its own line breaks broken. The groups inside
    /// it decide for themselves.
    | Group of Doc
    | Concat of Doc list
    /// Text written exactly as it is, line breaks included, with no indent added after them: a string literal that
    /// spans lines. A group containing line breaks this way cannot be flat.
    | Verbatim of string
    /// Text written at the end of the current line, just before its line break: a trailing comment. Whatever follows
    /// it on the same line is written before it, so it never comments out code. It takes no width.
    | LineSuffix of string
    /// `broken` when the enclosing group is broken, `flat` when it is flat: a separator that is a comma on one line
    /// and nothing between lines, say.
    | IfBreak of broken: Doc * flat: Doc
    /// A group that a later part of the document follows with `IfGroupBroken`.
    | NamedGroup of id: int * Doc
    /// `broken` if the named group `id`, laid out earlier, was broken, `flat` if not.
    | IfGroupBroken of id: int * broken: Doc * flat: Doc
    /// Items and the separators between them, alternating: item, separator, item, ... Each separator is laid out flat
    /// if the item after it fits on the rest of the line, and broken otherwise, so as many items go on each line as fit.
    | Fill of Doc list
    /// Constructs on lines of their own, with comments among them. With `blankLines`, a construct that takes more than
    /// one line, laid out where it is, gets a blank line before it (and its leading comments) and after it, except at
    /// the start and the end and between two imports; elsewhere the author's blank lines are kept, one for any number.
    /// Whether a construct takes more than one line is measured at its own indent and the line width, so a construct
    /// that breaks because it is long counts as well as one that always breaks.
    | Stack of blankLines: bool * StackEntry list

and StackEntry =
    | StackItem of Doc * isImport: bool
    /// A comment on a line of its own. A `leading` one belongs to the item after it. A block comment is written as it
    /// is, its line breaks included.
    | StackComment of string * leading: bool
    /// A blank line the author wrote.
    | StackBlank

type private Mode =
    | Flat
    | Break

/// What the renderer has left to do: lay out a document in a mode, or decide the next separator of a fill.
type private Command =
    | Print of indent: int * Mode * Doc
    | FillNext of indent: int * Doc list

module Doc =
    let empty = Concat []

    let text (s: string) =
        if s.Contains '\n' then invalidArg "s" $"Text cannot contain a line break: %A{s}"
        Text s

    let line = Line
    let softLine = SoftLine
    let hardLine = HardLine

    let nest indent doc = Nest(indent, doc)
    let group doc = Group doc
    let concat docs = Concat docs

    /// `s` as text, or verbatim when it contains line breaks.
    let textOrVerbatim (s: string) = if s.Contains '\n' then Verbatim s else Text s

    let lineSuffix (s: string) =
        if s.Contains '\n' then invalidArg "s" $"A line suffix cannot contain a line break: %A{s}"
        LineSuffix s

    /// Whether the document always contains a line break, however it is laid out.
    let rec hasHardLine (doc: Doc) =
        match doc with
        | HardLine -> true
        | Verbatim s -> s.Contains '\n'
        | Nest(_, doc)
        | Group doc
        | NamedGroup(_, doc) -> hasHardLine doc
        | Concat docs
        | Fill docs -> List.exists hasHardLine docs
        | IfBreak(broken, flat)
        | IfGroupBroken(_, broken, flat) -> hasHardLine broken && hasHardLine flat
        | Stack(_, entries) ->
            List.length entries > 1 || entries |> List.exists (function StackItem(doc, _) -> hasHardLine doc | _ -> false)
        | Text _
        | Line
        | SoftLine
        | LineSuffix _ -> false

    /// The length of `s` up to its first line break, and whether it has one.
    let private firstLine (s: string) =
        match s.IndexOf '\n' with
        | -1 -> s.Length, false
        | index -> index, true

    /// The documents, with `separator` between each one and the next.
    let join (separator: Doc) (docs: Doc list) =
        match docs with
        | [] -> empty
        | first :: rest -> Concat(first :: (rest |> List.collect (fun doc -> [ separator; doc ])))

    /// The stack's entries as documents with the line breaks and blank lines between them, given whether each item
    /// takes more than one line.
    let private stackLines (blankLines: bool) (entries: StackEntry list) (isMultiLine: Doc -> bool) : Doc list =
        let entries = Array.ofList entries
        let multiLine = entries |> Array.map (function StackItem(doc, _) -> isMultiLine doc | _ -> false)
        let isItem k = match entries[k] with StackItem _ -> true | _ -> false
        let isImport k = match entries[k] with StackItem(_, isImport) -> isImport | _ -> false
        // An item is set apart from the item before it when either takes more than one line, unless both are imports.
        let separated previous next =
            match previous, next with
            | Some p, Some n -> (multiLine[p] || multiLine[n]) && not (isImport p && isImport n)
            | Some p, None -> multiLine[p]
            | _ -> false
        let parts = ResizeArray<Doc>()
        let mutable emitted = false
        let mutable blank = false
        let mutable previousItem = None
        let mutable separatedNext = false
        let emit doc =
            if emitted then
                parts.Add HardLine
                if blank then parts.Add HardLine
            parts.Add doc
            emitted <- true
            blank <- false
        for k in 0 .. entries.Length - 1 do
            match entries[k] with
            | StackBlank -> if blankLines && emitted then blank <- true
            | StackComment(text, leading) ->
                if leading && not separatedNext then
                    let next = seq { k + 1 .. entries.Length - 1 } |> Seq.tryFind isItem
                    if blankLines && emitted && separated previousItem next then blank <- true
                    separatedNext <- true
                emit (textOrVerbatim text)
            | StackItem(doc, _) ->
                if not separatedNext && blankLines && emitted && separated previousItem (Some k) then blank <- true
                emit doc
                previousItem <- Some k
                separatedNext <- false
        List.ofSeq parts

    /// Lays the document out in `width` columns. No line ends in spaces, and the output ends with exactly one line
    /// break (an empty document renders as nothing).
    let render (width: int) (doc: Doc) : string =
        let out = StringBuilder()
        let mutable column = 0
        // The indent of the current line, written only once text arrives, so that empty lines get none.
        let mutable pendingIndent = 0
        // The line suffixes waiting for the end of the current line.
        let suffixes = ResizeArray<string>()
        // How each named group was laid out.
        let groups = Dictionary<int, Mode>()
        let groupMode id = match groups.TryGetValue id with | true, mode -> mode | _ -> Flat

        /// Whether a group laid out flat fits in `remaining` columns, together with whatever follows it on the same
        /// line. `current` is the group's own content, which must all be flat; `rest` is what follows it, where the
        /// first line break that may be taken ends the line: a group in `rest` is taken to break where it can, as
        /// Prettier takes it, so `def xs: list(int) = [` is measured up to its `[`, not to the end of the list. The
        /// walk stops once the line is full, so it costs at most the line width for each group, not the size of the
        /// document.
        let rec fits remaining (current: (int * Doc) list) (rest: Command list) =
            if remaining < 0 then
                false
            else
                match current with
                | (indent, doc) :: current ->
                    match doc with
                    | Text s -> fits (remaining - s.Length) current rest
                    | Line -> fits (remaining - 1) current rest
                    | SoftLine -> fits remaining current rest
                    | HardLine -> false
                    | Nest(n, doc) -> fits remaining ((indent + n, doc) :: current) rest
                    | Group doc
                    | NamedGroup(_, doc) -> fits remaining ((indent, doc) :: current) rest
                    | Concat docs
                    | Fill docs -> fits remaining ((docs |> List.map (fun doc -> indent, doc)) @ current) rest
                    | Verbatim s ->
                        match firstLine s with
                        | _, true -> false
                        | length, false -> fits (remaining - length) current rest
                    | LineSuffix _ -> fits remaining current rest
                    | IfBreak(_, flat) -> fits remaining ((indent, flat) :: current) rest
                    | IfGroupBroken(id, broken, flat) ->
                        fits remaining ((indent, (if groupMode id = Break then broken else flat)) :: current) rest
                    | Stack(_, [ StackItem(doc, _) ]) -> fits remaining ((indent, doc) :: current) rest
                    | Stack(_, []) -> fits remaining current rest
                    | Stack _ -> false
                | [] ->
                    match rest with
                    | [] -> true
                    // A fill's next separator breaks unless what follows it fits, so the line ends there or fits.
                    | FillNext _ :: _ -> true
                    | Print(indent, mode, doc) :: rest ->
                        match doc, mode with
                        | Text s, _ -> fits (remaining - s.Length) [] rest
                        | (Line | SoftLine), Break
                        | HardLine, _ -> true
                        | Line, Flat -> fits (remaining - 1) [] rest
                        | SoftLine, Flat -> fits remaining [] rest
                        | Nest(n, doc), _ -> fits remaining [] (Print(indent + n, mode, doc) :: rest)
                        | (Group doc | NamedGroup(_, doc)), _ -> fits remaining [] (Print(indent, mode, doc) :: rest)
                        | Concat docs, _ -> fits remaining [] ((docs |> List.map (fun doc -> Print(indent, mode, doc))) @ rest)
                        | Fill docs, Flat -> fits remaining [] ((docs |> List.map (fun doc -> Print(indent, Flat, doc))) @ rest)
                        | Fill docs, Break ->
                            match docs with
                            | first :: more -> fits remaining [] (Print(indent, Break, first) :: FillNext(indent, more) :: rest)
                            | [] -> fits remaining [] rest
                        | Verbatim s, _ ->
                            match firstLine s with
                            | length, true -> remaining - length >= 0
                            | length, false -> fits (remaining - length) [] rest
                        | LineSuffix _, _ -> fits remaining [] rest
                        | IfBreak(broken, flat), _ -> fits remaining [] (Print(indent, mode, (if mode = Break then broken else flat)) :: rest)
                        | IfGroupBroken(id, broken, flat), _ ->
                            fits remaining [] (Print(indent, mode, (if groupMode id = Break then broken else flat)) :: rest)
                        | Stack(_, entries), _ ->
                            match entries with
                            | StackItem(doc, _) :: more -> fits remaining [] (Print(indent, Break, doc) :: (if more.IsEmpty then rest else Print(indent, Break, HardLine) :: rest))
                            | StackComment(text, _) :: _ -> remaining - fst (firstLine text) >= 0
                            | StackBlank :: more -> fits remaining [] (Print(indent, mode, Stack(false, more)) :: rest)
                            | [] -> fits remaining [] rest

        let trimLine () =
            while out.Length > 0 && out[out.Length - 1] = ' ' do
                out.Length <- out.Length - 1

        let write (s: string) =
            if s.Length > 0 then
                if s.Contains '\n' then invalidArg "doc" $"Text cannot contain a line break: %A{s}"
                out.Append(' ', pendingIndent).Append(s) |> ignore
                pendingIndent <- 0
                column <- column + s.Length

        let writeVerbatim (s: string) =
            match s.LastIndexOf '\n' with
            | -1 -> write s
            | last ->
                out.Append(' ', pendingIndent).Append(s) |> ignore
                pendingIndent <- 0
                column <- s.Length - last - 1

        // The first suffix goes at the end of the line; any more (two lines joined into one) each get a line of their
        // own, at the indent of the line that follows.
        let flushSuffixes indent =
            suffixes
            |> Seq.iteri (fun index suffix ->
                trimLine ()
                if index > 0 then out.Append('\n').Append(' ', indent) |> ignore
                elif out.Length > 0 && out[out.Length - 1] <> '\n' then out.Append(' ') |> ignore
                out.Append(suffix) |> ignore)
            suffixes.Clear()

        let newLine indent =
            flushSuffixes indent
            trimLine ()
            out.Append('\n') |> ignore
            pendingIndent <- indent
            column <- indent

        let rec go (commands: Command list) =
            match commands with
            | [] -> ()
            | FillNext(indent, separator :: item :: more) :: rest ->
                let itemFits = fits (width - column) [ indent, separator; indent, item ] []
                let mode = if itemFits then Flat else Break
                go (Print(indent, mode, separator) :: Print(indent, mode, item) :: (if more.IsEmpty then rest else FillNext(indent, more) :: rest))
            | FillNext _ :: rest -> go rest
            | Print(indent, mode, doc) :: rest ->
                match doc with
                | Text s ->
                    write s
                    go rest
                | Line ->
                    if mode = Flat then write " " else newLine indent
                    go rest
                | SoftLine ->
                    if mode = Break then newLine indent
                    go rest
                | HardLine ->
                    newLine indent
                    go rest
                | Nest(n, doc) -> go (Print(indent + n, mode, doc) :: rest)
                | Group doc ->
                    let mode = if mode = Flat || fits (width - column) [ indent, doc ] rest then Flat else Break
                    go (Print(indent, mode, doc) :: rest)
                | NamedGroup(id, doc) ->
                    let mode = if mode = Flat || fits (width - column) [ indent, doc ] rest then Flat else Break
                    groups[id] <- mode
                    go (Print(indent, mode, doc) :: rest)
                | Concat docs -> go ((docs |> List.map (fun doc -> Print(indent, mode, doc))) @ rest)
                | Fill docs ->
                    match mode, docs with
                    | Flat, _ -> go ((docs |> List.map (fun doc -> Print(indent, Flat, doc))) @ rest)
                    | Break, first :: more -> go (Print(indent, Break, first) :: FillNext(indent, more) :: rest)
                    | Break, [] -> go rest
                | Verbatim s ->
                    writeVerbatim s
                    go rest
                | LineSuffix s ->
                    suffixes.Add s
                    go rest
                | IfBreak(broken, flat) -> go (Print(indent, mode, (if mode = Break then broken else flat)) :: rest)
                | IfGroupBroken(id, broken, flat) -> go (Print(indent, mode, (if groupMode id = Break then broken else flat)) :: rest)
                | Stack(blankLines, entries) ->
                    let isMultiLine doc = hasHardLine doc || not (fits (width - indent) [ indent, doc ] [])
                    go ((stackLines blankLines entries isMultiLine |> List.map (fun doc -> Print(indent, Break, doc))) @ rest)

        go [ Print(0, Break, doc) ]
        flushSuffixes 0

        let rendered = out.ToString().TrimEnd(' ', '\n')
        if rendered.Length = 0 then "" else rendered + "\n"
