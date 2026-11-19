/// The recursive-descent parser checked against the FParsec parser, one part of the grammar at a time, until it reads
/// whole files (see the Work in progress page "The parser").
module Nanyx.Parser.DescentCheck

open Parser.Program
open Parser.Program.DescentCursor

/// How the new parser read one thing the FParsec parser read.
type Outcome =
    /// The same tree, with the same spans.
    | Same
    /// It gave up, for this reason, and the FParsec parser's reading stands.
    | GaveUp of reason: string
    /// A different tree: each one written out (see `SyntaxDump.dump`).
    | Differs of fparsec: string * descent: string

/// Which of the new parser's entry points reads a type the FParsec parser read.
type TypeEntry =
    /// A def's annotation, `typeSignature`.
    | Annotation
    /// A type definition's body, `typeExpr`.
    | TypeBody

/// The types in the FParsec parser's tree of `parsed` that a part of the grammar reads whole: each def's annotation and
/// each type definition's body, except one with a `where` constraint, whose constraint is an expression.
let typeRoots (parsed: SourceModule) : (TypeEntry * TypeExpr) list =
    let roots = ResizeArray()
    // A lambda's result type, `{ (x: int): int -> ... }`, is its def's annotation too, but written after the `=`: an
    // annotation is the def's own when no `=` comes between where the def starts and it.
    let add (defStart: int) (annotation: TypeExpr option) (body: TypeExpr option) =
        match annotation, body with
        | Some annotation, _ when annotation.Span.Start > defStart && not (parsed.Source.Substring(defStart, annotation.Span.Start - defStart).Contains '=') ->
            roots.Add((Annotation, annotation))
        | Some _, _ -> ()
        | _, Some { Kind = TypeWhere _ } -> ()
        | _, Some body -> roots.Add((TypeBody, body))
        | None, None -> ()
    let rec walk (value: obj) =
        match value with
        | :? TopLevelItem as item ->
            match item.Kind with
            | Def(ValueDef(_, _, annotation, _)) -> add item.Span.Start annotation None
            | Def(TypeDef(_, _, _, body)) -> add item.Span.Start None (Some body)
            | _ -> ()
        | :? Statement as statement ->
            match statement.Kind with
            | DefStatement(_, _, annotation, _) -> add statement.Span.Start annotation None
            | TypeDefStatement(_, _, _, body) -> add statement.Span.Start None (Some body)
            | _ -> ()
        | _ -> ()
        for child in BlockStructure.children value do
            walk child
    walk parsed.Items
    List.ofSeq roots

/// How the new parser reads each of `roots`, the types `typeRoots` finds in `parsed`, from the file at `file`.
let checkTypes (file: string) (parsed: SourceModule) (roots: (TypeEntry * TypeExpr) list) : Outcome list =
    match (try Ok(cursorOf file parsed.Source) with GiveUp reason -> Error reason) with
    | Error reason -> roots |> List.map (fun _ -> GaveUp reason)
    | Ok cursor ->
        roots
        |> List.map (fun (entry, root) ->
            if not (seek cursor root.Span.Start) then GaveUp "no token starts where the type does"
            else
                try
                    let read = match entry with Annotation -> DescentTypes.typeSignature cursor | TypeBody -> DescentTypes.typeExpr cursor
                    let expected = SyntaxDump.dump root
                    let actual = SyntaxDump.dump read
                    if expected = actual then Same else Differs(expected, actual)
                with
                | GiveUp reason -> GaveUp reason
                | NoMatch -> GaveUp "no reading matched")
