namespace NyxCompiler

open Parser.Program

/// The error for a match arm whose pattern names a tag the value matched can't have: one its closed tag union doesn't
/// name. Such an arm can never match, so it is most likely a misspelt tag (`#nothing` for `#none`).
module internal UnknownTagArms =
    let rec private unwrap (ty: Ty) =
        match ty with
        | RecursiveNominals.Nominal(_, _, underlying, _)
        | TyAlias(_, _, underlying) -> unwrap underlying
        | _ -> ty

    /// The tags of `ty` with their payloads, in the order it names them, if it is a closed union of tags. A union
    /// with a rest can have any tag. A single tag, as `def simple = #nil` is, is a union of that one tag (#2160).
    let private closedTags (ty: Ty) : (string * Ty option) list option =
        match unwrap ty with
        | TyTag(name, payload) -> Some [ name, payload ]
        | TyUnion items when not items.IsEmpty && items |> List.forall (function TyTag _ -> true | _ -> false) ->
            items |> List.choose (function TyTag(name, payload) -> Some (name, payload) | _ -> None) |> Some
        | _ -> None

    /// The span of the tag's name at the start of a tag pattern written at `span`, `#` included.
    let private nameSpan (span: SourceSpan) (tagName: string) =
        { span with End = span.Start + tagName.Length + 1 }

    /// The tags of `tags` closest to `tagName` by edit distance, within half its length. A union has few tags, so
    /// a tag can be further from the one meant than a name in scope can (`#nothing` for `#none`).
    let private suggest (tags: string list) (tagName: string) : string list =
        let scored =
            tags
            |> List.map (fun tag -> tag, NameSuggestions.distance tagName tag)
            |> List.filter (fun (_, d) -> d <= max 1 ((tagName.Length + 1) / 2))
        match scored with
        | [] -> []
        | _ ->
            let best = scored |> List.map snd |> List.min
            scored |> List.filter (fun (_, d) -> d = best) |> List.map fst

    /// The error at `span` for an arm for `tagName`, which `ty`, whose tags are `tags`, doesn't have. It suggests a tag
    /// of `tags` but those other arms name already, `handled`: one is not what an arm for another tag means.
    let private unknownTag (ty: Ty) (tags: string list) (handled: Set<string>) (span: SourceSpan) (tagName: string) : Diagnostic =
        let listed =
            match tags |> List.map (sprintf "#%s") with
            | [ only ] -> $"whose only tag is {only}"
            | many -> "whose tags are " + String.concat ", " (List.truncate (many.Length - 1) many) + " and " + List.last many
        let suggestions = suggest (tags |> List.filter (handled.Contains >> not)) tagName |> List.map (sprintf "#%s")
        let question =
            match suggestions with
            | [] -> ""
            | _ -> $""": did you mean {String.concat " or " suggestions}?"""
        let message = $"This arm can never match: the value is a '{Unifier.typeText Map.empty ty}', {listed}, so it has no #{tagName}{question}"
        { Diagnostics.at DiagnosticCodes.TypeMismatch span message with
            Fixes =
                suggestions
                |> List.map (fun suggestion -> { Title = $"Write `{suggestion}`"; Edits = [ { Span = nameSpan span tagName; NewText = suggestion } ] }) }

    /// The errors for tag patterns in `pattern`, matched against a value of type `ty`, that name a tag `ty` doesn't
    /// have, looking into the payloads of the tags it does have. `handled` are the tags the other arms name there.
    let rec private check (handled: Set<string>) (ty: Ty) (pattern: Pattern) : Diagnostic list =
        match pattern.Kind with
        | TagPattern(tagName, payload) ->
            match closedTags ty with
            | Some tags ->
                match tags |> List.tryFind (fst >> (=) tagName) with
                | None -> [ unknownTag ty (List.map fst tags) handled pattern.Span tagName ]
                | Some (_, payloadTy) ->
                    match payload, payloadTy with
                    | Some payload, Some payloadTy -> check Set.empty payloadTy payload
                    | _ -> []
            | None -> []
        | AndPattern parts -> parts |> List.collect (check handled ty)
        | _ -> []

    /// The tags `pattern` names at the top: its own, or its parts'.
    let rec private tagsOf (pattern: Pattern) : string list =
        match pattern.Kind with
        | TagPattern(tagName, _) -> [ tagName ]
        | AndPattern parts -> parts |> List.collect tagsOf
        | _ -> []

    /// The errors for the tag patterns of `arms` that name a tag the scrutinee at their position, of the type in
    /// `scrutineeTypes` resolved before the arms add their constraints, can't have.
    let ofArms (scrutineeTypes: Ty list) (arms: MatchArm list) : Diagnostic list =
        // The tags the arms name at each position, but the arm at `armIndex`.
        let handledBesides armIndex index =
            arms
            |> List.indexed
            |> List.filter (fst >> (<>) armIndex)
            |> List.collect (fun (_, (patterns, _)) -> List.tryItem index patterns |> Option.map tagsOf |> Option.defaultValue [])
            |> Set.ofList
        arms
        |> List.indexed
        |> List.collect (fun (armIndex, (patterns, _)) ->
            List.zip (List.truncate scrutineeTypes.Length patterns) (List.truncate patterns.Length scrutineeTypes)
            |> List.indexed
            |> List.collect (fun (index, (pattern, ty)) -> check (handledBesides armIndex index) ty pattern))
