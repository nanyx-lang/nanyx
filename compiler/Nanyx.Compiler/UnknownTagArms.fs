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
    /// with a rest can have any tag. So can a single tag, as far as this check goes: it is often the type inferred
    /// for one value (`def simple = #nil`), and a match on that is written for the union it stands in for.
    let private closedTags (ty: Ty) : (string * Ty option) list option =
        match unwrap ty with
        | TyUnion items when items.Length > 1 && items |> List.forall (function TyTag _ -> true | _ -> false) ->
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

    let private unknownTag (ty: Ty) (tags: string list) (span: SourceSpan) (tagName: string) : Diagnostic =
        let listed =
            match tags |> List.map (sprintf "#%s") with
            | [ only ] -> only
            | many -> String.concat ", " (List.truncate (many.Length - 1) many) + " and " + List.last many
        let suggestions = suggest tags tagName |> List.map (sprintf "#%s")
        let question =
            match suggestions with
            | [] -> ""
            | _ -> $""": did you mean {String.concat " or " suggestions}?"""
        let message = $"This arm can never match: the value is a '{TypePrinting.tyToString ty}', whose tags are {listed}, so it has no #{tagName}{question}"
        { Diagnostics.at DiagnosticCodes.TypeMismatch span message with
            Fixes =
                suggestions
                |> List.map (fun suggestion -> { Title = $"Write `{suggestion}`"; Edits = [ { Span = nameSpan span tagName; NewText = suggestion } ] }) }

    /// The errors for tag patterns in `pattern`, matched against a value of type `ty`, that name a tag `ty` doesn't
    /// have, looking into the payloads of the tags it does have.
    let rec private check (ty: Ty) (pattern: Pattern) : Diagnostic list =
        match pattern.Kind with
        | TagPattern(tagName, payload) ->
            match closedTags ty with
            | Some tags ->
                match tags |> List.tryFind (fst >> (=) tagName) with
                | None -> [ unknownTag ty (List.map fst tags) pattern.Span tagName ]
                | Some (_, payloadTy) ->
                    match payload, payloadTy with
                    | Some payload, Some payloadTy -> check payloadTy payload
                    | _ -> []
            | None -> []
        | AndPattern parts -> parts |> List.collect (check ty)
        | _ -> []

    /// The errors for the tag patterns of `arms` that name a tag the scrutinee at their position, of the type in
    /// `scrutineeTypes` resolved before the arms add their constraints, can't have.
    let ofArms (scrutineeTypes: Ty list) (arms: MatchArm list) : Diagnostic list =
        arms
        |> List.collect (fun (patterns, _) ->
            List.zip (List.truncate scrutineeTypes.Length patterns) (List.truncate patterns.Length scrutineeTypes)
            |> List.collect (fun (pattern, ty) -> check ty pattern))
