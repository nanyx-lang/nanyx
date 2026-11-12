namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Operators a def's type requires by the member each reads, rather than by a context's name (see "Operator contexts"
/// in the docs' work in progress): `` def add: [`+`: a, a -> a] = { + } ``. Such a requirement is not a context of
/// its own. It is read, before the def is checked, as the prelude's context that has the member, at the type the
/// member's type is written for, so `add` is `[Add(a)] a, a -> a`, exactly as if its type said `[Add(a)]`. Several on
/// one type are the contexts `OperatorContexts.covering` gives: `` [(`+`: a, a -> a) & (`*`: a, a -> a)] `` is `[Num(a)]`.
///
/// Only an operator can be required this way, and only a member of one of the prelude's contexts.
module internal InlineContexts =
    /// The member an item in a def's contexts requires, with the type written for it, if it is written as a member.
    let private memberOf (ctx: TypeExpr) : (Identifier * TypeExpr) option =
        match ctx.Kind with
        | TypeRecord [ TypeField(name, false, false, Some memberType, None) ] -> Some (name, memberType)
        | _ -> None

    /// The type the context parameter `parameter` stands for in `specific`, a type of the shape of `general`, which
    /// is written with it: `a` for `(a, a) -> a` against a member declared `(t, t) -> t`.
    let rec private bindingOf (parameter: TyVar) (general: Ty) (specific: Ty) : Ty option =
        let inAll (pairs: (Ty * Ty) list) = pairs |> List.tryPick (fun (g, s) -> bindingOf parameter g s)
        match general, specific with
        | TyVar v, _ when v.Id = parameter.Id -> Some specific
        | TyFunc(ga, gr, _), TyFunc(sa, sr, _) -> inAll [ ga, sa; gr, sr ]
        | TyRecord gf, TyRecord sf -> inAll [ for KeyValue(name, g) in gf do match sf |> Map.tryFind name with Some s -> yield g, s | None -> () ]
        | TyApply(_, gs), TyApply(_, ss) when gs.Length = ss.Length -> inAll (List.zip gs ss)
        | TyTag(_, Some g), TyTag(_, Some s) -> bindingOf parameter g s
        | _ -> None

    /// The one parameter of the prelude's context `context` and the type of its member `memberName`.
    let private declaredMember (state: InferState) (context: Identifier) (memberName: Identifier) : (TyVar * Ty) option =
        match state.TypeDefs |> Map.tryFind context with
        | Some ({ Parameters = [ parameter ] } as info) ->
            match Unifier.unalias info.Underlying with
            | TyRecord fields -> fields |> Map.tryFind memberName |> Option.map (fun memberTy -> parameter, memberTy)
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
            Error [ Diagnostics.at DiagnosticCodes.UnknownContext ctx.Span $"Only an operator can be required by its member, and '{written}' is not one{advice state memberName}" ]
        else
            match OperatorContexts.smallest state memberName |> Option.bind (fun context -> declaredMember state context memberName |> Option.map (fun found -> context, found)) with
            | None -> Error [ Diagnostics.at DiagnosticCodes.UnknownContext ctx.Span $"No context in the prelude has a member '{written}'{advice state memberName}" ]
            | Some (context, (parameter, declaredTy)) ->
                let writtenTy, next = toTy state memberType
                match bindingOf parameter declaredTy writtenTy with
                | Some forTy when Unifier.applyToParameters [ parameter ] [ forTy ] declaredTy = writtenTy ->
                    Ok ({ Operator = memberName; WrittenTy = writtenTy; ForTy = forTy; At = ctx.Span }, next)
                | _ ->
                    let declared = Unifier.tyToDisplayString Ascii declaredTy
                    let parameterText = Unifier.tyToDisplayString Ascii (TyVar parameter)
                    Error [ Diagnostics.at DiagnosticCodes.TypeMismatch memberType.Span $"'{written}' is '{declared}' in {context}({parameterText}), which '{Unifier.tyToDisplayString Ascii writtenTy}' is not for any '{parameterText}'" ]

    /// The prelude's contexts that the members `required`, all of the type `forTy`, are read through, as a def's type
    /// writes them: `Add(a)`. Each member must have the type it is written with in the context it is read through.
    let private contextsFor (state: InferState) (forTy: Ty) (required: Required list) : Result<TypeExpr list, Diagnostic list> =
        let names =
            ContextInference.varsInOrder forTy |> List.choose (fun v -> v.Name |> Option.map (fun name -> v.Id, name)) |> Map.ofList
        let span = required.Head.At
        match OperatorContexts.covering state (required |> List.map _.Operator |> List.distinct), ContextInference.typeExprOf names forTy with
        | Some contexts, Some argument ->
            let mismatched =
                required
                |> List.filter (fun r ->
                    OperatorContexts.having state contexts r.Operator
                    |> Option.bind (fun context -> declaredMember state context r.Operator)
                    |> Option.forall (fun (parameter, declaredTy) -> Unifier.applyToParameters [ parameter ] [ forTy ] declaredTy <> r.WrittenTy))
            match mismatched with
            | [] -> Ok (contexts |> List.map (fun context -> TypeExpr.at span (TypeApply(context, [ argument ]))))
            | _ ->
                mismatched
                |> List.map (fun r ->
                    Diagnostics.at DiagnosticCodes.TypeMismatch r.At $"'{Names.written r.Operator}' does not have the type '{Unifier.tyToDisplayString Ascii r.WrittenTy}' in the prelude's context it is read through here")
                |> Error
        | _ ->
            Error [ Diagnostics.at DiagnosticCodes.UnknownContext span $"An operator cannot be required of the type '{Unifier.tyToDisplayString Ascii forTy}', which a def's type cannot write" ]

    /// `contexts`, those a def's type requires, with each operator required by its member read as the prelude's
    /// context that has it (`toTy` reads a type). The contexts for a type are where the first of its members is.
    let private resolveContexts (toTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (contexts: TypeExpr list) : Result<TypeExpr list, Diagnostic list> =
        let readAll =
            contexts
            |> List.fold (fun result ctx ->
                result
                |> Result.bind (fun (items, current) ->
                    match memberOf ctx with
                    | Some (memberName, memberType) ->
                        read toTy current ctx memberName memberType |> Result.map (fun (required, next) -> items @ [ Choice2Of2 required ], next)
                    | None -> Ok (items @ [ Choice1Of2 ctx ], current))) (Ok ([], state))
        readAll
        |> Result.bind (fun (items, _) ->
            let required = items |> List.choose (function Choice2Of2 required -> Some required | Choice1Of2 _ -> None)
            items
            |> List.fold (fun result item ->
                result
                |> Result.bind (fun (written: TypeExpr list, placed: Ty list) ->
                    match item with
                    | Choice1Of2 ctx -> Ok (written @ [ ctx ], placed)
                    | Choice2Of2 first when List.contains first.ForTy placed -> Ok (written, placed)
                    | Choice2Of2 first ->
                        contextsFor state first.ForTy (required |> List.filter (fun other -> other.ForTy = first.ForTy))
                        |> Result.map (fun contexts -> written @ contexts, first.ForTy :: placed))) (Ok ([], []))
            |> Result.map (fst >> List.distinct))

    /// `annotation`, a def's type, with each operator it requires by its member read as the prelude's context that has
    /// it (see `resolveContexts`); the same annotation when it requires none that way.
    let resolve (toTy: InferState -> TypeExpr -> Ty * InferState) (state: InferState) (annotation: TypeExpr) : Result<TypeExpr, Diagnostic list> =
        let resolved (contexts: TypeExpr list) (rebuild: TypeExpr list -> TypeExprKind) =
            if contexts |> List.exists (memberOf >> Option.isSome) then
                resolveContexts toTy state contexts |> Result.map (fun contexts -> { annotation with Kind = rebuild contexts })
            else Ok annotation
        match annotation.Kind with
        | TypeWithContext(contexts, inner) -> resolved contexts (fun contexts -> TypeWithContext(contexts, inner))
        | TypeContext contexts -> resolved contexts TypeContext
        | _ -> Ok annotation
