/// What the lowering to the core IR (see `CoreIrLower`) reads from the syntax tree, apart from types.
module internal Transpiler.Wasm.CoreIrLowerSyntax

open NyxCompiler
open Parser.Program

/// Whether lowering `expr` stores to a local before its value is ready: a block with statements before its last
/// expression, a `match` (which holds its scrutinees in locals), an interpolated string (which holds its holes), and
/// so on. Such an operand is lowered whole into one temporary (see `Atoms`).
let rec storesBeforeValue (expr: Expression) =
    match expr.Kind with
    | Block [ Kind (ExprStatement last) ] -> storesBeforeValue last
    | Block _
    | ExpressionKind.Match _ -> true
    | IfExpr(cond, _, _) -> storesBeforeValue cond
    | BinaryOp(_, _, left, right) -> storesBeforeValue left || storesBeforeValue right
    // `dbg` holds its value in a local while it prints it, and a loop stores its elements.
    | FunctionCall("dbg", _, _)
    | FunctionCall("__for", _, _) -> true
    // A read of a cell copies what it holds into a local of its own (see `CellSyntax`).
    | FunctionCall(name, _, [ _ ]) when name = CellSyntax.read || name = CellSyntax.unbox -> true
    | FunctionCall(_, _, args) -> args |> List.exists storesBeforeValue
    | TupleExpr items
    | ListExpr items -> items |> List.exists storesBeforeValue
    | RecordExpr fields ->
        fields |> List.exists (function NamedField(_, value) | PositionalField value -> storesBeforeValue value)
    | MemberAccess(inner, _, _) -> storesBeforeValue inner
    | TagExpr(_, Some payload) -> storesBeforeValue payload
    // Each hole is stored in its local before the string is built.
    | InterpolatedString parts -> parts |> List.exists (function StringExpr _ -> true | StringText _ -> false)
    | _ -> false

/// The fields of a record expression by name, a positional field named by its position.
let recordExprFields (fields: RecordField list) =
    fields
    |> List.mapi (fun index field ->
        match field with
        | NamedField(name, value) -> name, value
        | PositionalField value -> string index, value)

/// The messages of the errors deferred to run time inside `expr` (the `__panic` calls that stand for what failed to
/// check), in the order they are written.
let rec deferredMessages (expr: Expression) : string list =
    let all (exprs: Expression list) = exprs |> List.collect deferredMessages
    match expr.Kind with
    | FunctionCall("__panic", _, [ Kind (LiteralExpr(StringLit message)) ]) -> [ message ]
    | InterpolatedString parts -> parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None) |> all
    | Block statements ->
        statements
        |> List.choose (fun statement ->
            match statement.Kind with
            | DefStatement(_, _, _, value)
            | ExprStatement value -> Some value
            | _ -> None)
        |> all
    | BinaryOp(_, _, left, right) -> all [ left; right ]
    | IfExpr(cond, ifTrue, ifFalse) -> all [ cond; ifTrue; ifFalse ]
    | FunctionCall(_, _, args)
    | ContextMemberCall(_, _, args) -> all args
    | Lambda(_, body, _)
    | UseIn(_, body)
    | MemberAccess(body, _, _)
    | WorkflowBindExpr(_, body)
    | WorkflowReturnExpr body -> deferredMessages body
    | Pipe(value, _, _, args) -> all (value :: args)
    | TupleExpr items
    | ListExpr items -> all items
    | RecordExpr fields -> fields |> List.map (function NamedField(_, value) | PositionalField value -> value) |> all
    | ExpressionKind.Match(values, arms) -> all (values @ (arms |> List.map snd))
    | Except(value, arms) -> all (value :: (arms |> List.map snd))
    | TagExpr(_, payload) -> payload |> Option.toList |> all
    | ContextMemberAccess _
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// The type a type annotation names, if it is a primitive or a type the module defines: `string`, `(int)`, `Point`.
let rec typeOfAnnotation (namedTypes: Map<string, Ty>) (typeExpr: TypeExpr) : Ty option =
    match typeExpr.Kind with
    | TypeName ("int" | "string" | "bool" | "float" | "unit" as name) -> Some (TyPrimitive name)
    | TypeName name -> namedTypes |> Map.tryFind name |> Option.orElse (if SizedNumber.isName name then Some (TyPrimitive name) else None)
    | TypeUnit -> Some (TyPrimitive "unit")
    | TypeTuple [ single ] -> typeOfAnnotation namedTypes single
    | _ -> None

let flattenCallArgs (args: Expression list) =
    match args with
    | [ Kind (TupleExpr items) ] -> items
    | _ -> args

let parametersAndBody (expr: Expression) =
    match expr.Kind with
    | Lambda(parameters, body, _) -> parameters |> List.map fst, body
    | _ -> [], expr
