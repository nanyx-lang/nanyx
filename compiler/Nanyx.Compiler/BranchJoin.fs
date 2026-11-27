/// Joining the types of an `if`'s or a `match`'s branches into the type of the whole.
module NyxCompiler.BranchJoin

open NyxCompiler
open Parser.Program
open NyxCompiler.InferState

/// Branches of one type generic in contexts that carry different contexts, as `[c, d] seq(a)` and `[d] seq(a)` do,
/// join as that type carrying a variable that each branch's contexts fit in: a value that carries fewer contexts is
/// given where one carrying more is expected. Made equal, the branches' variables would be made the same contexts, and
/// a signature's `c` would stand for none (#1908). Only a type that names itself is joined so: what the others are made
/// from holds their arguments, which the joined type's are not.
let private joinCarried (state: InferState) (branchTypes: Ty list) : (Ty * InferState) option =
    let carriedOf ty =
        match resolveSoFar state ty with
        | TyNominal(_, args, underlying, isPrivate) & FunctionContexts.CarryingNominal(id, _, carried) -> Some (id, args, underlying, isPrivate, carried)
        | _ -> None
    match branchTypes |> List.map carriedOf with
    | Some (id, args, underlying, isPrivate, _) :: _ as branches when
        branches |> List.forall (function Some (other, otherArgs, _, _, _) -> other = id && otherArgs.Length = args.Length | None -> false)
        && (branches |> List.choose (Option.map (fun (_, _, _, _, carried) -> carried)) |> List.distinct).Length > 1 ->
        let carried, state = freshVar state
        let joinedArgs = List.truncate (args.Length - 1) args @ [ FunctionContexts.carried [ carried ] ]
        RecursiveNominals.instantiate underlying joinedArgs
        |> Option.map (fun joinedUnderlying ->
            let joined = TyNominal(id, joinedArgs, joinedUnderlying, isPrivate)
            joined, branchTypes |> List.fold (fun st ty -> addAssignableConstraint ty joined st) state)
    | _ -> None

/// Branches that are functions requiring different contexts, as `{ 1 }` and `{ sleep(50); 2 }` do, join as a function
/// requiring a variable that each branch's contexts fit in, as `joinCarried` joins types that carry them: a function that
/// requires fewer contexts is given where one that may require more is expected, so `race([{ 1 }, { sleep(50); 2 }])`
/// checks. Made equal, the branches would have to require the same contexts.
let private joinFunctions (state: InferState) (branchTypes: Ty list) : (Ty * InferState) option =
    let contextsOf ty =
        match Unifier.unalias (resolveSoFar state ty) with
        | TyFunc(_, _, contexts) -> Some contexts
        | _ -> None
    match branchTypes |> List.map contextsOf with
    | branches when branches.Length > 1 && branches |> List.forall Option.isSome && (List.distinct branches).Length > 1 ->
        let input, state = freshVar state
        let output, state = freshVar state
        let contexts, state = freshVar state
        let joined = TyFunc(input, output, [ contexts ])
        Some (joined, branchTypes |> List.fold (fun st ty -> addAssignableConstraint ty joined st) state)
    | _ -> None

/// The type of an `if` or `match` whose branches have the given types, or of a list literal's elements.
///
/// When every branch is a tag (or a union of tags), the tags are collected into one union instead of
/// being forced to agree, so `if c then #some(x) else #none` is `#some(a) | #none` without an
/// annotation. Same-named tags unify their payloads, so `#some(1)` against `#some("a")` is still an
/// error, unless both payloads are tags, which are joined: `#error(#notFound)` against `#error(#denied)` is
/// `#error(#notFound | #denied)`, or both are tuples or records, which are joined field by field: `#a(#x, 1)` against
/// `#a(#y, 2)` is `#a(#x | #y, int)`. When every branch is a record (or tuple) with the same fields, the fields are joined one by
/// one, so `if c then (s, #some(x)) else (s, #none)` is `(string, #some(a) | #none)`. Otherwise the
/// branches have to agree: every branch is constrained equal to the first, or, where the first is a tag, to the first
/// branch settled to a named type, which the tags are each one of.
///
/// `rowVars` are the rest variables of the tag unions being matched on. A branch of one of those types
/// (a catch-all arm returning what it caught) joins the union as its rest, so
/// `| #some(a), f -> #some(f(a)) | other, _ -> other` is `#some(b) | r`. A rest the constraints so far have settled
/// joins as the tags it stands for.
///
/// A branch whose type nothing has settled yet, as a call to a function parameter's is, joins the other branches'
/// tags as a fresh rest, and is that union. So `| #some(x), _ -> #some(x) | other, f -> f(other)` is
/// `#some(a) | s`, and `f` gives back a `#some(a) | s`: tags other than `#some` too, which it could not if it
/// were made equal to the first branch.
///
/// Branches of one type generic in contexts that carry different contexts are joined by `joinCarried`.
///
/// A branch of type `undefined`, which never gives a value, is left out of the join.
let rec internal joinBranchTypes (rowVars: Set<int>) (state: InferState) (branchTypes: Ty list) : Ty * InferState =
    let isUnionItem ty =
        match Unifier.unalias ty with
        | TyTag _ -> true
        | TyVar v -> rowVars.Contains v.Id
        | _ -> false
    // A union's members are tags and at most one rest, whatever variable that is: `#nothing | r` is joined with
    // `#some(int)` into `#some(int) | #nothing | r`, not made equal to it, which would drop `#nothing`.
    let isUnionMember ty =
        match Unifier.unalias ty with
        | TyTag _
        | TyVar _ -> true
        | _ -> false
    // A branch whose type is a variable an earlier join has already made a union, as a second call of a function
    // parameter's is once the first was joined, is that union, rest and all.
    let tagsOf ty =
        let ofType ty =
            match Unifier.unalias ty with
            | TyTag _ as tag -> Some [ tag ]
            | TyVar v as var when rowVars.Contains v.Id -> Some [ var ]
            | TyUnion items when items |> List.forall isUnionMember -> Some (items |> List.map Unifier.unalias)
            | _ -> None
        match Unifier.unalias ty with
        | TyVar v when not (rowVars.Contains v.Id) ->
            match resolveSoFar state ty with
            | TyUnion _ as union -> ofType union
            | _ -> None
        // A rest the constraints so far have settled is the tags it stands for, which an arm's tag of the same name
        // is unified with: a catch-all arm of a match on `#some(list(Score)) | #none` that handles `#none` is the
        // rest `#some(list(Score))`, so an arm `#none -> #some([])` makes a `list(Score)` (#2464). Joined as a rest,
        // the union would hold `#some` twice, and the `[]`'s element would be left free and generalised.
        | TyVar _ ->
            match resolveSoFar state ty with
            | TyTag _
            | TyUnion _ as settled -> ofType settled
            | _ -> ofType ty
        | _ -> ofType ty
    // A branch that never gives a value (of type `undefined`) adds nothing to the whole's type, which is what the
    // other branches give: `undefined` only when no branch gives one.
    let giving = branchTypes |> List.filter (fun ty -> not (Unifier.isBottom (resolveSoFar state ty)))
    match branchTypes with
    | [] -> TyPrimitive "unit", state
    | _ when giving.Length < branchTypes.Length ->
        if giving.IsEmpty then Unifier.bottomType, state else joinBranchTypes rowVars state giving
    | head :: tail ->
        let branchTags = branchTypes |> List.map tagsOf
        let untagged = List.zip branchTypes branchTags |> List.filter (snd >> Option.isNone) |> List.map fst
        // An annotation's variable is not unknown: it stands for any type, so it cannot be made a union.
        let isUnknown ty =
            match resolveSoFar state ty with
            | TyVar v -> v.Name.IsNone && not (rowVars.Contains v.Id)
            | _ -> false
        let unknownBranches =
            if untagged.Length < branchTypes.Length && untagged |> List.forall isUnknown then untagged else []
        if untagged.IsEmpty || not unknownBranches.IsEmpty then
            // Merge in order of first appearance; an existing tag of the same name unifies with the new one, unless
            // both payloads are tags, tuples or records, which are joined in turn. A union has one rest, so a second rest variable is
            // unified with the first.
            let taggedBranches = branchTags |> List.choose id
            let items = List.concat taggedBranches
            let tags = items |> List.filter (function TyTag _ -> true | _ -> false)
            let isJoinRest = function TyVar v -> state.JoinRests.Contains v.Id | _ -> false
            let rests = items |> List.filter (function TyVar _ -> true | _ -> false) |> List.distinct
            let joinRests, ownRests = rests |> List.partition isJoinRest
            // A payload that is a tag is joined, and so is a tuple or record one, field by field, so the tags in its
            // fields are joined too: `#a(#x, 1)` against `#a(#y, 2)` is `#a(#x | #y, int)`. A named record's fields
            // have the types its declaration gives them, so it is not joined.
            let isJoinablePayload st payload =
                let resolved = resolveSoFar st payload
                match Unifier.unalias resolved with
                | TyTag _ -> true
                | TyRecord _ -> (match resolved with TyAlias _ -> false | _ -> true)
                | TyUnion members -> members |> List.forall isUnionMember
                | _ -> false
            let merged, nextState =
                tags
                |> List.fold (fun (acc: Ty list, st) tag ->
                    let name = match tag with TyTag(n, _) -> n | _ -> ""
                    let index = acc |> List.tryFindIndex (function TyTag(n, _) -> n = name | _ -> false)
                    match index |> Option.map (fun i -> acc.[i]), tag with
                    // The same payload twice is that payload. Joined, a recursive alias's tags would hold the same two
                    // payloads again.
                    | Some (TyTag(_, Some existingPayload)), TyTag(_, Some payload) when
                        resolveSoFar st existingPayload = resolveSoFar st payload ->
                        acc, st
                    | Some (TyTag(_, Some existingPayload)), TyTag(_, Some payload) when
                        isJoinablePayload st existingPayload && isJoinablePayload st payload ->
                        let joinedPayload, next =
                            joinBranchTypes rowVars st [ resolveSoFar st existingPayload; resolveSoFar st payload ]
                        acc |> List.updateAt index.Value (TyTag(name, Some joinedPayload)), next
                    | Some existing, _ -> acc, addConstraint existing tag st
                    | None, _ -> acc @ [ tag ], st) ([], state)
            let rest, nextState =
                match ownRests with
                | rest :: others -> Some rest, others |> List.fold (fun st other -> addConstraint rest other st) nextState
                | [] when unknownBranches.IsEmpty && joinRests.IsEmpty -> None, nextState
                | [] ->
                    match freshVar nextState with
                    | TyVar v as rest, next -> Some rest, { next with JoinRests = next.JoinRests.Add v.Id }
                    | rest, next -> Some rest, next
            // A rest an earlier join made up is the tags this join adds to its branch, and this join's rest, so the
            // branch is the joined union: in a recursive function, the branch can be the joined union itself.
            let nextState =
                joinRests
                |> List.fold (fun st joinRest ->
                    let branchNames =
                        taggedBranches
                        |> List.find (List.contains joinRest)
                        |> List.choose (function TyTag(name, _) -> Some name | _ -> None)
                    let added = merged |> List.filter (function TyTag(name, _) -> not (List.contains name branchNames) | _ -> false)
                    let extended =
                        match added @ Option.toList rest with
                        | [ single ] -> single
                        | extendedItems -> TyUnion extendedItems
                    addConstraint joinRest extended st) nextState
            let joined =
                match merged @ Option.toList rest with
                | [ single ] -> single
                | joinedItems -> TyUnion joinedItems
            joined, unknownBranches |> List.fold (fun st ty -> addConstraint ty joined st) nextState
        else
            let recordFields ty =
                match Unifier.unalias ty with
                | TyRecord(fields, _) -> Some fields
                | _ -> None
            let branchFields = branchTypes |> List.map recordFields
            // A named record's fields have the types its declaration gives them, so there is nothing to join, and
            // joining them would lose the name: `[Point(x = 1, y = 2), Point(x = 3, y = 4)]` is a `list(Point)`.
            let isNamed = match head with TyAlias _ -> true | _ -> false
            match joinCarried state branchTypes |> Option.orElseWith (fun () -> joinFunctions state branchTypes) with
            | Some joined -> joined
            | None ->
            match branchFields with
            | Some first :: _ when
                tail.Length > 0
                && not isNamed
                && branchFields |> List.forall (function Some fields -> List.ofSeq fields.Keys = List.ofSeq first.Keys | None -> false) ->
                let allFields = branchFields |> List.choose id
                let joined, nextState =
                    first
                    |> Map.fold (fun (acc: Map<string, Ty>, st) key _ ->
                        let fieldTy, next = joinBranchTypes rowVars st (allFields |> List.map (fun fields -> fields.[key]))
                        acc |> Map.add key fieldTy, next) (Map.empty, state)
                TyRecord(joined, (match Unifier.unalias head with TyRecord(_, order) -> order | _ -> FieldOrder.none)), nextState
            | _ ->
                // A branch the constraints so far settle to a type named without arguments, whose declaration fixes
                // what it is, is constrained as that type, not as its variable. Solved afresh, newest first, the
                // variable would otherwise be made the other branch's type before the older constraints that settle
                // it are solved: an arm `v` of a `map.get(m, k)` whose values are `J` would make them `#null` beside
                // an arm `#null` (#2070). Any other type, such as a union of tags, may still be widened.
                let settled ty =
                    match resolveSoFar state ty with
                    | TyAlias(_, [], _)
                    | TyNominal(_, [], _, _) as named -> named
                    | _ -> ty
                // The branches are each made the type of one of them. Where some are tags and another is settled to a
                // named type, that is the named type, which the tags are each one of: made the first tag's type, the
                // other tags would be made that tag, so `| [] -> #e | [node] -> node | _ -> #c(nodes)` would make
                // `#c(...)` an `#e` (#2052).
                let isNamed ty = match settled ty with TyAlias _ | TyNominal _ -> true | _ -> false
                // A branch that holds `undefined` where a value of it gives one out, as `[]` of `list(undefined)`
                // does, is only a lower bound: the whole is what the others say it stands for (#3129). So the anchor is
                // a branch that holds none, and a branch that holds it is given where the anchor's type is expected,
                // where `undefined` binds no variable (see `Unifier.bottomType`).
                let holdsBottom ty = Unifier.holdsBottom (resolveSoFar state ty)
                let anchorIndex =
                    match tagsOf head with
                    | Some _ -> branchTypes |> List.tryFindIndex isNamed |> Option.defaultValue 0
                    | None -> branchTypes |> List.tryFindIndex (holdsBottom >> not) |> Option.defaultValue 0
                let anchor = branchTypes.[anchorIndex]
                let others = branchTypes |> List.removeAt anchorIndex
                let constrain st ty =
                    if holdsBottom ty && not (holdsBottom anchor) then addAssignableConstraint (settled ty) (settled anchor) st
                    else addConstraint (settled anchor) (settled ty) st
                anchor, others |> List.fold constrain state

/// The error for `expr`, an `if` whose `else` is left out (see `IfSyntax`), so that it gives `()` when its condition is
/// false, where its then branch `thenExpr` gives a value of `thenTy`, which is known not to be `()`: the mismatch with
/// `()` would say nothing of the `else` there isn't. None where the then branch is `()`, or still may be.
let internal leftOutElseError (state: InferState) (expr: Expression) (thenExpr: Expression) (elseExpr: Expression) (thenTy: Ty) : Diagnostic option =
    let resolved = Unifier.unalias (resolveSoFar state thenTy)
    match resolved with
    | _ when not (IfSyntax.isLeftOut thenExpr elseExpr) -> None
    | TyVar _
    | TyPrimitive "unit" -> None
    | _ when Unifier.isBottom resolved || Unifier.holdsError resolved -> None
    | _ ->
        let written = Unifier.tyToDisplayString Ascii resolved
        Some (
            Diagnostics.at DiagnosticCodes.TypeMismatch expr.Span
                $"This `if` has no `else`, so it gives '()' when its condition is false: add an `else` of type '{written}'")
