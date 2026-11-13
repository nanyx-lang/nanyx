/// Destructuring defs, `def (a, b) = value`, which the parser takes apart into a def of a temporary and one def per name.
module internal Nanyx.Formatter.Destructuring

open System
open Parser.Program

/// A def as a top-level item or a statement holds it: whether it is exported, its name, its type and its value.
type Def = bool * Identifier * TypeExpr option * Expression
/// The runs of top-level items or statements that make up one def each: a destructuring `def (a, b) = value` is
/// parsed as a def of a temporary and one def per name, all with the same span. Each run is its first index, its
/// length and whether it is a destructuring def.
let destructuringRuns (items: (SourceSpan * Def option) list) : (int * int * bool) list =
    let items = Array.ofList items
    let rec rootedAt temp (e: Expression) =
        match e.Kind with
        | MemberAccess(target, _, _) -> rootedAt temp target
        | IdentifierExpr n -> n = temp
        | _ -> false
    let runs = ResizeArray()
    let mutable i = 0
    while i < items.Length do
        match items[i] with
        | span, Some(_, temp, None, _) when DestructuringSyntax.isTemp temp ->
            let mutable j = i + 1
            let belongs k =
                match items[k] with
                | otherSpan, Some(_, _, None, value) -> otherSpan = span && rootedAt temp value
                | _ -> false
            while j < items.Length && belongs j do
                j <- j + 1
            runs.Add((i, j - i, true))
            i <- j
        | _ ->
            runs.Add((i, 1, false))
            i <- i + 1
    List.ofSeq runs

/// The pattern the defs of a destructuring def's names were taken from, rebuilt from the paths they read: `(a, (b, _))`.
let rebuiltPattern (bindings: Def list) =
    let rec path (e: Expression) acc =
        match e.Kind with
        | MemberAccess(target, field, _) -> path target (int field :: acc)
        | _ -> acc
    let rec render (entries: (int list * Identifier) list) =
        let maxIndex = entries |> List.map (fst >> List.head) |> List.fold max 0
        let element k =
            match entries |> List.tryFind (fun (p, _) -> p = [ k ]) with
            | Some(_, n) -> n
            | None ->
                let nested = entries |> List.filter (fun (p, _) -> List.head p = k && p.Length > 1) |> List.map (fun (p, n) -> List.tail p, n)
                if nested.IsEmpty then "_" else render nested
        "(" + String.Join(", ", [ for k in 0..maxIndex -> element k ]) + ")"
    match bindings |> List.map (fun (_, n, _, value) -> path value [], n) |> List.filter (fun (p, _) -> not p.IsEmpty) with
    | [] -> "()"
    | entries -> render entries
