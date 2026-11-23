namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A lambda of several parameters checked against a function type whose input is a record that is not a tuple, such
/// as `(x: int, y: int) -> int`, takes the record's fields by name: `{ x, y -> ... }`, or `{ y, x -> ... }`. A
/// positional field is taken by position. A record pattern renames a field, `{ (x = a, y = b) -> ... }`.
module internal LambdaParameterFields =
    let private isPositional (key: string) = key |> Seq.forall System.Char.IsDigit

    /// An error at each of the parameters `args` of `lambda` that takes no field of `expectedInput`, a record that is
    /// not a tuple: its name is not one of the record's fields, and the record has no positional field in its place.
    /// So `{ a, b -> ... }` for `(x: int, y: int) -> int` is an error at `a` and at `b`.
    let check (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (expectedInput: Ty) : Diagnostic list =
        match args, Unifier.unalias (resolveSoFar state expectedInput) with
        | _ :: _ :: _, TyRecord(fields, order) when fields |> Map.exists (fun key _ -> not (isPositional key)) ->
            let spans = state.Declared |> Map.tryFind (lambda.Id, LambdaParameters) |> Option.defaultValue []
            // In the order the type writes them, where it is known.
            let written = if order.Names.Length = fields.Count then order.Names else fields |> Map.toList |> List.map fst
            let named = written |> List.filter (isPositional >> not)
            let quoted = named |> List.map (fun field -> $"'{field}'")
            // The field in the parameter's place, if it is a named one, as the one it most likely means.
            let renamed index =
                match List.tryItem index written with
                | Some field when not (isPositional field) -> field
                | _ -> List.head named
            args
            |> List.indexed
            |> List.filter (fun (index, (name, _)) ->
                not (fields.ContainsKey name) && not (fields.ContainsKey(TypeResolution.tupleFieldName index)))
            |> List.map (fun (index, (name, _)) ->
                let span = spans |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.defaultValue lambda.Span
                let message =
                    $"The parameter '{name}' is not a field of the function's input, whose fields a lambda takes by name: "
                    + $"write {Diagnostics.listedOr quoted}, or rename a field with a record pattern, as in "
                    + $"'{{ ({renamed index} = {name}, ...) -> ... }}'"
                Diagnostics.at DiagnosticCodes.ParameterNamesNoField span message)
        | _ -> []
