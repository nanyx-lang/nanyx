/// The types literals are checked against where what they are written in says what it expects of them (#1798): the
/// value of a def declared at a type, an argument of a function whose parameter's type is known, and, inside those, an
/// element of a list literal expected to be a `list(a)`, the payload of a tag expected to be one of a union's, and the
/// value of an entry of a map literal expected to be a `map(k, v)`. A literal checked alone is inferred from its items,
/// so `[#n(1)]` is a `list(#n(int))`, which no other list of tags is, lists being invariant: checked against `list(T)`,
/// its items are `T`s, and `#arr([#arr([#n(1)]), #arr([#b(true)])])` is a `T` as declared.
module internal NyxCompiler.ExpectedLiterals

open Parser.Program
open NyxCompiler.InferState

/// `state` with `expected` the type `expr` is checked against.
let expect (expected: Ty) (state: InferState) (expr: Expression) : InferState =
    { state with ExpectedTypes = state.ExpectedTypes |> Map.add expr.Id expected }

/// `state` with `expected`, a function type, the type the lambda `lambda` is checked against: it takes the input that
/// type says, and its result is checked against the type's result (see `InferState.ExpectedLambdaTypes`).
let expectLambda (expected: Ty) (state: InferState) (lambda: Expression) : InferState =
    { state with ExpectedLambdaTypes = state.ExpectedLambdaTypes |> Map.add lambda.Id expected }

/// The function type the lambda `lambda` is checked against, as it was written down, if one is (see `expectLambda`).
let lambdaTypeOf (state: InferState) (lambda: Expression) : Ty option =
    state.ExpectedLambdaTypes |> Map.tryFind lambda.Id

/// `state` with `declared` the type the statement `last`, the last of the block that is a typed def's value, builds
/// (see `InferState.ExpectedBlockResults`).
let expectBlockResult (declared: Ty) (state: InferState) (last: Expression) : InferState =
    { state with ExpectedBlockResults = state.ExpectedBlockResults |> Map.add last.Id declared }

/// The type the statement `expr` builds, if it is the last of the block that is a typed def's value (see
/// `expectBlockResult`).
let blockResultOf (state: InferState) (expr: Expression) : Ty option =
    state.ExpectedBlockResults |> Map.tryFind expr.Id

/// The type `expr` is checked against, as it was written down: the one what it is written in expects of it (see
/// `expect`), or else the one it builds as the last statement of a typed def's block (see `expectBlockResult`).
let expectedOrBlockResultOf (state: InferState) (expr: Expression) : Ty option =
    state.ExpectedTypes |> Map.tryFind expr.Id |> Option.orElse (blockResultOf state expr)

/// `state` with the square-bracket literal that ends `body`, a lambda's, checked against `expected`, the lambda's
/// result: it builds that type, if it is one with a `fromArray` (see `CollectionLiterals`, #1868).
let rec expectLiteralResult (body: Expression) (expected: Ty) (state: InferState) : InferState =
    match body.Kind with
    | Block statements ->
        match SyntaxTree.blockValue statements with
        | Some last -> expectLiteralResult last expected state
        | None -> state
    | _ when (CollectionSyntax.segments body).IsSome -> expect expected state body
    | _ -> state

/// The type `expr` is checked against, as far as `state` has settled it, aliases looked through, if what it is written in
/// expects one.
let private expectedOf (state: InferState) (expr: Expression) : Ty option =
    state.ExpectedTypes |> Map.tryFind expr.Id |> Option.map (resolveSoFar state >> Unifier.unalias)

/// Whether what `expr` is written in expects a `char` of it (see `CharLiterals`).
let expectsChar (state: InferState) (expr: Expression) : bool =
    match expectedOf state expr with
    | Some Chars.CharType -> true
    | _ -> false

/// The type the items of the list literal `expr` are checked against: the element type of the list expected of it.
let elementOf (state: InferState) (expr: Expression) : Ty option =
    match expectedOf state expr with
    | Some (Lists.ListType element) -> Some element
    | _ -> None

/// The type the payload of the tag `name`, at `expr`, is checked against: that of the tag of the union expected there.
let payloadOf (state: InferState) (expr: Expression) (name: string) : Ty option =
    expectedOf state expr |> Option.bind (Unifier.payloadOfTag name)

/// `state` with each value of the map literal `expr` checked against the value type of the map expected of it, if
/// `expr` is a map literal and one is.
let intoMapEntries (state: InferState) (expr: Expression) : InferState =
    match expr.Kind, expectedOf state expr with
    | MapSyntax.Call entries, Some (Maps.MapType(_, value)) -> entries |> List.map snd |> List.fold (expect value) state
    | _ -> state

/// The record type expected of the record or tuple literal `expr`, if one is, with its fields.
let expectedRecord (state: InferState) (expr: Expression) : (Ty * Map<string, Ty>) option =
    match expectedOf state expr with
    | Some (TyRecord(fields, _) as ty) -> Some (ty, fields)
    | _ -> None

/// `state` with the value of each field of the record or tuple literal `expr`, `fields`, checked against the type of the field of
/// that name in the record expected of it, if one is: a lambda given for a field of type `[C] a -> b` can call `C`'s
/// members, as one given for a parameter of that type can (see `Contexts.extendEnvForLambdaArgument`).
let intoRecordFields (state: InferState) (expr: Expression) (fields: RecordField list) : InferState =
    match expectedOf state expr with
    | Some (TyRecord(expectedFields, _)) ->
        let positional = fields |> List.choose (function PositionalField value -> Some value | NamedField _ -> None)
        let named = fields |> List.choose (function NamedField(name, value) -> Some (name, value) | PositionalField _ -> None)
        (positional |> List.mapi (fun index value -> TypeResolution.tupleFieldName index, value)) @ named
        |> List.fold (fun state (name, value) ->
            match expectedFields |> Map.tryFind name with
            // What the value is written in may expect a type of it already, as a map literal does of the value of each
            // of its entries, which are pairs: that one is the more exact.
            | Some ty when not (state.ExpectedTypes.ContainsKey value.Id) -> expect ty state value
            | _ -> state) state
    | _ -> state

/// `state` with each of `exprs` that is a lambda checked against `expected`, if that is a function type, as a lambda
/// argument is: it takes the input that type says, and uses its contexts, but its result is only checked where the
/// lambda is, so a result of the wrong type is reported once (see `InferState.ExpectedLambdaTypes`).
let intoLambdas (expected: Ty) (state: InferState) (exprs: Expression list) : InferState =
    match resolveSoFar state expected |> Unifier.unalias with
    | TyFunc(input, _, contexts) ->
        exprs
        |> List.fold (fun state expr ->
            match expr.Kind with
            | Lambda _ ->
                let result, state = freshVar state
                expectLambda (TyFunc(input, result, contexts)) state expr
            | _ -> state) state
    | _ -> state

/// The function type the lambda `expr` is checked against, if what it is written in expects one: a record field's, or the
/// result of a lambda around it (see `InferState.ExpectedLambdaTypes`).
let functionOf (state: InferState) (expr: Expression) : Ty option =
    match lambdaTypeOf state expr |> Option.map (resolveSoFar state >> Unifier.unalias) |> Option.orElse (expectedOf state expr) with
    | Some (TyFunc _ as ty) -> Some ty
    | _ -> None
