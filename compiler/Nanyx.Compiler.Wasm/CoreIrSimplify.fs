/// Simplifying what inlining exposes in the core IR (see "What inlining exposes" in
/// `docs/src/content/deep-dives/inlining.md`), between the inliner's rounds and before reference counting:
///
/// - a call through a closure known at that point is a direct call to the function the closure is over, with the
///   closure as its environment, which the inliner may inline in turn;
/// - a field read from a known closure's environment is the value the closure captured there, and one read from a
///   record made in the function that nothing but such reads, and matches that take it apart, uses is the value it was
///   made with, so the record is not made (as a function whose parameter is a tuple makes one of its elements, #1884);
/// - a `match` takes the one arm its known scrutinees match, leaves out the arms they cannot, and no longer tests a
///   scrutinee that every arm matches whatever it is;
/// - the code after an `if` or a `match` that gives a tag is copied into each branch when it matches on that tag, where
///   the tag is known, if the copies come to little (case-of-case): how a box made in a branch meets its reading;
/// - a variable given another's value is that other, and a chain that ends in a variable given a value ends in it;
/// - a binding that nothing reads, of a value that has no effect, is not made;
/// - once nothing more is inlined, a local read once and at once is a temporary (`withTemporaries`).
///
/// What is known about a variable comes from its binding (a closure, a tag, another variable) and from the arm it is
/// in (the tag its pattern tests, and those the arms before it tested and failed). A value is read again somewhere else
/// only from a variable that no other variable shares a local with and that nothing assigns, never from a temporary,
/// which is used once, where it is computed.
module internal Transpiler.Wasm.CoreIrSimplify

open System.Collections.Generic
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrRewrite

/// What is known about a variable's value.
type private Fact =
    /// A closure over the lifted function of this name, and what it captures, each where it can be read again.
    | Closure of string * Atom option list
    /// A tag, and its payload where it can be read again.
    | Tag of string * Atom option
    /// A record that nothing but reads of its fields and matches that take it apart use, and each of its fields where it
    /// can be read again. One used otherwise may be written where it is passed, as a cell is.
    | Record of Atom option list
    /// None of these tags.
    | NotTags of Set<string>

/// Whether a value matches a pattern, as far as is known.
type private Decision =
    /// It does, and the pattern binds these variables to these values.
    | Matches of (Var * Atom) list
    | Fails
    | Unknown

/// Whether evaluating `e` has no effect but its value: it writes nothing, calls nothing and cannot trap.
let rec private isPure (e: Expr) =
    match e.Kind with
    | Atom _
    | MakeTag _
    | MakeRecord _
    | MakeClosure _
    | Field _
    | MakeList _
    | FloatConstant _
    | Interpolate _
    | Prim(Not, _)
    | Prim(FloatBinary _, _) -> true
    | Prim(Binary op, _) -> Expr.safeOperators.Contains op
    | Let _
    | Do _
    | If _
    | Match _ -> Expr.children e |> List.forall isPure
    | _ -> false

/// `body` with each variable let-bound to its value first.
let private bindAll (bindings: (Var * Atom) list) (body: Expr) : Expr =
    List.foldBack (fun (var: Var, value) rest -> Expr.make (Let(var, Expr.make (Atom value) var.Type, rest)) rest.Type) bindings body

/// Whether a value of `taken` can stand where one of `ty` was: they are held alike.
let private heldAlike (taken: IrType) (ty: IrType) = CoreIrLayout.valueType taken = CoreIrLayout.valueType ty

/// How often each variable is read in `e`.
let private reads (e: Expr) : Dictionary<int, int> =
    let counts = Dictionary<int, int>()
    let read (v: Var) = counts.[v.Id] <- (match counts.TryGetValue v.Id with | true, n -> n + 1 | _ -> 1)
    for e in allExprs e do
        for atom in directAtoms e do
            match atom with
            | AVar v -> read v
            | _ -> ()
        directVars e |> List.iter read
    counts

/// `e` without the bindings that nothing reads of values that have no effect, and without the `do`s of such values. A
/// temporary that nothing reads is evaluated for its effects with a `do`, since every temporary must be used.
let private withoutUnread (e: Expr) : Expr * bool =
    let counts = reads e
    let mutable changed = false
    let rec strip (e: Expr) : Expr =
        let links, last = Chain.split e
        links
        |> Chain.rebuild
            (fun link following ->
                match link.Kind with
                | Let(var, value, _) when not (counts.ContainsKey var.Id) ->
                    if isPure value then
                        changed <- true
                        following
                    elif var.Kind = Temp then
                        changed <- true
                        doValue (strip value) following
                    else { link with Kind = Let(var, strip value, following) }
                | Let(var, value, _) -> { link with Kind = Let(var, strip value, following) }
                | Do(value, _) when isPure value ->
                    changed <- true
                    following
                | Do(value, _) -> { link with Kind = Do(strip value, following) }
                | _ -> Chain.relink link following)
        <| mapNested strip last
    let stripped = strip e
    stripped, changed

/// `e` with each local that is read once, by what the next link stores or does or by what the chain ends in, made a
/// temporary, which is computed where it is read and needs no local. Nothing happens between the two, so nothing moves
/// past it; the reader must not be a temporary, whose value is computed later still, where it is read, unless it is one
/// made here, which is read at once too.
let withTemporaries (e: Expr) : Expr =
    let counts = reads e
    let readOnce (var: Var) =
        match counts.TryGetValue var.Id with
        | true, 1 -> true
        | _ -> false
    let isSimple (value: Expr) =
        match value.Kind with
        | Let _
        | Do _
        | If _
        | Match _
        | ForEach _
        | While _
        | Crash _
        | CrashWith _ -> false
        | _ -> true
    // The locals made temporaries: one is read at once, so a local it reads can be one too.
    let made = HashSet<int>()
    let readBy (var: Var) (reader: Expr) = directAtoms reader |> List.exists (function AVar v -> v.Id = var.Id | _ -> false)
    let rec walk (e: Expr) : Expr =
        let links, last = Chain.split e
        links
        |> Chain.rebuild
            (fun link following ->
                match link.Kind with
                | Let(var, value, _) ->
                    let value = walk value
                    let reader =
                        match following.Kind with
                        | Let(next, nextValue, _) when next.Kind <> Temp || made.Contains next.Id -> Some nextValue
                        | Do(nextValue, _) -> Some nextValue
                        | Let _ -> None
                        | _ when (Chain.next following).IsNone -> Some following
                        | _ -> None
                    match reader with
                    | Some reader when var.Kind = Local && readOnce var && isSimple value && isSimple reader && readBy var reader ->
                        let temp = { var with Kind = Temp }
                        made.Add temp.Id |> ignore
                        let renamed = mapVars (fun v -> if v.Id = var.Id then temp else v) id reader
                        let following =
                            match following.Kind with
                            | Let(next, _, after) -> { following with Kind = Let(next, renamed, after) }
                            | Do(_, after) -> { following with Kind = Do(renamed, after) }
                            | _ -> renamed
                        { link with Kind = Let(temp, value, following) }
                    | _ -> { link with Kind = Let(var, value, following) }
                | Do(value, _) -> { link with Kind = Do(walk value, following) }
                | _ -> Chain.relink link following)
        <| mapNested walk last
    walk e

/// How many core IR nodes the code after a branch may grow by when it is copied into each branch (see `Simplifier.Sequence`).
let private copyLimit = 40

/// The variables bound in `e`: by a `let`, a pattern, a guard or a loop.
let private boundIn (e: Expr) : Var list =
    allExprs e
    |> Seq.collect (fun e ->
        match e.Kind with
        | Let(v, _, _) -> [ v ]
        | Match(_, arms) ->
            arms |> List.collect (fun arm -> arm.Patterns |> List.collect (fun p -> Pattern.binds p @ List.map fst (Pattern.guards p)))
        | ForEach loop -> ForLoop.locals loop
        | _ -> [])
    |> List.ofSeq

/// Whether `rest` matches on the value of `var`, or of a variable the chain gives that value to.
let private matchesOn (var: Var) (rest: Expr) =
    let links, _ = Chain.split rest
    let names =
        links
        |> List.fold
            (fun (names: Set<int>) link ->
                match link.Kind with
                | Let(alias, { Kind = Atom(AVar v) }, _) when names.Contains v.Id -> names.Add alias.Id
                | _ -> names)
            (Set.singleton var.Id)
    allExprs rest
    |> Seq.exists (fun e ->
        match e.Kind with
        | Match(scrutinees, _) -> scrutinees |> List.exists (fun s -> names.Contains s.Id)
        | _ -> false)

/// Whether what is known about a variable of type `ty` can tell something: not a number, a string or a `bool`, whose
/// values the simplifier knows nothing of.
let private carriesFacts (ty: IrType) =
    match ty with
    | IrInt
    | IrSized _
    | IrBool
    | IrUnit
    | IrString
    | IrFloat
    | IrF32 -> false
    | _ -> true

/// The sizes of a chain's links and who reads what in them, worked out once for the chain, so that the case-of-case
/// rule can tell that copying the rest of the chain into each branch would grow it too much without going over the rest
/// at each link: a chain of n tags made and matched took n² time (#1847). Places are the links' indexes, and `n` is
/// what the chain ends in.
type private ChainShape(links: Expr list, last: Expr) =
    let links = Array.ofList links
    let n = links.Length
    let partAt place = if place = n then last else Chain.relink links.[place] (Expr.make (Atom AUnit) IrUnit)
    // Each link's own nodes, without what follows it.
    let own =
        links
        |> Array.map (fun link ->
            match link.Kind with
            | Let(_, value, _)
            | Do(value, _) -> 1 + size value
            | _ -> 1)
    // The nodes from each place to the end of the chain.
    let fromPlace =
        let sizes = Array.zeroCreate (n + 1)
        sizes.[n] <- size last
        for place in n - 1 .. -1 .. 0 do
            sizes.[place] <- own.[place] + sizes.[place + 1]
        sizes
    // The nodes of each link that a copy keeps whatever is known: none of a variable given another's value, which the
    // simplifier reads as that other.
    let kept =
        links
        |> Array.mapi (fun place link ->
            match link.Kind with
            | Let(_, { Kind = Atom _ }, _) -> 0
            | _ -> own.[place])
    let keptFrom =
        let sizes = Array.zeroCreate (n + 1)
        sizes.[n] <- size last
        for place in n - 1 .. -1 .. 0 do
            sizes.[place] <- kept.[place] + sizes.[place + 1]
        sizes
    // The variables each place reads, the places that read each variable, and the place that binds each variable.
    let readsAt =
        Array.init (n + 1) (fun place ->
            let read = HashSet<int>()
            for e in allExprs (partAt place) do
                for atom in directAtoms e do
                    match atom with
                    | AVar v -> read.Add v.Id |> ignore
                    | _ -> ()
                for v in directVars e do
                    read.Add v.Id |> ignore
            read)
    let readers = Dictionary<int, ResizeArray<int>>()
    do
        for place in 0..n do
            for id in readsAt.[place] do
                match readers.TryGetValue id with
                | true, places -> places.Add place
                | _ -> readers.[id] <- ResizeArray [ place ]
    let binders = Dictionary<int, int>()
    do
        links
        |> Array.iteri (fun place link ->
            match link.Kind with
            | Let(var, _, _) -> binders.[var.Id] <- place
            | _ -> ())

    /// Whether the chain from `start` on, copied into `branches` branches that each know the tag `var` is, could come
    /// to no more than its own size and `limit` more. What may come to little in a copy is the places that read `var`,
    /// or a variable such a place binds that facts can be known of, the places whose variable only those read, and the
    /// variables given another's value; the rest is copied whole into each branch.
    member _.MayCopy (var: Var) (start: int) (branches: int) (limit: int) : bool =
        let changing = HashSet<int>()
        let pending = Stack<int>([ var.Id ])
        while pending.Count > 0 do
            match readers.TryGetValue(pending.Pop()) with
            | true, places ->
                for place in places do
                    if place >= start && changing.Add place && place < n then
                        match links.[place].Kind with
                        | Let(bound, _, _) when carriesFacts bound.Type -> pending.Push bound.Id
                        | _ -> ()
            | _ -> ()
        let unread = Stack<int>(changing)
        while unread.Count > 0 do
            for id in readsAt.[unread.Pop()] do
                match binders.TryGetValue id with
                | true, binder when
                    binder >= start
                    && not (changing.Contains binder)
                    && readers.[id] |> Seq.forall changing.Contains
                    ->
                    changing.Add binder |> ignore
                    unread.Push binder
                | _ -> ()
        let unchanged = keptFrom.[start] - (changing |> Seq.sumBy (fun place -> if place = n then keptFrom.[n] else kept.[place]))
        branches * unchanged <= fromPlace.[start] + limit

/// The most temporaries a record made of temporaries can have for them to be made locals (see `RecordOfTemps`).
let private recordTemps = 32

/// One pass of the simplifier over the body of `f`, given the functions it may call.
type private Simplifier(functions: Dictionary<string, Function>, f: Function) =
    // How often each kind and name of variable is bound in the function: those of a variable that is the only one with
    // its local.
    let bound = Dictionary<VarKind * string, int>()
    // The variables something assigns or writes into, whose value is not known from their binding.
    let assigned = HashSet<int>()
    // The variables read other than as the record a field is read from or a match takes apart.
    let usedWhole = HashSet<int>()
    let mutable changed = false
    // Whether the code after a branch is being tried in each branch, inside which it is not tried again.
    let mutable trying = false
    let mutable nextId =
        maxVarId (f.Params |> List.fold (fun acc p -> max acc p.Id) 0) f.Body
    do
        let bind (v: Var) =
            bound.[(v.Kind, v.Name)] <- (match bound.TryGetValue((v.Kind, v.Name)) with | true, n -> n + 1 | _ -> 1)
        f.Params |> List.iter bind
        for e in allExprs f.Body do
            match e.Kind with
            | Let(v, _, _) -> bind v
            | Match(_, arms) ->
                for arm in arms do
                    for pattern in arm.Patterns do
                        Pattern.binds pattern |> List.iter bind
                        Pattern.guards pattern |> List.iter (fst >> bind)
            | ForEach loop -> ForLoop.locals loop |> List.iter bind
            | Assign(v, _)
            | SetField(v, _, _) -> assigned.Add v.Id |> ignore
            | _ -> ()
            match e.Kind with
            | Field _ -> ()
            | Match(scrutinees, arms) ->
                scrutinees
                |> List.iteri (fun index scrutinee ->
                    let takesApart (arm: Arm) =
                        match arm.Patterns.[index] with
                        | PFields _
                        | PWildcard -> true
                        | _ -> false
                    if not (List.forall takesApart arms) then usedWhole.Add scrutinee.Id |> ignore)
            | _ ->
                for atom in directAtoms e do
                    match atom with
                    | AVar v -> usedWhole.Add v.Id |> ignore
                    | _ -> ()
                directVars e |> List.iter (fun v -> usedWhole.Add v.Id |> ignore)

    /// Whether `atom` can be read again where it was not: a constant, or a variable that nothing assigns, whose local
    /// it has to itself, and that is a parameter, a local or a name a pattern binds: not a temporary, which is used once,
    /// nor a loop's own local, which its body reads through a binding of its own.
    let readable (atom: Atom) =
        match atom with
        | AVar v ->
            (match v.Kind with
             | Param
             | Local
             | MatchTemp
             | PatternBound -> true
             | Temp
             | InterpolationHole _
             | Scratch
             | ForLoop -> false)
            && not (assigned.Contains v.Id)
            && (match bound.TryGetValue((v.Kind, v.Name)) with
                | true, 1 -> true
                | _ -> false)
        | _ -> true

    let known (atom: Atom) = if readable atom then Some atom else None

    /// What is known about the value of `atom`.
    let factOf (facts: Map<int, Fact>) (atom: Atom) : Fact option =
        match atom with
        | AVar v -> facts.TryFind v.Id
        | ATag name -> Some(Tag(name, None))
        | AStaticClosure(name, _, _) -> Some(Closure(name, []))
        | _ -> None

    /// What is known about the variable `var` bound to `value`.
    let factOfValue (facts: Map<int, Fact>) (var: Var) (value: Expr) : Fact option =
        match value.Kind with
        | MakeClosure(name, _, captured) -> Some(Closure(name, List.map known captured))
        | MakeTag(name, payload) -> Some(Tag(name, known payload))
        | MakeRecord fields when not (usedWhole.Contains var.Id) -> Some(Record(fields |> List.map (snd >> known)))
        | Atom atom -> factOf facts atom
        | _ -> None

    /// `facts` with what is known about `var` added, unless something assigns it.
    let learn (var: Var) (fact: Fact option) (facts: Map<int, Fact>) =
        match fact with
        | Some fact when not (assigned.Contains var.Id) -> facts.Add(var.Id, fact)
        | _ -> facts

    /// Whether `value` matches `pattern`, as far as `facts` tell.
    let rec decide (facts: Map<int, Fact>) (pattern: Pattern) (value: Atom) : Decision =
        match pattern, value with
        | PWildcard, _ -> Matches []
        | PBind var, _ -> Matches [ var, value ]
        | PInt expected, (AInt actual | ASized(_, actual)) -> if expected = actual then Matches [] else Fails
        | PBool expected, ABool actual -> if expected = actual then Matches [] else Fails
        | PString expected, AString actual -> if expected = actual then Matches [] else Fails
        | PTag(name, inner), _ ->
            match factOf facts value, inner with
            | Some(Tag(actual, _)), _ when actual <> name -> Fails
            | Some(Tag _), (None | Some PWildcard) -> Matches []
            | Some(Tag(_, Some payload)), Some inner -> decide facts inner payload
            | Some(NotTags excluded), _ when excluded.Contains name -> Fails
            | _ -> Unknown
        | PFields fields, _ ->
            match factOf facts value with
            | Some(Record known) ->
                fields
                |> List.map (fun (slot, inner) ->
                    match List.tryItem slot known with
                    | Some(Some field) -> decide facts inner field
                    | _ -> Unknown)
                |> combine
            | _ -> Unknown
        | PAll parts, _ -> parts |> List.map (fun part -> decide facts part value) |> combine
        | _ -> Unknown

    /// Whether values match all of their patterns, given whether each matches its own.
    and combine (decisions: Decision list) : Decision =
        if decisions |> List.contains Fails then Fails
        elif decisions |> List.forall (function Matches _ -> true | _ -> false) then
            Matches(decisions |> List.collect (function Matches bindings -> bindings | _ -> []))
        else Unknown

    /// What an arm with these patterns tells about its scrutinees once it is taken.
    let armFacts (facts: Map<int, Fact>) (scrutinees: Var list) (patterns: Pattern list) =
        List.fold2
            (fun facts (scrutinee: Var) pattern ->
                match pattern with
                | PTag(name, Some(PBind payload)) -> learn scrutinee (Some(Tag(name, known (AVar payload)))) facts
                | PTag(name, _) -> learn scrutinee (Some(Tag(name, None))) facts
                | _ -> facts)
            facts
            scrutinees
            patterns

    /// What an arm with these patterns tells about its scrutinees once it has been tested and not taken: when only one
    /// of its patterns can fail, and that one only tests a tag, the scrutinee is not that tag.
    let failedArmFacts (facts: Map<int, Fact>) (scrutinees: Var list) (patterns: Pattern list) =
        let irrefutable pattern =
            match pattern with
            | PWildcard
            | PBind _ -> true
            | _ -> false
        match List.zip scrutinees patterns |> List.filter (snd >> irrefutable >> not) with
        | [ scrutinee, PTag(name, inner) ] when inner |> Option.forall irrefutable ->
            let excluded =
                match facts.TryFind scrutinee.Id with
                | Some(NotTags excluded) -> excluded
                | _ -> Set.empty
            learn scrutinee (Some(NotTags(excluded.Add name))) facts
        | _ -> facts

    member _.Changed = changed

    /// When `link` and the links after it in `remaining` bind temporaries one after another that are, in that order,
    /// the temporaries a record bound next to a variable is made of, and the record is only read a field at a time:
    /// how many of `remaining` that takes, and the links again, from `link` to the record's, with the temporaries made
    /// locals. A record used whole is left as it is, since what is read from it can't be known.
    member private _.RecordOfTemps (link: Expr) (remaining: Expr list) : (int * Expr list) option =
        // At most `recordTemps` temporaries are looked through, so that a long chain of temporaries, as `1 + 1 + ...`
        // makes, isn't looked through from each of them (#2351).
        let rec collect (bound: Expr list) (rest: Expr list) =
            match rest with
            | _ when bound.Length > recordTemps -> None
            | ({ Kind = Let(var, { Kind = MakeRecord fields }, _) } as record) :: _ when var.Kind <> Temp && not (usedWhole.Contains var.Id) ->
                let temps = bound |> List.rev |> List.map (fun link -> match link.Kind with Let(temp, _, _) -> temp.Id | _ -> -1)
                let read = fields |> List.choose (function _, AVar v when v.Kind = Temp -> Some v.Id | _ -> None)
                if read = temps then Some(List.rev bound, record) else None
            | ({ Kind = Let(temp, _, _) } as next) :: rest when temp.Kind = Temp -> collect (next :: bound) rest
            | _ -> None
        match collect [ link ] remaining with
        | Some(temps, record) ->
            let locals =
                temps
                |> List.map (fun link -> match link.Kind with Let(temp, _, _) -> temp.Id, { temp with Kind = Local } | _ -> internalError "a link that binds no temporary")
                |> Map.ofList
            let local (atom: Atom) = match atom with AVar v when locals.ContainsKey v.Id -> AVar locals.[v.Id] | other -> other
            let relinked =
                temps
                |> List.map (fun link -> match link.Kind with Let(temp, value, next) -> { link with Kind = Let(locals.[temp.Id], value, next) } | _ -> link)
            let recordLink =
                match record.Kind with
                | Let(var, ({ Kind = MakeRecord fields } as value), next) ->
                    { record with Kind = Let(var, { value with Kind = MakeRecord(fields |> List.map (fun (name, atom) -> name, local atom)) }, next) }
                | _ -> record
            Some(temps.Length, relinked @ [ recordLink ])
        | None -> None

    /// A chain, with what each of its bindings tells known in what follows it.
    member this.Sequence (facts: Map<int, Fact>) (e: Expr) : Expr =
        let links, chainEnd = Chain.split e
        let rebuilt = ResizeArray<Expr -> Expr>()
        let mutable facts = facts
        let mutable remaining = links
        // How many links are left: the rules below rewrite only the links at the head, so what follows the link being
        // gone over is the chain's own links from place `count - left` on. The count is taken once: a list's length is
        // counted along it, which at each link made a chain of n links take n² time (#2488).
        let count = links.Length
        let mutable left = count
        let shape = lazy (ChainShape(links, chainEnd))
        let mutable last = chainEnd
        let mutable branchedLast = None
        // The atom each variable bound in this chain to one is given.
        let aliases = Dictionary<int, Atom>()
        // The variable each variable this chain gave another's value to is read as, put in each link as it is reached
        // rather than in the whole rest of the chain at once, which made a function of n such links take n² time.
        let renamed = Dictionary<int, Var>()
        let rename (e: Expr) =
            if renamed.Count = 0 then e
            else mapVars (fun v -> match renamed.TryGetValue v.Id with | true, source -> source | _ -> v) id e
        // A link with its own parts renamed, not what follows it.
        let renameLink (link: Expr) =
            match Chain.next link with
            | Some following when renamed.Count > 0 -> Chain.relink (rename (Chain.relink link (Expr.make (Atom AUnit) IrUnit))) following
            | _ -> link
        while not remaining.IsEmpty do
            let link = renameLink remaining.Head
            remaining <- remaining.Tail
            left <- left - 1
            match link.Kind, remaining with
            // A temporary given at once to a variable is that variable's value itself.
            | Let(temp, value, _), ({ Kind = Let(var, { Kind = Atom(AVar read) }, _) } as next) :: rest when temp.Kind = Temp && read.Id = temp.Id ->
                changed <- true
                remaining <- { next with Kind = Let(var, value, next) } :: rest
            // A temporary a tag is made of, given to a variable at once, is a local, so that the payload can be read
            // again where the tag is matched. No other temporary waits where it is stored, since none waits where the
            // tag is.
            | Let(temp, value, _), ({ Kind = Let(var, ({ Kind = MakeTag(name, AVar read) } as tag), _) } as next) :: rest
                when temp.Kind = Temp && read.Id = temp.Id && var.Kind <> Temp ->
                changed <- true
                let local = { temp with Kind = Local }
                remaining <- { link with Kind = Let(local, value, link) } :: { next with Kind = Let(var, { tag with Kind = MakeTag(name, AVar local) }, next) } :: rest
                left <- left + 1
            // The temporaries a record is made of, bound one after another just before it in the order it reads them,
            // with the record given to a variable, are locals, so that a field read from the record is the value it
            // was made with: a context instance's member, a closure, is then called directly (#2578). They are
            // computed in the same order either way.
            | Let(temp, _, _), _ when temp.Kind = Temp && (this.RecordOfTemps link remaining).IsSome ->
                changed <- true
                let count, rebound = (this.RecordOfTemps link remaining).Value
                remaining <- rebound @ List.skip count remaining
                left <- left + 1
            // A variable given the value of another of its type is that other: what follows reads it instead. Not a
            // parameter on the heap, which its function may only borrow: the variable is what takes it over, so that a
            // match on it can reuse its memory (see `CoreIrReuse`).
            | Let(var, { Kind = Atom(AVar source) }, _), _ when
                source.Type = var.Type
                && not (assigned.Contains var.Id)
                && readable (AVar source)
                && not (source.Kind = Param && CoreIrDrop.isHeapType source.Type)
                ->
                changed <- true
                renamed.[var.Id] <- source
            | Let(var, value, _), _ ->
                let value = this.Value facts value
                let following, chainEnd, start = remaining, last, count - left
                let mayCopy branches limit = shape.Value.MayCopy var start branches limit
                match this.Branched facts var value mayCopy (fun () -> Chain.rebuild Chain.relink following chainEnd |> rename), last.Kind with
                | Some branched, _ ->
                    remaining <- []
                    left <- 0
                    branchedLast <- Some branched
                // A chain that ends in the variable its last link binds ends in that link's value.
                | None, Atom(AVar result) when remaining.IsEmpty && result.Id = var.Id && heldAlike value.Type last.Type ->
                    changed <- true
                    branchedLast <- Some value
                | None, _ ->
                    facts <- learn var (factOfValue facts var value) facts
                    match value.Kind with
                    | Atom atom when not (assigned.Contains var.Id) -> aliases.[var.Id] <- atom
                    | _ -> ()
                    // A temporary's value is evaluated where it is used, so a chain that is its value stays whole there.
                    if var.Kind = Temp then rebuilt.Add(fun next -> Expr.make (Let(var, value, next)) next.Type)
                    else rebuilt.Add(fun next -> bindValue var value next)
            | Do(value, _), _ ->
                let value = this.Value facts value
                rebuilt.Add(fun next -> doValue value next)
            | _ -> rebuilt.Add(fun next -> Chain.relink link next)
        let last = if branchedLast.IsSome then last else rename last
        let last =
            match branchedLast, last.Kind with
            | Some branched, _ -> branched
            // A chain that ends in a variable it gives another's value to ends in that value.
            | None, Atom(AVar result) ->
                match aliases.TryGetValue result.Id with
                | true, value when readable value && heldAlike (atomType value) last.Type ->
                    changed <- true
                    { last with Kind = Atom value; Type = atomType value }
                | _ -> last
            | None, _ -> this.Value facts last
        Chain.rebuild (fun rebuild next -> rebuild next) (List.ofSeq rebuilt) last

    /// `let var = value in rest`, with `rest` copied into each branch of `value`, when `value` is a `match` or an `if`
    /// that gives a tag in some branch and `rest` matches on `var`: in each copy, which tag `var` is is known. It is
    /// done only when the copies, simplified, grow the code by no more than `copyLimit` nodes, as when all but one of
    /// them come to little. `mayCopy` tells, from the chain's shape, whether they could (see `ChainShape`), so that
    /// copies that cannot are never made.
    member this.Branched (facts: Map<int, Fact>) (var: Var) (value: Expr) (mayCopy: int -> int -> bool) (rest: unit -> Expr) : Expr option =
        let rec givesTag (branch: Expr) =
            match (snd (Chain.split branch)).Kind with
            | MakeTag _
            | Atom(ATag _) -> true
            | Match(_, arms) -> arms |> List.exists (fun arm -> givesTag arm.Body)
            | If(_, ifTrue, ifFalse) -> givesTag ifTrue || givesTag ifFalse
            | _ -> false
        let branches =
            match value.Kind with
            | Match(_, arms) -> arms |> List.map _.Body
            | If(_, ifTrue, ifFalse) -> [ ifTrue; ifFalse ]
            | _ -> []
        if trying || var.Kind = Temp || assigned.Contains var.Id || not (List.exists givesTag branches) then None
        elif not (mayCopy branches.Length (size value + copyLimit)) then None
        else
            let rest = rest ()
            if not (matchesOn var rest) then None
            else
                let wasChanged = changed
                trying <- true
                let into index (branch: Expr) =
                    let copy = bindValue var branch rest
                    if index = 0 then copy else this.Fresh copy
                let branched =
                    match value.Kind with
                    | Match(scrutinees, arms) -> Match(scrutinees, arms |> List.mapi (fun index arm -> { arm with Body = into index arm.Body }))
                    | If(condition, ifTrue, ifFalse) -> If(condition, into 0 ifTrue, into 1 ifFalse)
                    | kind -> kind
                let simplified = this.Value facts (Expr.make branched rest.Type) |> withoutUnread |> fst
                trying <- false
                if size simplified <= size value + size rest + copyLimit then
                    changed <- true
                    Some simplified
                else
                    changed <- wasChanged
                    None

    /// `e` with every variable it binds made new, under a name of its own, as a copy of `e` beside it needs.
    member _.Fresh (e: Expr) : Expr =
        let renamed = Dictionary<int, Var>()
        for v in boundIn e do
            if not (renamed.ContainsKey v.Id) then
                nextId <- nextId + 1
                let fresh = { v with Id = nextId; Name = $"{v.Name}__c{nextId}" }
                bound.[(fresh.Kind, fresh.Name)] <- 1
                if assigned.Contains v.Id then assigned.Add fresh.Id |> ignore
                renamed.[v.Id] <- fresh
        mapVars (fun v -> match renamed.TryGetValue v.Id with | true, fresh -> fresh | _ -> v) id e

    /// An expression, with what `facts` tell put to use in it.
    member this.Value (facts: Map<int, Fact>) (e: Expr) : Expr =
        match e.Kind with
        | Let _
        | Do _ -> this.Sequence facts e
        | CallClosure(closure, args, _) ->
            match facts.TryFind closure.Id with
            | Some(Closure(name, _)) ->
                match functions.TryGetValue name with
                | true, callee when callee.Params.Length = args.Length + 1 && callee.Result = e.Type ->
                    changed <- true
                    { e with Kind = Call(name, AVar closure :: args) }
                | _ -> e
            | _ -> e
        | Field(record, slot) when slot >= 1 ->
            match factOf facts record with
            | Some(Closure(_, captured)) when slot <= captured.Length ->
                match captured.[slot - 1] with
                | Some value when atomType value = e.Type ->
                    changed <- true
                    { e with Kind = Atom value }
                | _ -> e
            | _ -> e
        | Field(record, slot) ->
            match factOf facts record with
            | Some(Record fields) when slot < fields.Length ->
                match fields.[slot] with
                | Some value when atomType value = e.Type ->
                    changed <- true
                    { e with Kind = Atom value }
                | _ -> e
            | _ -> e
        | If(condition, ifTrue, ifFalse) -> { e with Kind = If(condition, this.Sequence facts ifTrue, this.Sequence facts ifFalse) }
        | Match(scrutinees, arms) -> this.Match facts e scrutinees arms
        | ForEach loop -> { e with Kind = ForEach { loop with Body = this.Sequence facts loop.Body } }
        | While loop -> { e with Kind = While { loop with Condition = this.Sequence facts loop.Condition; Body = this.Sequence facts loop.Body } }
        | _ -> e

    /// A `match`: without the scrutinees every arm matches whatever they are, and the arms its scrutinees cannot match,
    /// and the arm they are known to match in its place.
    member this.Match (facts: Map<int, Fact>) (e: Expr) (scrutinees: Var list) (arms: Arm list) : Expr =
        let untested =
            scrutinees
            |> List.mapi (fun index _ ->
                arms
                |> List.forall (fun arm ->
                    match arm.Patterns.[index] with
                    | PWildcard
                    | PBind _ -> true
                    | _ -> false))
        let scrutinees, arms =
            if not (List.contains true untested) then scrutinees, arms
            else
                changed <- true
                let tested items = List.zip untested items |> List.filter (fst >> not) |> List.map snd
                tested scrutinees,
                arms
                |> List.map (fun arm ->
                    let bindings =
                        List.zip3 untested arm.Patterns scrutinees
                        |> List.choose (function true, PBind var, scrutinee -> Some(var, AVar scrutinee) | _ -> None)
                    { Patterns = tested arm.Patterns; Body = bindAll bindings arm.Body })
        let decided =
            arms
            |> List.map (fun arm -> arm, List.map2 (fun pattern (scrutinee: Var) -> decide facts pattern (AVar scrutinee)) arm.Patterns scrutinees |> combine)
            |> List.filter (snd >> (<>) Fails)
        match decided with
        | (arm, Matches bindings) :: _ when heldAlike arm.Body.Type e.Type ->
            changed <- true
            this.Sequence facts (bindAll bindings arm.Body)
        | [] -> { e with Kind = Match(scrutinees, arms) }
        | _ ->
            if decided.Length < arms.Length then changed <- true
            let mutable failed = facts
            let arms =
                decided
                |> List.map (fun (arm, _) ->
                    let body = this.Sequence (armFacts failed scrutinees arm.Patterns) arm.Body
                    let hasGuard = arm.Patterns |> List.exists (Pattern.guards >> List.isEmpty >> not)
                    if not hasGuard then failed <- failedArmFacts failed scrutinees arm.Patterns
                    { arm with Body = body })
            { e with Kind = Match(scrutinees, arms) }

/// The most passes the simplifier makes over a body before it is given back as it is by then.
let private passes = 8

/// The body of `f` simplified, given the functions it may call as they are by now, and whether it is settled: whether the
/// last pass changed nothing, so that simplifying it again would give it back as it is. A
/// pass reads only the number of parameters and the result type of the functions it may call, which inlining into them
/// never changes, so a settled body stays settled until something else changes it (see `CoreIrInline`).
let simplify (functions: Dictionary<string, Function>) (f: Function) : Expr * bool =
    let mutable body = f.Body
    let mutable changed = true
    let mutable pass = 0
    while changed && pass < passes do
        let simplifier = Simplifier(functions, { f with Body = body })
        let simplified = simplifier.Sequence Map.empty body
        let stripped, didStrip = withoutUnread simplified
        changed <- simplifier.Changed || didStrip
        body <- stripped
        pass <- pass + 1
    body, not changed
