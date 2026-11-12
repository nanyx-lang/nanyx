/// A lambda that uses a context requires it where it is called, but function types do not say so yet (#768). Until
/// they do, such a lambda must not leave the scope that supplies the context: a def whose type requires it, or a `use`
/// that loads it. A lambda uses a context when it names one of its members, a def that requires it, or a local
/// function that does either (see `EscapingLambdas.tryFind`). The runtime's ambient contexts, which the top level
/// has, are left out.
module internal NyxCompiler.ContextEscapes

open Parser.Program
open NyxCompiler.InferState

/// The names of the contexts `contexts` writes.
let rec private namesOf (contexts: TypeExpr list) : Identifier list =
    contexts
    |> List.collect (fun ctx ->
        match ctx.Kind with
        | TypeName name
        | TypeApply(name, _) -> [ name ]
        | TypeContext items -> namesOf items
        | _ -> [])

/// What escapes a scope that supplies the contexts named `supplied`, and loads the members `memberNamed` finds by
/// name, if anything does: the lambda, or the name of a local function, that `result`, what the scope results in, is
/// or holds, with the contexts it uses and the members it names. `blocks` are the scope's blocks.
let private tryFindEscaping (state: InferState) (supplied: Identifier list) (memberNamed: Expression -> Identifier option) (blocks: Statement list list) (result: Expression) =
    let ambient = state.AmbientMembers |> Map.values |> Seq.map snd |> Set.ofSeq |> Set.union state.CarriedAmbientContexts
    let supplied = supplied |> List.filter (ambient.Contains >> not) |> Set.ofList
    let memberContexts =
        lazy
            (state.TypeDefs
             |> Map.toSeq
             |> Seq.filter (fun (_, info) -> info.IsContext)
             |> Seq.collect (fun (ctxName, info) -> info.Members.Values |> Seq.map (fun symbol -> symbol, ctxName))
             |> Map.ofSeq)
    // The supplied contexts that naming `node` requires: the one whose member it names, or those the def it names
    // requires.
    let requiredAt (node: Expression) =
        match state.References |> Map.tryFind node.Id with
        | Some symbol ->
            match memberContexts.Force().TryFind symbol, state.Symbols.TryFind symbol with
            | Some ctxName, _ -> [ ctxName ]
            | None, Some declared when declared.Kind = TopLevelValue || declared.Kind = LocalValue ->
                state.ContextRequirements |> Map.tryFind declared.Name |> Option.map namesOf |> Option.defaultValue []
            | _ -> []
            |> List.filter supplied.Contains
        | None -> []
    let usesDirectly (_: Expression) (node: Expression) = (memberNamed node).IsSome || not (requiredAt node).IsEmpty
    EscapingLambdas.tryFind state usesDirectly blocks result
    |> Option.map (fun escaping ->
        let within = SyntaxTree.expressionsWithin escaping
        // Through a local function, it uses what the scope supplies.
        let used = within |> List.collect requiredAt |> List.distinct
        escaping, (if used.IsEmpty then List.ofSeq supplied else used), within |> List.choose memberNamed |> List.distinct)

/// The error for `escaping`, which uses the contexts `used`, or else the members `members`, leaving the scope that
/// supplies them at `supplier`.
let private escapes (escaping: Expression) (used: Identifier list) (members: Identifier list) (where: string) (supplier: SourceSpan) (label: string) =
    let quoted names = names |> List.map (fun name -> $"'{name}'") |> Diagnostics.listed
    let required =
        match used with
        | [ single ] -> $"the context '{single}'"
        | _ :: _ -> $"the contexts {quoted used}"
        | [] -> quoted members
    let subject =
        match escaping.Kind with
        | IdentifierExpr name -> $"'{name}'"
        | _ -> "This lambda"
    let message = $"{subject} requires {required}, which is not available where it ends up: {where}"
    [ Diagnostics.at DiagnosticCodes.ContextEscapesScope escaping.Span message |> Diagnostics.withRelated supplier label ]

/// The error, if any, for the def `name`, whose type requires `contexts` and whose value is `value`: what its function
/// returns must not be, or hold, a lambda that uses one of them.
let checkDef (state: InferState) (name: Identifier) (contexts: TypeExpr list option) (value: Expression) : Result<unit, Diagnostic list> =
    match contexts, value.Kind with
    | Some contexts, Lambda(_, body) ->
        let supplied = namesOf contexts
        match tryFindEscaping state supplied (fun _ -> None) (SyntaxTree.blocksWithin body) (EscapingLambdas.resultOf body) with
        | Some (escaping, used, members) ->
            let where = $"it is returned from '{name}', which supplies it only inside itself"
            Error (escapes escaping used members where (declarationSpan state DefValue value) "the def that supplies it")
        | None -> Ok ()
    | _ -> Ok ()

/// The names a `use` of `value` loads: the fields of the record it is.
let private loadedBy (state: InferState) (value: Expression) : Set<Identifier> =
    let fieldsOf ty =
        match Unifier.unalias ty with
        | TyRecord fields -> fields.Keys |> Set.ofSeq
        | _ -> Set.empty
    match state.ExprTypes.TryFind value.Id |> Option.map (resolveSoFar state >> Unifier.unalias) with
    | Some (TyNominal(_, underlying, _)) -> fieldsOf underlying
    | Some ty -> fieldsOf ty
    | None -> Set.empty

/// The contexts a `use` of `value`, which loads `loaded`, supplies: the one its type names, or else each whose members
/// it loads.
let private suppliedBy (state: InferState) (value: Expression) (loaded: Set<Identifier>) : Identifier list =
    match state.ExprTypes.TryFind value.Id |> Option.map (resolveSoFar state) with
    | Some (TyNominal({ Name = name }, _, _) | TyAlias(name, _, _)) when state.TypeDefs |> Map.tryFind name |> Option.exists _.IsContext -> [ name ]
    | _ ->
        state.TypeDefs
        |> Map.toList
        |> List.filter (fun (_, info) -> info.IsContext && not info.Members.IsEmpty && info.Members.Keys |> Seq.forall loaded.Contains)
        |> List.distinctBy (fun (_, info) -> info.Symbol)
        |> List.map fst

/// The name `node` names as a value or calls, if it does.
let private nameOf (node: Expression) =
    match node.Kind with
    | IdentifierExpr name
    | FunctionCall(name, _, _)
    | Pipe(_, name, _, _) -> Some name
    | _ -> None

/// The nodes within `exprs` that a `use` among them loads names for, each with the names it loads there: a `use`
/// nearer to them than the one being checked.
let private shadowedWithin (state: InferState) (exprs: Expression list) : Map<NodeId, Set<Identifier>> =
    let mark (names: Set<Identifier>) (within: Expression list) (shadowed: Map<NodeId, Set<Identifier>>) =
        within
        |> List.collect SyntaxTree.expressionsWithin
        |> List.fold (fun acc node ->
            acc |> Map.add node.Id (Set.union names (acc |> Map.tryFind node.Id |> Option.defaultValue Set.empty))) shadowed
    exprs
    |> List.collect SyntaxTree.expressionsWithin
    |> List.fold (fun shadowed node ->
        match node.Kind with
        | UseIn(value, body) -> mark (loadedBy state value) [ body ] shadowed
        | Block statements ->
            statements
            |> List.indexed
            |> List.fold (fun acc (index, statement) ->
                match statement.Kind with
                | UseStatement value -> mark (loadedBy state value) (statements |> List.skip (index + 1) |> List.collect SyntaxTree.ofStatement) acc
                | _ -> acc) shadowed
        | _ -> shadowed) Map.empty

/// The error, if any, for a `use` of `value`, written at `useSpan`, whose scope is `scope`, the expressions it loads
/// its members for, with `blocks` the blocks among them, and which results in `result`: that must not be, or hold, a
/// lambda that uses what the `use` loads. A lambda uses a member it loads when it names one that nothing nearer binds.
/// `where` says where the lambda that escapes goes.
let private checkUse (state: InferState) (useSpan: SourceSpan) (value: Expression) (scope: Expression list) (blocks: Statement list list) (result: Expression) (where: Expression -> string) =
    let loaded = loadedBy state value
    if loaded.IsEmpty then
        Ok ()
    else
        let shadowed = shadowedWithin state scope
        let memberNamed (node: Expression) =
            nameOf node
            |> Option.filter (fun name ->
                loaded.Contains name
                && not (state.References.ContainsKey node.Id)
                && not (shadowed |> Map.tryFind node.Id |> Option.exists (Set.contains name)))
        let supplied = suppliedBy state value loaded
        match tryFindEscaping state supplied memberNamed blocks result with
        | Some (escaping, used, members) ->
            Error (escapes escaping used (if members.IsEmpty then List.ofSeq loaded else members) (where escaping) useSpan "the `use`")
        | None -> Ok ()

/// What a lambda that is the result of a `use` leaves.
let private resultOfUse = "it is the result of the `use` that loads it"

/// The error, if any, for `use value in body`, at `expr` (see `checkUse`). A body in braces that escapes is most likely
/// meant as a block (see `Linters.UseInBraces`).
let checkUseIn (state: InferState) (expr: Expression) (value: Expression) (body: Expression) : Result<unit, Diagnostic list> =
    let where (escaping: Expression) =
        match body.Kind with
        | Lambda([], _) when escaping.Id = body.Id ->
            $"{resultOfUse}. The braces make it a function of no arguments: to run the statements with the context loaded, put them on the lines after `in`, indented"
        | _ -> resultOfUse
    checkUse state (SourceSpan.between expr.Span value.Span) value [ body ] (SyntaxTree.blocksWithin body) (EscapingLambdas.resultOf body) where

/// The error, if any, for the `use` statements among `statements`, a block's (see `checkUse`): the scope of each is the
/// statements after it.
let checkBlock (state: InferState) (statements: Statement list) : Result<unit, Diagnostic list> =
    match statements |> List.tryLast |> Option.bind (fun last -> Statement.finalExpression last.Kind) with
    | Some result ->
        statements
        |> List.indexed
        |> List.fold (fun checkedSoFar (index, statement) ->
            checkedSoFar
            |> Result.bind (fun () ->
                match statement.Kind with
                | UseStatement value ->
                    let rest = statements |> List.skip (index + 1)
                    let scope = rest |> List.collect SyntaxTree.ofStatement
                    checkUse state statement.Span value scope (rest :: (scope |> List.collect SyntaxTree.blocksWithin)) result (fun _ -> resultOfUse)
                | _ -> Ok ())) (Ok ())
    | None -> Ok ()
