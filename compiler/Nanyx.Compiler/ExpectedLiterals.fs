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

/// The type `expr` is checked against, as far as `state` has settled it, aliases looked through, if what it is written in
/// expects one.
let private expectedOf (state: InferState) (expr: Expression) : Ty option =
    state.ExpectedTypes |> Map.tryFind expr.Id |> Option.map (resolveSoFar state >> Unifier.unalias)

/// The type the items of the list literal `expr` are checked against: the element type of the list expected of it.
let elementOf (state: InferState) (expr: Expression) : Ty option =
    match expectedOf state expr with
    | Some (Lists.ListType element) -> Some element
    | _ -> None

/// The type the payload of the tag `name`, at `expr`, is checked against: that of the tag of the union expected there.
let payloadOf (state: InferState) (expr: Expression) (name: string) : Ty option =
    let payloadIn =
        function
        | TyTag(tag, Some payload) when tag = name -> Some payload
        | _ -> None
    match expectedOf state expr with
    | Some (TyUnion items) -> items |> List.tryPick payloadIn
    | Some ty -> payloadIn ty
    | None -> None

/// `state` with each value of the map literal `expr` checked against the value type of the map expected of it, if
/// `expr` is a map literal and one is.
let intoMapEntries (state: InferState) (expr: Expression) : InferState =
    match expr.Kind, expectedOf state expr with
    | MapSyntax.Call entries, Some (Maps.MapType(_, value)) -> entries |> List.map snd |> List.fold (expect value) state
    | _ -> state
