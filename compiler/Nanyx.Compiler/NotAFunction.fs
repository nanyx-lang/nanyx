namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A call of a value whose type is known and is no function: `x(3)` where `x` is an `int` (#2076). Unchecked, it would
/// be a mismatch of its type and a function type the checker made up for the call.
module internal NotAFunction =
    /// How a message names a value of the type `ty`: `an int`, or `of type '(x: int)'`.
    let private described (typeDefs: Map<string, TypeDefInfo>) (ty: Ty) =
        match ty with
        | TyPrimitive name when name <> "unit" ->
            let article = if "aeiou".Contains name[0] then "an" else "a"
            $"{article} {name}"
        | _ -> $"of type '{Unifier.tyToStringIn typeDefs ty}'"

    /// The error for a call of `name`, of the type `calleeTy` and written at `calleeSpan`, with `args`, when the type is
    /// known to be no function: a number, a string, a `bool`, `()`, a measured value, a record or tuple, a tag or union
    /// of tags, or a list. A map and an array are called to look an item up, and a record given only a block, as in
    /// `proc { ... }`, is a workflow's builder, so neither is one. Nor is a value that never comes, as `crash(...)`'s,
    /// which may be called, or a keyword's call that a local hides (`call` is one of those), which says so itself (see
    /// `HiddenKeywords`).
    let check (state: InferState) (call: Expression) (name: Identifier) (calleeTy: Ty) (calleeSpan: SourceSpan) (args: Expression list) : Diagnostic option =
        let ty = Unifier.unalias (resolveSoFar state calleeTy)
        let notCallable =
            match ty, args with
            | _ when Unifier.isError ty || Unifier.isBottom ty || state.DelayCalls.ContainsKey call.Span -> false
            | TyRecord _, [ Kind (Lambda([], _, _)) ] -> false
            | TyPrimitive _, _
            | TyMeasure _, _
            | TyRecord _, _
            | TyTag _, _
            | TyUnion _, _ -> true
            | Lists.ListType _, _ -> true
            | _ -> false
        if notCallable then
            Some (Diagnostics.at DiagnosticCodes.NotAFunction calleeSpan $"'{name}' is {described state.TypeDefs ty}, not a function, so it can't be called" |> Diagnostics.about name)
        else None
