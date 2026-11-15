namespace NyxCompiler

open Parser.Program

/// A value that crosses into or out of a foreign function: one of WebAssembly's value types.
[<RequireQualifiedAccess>]
type ExternValue =
    | I32
    | I64
    | F32
    | F64

/// A foreign function (see the Foreign functions page of the docs): a def marked `@extern("module", "name")`, with a
/// written type and no value, which is the import `name` of `module` that the embedder provides.
type Extern =
    { Module: string
      Name: string
      Parameters: ExternValue list
      /// None for a function that returns nothing, written `()`.
      Result: ExternValue option }

module Externs =
    /// The attribute that declares a foreign function: `@extern("wasi_snapshot_preview1", "args_sizes_get")`.
    let attribute = "extern"

    /// The value type a type named `name` is, as it is written (`I32`) or after the built-in names are replaced (`int`
    /// for `I64`, `float` for `F64`).
    let private valueOfName (name: string) =
        match name with
        | "I32" -> Some ExternValue.I32
        | "I64"
        | "int" -> Some ExternValue.I64
        | "F32" -> Some ExternValue.F32
        | "F64"
        | "float" -> Some ExternValue.F64
        | _ -> None

    /// The value type as it is written.
    let valueName (value: ExternValue) =
        match value with
        | ExternValue.I32 -> "I32"
        | ExternValue.I64 -> "I64"
        | ExternValue.F32 -> "F32"
        | ExternValue.F64 -> "F64"

    let private isRuntimeName (name: string) = name = Hosts.runtimeContext || name.EndsWith("." + Hosts.runtimeContext)

    let private invalid (span: SourceSpan) (message: string) = Diagnostics.at DiagnosticCodes.InvalidExtern span message

    /// The value types a foreign function's type writes for its parameters and its result, or the errors in it: a type
    /// that is not a function, a part that is not one of the value types, a context other than `HostRuntime`, and no
    /// `HostRuntime` at all.
    let private signatureOf (typeExpr: TypeExpr) : Result<ExternValue list * ExternValue option, Diagnostic list> =
        let valueAt (part: TypeExpr) =
            match part.Kind with
            | TypeName name ->
                match valueOfName name with
                | Some value -> Ok value
                | None -> Error [ invalid part.Span $"A foreign function takes and returns only WebAssembly's value types, I32, I64, F32 and F64, not '{name}'" ]
            | _ -> Error [ invalid part.Span "A foreign function takes and returns only WebAssembly's value types, I32, I64, F32 and F64" ]
        let contexts, func =
            match typeExpr.Kind with
            | TypeWithContext(contexts, func) -> contexts, func
            | _ -> [], typeExpr
        let contextErrors =
            let names = contexts |> List.map (fun context -> context, match context.Kind with TypeName name -> Some name | _ -> None)
            let others =
                names
                |> List.filter (fun (_, name) -> not (name |> Option.exists isRuntimeName))
                |> List.map (fun (context, _) -> invalid context.Span $"A foreign function requires only the context '{Hosts.runtimeContext}'")
            let missing =
                if names |> List.exists (fun (_, name) -> name |> Option.exists isRuntimeName) then []
                else
                    [ Diagnostics.at
                          DiagnosticCodes.ExternWithoutHostRuntime
                          typeExpr.Span
                          $"A foreign function's type must require '{Hosts.runtimeContext}', so that only a host can call it: write [{Hosts.runtimeContext}] before it" ]
            others @ missing
        let collect (results: Result<'a, Diagnostic list> list) =
            match results |> List.collect (function Error errors -> errors | Ok _ -> []) with
            | [] -> Ok (results |> List.choose (function Ok value -> Some value | Error _ -> None))
            | errors -> Error errors
        match func.Kind with
        | TypeFunc(input, output) ->
            let parameters =
                match input.Kind with
                | TypeUnit -> Ok []
                | TypeTuple items -> items |> List.map valueAt |> collect
                | _ -> valueAt input |> Result.map List.singleton
            let result =
                match output.Kind with
                | TypeUnit -> Ok None
                | _ -> valueAt output |> Result.map Some
            match parameters, result, contextErrors with
            | Ok parameters, Ok result, [] -> Ok (parameters, result)
            | _ -> Error ((match parameters with Error errors -> errors | Ok _ -> []) @ (match result with Error errors -> errors | Ok _ -> []) @ contextErrors)
        | _ -> Error (invalid func.Span "A foreign function's type is a function's, as `[HostRuntime] (I32, I32) -> I32`" :: contextErrors)

    /// The foreign function `@extern` with `argument`, at `attributeSpan`, declares of the def `name` written at
    /// `defSpan` with `typeOpt` and `value`, or the errors in it: an argument that is not the import's module and name, a
    /// def with a value or with no written type, and a type outside the rules (see `signatureOf`).
    let declared (attributeSpan: SourceSpan) (argument: Expression option) (name: string) (defSpan: SourceSpan) (typeOpt: TypeExpr option) (value: Expression) : Result<Extern, Diagnostic list> =
        let import =
            match argument with
            | Some (Kind (TupleExpr [ Kind (LiteralExpr(StringLit importModule)); Kind (LiteralExpr(StringLit importName)) ])) -> Ok (importModule, importName)
            | _ -> Error [ invalid attributeSpan $"Attribute '@{attribute}' takes the import's module and name, as `@{attribute}(\"wasi_snapshot_preview1\", \"fd_write\")`" ]
        let bodiless =
            match value.Kind with
            | ExternSyntax.Bodiless -> Ok ()
            | _ -> Error [ invalid value.Span $"The foreign function '{name}' has a value: the embedder provides it, so write only its type" ]
        let signature =
            match typeOpt with
            | Some typeExpr -> signatureOf typeExpr
            | None -> Error [ invalid defSpan $"The foreign function '{name}' needs its type written, as `def {name}: [HostRuntime] (I32, I32) -> I32`" ]
        match import, bodiless, signature with
        | Ok (importModule, importName), Ok (), Ok (parameters, result) ->
            Ok { Module = importModule; Name = importName; Parameters = parameters; Result = result }
        | _ ->
            let errorsOf result = match result with Error errors -> errors | Ok _ -> []
            Error (errorsOf import @ errorsOf bodiless @ errorsOf signature)

    /// The value types of a foreign function whose type is `ty` (as the type checker has it): its parameters and its
    /// result, if every one is a value type.
    let signatureOfType (ty: Ty) : (ExternValue list * ExternValue option) option =
        let valueOf (ty: Ty) =
            match Unifier.unalias ty with
            | TyPrimitive name -> valueOfName name
            | _ -> None
        match Unifier.unalias ty with
        | TyFunc(input, output, _) ->
            let parameters =
                match Unifier.unalias input with
                | TyPrimitive "unit" -> Some []
                | TyRecord fields when fields |> Map.forall (fun key _ -> fst (System.Int32.TryParse key)) ->
                    let values = fields |> Map.toList |> List.sortBy (fst >> int) |> List.map (snd >> valueOf)
                    if values |> List.forall Option.isSome then Some (List.choose id values) else None
                | single -> valueOf single |> Option.map List.singleton
            let result =
                match Unifier.unalias output with
                | TyPrimitive "unit" -> Some None
                | single -> valueOf single |> Option.map Some
            match parameters, result with
            | Some parameters, Some result -> Some (parameters, result)
            | _ -> None
        | _ -> None

    /// A foreign function's type as a message shows it: `(I32, I32) -> I32`.
    let private shown (declared: Extern) =
        let parameters = declared.Parameters |> List.map valueName |> String.concat ", "
        let result = declared.Result |> Option.map valueName |> Option.defaultValue "()"
        $"({parameters}) -> {result}"

    /// The errors for the foreign functions in `externs`, each with its def's name and where it is written, that name an
    /// import an earlier one names with another type: WebAssembly imports each function once.
    let conflicts (externs: (string * Extern * SourceSpan) list) : Diagnostic list =
        externs
        |> List.fold (fun (seen: Map<string * string, string * Extern>, errors) (name, declared, span) ->
            match seen |> Map.tryFind (declared.Module, declared.Name) with
            | Some (_, earlier) when earlier.Parameters = declared.Parameters && earlier.Result = declared.Result -> seen, errors
            | Some (earlierName, earlier) ->
                let error =
                    Diagnostics.at
                        DiagnosticCodes.ConflictingExterns
                        span
                        $"'{name}' imports \"{declared.Name}\" from \"{declared.Module}\" as {shown declared}, but '{earlierName}' imports it as {shown earlier}"
                seen, error :: errors
            | None -> seen |> Map.add (declared.Module, declared.Name) (name, declared), errors) (Map.empty, [])
        |> snd
        |> List.rev
