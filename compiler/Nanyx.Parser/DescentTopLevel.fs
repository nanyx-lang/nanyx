/// The recursive-descent parser's top level: a module's items (`TopLevel.fs`), its declaration and its `module name =`
/// blocks, imports and re-exports, attributes, type and context definitions, pattern definitions, defs and
/// declarations, destructuring defs and a script's expressions, read from the layout pass's tokens of the text with
/// its directives blanked. It reads a whole file, and gives up on the file at anything it doesn't read.
module Parser.Program.DescentTopLevel

open System
open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor
open Parser.Program.DescentExpressions
open Parser.Program.DescentBlocks

/// A name of a module path, `collections.list` or `./util/strings`, as `Lexical.modulePath` reads it: names joined by
/// `.` or `/` written with no spaces.
let private modulePath (c: Cursor) : string =
    let head, _ = identifierWithSpan c
    let parts = Text.StringBuilder(head)
    let mutable going = true
    while going do
        if c.Adjacent && (c.Is Dot || c.Is(Operator, "/")) && (match c.At 1 with Code next -> (next.Kind = Name || next.Kind = Keyword) && next.Start = c.Token.Value.End | _ -> false) then
            let separator = c.Text(c.Take())
            parts.Append(separator).Append(fst (identifierWithSpan c)) |> ignore
        else going <- false
    parts.ToString()

/// Whether the line ends at the cursor: the next code token is on a later line, or there is none
/// (`followedBy (newline <|> eof)`).
let private lineEnds (c: Cursor) = c.Is EndOfFile || not c.OnSameLine

/// Where an import comes from: a string, a file's path, or a module's path (`Lexical.importSource`).
let private importSource (c: Cursor) : ImportSource =
    match c.Token with
    | Some token when token.Kind = StringLiteral ->
        match fst (DescentLiterals.stringLiteral c) with
        | StringLit path -> PathImport path
        | _ -> raise NoMatch
    | _ -> ModuleImport(modulePath c)

/// A name qualified with dots, any part a type's or in backticks, and its span (`ParseState.qualifiedIdentifier`).
let private qualified (c: Cursor) : Identifier * SourceSpan =
    let start = c.Start
    let parts = ResizeArray [ fst (name c) ]
    while c.Adjacent && c.Is Dot do
        c.Take() |> ignore
        parts.Add(fst (name c))
    String.concat "." parts, c.SpanFrom start

/// The names an import selects, in brackets, separated by commas or line breaks (`Lexical.importSelection`).
let private importSelection (c: Cursor) : (Identifier * SourceSpan) list =
    c.Expect LeftParen |> ignore
    let names = ResizeArray [ qualified c ]
    while not (c.Is RightParen) do
        c.Skip Comma |> ignore
        names.Add(qualified c)
    c.Expect RightParen |> ignore
    List.ofSeq names

/// One import: its source, then `as` and an alias, a selection, or both (`Lexical.importItem`).
let private importItem (c: Cursor) : ImportItem =
    let start = c.Start
    let source = importSource c
    let alias, selection =
        if c.IsKeyword "as" then
            c.Take() |> ignore
            if c.Is LeftParen then None, Some(importSelection c)
            else
                let alias, _ = identifierWithSpan c
                let selection =
                    if c.Is Ampersand then
                        c.Take() |> ignore
                        Some(importSelection c)
                    else None
                Some alias, selection
        else None, None
    { Source = source; Alias = alias; Selection = selection; Span = c.SpanFrom start }

/// What follows `import`: one item, or several in brackets, each on its own (`Lexical.importItems`).
let importItems (c: Cursor) : ImportItem list =
    let several () =
        c.Expect LeftParen |> ignore
        let items = ResizeArray [ importItem c ]
        while not (c.Is RightParen) do
            items.Add(importItem c)
        c.Expect RightParen |> ignore
        List.ofSeq items
    match c.Attempt several with
    | Some items -> items
    | None -> [ importItem c ]

/// What follows `export` in a re-export: a module, and the names it exports that are exported again, on its line, or
/// the module alone on its line (`Lexical.reExportItem`).
let private reExportItem (c: Cursor) : ImportItem =
    let start = c.Start
    let source = importSource c
    let selection =
        if c.Is LeftParen && c.OnSameLine then Some(importSelection c)
        elif lineEnds c then None
        else raise NoMatch
    { Source = source; Alias = None; Selection = selection; Span = c.SpanFrom start }

/// A type's or a context's parameter, a single lowercase letter, with a bound after `::` if it has one
/// (`Types.typeParam`).
let private typeParameter (c: Cursor) : Identifier * TypeExpr option =
    let parameter, _ = identifierWithSpan c
    if not (Names.isTypeVariable parameter) then c.GiveUp "a type parameter that isn't a single lowercase letter, which is a mistake"
    if c.Is DoubleColon then
        c.Take() |> ignore
        parameter, Some(DescentTypes.typeExpr c)
    else parameter, None

/// A type's parameters in brackets straight after its name, if it has any.
let private typeParameters (c: Cursor) : (Identifier * TypeExpr option) list =
    if c.Is LeftParen && c.OnSameLine then
        c.Take() |> ignore
        let parameters = sepBy1 c (fun () -> typeParameter c) (fun () -> c.Expect Comma |> ignore)
        c.Expect RightParen |> ignore
        parameters
    else []

/// `private` or `protected` after a type's `=`, with where it is written (`Expressions.typeDefCore`'s `kindModifier`).
let private kindModifier (c: Cursor) : TypeDefModifier list =
    let kindOf text = match text with "private" -> Some Private | "protected" -> Some Protected | _ -> None
    match c.Token with
    | Some token when token.Kind = Keyword && (kindOf (c.Text token)).IsSome ->
        c.Take() |> ignore
        if c.IsKeyword "private" || c.IsKeyword "protected" then c.GiveUp "a type both private and protected, which is a mistake"
        [ (kindOf (c.Text token)).Value; KindAt(c.SpanOf token) ]
    | _ -> []

/// A type's body, and `where` and a predicate after it if it has one (`TypeWhere`).
let private bodyWithWhere (c: Cursor) : TypeExpr =
    let start = c.Start
    let body = DescentTypes.typeExpr c
    if c.Across(fun c -> c.IsKeyword "where") then
        c.Take() |> ignore
        c.Across(fun c -> c.Token.IsSome) |> ignore
        let predicate = expression Crossing c
        TypeExpr.at (endingAt predicate.Span (c.SpanFrom start)) (TypeWhere(body, predicate))
    else body

/// After a type's `=`, which may end its line: the body may start the line after.
let private afterEquals (c: Cursor) =
    c.Expect Equals |> ignore
    c.Across(fun c -> c.Token.IsSome) |> ignore

/// A type's or a context's definition: `scoped export context Name(a) = protected ...` or
/// `export type Name(a) = [Tx] private ...` (`Expressions.typeDefCore`). Its body declares its name.
let typeDefinition (c: Cursor) : Definition =
    let defStart = c.Start
    let declaring (name: Identifier) (nameSpan: SourceSpan) (definition: Definition) =
        match definition with
        | TypeDef(_, _, _, body) ->
            c.Declare TypeDefBody body.Id [ name, nameSpan ]
            c.NoteDocumentable defStart nameSpan.Start
        | ValueDef _ -> ()
        definition
    let withKind modifiers kind =
        match kind with
        | first :: rest -> first :: modifiers @ rest
        | [] -> modifiers
    let contextDefinition () =
        let scoped =
            if c.Is(Name, "scoped") then
                let token = c.Take()
                [ Scoped(c.SpanOf token) ]
            else []
        let exported = if c.IsKeyword "export" then (c.Take() |> ignore; [ Export ]) else []
        c.Expect(Keyword, "context") |> ignore
        let name, nameSpan = identifierWithSpan c
        if name = "type" then raise NoMatch
        let parameters = typeParameters c
        afterEquals c
        let kind = kindModifier c
        c.Across(fun c -> c.Token.IsSome) |> ignore
        let body = bodyWithWhere c
        declaring name nameSpan (TypeDef(name, withKind (Context :: scoped @ exported) kind, parameters, body))
    let typeDefinition () =
        let modifiers = ResizeArray()
        let mutable going = true
        while going do
            if c.IsKeyword "export" then (c.Take() |> ignore; modifiers.Add Export)
            elif c.IsKeyword "context" then (c.Take() |> ignore; modifiers.Add Context)
            else going <- false
        c.Expect(Keyword, "type") |> ignore
        let nameStart = c.Start
        let typeName, _ = identifierWithSpan c
        let name =
            if c.Adjacent && c.Is Dot then
                c.Take() |> ignore
                $"{typeName}.{fst (identifierWithSpan c)}"
            else typeName
        let nameSpan = c.SpanFrom nameStart
        let parameters = typeParameters c
        afterEquals c
        // `[Tx] protected (...)`: the scoped contexts its values belong to the scope of, before its kind.
        let scope =
            let inScope () =
                c.Expect LeftBracket |> ignore
                let contexts = sepBy1 c (fun () -> identifierWithSpan c) (fun () -> c.Expect Comma |> ignore)
                c.Expect RightBracket |> ignore
                if not (c.IsKeyword "protected" || c.IsKeyword "private") then raise NoMatch
                InScopeOf contexts
            if c.Is LeftBracket then Option.toList (c.Attempt inScope) else []
        let kind = kindModifier c
        c.Across(fun c -> c.Token.IsSome) |> ignore
        let body = bodyWithWhere c
        declaring name nameSpan (TypeDef(name, withKind (List.ofSeq modifiers) kind @ scope, parameters, body))
    match c.Attempt contextDefinition with
    | Some definition -> definition
    | None -> typeDefinition ()

/// The column `offset` is at, counted from 0, and whether only spaces come before it on its line.
let private columnAt (source: string) (offset: int) =
    let lineStart = if offset = 0 then 0 else source.LastIndexOf('\n', offset - 1) + 1
    offset - lineStart, source.Substring(lineStart, offset - lineStart).Trim() = ""

/// A module's item, as the items it stands for: one, or the defs a destructuring def desugars into. In a module block
/// (`inBlock`) there is no module declaration, pattern or destructuring def (`TopLevel`'s `moduleBlockItem`).
let rec private items (c: Cursor) (inBlock: bool) : TopLevelItem list =
    let start = c.Start
    let token = c.Token.Value
    let nextText = match c.At 1 with Code next -> c.Text next | _ -> ""
    match token.Kind, c.Text token with
    | Keyword, "module" -> moduleItem c start inBlock
    | Keyword, "export" when nextText = "module" && not inBlock -> moduleItem c start inBlock
    | Keyword, "import" ->
        c.Take() |> ignore
        if not c.OnSameLine then raise NoMatch
        let imported = importItems c
        [ TopLevelItem.at (c.SpanFrom start) (Import imported) ]
    // `@entry`, which annotates the item after it, its argument written straight after its name.
    | At, _ ->
        c.Take() |> ignore
        if not c.Adjacent then raise NoMatch
        let attributeName, _ = identifierWithSpan c
        let argument = if c.Adjacent && c.Is LeftParen then Some(attributeArgument c) else None
        [ TopLevelItem.at (c.SpanFrom start) (TopLevelItemKind.Attribute(attributeName, argument)) ]
    | _ -> definitionOrExpression c start inBlock

/// `module name` declaring the file's module, or `module name =` and a block of items on the lines after it.
and private moduleItem (c: Cursor) (start: int) (inBlock: bool) : TopLevelItem list =
    let isExport = c.IsKeyword "export"
    if isExport then c.Take() |> ignore
    c.Expect(Keyword, "module") |> ignore
    let first, _ = identifierWithSpan c
    if c.Is Equals && c.OnSameLine && not isExport then
        c.Take() |> ignore
        match c.At 0 with
        // The block's items are the lines at its first item's column, as the FParsec parser reads them: a line at
        // another column ends it, whatever layout tokens come before it.
        | Indent _ ->
            c.Index <- c.Index + 1
            let column, _ = columnAt c.Source c.Start
            let blockItems = ResizeArray(items c true)
            let mutable going = true
            while going do
                let index = c.Index
                skipLayout c
                let atItem =
                    c.Token.IsSome && not (c.Is EndOfFile) && not c.OnSameLine
                    && columnAt c.Source c.Start = (column, true)
                if atItem then blockItems.AddRange(items c true)
                else
                    c.Index <- index
                    going <- false
            let last = blockItems[blockItems.Count - 1]
            let text = c.SpanFrom start
            c.NoteModuleBlock first text
            [ TopLevelItem.at (endingAt last.Span text) (ModuleBlock(first, List.ofSeq blockItems)) ]
        | _ -> c.GiveUp "a module block the layout pass doesn't open"
    elif inBlock then raise NoMatch
    else
        let parts = ResizeArray [ first ]
        while c.Adjacent && c.Is Dot do
            c.Take() |> ignore
            parts.Add(fst (identifierWithSpan c))
        [ TopLevelItem.at (c.SpanFrom start) (ModuleDecl(isExport, String.concat "." parts)) ]

/// A re-export, a type's definition, a pattern's, a def, a destructuring def, or an expression.
and private definitionOrExpression (c: Cursor) (start: int) (inBlock: bool) : TopLevelItem list =
    let single kind = [ TopLevelItem.at (c.SpanFrom start) kind ]
    let nextText = match c.At 1 with Code next -> c.Text next | _ -> ""
    let reExport () =
        c.Expect(Keyword, "export") |> ignore
        if not c.OnSameLine then raise NoMatch
        ReExport(reExportItem c)
    let isDef = c.IsKeyword "def" || (c.IsKeyword "export" && nextText = "def")
    // A destructuring def is tried before any other item (`TopLevel.topLevelItems`): `export def (a, b)` is no
    // re-export of a module called `def`.
    let destructured = if isDef && not inBlock then c.Attempt(fun () -> destructuringDefinition c start) else None
    match destructured with
    | Some statements ->
        statements
        |> List.map (fun statement ->
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, value) -> TopLevelItem.at statement.Span (Def(ValueDef(isExport, name, typeOpt, value)))
            | _ -> c.GiveUp "a destructuring def's statement that isn't a def")
    | None ->
    match c.Attempt(fun () -> if c.IsKeyword "export" then reExport () else raise NoMatch) with
    | Some kind -> single kind
    | None ->
    // A `use` at the top level is a mistake, but `use ... in`, an expression.
    if c.IsKeyword "use" then
        match c.Attempt(fun () -> DescentControl.statementValue c) with
        | Some value -> single (Expr value)
        | None -> c.GiveUp "a `use` at the top level, which is a mistake"
    elif c.IsKeyword "type" || c.IsKeyword "context" || (c.IsKeyword "export" && (nextText = "type" || nextText = "context")) || (c.Is(Name, "scoped") && (nextText = "export" || nextText = "context")) then
        single (Def(typeDefinition c))
    elif not inBlock && (c.IsKeyword "pattern" || (c.IsKeyword "export" && nextText = "pattern")) then
        patternDefinition c start
    elif isDef then valueDefinition c start
    else
        let value = DescentControl.statementValue c
        single (Expr value)

/// `pattern T -> #A | #B = { ... }`: a custom pattern, as the def of its function, whose name is made up from its type
/// (see `PatternSyntax`).
and private patternDefinition (c: Cursor) (start: int) : TopLevelItem list =
    let isExport = c.IsKeyword "export"
    if isExport then c.Take() |> ignore
    c.Expect(Keyword, PatternSyntax.keyword) |> ignore
    let signatureStart = c.Start
    let signature = DescentTypes.typeSignature c
    let span = c.SpanFrom signatureStart
    let shape =
        match signature with
        | PatternSyntax.Signature s when s.Results |> List.forall (fst >> PatternSyntax.isPatternTag) -> s
        | _ -> c.GiveUp "a pattern whose type is a mistake"
    afterEquals c
    let body = DescentExpressions.defValue c
    let name = PatternSyntax.defName (PatternSyntax.shape shape)
    c.Declare DefValue body.Id [ name, span ]
    c.NoteDocumentable start span.Start
    [ TopLevelItem.at (c.SpanFrom start) (Def(ValueDef(isExport, name, Some signature, body))) ]

/// `def name: T = value`, or with nothing after its type on the line, a foreign function's declaration, whose value is
/// a call to the built-in "__extern" spanning the declaration to the end of its type (see `ExternSyntax`).
and private valueDefinition (c: Cursor) (start: int) : TopLevelItem list =
    let isExport = c.IsKeyword "export"
    if isExport then c.Take() |> ignore
    c.Expect(Keyword, "def") |> ignore
    let name, nameSpan, typeOpt = nameAndType c
    let indented = c.Is Equals && (match c.At 1 with Indent _ -> true | _ -> false)
    let value =
        match typeOpt with
        | Some typeExpr when not (c.Is Equals) ->
            if not (lineEnds c) then raise NoMatch
            Expression.at { c.SpanFrom start with End = typeExpr.Span.End } (FunctionCall(ExternSyntax.builtIn, None, []))
        | _ -> defValue c
    c.Declare DefValue value.Id [ name, nameSpan ]
    c.NoteDocumentable start nameSpan.Start
    // The FParsec parser takes the comment after a block's last expression statement as the statement's tail, so the
    // def it is the value of spans it too, though the statement and the block end before it.
    let span =
        match value.Kind, c.TrailingCommentEnd with
        | Block statements, Some commentEnd when indented && (match List.last statements with { Kind = ExprStatement _ } -> true | _ -> false) ->
            { c.SpanFrom start with End = commentEnd }
        | _ -> c.SpanFrom start
    [ TopLevelItem.at span (Def(ValueDef(isExport, name, typeOpt, value))) ]

/// Fills the forward references the files before this one read through.
let fill () =
    DescentControl.fill ()
    Forward.typeDefinition.Value <- typeDefinition
    Forward.importItems.Value <- importItems

/// The items of `source`, the text of the file at `file`, and what the FParsec parser would keep beside them
/// (`ParseState`), if the descent parser reads all of it (`TopLevel.moduleParser`), or why it gives up.
let tryReadModuleWithState (file: string) (source: string) : Result<TopLevelItem list * ParseState, string> =
    fill ()
    try
        let c = cursorOf file (Header.Directives.blank source)
        let all = ResizeArray()
        skipLayout c
        while not (c.Is EndOfFile) do
            all.AddRange(items c false)
            skipLayout c
        let state =
            { ParseState.empty () with
                Declared = c.Declared
                DocumentableStarts = c.DocumentableStarts
                StringTexts = c.StringTexts
                Comments = c.Comments
                ModuleBlocks = c.ModuleBlocks }
        Ok(List.ofSeq all, state)
    with
    | GiveUp reason -> Error reason
    | NoMatch -> Error "no reading matched"

/// The items of `source`, the text of the file at `file`, if the descent parser reads all of it, or why it gives up.
let tryReadModule (file: string) (source: string) : Result<TopLevelItem list, string> =
    tryReadModuleWithState file source |> Result.map fst

/// The items of `source`, the text of the file at `file`, if the descent parser reads all of it: `None` when it gives
/// up, for the FParsec parser to read it instead.
let readModule (file: string) (source: string) : TopLevelItem list option =
    tryReadModule file source |> Result.toOption
