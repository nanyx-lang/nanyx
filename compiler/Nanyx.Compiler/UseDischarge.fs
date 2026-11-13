namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A `use` may give a context's member a lambda that needs more contexts than the member's type declares, as
/// `use Log(log = { m -> println(m) })` does for `log: string -> ()`. The member keeps its declared type, so `m` takes
/// `string` from it, and the contexts the lambda needs besides are required where the `use` is written, as a Koka or
/// Flix handler's clauses add their effects to the place where the handler is installed. Like any lambda, it is checked
/// where it is made against the contexts there (see `LambdaContexts`), so it runs with the instances in scope at the
/// `use`, and the backend's `FunctionLowering.Coerce` captures them where it is given.
///
/// Only an instance written at the `use` itself: one built as a value, `def inst = Log(log = ...)`, has nowhere to say
/// what it needs.
module internal UseDischarge =
    /// `state` with `value`, the instance a `use` loads, marked as written at the `use` when it is a call.
    let mark (value: Expression) (state: InferState) : InferState =
        match value.Kind with
        | FunctionCall _ -> { state with UseInstances = state.UseInstances.Add value.Id }
        | _ -> state

    /// The input of `call`, which makes a context whose members are `underlying` from `args`, as one that fits it:
    /// `inputTy`, the type of the arguments, with each member that a `use` gives a lambda taking the contexts the
    /// member's type declares. And `state` with the contexts those lambdas need besides used where the `use` is written,
    /// as a call there would use them. A member whose type has a context variable keeps the lambda's type, which binds
    /// the variable. Any other call keeps `inputTy`, so a lambda that needs more is NYX0227 there.
    let fit (state: InferState) (call: Expression) (underlying: Ty) (args: Expression list) (inputTy: Ty) : Ty * InferState =
        match args, recordFieldsOf (resolveSoFar state inputTy), recordFieldsOf underlying with
        | [ Kind (RecordExpr fields) ], Some given, Some members when state.UseInstances.Contains call.Id ->
            let lambdas = fields |> List.choose (function NamedField(name, Kind (Lambda _)) -> Some name | _ -> None)
            let fitted, extras =
                lambdas
                |> List.fold (fun (fitted: Map<string, Ty>, extras) name ->
                    match fitted |> Map.tryFind name |> Option.map Unifier.unalias, members |> Map.tryFind name |> Option.map Unifier.unalias with
                    | Some (TyFunc(input, output, needs)), Some (TyFunc(_, _, declared)) when declared |> List.forall (function TyVar _ -> false | _ -> true) ->
                        let declared = declared |> List.map (resolveSoFar state)
                        let more = needs |> List.map (resolveSoFar state) |> List.filter (fun ctx -> not (List.contains ctx declared))
                        fitted |> Map.add name (TyFunc(input, output, declared)), extras @ more
                    | _ -> fitted, extras) (given, [])
            TyRecord fitted, LambdaContexts.record (List.distinct extras) state
        | _ -> inputTy, state
