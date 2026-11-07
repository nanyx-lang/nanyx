namespace Nanyx.Formatter

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

type private Mode =
    | Flat
    | Break

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
        | Group doc -> hasHardLine doc
        | Concat docs -> List.exists hasHardLine docs
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

    /// Whether a group laid out flat fits in `remaining` columns, together with whatever follows it on the same line.
    /// `current` is the group's own content, which must all be flat; `rest` is what follows it, where the first line
    /// break that is taken ends the line. A group in `rest` is assumed flat, as Prettier assumes, so a line may break
    /// a little earlier than it strictly has to, but never later.
    ///
    /// The walk stops once the line is full, so it costs at most the line width for each group, not the size of the
    /// document.
    let rec private fits remaining (current: (int * Doc) list) (rest: (int * Mode * Doc) list) =
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
                | Group doc -> fits remaining ((indent, doc) :: current) rest
                | Concat docs -> fits remaining ((docs |> List.map (fun doc -> indent, doc)) @ current) rest
                | Verbatim s ->
                    match firstLine s with
                    | _, true -> false
                    | length, false -> fits (remaining - length) current rest
                | LineSuffix _ -> fits remaining current rest
            | [] ->
                match rest with
                | [] -> true
                | (indent, mode, doc) :: rest ->
                    match doc, mode with
                    | Text s, _ -> fits (remaining - s.Length) [] rest
                    | (Line | SoftLine), Break
                    | HardLine, _ -> true
                    | Line, Flat -> fits (remaining - 1) [] rest
                    | SoftLine, Flat -> fits remaining [] rest
                    | Nest(n, doc), _ -> fits remaining [] ((indent + n, mode, doc) :: rest)
                    | Group doc, _ -> fits remaining [] ((indent, Flat, doc) :: rest)
                    | Concat docs, _ -> fits remaining [] ((docs |> List.map (fun doc -> indent, mode, doc)) @ rest)
                    | Verbatim s, _ ->
                        match firstLine s with
                        | length, true -> remaining - length >= 0
                        | length, false -> fits (remaining - length) [] rest
                    | LineSuffix _, _ -> fits remaining [] rest

    /// Lays the document out in `width` columns. No line ends in spaces, and the output ends with exactly one line
    /// break (an empty document renders as nothing).
    let render (width: int) (doc: Doc) : string =
        let out = StringBuilder()
        let mutable column = 0
        // The indent of the current line, written only once text arrives, so that empty lines get none.
        let mutable pendingIndent = 0
        // The line suffixes waiting for the end of the current line.
        let suffixes = ResizeArray<string>()

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

        let rec go (commands: (int * Mode * Doc) list) =
            match commands with
            | [] -> ()
            | (indent, mode, doc) :: rest ->
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
                | Nest(n, doc) -> go ((indent + n, mode, doc) :: rest)
                | Group doc ->
                    let mode = if mode = Flat || fits (width - column) [ indent, doc ] rest then Flat else Break
                    go ((indent, mode, doc) :: rest)
                | Concat docs -> go ((docs |> List.map (fun doc -> indent, mode, doc)) @ rest)
                | Verbatim s ->
                    writeVerbatim s
                    go rest
                | LineSuffix s ->
                    suffixes.Add s
                    go rest

        go [ 0, Break, doc ]
        flushSuffixes 0

        let rendered = out.ToString().TrimEnd(' ', '\n')
        if rendered.Length = 0 then "" else rendered + "\n"
