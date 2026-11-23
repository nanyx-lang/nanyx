/// A def attached to a context, as `def Async.start`, is in scope by its bare name wherever the context is available:
/// in a function that requires it, or one whose requirements are inferred, under a `use` of it, or anywhere for an
/// ambient context a host provides, as the context's members are (#2493). A name bound nearer, a local def, a parameter or an import, wins, since only a name
/// that is otherwise not in scope reaches the attached def. The name is inferred as the attached def's, and written so
/// after inference (see `ModuleLinking.writeAttachedNames`), so that what follows sees `Async.start`.
module internal NyxCompiler.AttachedContextDefs

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState

/// Whether code in `env` reaches the defs attached to the context `ctxName` by their bare names: where it is available
/// (see `Contexts.isAvailable`), and in a def that doesn't write its type, which a call of one infers it to require, as
/// a call of the def by its whole name does.
let private reachable (env: TypeEnv) (state: InferState) (ctxName: Identifier) =
    match state.ContextScope with
    | DefScope(_, true) -> TypeResolution.tryFindTypeDef ctxName state |> Option.exists _.IsContext
    | _ -> Contexts.isAvailable env state ctxName

/// The name of the def attached to a context available in `env` that the bare name `name` reaches, if one does: the
/// context's name, as the module sees it, then `name`. None when none does, or when several contexts available attach
/// a def of that name, where the bare name says nothing about which.
let qualifiedFor (env: TypeEnv) (state: InferState) (name: Identifier) : Identifier option =
    if name.Contains '.' then None
    else
        let suffix = "." + name
        // A def imported twice, as one module's under an alias of its own and under the prelude's, is one def: the
        // shortest of its names is the one it is written as.
        let reached =
            env.Names
            |> Seq.filter (fun entry -> entry.Key.EndsWith suffix)
            |> Seq.filter (fun entry -> reachable env state (entry.Key.Substring(0, entry.Key.Length - suffix.Length)))
            |> Seq.groupBy (fun entry -> entry.Value.Symbol)
            |> List.ofSeq
        match reached with
        | [ _, names ] -> names |> Seq.map _.Key |> Seq.minBy (fun qualified -> qualified.Length, qualified) |> Some
        | _ -> None

/// `state`, where the bare name at `node` stands for `qualified`.
let record (node: NodeId) (qualified: Identifier) (state: InferState) : InferState =
    { state with AttachedByBareName = state.AttachedByBareName |> Map.add node qualified }
