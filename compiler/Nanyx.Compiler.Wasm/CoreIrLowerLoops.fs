/// Lowers loops (see the loops page of the docs) to the core IR's loop nodes.
module internal Transpiler.Wasm.CoreIrLowerLoops

open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

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
            | other -> cannot $"a `for` loop over a value of type '{IrType.show other}'"
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
        k (Expr.make (ForEach forLoop) IrUnit))

/// The function a sequence of type `ty` is pulled with, and what its `#next` holds: the element's type and the rest's.
/// None when `ty` isn't a sequence: a record of one function of no parameters that gives `#next(x, rest)` or `#done`.
let private pulledBy (ty: IrType) =
    match IrType.unfold ty with
    | IrRecord [ _, (IrFunction(IrUnit, IrTags tags, _) as next) ] when tags |> List.exists (fun (name, payload) -> name = "done" && payload.IsNone) ->
        match tags |> List.tryFind (fst >> (=) "next") |> Option.bind snd with
        | Some (IrRecord [ _, element; _, rest ]) -> Some (next, element, rest)
        | _ -> None
    | _ -> None

/// Whether a `for` loop over `iterables` pulls a sequence, which `forEachPulling` lowers.
let pullsSequence (tyOf: Expression -> IrType) (iterables: Expression list) =
    iterables |> List.exists (fun iterable ->
        match iterable.Kind with
        | RangeSyntax.Call _ -> false
        | _ -> (pulledBy (tyOf iterable)).IsSome)

/// `for xs, s | arms` where one of the iterables is a sequence (`seq(a)`): a `while` loop that, each time round, takes
/// the next element of each iterable in turn and runs the body with them, or stops once one has none. A sequence is
/// pulled by calling its function, with the members of the contexts it carries (`members`), and its rest is held for
/// the next time round; a list's rest is too, and a range counts. `local` makes a local of a name, made unique, and a
/// type; the rest is as for `forEach`.
let forEachPulling
    (local: string -> IrType -> Var)
    (tyOf: Expression -> IrType)
    (atoms: Expression list -> (Atom list -> Expr) -> Expr)
    (members: (ContextMemberKey * IrType) list -> (Atom list -> Expr) -> Expr)
    (lowerBody: Map<string, Var> -> Expr)
    (vars: Map<string, Var>)
    (depth: int)
    (iterables: Expression list)
    (parameters: (Identifier * TypeExpr option) list)
    (k: Expr -> Expr) =
    let going = local "__for_going" IrBool
    let letIn (var: Var) (value: Expr) (body: Expr) = Expr.make (Let(var, value, body)) body.Type
    let stop = Expr.make (Assign(going, ABool false)) IrUnit
    let andThen (first: Expr) (second: Expr) = Expr.make (Do(first, second)) second.Type
    // Each iterable's operands, and, from their atoms, the locals that walk it, each with its starting value, and
    // `step`, which binds its next element for the rest of the time round (`next`) and moves it on after, or stops.
    let walker (iterable: Expression) (parameter: Identifier) : Expression list * (Atom list -> (Var * Expr) list * ((Var -> Expr) -> Expr)) =
        let element ty = local $"__for_{parameter}" ty
        match iterable.Kind with
        | RangeSyntax.Call(first, last) ->
            let operands = first :: Option.toList last
            operands, fun atoms ->
                let counter = local "__for_counter" IrInt
                let finish = local "__for_end" IrInt
                let walking = (counter, Expr.make (Atom atoms.Head) IrInt) :: (if last.IsSome then [ finish, Expr.make (Atom atoms.[1]) IrInt ] else [])
                let step (next: Var -> Expr) =
                    let value = element IrInt
                    let following = local "__for_following" IrInt
                    let moveOn = letIn following (Expr.make (Prim(Binary "+", [ AVar counter; AInt 1L ])) IrInt) (Expr.make (Assign(counter, AVar following)) IrUnit)
                    let body = letIn value (Expr.make (Atom(AVar counter)) IrInt) (andThen (next value) moveOn)
                    if last.IsNone then body
                    else
                        let past = local "__for_past" IrBool
                        letIn past (Expr.make (Prim(Binary ">", [ AVar counter; AVar finish ])) IrBool) (Expr.make (If(AVar past, stop, body)) IrUnit)
                walking, step
        | _ ->
            let ty = tyOf iterable
            [ iterable ], fun atoms ->
                let rest = local "__for_rest" ty
                match ty, pulledBy ty with
                | IrList elementTy, _ ->
                    let step (next: Var -> Expr) =
                        let head = element elementTy
                        let tail = local "__for_tail" ty
                        let taken = { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = andThen (next head) (Expr.make (Assign(rest, AVar tail)) IrUnit) }
                        Expr.make (Match([ rest ], [ taken; { Patterns = [ PWildcard ]; Body = stop } ])) IrUnit
                    [ rest, Expr.make (Atom atoms.Head) ty ], step
                | _, Some (IrFunction(_, result, wanted) as nextTy, elementTy, restTy) ->
                    let step (next: Var -> Expr) =
                        let pull = local "__for_pull" nextTy
                        let pulled = local "__for_pulled" result
                        let value = element elementTy
                        let following = local "__for_following" restTy
                        let taken =
                            { Patterns = [ PTag("next", Some (PFields [ 0, PBind value; 1, PBind following ])) ]
                              Body = andThen (next value) (Expr.make (Assign(rest, AVar following)) IrUnit) }
                        let matched = Expr.make (Match([ pulled ], [ taken; { Patterns = [ PWildcard ]; Body = stop } ])) IrUnit
                        letIn pull (Expr.make (Field(AVar rest, 0)) nextTy) (members wanted (fun memberAtoms ->
                            letIn pulled (Expr.make (CallClosure(pull, memberAtoms, Borrowing)) result) matched))
                    [ rest, Expr.make (Atom atoms.Head) ty ], step
                | other, _ -> cannot $"a `for` loop over a value of type '{IrType.show other}'"
    let names = parameters |> List.map fst
    let walkers = List.map2 walker iterables names
    atoms (walkers |> List.collect fst) (fun atoms ->
        let walking, steps =
            walkers
            |> List.mapFold (fun (remaining: Atom list) (operands, make) ->
                make (List.take operands.Length remaining), List.skip operands.Length remaining) atoms
            |> fst
            |> List.unzip
        // Each step binds its element and passes it on to the next, and the last runs the body with all of them.
        let rec run (remaining: ((Var -> Expr) -> Expr) list) (bound: Map<string, Var>) (names: Identifier list) =
            match remaining, names with
            | step :: rest, name :: others -> step (fun element -> run rest (bound |> Map.add name element) others)
            | _ -> lowerBody bound
        let loop = Expr.make (While { Condition = Expr.make (Atom(AVar going)) IrBool; Depth = depth; Body = run steps vars names }) IrUnit
        k (List.foldBack (fun (var, value) body -> letIn var value body) (List.concat walking) (letIn going (Expr.make (Atom(ABool true)) IrBool) loop)))

/// `while condition -> body`, from the lowered condition and body, which run again each time round. `depth` is how many
/// loops enclose this one, which names its labels.
let whileLoop (depth: int) (condition: Expr) (body: Expr) =
    Expr.make (While { Condition = condition; Depth = depth; Body = body }) IrUnit
