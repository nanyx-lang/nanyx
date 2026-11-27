/// What each name in a module is, for an editor to colour it by: a function, a parameter, a variable, a field, a tag, a
/// type, a context, a module, an operator or a workflow keyword, as the type checker resolved it. Positions are
/// character offsets into the source as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.SemanticTokens

open Parser.Program

/// What a name is. Each but `Unresolved` is one of the protocol's standard token types, so that themes colour it with no
/// setup.
[<RequireQualifiedAccess>]
type TokenType =
    /// A def or local def whose value is a function, a context's member that is one, or an attached definition.
    | Function
    /// A lambda's parameter.
    | Parameter
    /// A def or local def whose value is not a function, a pattern variable, or a context's member that is not one.
    | Variable
    /// A record's field: in a record type, a record, a field read and a record pattern.
    | Property
    /// A tag, `#some`.
    | EnumMember
    | Type
    /// A context.
    | Interface
    /// A module: in its `module` line, and an import's path and alias.
    | Namespace
    /// An operator that calls a definition: an attached one, or a context's member.
    | Operator
    /// A workflow keyword, `try` or `return`, which calls the builder's member of its name.
    | Keyword
    /// A name that names nothing in scope: an unknown value, function, member, type, context or module, or the type an
    /// attached definition is attached to that isn't there. Not one of the protocol's standard types: the editor maps it
    /// to the colour its theme gives errors.
    | Unresolved

[<RequireQualifiedAccess>]
type TokenModifier =
    /// Where the name is declared, rather than used.
    | Declaration
    /// A def, a local def or a pattern variable, which never changes.
    | Readonly
    /// A name the standard library declares.
    | DefaultLibrary
    /// A cell, made with `mut` or taken by a `mut` parameter: not one of the protocol's standard modifiers.
    | Mutable
    /// A protected or private type's name (see `NominalNames`).
    | Nominal
    /// A private type's name.
    | Private

/// A name, written at `Span`, with what it is.
type Token = { Span: SourceSpan; Type: TokenType; Modifiers: TokenModifier list }

/// The last `length` characters of `span`, a name on one line: the name's own part in `m.origin`.
let private lastChars (length: int) (span: SourceSpan) =
    let start = max span.Start (span.End - length)
    { span with Start = start; StartColumn = span.StartColumn + start - span.Start }

/// The first `length` characters of `span`.
let private firstChars (length: int) (span: SourceSpan) = { span with End = min span.End (span.Start + length) }

let rec private isFunction (ty: Ty) =
    match ty with
    | TyFunc _ -> true
    | TyAlias(_, _, ty) -> isFunction ty
    | _ -> false

/// The span from `start` to `end'` of `source`, at or after the start of `like`: its line and column are counted from
/// `like`'s, not from the start of the text, which is slow over every name of a long module.
let private spanFrom (source: string) (like: SourceSpan) (start: int) (end': int) =
    let mutable lineBreaks = 0
    for index in like.Start .. start - 1 do
        if source.[index] = '\n' then lineBreaks <- lineBreaks + 1
    let column = if lineBreaks = 0 then like.StartColumn + start - like.Start else start - (source.LastIndexOf('\n', start - 1) + 1)
    { like with Start = start; End = end'; StartLine = like.StartLine + lineBreaks; StartColumn = column }

/// The span of the field's name `name`, written with the value or type of the node `node`, as the parser saw it written
/// (see `NamedFieldValue`): `x` in `x = 1`, `x: int` and the pattern `(x = a)`.
let private fieldNameSpan (typed: TypedModule) (node: NodeId) (name: string) =
    typed.Declared |> Map.tryFind (node, NamedFieldValue) |> Option.bind (List.tryFind (fst >> (=) name)) |> Option.map snd

/// The codes of the diagnostics that report a name as naming nothing in scope.
let private unknownNameCodes =
    [ DiagnosticCodes.UnknownIdentifier; DiagnosticCodes.UnknownFunction; DiagnosticCodes.UnknownMember
      DiagnosticCodes.UnknownAttachedType; DiagnosticCodes.UnknownContext; DiagnosticCodes.UnknownType ]
    |> List.map _.Code
    |> Set.ofList

/// The name each of `diagnostics` reports as naming nothing, in the file `file` whose text is `source`: the last part of
/// the name it is about, where it is written last in the diagnostic's span (`missing` in `m.missing`), or the span's
/// own last part if the name isn't written there.
let private unresolved (file: string) (source: string) (diagnostics: Diagnostic list) : Token list =
    diagnostics
    |> List.choose (fun diagnostic ->
        match diagnostic.Span with
        | Some span when unknownNameCodes.Contains diagnostic.Code && span.File = file && 0 <= span.Start && span.Start < span.End && span.End <= source.Length ->
            let text = source.Substring(span.Start, span.End - span.Start)
            let ownPart (name: string) = name.Substring(name.LastIndexOf '.' + 1)
            let at =
                diagnostic.Subject
                |> Option.map (fun subject -> ownPart subject, text.LastIndexOf(ownPart subject, System.StringComparison.Ordinal))
                |> Option.filter (fun (name, at) -> name <> "" && at >= 0)
                |> Option.defaultWith (fun () -> let name = ownPart text in name, text.Length - name.Length)
            match at with
            | name, at when name <> "" -> Some { Span = spanFrom source span (span.Start + at) (span.Start + at + name.Length); Type = TokenType.Unresolved; Modifiers = [] }
            | _ -> None
        | _ -> None)

/// Every name in the compiled root module, the file `file` whose text is `source`, that an editor colours by what it
/// is, in the order they are written: each name that refers to a symbol, at its declaration and at each use, and each
/// field, tag, module name, operator and workflow keyword. A qualified name gives its own part, and its alias part
/// as a module. A name the compile reports as naming nothing is `Unresolved`. A module with type or name errors has
/// tokens for what did resolve; one that has no types, as one whose imports don't check, has none.
let inModule (file: string) (source: string) (result: CompileResult) : Token list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let source = source.Replace("\r\n", "\n").Replace("\r", "\n")
        let expressions = Hover.allExpressions syntax
        let nominal =
            NominalNames.inModule file result
            |> List.map (fun name -> name.Span.Start, (if name.IsPrivate then [ TokenModifier.Nominal; TokenModifier.Private ] else [ TokenModifier.Nominal ]))
            |> Map.ofList
        let contexts =
            typed.TypeDefs |> Map.values |> Seq.filter _.IsContext |> Seq.choose _.Symbol |> Set.ofSeq
        let typeOf (id: SymbolId) = typed.SymbolTypes |> Map.tryFind id |> Option.orElse (typed.ImportedTypes |> Map.tryFind id)
        // The cells: local defs made with `mut`, and parameters that take one, each by its name and where it is in scope.
        let cells =
            expressions
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements
                    |> List.choose (fun statement ->
                        match statement.Kind with
                        | DefStatement(_, name, _, { Kind = MutSyntax.Call _ }) -> Some (name, statement.Span)
                        | _ -> None)
                | Lambda(parameters, _, _) ->
                    parameters
                    |> List.choose (fun (name, typeOpt) ->
                        match typeOpt with
                        | CellPassingSyntax.CellParameter _ -> Some (name, expr.Span)
                        | _ -> None)
                | _ -> [])
        let isCell (symbol: Symbol) =
            (symbol.Kind = LocalValue || symbol.Kind = Parameter)
            && cells |> List.exists (fun (name, span) -> name = symbol.Name && SourceSpan.contains span symbol.Declaration)
        let isLibrary (symbol: Symbol) =
            symbol.Declaration.File <> file && symbol.Declaration.File <> "" && not (System.IO.Path.IsPathRooted symbol.Declaration.File)
        let symbolToken (symbol: Symbol) (span: SourceSpan) (declaration: bool) =
            let cell = isCell symbol
            let typeAndModifiers =
                match symbol.Kind with
                | TopLevelValue
                | LocalValue ->
                    if typeOf symbol.Id |> Option.exists isFunction then Some (TokenType.Function, [])
                    elif cell then Some (TokenType.Variable, [ TokenModifier.Mutable ])
                    else Some (TokenType.Variable, [ TokenModifier.Readonly ])
                | Parameter -> Some (TokenType.Parameter, if cell then [ TokenModifier.Mutable ] else [])
                | PatternVariable -> Some (TokenType.Variable, [ TokenModifier.Readonly ])
                | ImportedModule -> Some (TokenType.Namespace, [])
                | TypeDefinition ->
                    let tokenType = if contexts.Contains symbol.Id then TokenType.Interface else TokenType.Type
                    Some (tokenType, nominal |> Map.tryFind span.Start |> Option.defaultValue [])
                | ContextMember ->
                    if typeOf symbol.Id |> Option.forall isFunction then Some (TokenType.Function, [])
                    else Some (TokenType.Variable, [ TokenModifier.Readonly ])
                // A unit is written as part of a number, which is a token of its own.
                | UnitOfMeasure -> None
            typeAndModifiers
            |> Option.map (fun (tokenType, modifiers) ->
                { Span = span
                  Type = tokenType
                  Modifiers =
                    (if declaration then [ TokenModifier.Declaration ] else [])
                    @ modifiers
                    @ (if isLibrary symbol then [ TokenModifier.DefaultLibrary ] else []) })
        let textOf (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)
        // An attached definition's type part, `Point` in `Point.show` and `string` in `t.string.uppercase`, written just
        // before its own name.
        let attachedType (symbol: Symbol) (span: SourceSpan) =
            let dot = symbol.Name.LastIndexOf '.'
            let text = textOf span
            if dot > 0 && text.EndsWith(symbol.Name, System.StringComparison.Ordinal) && (text.Length = symbol.Name.Length || text.[text.Length - symbol.Name.Length - 1] = '.') then
                let start = span.End - symbol.Name.Length
                let part = spanFrom source span start (start + dot)
                Some { Span = part; Type = TokenType.Type; Modifiers = nominal |> Map.tryFind part.Start |> Option.defaultValue [] }
            else None
        let named (symbol: Symbol) (span: SourceSpan) (declaration: bool) =
            let own = lastChars (Names.written (Names.ownPart symbol.Name)).Length span
            Option.toList (symbolToken symbol own declaration) @ Option.toList (attachedType symbol span)
        let declarations =
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol ->
                symbol.Declaration.File = file && not (SourceSpan.isNone symbol.Declaration) && not (ReservedNames.isHidden symbol))
            |> Seq.toList
            |> List.collect (fun symbol ->
                match symbol.Kind with
                | ImportedModule ->
                    // The import's path, and its alias if it has one.
                    let text = textOf symbol.Declaration
                    let pathLength = text |> Seq.takeWhile (System.Char.IsWhiteSpace >> not) |> Seq.length
                    let path = { Span = firstChars pathLength symbol.Declaration; Type = TokenType.Namespace; Modifiers = [ TokenModifier.Declaration ] }
                    let alias =
                        if pathLength < text.Length && text.EndsWith(" " + symbol.Name, System.StringComparison.Ordinal) then
                            symbolToken symbol (lastChars symbol.Name.Length symbol.Declaration) true |> Option.toList
                        else []
                    path :: alias
                | _ -> named symbol symbol.Declaration true)
        let uses =
            expressions
            |> List.collect (fun expr ->
                let referenced =
                    match WrittenReferences.symbolOf typed expr.Id, Definition.nameSpan expr with
                    | Some symbol, Some span when not (ReservedNames.isHidden symbol) ->
                        match expr.Kind with
                        | BinaryOp _ -> [ { Span = span; Type = TokenType.Operator; Modifiers = (if isLibrary symbol then [ TokenModifier.DefaultLibrary ] else []) } ]
                        | _ -> named symbol span false
                    | _ -> []
                // A name brought in by an import is written under its alias, `m` in `m.foo`.
                let alias =
                    match typed.ImportReferences |> Map.tryFind expr.Id |> Option.bind typed.Symbols.TryFind with
                    | Some import when WrittenReferences.ofModule typed |> Map.tryFind expr.Id <> Some import.Id && (textOf expr.Span).StartsWith(import.Name + ".", System.StringComparison.Ordinal) ->
                        [ { Span = firstChars import.Name.Length expr.Span; Type = TokenType.Namespace; Modifiers = [] } ]
                    | _ -> []
                let own =
                    match expr.Kind with
                    | MemberAccess(_, _, Some span) when referenced.IsEmpty -> [ { Span = span; Type = TokenType.Property; Modifiers = [] } ]
                    | RecordExpr fields ->
                        fields
                        |> List.choose (function
                            | NamedField(name, value) -> fieldNameSpan typed value.Id name
                            | PositionalField _ -> None)
                        |> List.map (fun span -> { Span = span; Type = TokenType.Property; Modifiers = [] })
                    | TagExpr(name, _) -> [ { Span = firstChars (name.Length + 1) expr.Span; Type = TokenType.EnumMember; Modifiers = [] } ]
                    | WorkflowBindExpr(keyword, _) when (textOf expr.Span).StartsWith(keyword, System.StringComparison.Ordinal) ->
                        [ { Span = firstChars keyword.Length expr.Span; Type = TokenType.Keyword; Modifiers = [] } ]
                    | WorkflowReturnExpr _ when (textOf expr.Span).StartsWith(WorkflowSyntax.returnKeyword, System.StringComparison.Ordinal) ->
                        [ { Span = firstChars WorkflowSyntax.returnKeyword.Length expr.Span; Type = TokenType.Keyword; Modifiers = [] } ]
                    | _ -> []
                referenced @ alias @ own)
        let types =
            (syntax |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
            |> List.collect TypeExpr.within
            |> List.collect (fun typeExpr ->
                match typeExpr.Kind with
                | TypeTag(name, _) -> [ { Span = firstChars (name.Length + 1) typeExpr.Span; Type = TokenType.EnumMember; Modifiers = [] } ]
                | TypeRecord fields
                | TypeOpenRecord(fields, _) ->
                    fields
                    |> List.choose (function
                        | TypeField(name, _, Some fieldType, _) -> fieldNameSpan typed fieldType.Id name
                        | _ -> None)
                    |> List.map (fun span -> { Span = span; Type = TokenType.Property; Modifiers = [ TokenModifier.Declaration ] })
                | _ ->
                    match WrittenReferences.symbolOf typed typeExpr.Id, Definition.typeNameSpan typeExpr with
                    | Some symbol, Some span -> named symbol span false
                    | _ -> [])
        let patterns =
            expressions
            |> List.collect (fun expr ->
                match expr.Kind with
                | Match(_, arms)
                | Except(_, arms) -> arms |> List.collect (fst >> List.collect Pattern.within)
                | _ -> [])
            |> List.collect (fun pattern ->
                match pattern.Kind with
                | TagPattern(name, _) -> [ { Span = firstChars (name.Length + 1) pattern.Span; Type = TokenType.EnumMember; Modifiers = [] } ]
                | RecordMemberPattern(members, _) ->
                    members
                    |> List.choose (fun (name, inner) -> fieldNameSpan typed inner.Id name)
                    |> List.map (fun span -> { Span = span; Type = TokenType.Property; Modifiers = [] })
                | RecordPattern(typeName, _) ->
                    let span = firstChars (Names.written typeName).Length pattern.Span
                    [ { Span = span; Type = TokenType.Type; Modifiers = nominal |> Map.tryFind span.Start |> Option.defaultValue [] } ]
                | _ -> [])
        let modules =
            syntax
            |> List.collect (fun item ->
                match item.Kind with
                | ModuleDecl(_, name)
                | ModuleBlock(name, _) when not (SourceSpan.isNone item.Span) ->
                    let text = textOf item.Span
                    match text.IndexOf("module " + name, System.StringComparison.Ordinal) with
                    | -1 -> []
                    | at ->
                        let start = item.Span.Start + at + "module ".Length
                        [ { Span = spanFrom source item.Span start (start + name.Length); Type = TokenType.Namespace; Modifiers = [ TokenModifier.Declaration ] } ]
                | _ -> [])
        // A name found twice keeps what it was first found as: unresolved, then its declaration, then its use.
        unresolved file source result.Diagnostics @ declarations @ uses @ types @ patterns @ modules
        |> List.filter (fun token ->
            token.Span.File = file && not (SourceSpan.isNone token.Span) && token.Span.Start < token.Span.End
            && not ((textOf token.Span).Contains '\n'))
        |> List.distinctBy (fun token -> token.Span.Start)
        |> List.sortBy (fun token -> token.Span.Start)
        // Tokens must not overlap: an inner one is dropped.
        |> List.fold (fun (kept: Token list) token ->
            match kept with
            | last :: _ when token.Span.Start < last.Span.End -> kept
            | _ -> token :: kept) []
        |> List.rev
    | _ -> []
