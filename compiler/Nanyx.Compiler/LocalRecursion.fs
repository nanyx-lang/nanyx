/// A local def is in scope in the statements after it, and in its own value only when it is written `rec` (see the
/// functions page of the docs). The parser marks a `rec` def by wrapping its value in a call to "__rec" (see
/// `RecSyntax`).
module internal NyxCompiler.LocalRecursion

open Parser.Program

/// Whether the local def whose value is `value` is written `rec`, so that its value can name it.
let isRec (value: Expression) =
    match value.Kind with
    | RecSyntax.Call _ -> true
    | _ -> false

/// The value `value`'s def writes: for a `rec` def, the one inside the call that marks it, so that an annotated `rec`
/// def whose value is a lambda runs in the contexts its type declares, and is checked against that type, as a `def` is.
let written (value: Expression) =
    match value.Kind with
    | RecSyntax.Call inner -> inner
    | _ -> value

/// An error at each place the value of the local def `name` names it, when the def is not `rec` and `env`, the scope
/// around the def, binds nothing of that name either.
let selfReferences (env: TypeEnv) (name: Identifier) (value: Expression) : Diagnostic list =
    if isRec value || env.Names.ContainsKey name then []
    else
        SyntaxTree.freeReferences value
        |> List.filter (fst >> (=) name)
        |> List.map (fun (_, at) ->
            let message = $"A local def can't see itself: write `rec {name} = ...` to make it recursive"
            Diagnostics.at DiagnosticCodes.UnknownIdentifier at.Span message |> Diagnostics.about name)
