/// A function that never returns, inferred as one (#1864): a top-level def that doesn't write its type, whose result is
/// a type variable found nowhere else in its type, gives back `undefined`. No value of such a variable can be made, so
/// the function can only call itself, or something else that never returns: `{ x -> loop(x) }` is an `a -> undefined`,
/// while `{ xs -> xs.head }`, whose result is in its input's type, and `{ -> [] }`, whose result is a `list(a)`, stay
/// generic. A parameterised context the def requires can make a value of a variable it names, as `[Decode(a)] string
/// -> a` does, so such a def is left as it is (see the linter's `resultOnlyTypeVariable`), as is one that doesn't
/// type-check, whose type says nothing of what it returns.
module internal NyxCompiler.NeverReturns

open Parser.Program
open NyxCompiler.InferState

/// `scheme`, the type the top-level def `name`, written at `span`, was generalised to once its literals and instances were settled, with
/// its result `undefined` if it never returns, and `state` with that variable solved to it, so that the types recorded
/// for the def's expressions say so too.
let close (state: InferState) (name: Identifier) (span: SourceSpan) (scheme: TypeScheme) : TypeScheme * InferState =
    let requiresParameterised =
        state.ContextRequirements
        |> Map.tryFind name
        |> Option.exists (List.exists (fun context -> match context.Kind with TypeApply(_, _ :: _) -> true | _ -> false))
    let vars =
        if requiresParameterised || Solving.failsWithin span state.Constraints then []
        else Unifier.neverReturnedVars scheme.Type |> List.filter (fun v -> v.Name.IsNone && scheme.Quantified.Contains v.Id)
    match vars with
    | [] -> scheme, state
    | _ ->
        let closing = vars |> List.map (fun v -> v.Id, Unifier.bottomType) |> Map.ofList
        { scheme with
            Type = Unifier.apply closing scheme.Type
            Quantified = vars |> List.fold (fun quantified v -> Set.remove v.Id quantified) scheme.Quantified },
        vars |> List.fold (fun state v -> addConstraint (TyVar v) Unifier.bottomType state) state
