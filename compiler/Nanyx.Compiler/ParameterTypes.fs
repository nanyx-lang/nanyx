/// Where a def writes the type of each of its parameters (#3109): in the function type it writes, `def f: int, string ->
/// int`, or as its lambda's parameters' annotations, `def f = { x: int -> ... }`. A call's argument of the wrong type
/// points back at the parameter's type there (see `InferState.AnnotationSpans`).
module internal NyxCompiler.ParameterTypes

open Parser.Program
open NyxCompiler.InferState

/// Where the type of each parameter of a def that writes `declared`, its type without its contexts, as its value
/// `value` is written: none for a def that writes neither its type's parameters nor its lambda's.
let private writtenIn (declared: TypeExpr option) (value: Expression) : SourceSpan option list option =
    match declared, value.Kind with
    | Some { Kind = TypeFunc(input, _) }, _ ->
        match input.Kind with
        | TypeUnit -> Some []
        | TypeTuple items -> Some (items |> List.map (fun item -> Some item.Span))
        | TypeRecord fields ->
            fields
            |> List.choose (function
                | TypeField(_, _, written, _) -> Some (written |> Option.map _.Span)
                | TypeFieldAttribute _ -> None)
            |> Some
        | _ -> Some [ Some input.Span ]
    | None, Lambda(args, _, _) when args |> List.exists (snd >> Option.isSome) ->
        Some (args |> List.map (snd >> Option.map _.Span))
    | _ -> None

/// `state` with where the def `symbol` writes its parameters' types recorded, if it does (see `writtenIn`).
let record (symbol: SymbolId option) (declared: TypeExpr option) (value: Expression) (state: InferState) : InferState =
    match symbol, writtenIn declared value with
    | Some symbol, Some written -> { state with ParameterTypes = state.ParameterTypes |> Map.add symbol written }
    | _ -> state

/// The types an argument at `index` of `count` given to the def `callee`, named `name`, is checked against, where they
/// are written, with what a mismatch says of each: the type the callee writes for the parameter, and the types a lambda
/// given for it writes for its own parameters.
let private written (state: InferState) (callee: SymbolId option) (name: Identifier) (index: int) (count: int) (arg: Expression) : (SourceSpan * string) list =
    let parameter =
        match callee |> Option.bind (fun symbol -> state.ParameterTypes |> Map.tryFind symbol) with
        | Some written when written.Length = count -> written.[index] |> Option.map (fun at -> at, $"the type '{name}' takes here") |> Option.toList
        | _ -> []
    let own =
        match arg.Kind with
        | Lambda(lambdaArgs, _, _) -> lambdaArgs |> List.choose (snd >> Option.map (fun written -> written.Span, "the type written here"))
        | _ -> []
    parameter @ own

/// `state` noting that the argument `arg`, the one at `index` of `count` given to the def `callee`, named `name`, is
/// checked against `paramTy` (see `written`, `InferState.AnnotationSpans`).
let noteArgument (callee: SymbolId option) (name: Identifier) (index: int) (count: int) (arg: Expression) (paramTy: Ty) (state: InferState) : InferState =
    written state callee name index count arg |> List.fold (fun state (at, label) -> noteAnnotationAs label arg.Span at paramTy state) state

/// `state` noting what each of `items`, a tuple of the arguments given to the def `callee`, named `name`, points back
/// at when a mismatch is reported at it (see `written`, `InferState.ArgumentNotes`).
let noteItems (callee: SymbolId option) (name: Identifier) (items: Expression list) (state: InferState) : InferState =
    items
    |> List.indexed
    |> List.fold (fun state (index, item) ->
        match written state callee name index items.Length item with
        | [] -> state
        | notes -> { state with ArgumentNotes = state.ArgumentNotes |> Map.add item.Span notes }) state
