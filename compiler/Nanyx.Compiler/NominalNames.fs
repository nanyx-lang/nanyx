/// Where the names of nominal types are written, so that the editor can show them apart from aliases of a shape (see
/// the Protected types page of the docs' work in progress). A nominal type's name is an ordinary name, so only the
/// compiler knows which names are nominal: it reads that from each type's declaration.
module NyxCompiler.NominalNames

open Parser.Program

/// A nominal type's name, written at `Span`: protected, or private when `IsPrivate`.
type NominalName = { Span: SourceSpan; IsPrivate: bool }

/// The part of a name after its last dot: `Email` in `m.Email`.
let private ownName (name: string) = name.Substring(name.LastIndexOf '.' + 1)

/// The last `length` characters of `span`, a name on one line: the type's own name in `m.Email`.
let private lastChars (length: int) (span: SourceSpan) =
    let start = max span.Start (span.End - length)
    { span with Start = start; StartColumn = span.StartColumn + start - span.Start }

/// Every place the compiled root module, the file `file`, writes the name of a nominal type, its own or one it
/// imports, in the order they are written: the type's declaration, a type that names it, its constructor, a pattern
/// that takes it apart, and the type's part of an attached definition's name (`Email` in `def Email.parse` and in a
/// call `Email.parse(s)`). A qualified name gives only the type's own part. Empty when the module did not type-check.
let inModule (file: string) (result: CompileResult) : NominalName list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let byName = typed.TypeDefs |> Map.filter (fun _ info -> info.IsNominal)
        let bySymbol =
            byName
            |> Map.values
            |> Seq.choose (fun info -> info.Symbol |> Option.map (fun id -> id, info))
            |> Map.ofSeq
        let named (info: TypeDefInfo) (span: SourceSpan) =
            { Span = lastChars (Names.written (ownName info.Name)).Length span; IsPrivate = info.IsPrivate }
        let referenced (id: NodeId) = typed.References |> Map.tryFind id
        // `T.member`, an attached definition of the nominal type `T`, written at `span`: the span of `T`, if it is written.
        let attachedType (symbol: Symbol) (span: SourceSpan) =
            let dot = symbol.Name.LastIndexOf '.'
            match byName |> Map.tryFind (symbol.Name.Substring(0, max dot 0)) with
            | Some info when dot > 0 ->
                let memberLength = (Names.written (symbol.Name.Substring(dot + 1))).Length
                if span.End - span.Start > memberLength + 1 then Some (named info { span with End = span.End - memberLength - 1 })
                else None
            | _ -> None
        let declarations =
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol -> symbol.Declaration.File = file && not (SourceSpan.isNone symbol.Declaration))
            |> Seq.choose (fun symbol ->
                match bySymbol |> Map.tryFind symbol.Id with
                | Some info -> Some (named info symbol.Declaration)
                | None -> attachedType symbol symbol.Declaration)
            |> List.ofSeq
        let expressions = Hover.allExpressions syntax
        let inExpressions =
            expressions
            |> List.choose (fun expr ->
                match referenced expr.Id, Definition.nameSpan expr with
                | Some id, Some span ->
                    match bySymbol |> Map.tryFind id, typed.Symbols |> Map.tryFind id with
                    | Some info, _ -> Some (named info span)
                    | None, Some symbol -> attachedType symbol span
                    | None, None -> None
                | _ -> None)
        let inTypes =
            (syntax |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
            |> List.collect TypeExpr.within
            |> List.choose (fun typeExpr ->
                match referenced typeExpr.Id |> Option.bind (fun id -> bySymbol |> Map.tryFind id), Definition.typeNameSpan typeExpr with
                | Some info, Some span -> Some (named info span)
                | _ -> None)
        let inPatterns =
            expressions
            |> List.collect (fun expr ->
                match expr.Kind with
                | Match(_, arms) -> arms |> List.collect (fst >> List.collect Pattern.within)
                | _ -> [])
            |> List.choose (fun pattern ->
                match pattern.Kind with
                | RecordPattern(typeName, _) ->
                    byName
                    |> Map.tryFind typeName
                    |> Option.map (fun info -> named info { pattern.Span with End = pattern.Span.Start + (Names.written typeName).Length })
                | _ -> None)
        declarations @ inExpressions @ inTypes @ inPatterns
        |> List.filter (fun name -> not (SourceSpan.isNone name.Span) && name.Span.File = file)
        |> List.distinct
        |> List.sortBy (fun name -> name.Span.Start)
    | _ -> []
