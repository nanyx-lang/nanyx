/// Doc comments: a comment that starts with `--|` on a line of its own, and goes on for the lines after it that are
/// indented further (see `lineComment`). It documents what is written on the first line with code after it. The
/// parser notes where every comment is, so a doc comment is found, and attached to what it documents, by position.
[<AutoOpen>]
module Parser.Program.DocCommentParsing

open System
open Parser.Program

/// What a doc comment documents.
type DocTarget =
    /// A declaration: a `def` (at the top level or in a block), a `type` or `context`, a field of a record type or a
    /// member of a context. The span is that of the name it declares, as `DeclaredNames` has it.
    | DocumentsDeclaration of SourceSpan
    /// A tag of a union type. The span is that of the tag, from its `#`.
    | DocumentsTag of SourceSpan
    /// The module: a doc comment at the top of the file, before `module` or the first import.
    | DocumentsModule
    /// Nothing: the code after the comment is not something a doc comment can document.
    | DocumentsNothing

type DocComment =
    { /// The text, as Markdown: the comment without its `--|`, its continuation lines without the indent they share.
      Text: string
      /// The whole comment, from its `--|` to the end of its last line.
      Span: SourceSpan
      Target: DocTarget }

/// The doc comments of a file, and the names its top level declares, by which the names a doc comment links to
/// (`` [`name`] ``) can be found.
type FileDocComments =
    { Comments: DocComment list
      /// Each name the file's top level declares (a def, a type, a context), with the span it is declared at.
      TopLevel: (Identifier * SourceSpan) list }

module DocComments =
    /// The lines of a doc comment as written, from its `--|`: the text on its first line after the `--|` (without the
    /// space after it), and each continuation line without the indent the continuation lines share. A blank line is
    /// empty.
    let lines (written: string) : string * string list =
        match written.Replace("\r\n", "\n").Split '\n' |> List.ofArray with
        | [] -> "", []
        | first :: rest ->
            let first = first.Substring(min 3 first.Length).TrimEnd()
            let first = if first.StartsWith " " then first.Substring 1 else first
            let rest = rest |> List.map (fun line -> line.TrimEnd())
            let indentOf (line: string) = line.Length - line.TrimStart(' ', '\t').Length
            let shared =
                match rest |> List.filter (String.IsNullOrWhiteSpace >> not) with
                | [] -> 0
                | written -> written |> List.map indentOf |> List.min
            first, rest |> List.map (fun line -> if line.Length = 0 then line else line.Substring shared)

    /// The text of a doc comment as written, as Markdown (see `DocComment.Text`).
    let markdown (written: string) : string =
        let first, rest = lines written
        String.concat "\n" (first :: rest) |> fun text -> text.Trim('\n')

    /// The lines of the doc comment as written, laid out with each continuation line `indent` spaces past the `--|`
    /// line: the text and its line breaks are kept, and the continuation lines keep their indents relative to each other.
    let relaidOutLines (indent: int) (written: string) : string list =
        let first, rest = lines written
        let firstLine = if first.Length = 0 then "--|" else "--| " + first
        firstLine :: (rest |> List.map (fun line -> if line.Length = 0 then line else String(' ', indent) + line))

    /// The doc comment as written, laid out as `relaidOutLines` lays it out.
    let relaidOut (indent: int) (written: string) : string = String.concat "\n" (relaidOutLines indent written)

    /// The doc comments among `comments` (as the parser notes them in `source`), each attached to what it documents in
    /// `items`, the module as parsed, whose declarations are `declared`. A doc comment documents the name or tag
    /// declared by the construct that starts where the code after it starts, as `documentableStarts` notes them (see
    /// `noteDocumentable`), or the one that starts there itself.
    let attach
        (filePath: string)
        (source: string)
        (items: TopLevelItem list)
        (declared: DeclaredNames)
        (documentableStarts: Map<int, int>)
        (comments: NotedComment list)
        : FileDocComments =
        let lines = LineTable source

        let declarations =
            declared
            |> Map.toList
            |> List.collect (fun ((_, declarer), names) ->
                match declarer with
                | DefValue
                | TypeDefBody -> names |> List.truncate 1
                | RecordTypeFields -> names
                | LambdaParameters
                | ArmBody
                | NamedFieldValue -> [])
            |> List.map (fun (_, span) -> DocumentsDeclaration span, span.Start)
        let rec typeDefBodies (item: TopLevelItem) =
            match item.Kind with
            | Def (TypeDef(_, _, _, body)) -> [ body ]
            | ModuleBlock(_, items) -> items |> List.collect typeDefBodies
            | _ -> []
        let tags =
            let rec walk (typeExpr: TypeExpr) =
                match typeExpr.Kind with
                | TypeTag _ when not (SourceSpan.isNone typeExpr.Span) -> [ DocumentsTag typeExpr.Span, typeExpr.Span.Start ]
                | _ -> TypeExpr.children typeExpr |> List.collect walk
            items |> List.collect typeDefBodies |> List.collect walk
        let targets = declarations @ tags

        // The first item written but for attributes (as `codeAfter` skips them), which a doc comment before it
        // documents the module by if it is `module` or an import: `@partial module prelude` is a module declaration.
        let firstItem =
            items
            |> List.filter (fun item ->
                not (SourceSpan.isNone item.Span)
                && (match item.Kind with
                    | Attribute _ -> false
                    | _ -> true))
            |> List.sortBy _.Span.Start
            |> List.tryHead

        // Where the code after `offset` starts: past blank lines and `@attribute` lines. `None` at the end of the file.
        let rec codeAfter offset =
            let mutable i = offset
            while i < source.Length && Char.IsWhiteSpace source[i] do
                i <- i + 1
            if i >= source.Length then None
            elif source[i] = '@' then
                match source.IndexOf('\n', i) with
                | -1 -> None
                | lineEnd -> codeAfter lineEnd
            else Some i

        // A comment before the first item documents the module if that is `module` or an import: the code after it is
        // that item, or what follows the line `@partial module name`, which `codeAfter` skips as an attribute's.
        let targetAfter (commentEnd: int) =
            match codeAfter commentEnd, firstItem with
            | None, _ -> DocumentsNothing
            | Some code, Some first when
                commentEnd <= first.Span.Start
                && code >= first.Span.Start
                && (match first.Kind with ModuleDecl _ | Import _ -> true | _ -> false) ->
                DocumentsModule
            | Some code, _ ->
                let declaredAt = documentableStarts |> Map.tryFind code |> Option.defaultValue code
                targets
                |> List.tryFind (fun (_, start) -> start = declaredAt)
                |> Option.map fst
                |> Option.defaultValue DocumentsNothing

        let docs =
            comments
            |> List.filter _.IsDoc
            |> List.map (fun comment -> comment.Start, comment.End)
            |> List.distinct
            |> List.sortBy fst
            |> List.map (fun (start, finish) ->
                { Text = markdown (source.Substring(start, finish - start))
                  Span = lines.Span filePath start finish
                  Target = targetAfter finish })
        let rec topLevel (item: TopLevelItem) =
            match item.Kind with
            | Def (ValueDef(_, _, _, value)) -> declared |> Map.tryFind (value.Id, DefValue) |> Option.toList |> List.collect (List.truncate 1)
            | Def (TypeDef(_, _, _, body)) -> declared |> Map.tryFind (body.Id, TypeDefBody) |> Option.toList |> List.collect (List.truncate 1)
            | ModuleBlock(_, items) -> items |> List.collect topLevel
            | _ -> []
        { Comments = docs; TopLevel = items |> List.collect topLevel }

    /// The doc comments of a file that does not parse: none.
    let none: FileDocComments = { Comments = []; TopLevel = [] }
