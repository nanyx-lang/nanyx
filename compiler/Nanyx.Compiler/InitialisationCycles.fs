/// Top-level values that need each other to be built (#955): `def a = b + 1` and `def b = a + 1`, or a value that
/// needs itself. A function is not built when its definition is, only called later, so functions that use each other
/// are not such a cycle. But a value may call a function while it is built, so a cycle through a function that a value
/// is in is one: `def a = f()` with `def f = { -> a }`. A value that only names a function, without calling it, is
/// counted as calling it.
module NyxCompiler.InitialisationCycles

open Parser.Program

/// Whether `value` is a function: a lambda, whose body runs when it is called rather than when it is defined.
let private isFunction (value: Expression) =
    match value.Kind with
    | Lambda _ -> true
    | _ -> false

/// An error at each top-level value of `module'` that needs its own value to be built, naming the definitions it
/// needs it through.
let check (declared: DeclaredNames) (module': TopLevelItem list) : Diagnostic list =
    let items = Array.ofList module'
    let uses = DefinitionOrder.usesBy SyntaxTree.freeNames module'
    let componentOf = DefinitionOrder.componentsOf uses
    // `uses` has an entry for each value definition and no other item.
    let definition index =
        match items.[index].Kind with
        | Def (ValueDef(_, name, _, value)) -> name, value
        | _ -> invalidOp "InitialisationCycles: a use of an item that is not a value definition"
    uses
    |> Seq.choose (fun (KeyValue(index, used)) ->
        let name, value = definition index
        let others = uses |> Map.filter (fun other _ -> other <> index && componentOf.[other] = componentOf.[index]) |> Map.keys |> List.ofSeq
        let span = declared |> Map.tryFind (value.Id, DefValue) |> Option.bind List.tryHead |> Option.map snd
        match span with
        | Some span when not (isFunction value) && (List.contains index used || not others.IsEmpty) ->
            let through =
                match others |> List.map (fun other -> $"'{fst (definition other)}'") with
                | [] -> ""
                | names -> $", through {Diagnostics.listed names}"
            Some (Diagnostics.at DiagnosticCodes.InitialisationCycle span $"'{name}' needs its own value to be built{through}")
        | _ -> None)
    |> List.ofSeq
