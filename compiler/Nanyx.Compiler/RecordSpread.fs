/// Record spread (see the records page of the docs): `(...a, f = v, ...b)` is a record of every field of `a`, then
/// `f`, then every field of `b`, a later part's field replacing an earlier one's. The parser makes it a call to the
/// built-in "__spread" (see `SpreadSyntax`).
module internal NyxCompiler.RecordSpread

open Parser.Program
open NyxCompiler.InferState

/// The record `expr` makes of `parts`: each part is checked in the order written, and a spread one must be a record
/// whose fields are known by then.
let infer (infer: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>) (env: TypeEnv) (state: InferState) (expr: Expression) (parts: SpreadPart list) =
    let addPart (fields: Map<string, Ty>, current: InferState, errors: Diagnostic list) part =
        match part with
        | SpreadRecord record ->
            match infer env current record with
            | Error err -> fields, current, errors @ err
            | Ok (typedRecord, next) ->
                match recordFieldsSoFar next typedRecord.Type with
                | Some spread -> Map.foldBack Map.add spread fields, next, errors
                | None ->
                    let message = "a spread (`...`) needs a record whose fields are known here"
                    fields, next, errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordSpread record.Span message ]
        | SpreadField(name, value) ->
            match infer env current value with
            | Error err -> fields, current, errors @ err
            | Ok (typedValue, next) -> Map.add name typedValue.Type fields, next, errors
    match parts |> List.fold addPart (Map.empty, state, []) with
    | fields, current, [] -> Ok (mkTypedExpr expr (TyRecord fields) None None None, current)
    | _, _, errors -> Error errors
