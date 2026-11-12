/// The function values lambda lifting leaves in the syntax tree (see `LambdaLifting.lift`), as the core IR is given
/// them: a closure block is a call to the marker "__closure" with the lifted function's table slot, a call to the
/// marker "__slot" with its name, and then the values it captures. (A marker call rather than a tuple, so that
/// `f(closure)` is not flattened into one argument per field.) No code can write either name. The names an expression
/// refers to, and the uses of a local through the closures that capture it, are found here too.
module internal Transpiler.Wasm.ClosureSyntax

open Parser.Program

let builtIn = "__closure"

/// The marker that names the lifted function whose table slot a closure block holds.
let slot = "__slot"

/// The name of the environment parameter every lifted function takes first: the closure block itself.
let envParameter = "__env"

/// The closure block for the lifted function `target` over `captured`, standing in for `origin` (the lambda or function
/// reference it replaces), whose id and span it keeps.
let make (origin: Expression) (target: string) (captured: Expression list) : Expression =
    { origin with
        Kind = FunctionCall(builtIn, None, Expression.synthetic (FunctionCall(slot, None, [ Expression.synthetic (IdentifierExpr target) ])) :: captured) }

/// A closure block's lifted function, and what it captures.
let (|Closure|_|) (kind: ExpressionKind) =
    match kind with
    | FunctionCall(name, _, Kind (FunctionCall(slotName, _, [ Kind (IdentifierExpr target) ])) :: captured) when name = builtIn && slotName = slot ->
        Some (target, captured)
    | _ -> None

/// Every name an expression refers to, with the node that refers to it: identifiers, callees and pipe targets.
let rec references (expr: Expression) : (string * Expression) list =
    match expr.Kind with
    | IdentifierExpr name -> [ name, expr ]
    | FunctionCall(name, _, args) -> (name, expr) :: (args |> List.collect references)
    | Pipe(value, name, _, args) -> (name, expr) :: references value @ (args |> List.collect references)
    | Lambda(_, body, _) -> references body
    | BinaryOp(_, _, left, right) -> references left @ references right
    | IfExpr(cond, thenExpr, elseExpr) -> references cond @ references thenExpr @ references elseExpr
    | Block statements -> statements |> List.collect referencesInStatement
    | Except(value, arms) -> references { expr with Kind = Match([ value ], arms) }
    | Match(values, arms) ->
        (values |> List.collect references)
        @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect referencesInPattern) @ references body))
    | TupleExpr items
    | ListExpr items -> items |> List.collect references
    | RecordExpr fields ->
        fields |> List.collect (function NamedField(_, value) | PositionalField value -> references value)
    | TagExpr(_, payload) -> payload |> Option.map references |> Option.defaultValue []
    | MemberAccess(inner, _, _) -> references inner
    | UseIn(value, body) -> references value @ references body
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> references value
    | InterpolatedString parts ->
        parts |> List.collect (function StringExpr value -> references value | StringText _ -> [])
    | ContextMemberCall(_, _, args) -> args |> List.collect references
    | ContextMemberAccess _
    | UnitExpr
    | LiteralExpr _ -> []

and private referencesInStatement (statement: Statement) : (string * Expression) list =
    match statement.Kind with
    | DefStatement(_, _, _, rhs) -> references rhs
    | ExprStatement value
    | UseStatement value -> references value
    | ImportStatement _
    | TypeDefStatement _ -> []

and private referencesInPattern (pattern: Pattern) : (string * Expression) list =
    match pattern.Kind with
    | GuardPattern predicate -> references predicate
    | RangePattern(startValue, endValue) -> references startValue @ references endValue
    | TagPattern(_, Some payload) -> referencesInPattern payload
    | TuplePattern patterns
    | AndPattern patterns
    | ListPattern(patterns, _) -> patterns |> List.collect referencesInPattern
    | ListSplatMiddle(before, after) -> (before @ after) |> List.collect referencesInPattern
    | RecordPattern(_, patterns) -> patterns |> List.collect referencesInPattern
    | RecordMemberPattern(members, _) -> members |> List.collect (snd >> referencesInPattern)
    | TagPattern(_, None)
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []

/// The nodes in `expr`, after lifting, that use the local `name`: each reference to it, and, for a closure that captures
/// it, the uses in the lifted function the closure is over (its definition, which `definitionOf` finds), which binds it
/// under the same name. The capture itself is not a use.
let rec usesThroughClosures (definitionOf: string -> Expression option) (name: string) (expr: Expression) : Expression list =
    let found = references expr
    let closures =
        found
        |> List.choose (fun (_, node) ->
            match node.Kind with
            | Closure(target, captured) ->
                let captures = captured |> List.filter (function Kind (IdentifierExpr captured) -> captured = name | _ -> false)
                if captures.IsEmpty then None else Some (target, captures)
            | _ -> None)
    let captures = closures |> List.collect snd
    let direct =
        found
        |> List.filter (fun (referenced, node) -> referenced = name && not (captures |> List.exists (fun capture -> obj.ReferenceEquals(capture, node))))
        |> List.map snd
    direct @ (closures |> List.collect (fun (target, _) -> definitionOf target |> Option.map (usesThroughClosures definitionOf name) |> Option.defaultValue []))
