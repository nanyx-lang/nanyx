/// Values of a type in a scoped context's scope, as `type Cursor = [Tx] protected (...)` declares it (see
/// `TypeDefInfo.ScopedBy`). A constructor call makes one of a scope of its own and requires the context there, as a call
/// to a function that requires it does, so the lambda around ties the scope to the context it is given.
module internal NyxCompiler.ScopedTypes

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeResolution

/// The type of the value the constructor `name` of `info` makes, what that is made from, and `state` once the context
/// of the value's scope, if it has one, is recorded as used by the lambda around (see `LambdaContexts`). Each value a
/// generic type's constructor makes is of the type at arguments of its own.
let made (env: TypeEnv) (state: InferState) (name: Identifier) (info: TypeDefInfo) : Ty * Ty * InferState =
    if isNominal info then
        let ty, underlying, scoped, next = freshNominalType state info
        ty, underlying, LambdaContexts.recordUnsupplied env (Option.toList scoped) next
    else
        // A recursive alias's value is made from what it stands for one level deep, not its closed definition.
        let ty, next = freshAliasType state name info
        ty, RecursiveAliases.underlying ty, next

/// Checks that the context whose scope the values of `info` belong to, if any, is available where its constructor
/// `name` is called, at `call` written at `span`.
let requireContext (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (info: TypeDefInfo) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match info.ScopedBy with
    | Some context -> Contexts.ensureContextAvailable (Some call.Id) true env state name [ TypeExpr.synthetic (TypeName context) ] None span
    | None -> Ok state
