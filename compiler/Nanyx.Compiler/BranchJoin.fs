/// Joining the types of an `if`'s or a `match`'s branches into the type of the whole.
module NyxCompiler.BranchJoin

open NyxCompiler
open NyxCompiler.InferState

/// The type of an `if` or `match` whose branches have the given types.
///
/// When every branch is a tag (or a union of tags), the tags are collected into one union instead of
/// being forced to agree, so `if c then #some(x) else #none` is `#some(a) | #none` without an
/// annotation. Same-named tags unify their payloads, so `#some(1)` against `#some("a")` is still an
/// error. When every branch is a record (or tuple) with the same fields, the fields are joined one by
/// one, so `if c then (s, #some(x)) else (s, #none)` is `(string, #some(a) | #none)`. Otherwise the
/// branches have to agree as before: every later branch is constrained equal to the first.
///
/// `rowVars` are the rest variables of the tag unions being matched on. A branch of one of those types
/// (a catch-all arm returning what it caught) joins the union as its rest, so
/// `| #some(a), f -> #some(f(a)) | other, _ -> other` is `#some(b) | r`.
///
/// A branch whose type nothing has settled yet, as a call to a function parameter's is, joins the other branches'
/// tags as a fresh rest, and is that union. So `| #some(x), _ -> #some(x) | other, f -> f(other)` is
/// `#some(a) | s`, and `f` gives back a `#some(a) | s`: tags other than `#some` too, which it could not if it
/// were made equal to the first branch.
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
    let tagsOf ty =
        match Unifier.unalias ty with
        | TyTag _ as tag -> Some [ tag ]
        | TyVar v as var when rowVars.Contains v.Id -> Some [ var ]
        | TyUnion items when items |> List.forall isUnionMember -> Some (items |> List.map Unifier.unalias)
        | _ -> None
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
            // Merge in order of first appearance; an existing tag of the same name unifies with the new one.
            // A union has one rest, so a second rest variable is unified with the first.
            let taggedBranches = branchTags |> List.choose id
            let items = List.concat taggedBranches
            let tags = items |> List.filter (function TyTag _ -> true | _ -> false)
            let isJoinRest = function TyVar v -> state.JoinRests.Contains v.Id | _ -> false
            let rests = items |> List.filter (function TyVar _ -> true | _ -> false) |> List.distinct
            let joinRests, ownRests = rests |> List.partition isJoinRest
            let merged, nextState =
                tags
                |> List.fold (fun (acc: Ty list, st) tag ->
                    let name = match tag with TyTag(n, _) -> n | _ -> ""
                    match acc |> List.tryFind (function TyTag(n, _) -> n = name | _ -> false) with
                    | Some existing -> acc, addConstraint existing tag st
                    | None -> acc @ [ tag ], st) ([], state)
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
                | TyRecord fields -> Some fields
                | _ -> None
            let branchFields = branchTypes |> List.map recordFields
            match branchFields with
            | Some first :: _ when
                tail.Length > 0
                && branchFields |> List.forall (function Some fields -> List.ofSeq fields.Keys = List.ofSeq first.Keys | None -> false) ->
                let allFields = branchFields |> List.choose id
                let joined, nextState =
                    first
                    |> Map.fold (fun (acc: Map<string, Ty>, st) key _ ->
                        let fieldTy, next = joinBranchTypes rowVars st (allFields |> List.map (fun fields -> fields.[key]))
                        acc |> Map.add key fieldTy, next) (Map.empty, state)
                TyRecord joined, nextState
            | _ ->
                head, tail |> List.fold (fun st ty -> addConstraint head ty st) state
