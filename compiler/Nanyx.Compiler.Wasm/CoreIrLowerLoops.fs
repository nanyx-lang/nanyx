/// Lowers loops (see the loops page of the docs) to the core IR's loop nodes.
module internal Transpiler.Wasm.CoreIrLowerLoops

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

/// Whether `expr`, which a `for` loop walks, is a map or a set, whose store the loop walks (see `StoreWalk`), and
/// whether a set. `sourceTypeOf` gives an expression's type as the type checker has it.
let walksStore (sourceTypeOf: Expression -> Ty option) (expr: Expression) : bool option =
    match sourceTypeOf expr with
    | Some (Maps.MapType _) -> Some false
    | Some (Sets.SetType _) -> Some true
    | _ -> None

/// A map or a set walked in step with a sequence is walked as the list its `toList` gives: for such an iterable, the
/// call of `toList` on its store. `callee` names the function to call at a type, and `toIrType` lowers a type.
let storeAsList
    (sourceTypeOf: Expression -> Ty option)
    (callee: string -> Ty -> string)
    (toIrType: Ty -> IrType)
    (iterable: Expression)
    : (Atom -> Expr) option =
    match sourceTypeOf iterable with
    | Some (Maps.MapType(key, value) as ty) ->
        Some (ty, Maps.toList, TyRecord(Map.ofList [ "0", key; "1", value ], FieldOrder.none))
    | Some (Sets.SetType element as ty) -> Some (ty, Sets.toList, element)
    | _ -> None
    |> Option.map (fun (ty, toList, elementTy) ->
        let listTy = Lists.ofType elementTy
        let name = callee toList (TyFunc(ty, listTy, []))
        fun (store: Atom) -> Expr.make (Call(name, [ store ])) (toIrType listTy))

/// `for xs, ys | arms`: the body runs for each step, with each iterable's element in a local the body names by its
/// parameter. A range counts from its start (to its end, if it has one); a list walks its cells; a map's or a set's
/// store walks its trie (see `StoreWalk`), which `walksStore` says an iterable is, and whether a set's. `depth` is how many
/// loops enclose this one, which names its locals; `atoms` lowers the iterables' operands, and `lowerBody` the body,
/// given `vars` with each parameter bound to its element's local.
let forEach
    (newVar: string -> VarKind -> IrType -> Var)
    (tyOf: Expression -> IrType)
    (walksStore: Expression -> bool option)
    (atoms: Expression list -> (Atom list -> Expr) -> Expr)
    (lowerBody: Map<string, Var> -> Expr)
    (vars: Map<string, Var>)
    (depth: int)
    (iterables: Expression list)
    (parameters: (Identifier * TypeExpr option) list)
    (k: Expr -> Expr) =
    // The first stream's locals keep the names a loop over one iterable has always had.
    let suffix index = if index = 0 then "" else $"_{index}"
    let mutable stores = 0
    // The locals that walk a store, named as the stream's own are.
    let storeWalk (storeTy: IrType) (keys: bool) : StoreWalk =
        stores <- stores + 1
        let local (name: string) ty = newVar $"__for_store_{name}_{depth}_{stores}" ForLoop ty
        { StoreType = storeTy
          Keys = keys
          Root = local "root" IrWord
          Pending = local "pending" IrWord
          Node = local "node" IrWord
          LeafBits = local "leafbits" IrWord
          Leaves = local "leaves" IrWord
          Remaining = local "remaining" IrWord
          LeafIndex = local "leafindex" IrWord
          LeafEnd = local "leafend" IrWord
          Depth = local "depth" IrWord
          Path = local "path" IrInt }
    // What a store's walk gives: a map's entry, or a set's element.
    let storeElement (storeTy: IrType) (keys: bool) =
        let entry = CoreIrLayout.StoreLayout.entry storeTy
        match entry with
        | IrRecord((_, key) :: _) when keys -> key
        | _ -> entry
    // Each iterable's operands, and how to make its source and element type from their atoms.
    let source (iterable: Expression) : Expression list * (Atom list -> ForSource * IrType) =
        match iterable.Kind with
        | RangeSyntax.Call(first, Some last) -> [ first; last ], fun atoms -> OverRange(atoms.[0], atoms.[1]), IrInt
        | RangeSyntax.Call(first, None) -> [ first ], fun atoms -> OverRangeFrom atoms.Head, IrInt
        | _ ->
            match tyOf iterable, walksStore iterable with
            | storeTy, Some keys -> [ iterable ], fun atoms -> OverStore(atoms.Head, storeWalk storeTy keys), storeElement storeTy keys
            | IrList elementTy, None -> [ iterable ], fun atoms -> OverList atoms.Head, elementTy
            | other, None -> cannot $"a `for` loop over a value of type '{IrType.show other}'"
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
    | IrRecord [ _, (IrFunction(IrUnit, IrTags tags, _, _) as next) ] when tags |> List.exists (fun (name, payload) -> name = NyxCompiler.Seqs.doneTag && payload.IsNone) ->
        match tags |> List.tryFind (fst >> (=) NyxCompiler.Seqs.nextTag) |> Option.bind snd with
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
    (storeAsList: Expression -> (Atom -> Expr) option)
    (atoms: Expression list -> (Atom list -> Expr) -> Expr)
    (members: (ContextMemberKey * IrType) list -> (Atom list -> Expr) -> Expr)
    (lowerBody: Map<string, Var> -> Expr)
    (vars: Map<string, Var>)
    (depth: int)
    (iterables: Expression list)
    (parameters: (Identifier * TypeExpr option) list)
    (k: Expr -> Expr) =
    let going = local "__for_going" IrBool
    let stop = Expr.make (Assign(going, ABool false)) IrUnit
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
                    let moveOn = Expr.letIn following (Expr.make (Prim(Binary "+", [ AVar counter; AInt 1L ])) IrInt) (Expr.make (Assign(counter, AVar following)) IrUnit)
                    let body = Expr.letIn value (Expr.make (Atom(AVar counter)) IrInt) (Expr.doThen (next value) moveOn)
                    if last.IsNone then body
                    else
                        let past = local "__for_past" IrBool
                        Expr.letIn past (Expr.make (Prim(Binary ">", [ AVar counter; AVar finish ])) IrBool) (Expr.make (If(AVar past, stop, body)) IrUnit)
                walking, step
        | _ ->
            // A map or a set walked in step with a sequence is walked as the list its `toList` gives.
            let asList = storeAsList iterable
            let ty = match asList with Some toList -> (toList AUnit).Type | None -> tyOf iterable
            [ iterable ], fun atoms ->
                let rest = local "__for_rest" ty
                let start = match asList with Some toList -> toList atoms.Head | None -> Expr.make (Atom atoms.Head) ty
                match ty, pulledBy ty with
                | IrList elementTy, _ ->
                    let step (next: Var -> Expr) =
                        let head = element elementTy
                        let tail = local "__for_tail" ty
                        let taken = { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = Expr.doThen (next head) (Expr.make (Assign(rest, AVar tail)) IrUnit) }
                        Expr.make (Match([ rest ], [ taken; { Patterns = [ PWildcard ]; Body = stop } ])) IrUnit
                    [ rest, start ], step
                | _, Some (IrFunction(_, result, wanted, _) as nextTy, elementTy, restTy) ->
                    let step (next: Var -> Expr) =
                        let pull = local "__for_pull" nextTy
                        let pulled = local "__for_pulled" result
                        let value = element elementTy
                        let following = local "__for_following" restTy
                        let taken =
                            { Patterns = [ PTag(NyxCompiler.Seqs.nextTag, Some (PFields [ 0, PBind value; 1, PBind following ])) ]
                              Body = Expr.doThen (next value) (Expr.make (Assign(rest, AVar following)) IrUnit) }
                        let matched = Expr.make (Match([ pulled ], [ taken; { Patterns = [ PWildcard ]; Body = stop } ])) IrUnit
                        Expr.letIn pull (Expr.make (Field(AVar rest, 0)) nextTy) (members wanted (fun memberAtoms ->
                            Expr.letIn pulled (Expr.make (CallClosure(pull, memberAtoms, Borrowing)) result) matched))
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
        k (List.foldBack (fun (var, value) body -> Expr.letIn var value body) (List.concat walking) (Expr.letIn going (Expr.make (Atom(ABool true)) IrBool) loop)))

/// `while condition -> body`, from the lowered condition and body, which run again each time round. `depth` is how many
/// loops enclose this one, which names its labels.
let whileLoop (depth: int) (condition: Expr) (body: Expr) =
    Expr.make (While { Condition = condition; Depth = depth; Body = body }) IrUnit
