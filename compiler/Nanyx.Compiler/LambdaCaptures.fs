namespace NyxCompiler

open Parser.Program

/// What a lambda captures: the names of the code around it that it refers to, which its closure block carries (see the
/// backend's `LambdaLifting`).
module LambdaCaptures =
    /// Every name a pattern binds.
    let rec boundNamesPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | IdentifierPattern name -> [ name ]
        | TagPattern(_, Some payload) -> boundNamesPattern payload
        | ArgumentTagPattern(_, arguments, payload) -> boundNames arguments @ (payload |> Option.map boundNamesPattern |> Option.defaultValue [])
        | TuplePattern(patterns, _)
        | AndPattern patterns -> patterns |> List.collect boundNamesPattern
        | ListPattern(patterns, rest) ->
            (patterns |> List.collect boundNamesPattern) @ (rest |> Option.map boundNamesPattern |> Option.defaultValue [])
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect boundNamesPattern
        | RecordPattern(_, patterns) -> patterns |> List.collect boundNamesPattern
        | RecordMemberPattern(members, _) -> members |> List.collect (fun (name, p) -> name :: boundNamesPattern p)
        // A guard's predicate is inlined where the arm is tested, so what it binds is bound there.
        | GuardPattern predicate -> boundNames predicate
        | TagPattern(_, None)
        | LiteralPattern _
        | RangePattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []

    /// Every name bound anywhere inside an expression: local defs, lambda parameters and pattern variables.
    /// (An over-approximation of what a nested lambda could capture, which errs on the side of not lifting.) What a `use`
    /// loads is not looked inside: its lambdas are lifted in the scope around it.
    and boundNames (expr: Expression) : string list =
        match expr.Kind with
        | Lambda(args, body, _) -> (args |> List.map fst) @ boundNames body
        | Block statements ->
            statements |> List.map _.Kind
            |> List.collect (function
                | DefStatement(_, name, _, rhs) -> name :: boundNames rhs
                | ExprStatement value -> boundNames value
                | UseStatement _
                | ImportStatement _
                | TypeDefStatement _
                | AttributeStatement _ -> [])
        | Except(value, arms) -> boundNames { expr with Kind = Match([ value ], arms) }
        | Match(values, arms) ->
            (values |> List.collect boundNames)
            @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect boundNamesPattern) @ boundNames body))
        | UseIn(_, body) -> boundNames body
        | WorkflowBindExpr(name, value) -> name :: boundNames value
        | _ -> SyntaxTree.childrenOf expr |> fst |> List.collect boundNames

    /// Every name an expression refers to, with the node that refers to it: identifiers, callees and pipe targets.
    let rec references (expr: Expression) : (string * Expression) list =
        match expr.Kind with
        | IdentifierExpr name -> [ name, expr ]
        | FunctionCall(name, _, args) -> (name, expr) :: (args |> List.collect references)
        | Pipe(value, name, _, args) -> (name, expr) :: references value @ (args |> List.collect references)
        | Except(value, arms) -> references { expr with Kind = Match([ value ], arms) }
        // Each arm's patterns, then its body.
        | Match(values, arms) ->
            (values |> List.collect references)
            @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect Pattern.expressions |> List.collect references) @ references body))
        | _ ->
            let children, statements = SyntaxTree.childrenOf expr
            (children @ (statements |> List.collect SyntaxTree.ofStatement)) |> List.collect references

    /// The names of `scope` that `liftedBody`, the body of a lambda that binds `bound` (its parameters, and every name
    /// a def, pattern or lambda inside it binds), refers to from outside: those it names and does not bind, and those
    /// it names before it binds them, as the value of a def that shadows the name does (`def x = x + 1`, `mut state =
    /// #continue(state)`).
    let capturedFrom (scope: Set<string>) (parameters: Set<string>) (bound: Set<string>) (liftedBody: Expression) =
        let referenced = references liftedBody |> List.map fst |> Set.ofList
        let readBeforeBound = Set.difference (SyntaxTree.freeNames liftedBody |> Set.ofList) parameters
        Set.union (Set.difference referenced bound) readBeforeBound |> Set.intersect scope |> Set.toList
