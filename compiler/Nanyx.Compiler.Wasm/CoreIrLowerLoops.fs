/// Lowers loops (see the loops page of the docs) to the core IR's loop nodes.
module internal Transpiler.Wasm.CoreIrLowerLoops

open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

let private mk kind ty : Expr = { Kind = kind; Type = ty }

/// `for xs, ys | arms`: the body runs for each step, with each iterable's element in a local the body names by its
/// parameter. A range counts from its start (to its end, if it has one); a list walks its cells. `depth` is how many
/// loops enclose this one, which names its locals; `atoms` lowers the iterables' operands, and `lowerBody` the body,
/// given `vars` with each parameter bound to its element's local.
let forEach
    (newVar: string -> VarKind -> IrType -> Var)
    (tyOf: Expression -> IrType)
    (atoms: Expression list -> (Atom list -> Expr) -> Expr)
    (lowerBody: Map<string, Var> -> Expr)
    (vars: Map<string, Var>)
    (depth: int)
    (iterables: Expression list)
    (parameters: (Identifier * TypeExpr option) list)
    (k: Expr -> Expr) =
    // The first stream's locals keep the names a loop over one iterable has always had.
    let suffix index = if index = 0 then "" else $"_{index}"
    // Each iterable's operands, and how to make its source and element type from their atoms.
    let source (iterable: Expression) : Expression list * (Atom list -> ForSource * IrType) =
        match iterable.Kind with
        | RangeSyntax.Call(first, Some last) -> [ first; last ], fun atoms -> OverRange(atoms.[0], atoms.[1]), IrInt
        | RangeSyntax.Call(first, None) -> [ first ], fun atoms -> OverRangeFrom atoms.Head, IrInt
        | _ ->
            match tyOf iterable with
            | IrList elementTy -> [ iterable ], fun atoms -> OverList atoms.Head, elementTy
            | other -> cannot $"a `for` loop over a value of type {IrType.show other}"
    let walkers =
        iterables |> List.mapi (fun index _ ->
            newVar $"__for_cursor_{depth}{suffix index}" ForLoop IrWord, newVar $"__for_end_{depth}{suffix index}" ForLoop IrInt)
    let sources = iterables |> List.map source
    atoms (sources |> List.collect fst) (fun atoms ->
        let streams =
            List.zip3 sources parameters walkers
            |> List.mapFold (fun (remaining: Atom list) ((operands, make), (parameter, _), (cursor, finish)) ->
                let source, elementTy = make (List.take operands.Length remaining)
                let stream =
                    { Source = source
                      Cursor = cursor
                      End = finish
                      Element = newVar $"__for_{sanitizeIdentifier parameter}_{depth}" ForLoop elementTy }
                (parameter, stream), List.skip operands.Length remaining) atoms
            |> fst
        let vars = streams |> List.fold (fun vars (parameter, stream) -> vars |> Map.add parameter stream.Element) vars
        let forLoop: ForLoop = { Streams = List.map snd streams; Depth = depth; Body = lowerBody vars }
        k (mk (ForEach forLoop) IrUnit))

/// `while condition -> body`, from the lowered condition and body, which run again each time round. `depth` is how many
/// loops enclose this one, which names its labels.
let whileLoop (depth: int) (condition: Expr) (body: Expr) =
    mk (While { Condition = condition; Depth = depth; Body = body }) IrUnit
