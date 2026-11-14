/// The errors for a pipe `x \name` that finds no one function to call, each saying what the pipe looked for: the
/// definition attached under `name` to `x`'s type, and then a plain `name` in scope that takes `x`.
module NyxCompiler.PipeLookups

open Parser.Program
open NyxCompiler.InferState

/// What a pipe looked for.
type Lookup =
    { /// The type of the value piped, as far as it is known where the pipe is.
      PipedType: Ty
      /// The names definitions can be attached to `PipedType` under, nearest first.
      AttachedUnder: string list
      /// The name the pipe is written with: `trim` in `s \trim`.
      Written: Identifier
      /// The name the pipe called: `Written`, or the definition attached under it that it found, `string.trim`.
      Called: Identifier
      /// Where `Written` is.
      At: SourceSpan }

/// The definitions in `env` attached under `name` to a type, as `Type.name`, in alphabetical order: what a pipe to
/// `name` could call if the piped value's type were known. `isTypeName` says which names are types.
let private attachedAnywhere (env: TypeEnv) (isTypeName: string -> bool) (name: string) : string list =
    let suffix = "." + name
    env
    |> Map.keys
    |> Seq.filter (fun key -> key.EndsWith suffix && isTypeName (key.Substring(0, key.Length - suffix.Length)))
    |> Seq.sort
    |> Seq.toList

/// The error for piping a value whose type is not known yet to a name nothing in scope is called, when types in
/// scope attach it (`attached`): none if none does.
let private unknownType (lookup: Lookup) (attached: string list) : Diagnostic option =
    let quoted = attached |> List.map (fun candidate -> $"'{candidate}'")
    let found =
        match attached with
        | [] -> None
        | [ only ] -> Some ($"cannot find '{only}'", only)
        | first :: _ -> Some ($"cannot choose between {Diagnostics.listed quoted}", first)
    found
    |> Option.map (fun (missed, example) ->
        Diagnostics.at DiagnosticCodes.UnknownFunction lookup.At
            $"The type of the value piped to '{lookup.Written}' is not known here, so the pipe {missed}: annotate the value's type, or name the definition, as in `\\{example}`")

/// The error for piping a value of a named type to a name none of its names attaches and nothing in scope is called.
/// It says where the definition is when the pipe cannot reach it: under the alias of a module imported with `as`, or
/// in a standard-library module that is not imported. Failing those, it names what other types attach under the name.
let private notAttached (env: TypeEnv) (state: InferState) (isTypeName: string -> bool) (lookup: Lookup) : Diagnostic =
    let name = lookup.Written
    let attachedNames = lookup.AttachedUnder |> List.map (fun typeName -> $"{typeName}.{name}")
    let aliased () =
        attachedNames
        |> List.tryPick (fun attachedName ->
            env
            |> Map.keys
            |> Seq.filter (fun key -> key.EndsWith("." + attachedName))
            |> Seq.sort
            |> Seq.tryHead
            |> Option.map (fun qualified -> $". '{attachedName}' is available as '{qualified}'"))
    let inStandardLibrary () =
        attachedNames
        |> List.tryPick (fun attachedName ->
            match StandardLibraryImports.hint state.StandardLibraryExports.Value attachedName with
            | "" -> None
            | hint -> Some hint)
    let elsewhere () =
        match attachedAnywhere env isTypeName name |> List.map (fun candidate -> $"'{candidate}'") with
        | [] -> None
        | others ->
            let verb = if others.Length = 1 then "is" else "are"
            Some $". Only {Diagnostics.listed others} {verb} in scope"
    let hint =
        aliased ()
        |> Option.orElseWith inStandardLibrary
        |> Option.orElseWith elsewhere
        |> Option.defaultValue ""
    Diagnostics.at DiagnosticCodes.UnknownFunction lookup.At
        $"'{Unifier.tyToString lookup.PipedType}' has no attached '{name}', and there is no function '{name}' in scope{hint}"

/// The error for piping a value of a named type that attaches nothing under the pipe's name to the plain function of
/// that name, when the function does not take the value: what it takes instead. None when it does, when the pipe
/// found an attached definition, or when the value's type is not known yet. `hasArguments` says whether the pipe
/// passes more arguments after the value, which makes the value the first of the function's parameters.
let misfit (env: TypeEnv) (state: InferState) (lookup: Lookup) (hasArguments: bool) : Diagnostic option =
    match lookup.PipedType, lookup.AttachedUnder, TypeEnv.tryFind lookup.Written env with
    | TyVar _, _, _
    | _, [], _ -> None
    | _ when lookup.Called <> lookup.Written || Unifier.isError lookup.PipedType -> None
    | _, _, Some scheme ->
        let takes =
            match Unifier.unalias (fst (instantiate state scheme)) with
            | TyFunc(TyRecord parameters, _, _) when hasArguments -> parameters |> Map.tryFind "0"
            | TyFunc(input, _, _) when not hasArguments -> Some input
            | _ -> None
        takes
        |> Option.filter (fun takes -> Unifier.unify [ lookup.PipedType, takes, Assignable, SourceSpan.none ] |> Result.isError)
        |> Option.map (fun takes ->
            let name = lookup.Written
            let piped = Unifier.tyToString lookup.PipedType
            Diagnostics.at DiagnosticCodes.TypeMismatch lookup.At
                $"'{piped}' has no attached '{name}', and the function '{name}' in scope takes '{Unifier.tyToString takes}', not '{piped}'")
    | _ -> None

/// The error for a pipe that found nothing to call: none when it looked for nothing more than a plain name, which the
/// ordinary unknown-function error covers. `isTypeName` says which names are types.
let private unresolved (env: TypeEnv) (state: InferState) (isTypeName: string -> bool) (lookup: Lookup) : Diagnostic option =
    match lookup.PipedType, lookup.AttachedUnder with
    | _ when lookup.Written.Contains "." -> None
    | TyVar _, _ -> unknownType lookup (attachedAnywhere env isTypeName lookup.Written)
    | _, [] -> None
    | _ -> Some (notAttached env state isTypeName lookup)

/// The error for a pipe that reached an attached definition more than one import brings in: it names each module.
let private ambiguous (env: TypeEnv) (state: InferState) (lookup: Lookup) : Diagnostic option =
    let called = lookup.Called
    match state.AmbiguousImports |> Map.tryFind called, TypeEnv.tryFind called env with
    | Some (symbol, candidates), Some scheme when scheme.Symbol = Some symbol ->
        let modules = candidates |> List.map (fun candidate -> $"'{candidate.Substring(0, candidate.Length - called.Length - 1)}'")
        Some (Diagnostics.at DiagnosticCodes.AmbiguousAttachedFunction lookup.At
                $"'{lookup.Written}' is ambiguous for '{Unifier.tyToString lookup.PipedType}': '{called}' is defined in {Diagnostics.listed modules}; import all but one of them with 'as'")
    | _ -> None

/// `diagnostics`, the errors of the call a pipe made, with the error for a name the call found nothing for, or more
/// than one of, replaced by the pipe's own, which says what the pipe looked for.
let explain (env: TypeEnv) (state: InferState) (isTypeName: string -> bool) (lookup: Lookup) (diagnostics: Diagnostic list) : Diagnostic list =
    let replaced, pipeError =
        if lookup.Called = lookup.Written then DiagnosticCodes.UnknownFunction, unresolved env state isTypeName lookup
        else DiagnosticCodes.AmbiguousName, ambiguous env state lookup
    match pipeError with
    | Some pipeError -> diagnostics |> List.map (fun diagnostic -> if diagnostic.Code = replaced.Code then pipeError else diagnostic)
    | None -> diagnostics
