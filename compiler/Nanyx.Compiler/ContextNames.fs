/// `context l` before a lambda's parameters: a name for the instance of a context the lambda is given, which only
/// `use l` can do anything with (see docs/src/content/advanced/contexts.md#naming-the-instance-a-lambda-is-given).
module internal NyxCompiler.ContextNames

open Parser.Program
open NyxCompiler.InferState

/// The contexts a lambda's body is given where `env` is its environment, each by the name `state` knows it by, with
/// the scope of a `memory` block's `Memory`: each plain context with members whose members are all bound there as its
/// own, by a signature (the def's, or the type the lambda is checked against), and `Memory` in a `memory` block. One a
/// `use` loads is the code around the lambda's, not the lambda's.
let private given (env: TypeEnv) (state: InferState) : (Identifier * TypeDefInfo * Ty option) list =
    let isGiven (info: TypeDefInfo) =
        info.IsContext
        && info.Parameters.IsEmpty
        && not info.Members.IsEmpty
        && info.Members
           |> Map.forall (fun memberName symbol ->
               match TypeEnv.tryFind memberName env with
               | Some { Symbol = Some bound; ProvidedBy = Some (FromSignature _) } -> bound = symbol
               | _ -> false)
    let plain =
        state.TypeDefs
        |> Map.toList
        |> List.filter (snd >> isGiven)
        // A context imported under an alias is in `TypeDefs` by both names: the one it is declared under is its own.
        |> List.groupBy (fun (_, info) -> info.Symbol)
        |> List.map (fun (_, named) ->
            let name, info = named |> List.tryFind (fun (name, info) -> name = info.Name) |> Option.defaultValue named.Head
            name, info, None)
    let memory =
        match MemoryCells.blockScope env, state.TypeDefs.TryFind MemoryCells.context with
        | Some scope, Some info -> [ MemoryCells.context, info, Some scope ]
        | _ -> []
    plain @ memory

/// `env` and `state` for the body of `lambda`, `body`, with each of `names` (from `context l` or `context l: Log`)
/// bound to the instance of a context the lambda is given. A name binds the members of its context as `env` has them,
/// to load with `use`. A name for a context the lambda isn't given, or one that could name more than one, is an error.
let bind (env: TypeEnv) (state: InferState) (lambda: Expression) (body: Expression) (names: (Identifier * TypeExpr option * SourceSpan) list) : Result<TypeEnv * InferState, Diagnostic list> =
    let contexts = given env state
    let quoted = contexts |> List.map (fun (ctxName, _, _) -> $"'{ctxName}'")
    let named (name: Identifier) (written: TypeExpr option) (span: SourceSpan) =
        match written with
        | Some { Kind = TypeName ctxName } ->
            match contexts |> List.tryFind (fun (_, info, _) -> state.TypeDefs |> Map.tryFind ctxName |> Option.exists (fun named -> named.Symbol = info.Symbol)) with
            | Some found -> Ok found
            | None when contexts.IsEmpty ->
                Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span $"'{name}' names the context '{ctxName}', but this lambda is given no context to name" |> Diagnostics.about name ]
            | None ->
                Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span $"'{name}' names the context '{ctxName}', which this lambda isn't given: it is given {Diagnostics.listed quoted}" |> Diagnostics.about name ]
        | Some written ->
            Error [ Diagnostics.at DiagnosticCodes.UnknownContextName written.Span $"'{name}' can name only a context, written by its name, as in `context {name}: Log`" |> Diagnostics.about name ]
        | None ->
            match contexts with
            | [ found ] -> Ok found
            | [] -> Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span $"'{name}' names no context: this lambda is given none to name" |> Diagnostics.about name ]
            | (first, _, _) :: _ ->
                Error [ Diagnostics.at DiagnosticCodes.UnknownContextName span
                            $"'{name}' could name any of {Diagnostics.listed quoted}, which this lambda is given: say which, as in `context {name}: {first}`" |> Diagnostics.about name ]
    names
    |> List.fold (fun result (name, written, span) ->
        result
        |> Result.bind (fun (env', current) ->
            named name written span
            |> Result.map (fun (ctxName, info, scope) ->
                let members =
                    info.Members
                    |> Map.toList
                    |> List.choose (fun (memberName, _) -> TypeEnv.tryFind memberName env |> Option.map (fun scheme -> memberName, scheme))
                let ty = TyApply(ctxName, [])
                let symbol, next = declareSymbol LambdaParameters lambda.Id name Parameter false (Some lambda.Span.End) current
                let next = next |> recordSymbolType symbol ty
                let next =
                    match symbol with
                    | Some symbol -> { next with ContextNames = next.ContextNames |> Map.add symbol { Context = ctxName; Members = members; Body = body.Id; Scope = scope } }
                    | None -> next
                TypeEnv.declare name symbol (TypeEnv.mono ty) env', next))) (Ok (env, state))

/// The instance `value` loads, where it is a name a lambda binds with `context l` in `env`: the name's symbol, its
/// type, and its context's members to bind again. None for any other value.
let loaded (env: TypeEnv) (state: InferState) (value: Expression) : (TypeScheme * ContextName) option =
    match value.Kind with
    | IdentifierExpr name ->
        TypeEnv.tryFind name env
        |> Option.bind (fun scheme ->
            scheme.Symbol |> Option.bind (fun symbol -> state.ContextNames |> Map.tryFind symbol) |> Option.map (fun instance -> scheme, instance))
    | _ -> None

/// `state` with `value`, the name a `use` loads, recorded as a reference to it (`scheme`'s), of its type.
let recordLoad (value: Expression) (scheme: TypeScheme) (state: InferState) : InferState =
    let state = recordReference value scheme state
    { state with ExprTypes = state.ExprTypes |> Map.add value.Id scheme.Type }

/// `env` with the members of `instance` bound again as they were where its lambda's body starts, and a `memory`
/// block's scope the one a `mut` takes: what `use l` does.
let load (env: TypeEnv) (instance: ContextName) : TypeEnv =
    let env = instance.Scope |> Option.fold (fun acc scope -> MemoryCells.withBlockScope scope acc) env
    instance.Members |> List.fold (fun acc (memberName, scheme) -> TypeEnv.extend memberName scheme acc) env
