/// A parse written out as text, one node per line, with every span and without node ids, so that two parsers' trees
/// can be compared and their first difference shown.
module Nanyx.Parser.SyntaxDump

open System
open System.Collections
open System.Globalization
open System.Text
open Microsoft.FSharp.Reflection
open Parser.Program

let private span (s: SourceSpan) =
    if SourceSpan.isNone s then "@none" else $"@{s.StartLine + 1}:{s.StartColumn + 1} [{s.Start}..{s.End})"

/// How a value of a type is taken apart, worked out once per type: reflection is slow, and the corpus is large.
type private Shape =
    /// A syntax node: a record with a `Kind`, a `Span` and an `Id`. The id is left out.
    | Node of kindType: Type * readSpan: (obj -> obj) * readKind: (obj -> obj)
    | Record of name: string * fields: (string * Type * (obj -> obj)) array
    | ListOf of itemType: Type
    | TupleOf of itemTypes: Type array * read: (obj -> obj array)
    | UnionOf of tag: (obj -> int) * cases: (string * Type array * (obj -> obj array)) array
    | Leaf

let private shapes = Collections.Concurrent.ConcurrentDictionary<Type, Shape>()

let private shapeOf (t: Type) =
    shapes.GetOrAdd(t, fun t ->
        if FSharpType.IsRecord(t, true) then
            let fields = FSharpType.GetRecordFields(t, true)
            let named name = fields |> Array.tryFind (fun f -> f.Name = name)
            let reader (f: Reflection.PropertyInfo) = FSharpValue.PreComputeRecordFieldReader f
            match named "Kind", named "Span", named "Id" with
            | Some kind, Some nodeSpan, Some _ -> Node(kind.PropertyType, reader nodeSpan, reader kind)
            | _ -> Record(t.Name, fields |> Array.map (fun f -> f.Name + ": ", f.PropertyType, reader f))
        elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>> then
            ListOf(t.GetGenericArguments()[0])
        elif FSharpType.IsTuple t then
            TupleOf(FSharpType.GetTupleElements t, FSharpValue.PreComputeTupleReader t)
        elif FSharpType.IsUnion(t, true) then
            let cases =
                FSharpType.GetUnionCases(t, true)
                |> Array.map (fun case ->
                    case.Name, case.GetFields() |> Array.map (fun f -> f.PropertyType), FSharpValue.PreComputeUnionReader(case, true))
            UnionOf(FSharpValue.PreComputeUnionTagReader(t, true), cases)
        else
            Leaf)

/// Writes `value`, of type `t`, into `out` at `depth`. It walks the tree by reflection, so it needs no change when
/// `Ast.fs` does: a node's `Id` is left out, its `Kind` written in its place, and a span written on the line of
/// whatever holds it.
let rec private write (out: StringBuilder) (depth: int) (label: string) (t: Type) (value: obj) =
    let line (text: string) = out.Append(' ', 2 * depth).Append(label).Append(text).Append('\n') |> ignore
    let children (items: (string * Type * obj) seq) = for name, childType, child in items do write out (depth + 1) name childType child
    match value with
    | :? string as s -> line ("\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"")
    | :? SourceSpan as s -> line (span s)
    | :? float as f -> line (f.ToString("R", CultureInfo.InvariantCulture))
    | _ ->
        match shapeOf t with
        | Node(kindType, readSpan, readKind) -> write out depth (label + span (readSpan value :?> SourceSpan) + " ") kindType (readKind value)
        | Record(name, fields) ->
            line name
            children (fields |> Seq.map (fun (fieldName, fieldType, read) -> fieldName, fieldType, read value))
        | ListOf itemType ->
            let items = value :?> IEnumerable |> Seq.cast<obj> |> List.ofSeq
            line $"[{items.Length}]"
            children (items |> Seq.map (fun item -> "- ", itemType, item))
        | TupleOf(itemTypes, read) ->
            line "tuple"
            children (Seq.zip itemTypes (read value) |> Seq.map (fun (itemType, item) -> ", ", itemType, item))
        // `None` is null, which the tag reader takes as it is.
        | UnionOf(tag, cases) ->
            let name, fieldTypes, read = cases[tag value]
            line name
            children (Seq.zip fieldTypes (read value) |> Seq.map (fun (fieldType, item) -> "", fieldType, item))
        | Leaf -> line (if isNull value then "null" else string value)

/// `value`, of type `'T`, written out one node per line.
let dump<'T> (value: 'T) =
    let out = StringBuilder()
    write out 0 "" typeof<'T> (box value)
    out.ToString()

/// A parse's result written out: its items and trivia, or its error's position and message.
let ofParse (result: Result<SourceModule, ParseError>) =
    match result with
    | Ok parsed -> dump parsed.Items + dump parsed.Trivia
    | Error error ->
        let message = error.Message.ReplaceLineEndings "\n"
        $"error {span error.Span}\n{message}\n"
