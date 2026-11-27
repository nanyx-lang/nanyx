/// Square-bracket literals of any type that says how to be built from one (see `CollectionSyntax` and the collections
/// page of the docs): `Array[1, 2]`, which the parser writes as the call to `Array.fromArray` that builds it, and
/// `[1, 2]` where an `Array(int)` is expected, which is read as that call. A literal with no expected type, or one of a
/// type variable, stays a list, or a map if its items are entries.
module internal NyxCompiler.CollectionLiterals

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeResolution

type private Infer = TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>

/// The error for a literal of the type `typeName`, written at `span`, which has no `fromArray` to build one with.
let noFromArray (span: SourceSpan) (typeName: string) =
    Diagnostics.at DiagnosticCodes.NoFromArray span
        $"'{typeName}' has no `fromArray`, so a square-bracket literal can't build one: attach one to it, as in `def {typeName}.fromArray: Array(a) -> {typeName}`"
    |> Diagnostics.about typeName

/// The prelude's function a spread of a value of type `ty` is read as, to make a list of its elements: `__spreadList`
/// for a list, or a type nothing has settled, `__spreadArray` for an array, `__spreadSeq` for a sequence, which may
/// need contexts, and `__spreadIterable` for any other type a definition can be attached to, which must be `Iterable`.
/// None for a type that can't be: a record, a tag, a function.
let private spreadFunction (ty: Ty) : Identifier option =
    match ty with
    | Arrays.ArrayType _ -> Some CollectionSyntax.spreadArray
    | Lists.ListType _
    | TyVar _ -> Some CollectionSyntax.spreadList
    | Seqs.SeqType _ -> Some CollectionSyntax.spreadSeq
    | _ when not (attachedTypeNames ty).IsEmpty -> Some CollectionSyntax.spreadIterable
    | _ -> None

/// `expr`, the spread `...operand` in a literal: read as the call that makes a list of the operand's elements (see
/// `spreadFunction`), so the literal's items are one list. A value that can't be iterated is an error.
let inferSpread (infer: Infer) (env: TypeEnv) (state: InferState) (expr: Expression) (operand: Expression) =
    // The operand is inferred once to see what it is, and that inference set aside: it is inferred again as the
    // argument of the call it is read as.
    match infer env state operand with
    | Error errors -> Error errors
    | Ok (typed, seen) ->
        let ty = Unifier.unalias (resolveSoFar seen typed.Type)
        match spreadFunction ty with
        | Some callee -> inferAsCall (infer env) state expr callee [ operand ]
        | None ->
            Error [ Diagnostics.at DiagnosticCodes.InvalidCollectionSpread operand.Span
                        $"A spread in a square-bracket literal takes something that can be iterated, such as a list, an array, a set or a sequence, not a '{Unifier.typeText state.TypeDefs ty}'" ]

/// `literal`, a list literal with a spread in it, `[a, ...xs]`, which the parser writes `[a] ++ xs`, when one of its
/// spreads is not a list: inferred as the list of its items with each spread read as `inferSpread` reads it, which is
/// recorded in `ReadAs` to be written as it. None when every spread is a list, or a type nothing has settled, so the
/// literal is the lists joined as written.
let spreading (infer: Infer) (env: TypeEnv) (state: InferState) (literal: Expression) : Result<TypedExpr * InferState, Diagnostic list> option =
    match CollectionSyntax.segments literal with
    | Some segments ->
        let isList (operand: Expression) =
            match infer env state operand with
            | Ok (typed, seen) -> spreadFunction (Unifier.unalias (resolveSoFar seen typed.Type)) = Some CollectionSyntax.spreadList
            | Error _ -> true
        let spreads = segments |> List.choose (function Choice2Of2 operand -> Some operand | Choice1Of2 _ -> None)
        if spreads |> List.forall isList then None
        else
            let list = { literal with Kind = (CollectionSyntax.listOf literal.Span segments).Kind }
            Some (infer env state list |> Result.map (fun (typed, next) -> typed, { next with ReadAs = next.ReadAs |> Map.add literal.Id list }))
    | None -> None

/// Whether a literal of `ty`, expected where `literal` is written, is built through the type's `fromArray`: a list's
/// items stay a list, and a map's entries a map, as does anything without a name a definition can be attached to.
let private builds (literal: Expression) (ty: Ty) =
    match literal.Kind, ty with
    | MapSyntax.Call _, Maps.MapType _ -> false
    | MapSyntax.Call _, Lists.ListType _ -> true
    | _, Lists.ListType _ -> false
    | _, (TyNominal _ | Maps.MapType _ | Sets.SetType _ | Arrays.ArrayType _) -> true
    | _ -> false

/// `literal`, a square-bracket literal written where a value of `expected` is, inferred as the call of the type's
/// `fromArray` that builds it, which is recorded in `ReadAs` to be written as it. None when the literal stays a list
/// or a map (see `builds`).
let targeted (infer: Infer) (env: TypeEnv) (state: InferState) (literal: Expression) (expected: Ty) : Result<TypedExpr * InferState, Diagnostic list> option =
    match CollectionSyntax.segments literal with
    | None -> None
    | Some segments ->
        let ty = Unifier.unalias (resolveSoFar state expected)
        if not (builds literal ty) then
            None
        else
            match tryResolveAttachedFunction env ty CollectionSyntax.fromArray with
            | Some fromArray ->
                let array = Expression.from literal (FunctionCall(CollectionSyntax.arrayOf, None, [ CollectionSyntax.listOf literal.Span segments ]))
                Some (inferAsCall (infer env) state literal fromArray [ array ])
            | None ->
                let typeName = attachedTypeNames ty |> List.tryHead |> Option.defaultValue (Unifier.typeText state.TypeDefs ty)
                Some (Error [ noFromArray literal.Span typeName ])
