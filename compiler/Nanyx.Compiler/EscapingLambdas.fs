/// Finding a lambda that outlives the scope that gives it something it uses: a `memory` block's cells (see
/// `MemoryCells`), or the members of a context a def or a `use` supplies (see `ContextEscapes`).
module internal NyxCompiler.EscapingLambdas

open Parser.Program
open NyxCompiler.InferState

let rec private holdsFunction (ty: Ty) =
    match ty with
    | TyFunc _ -> true
    | TyAlias(_, _, underlying)
    | TyNominal(_, underlying, _) -> holdsFunction underlying
    | TyRecord fields
    | TyOpenRecord(fields, _) -> fields |> Map.exists (fun _ field -> holdsFunction field)
    | TyTag(_, payload) -> payload |> Option.exists holdsFunction
    | TyApply(_, args)
    | TyUnion args -> args |> List.exists holdsFunction
    | TyVar _
    | TyPrimitive _ -> false

/// What `body` results in: the final expression of its block, or `body` itself.
let resultOf (body: Expression) : Expression =
    match body.Kind with
    | Block statements -> statements |> List.tryLast |> Option.bind (fun last -> Statement.finalExpression last.Kind) |> Option.defaultValue body
    | _ -> body

/// The lambda that uses something of a scope's, or the name of a local function that does, that `result`, what the
/// scope results in, is or holds, if any. `usesDirectly lambda node` says whether `node`, within `lambda`, names
/// something of the scope's itself. A lambda also uses it when it names a local function that does: one defined in
/// `blocks`, the scope's blocks, each as its statements.
let tryFind (state: InferState) (usesDirectly: Expression -> Expression -> bool) (blocks: Statement list list) (result: Expression) : Expression option =
    if state.ExprTypes.TryFind result.Id |> Option.exists (resolveSoFar state >> holdsFunction) then
        let symbolOf (node: Expression) = state.References |> Map.tryFind node.Id
        let uses (users: Set<SymbolId>) (lambda: Expression) =
            SyntaxTree.expressionsWithin lambda
            |> List.exists (fun node -> usesDirectly lambda node || symbolOf node |> Option.exists users.Contains)
        let firstUser (users: Set<SymbolId>) (expr: Expression) =
            SyntaxTree.expressionsWithin expr
            |> List.tryFind (fun node ->
                match node.Kind with
                | Lambda _ -> uses users node
                | IdentifierExpr _ -> symbolOf node |> Option.exists users.Contains
                | _ -> false)
        // The local functions in the scope that use what it gives, in the order they are defined.
        let users =
            blocks
            |> List.concat
            |> List.fold (fun users (statement: Statement) ->
                match statement.Kind with
                | DefStatement(_, name, _, value) when (firstUser users value).IsSome ->
                    match state.DeclarationSymbols |> Map.tryFind (value.Id, DefValue, name) with
                    | Some symbol -> users.Add symbol
                    | None -> users
                | _ -> users) Set.empty
        firstUser users result
    else
        None
