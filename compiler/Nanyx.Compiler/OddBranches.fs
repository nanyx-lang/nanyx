/// The branch of a `match`, an `if` or a list literal whose type doesn't agree with those before it (#2688). The
/// branches' types are joined (see `BranchJoin.joinBranchTypes`) by constraints reported at the whole, which said
/// neither which branch was the odd one out nor what the others were. Where one of those constraints fails, it is
/// reported instead at the first branch that doesn't join with the branches before it, saying what it gives and what
/// they give. Only where it is reported, and what it says, changes: not which constraints there are or their order.
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

/// `state` with where a mismatch of `branches`, the typed branches of a construct about to be joined with `rowVars` (see
/// `joinBranchTypes`), is reported: the first branch that doesn't join with those before it, as the join's constraints,
/// which `state`'s origin is the span of, would fail. A number literal that nothing has settled yet is taken to be of its
/// default type, as it is once the def is generalised; an error earlier in the module is set aside (see
/// `Solving.agreeing`).
let note (rowVars: Set<int>) (branches: Branches) (typed: TypedExpr list) (state: InferState) : InferState =
    if typed.Length < 2 || SourceSpan.isNone state.Origin then state
    else
        let reported =
            lazy (
                let settled = NumericLiterals.settleWithin (typed |> List.map _.Type) state
                let agreeing = Solving.agreeing settled.Constraints
                // The join of the first `count` branches, solved with what agrees so far.
                let joined count =
                    let joinedTy, next = BranchJoin.joinBranchTypes rowVars settled (typed |> List.truncate count |> List.map _.Type)
                    let added = next.Constraints |> List.truncate (next.Constraints.Length - settled.Constraints.Length)
                    joinedTy, Solving.unify (added @ agreeing)
                [ 2 .. typed.Length ]
                |> List.tryFind (fun count -> joined count |> snd |> Result.isError)
                |> Option.map (fun count ->
                    let others, solved = joined (count - 1)
                    let odd = typed.[count - 1]
                    let resolved ty = match solved with Ok subst -> Unifier.apply subst ty | Error _ -> ty
                    odd.Expr.Span, message branches (resolved odd.Type) (resolved others)))
        { state with BranchSpans = state.BranchSpans |> Map.add state.Origin reported }
