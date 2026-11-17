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

/// `expr`, the spread `...operand` in a literal that builds another type: read as the prelude's `__spreadArray(operand)`
/// for an array, and `__spreadList(operand)` for a list, so the literal's items are one list. Anything else is an error.
let inferSpread (infer: Infer) (env: TypeEnv) (state: InferState) (expr: Expression) (operand: Expression) =
    // The operand is inferred once to see what it is, and that inference set aside: it is inferred again as the
    // argument of the call it is read as.
    match infer env state operand with
    | Error errors -> Error errors
    | Ok (typed, seen) ->
        match Unifier.unalias (resolveSoFar seen typed.Type) with
        | Arrays.ArrayType _ -> inferAsCall (infer env) state expr CollectionSyntax.spreadArray [ operand ]
        | Lists.ListType _
        | TyVar _ -> inferAsCall (infer env) state expr CollectionSyntax.spreadList [ operand ]
        | ty ->
            Error [ Diagnostics.at DiagnosticCodes.InvalidCollectionSpread operand.Span
                        $"A spread in a square-bracket literal takes a list or an array, not a '{Unifier.tyToString ty}'" ]

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
                let typeName = attachedTypeNames ty |> List.tryHead |> Option.defaultValue (Unifier.tyToString ty)
                Some (Error [ noFromArray literal.Span typeName ])
