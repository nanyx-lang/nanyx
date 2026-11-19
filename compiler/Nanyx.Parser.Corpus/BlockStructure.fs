/// The block structure of a piece of Nanyx, read two ways so that they can be compared: from the FParsec parser's
/// tree, and from the layout pass's tokens (see `Layout`). Each is a list of groups, a group being the items of one
/// block written one per line: the offsets they start at. It is how stage 2 of the parser's rewrite is checked (see
/// the Work in progress page "The parser").
module Nanyx.Parser.BlockStructure

open System
open System.Collections
open Microsoft.FSharp.Reflection
open Parser.Program

/// Whether the item at `offset` in `source` starts its line: only spaces and tabs come before it there, or those and a
/// `|`, as before a match's arm or a union's case.
let private startsLine (source: string) (offset: int) =
    let mutable i = offset
    let skipSpaces () =
        while i > 0 && (source[i - 1] = ' ' || source[i - 1] = '\t') do
            i <- i - 1
    skipSpaces ()
    if i > 0 && source[i - 1] = '|' then
        i <- i - 1
        skipSpaces ()
    i = 0 || source[i - 1] = '\n'

/// Groups of fewer than two items say nothing about where items are separated, and are left out of both readings. A
/// group is listed once, though the tree may hold its node twice, as a destructuring def's items share their value.
let private normalize (source: string) (groups: int list seq) =
    groups
    |> Seq.map (fun group -> group |> List.filter (startsLine source) |> List.distinct)
    |> Seq.filter (fun group -> group.Length >= 2)
    |> Seq.distinct
    |> Seq.sort
    |> List.ofSeq

/// The children of `value` in the tree, found by reflection so that this needs no change when `Ast.fs` does.
let private children (value: obj) : obj seq =
    match value with
    | null
    | :? string
    | :? SourceSpan -> Seq.empty
    | :? IEnumerable as items -> Seq.cast<obj> items
    | _ ->
        let t = value.GetType()
        if FSharpType.IsRecord(t, true) then FSharpValue.GetRecordFields(value, true) :> obj seq
        elif FSharpType.IsTuple t then FSharpValue.GetTupleFields value :> obj seq
        elif FSharpType.IsUnion(t, true) then snd (FSharpValue.GetUnionFields(value, t, true)) :> obj seq
        else Seq.empty

/// The arms of the multi-branch `if` that `expr` is, if it is one, each where its condition, or its `else`, starts.
/// The parser makes it nested ifs and if-matches (`Match`es of two arms), the later arms' spanning from their `|`.
let private multiBranchArms (source: string) (expr: Expression) =
    let isAt (offset: int) (text: string) = offset >= 0 && String.CompareOrdinal(source, offset, text, 0, text.Length) = 0
    let skipSpacesBack (offset: int) =
        let mutable i = offset
        while i > 0 && Char.IsWhiteSpace source[i - 1] do
            i <- i - 1
        i
    let skipSpaces (offset: int) =
        let mutable i = offset
        while i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i + 1
        i
    // Where the `else` of an `| else -> body` arm starts, given where its body does.
    let elseBefore (body: Expression) =
        let arrow = skipSpacesBack body.Span.Start - 2
        let keyword = skipSpacesBack arrow - 4
        if isAt arrow "->" && isAt keyword "else" && source[skipSpacesBack keyword - 1] = '|' then Some keyword else None
    let rec arms (expr: Expression) =
        let arm (head: Expression) (rest: Expression) =
            let later =
                if isAt rest.Span.Start "|" then arms rest
                else elseBefore rest |> Option.toList
            head.Span.Start :: later
        match expr.Kind with
        | IfExpr(condition, _, rest) -> arm condition rest
        | Match([ value ], [ _; [ _ ], rest ]) -> arm value rest
        | _ -> []
    if isAt expr.Span.Start "if" && isAt (skipSpaces (expr.Span.Start + 2)) "|" then Some(arms expr) else None

/// The groups in the FParsec parser's tree of `parsed`: the top level's items, a module block's, a block's
/// statements, a match's or an `except`'s arms (each where its first pattern starts), a multi-branch `if`'s arms
/// and a union type's cases.
let ofTree (parsed: SourceModule) : int list list =
    let groups = ResizeArray<int list>()
    let rec walk (value: obj) =
        match value with
        | :? (TopLevelItem list) as items -> groups.Add(items |> List.map _.Span.Start)
        | :? (Statement list) as statements -> groups.Add(statements |> List.map _.Span.Start)
        | :? (MatchArm list) as arms -> groups.Add(arms |> List.choose (fun (patterns, _) -> patterns |> List.tryHead |> Option.map _.Span.Start))
        | :? Expression as expr ->
            match multiBranchArms parsed.Source expr with
            | Some arms -> groups.Add arms
            | None -> ()
        | :? TypeExprKind as kind ->
            match kind with
            | TypeUnion members -> groups.Add(members |> List.map _.Span.Start)
            | _ -> ()
        | _ -> ()
        for child in children value do
            walk child
    walk parsed.Items
    normalize parsed.Source groups

/// The groups the layout pass makes of `source`, read from its `Indent`, `Newline` and `Dedent` tokens.
let ofLayout (source: string) : int list list =
    let lexed = Lexer.lex "" source
    Layout.layout source lexed.Tokens |> Layout.blocks |> normalize source

/// `groups` written out one per line, each item as its line and column, counted from 1.
let describe (source: string) (groups: int list list) =
    let lines = LineTable source
    let at (offset: int) =
        let span = lines.Span "" offset offset
        $"{span.StartLine + 1}:{span.StartColumn + 1}"
    groups |> List.map (fun group -> group |> List.map at |> String.concat " ") |> String.concat "\n"
