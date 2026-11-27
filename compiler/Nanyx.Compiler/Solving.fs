namespace NyxCompiler

open System.Collections.Generic
open Parser.Program
open NyxCompiler.Unifier

/// Solving a module's constraint list with `Unifier.unifyFrom`, remembering what lists were solved to so that a list
/// that extends a solved one is solved on top of it.
module Solving =

    /// Whether `found` holds for a variable that one of `tys` holds, or that is held by what `subst` binds such a
    /// variable to, and so on. Each variable is looked at once.
    let private reachesVar (subst: Subst) (found: int -> bool) (tys: Ty list) : bool =
        let seen = HashSet<int>()
        let rec visit ty =
            match ty with
            | TyVar v ->
                seen.Add v.Id
                && (found v.Id
                    || match subst.TryFind v.Id with
                       | Some bound -> visit bound
                       | None -> false)
            | _ -> Ty.exists visit ty
        List.exists visit tys

    let private sides (constraints: ConstraintSet) =
        constraints |> List.collect (fun (left, right, _, _) -> [ left; right ])

    /// Which constraints given after a solved list can be solved on top of what it solved to.
    type private Extension =
        | AnyAfter
        /// Those that reach none of these variables (see `reachesVar`), which the constraints it put off to the end reach.
        /// Solving them all afresh would solve the later constraints before those put off, but as they share no
        /// variable, which comes first does not show.
        | ApartFrom of HashSet<int>
        /// None: the order its own constraints were solved in showed otherwise.
        | NoneAfter

    /// What constraints given after a list solved to `subst`, in the order `order`, can be solved on top of it.
    let private extensionOf (subst: Subst) (order: SolveOrder) =
        match order with
        | Hidden -> AnyAfter
        | PutOff putOff ->
            let reached = HashSet<int>()
            reachesVar subst (fun id -> reached.Add id |> ignore; false) (sides putOff) |> ignore
            ApartFrom reached
        | Shown -> NoneAfter

    /// Whether `newer` can be solved on top of `subst`, which solved the constraints after them.
    let private extends (extension: Extension) (subst: Subst) (newer: ConstraintSet) =
        match extension with
        | AnyAfter -> true
        | ApartFrom reached -> not (reachesVar subst reached.Contains (sides newer))
        | NoneAfter -> false

    /// A list of constraints this thread solved, what solving them gave, where its made-up rest variables ended, and
    /// which constraints given after it can be solved on top of it.
    type private Solved = { Result: Result<Subst, Mismatch>; NextRestId: int; Extension: Extension }

    /// How many solved lists `solvedLists` keeps before it starts again.
    let private solvedListsKept = 64

    /// The lists of constraints this thread solved lately, by reference (see `unifyDetailed`).
    let private solvedLists =
        new System.Threading.ThreadLocal<Dictionary<ConstraintSet, Solved>>(fun () ->
            Dictionary<ConstraintSet, Solved>(HashIdentity.Reference))

    /// The constraints at the front of `constraints` that come before a list solved lately, and what that list solved
    /// to, if it agreed and they can be solved on top of it.
    let private solvedTail (solved: Dictionary<ConstraintSet, Solved>) (constraints: ConstraintSet) =
        let rec walk (newer: ConstraintSet) (remaining: ConstraintSet) =
            match solved.TryGetValue remaining with
            | true, { Result = Ok subst; NextRestId = restFrom; Extension = extension } when extends extension subst newer ->
                Some (List.rev newer, subst, restFrom, extension)
            | true, _ -> None
            | false, _ ->
                match remaining with
                | [] -> None
                | constraint :: rest -> walk (constraint :: newer) rest
        walk [] constraints

    /// Which constraints given after two lists solved one on top of the other, the older first, can be solved on top
    /// of both.
    let private both (older: Extension) (newer: Extension) =
        match older, newer with
        | NoneAfter, _
        | _, NoneAfter -> NoneAfter
        | AnyAfter, other
        | other, AnyAfter -> other
        | ApartFrom older, ApartFrom newer ->
            let reached = HashSet<int>(older)
            reached.UnionWith newer
            ApartFrom reached

    /// A list of constraints the type checker held when it had generalised a top-level def (see `checkpoint`): what
    /// solving it gave, and the variables its constraints name.
    type private Checkpoint = { Subst: Subst; NextRestId: int; Extension: Extension; Vars: Set<int> }

    /// The checkpoints, by their lists, kept as long as the lists are.
    let private checkpoints = System.Runtime.CompilerServices.ConditionalWeakTable<ConstraintSet, Checkpoint>()

    /// `known` with the variables `constraints` name.
    let private varIds (constraints: ConstraintSet) (known: Set<int>) =
        let rec add (acc: Set<int>) ty =
            match ty with
            | TyVar v -> acc.Add v.Id
            | _ -> Ty.fold add acc ty
        constraints |> List.fold (fun acc (left, right, _, _) -> add (add acc left) right) known

    /// Whether `constraints` name none of `vars`.
    let private apart (vars: Set<int>) (constraints: ConstraintSet) =
        let rec named ty =
            match ty with
            | TyVar v -> vars.Contains v.Id
            | _ -> Ty.exists named ty
        constraints |> List.forall (fun (left, right, _, _) -> not (named left || named right))

    /// The constraints at the front of `constraints` that come before its nearest checkpointed list, and that
    /// checkpoint.
    let private checkpointTail (constraints: ConstraintSet) =
        let rec walk (newer: ConstraintSet) (remaining: ConstraintSet) =
            match remaining with
            | [] -> None
            | constraint :: rest ->
                match checkpoints.TryGetValue remaining with
                | true, found -> Some (List.rev newer, found)
                | false, _ -> walk (constraint :: newer) rest
        walk [] constraints

    /// How many lists `unifyDetailed` solved from the start, where `countingFullSolves` asks for it.
    let private fullSolves = System.Threading.AsyncLocal<int ref option>()

    /// Runs `action`, giving what it gives and how many lists of constraints it solved from the start, rather than on
    /// top of one solved before (see `unifyDetailed`), on its own thread and those it starts: for the tests that check
    /// a module is solved a def at a time.
    let countingFullSolves (action: unit -> 'T) : 'T * int =
        let count = ref 0
        fullSolves.Value <- Some count
        try
            let result = action ()
            result, count.Value
        finally
            fullSolves.Value <- None

    /// How many constraints `unifyDetailed` solved, where `countingSolvedConstraints` asks for it.
    let private solvedConstraints = System.Threading.AsyncLocal<int ref option>()

    /// Runs `action`, giving what it gives and how many constraints it solved, counting each time one is solved, on its
    /// own thread and those it starts: the type checker's work, for the tests that check it grows with the program.
    let countingSolvedConstraints (action: unit -> 'T) : 'T * int =
        let count = ref 0
        solvedConstraints.Value <- Some count
        try
            let result = action ()
            result, count.Value
        finally
            solvedConstraints.Value <- None

    /// `Unifier.unifyFrom`, counted for `countingSolvedConstraints`.
    let private unifyFrom subst restId (constraints: ConstraintSet) =
        solvedConstraints.Value |> Option.iter (fun count -> count.Value <- count.Value + constraints.Length)
        unifyFrom subst restId constraints

    /// Solves `constraints`, or gives the first that cannot be satisfied (see `Mismatch`). The type checker adds
    /// constraints only at the front of the list it holds, and looks types up against it again and again as it goes.
    /// So when the list ends in one solved lately that agreed, only the constraints in front of it are solved, on top
    /// of what it solved to. When they don't agree with it, or the order they are solved in shows, the whole list is
    /// solved again, newest first, so that the failure reported, and the order in which a union's tags are added, are
    /// those solving it afresh gives. So is a list that ends in one whose order showed only in the constraints it put
    /// off to the end, when the constraints in front reach what those do (see `Extension`).
    ///
    /// Before it is solved again, a list that ends in a checkpoint (see `checkpoint`) is solved on top of that, when the
    /// constraints in front of it name none of its variables: the two share nothing, so which is solved first does not
    /// show, and they agree when each does. A failure is found by solving the whole list again, as before.
    let unifyDetailed (constraints: ConstraintSet) : Result<Subst, Mismatch> =
        let solved = solvedLists.Value
        match solved.TryGetValue constraints with
        | true, known -> known.Result
        | false, _ ->
            let fromStart () =
                fullSolves.Value |> Option.iter (fun count -> count.Value <- count.Value + 1)
                unifyFrom empty 0 constraints |> Result.map (fun (subst, nextRestId, order) -> subst, nextRestId, extensionOf subst order)
            // A made-up rest variable a constraint names, taken from what an earlier solve gave, names nothing when
            // the whole list is solved (see `isMadeUpRest`), but would name what it was solved to on top of that solve.
            let namesMadeUpRest newer = highestVarId newer >= firstMadeUpRest
            let fromCheckpoint () =
                match checkpointTail constraints with
                | Some (newer, found) when not (namesMadeUpRest newer) && apart found.Vars newer ->
                    match unifyFrom found.Subst found.NextRestId newer with
                    | Ok (subst, nextRestId, order) -> Ok (subst, nextRestId, both found.Extension (extensionOf subst order))
                    | Error _ -> fromStart ()
                | _ -> fromStart ()
            let result =
                match solvedTail solved constraints with
                | Some (newer, subst, restFrom, extension) when not (namesMadeUpRest newer) ->
                    // The constraints it put off reach nothing the newer ones bind, so they reach the same still.
                    match unifyFrom subst restFrom newer with
                    | Ok (subst, nextRestId, Hidden) -> Ok (subst, nextRestId, extension)
                    | _ -> fromCheckpoint ()
                | _ -> fromCheckpoint ()
            if solved.Count >= solvedListsKept then solved.Clear()
            let known =
                match result with
                | Ok (subst, nextRestId, extension) -> { Result = Ok subst; NextRestId = nextRestId; Extension = extension }
                | Error mismatch -> { Result = Error mismatch; NextRestId = 0; Extension = NoneAfter }
            solved[constraints] <- known
            known.Result

    /// Solves `constraints`, or gives the first that cannot be satisfied, with what was wrong and where.
    let unify (constraints: ConstraintSet) : Result<Subst, string * SourceSpan> =
        unifyDetailed constraints |> Result.mapError (fun (message, span, _) -> messageText Map.empty message, span)

    /// Each variable of the expected sides of `constraints`, once: of a call's argument's constraint, those of the
    /// parameter's type, which the argument would have fixed.
    let private expectedVars (constraints: ConstraintSet) : TyVar list =
        let found = ResizeArray<TyVar>()
        let rec visit ty =
            match ty with
            | TyVar v ->
                if not (found.Exists(fun seen -> seen.Id = v.Id)) then found.Add v
                false
            | _ -> Ty.exists visit ty
        for _, expected, _, _ in constraints do
            visit expected |> ignore
        List.ofSeq found

    /// `remaining` solved past each failure (see `solveDetailed`), with the constraints that agree, those set aside
    /// added to `setAside`, and the failures after `failures`, which are newest first.
    let rec private solveSettingAside (remaining: ConstraintSet) (failures: Mismatch list) (setAside: ConstraintSet) : Subst * ConstraintSet * ConstraintSet * Mismatch list =
        match unifyDetailed remaining with
        | Ok subst -> subst, remaining, setAside, List.rev failures
        | Error (_, span, _ as failure) ->
            let kept, failed = remaining |> List.partition (fun (_, _, _, origin) -> origin <> span)
            // A failure from no constraint given (which `unify` does not make) cannot be set aside.
            if failed.IsEmpty then empty, [], setAside, List.rev (failure :: failures)
            else solveSettingAside kept (failure :: failures) (failed @ setAside)

    /// Solves `constraints` as `unify` does, carrying on past a failure: the constraints from where it failed are
    /// set aside and the rest solved again, until they agree. Gives the substitution the rest agree on, and each
    /// failure in the order it was found (see `Mismatch`). With no failure, the substitution is the one `unify` gives.
    let solveDetailed (constraints: ConstraintSet) : Subst * Mismatch list =
        let subst, _, _, failures = solveSettingAside constraints [] []
        subst, failures

    /// `solveDetailed`, for the types a module is given once it is checked: each variable that a constraint set aside
    /// would have fixed and that the rest leave open is the error type, solved after them, so that `list.map(xs, 5)`
    /// is a `list(???)` (#1947). Checking itself sets constraints aside to try them, so this is for its end only.
    let solveLeavingUnknown (constraints: ConstraintSet) : Subst * Mismatch list =
        match solveSettingAside constraints [] [] with
        | subst, _, [], failures -> subst, failures
        | subst, kept, setAside, failures ->
            let unknown = expectedVars setAside |> List.map (fun v -> TyVar v, errorType, Equal, SourceSpan.none)
            match unifyDetailed (kept @ unknown) with
            | Ok withUnknown -> withUnknown, failures
            | Error _ -> subst, failures

    /// A list of constraints solved past its failures (see `solvedSoFar`): what the rest agree on, the constraints of it
    /// that agree, and its failures in the order they were found.
    type private SoFar = { Subst: Subst; Kept: ConstraintSet; Failures: Mismatch list }

    /// The lists of constraints `solvedSoFar` solved lately, by reference.
    let private soFarLists =
        new System.Threading.ThreadLocal<Dictionary<ConstraintSet, SoFar>>(fun () ->
            Dictionary<ConstraintSet, SoFar>(HashIdentity.Reference))

    /// The lists `checkpoint` marked whose constraints did not all agree, each with what `solvedSoFar` made of it: the
    /// checkpoint is on the constraints of it that agree, which the lists after it are solved on top of. They are kept
    /// as long as the lists are, as the checkpoints are, so that a list after one still reaches its checkpoint once
    /// `soFarLists` has been cleared (#2848).
    let private checkpointedSoFar = System.Runtime.CompilerServices.ConditionalWeakTable<ConstraintSet, SoFar>()

    /// `constraints` solved as `solveDetailed` does, for the type checker, which asks again and again as it goes. The
    /// constraints in front of a list solved lately are solved with the constraints of it that agree, after its failures,
    /// rather than the whole list from the start each time: an error in the module would otherwise have every later
    /// question solve all of the module again, twice (#2449). Where that list kept all its constraints, they are
    /// `constraints` itself, which is given as it is, so that the checkpoints in it are still found (see `checkpoint`).
    /// A checkpoint reached before any such list is given as it is too: it agrees, so a constraint set aside in a list
    /// older than it, while a def was being checked, no longer is, and the lists after it are solved on top of it
    /// rather than on that older list's copy of the constraints it kept, which has no checkpoint in it (#2709).
    let private solvedSoFar (constraints: ConstraintSet) : SoFar =
        let given = soFarLists.Value
        match given.TryGetValue constraints with
        | true, known -> known
        | false, _ ->
            let rec walk (newer: ConstraintSet) (remaining: ConstraintSet) =
                match given.TryGetValue remaining with
                // A checkpoint agrees, all of it: what was set aside in it, or in a list older than it, while a def
                // was being checked, no longer is, so the whole list is solved, on top of it.
                | _ when checkpoints.TryGetValue remaining |> fst -> constraints, []
                | true, known when obj.ReferenceEquals(known.Kept, remaining) -> constraints, known.Failures
                | true, known -> List.rev newer @ known.Kept, known.Failures
                | false, _ ->
                    match checkpointedSoFar.TryGetValue remaining, remaining with
                    | (true, known), _ -> List.rev newer @ known.Kept, known.Failures
                    | _, [] -> constraints, []
                    | _, constraint :: rest -> walk (constraint :: newer) rest
            let toSolve, earlier = walk [] constraints
            let subst, kept, _, failures = solveSettingAside toSolve [] []
            let known = { Subst = subst; Kept = kept; Failures = earlier @ failures }
            if given.Count >= solvedListsKept then given.Clear()
            given[constraints] <- known
            known

    /// Marks `constraints`, a list that agrees, as one later solves can start from (see `unifyDetailed`).
    let private mark (constraints: ConstraintSet) =
        match checkpoints.TryGetValue constraints, solvedLists.Value.TryGetValue constraints with
        | (false, _), (true, { Result = Ok subst; NextRestId = nextRestId; Extension = extension }) ->
            let vars =
                match checkpointTail constraints with
                | Some (newer, previous) -> varIds newer previous.Vars
                | None -> varIds constraints Set.empty
            checkpoints.AddOrUpdate(constraints, { Subst = subst; NextRestId = nextRestId; Extension = extension; Vars = vars })
        | _ -> ()

    /// Marks `constraints`, the list the type checker holds once it has generalised a top-level def, as one later
    /// solves can start from (see `unifyDetailed`): the next def's constraints usually share no variable with it, so a
    /// module is solved once, a def at a time, rather than again for each def (#1266). One that doesn't agree, after a
    /// type error, marks the constraints of it that do, which `solvedSoFar` solves the lists after it on top of, so
    /// that the error doesn't have every later def solved from the start (#2848).
    let checkpoint (constraints: ConstraintSet) =
        match constraints with
        | [] -> ()
        | _ ->
            // Solved past its failures first: solving a list with a failure in it as it is would solve it from the start.
            match solvedSoFar constraints with
            | known when obj.ReferenceEquals(known.Kept, constraints) ->
                match unifyDetailed constraints with
                | Ok _ -> mark constraints
                | Error _ -> ()
            | { Kept = _ :: _ as kept } as known when not (checkpointedSoFar.TryGetValue constraints |> fst) ->
                checkpointedSoFar.AddOrUpdate(constraints, known)
                match unifyDetailed kept with
                | Ok _ -> mark kept
                | Error _ -> ()
            | _ -> ()

    /// The constraints of `constraints` that agree, as `solveDetailed` keeps them: those it sets aside are not among
    /// them. The type checker asks whether a constraint fits with those it has so far by solving it with these, so that
    /// an error earlier in the module, reported where it is, does not make every later constraint seem not to fit.
    let agreeing (constraints: ConstraintSet) : ConstraintSet = (solvedSoFar constraints).Kept

    /// Whether solving `constraints` as `solve` does fails anywhere in `span`, without writing out the failures' messages.
    let failsWithin (span: SourceSpan) (constraints: ConstraintSet) : bool =
        (solvedSoFar constraints).Failures |> List.exists (fun (_, at, _) -> SourceSpan.contains span at)

    /// Solves `constraints` as `solveDetailed` does, giving each failure's message and where it is, for the type checker
    /// as it goes (see `solvedSoFar`). A failure found in a list this one extends is given first, before those found in
    /// the constraints in front of it.
    let solve (constraints: ConstraintSet) : Subst * (string * SourceSpan) list =
        let known = solvedSoFar constraints
        known.Subst, known.Failures |> List.map (fun (message, span, _) -> messageText Map.empty message, span)
