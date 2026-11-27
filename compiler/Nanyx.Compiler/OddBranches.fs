/// The branch of a `match`, an `if` or a list literal whose type doesn't agree with those before it (#2688). The
/// branches' types are joined (see `BranchJoin.joinBranchTypes`) by constraints reported at the whole, which said
/// neither which branch was the odd one out nor what the others were. Where one of those constraints fails, it is
/// reported instead at the first branch that doesn't join with the branches before it, saying what it gives and what
/// they give. Where the two differ inside, as two functions whose results are different unions do, the mismatch keeps
/// what it says, which names the difference, and only moves to the branch. Only where it is reported, and what it says,
/// changes: not which constraints there are or their order.
module internal NyxCompiler.OddBranches

open Parser.Program
open NyxCompiler.InferState

/// What a construct's branches are, for its message.
type Branches =
    | Arms
    | IfBranches
    | Items

/// What a mismatch of the branch of type `odd` with the branches before it, which give `others`, says.
let private message (branches: Branches) (odd: Ty) (others: Ty) : MismatchText = fun tyToString ->
    match branches with
    | Arms -> $"This arm gives '{tyToString odd}', but the arms before it give '{tyToString others}'"
    | IfBranches -> $"The `else` branch gives '{tyToString odd}', but the `then` branch gives '{tyToString others}'"
    | Items -> $"This item is '{tyToString odd}', but the items before it are '{tyToString others}'"

/// What a related location at a branch before the odd one out, of type `ty`, says (#2717).
let private relatedLabel (branches: Branches) (ty: Ty) : MismatchText = fun tyToString ->
    match branches with
    | Arms -> $"this arm gives '{tyToString ty}'"
    | IfBranches -> $"the `then` branch gives '{tyToString ty}'"
    | Items -> $"this item is '{tyToString ty}'"

/// `state` with where a mismatch of `branches`, the typed branches of a construct about to be joined with `rowVars` (see
/// `joinBranchTypes`), is reported: the first branch that doesn't join with those before it, as the join's constraints,
/// which `state`'s origin is the span of, would fail. A number literal that nothing has settled yet is taken to be of its
/// default type, as it is once the def is generalised. Where the mismatch says what the branches give, each branch
/// before the odd one out is a related location, saying what it gives.
let note (rowVars: Set<int>) (branches: Branches) (typed: TypedExpr list) (state: InferState) : InferState =
    if typed.Length < 2 || SourceSpan.isNone state.Origin then state
    else
        let reported =
            lazy (
                let settled = NumericLiterals.settleWithin (typed |> List.map _.Type) state
                let types = typed |> List.map (fun branch -> resolveSoFar settled branch.Type)
                // The join of the first `count` branches, of their types as far as they are known, solved on its own: a
                // branch that conflicts only with something outside the construct is not the odd one out, and that
                // mismatch keeps what it says.
                let joined count =
                    let joinedTy, next = BranchJoin.joinBranchTypes rowVars settled (List.truncate count types)
                    let added = next.Constraints |> List.truncate (next.Constraints.Length - settled.Constraints.Length)
                    joinedTy, Solving.unify added
                [ 2 .. typed.Length ]
                |> List.tryFind (fun count -> joined count |> snd |> Result.isError)
                |> Option.map (fun count ->
                    let others, solved = joined (count - 1)
                    let resolved ty = match solved with Ok subst -> Unifier.apply subst ty | Error _ -> ty
                    let odd, others = resolved types.[count - 1], resolved others
                    // The join failed on the branches' types themselves, not on something inside them.
                    let plain (left: Ty) (right: Ty) = $"'{Unifier.tyToString left}' vs '{Unifier.tyToString right}'"
                    let said =
                        match joined count |> snd with
                        | Error (failure, _) when failure = plain others odd || failure = plain odd others -> Some (message branches odd others)
                        | _ -> None
                    let related =
                        match said with
                        | Some _ -> [ for index in 0 .. count - 2 -> typed.[index].Expr.Span, relatedLabel branches (resolved types.[index]) ]
                        | None -> []
                    typed.[count - 1].Expr.Span, said, related))
        { state with BranchSpans = state.BranchSpans |> Map.add state.Origin reported }
