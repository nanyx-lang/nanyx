/// What the lowering to the core IR (see `CoreIrLower`) reads from the syntax tree, apart from types.
module internal Transpiler.Wasm.CoreIrLowerSyntax

open NyxCompiler
open Parser.Program

/// Whether lowering `expr` stores to a local before its value is ready: a block with statements before its last
/// expression, a `match` (which holds its scrutinees in locals), an interpolated string (which holds its holes), and
/// so on. Such an operand is lowered whole into one temporary (see `Atoms`). What it finds of each expression it looks
/// at is kept in `found`, so that asking it of each operand of a long chain in turn looks at each part of the chain
/// once (#2351). `callStores` says it of a call the syntax alone can't tell of: one through a function value whose
/// type holds boxes, which holds its arguments in locals (see `CallValue`).
let rec storesBeforeValue (callStores: Expression -> bool) (found: System.Collections.Generic.Dictionary<Expression, bool>) (expr: Expression) =
    match found.TryGetValue expr with
    | true, stores -> stores
    | _ ->
        let stores = storesBeforeValueOf callStores found expr
        found.[expr] <- stores
        stores

and private storesBeforeValueOf callStores found (expr: Expression) =
    let storesBeforeValue = storesBeforeValue callStores found
    match expr.Kind with
    | Block [ Kind (ExprStatement last) ] -> storesBeforeValue last
    | Block _
    | ExpressionKind.Match _ -> true
    | IfExpr(cond, _, _) -> storesBeforeValue cond
    | BinaryOp(_, _, left, right) -> storesBeforeValue left || storesBeforeValue right
    // `dbg` holds its value in a local while it prints it, a `for` loop stores its elements, and a `while` loop's
    // condition and body run again each time round.
    | FunctionCall(name, _, _) when name = DbgSyntax.builtIn || name = DbgSyntax.shown || name = ForSyntax.builtIn -> true
    // A collection literal's array is held in a local while its items are written into it.
    | FunctionCall(name, _, [ _ ]) when name = CollectionSyntax.arrayOf -> true
    | WhileSyntax.Call _ -> true
    // A read of a cell copies what it holds into a local of its own (see `CellSyntax`).
    | FunctionCall(name, _, [ _ ]) when name = CellSyntax.read || name = CellSyntax.unbox -> true
    // A new box, a captured cell's or an `MRef`'s, is held in a local while it goes on its region, and a mutable array's
    // intrinsic holds the box, or what it holds, in one (see `CoreIrRegions`).
    | FunctionCall(name, _, _) when name = CellSyntax.box || name = Intrinsics.boxNew || CoreIrRegions.isMutableArrayIntrinsic name -> true
    | FunctionCall(_, _, args) -> callStores expr || args |> List.exists storesBeforeValue
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

/// The messages of the errors deferred to run time inside `expr` (the `__crash` calls that stand for what failed to
/// check), in the order they are written. What patterns hold and what a `use` loads are not looked inside.
let rec deferredMessages (expr: Expression) : string list =
    let all (exprs: Expression list) = exprs |> List.collect deferredMessages
    match expr.Kind with
    | CrashSyntax.Call message -> [ message ]
    | Block statements ->
        statements
        |> List.choose (fun statement ->
            match statement.Kind with
            | DefStatement(_, _, _, value)
            | ExprStatement value -> Some value
            | _ -> None)
        |> all
    | UseIn(_, body) -> deferredMessages body
    | ExpressionKind.Match(values, arms) -> all (values @ (arms |> List.map snd))
    | Except(value, arms) -> all (value :: (arms |> List.map snd))
    | _ -> SyntaxTree.childrenOf expr |> fst |> all

/// The type a type annotation names, if it is a primitive or a type the module defines: `string`, `(int)`, `Point`.
let rec typeOfAnnotation (namedTypes: Map<string, Ty>) (typeExpr: TypeExpr) : Ty option =
    match typeExpr.Kind with
    | TypeName ("int" | "string" | "bool" | "float" | "unit" as name) -> Some (TyPrimitive name)
    | TypeName name -> namedTypes |> Map.tryFind name |> Option.orElse (if SizedNumber.isName name || name = Chars.name then Some (TyPrimitive name) else None)
    | TypeUnit -> Some (TyPrimitive "unit")
    | TypeTuple [ single ] -> typeOfAnnotation namedTypes single
    | _ -> None

let parametersAndBody (expr: Expression) =
    match expr.Kind with
    | Lambda(parameters, body, _) -> parameters |> List.map fst, body
    | _ -> [], expr
