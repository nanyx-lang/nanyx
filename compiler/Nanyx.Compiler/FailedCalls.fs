namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A call that fails for a reason outside its callee's type: a context it cannot find, a callee a module block keeps
/// from it, or an argument passed by position for a named parameter. It is its error, recorded in `FailedExprs`, but
/// the callee's type still says what it gives back, so what uses it is checked as usual (#1947).
module internal FailedCalls =
    /// `state` with the call `call` failed with `errors`.
    let failed (call: Expression) (errors: Diagnostic list) (state: InferState) =
        { state with FailedExprs = (call.Id, errors) :: state.FailedExprs }

    /// The call `call`, failed with `errors`, typed as the result `output` of a callee that takes `input`, unknown where
    /// `input` would have said: its arguments are not checked against the callee.
    let result (call: Expression) (input: Ty) (output: Ty) (errors: Diagnostic list) (state: InferState) =
        let unknown = TypeEnv.freeTyVars input |> Seq.map (fun id -> id, Unifier.errorType) |> Map.ofSeq
        mkTypedExpr call (Unifier.apply unknown output) None None None, failed call errors state

    /// Whether the errors naming a callee gives (see `Visibility.referenceErrors`) leave it known: a callee a module
    /// block keeps from here is, while a name that is ambiguous or names a context instance says nothing of what the
    /// call gives back.
    let calleeKnown (errors: Diagnostic list) =
        errors |> List.forall (fun error -> error.Code = DiagnosticCodes.NotExported.Code || error.Code = DiagnosticCodes.OutsideModuleBlock.Code)
