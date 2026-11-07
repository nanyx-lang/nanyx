/// What an editor offers to complete. Positions are character offsets into the source as the parser saw it, with
/// line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Completion

open Parser.Program

/// The names that can be written unqualified at `offset` in the compiled root module, one symbol for each name,
/// ordered by name. Where several declarations of a name are in scope, the nearest is the one the name means: the one
/// with the narrowest scope (a parameter before a top-level def, a top-level def before an import), and of equal
/// scopes the one declared last (a def in a block shadows an earlier one of the same name). Names with a dot
/// (`inner.secret`, `String.shout`) are left out: they are reached through what comes before the dot. Empty when
/// the module did not type-check.
let namesInScope (result: CompileResult) (offset: int) : Symbol list =
    match result.Syntax, result.Typed with
    | Some _, Some typed ->
        typed.Scopes
        |> Map.toSeq
        |> Seq.filter (fun (_, scope) -> scope.Start <= offset && offset < scope.End)
        |> Seq.map (fun (id, scope) -> typed.Symbols.[id], scope)
        |> Seq.filter (fun (symbol, _) -> not (symbol.Name.Contains '.'))
        |> Seq.groupBy (fun (symbol, _) -> symbol.Name)
        |> Seq.map (fun (_, candidates) ->
            candidates
            |> Seq.minBy (fun (symbol, scope) -> scope.End - scope.Start, -symbol.Declaration.Start)
            |> fst)
        |> Seq.sortBy (fun symbol -> symbol.Name)
        |> List.ofSeq
    | _ -> []

/// A name `namesInScope` finds, with its type as hover writes it (with made-up type variables named by `names`), if
/// it has one, and whether that type is a function's.
type NameInScope = { Symbol: Symbol; Type: string option; IsFunction: bool }

/// The names of `namesInScope`, with their types: a declaration of the module's own has the type inference recorded
/// for it, and a name an import brings in has the type its module gave it. An import's alias has none.
let typedNamesInScope (names: TypeVarNames) (result: CompileResult) (offset: int) : NameInScope list =
    match result.Typed with
    | Some typed ->
        namesInScope result offset
        |> List.map (fun symbol ->
            let ty =
                match typed.SymbolTypes |> Map.tryFind symbol.Id with
                | Some ty -> Some ty
                | None when symbol.Kind = TopLevelValue -> typed.Types |> Map.tryFind symbol.Name
                | None -> None
            { Symbol = symbol
              Type = ty |> Option.map (Unifier.tyToDisplayString names)
              IsFunction =
                match ty |> Option.map Unifier.unalias with
                | Some (TyFunc _) -> true
                | _ -> false })
    | None -> []
