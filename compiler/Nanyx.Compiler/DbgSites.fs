namespace NyxCompiler

open Parser.Program
open Parser.Program.Api

/// Where the `dbg` calls of a source file are, and where a `dbg` a run reached in one version of a file is in another:
/// an editor shows each value a run's `dbg`s gave at the end of its line, and keeps it there while the lines above it
/// change (#2054).
module DbgSites =
    /// A `dbg` call: the top-level definition it is in (`""` for top-level code, `block.name` for a def in a
    /// `module block =`), the how-manyeth `dbg` it is there, counted from 0 in the order they are written, its source
    /// text, and where its name is, the line and column counted from 1, as a run names the place of a hit.
    type Site = { Definition: string; Index: int; Text: string; Line: int; Column: int }

    /// Where the call `expr` names `dbg`, if it is one, at the name, as the compiler takes a hit's place, and the span of
    /// the whole call.
    let private dbgAt (expr: Expression) =
        match expr.Kind with
        | FunctionCall(name, nameSpan, _)
        | Pipe(_, name, nameSpan, _) when name = DbgSyntax.builtIn -> Some (nameSpan |> Option.defaultValue expr.Span, expr.Span)
        | _ -> None

    /// The `dbg` calls of `items`, each with the definition it is in, its name prefixed with `prefix`.
    let rec private callsIn (prefix: string) (items: TopLevelItem list) =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, _, value)) -> SyntaxTree.expressionsWithin value |> List.choose dbgAt |> List.map (fun spans -> prefix + name, spans)
            | Expr expr -> SyntaxTree.expressionsWithin expr |> List.choose dbgAt |> List.map (fun spans -> prefix, spans)
            | ModuleBlock(blockName, inner) -> callsIn (prefix + blockName + ".") inner
            | _ -> [])

    /// The text of `source` that `span` covers, or none when it isn't in it.
    let private textOf (source: string) (span: SourceSpan) =
        if span.Start >= 0 && span.End <= source.Length && span.Start <= span.End then source.Substring(span.Start, span.End - span.Start) else ""

    /// The `dbg` calls of `source`, in the order they are written; none if it doesn't parse.
    let sitesIn (source: string) : Site list option =
        match parseModuleFileWithNames "" source with
        | Result.Ok (items, _, _) ->
            callsIn "" items
            |> List.sortBy (fun (_, (name, _)) -> name.StartLine, name.StartColumn)
            |> List.groupBy fst
            |> List.collect (fun (definition, calls) ->
                calls
                |> List.mapi (fun index (_, (name, call)) ->
                    { Definition = definition; Index = index; Text = textOf source call; Line = name.StartLine + 1; Column = name.StartColumn + 1 }))
            |> List.sortBy (fun site -> site.Line, site.Column)
            |> Some
        | Result.Error _ -> None

    /// The how-manyeth of the `dbg`s of `sites` in its definition with its text `site` is, counted from 0.
    let private occurrence (sites: Site list) (site: Site) =
        sites |> List.filter (fun other -> other.Definition = site.Definition && other.Text = site.Text && other.Index < site.Index) |> List.length

    /// The `dbg` of `current` that is `site` of `old`: the one of its definition with its text, the same occurrence of
    /// it; or else, where its definition has as many as it had, the one at its index there; or else, where the file has
    /// as many as it had, the one at its place in the file.
    let private matching (old: Site list) (current: Site list) (site: Site) =
        let inDefinition (sites: Site list) = sites |> List.filter (fun other -> other.Definition = site.Definition)
        let sameText (sites: Site list) = inDefinition sites |> List.filter (fun other -> other.Text = site.Text)
        match sameText current |> List.tryItem (occurrence old site) with
        | Some found -> Some found
        | None when (inDefinition old).Length = (inDefinition current).Length -> inDefinition current |> List.tryItem site.Index
        | None when old.Length = current.Length -> current |> List.tryItem (List.findIndex ((=) site) old)
        | None -> None

    /// Where each of `places`, a `dbg` a run reached in the file when its text was `before`, a line and a column counted
    /// from 1, is in its text `now` (see `matching`): `None` for a place that names no `dbg` of `before`, or whose `dbg`
    /// can't be found; none at all if either text doesn't parse, when the editor keeps the places it has.
    let moved (before: string) (now: string) (places: (int * int) list) : (int * int) option list option =
        match sitesIn before, sitesIn now with
        | Some old, Some current ->
            places
            |> List.map (fun (line, column) ->
                old
                |> List.tryFind (fun site -> site.Line = line && site.Column = column)
                |> Option.bind (matching old current)
                |> Option.map (fun found -> found.Line, found.Column))
            |> Some
        | _ -> None
