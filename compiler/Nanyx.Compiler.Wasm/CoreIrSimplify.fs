/// Simplifying what inlining exposes in the core IR (see "What inlining exposes" in
/// `docs/src/content/work-in-progress/inlining.md`), between the inliner's rounds and before reference counting:
///
/// - a call through a closure known at that point is a direct call to the function the closure is over, with the
///   closure as its environment, which the inliner may inline in turn;
/// - a field read from a known closure's environment is the value the closure captured there;
/// - a `match` takes the one arm its known scrutinees match, leaves out the arms they cannot, and no longer tests a
///   scrutinee that every arm matches whatever it is;
/// - a binding that nothing reads, of a value that has no effect, is not made.
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
    /// None of these tags.
    | NotTags of Set<string>

/// Whether a value matches a pattern, as far as is known.
type private Decision =
    /// It does, and the pattern binds these variables to these values.
    | Matches of (Var * Atom) list
    | Fails
    | Unknown

/// The operators that cannot trap: a value made with one of them that nothing reads need not be made.
let private safeOperators = set [ "=="; "!="; "<"; "<="; ">"; ">="; "and"; "or" ]

/// Whether evaluating `e` has no effect but its value: it writes nothing, calls nothing and cannot trap.
let rec private isPure (e: Expr) =
    match e.Kind with
    | Atom _
    | MakeTag _
    | MakeRecord _
    | MakeClosure _
    | Field _
    | MakeList _
    | BoxFloat _
    | Interpolate _
    | Prim(Not, _)
    | Prim(FloatBinary _, _) -> true
    | Prim(Binary op, _) -> safeOperators.Contains op
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

/// One pass of the simplifier over the body of `f`, given the functions it may call.
type private Simplifier(functions: Dictionary<string, Function>, f: Function) =
    // How often each kind and name of variable is bound in the function: those of a variable that is the only one with
    // its local.
    let bound = Dictionary<VarKind * string, int>()
    // The variables something assigns or writes into, whose value is not known from their binding.
    let assigned = HashSet<int>()
    let mutable changed = false
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

    /// Whether `atom` can be read again where it was not: a constant, or a variable that nothing assigns, whose local
    /// it has to itself, and that is not a temporary.
    let readable (atom: Atom) =
        match atom with
        | AVar v ->
            v.Kind <> Temp
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

    /// What is known about a variable bound to `value`.
    let factOfValue (facts: Map<int, Fact>) (value: Expr) : Fact option =
        match value.Kind with
        | MakeClosure(name, _, captured) -> Some(Closure(name, List.map known captured))
        | MakeTag(name, payload) -> Some(Tag(name, known payload))
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

    /// A chain, with what each of its bindings tells known in what follows it.
    member this.Sequence (facts: Map<int, Fact>) (e: Expr) : Expr =
        let links, last = Chain.split e
        let rebuilt = ResizeArray<Expr -> Expr>()
        let mutable facts = facts
        let mutable remaining = links
        while not remaining.IsEmpty do
            let link = remaining.Head
            remaining <- remaining.Tail
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
            | Let(var, value, _), _ ->
                let value = this.Value facts value
                facts <- learn var (factOfValue facts value) facts
                // A temporary's value is evaluated where it is used, so a chain that is its value stays whole there.
                if var.Kind = Temp then rebuilt.Add(fun next -> Expr.make (Let(var, value, next)) next.Type)
                else rebuilt.Add(fun next -> bindValue var value next)
            | Do(value, _), _ ->
                let value = this.Value facts value
                rebuilt.Add(fun next -> doValue value next)
            | _ -> rebuilt.Add(fun next -> Chain.relink link next)
        Chain.rebuild (fun rebuild next -> rebuild next) (List.ofSeq rebuilt) (this.Value facts last)

    /// An expression, with what `facts` tell put to use in it.
    member this.Value (facts: Map<int, Fact>) (e: Expr) : Expr =
        match e.Kind with
        | Let _
        | Do _ -> this.Sequence facts e
        | CallClosure(closure, args) ->
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

/// The most passes the simplifier makes over a body before it is given back as it is by then.
let private passes = 8

/// The body of `f` simplified, given the functions it may call as they are by now, and whether anything changed.
let simplify (functions: Dictionary<string, Function>) (f: Function) : Expr * bool =
    let mutable body = f.Body
    let mutable changed = true
    let mutable any = false
    let mutable pass = 0
    while changed && pass < passes do
        let simplifier = Simplifier(functions, { f with Body = body })
        let simplified = simplifier.Sequence Map.empty body
        let stripped, didStrip = withoutUnread simplified
        changed <- simplifier.Changed || didStrip
        any <- any || changed
        body <- stripped
        pass <- pass + 1
    body, any
