namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The tag union a match's arms describe for a value they take apart by tag. `Infer` types the scrutinee as this union
/// before it types the arms one by one, so that arms naming different tags, or the same tag with different payloads,
/// add to one union rather than each narrowing the scrutinee to the tag it names.
module internal ArmUnion =
    /// The union that `patterns`, the patterns at one position of a match's arms, describe: an item for each tag they
    /// name, whose payload is the union its payload patterns describe in turn, or a fresh variable when they describe
    /// none. Where a catch-all pattern stands at this position or around it (`isOpen`), other tags and payloads can
    /// reach the match, so the union gets a rest for them. `None` when the patterns are not all tags and catch-alls, or
    /// name a single tag with nothing more to say about its payload: the arms then type the scrutinee themselves.
    let rec describe (isOpen: bool) (patterns: Pattern list) (state: InferState) : (Ty * InferState) option =
        let nonCatchAll = patterns |> List.filter (PatternSpace.isCatchAll >> not)
        let isOpen = isOpen || nonCatchAll.Length < patterns.Length
        let tags = nonCatchAll |> List.choose (function Kind (TagPattern(tagName, payloadOpt)) -> Some (tagName, payloadOpt) | _ -> None)
        if nonCatchAll.IsEmpty || tags.Length < nonCatchAll.Length then
            None
        else
            let mutable current = state
            let mutable describedPayload = false
            let items =
                tags
                |> List.groupBy fst
                |> List.sortBy fst
                |> List.map (fun (tagName, entries) ->
                    match entries |> List.choose snd with
                    | [] -> TyTag(tagName, None)
                    | payloads ->
                        match describe isOpen payloads current with
                        | Some (payloadTy, next) ->
                            current <- next
                            describedPayload <- true
                            TyTag(tagName, Some payloadTy)
                        | None ->
                            let payloadTy, next = freshVar current
                            current <- next
                            TyTag(tagName, Some payloadTy))
            if isOpen then
                let restTy, next = freshVar current
                Some (TyUnion (items @ [ restTy ]), next)
            elif items.Length > 1 then
                Some (TyUnion items, current)
            elif describedPayload then
                Some (items.Head, current)
            else
                None
