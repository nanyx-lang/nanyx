namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Operators a def's type requires by the member each reads, rather than by a context's name: `` def add: [`+`: a, a ->
/// a] = { + } ``. The language no longer has this form (#1531): it was only another way of writing the prelude's
/// context that has the member, which the inference of operator contexts finds anyway (see "Operators find their
/// context" in the docs' Contexts page). Each one is an error whose fix writes that context, at the type the member's type is
/// written for: `Add(a)` for `` `+`: a, a -> a ``. Several on one type are the contexts `OperatorContexts.covering`
/// gives: `` [(`+`: a, a -> a), (`*`: a, a -> a)] `` is `[Num(a)]`.
///
/// Only an operator could be required this way, and only a member of one of the prelude's contexts.
module internal InlineContexts =
    /// The member an item in a def's contexts requires, with the type written for it, if it is written as a member.
    let private memberOf (ctx: TypeExpr) : (Identifier * TypeExpr) option =
        match ctx.Kind with
        | TypeRecord [ TypeField(name, false, Some memberType, None) ] -> Some (name, memberType)
        | _ -> None

    /// The type the context parameter `parameter` stands for in `specific`, a type of the shape of `general`, which
    /// is written with it: `a` for `(a, a) -> a` against a member declared `(t, t) -> t`.
    let rec private bindingOf (parameter: TyVar) (general: Ty) (specific: Ty) : Ty option =
        let inAll (pairs: (Ty * Ty) list) = pairs |> List.tryPick (fun (g, s) -> bindingOf parameter g s)
        match general, specific with
        | TyVar v, _ when v.Id = parameter.Id -> Some specific
        | TyFunc(ga, gr, _), TyFunc(sa, sr, _) -> inAll [ ga, sa; gr, sr ]
        | TyRecord(gf, _), TyRecord(sf, _) -> inAll [ for KeyValue(name, g) in gf do match sf |> Map.tryFind name with Some s -> yield g, s | None -> () ]
        | TyApply(_, gs), TyApply(_, ss) when gs.Length = ss.Length -> inAll (List.zip gs ss)
        | TyScope g, TyScope s -> bindingOf parameter g s
        | TyTag(_, Some g), TyTag(_, Some s) -> bindingOf parameter g s
        | _ -> None

    /// The one parameter of the prelude's context `context` and the type of its member `memberName`. Its associated
    /// types, as `Num`'s `fraction`, are parameters after it (see `AssociatedTypes`).
    let private declaredMember (state: InferState) (context: Identifier) (memberName: Identifier) : (TyVar * Ty) option =
        match state.TypeDefs |> Map.tryFind context with
        | Some ({ Parameters = parameter :: _ } as info) when info.Parameters.Length - info.AssociatedTypes.Length = 1 ->
            match Unifier.unalias info.Underlying with
            | TyRecord(fields, _) -> fields |> Map.tryFind memberName |> Option.map (fun memberTy -> parameter, memberTy)
            | _ -> None
        | _ -> None

    /// What to write in place of requiring `memberName`, which no operator reads or no context of the prelude has.
    let private advice (state: InferState) (memberName: Identifier) =
        match OperatorContexts.smallest state memberName with
        | Some context -> $": it is a member of {context}, so require [{context}(a)]"
        | None when OrderingOperators.isOperator memberName ->
            $": it is read through {OrderingOperators.context}'s '{WellKnownContexts.ordering.Member}', so require [{OrderingOperators.context}(a)]"
        | None when memberName = EqualityOperators.notEquals ->
            $": it is 'not' of '{EqualityOperators.equals}', so require [{EqualityOperators.context}(a)]"
        | None -> ""

    /// A member a def's type requires, as it is read: the type it is required at, and where it is written.
    type private Required =
        { Operator: Identifier
          WrittenTy: Ty
          ForTy: Ty
          At: SourceSpan }

    /// The member the item `ctx` requires, `memberName` at the type `memberType`, as it is read (`toTy` reads a type).
    /// It must be an operator that is a member of one of the prelude's contexts, at that member's type.
    let private read (toTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (ctx: TypeExpr) (memberName: Identifier) (memberType: TypeExpr) : Result<Required * InferState, Diagnostic list> =
        let written = Names.written memberName
        if Names.isIdentifier memberName then
            Error [ Diagnostics.at DiagnosticCodes.OperatorRequiredByMember ctx.Span $"Only an operator can be required by its member, and '{written}' is not one{advice state memberName}" ]
        else
            match OperatorContexts.smallest state memberName |> Option.bind (fun context -> declaredMember state context memberName |> Option.map (fun found -> context, found)) with
            | None -> Error [ Diagnostics.at DiagnosticCodes.OperatorRequiredByMember ctx.Span $"No context in the prelude has a member '{written}'{advice state memberName}" ]
            | Some (context, (parameter, declaredTy)) ->
                let writtenTy, next = toTy state memberType
                match bindingOf parameter declaredTy writtenTy with
                | Some forTy when Unifier.applyToParameters [ parameter ] [ forTy ] declaredTy = writtenTy ->
                    Ok ({ Operator = memberName; WrittenTy = writtenTy; ForTy = forTy; At = ctx.Span }, next)
                | _ ->
                    let declared = Unifier.tyToDisplayString Ascii declaredTy
                    let parameterText = Unifier.tyToDisplayString Ascii (TyVar parameter)
                    Error [ Diagnostics.at DiagnosticCodes.OperatorRequiredByMember memberType.Span $"'{written}' is '{declared}' in {context}({parameterText}), which '{Unifier.tyToDisplayString Ascii writtenTy}' is not for any '{parameterText}'" ]

    /// The prelude's contexts that the members `required`, all of the type `forTy`, are read through, each written as a
    /// def's type writes it: `Add(a)`. Each member must have the type it is written with in the context it is read through.
    let private contextsFor (state: InferState) (forTy: Ty) (required: Required list) : Result<string list, Diagnostic list> =
        let span = required.Head.At
        match OperatorContexts.covering state (required |> List.map _.Operator |> List.distinct) with
        | Some contexts when (forTy |> function TyVar v -> v.Name.IsSome | _ -> false) ->
            let mismatched =
                required
                |> List.filter (fun r ->
                    OperatorContexts.having state contexts r.Operator
                    |> Option.bind (fun context -> declaredMember state context r.Operator)
                    |> Option.forall (fun (parameter, declaredTy) -> Unifier.applyToParameters [ parameter ] [ forTy ] declaredTy <> r.WrittenTy))
            match mismatched with
            | [] -> Ok (contexts |> List.map (fun context -> $"{context}({Unifier.tyToDisplayString Source forTy})"))
            | _ ->
                mismatched
                |> List.map (fun r ->
                    Diagnostics.at DiagnosticCodes.OperatorRequiredByMember r.At $"'{Names.written r.Operator}' does not have the type '{Unifier.tyToDisplayString Ascii r.WrittenTy}' in the prelude's context it is read through here")
                |> Error
        | _ ->
            Error [ Diagnostics.at DiagnosticCodes.OperatorRequiredByMember span $"An operator cannot be required of the type '{Unifier.tyToDisplayString Ascii forTy}', which a def's type cannot write" ]

    /// The error for each of `contexts`, those a def's type requires, that requires an operator by its member (`toTy`
    /// reads a type). Each is the prelude's context that has the member, at the type the member's type is written for,
    /// and the fix writes those contexts in place of the members: the contexts for a type where the first of its members
    /// is, the other members of that type removed.
    let private errors (toTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (contexts: TypeExpr list) : Diagnostic list =
        let readAll =
            contexts
            |> List.fold (fun result ctx ->
                result
                |> Result.bind (fun (items, current) ->
                    match memberOf ctx with
                    | Some (memberName, memberType) ->
                        read toTy current ctx memberName memberType |> Result.map (fun (required, next) -> items @ [ Choice2Of2 required ], next)
                    | None -> Ok (items @ [ Choice1Of2 ctx ], current))) (Ok ([], state))
        let resolved =
            readAll
            |> Result.bind (fun (items, _) ->
                let required = items |> List.choose (function Choice2Of2 required -> Some required | Choice1Of2 _ -> None)
                items
                |> List.fold (fun result item ->
                    result
                    |> Result.bind (fun (edits: TextEdit list, written: Map<Ty, string>, previous: SourceSpan) ->
                        match item with
                        | Choice1Of2 ctx -> Ok (edits, written, ctx.Span)
                        // Removed with the `,` before it, from the end of the item before it.
                        | Choice2Of2 other when written.ContainsKey other.ForTy ->
                            let removed = { previous with Start = previous.End; End = other.At.End; StartColumn = previous.StartColumn + previous.End - previous.Start }
                            Ok (edits @ [ { Span = removed; NewText = "" } ], written, other.At)
                        | Choice2Of2 first ->
                            contextsFor state first.ForTy (required |> List.filter (fun other -> other.ForTy = first.ForTy))
                            |> Result.map (fun contexts ->
                                let text = String.concat ", " contexts
                                edits @ [ { Span = first.At; NewText = text } ], Map.add first.ForTy text written, first.At))) (Ok ([], Map.empty, SourceSpan.none))
                |> Result.map (fun (edits, written, _) -> required, edits, written))
        match resolved with
        | Error diagnostics -> diagnostics
        | Ok (required, edits, written) ->
            required
            |> List.map (fun r ->
                let context = written[r.ForTy]
                let diagnostic =
                    Diagnostics.at DiagnosticCodes.OperatorRequiredByMember r.At
                        $"An operator is required by its context, not by its member: write '{context}' for '{Names.written r.Operator}'"
                { diagnostic with Fixes = [ { Title = $"Write `{context}`"; Edits = edits } ] })

    /// The errors for the operators `annotation`, a def's type, requires by their members (see `errors`); `Ok` when it
    /// requires none that way.
    let check (toTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (annotation: TypeExpr) : Result<unit, Diagnostic list> =
        let contexts =
            match annotation.Kind with
            | TypeWithContext(contexts, _)
            | TypeContext contexts -> contexts
            | _ -> []
        if contexts |> List.exists (memberOf >> Option.isSome) then Error (errors toTy state contexts) else Ok ()