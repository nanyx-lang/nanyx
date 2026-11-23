/// A def attached to a context, as `def Async.start`, is in scope by its bare name wherever the context is available:
/// in a function that requires it, under a `use` of it, or anywhere for an ambient context a host provides, as the
/// context's members are (#2493). In a def that doesn't write its type, where no context available attaches it, it
/// means what its whole name would, and requires its context as a call of that does. A name bound nearer, a local def, a parameter,
/// a top-level def or an import, wins, since only a name that is otherwise not in scope reaches an attached def. The
/// name is inferred as the attached def's, and written so after inference (see `ModuleLinking.writeAttachedNames`), so
/// that what follows sees `Async.start`.
module internal NyxCompiler.AttachedContextDefs

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState

/// The defs attached to contexts that the bare name `name` reaches in `env`, each by the context's name, as the module
/// sees it, then `name`: those of the contexts available there (see `Contexts.isAvailable`), or, if there are none, in a
/// def that doesn't write its type, those of any context, where the name means what the def's whole name would, a call
/// of which requires the context. A def imported twice, as one module's under an alias of its own and under the
/// prelude's, is one def, written by the shortest of its names.
let private reached (env: TypeEnv) (state: InferState) (name: Identifier) : Identifier list =
    if name.Contains '.' then []
    else
        let suffix = "." + name
        let attached =
            env.Names
            |> Seq.filter (fun entry -> entry.Key.EndsWith suffix)
            // Not through an import the compiler made up under an alias no code can write (`__async`, see
            // `TaskScopes`): what only it brings in is not in scope.
            |> Seq.filter (fun entry -> not (entry.Key.StartsWith "__"))
            |> Seq.choose (fun entry ->
                let ctxName = entry.Key.Substring(0, entry.Key.Length - suffix.Length)
                match TypeResolution.tryFindTypeDef ctxName state with
                | Some info when info.IsContext -> Some (entry, Contexts.isAvailable env state ctxName)
                | _ -> None)
            |> List.ofSeq
        let inferring = match state.ContextScope with DefScope(_, true) -> true | _ -> false
        let chosen =
            match attached |> List.filter snd with
            | [] when inferring -> attached
            | available -> available
        chosen
        |> List.groupBy (fun (entry, _) -> entry.Value.Symbol)
        |> List.map (fun (_, names) -> names |> List.map (fun (entry, _) -> entry.Key) |> List.minBy (fun qualified -> qualified.Length, qualified))
        |> List.sort

/// What the bare name `name`, which nothing nearer binds, stands for: the one def attached to a context available in
/// `env` that it reaches, or an error when several contexts available attach a def of that name, as the bare name says
/// nothing about which (`NYX0106`), at `span`. None when it reaches none.
let qualifiedFor (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<Identifier, Diagnostic list> option =
    match reached env state name with
    | [] -> None
    | [ qualified ] -> Some (Ok qualified)
    | several ->
        let shown = several |> List.map (fun qualified -> $"'{qualified}'") |> String.concat " and "
        let message = $"'{name}' could be {shown}, attached to contexts that are both in scope here: write which, as `{several.Head}`"
        Some (Error [ Diagnostics.at DiagnosticCodes.AmbiguousAttachedFunction span message |> Diagnostics.about name ])

/// `state`, where the bare name at `node` stands for `qualified`.
let record (node: NodeId) (qualified: Identifier) (state: InferState) : InferState =
    { state with AttachedByBareName = state.AttachedByBareName |> Map.add node qualified }
