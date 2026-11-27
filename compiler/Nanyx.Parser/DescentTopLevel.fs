/// The recursive-descent parser's top level: a module's items, its declaration and its `module name =`
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

/// A name of a module path, `collections.list` or `./util/strings`: names joined by `.` or `/` written with no spaces.
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

/// Where an import comes from: a string, a file's path, or a module's path.
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

/// The names an import selects, in brackets, separated by commas or line breaks.
let private importSelection (c: Cursor) : (Identifier * SourceSpan) list =
    c.Expect LeftParen |> ignore
    let names = ResizeArray [ qualified c ]
    while not (c.Is RightParen) do
        c.Skip Comma |> ignore
        names.Add(qualified c)
    c.Expect RightParen |> ignore
    List.ofSeq names

/// One import: its source, then `as` and an alias, a selection, or both.
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

/// What follows `import`: one item, or several in brackets, each on its own.
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
/// the module alone on its line.
let private reExportItem (c: Cursor) : ImportItem =
    let start = c.Start
    let source = importSource c
    let selection =
        if c.Is LeftParen && c.OnSameLine then Some(importSelection c)
        elif lineEnds c then None
        else raise NoMatch
    { Source = source; Alias = None; Selection = selection; Span = c.SpanFrom start }

/// A type's or a context's parameter, a single lowercase letter, with a bound after `::` if it has one
///.
let private typeParameter (c: Cursor) : Identifier * TypeExpr option =
    let parameter, span = identifierWithSpan c
    if not (Names.isTypeVariable parameter) then
        if Char.IsLower parameter[0] then
            c.Report span.Start
                $"A type parameter is a single lowercase letter, such as 'a', so '{parameter}' cannot be one: longer lowercase names are reserved for associated types"
        else c.Report span.Start $"A type parameter is a single lowercase letter, such as 'a', so '{parameter}' cannot be one"
    if c.Is DoubleColon then
        c.Take() |> ignore
        parameter, Some(DescentTypes.typeExpr c)
    else parameter, None

/// A type's parameters in brackets straight after its name, if it has any.
let private typeParameters (c: Cursor) : (Identifier * TypeExpr option) list =
    if c.Is LeftParen && c.OnSameLine then
        c.Take() |> ignore
        let parameters = sepBy1If c (fun () -> typeParameter c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
        c.Expect RightParen |> ignore
        parameters
    else []

/// `private` or `protected` after a type's `=`, with where it is written.
let private kindModifier (c: Cursor) : TypeDefModifier list =
    let kindOf text = match text with "private" -> Some Private | "protected" -> Some Protected | _ -> None
    match c.Token with
    | Some token when token.Kind = Keyword && (kindOf (c.Text token)).IsSome ->
        c.Take() |> ignore
        if c.IsKeyword "private" || c.IsKeyword "protected" then
            c.Report (if c.Text token = "private" then c.Start else token.Start) "A private type is nominal already, so it is not also `protected`: keep `private`"
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

/// A type's or a context's definition: `export scoped context Name(a) = protected ...` or
/// `export type Name(a) = [Tx] private ...`. Its body declares its name.
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
        let takeExport () = if c.IsKeyword "export" then (c.Take() |> ignore; true) else false
        let exportFirst = takeExport ()
        let scoped =
            if c.Is(Name, "scoped") then
                let token = c.Take()
                [ Scoped(c.SpanOf token) ]
            else []
        // `scoped export context`, the order of earlier versions, is read too, and reported with a fix (see
        // `NyxCompiler.KeywordOrder`).
        let exported = if exportFirst || (not scoped.IsEmpty && takeExport ()) then [ Export ] else []
        c.Expect(Keyword, "context") |> ignore
        noDollar c
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
        noDollar c
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
                let contexts = sepBy1If c (fun () -> identifierWithSpan c) (fun () -> c.Expects Comma) (fun () -> c.Expect Comma |> ignore)
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

/// Whether a dimension's or a unit's declaration starts at the cursor: `dimension` or `unit`, after `export` if it is
/// exported, with a name after it on its line. The words are not reserved, so anywhere else they are names.
let private unitDeclarationAhead (c: Cursor) =
    let wordAt = if c.IsKeyword "export" then 1 else 0
    match c.At wordAt, c.At(wordAt + 1) with
    | Code word, Code declared ->
        let text = c.Text word
        word.Kind = Name && (text = UnitSyntax.dimensionKeyword || text = UnitSyntax.unitKeyword) && declared.Kind = Name
        && c.OnSameLineAt(wordAt + 1)
    | _ -> false

/// `dimension Time`, `dimension Bytes(int)`, `unit ns: Time` or `unit ms: Time = 1_000us`, after `export` if it is
/// exported (see `UnitSyntax`).
let private unitDeclaration (c: Cursor) (start: int) : TopLevelItem list =
    let isExport = c.IsKeyword "export"
    if isExport then c.Take() |> ignore
    let word = c.Text(c.Expect Name)
    let declared, nameSpan = identifierWithSpan c
    let kind =
        if word = UnitSyntax.dimensionKeyword then
            let storage =
                if c.Is LeftParen && c.OnSameLine then
                    c.Take() |> ignore
                    let storage = DescentTypes.typeExpr c
                    c.Expect RightParen |> ignore
                    Some storage
                else None
            let derived =
                if storage.IsNone && c.Is Equals && c.OnSameLine then
                    c.Take() |> ignore
                    Some (DescentTypes.typeExpr c)
                else None
            DimensionDecl(isExport, declared, storage, derived)
        else
            c.Expect Colon |> ignore
            let dimension =
                let name, span = identifierWithSpan c
                TypeExpr.at span (TypeName name)
            // A number of another unit, `1_000us`, or numbers of units multiplied and divided on one line, read from
            // the left, as a unit of a derived dimension is: `1km / 1hr` (see `UnitSyntax`).
            let unitLiteral () =
                if not (c.Is Number) then c.Fail "a number with a unit, such as `1_000us`"
                let literalStart = c.Start
                match DescentLiterals.literal c with
                | UnitLit _ as literal, span -> Expression.at span (LiteralExpr literal)
                | _ -> c.Report literalStart "A unit is a number of another unit, with no space between the number and the unit, as in `unit ms: Time = 1_000us`"
            let multiple =
                if c.Is Equals then
                    c.Take() |> ignore
                    let mutable result = unitLiteral ()
                    while (c.Is(Operator, UnitSyntax.times) || c.Is(Operator, UnitSyntax.over)) && c.OnSameLine do
                        let op = c.Take()
                        let right = unitLiteral ()
                        result <- Expression.at (SourceSpan.between result.Span right.Span) (BinaryOp(c.Text op, Some (c.SpanOf op), result, right))
                    Some result
                else None
            UnitDecl(isExport, declared, dimension, multiple)
    if not (lineEnds c) then c.Fail "the end of the line"
    c.NoteDocumentable start nameSpan.Start
    let item = TopLevelItem.at (c.SpanFrom start) kind
    c.Declare MeasureDeclaration item.Id [ declared, nameSpan ]
    [ item ]

/// A module's item, as the items it stands for: one, or the defs a destructuring def desugars into. In a module block
/// (`inBlock`) there is no module declaration, pattern or destructuring def.
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
        // The block's items are the lines at its first item's column: a line at another column ends it, whatever layout
        // tokens come before it.
        | Indent _ ->
            c.Index <- c.Index + 1
            let column, _ = columnAt c.Source c.Start
            let blockItems = ResizeArray(items c true)
            let mutable going = true
            while going do
                let index = c.Index
                skipLayout c
                // An attribute's item may be on its line, as in `@baseUnit unit ns: Time`.
                let afterAttribute =
                    match blockItems[blockItems.Count - 1].Kind with
                    | Attribute _ -> c.Index = index && c.Token.IsSome && not (c.Is EndOfFile) && c.OnSameLine
                    | _ -> false
                let atItem =
                    afterAttribute
                    || c.Token.IsSome && not (c.Is EndOfFile) && not c.OnSameLine
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
    // A destructuring def is tried before any other item: `export def (a, b)` is no
    // re-export of a module called `def`.
    let destructured =
        if isDef then
            let patternAt = if c.IsKeyword "export" then 2 else 1
            c.AttemptIf(not (DescentPatterns.loneNameAt c patternAt true [ "`,`" ]), fun () -> destructuringDefinition c start)
        else None
    match destructured with
    | Some statements ->
        statements
        |> List.map (fun statement ->
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, value) -> TopLevelItem.at statement.Span (Def(ValueDef(isExport, name, typeOpt, value)))
            | _ -> c.GiveUp "a destructuring def's statement that isn't a def")
    | None ->
    // A re-export takes `export`, a module's path on its line, and then expects `(` or the end of the line: `export def
    // name` is none, and fails at the name, noting nothing.
    let reExportNext () =
        if not (c.IsKeyword "export") then false
        elif not (c.OnSameLineAt 1) then
            c.NoteAt 1
            false
        elif c.IsAt(1, StringLiteral) then true
        elif not (c.IsAt(1, Name) || c.IsAt(1, Keyword)) then
            c.NoteAt 1
            false
        else
            // The offset after the path, whose parts `modulePath` takes when each is straight after the one before.
            let mutable after = 2
            while c.AdjacentAt after && (c.IsAt(after, Dot) || c.IsAt(after, Operator, "/")) && c.AdjacentAt(after + 1)
                  && (c.IsAt(after + 1, Name) || c.IsAt(after + 1, Keyword)) do
                after <- after + 2
            if (c.IsAt(after, LeftParen) && c.OnSameLineAt after) || c.IsAt(after, EndOfFile) || not (c.OnSameLineAt after) then true
            else
                c.NoteAt after
                false
    match c.AttemptIf(reExportNext (), fun () -> if c.IsKeyword "export" then reExport () else raise NoMatch) with
    | Some kind -> single kind
    | None ->
    if unitDeclarationAhead c then unitDeclaration c start else
    // A `use` at the top level is a mistake, but `use ... in`, an expression.
    if c.IsKeyword "use" then
        match c.Attempt(fun () -> DescentControl.statementValue c) with
        | Some value -> single (Expr value)
        | None -> c.Report start "A `use` belongs in a block, where it loads an instance for the rest of the block: put it in the body of a def such as `def main = { ... }`"
    elif c.IsKeyword "type" || c.IsKeyword "context" || (c.IsKeyword "export" && (nextText = "type" || nextText = "context" || nextText = "scoped")) || (c.Is(Name, "scoped") && (nextText = "export" || nextText = "context")) then
        single (Def(typeDefinition c))
    elif c.IsKeyword "pattern" || (c.IsKeyword "export" && nextText = "pattern") then
        patternDefinition c start
    elif isDef then valueDefinition c start
    // `mut x = 0`, a cell, which can't be at the top level: a def of it, as in a block, for the checker to report.
    elif c.IsKeyword "mut" && (match c.At 1 with Code next -> next.Kind = Name || next.Kind = BacktickedName | _ -> false) then
        match (DescentBlocks.statement c).Kind with
        | DefStatement(isExport, name, typeOpt, value) -> single (Def(ValueDef(isExport, name, typeOpt, value)))
        | _ -> c.GiveUp "a `mut` declaration that isn't a def"
    else
        let value = DescentControl.statementValue c
        single (Expr value)

/// A custom pattern at the top level or in a module block (see `DescentBlocks.patternDeclaration`).
and private patternDefinition (c: Cursor) (start: int) : TopLevelItem list =
    let isExport, name, signature, body = patternDeclaration c start
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
        // With no type either, it spans to the end of the spaces after its name.
        | None when not (c.Is Equals) && lineEnds c ->
            let mutable finish = c.LastEnd
            while finish < c.Source.Length && (c.Source[finish] = ' ' || c.Source[finish] = '\t') do
                finish <- finish + 1
            Expression.at { c.SpanFrom start with End = finish } (FunctionCall(ExternSyntax.builtIn, None, []))
        | _ -> defValue c
    c.Declare DefValue value.Id [ name, nameSpan ]
    c.NoteDocumentable start nameSpan.Start
    // The comment after a block's last expression statement is the statement's tail, so the def it is the value of
    // spans it too, though the statement and the block end before it.
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

/// `error`, or where it is at a `::` line, which is a directive after the code has started, the message that says where
/// directives go (`Api.runModuleParser`).
let private directiveAfterCode (source: string) (error: ParseError) =
    let start = error.Span.Start
    let atLineStart = start = 0 || (start <= source.Length && source[start - 1] = '\n')
    if atLineStart && String.CompareOrdinal(source, start, "::", 0, 2) = 0 then
        { error with Message = "A directive (a `::` line) must come before the first line of code"; Found = None }
    else error

/// The items of `source`, the text of the file at `file`, and what is kept beside them (`ParseState`), or its first
/// mistake: reported where it is (`Reported`), or what was found where no reading matched.
let tryReadModuleWithState (file: string) (source: string) : Result<TopLevelItem list * ParseState, ParseError> =
    fill ()
    let blanked = Header.Directives.blank source
    let mutable cursor = None
    try
        let c = cursorOf file blanked
        cursor <- Some c
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
                TokensRead = c.TokensRead
                NoMatches = c.NoMatches
                Comments = c.Comments
                ModuleBlocks = c.ModuleBlocks }
        Ok(List.ofSeq all, state)
    with
    | Reported error -> Error error
    | GiveUp _
    | NoMatch -> Error(directiveAfterCode blanked (cursor.Value.Unexpected()))

/// The offset in `source` of the start of the line `offset` is on.
let private lineStartOf (source: string) (offset: int) =
    if offset = 0 then 0 else source.LastIndexOf('\n', offset - 1) + 1

/// Whether `token` starts a line at the first column, where a top-level item can start, and isn't a closing bracket or
/// brace, which ends what an item above it opened.
let private startsTopLevelLine (source: string) (token: Token) =
    token.Start = lineStartOf source token.Start && not (List.contains token.Kind [ RightParen; RightBracket; RightBrace ])

/// The items of `source`, the text of the file at `file`, read past each mistake (stage 5 of the move to the new
/// parser): an item that doesn't read is left out, its error is noted, and reading goes on at the first line after the
/// item's start, from the error's line on, that starts at the first column, as the next top-level item. So an item left
/// open, as `def b = (1 +` is, loses only itself when the error is found on the line of the item after it, and an item
/// read before the error that runs on into its line is left out too. The lexer's mistakes are noted too, and an
/// error at or after the first of them, which has taken the rest of the line or the file, isn't. The errors are in the
/// order they are in the text.
let readModuleRecovering (file: string) (source: string) : TopLevelItem list * ParseState * ParseError list =
    fill ()
    let blanked = Header.Directives.blank source
    let lexed = Lexer.lex file blanked
    let c = Cursor(file, blanked, Layout.layout blanked lexed.Tokens)
    let errors = ResizeArray(lexed.Errors)
    let lexerErrorAt = lexed.Errors |> List.tryHead |> Option.map _.Span.Start
    let note (error: ParseError) =
        if lexerErrorAt |> Option.forall (fun at -> error.Span.Start < at) then errors.Add error
    let all = ResizeArray<TopLevelItem>()
    // Where reading last went on after a mistake: it only ever goes on further down, so it ends.
    let mutable resumedAt = -1
    skipLayout c
    while not (c.Is EndOfFile) do
        let itemStart = c.Start
        let failedAt =
            try
                all.AddRange(items c false)
                None
            with
            | Reported error -> Some error
            | GiveUp _
            | NoMatch -> Some(directiveAfterCode blanked (c.Unexpected()))
        match failedAt with
        | Some error ->
            note error
            // An item read before the mistake may have run on into the line it is on, as `def d = c +` does into a
            // `def` under it: it goes too, and reading goes on at that line, though it is before where this read began.
            let errorLine = lineStartOf blanked (min error.Span.Start blanked.Length)
            let runOn = all |> Seq.filter (fun item -> item.Span.Start >= resumedAt && item.Span.End > errorLine) |> List.ofSeq
            all.RemoveAll(fun item -> List.contains item runOn) |> ignore
            let from = runOn |> List.fold (fun from item -> min from item.Span.Start) itemStart
            c.Index <- 0
            let resumes () =
                match c.Token with
                | Some token ->
                    token.Kind = EndOfFile
                    || (token.Start > from && token.Start > resumedAt && token.Start >= errorLine && startsTopLevelLine blanked token)
                | None -> false
            while not (resumes ()) do
                c.Index <- c.Index + 1
            resumedAt <- c.Start
        | None -> ()
        skipLayout c
    let state =
        { ParseState.empty () with
            Declared = c.Declared
            DocumentableStarts = c.DocumentableStarts
            StringTexts = c.StringTexts
            TokensRead = c.TokensRead
            NoMatches = c.NoMatches
            Comments = c.Comments
            ModuleBlocks = c.ModuleBlocks }
    List.ofSeq all, state, errors |> Seq.sortBy _.Span.Start |> List.ofSeq
