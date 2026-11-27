/// Record spread (see the records page of the docs): `(...a, f = v, ...b)` is a record of every field of `a`, then
/// `f`, then every field of `b`, a later part's field replacing an earlier one's. The parser makes it a call to the
/// built-in "__spread" (see `SpreadSyntax`).
module internal NyxCompiler.RecordSpread

open Parser.Program
open NyxCompiler.InferState

type private Infer = TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>

/// The record `expr` makes of `parts` when the first is a spread of `record`, typed `typedRecord`, an open record whose
/// known fields are `known` (#1866): each later part sets one of those fields, at its type, and the record made is of
/// the open record's own type. Adding a field it isn't known to have would need to know that the rest of the record
/// lacks it, so that, like a second spread, is an error that says to close the record's type.
let private updateOpen (infer: Infer) (env: TypeEnv) (state: InferState) (expr: Expression) (typedRecord: TypedExpr) (known: Map<string, Ty>) (rest: SpreadPart list) =
    let closeIt = "write the record's type with all its fields, closed, as in `(x: int, y: int)`, to make a record of other fields"
    let setPart (current: InferState, errors: Diagnostic list) part =
        match part with
        | SpreadField(name, value) when known.ContainsKey name ->
            match infer env current value with
            | Error err -> current, errors @ err
            | Ok (typedValue, next) -> addConstraintAt value.Span typedValue.Type known.[name] next, errors
        | SpreadField(name, value) ->
            let message =
                $"A spread of a record that may have more fields can only set the fields it is known to have, and '{name}' isn't one of them: {closeIt}"
            current, errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordSpread value.Span message |> Diagnostics.about name ]
        | SpreadRecord record ->
            let message = $"A spread of a record that may have more fields can't be joined with another record: {closeIt}"
            current, errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordSpread record.Span message ]
    match rest |> List.fold setPart (state, []) with
    | current, [] -> Ok (mkTypedExpr expr typedRecord.Type None None None, current)
    | _, errors -> Error errors

/// The record `expr` makes of `parts`: each part is checked in the order written, and a spread one must be a record
/// whose fields are known by then, or else an open record spread first and given only its known fields again (see
/// `updateOpen`).
let infer (infer: Infer) (env: TypeEnv) (state: InferState) (expr: Expression) (parts: SpreadPart list) =
    let openFirst =
        match parts with
        | SpreadRecord record :: rest ->
            match infer env state record with
            | Ok (typedRecord, next) ->
                match Unifier.unalias (resolveSoFar next typedRecord.Type) with
                | TyOpenRecord(known, _) -> Some (updateOpen infer env next expr typedRecord known rest)
                | _ -> None
            | Error _ -> None
        | _ -> None
    match openFirst with
    | Some updated -> updated
    | None ->
    let addPart (fields: Map<string, Ty>, current: InferState, errors: Diagnostic list) part =
        match part with
        | SpreadRecord record ->
            match infer env current record with
            | Error err -> fields, current, errors @ err
            | Ok (typedRecord, next) ->
                // A private type's fields are its representation, which only its home module spreads out.
                match ProtectedShapes.hiddenRepresentation next record.Span (resolveSoFar next typedRecord.Type) with
                | Some nominal -> fields, next, errors @ [ ProtectedShapes.takenApartOutside nominal record.Span ]
                | None ->
                match recordFieldsSoFar next typedRecord.Type with
                | Some spread -> Map.foldBack Map.add spread fields, next, errors
                | None ->
                    let message =
                        match Unifier.unalias (resolveSoFar next typedRecord.Type) with
                        | TyOpenRecord _ ->
                            "A spread of a record that may have more fields goes first, and sets only the fields it is known to have"
                        | _ -> "A spread (`...`) needs a record whose fields are known here"
                    fields, next, errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordSpread record.Span message ]
        | SpreadField(name, value) ->
            match infer env current value with
            | Error err -> fields, current, errors @ err
            | Ok (typedValue, next) -> Map.add name typedValue.Type fields, next, errors
    match parts |> List.fold addPart (Map.empty, state, []) with
    | fields, current, [] -> Ok (mkTypedExpr expr (TyRecord(fields, FieldOrder.none)) None None None, current)
    | _, _, errors -> Error errors
