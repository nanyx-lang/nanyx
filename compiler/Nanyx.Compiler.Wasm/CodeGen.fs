module Transpiler.Wasm.CodeGen

open System
open System.Text
open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat

/// Something the backend cannot compile, and where it is written: what `tryTranspileTypedModuleToWat` reports as a
/// diagnostic.
exception UnsupportedConstruct of message: string * span: SourceSpan

/// Fails with `message`, placed at `span`.
let private unsupportedAt (span: SourceSpan) (message: string) : 'a = raise (UnsupportedConstruct(message, span))

type private ContextFn = string * int * int

/// The module's function table (`$ctx_table`), as far as functions used as values are concerned: a top-level
/// function referenced without being called, or a lifted lambda, gets a slot here and is called through
/// `call_indirect`. Shared by every function of a module and read when the table is rendered.
type private FunctionTable =
    { /// Function name -> (WebAssembly name, slot, arity).
      Values: System.Collections.Generic.Dictionary<string, ContextFn>
      /// The next free slot.
      mutable NextSlot: int
      /// The arities of every indirect call made through a local, so a `$ctx_fn_N` type exists for each.
      IndirectArities: System.Collections.Generic.HashSet<int> }

/// The record layouts the module allocates, each numbered as the functions are emitted. Every record carries its
/// layout's number.
type private RecordTable =
    { /// Field names in storage order -> the layout's number.
      LayoutIds: System.Collections.Generic.Dictionary<string list, int> }

/// A module of just the allocator, exporting `alloc`, `free` and the memory, so that tests can drive it directly:
/// nothing in generated code frees yet.
let allocatorModuleWat =
    [ "(module"
      "  (memory 1)"
      "  (export \"memory\" (memory 0))"
      $"  (global $heap_ptr (mut i32) (i32.const {heapStartOffset}))"
      runtimeHelpers |> Map.find Helpers.alloc
      runtimeHelpers |> Map.find Helpers.free
      $"  (export \"alloc\" (func ${Helpers.alloc}))"
      $"  (export \"free\" (func ${Helpers.free}))"
      ")" ]
    |> String.concat "\n"

let private ctxFnName (name: string, _, _) = name
let private ctxFnSlot (_, slot: int, _) = slot
let private ctxFnArity (_, _, arity: int) = arity

// Extract context type expressions from a TypeWithContext or TypeContext annotation
let private extractContextTypes (typeExpr: TypeExpr option) : TypeExpr list =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, _))) -> contexts
    | Some (Kind (TypeContext contexts)) -> contexts
    | _ -> []

// Get the inner type (without context) from a TypeWithContext or TypeContext
let private unwrapContextType (typeExpr: TypeExpr option) : TypeExpr option =
    match typeExpr with
    | Some (Kind (TypeWithContext(_, inner))) -> Some inner
    | Some (Kind (TypeContext _)) -> None  // TypeContext alone has no inner type
    | other -> other


/// Whether `name` is a nominal type's, qualified by its module block or not (`$Money`, `main.money.$Money`).
let private isNominalTypeName (name: string) =
    let own = match name.LastIndexOf '.' with -1 -> name | dot -> name.Substring(dot + 1)
    own.StartsWith "$"

let private flattenCallArgs (args: Expression list) =
    match args with
    | [Kind (TupleExpr items)] -> items
    | _ -> args

/// The function-table slot of a top-level function used as a value, assigning one on first use.
let private functionValueSlot (table: FunctionTable) (name: string) (arity: int) : int =
    match table.Values.TryGetValue name with
    | true, ctxFn -> ctxFnSlot ctxFn
    | _ ->
        let slot = table.NextSlot
        table.NextSlot <- slot + 1
        table.Values.[name] <- (sanitizeIdentifier name, slot, arity)
        slot

let rec private collectExprLocalNames (expr: Expression) : string list =
    match expr.Kind with
    | Block statements -> collectStatementLocalNames statements
    | BinaryOp(_, left, right) -> (collectExprLocalNames left) @ (collectExprLocalNames right)
    | IfExpr(cond, thenExpr, elseExpr) ->
        (collectExprLocalNames cond) @ (collectExprLocalNames thenExpr) @ (collectExprLocalNames elseExpr)
    | FunctionCall(_, _, args) -> args |> List.collect collectExprLocalNames
    | Lambda(_, body) -> collectExprLocalNames body
    | Pipe(value, _, _, args) -> (collectExprLocalNames value) @ (args |> List.collect collectExprLocalNames)
    | TupleExpr items
    | ListExpr items -> items |> List.collect collectExprLocalNames
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectExprLocalNames value
            | PositionalField value -> collectExprLocalNames value)
    | Match(values, arms) ->
        let valueLocals = values |> List.collect collectExprLocalNames
        let armLocals =
            arms
            |> List.collect (fun (_, armExpr) -> collectExprLocalNames armExpr)
        valueLocals @ armLocals
    | UseIn(_, body) -> collectExprLocalNames body
    | MemberAccess(inner, _, _) -> collectExprLocalNames inner
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectExprLocalNames
    | TagExpr(_, payload) -> payload |> Option.map collectExprLocalNames |> Option.defaultValue []
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr inner -> collectExprLocalNames inner)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectExprLocalNames value
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectStatementLocalNames (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, name, _, rhs) -> name :: collectExprLocalNames rhs
        | ExprStatement expr -> collectExprLocalNames expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

let private sanitizeImportSuffix (name: string) = sanitizeIdentifier name


let private escapeWasmData (bytes: byte array) =
    let sb = StringBuilder()
    for b in bytes do
        let c = char b
        if b >= 0x20uy && b <= 0x7Euy && c <> '"' && c <> '\\' then
            sb.Append c |> ignore
        else
            sb.Append(sprintf "\\%02x" b) |> ignore
    sb.ToString()

let private buildStringLiteralMap (literals: string list) =
    let mutable offset = stringDataBaseOffset
    let entries =
        literals
        |> List.map (fun value ->
            let bytes = Encoding.UTF8.GetBytes value
            let lengthBytes = BitConverter.GetBytes bytes.Length
            let dataBytes = Array.concat [ lengthBytes; bytes; [| 0uy |] ]
            let currentOffset = offset
            offset <- offset + dataBytes.Length
            value, currentOffset, dataBytes)
    let map = entries |> List.map (fun (value, baseOffset, _) -> value, baseOffset + 4) |> Map.ofList
    map, entries

/// The number of the record layout with `fieldNames` (in storage order), numbering it if it is new.
let private recordLayoutId (table: RecordTable) (fieldNames: string list) =
    let layouts = table.LayoutIds
    match layouts.TryGetValue fieldNames with
    | true, id -> id
    | _ ->
        let id = layouts.Count + 1
        layouts.[fieldNames] <- id
        id

/// The immediate sub-expressions of `expr`, for traversals that only need to look inside.
let private exprChildren (expr: Expression) : Expression list =
    match expr.Kind with
    | InterpolatedString parts ->
        parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None)
    | Block statements ->
        statements |> List.map _.Kind
        |> List.choose (function
            | DefStatement(_, _, _, rhs) -> Some rhs
            | ExprStatement value -> Some value
            | _ -> None)
    | BinaryOp(_, left, right) -> [ left; right ]
    | IfExpr(cond, thenExpr, elseExpr) -> [ cond; thenExpr; elseExpr ]
    | FunctionCall(_, _, args) -> args
    | Lambda(_, body) -> [ body ]
    | Pipe(value, _, _, args) -> value :: args
    | TupleExpr items
    | ListExpr items -> items
    | RecordExpr fields ->
        fields
        |> List.map (function
            | NamedField(_, value) -> value
            | PositionalField value -> value)
    | Match(values, arms) -> values @ Pattern.armExpressions arms @ (arms |> List.map snd)
    | UseIn(_, body) -> [ body ]
    | MemberAccess(inner, _, _) -> [ inner ]
    | TagExpr(_, payload) -> Option.toList payload
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> [ value ]
    | ContextMemberCall(_, _, args) -> args
    | ContextMemberAccess(_, _)
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// Whether `inner`, an interpolated string's hole, has the error type: the type checker could not tell what it is,
/// because of an error deferred to run time (in a function it calls, say), so there is no way to print it.
let private isErrorTypedHole (checkedTypes: Map<NodeId, Ty>) (inner: Expression) =
    checkedTypes |> Map.tryFind inner.Id |> Option.exists Unifier.isError


/// The holes in the interpolated strings of `expr` that have the error type (see `isErrorTypedHole`).
let rec private errorTypedHoles (checkedTypes: Map<NodeId, Ty>) (expr: Expression) : Expression list =
    let own =
        match expr.Kind with
        | InterpolatedString parts ->
            parts |> List.choose (function StringExpr inner when isErrorTypedHole checkedTypes inner -> Some inner | _ -> None)
        | _ -> []
    own @ (exprChildren expr |> List.collect (errorTypedHoles checkedTypes))

/// The values `expr` matches on that have the error type.
let rec private errorTypedScrutinees (checkedTypes: Map<NodeId, Ty>) (expr: Expression) : Expression list =
    let own =
        match expr.Kind with
        | ExpressionKind.Match(values, _) -> values |> List.filter (isErrorTypedHole checkedTypes)
        | _ -> []
    own @ (exprChildren expr |> List.collect (errorTypedScrutinees checkedTypes))

/// True when `predicate` holds for `expr` or any expression nested inside it.
let rec private existsExpr (predicate: Expression -> bool) (expr: Expression) : bool =
    let recurse = existsExpr predicate
    predicate expr
    || match expr.Kind with
       | FunctionCall(_, _, args) -> args |> List.exists recurse
       | BinaryOp(_, left, right) -> recurse left || recurse right
       | IfExpr(cond, thenExpr, elseExpr) -> recurse cond || recurse thenExpr || recurse elseExpr
       | Block statements -> existsStatements predicate statements
       | Lambda(_, body) -> recurse body
       | Pipe(value, _, _, args) -> recurse value || (args |> List.exists recurse)
       | TupleExpr items
       | ListExpr items -> items |> List.exists recurse
       | RecordExpr fields ->
           fields
           |> List.exists (function
               | NamedField(_, value) -> recurse value
               | PositionalField value -> recurse value)
       | Match(values, arms) ->
           (values @ Pattern.armExpressions arms |> List.exists recurse) || (arms |> List.exists (fun (_, armExpr) -> recurse armExpr))
       | UseIn(_, body) -> recurse body
       | MemberAccess(inner, _, _) -> recurse inner
       | TagExpr(_, payload) -> payload |> Option.exists recurse
       | InterpolatedString parts ->
           parts
           |> List.exists (function
               | StringText _ -> false
               | StringExpr inner -> recurse inner)
       | WorkflowBindExpr(_, value)
       | WorkflowReturnExpr value -> recurse value
       | ContextMemberCall(_, _, args) -> args |> List.exists recurse
       | ContextMemberAccess(_, _)
       | UnitExpr
       | LiteralExpr _
       | IdentifierExpr _ -> false

and private existsStatements (predicate: Expression -> bool) (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, rhs) -> existsExpr predicate rhs
        | ExprStatement expr -> existsExpr predicate expr
        | UseStatement (UseValue value)
        | UseStatement (UseContextInstance(_, value)) -> existsExpr predicate value
        | ImportStatement _
        | TypeDefStatement _ -> false)

let rec private containsDbgCallExpr (expr: Expression) : bool =
    match expr.Kind with
    | FunctionCall(name, _, _) when name = "dbg" -> true
    | FunctionCall(_, _, args) -> args |> List.exists containsDbgCallExpr
    | BinaryOp(_, left, right) -> containsDbgCallExpr left || containsDbgCallExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        containsDbgCallExpr cond || containsDbgCallExpr thenExpr || containsDbgCallExpr elseExpr
    | Block statements -> containsDbgCallStatements statements
    | Lambda(_, body) -> containsDbgCallExpr body
    | Pipe(value, _, _, args) -> containsDbgCallExpr value || (args |> List.exists containsDbgCallExpr)
    | TupleExpr items
    | ListExpr items -> items |> List.exists containsDbgCallExpr
    | RecordExpr fields ->
        fields
        |> List.exists (function
            | NamedField(_, value) -> containsDbgCallExpr value
            | PositionalField value -> containsDbgCallExpr value)
    | Match(values, arms) ->
        (values @ Pattern.armExpressions arms |> List.exists containsDbgCallExpr)
        || (arms |> List.exists (fun (_, armExpr) -> containsDbgCallExpr armExpr))
    | UseIn(_, body) -> containsDbgCallExpr body
    | MemberAccess(inner, _, _) -> containsDbgCallExpr inner
    | TagExpr(_, payload) -> payload |> Option.exists containsDbgCallExpr
    | InterpolatedString parts ->
        parts
        |> List.exists (function
            | StringText _ -> false
            | StringExpr inner -> containsDbgCallExpr inner)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> containsDbgCallExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists containsDbgCallExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private containsDbgCallStatements (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, rhs) -> containsDbgCallExpr rhs
        | ExprStatement expr -> containsDbgCallExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)

let rec private usesHeapAllocationExpr (expr: Expression) : bool =
    match expr.Kind with
    | TupleExpr _
    | ListExpr _
    | RecordExpr _
    | FunctionCall("__closure", _, _) -> true
    | FunctionCall(_, _, args) -> flattenCallArgs args |> List.exists usesHeapAllocationExpr
    | BinaryOp(_, left, right) -> usesHeapAllocationExpr left || usesHeapAllocationExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        usesHeapAllocationExpr cond || usesHeapAllocationExpr thenExpr || usesHeapAllocationExpr elseExpr
    | Block statements -> usesHeapAllocationStatements statements
    | Lambda(_, body) -> usesHeapAllocationExpr body
    | Pipe(value, _, _, args) -> usesHeapAllocationExpr value || (flattenCallArgs args |> List.exists usesHeapAllocationExpr)
    | Match(values, arms) ->
        (values @ Pattern.armExpressions arms |> List.exists usesHeapAllocationExpr)
        || (arms |> List.exists (fun (_, armExpr) -> usesHeapAllocationExpr armExpr))
    | UseIn(_, body) -> usesHeapAllocationExpr body
    | MemberAccess(inner, _, _) -> usesHeapAllocationExpr inner
    | TagExpr(_, payload) -> payload.IsSome
    | InterpolatedString _ -> true
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> usesHeapAllocationExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists usesHeapAllocationExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private usesHeapAllocationStatements (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, rhs) -> usesHeapAllocationExpr rhs
        | ExprStatement expr -> usesHeapAllocationExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)

let rec private collectDbgTagNamesExpr (expr: Expression) : string list =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (TagExpr(tagName, None))] -> [ tagName ]
        | _ -> []
    | FunctionCall(_, _, args) -> args |> List.collect collectDbgTagNamesExpr
    | BinaryOp(_, left, right) -> collectDbgTagNamesExpr left @ collectDbgTagNamesExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectDbgTagNamesExpr cond @ collectDbgTagNamesExpr thenExpr @ collectDbgTagNamesExpr elseExpr
    | Lambda(_, body) -> collectDbgTagNamesExpr body
    | Pipe(value, _, _, args) -> collectDbgTagNamesExpr value @ (args |> List.collect collectDbgTagNamesExpr)
    | Block statements -> collectDbgTagNamesStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectDbgTagNamesExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectDbgTagNamesExpr value
            | PositionalField value -> collectDbgTagNamesExpr value)
    | Match(values, arms) ->
        let valueTags = values @ Pattern.armExpressions arms |> List.collect collectDbgTagNamesExpr
        let armTags = arms |> List.collect (fun (_, armExpr) -> collectDbgTagNamesExpr armExpr)
        valueTags @ armTags
    | UseIn(_, body) -> collectDbgTagNamesExpr body
    | MemberAccess(value, _, _) -> collectDbgTagNamesExpr value
    | TagExpr(_, payload) -> payload |> Option.map collectDbgTagNamesExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectDbgTagNamesExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectDbgTagNamesExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectDbgTagNamesExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectStringLiteralsExpr (expr: Expression) : string list =
    match expr.Kind with
    | LiteralExpr(StringLit value) -> [ value ]
    | Block statements -> collectStringLiteralsStatements statements
    | BinaryOp(_, left, right) -> collectStringLiteralsExpr left @ collectStringLiteralsExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectStringLiteralsExpr cond @ collectStringLiteralsExpr thenExpr @ collectStringLiteralsExpr elseExpr
    | FunctionCall(_, _, args) -> args |> List.collect collectStringLiteralsExpr
    | Lambda(_, body) -> collectStringLiteralsExpr body
    | Pipe(value, _, _, args) -> collectStringLiteralsExpr value @ (args |> List.collect collectStringLiteralsExpr)
    | TupleExpr items
    | ListExpr items -> items |> List.collect collectStringLiteralsExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectStringLiteralsExpr value
            | PositionalField value -> collectStringLiteralsExpr value)
    | Match(values, arms) ->
        let valueStrings = values |> List.collect collectStringLiteralsExpr
        let armStrings =
            arms
            |> List.collect (fun (patterns, armExpr) ->
                (patterns |> List.collect collectStringLiteralsPattern) @ collectStringLiteralsExpr armExpr)
        valueStrings @ armStrings
    | UseIn(_, body) -> collectStringLiteralsExpr body
    | MemberAccess(inner, _, _) -> collectStringLiteralsExpr inner
    | TagExpr(_, payload) -> payload |> Option.map collectStringLiteralsExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        // Literals inside `{...}` holes need data-segment offsets like any other literal.
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectStringLiteralsExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectStringLiteralsExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectStringLiteralsExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// String literals that appear inside patterns (`| "Sat" -> ...`, `| #name("x") -> ...`) need data-segment
/// offsets just like literals in expressions.
and private collectStringLiteralsPattern (pattern: Pattern) : string list =
    match pattern.Kind with
    | LiteralPattern(StringLit value) -> [ value ]
    | TagPattern(_, Some payload) -> collectStringLiteralsPattern payload
    | TuplePattern patterns
    | AndPattern patterns
    | ListPattern(patterns, _) -> patterns |> List.collect collectStringLiteralsPattern
    | ListSplatMiddle(beforePatterns, afterPatterns) ->
        (beforePatterns |> List.collect collectStringLiteralsPattern)
        @ (afterPatterns |> List.collect collectStringLiteralsPattern)
    | RecordPattern(_, patterns) -> patterns |> List.collect collectStringLiteralsPattern
    | RecordMemberPattern members -> members |> List.collect (fun (_, p) -> collectStringLiteralsPattern p)
    | GuardPattern value
    | RangePattern(value, _) -> collectStringLiteralsExpr value
    | TagPattern(_, None)
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []

and private collectStringLiteralsStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, rhs) -> collectStringLiteralsExpr rhs
        | ExprStatement expr -> collectStringLiteralsExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

and private collectDbgTagNamesStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, value) -> collectDbgTagNamesExpr value
        | ExprStatement value -> collectDbgTagNamesExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

let rec private collectDbgTagPayloadNamesExpr (expr: Expression) : string list =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (TagExpr(tagName, Some _))] -> [ tagName ]
        | _ -> []
    | FunctionCall(_, _, args) -> args |> List.collect collectDbgTagPayloadNamesExpr
    | BinaryOp(_, left, right) -> collectDbgTagPayloadNamesExpr left @ collectDbgTagPayloadNamesExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectDbgTagPayloadNamesExpr cond @ collectDbgTagPayloadNamesExpr thenExpr @ collectDbgTagPayloadNamesExpr elseExpr
    | Lambda(_, body) -> collectDbgTagPayloadNamesExpr body
    | Pipe(value, _, _, args) -> collectDbgTagPayloadNamesExpr value @ (args |> List.collect collectDbgTagPayloadNamesExpr)
    | Block statements -> collectDbgTagPayloadNamesStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectDbgTagPayloadNamesExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectDbgTagPayloadNamesExpr value
            | PositionalField value -> collectDbgTagPayloadNamesExpr value)
    | Match(values, arms) ->
        let valueTags = values @ Pattern.armExpressions arms |> List.collect collectDbgTagPayloadNamesExpr
        let armTags = arms |> List.collect (fun (_, armExpr) -> collectDbgTagPayloadNamesExpr armExpr)
        valueTags @ armTags
    | UseIn(_, body) -> collectDbgTagPayloadNamesExpr body
    | MemberAccess(value, _, _) -> collectDbgTagPayloadNamesExpr value
    | TagExpr(_, payload) -> payload |> Option.map collectDbgTagPayloadNamesExpr |> Option.defaultValue []
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectDbgTagPayloadNamesExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectDbgTagPayloadNamesExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectDbgTagPayloadNamesExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectDbgTagPayloadNamesStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, value) -> collectDbgTagPayloadNamesExpr value
        | ExprStatement value -> collectDbgTagPayloadNamesExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

let rec private containsRegularDbgExpr (expr: Expression) : bool =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (TagExpr(_, None))] -> false
        | _ -> true
    | FunctionCall(_, _, args) -> args |> List.exists containsRegularDbgExpr
    | BinaryOp(_, left, right) -> containsRegularDbgExpr left || containsRegularDbgExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        containsRegularDbgExpr cond || containsRegularDbgExpr thenExpr || containsRegularDbgExpr elseExpr
    | Lambda(_, body) -> containsRegularDbgExpr body
    | Pipe(value, _, _, args) -> containsRegularDbgExpr value || (args |> List.exists containsRegularDbgExpr)
    | Block statements -> containsRegularDbgStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.exists containsRegularDbgExpr
    | RecordExpr fields ->
        fields
        |> List.exists (function
            | NamedField(_, value) -> containsRegularDbgExpr value
            | PositionalField value -> containsRegularDbgExpr value)
    | Match(values, arms) ->
        (values @ Pattern.armExpressions arms |> List.exists containsRegularDbgExpr)
        || (arms |> List.exists (fun (_, armExpr) -> containsRegularDbgExpr armExpr))
    | UseIn(_, body) -> containsRegularDbgExpr body
    | MemberAccess(value, _, _) -> containsRegularDbgExpr value
    | TagExpr(_, payload) -> payload |> Option.exists containsRegularDbgExpr
    | InterpolatedString parts ->
        parts
        |> List.exists (function
            | StringText _ -> false
            | StringExpr value -> containsRegularDbgExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> containsRegularDbgExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists containsRegularDbgExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private containsDbgStringLiteralExpr (expr: Expression) : bool =
    let flattenDbgArgs args =
        match args with
        | [Kind (TupleExpr items)] -> items
        | _ -> args

    match expr.Kind with
    | FunctionCall(name, _, args) when name = "dbg" ->
        match flattenDbgArgs args with
        | [Kind (LiteralExpr(StringLit _))] -> true
        | _ -> false
    | FunctionCall(_, _, args) -> args |> List.exists containsDbgStringLiteralExpr
    | BinaryOp(_, left, right) -> containsDbgStringLiteralExpr left || containsDbgStringLiteralExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        containsDbgStringLiteralExpr cond || containsDbgStringLiteralExpr thenExpr || containsDbgStringLiteralExpr elseExpr
    | Lambda(_, body) -> containsDbgStringLiteralExpr body
    | Pipe(value, _, _, args) -> containsDbgStringLiteralExpr value || (args |> List.exists containsDbgStringLiteralExpr)
    | Block statements -> statements |> List.map _.Kind |> List.exists (function
        | DefStatement(_, _, _, rhs) -> containsDbgStringLiteralExpr rhs
        | ExprStatement expr -> containsDbgStringLiteralExpr expr
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)
    | TupleExpr values
    | ListExpr values -> values |> List.exists containsDbgStringLiteralExpr
    | RecordExpr fields ->
        fields
        |> List.exists (function
            | NamedField(_, value) -> containsDbgStringLiteralExpr value
            | PositionalField value -> containsDbgStringLiteralExpr value)
    | Match(values, arms) ->
        (values @ Pattern.armExpressions arms |> List.exists containsDbgStringLiteralExpr)
        || (arms |> List.exists (fun (_, armExpr) -> containsDbgStringLiteralExpr armExpr))
    | UseIn(_, body) -> containsDbgStringLiteralExpr body
    | MemberAccess(value, _, _) -> containsDbgStringLiteralExpr value
    | TagExpr(_, payload) -> payload |> Option.exists containsDbgStringLiteralExpr
    | InterpolatedString parts ->
        parts
        |> List.exists (function
            | StringText _ -> false
            | StringExpr inner -> containsDbgStringLiteralExpr inner)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> containsDbgStringLiteralExpr value
    | ContextMemberAccess(_, _) -> false
    | ContextMemberCall(_, _, args) -> args |> List.exists containsDbgStringLiteralExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> false

and private containsRegularDbgStatements (statements: Statement list) : bool =
    statements |> List.map _.Kind
    |> List.exists (function
        | DefStatement(_, _, _, value) -> containsRegularDbgExpr value
        | ExprStatement value -> containsRegularDbgExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> false)

let rec private collectTagNamesExpr (expr: Expression) : string list =
    match expr.Kind with
    | TagExpr(name, payload) ->
        match payload with
        | Some value -> name :: collectTagNamesExpr value
        | None -> [ name ]
    | BinaryOp(_, left, right) -> collectTagNamesExpr left @ collectTagNamesExpr right
    | IfExpr(cond, thenExpr, elseExpr) ->
        collectTagNamesExpr cond @ collectTagNamesExpr thenExpr @ collectTagNamesExpr elseExpr
    | FunctionCall(_, _, args) -> args |> List.collect collectTagNamesExpr
    | Lambda(_, body) -> collectTagNamesExpr body
    | Pipe(value, _, _, args) -> collectTagNamesExpr value @ (args |> List.collect collectTagNamesExpr)
    | Block statements -> collectTagNamesStatements statements
    | TupleExpr values
    | ListExpr values -> values |> List.collect collectTagNamesExpr
    | RecordExpr fields ->
        fields
        |> List.collect (function
            | NamedField(_, value) -> collectTagNamesExpr value
            | PositionalField value -> collectTagNamesExpr value)
    | Match(values, arms) ->
        let valueTags = values |> List.collect collectTagNamesExpr
        let armTags =
            arms
            |> List.collect (fun (patterns, armExpr) ->
                (patterns |> List.collect collectTagNamesPattern) @ (collectTagNamesExpr armExpr))
        valueTags @ armTags
    | UseIn(_, body) -> collectTagNamesExpr body
    | MemberAccess(value, _, _) -> collectTagNamesExpr value
    | InterpolatedString parts ->
        parts
        |> List.collect (function
            | StringText _ -> []
            | StringExpr value -> collectTagNamesExpr value)
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> collectTagNamesExpr value
    | ContextMemberAccess(_, _) -> []
    | ContextMemberCall(_, _, args) -> args |> List.collect collectTagNamesExpr
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

and private collectTagNamesStatements (statements: Statement list) : string list =
    statements |> List.map _.Kind
    |> List.collect (function
        | DefStatement(_, _, _, value) -> collectTagNamesExpr value
        | ExprStatement value -> collectTagNamesExpr value
        | ImportStatement _
        | TypeDefStatement _
        | UseStatement _ -> [])

and private collectTagNamesPattern (pattern: Pattern) : string list =
    match pattern.Kind with
    | TagPattern(tagName, payloadOpt) ->
        match payloadOpt with
        | Some payload -> tagName :: collectTagNamesPattern payload
        | None -> [ tagName ]
    | TuplePattern patterns
    | AndPattern patterns
    | ListPattern(patterns, _) -> patterns |> List.collect collectTagNamesPattern
    | ListSplatMiddle(beforePatterns, afterPatterns) ->
        (beforePatterns |> List.collect collectTagNamesPattern)
        @ (afterPatterns |> List.collect collectTagNamesPattern)
    | RecordPattern(_, patterns) -> patterns |> List.collect collectTagNamesPattern
    | RecordMemberPattern members -> members |> List.collect (fun (_, p) -> collectTagNamesPattern p)
    | GuardPattern value
    | RangePattern(value, _) -> collectTagNamesExpr value
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []

/// Where a call to `name`, the call at node `callId`, finds each member of the contexts the callee requires, in the
/// order it requires them, given the keys of the members in scope there (`scopeKeys`). Each is found where the type
/// checker found its context: an instance in scope (a `use`, or a context the caller's type declares), or one built
/// from attached definitions. One the type checker left to be found by name is the member of that key in scope, or a
/// compatible one (the same context and member, at other type arguments), or else the ambient instance's member.
let private contextArguments
    (requirements: Map<string, string list>)
    (contextTypes: Map<string, TypeExpr list>)
    (typeLayouts: Map<string, string list>)
    (contextInstances: Map<NodeId, ContextInstance option list>)
    (functionWrappers: Map<string, string>)
    (ambientMembers: Map<string, string>)
    (scopeKeys: Set<string>)
    (name: string)
    (callId: NodeId)
    : CoreIrLower.ContextArgument list =
    let required = requirements |> Map.tryFind name |> Option.defaultValue []

    // A member of the same context at other type arguments, not already passed: a required `ToString_a_toString`
    // can be an available `ToString_int_toString`.
    let findCompatible (used: Set<string>) (requiredKey: string) : string option =
        let parts = requiredKey.Split('_')
        if parts.Length < 2 then None
        else
            let baseName = parts.[0]
            let memberName = parts.[parts.Length - 1]
            scopeKeys
            |> Seq.tryFind (fun availableKey ->
                not (used.Contains availableKey) &&
                let availableParts = availableKey.Split('_')
                availableParts.Length >= 2 &&
                availableParts.[0] = baseName &&
                availableParts.[availableParts.Length - 1] = memberName)

    // The member in scope, else the ambient instance's.
    let memberFor (requiredKey: string) (resolvedKey: string) =
        if scopeKeys.Contains resolvedKey then CoreIrLower.MemberInScope resolvedKey
        else
            match ambientMembers |> Map.tryFind resolvedKey with
            | Some wrapper -> CoreIrLower.ClosureOverWrapper wrapper
            | None ->
                let available = (List.ofSeq scopeKeys) @ (ambientMembers |> Map.toList |> List.map fst) |> String.concat ", "
                failwith $"Context function {requiredKey} not available in current scope. Available: [{available}]"

    // Where the type checker found each context the callee requires, as each required member's instance, in the order
    // of `required`: none for a call it found nothing for.
    let instances =
        match contextInstances |> Map.tryFind callId, contextTypes |> Map.tryFind name with
        | Some found, Some types when found.Length = types.Length ->
            List.zip types found
            |> List.collect (fun (contextType, instance) ->
                let baseName = match contextType.Kind with TypeName n | TypeApply(n, _) -> n | _ -> "unknown"
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue [] |> List.map (fun memberName -> memberName, instance))
            |> fun (perMember: (string * ContextInstance option) list) -> if perMember.Length = required.Length then Some perMember else None
        | _ -> None

    required
    |> List.mapi (fun index requiredKey -> requiredKey, instances |> Option.map (fun perMember -> perMember.[index]))
    |> List.fold (fun (acc: CoreIrLower.ContextArgument list, used: Set<string>) (requiredKey, instance) ->
        match instance with
        // An instance in scope, as written where it is: the members of a `use` or of the context a function's type
        // declares.
        | Some (memberName, Some (InstanceInScope written)) ->
            let key = contextMemberKey (Some written) memberName
            acc @ [ memberFor requiredKey key ], used.Add key
        // An instance built from the attached definitions: each member is one of them.
        | Some (memberName, Some (InstanceSynthesized members)) ->
            let definition = members |> List.find (fun (n, _) -> n = memberName) |> snd
            let argument =
                match functionWrappers |> Map.tryFind definition with
                | Some wrapper -> CoreIrLower.ClosureOverWrapper wrapper
                | None -> CoreIrLower.DefinitionValue definition
            acc @ [ argument ], used
        | _ ->
            let resolvedKey =
                if scopeKeys.Contains requiredKey then requiredKey
                else findCompatible used requiredKey |> Option.defaultValue requiredKey
            acc @ [ memberFor requiredKey resolvedKey ], used.Add resolvedKey) ([], Set.empty)
    |> fst

let private extractTypeLayouts (module': Module) : Map<string, string list> * Map<string, Map<string, string>> =
    let rec typeExprToTypeString (t: TypeExpr) : string =
        match t.Kind with
        | TypeName name -> name
        | TypeFunc(param, ret) -> $"({typeExprToTypeString param}) -> ({typeExprToTypeString ret})"
        | TypeUnit -> "()"
        | TypeTuple items -> items |> List.map typeExprToTypeString |> String.concat ", " |> sprintf "(%s)"
        | _ -> "unknown"
    let results =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, Kind (TypeRecord fields)))) ->
                let fieldInfo =
                    fields
                    |> List.choose (function
                        | TypeField(fieldName, _, _, typeOpt, _) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName)
                        | TypeMember(fieldName, typeOpt) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName))
                let sortedFieldNames = fieldInfo |> List.map fst |> List.sort
                let fieldTypeMap = fieldInfo |> Map.ofList
                Some (name, sortedFieldNames, fieldTypeMap)
            | _ -> None)
    let layouts = results |> List.map (fun (n, fields, _) -> (n, fields)) |> Map.ofList
    let fieldTypes = results |> List.map (fun (n, _, types) -> (n, types)) |> Map.ofList
    layouts, fieldTypes

/// The functions a module was compiled into, from the core IR (see "Core IR" in `compiler/design/memory.md`).
type BackendReport =
    { /// The functions, by their names in WebAssembly text, specialisations and the start function included.
      Functions: string list
      /// The specialisations among them, each with the definition it specialises.
      Specialisations: (string * string) list }

/// `typed` as WebAssembly text, and the functions it was compiled into.
let transpileTypedModuleWithReport (typed: TypedModule) : string * BackendReport =
    let contextInstances = typed.ContextInstances
    let checkedTypes = typed.ExprTypes
    let module' = typed.Module
    // The definitions synthesized instances are made of are passed as function values.
    let synthesizedMembers =
        contextInstances
        |> Map.values
        |> Seq.collect (List.choose (function Some (InstanceSynthesized members) -> Some members | _ -> None) >> List.concat)
        |> Seq.map snd
        |> Set.ofSeq
    let module', liftedOrigins, functionWrappers = LambdaLifting.lift synthesizedMembers module'
    let defs =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, typeOpt, expr))) -> Some(name, typeOpt, expr)
            | _ -> None)

    let topLevelExprs =
        module'
        |> List.choose (function
            | Kind (Expr expr) -> Some expr
            | _ -> None)

    let typeLayouts, typeFieldTypes = extractTypeLayouts module'

    let nominalWrappers =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, body))) when isNominalTypeName name && (match body.Kind with TypeRecord _ -> false | _ -> true) -> Some name
            | _ -> None)
        |> Set.ofList

    // Calculate context requirements for each function based on TypeWithContext annotations
    // For parameterized contexts like ToString(a), generate qualified keys like "ToString_a_toString"
    // For simple contexts like Println, keep simple member names like "println"
    let functionContextTypes =
        defs
        |> List.choose (fun (name, typeOpt, _) ->
            match extractContextTypes typeOpt with
            | [] -> None
            | contextTypes -> Some (name, contextTypes))
        |> Map.ofList

    let functionContextRequirements =
        defs
        |> List.choose (fun (name, typeOpt, _) ->
            let contextTypes = extractContextTypes typeOpt
            if contextTypes.IsEmpty then None
            else
                // For each context type, get its field names and generate appropriate keys
                let contextFnNames =
                    contextTypes
                    |> List.collect (fun ctxType ->
                        let baseName = match ctxType.Kind with
                                       | TypeName n -> n
                                       | TypeApply(n, _) -> n
                                       | _ -> "unknown"
                        let needsQualified = match ctxType.Kind with
                                             | TypeApply _ -> true  // Parameterized - need qualified
                                             | _ -> false           // Simple - keep simple names
                        typeLayouts |> Map.tryFind baseName 
                        |> Option.defaultValue []
                        |> List.map (fun memberName ->
                            if needsQualified then
                                let ctxKey = typeExprToKey ctxType
                                $"{ctxKey}_{memberName}"
                            else
                                memberName))
                if contextFnNames.IsEmpty then None
                else Some (name, contextFnNames))
        |> Map.ofList

    // The ambient context instance: a module-level function named like a member of a declared context stands
    // in for that member whenever a callee requires the context and the caller has nothing else to supply.
    // This is how the runtime-provided `Console` (the functions `nanyx/console` exports) reaches `@entry`.
    // Closure conversion made a wrapper for each such function, so the member is a closure over it.
    let ambientContextMembers =
        let contextMemberNames =
            module'
            |> List.collect (function
                | Kind (Def (TypeDef(_, modifiers, _, Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
                    fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
                | _ -> [])
        if functionContextRequirements.IsEmpty then Map.empty
        else
            contextMemberNames
            |> List.filter (fun memberName -> not (functionContextRequirements.ContainsKey memberName))
            |> List.choose (fun memberName -> functionWrappers |> Map.tryFind memberName |> Option.map (fun wrapper -> memberName, wrapper))
            |> Map.ofList

    let functionTable: FunctionTable =
        { Values = System.Collections.Generic.Dictionary<string, ContextFn>()
          NextSlot = 0
          IndirectArities = System.Collections.Generic.HashSet<int>() }

    let recordTable: RecordTable =
        { LayoutIds = System.Collections.Generic.Dictionary<string list, int>() }

    let usedHelpers = System.Collections.Generic.HashSet<string>()

    let moduleUsesDbg =
        (defs |> List.exists (fun (_, _, expr) -> containsDbgCallExpr expr)) ||
        (topLevelExprs |> List.exists containsDbgCallExpr)
    let moduleUsesRegularDbg =
        (defs |> List.exists (fun (_, _, expr) -> containsRegularDbgExpr expr)) ||
        (topLevelExprs |> List.exists containsRegularDbgExpr)
    let moduleUsesDbgString = 
        (defs |> List.exists (fun (_, _, expr) -> containsDbgStringLiteralExpr expr)) ||
        (topLevelExprs |> List.exists containsDbgStringLiteralExpr)
    let moduleUsesHeap = 
        (defs |> List.exists (fun (_, _, expr) -> usesHeapAllocationExpr expr)) ||
        (topLevelExprs |> List.exists usesHeapAllocationExpr)
    let dbgTagNames =
        (defs |> List.collect (fun (_, _, expr) -> collectDbgTagNamesExpr expr)) @
         (topLevelExprs |> List.collect collectDbgTagNamesExpr)
        |> List.distinct
        |> List.sort
    let dbgTagPayloadNames =
        (defs |> List.collect (fun (_, _, expr) -> collectDbgTagPayloadNamesExpr expr)) @
         (topLevelExprs |> List.collect collectDbgTagPayloadNamesExpr)
        |> List.distinct
        |> List.sort
    let tagIdMap =
        (defs |> List.collect (fun (_, _, expr) -> collectTagNamesExpr expr)) @
         (topLevelExprs |> List.collect collectTagNamesExpr)
        |> List.distinct
        |> List.sort
        |> List.mapi (fun index name -> name, index + 1)
        |> Map.ofList

    let stringLiterals =
        (defs |> List.collect (fun (_, _, expr) -> collectStringLiteralsExpr expr)) @
         (topLevelExprs |> List.collect collectStringLiteralsExpr) @
         // What each hole or matched value of no known type writes, if a debug build reaches it.
         ((defs |> List.map (fun (_, _, expr) -> expr)) @ topLevelExprs
          |> List.collect (fun expr ->
              (errorTypedHoles checkedTypes expr |> List.map (fun hole -> Interpolation.unknownHoleMessage hole.Span))
              @ (errorTypedScrutinees checkedTypes expr |> List.map (fun value -> Interpolation.unknownScrutineeMessage value.Span))))
        |> List.distinct

    let stringLiteralMap, stringLiteralData = buildStringLiteralMap stringLiterals

    /// The other names a function can call the members of the contexts it requires by, each with the key it stands
    /// for: a member of a parameterised context (`Show_a_show`) by its own name (`show`), as the type checker allows,
    /// when no other context the function declares has a member of that name.
    let contextMemberAliases (name: string) : (string * string) list =
        let keys = functionContextRequirements |> Map.tryFind name |> Option.defaultValue []
        functionContextTypes
        |> Map.tryFind name
        |> Option.defaultValue []
        |> List.collect (fun ctxType ->
            match ctxType.Kind with
            | TypeApply(baseName, _) ->
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue []
                |> List.map (fun memberName -> memberName, contextMemberKey (Some ctxType) memberName)
            | _ -> [])
        |> List.groupBy fst
        |> List.choose (fun (memberName, aliased) ->
            match aliased with
            | [ (_, key) ] when List.contains key keys && not (List.contains memberName keys) -> Some (memberName, key)
            | _ -> None)

    /// The members of the contexts `name` requires, as the core IR takes them: each with its type, from the context's
    /// definition with its parameters as the function's annotation gives them, where that can be worked out.
    let coreIrContextParameters (typeDefs: Map<string, TypeDefInfo>) (definitionTypes: Map<string, Ty>) (name: string) =
        // The function's own type variables, by the names its annotation gives them.
        let rec namedVars (ty: Ty) : (string * Ty) list =
            match ty with
            | TyVar { Name = Some varName } -> [ varName, ty ]
            | TyVar _ | TyPrimitive _ -> []
            | TyNominal(_, underlying, _) -> namedVars underlying
            | TyFunc(input, output) -> namedVars input @ namedVars output
            | TyRecord fields -> fields |> Map.toList |> List.collect (snd >> namedVars)
            | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> namedVars)
            | TyTag(_, payload) -> payload |> Option.map namedVars |> Option.defaultValue []
            | TyApply(_, args) -> args |> List.collect namedVars
            | TyUnion items -> items |> List.collect namedVars
            | TyAlias(_, args, underlying) -> (args |> List.collect namedVars) @ namedVars underlying
        // The type variables the annotation writes, each matched to the one in the same place in the definition's
        // type: inference may have merged a written variable with another and dropped its name.
        let rec matchAnnotation (annotation: TypeExpr) (ty: Ty) : (string * Ty) list =
            match annotation.Kind, Unifier.unalias ty with
            | (TypeName varName | TypeVar varName), TyVar _ -> [ varName, ty ]
            | TypeFunc(input, output), TyFunc(input', output') -> matchAnnotation input input' @ matchAnnotation output output'
            | TypeTuple items, TyRecord fields ->
                items |> List.mapi (fun index item -> fields |> Map.tryFind (string index) |> Option.map (matchAnnotation item) |> Option.defaultValue []) |> List.concat
            | TypeApply(applied, items), TyApply(applied', items') when applied = applied' && items.Length = items'.Length ->
                List.map2 matchAnnotation items items' |> List.concat
            | _ -> []
        let annotated =
            match defs |> List.tryFind (fun (defName, _, _) -> defName = name), definitionTypes |> Map.tryFind name with
            | Some (_, typeOpt, _), Some ty ->
                match unwrapContextType typeOpt with
                | Some annotation -> matchAnnotation annotation ty
                | None -> []
            | _ -> []
        let vars =
            (definitionTypes |> Map.tryFind name |> Option.map namedVars |> Option.defaultValue []) @ annotated |> Map.ofList
        let argumentTy (argument: TypeExpr) =
            match argument.Kind with
            | TypeName ("int" | "string" | "bool" | "unit" | "float" as primitive) -> Some (TyPrimitive primitive)
            | TypeName varName
            | TypeVar varName -> vars |> Map.tryFind varName
            | _ -> None
        let memberTypes =
            functionContextTypes
            |> Map.tryFind name
            |> Option.defaultValue []
            |> List.collect (fun ctxType ->
                let baseName, arguments =
                    match ctxType.Kind with
                    | TypeApply(baseName, arguments) -> baseName, arguments
                    | TypeName baseName -> baseName, []
                    | _ -> "unknown", []
                let memberTy (memberName: string) =
                    match typeDefs |> Map.tryFind baseName with
                    | Some info when info.Parameters.Length = arguments.Length ->
                        let argumentTys = arguments |> List.map argumentTy
                        if argumentTys |> List.forall Option.isSome then
                            let parameters = List.zip (info.Parameters |> List.map _.Id) (argumentTys |> List.map Option.get) |> Map.ofList
                            let rec bind (ty: Ty) =
                                match ty with
                                | TyVar v when parameters.ContainsKey v.Id -> parameters.[v.Id]
                                | TyFunc(input, output) -> TyFunc(bind input, bind output)
                                | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> bind))
                                | TyTag(tag, payload) -> TyTag(tag, payload |> Option.map bind)
                                | TyApply(applied, args) -> TyApply(applied, args |> List.map bind)
                                | TyUnion items -> TyUnion(items |> List.map bind)
                                | TyAlias(alias, args, underlying) -> TyAlias(alias, args |> List.map bind, bind underlying)
                                | other -> other
                            match Unifier.unalias info.Underlying with
                            | TyRecord members -> members |> Map.tryFind memberName |> Option.map bind
                            | _ -> None
                        else None
                    | _ -> None
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue []
                |> List.map (fun memberName -> contextMemberKey (Some ctxType) memberName, memberTy memberName))
            |> Map.ofList
        let parameters =
            functionContextRequirements.[name]
            |> List.map (fun key ->
                { CoreIrLower.ContextParameter.Key = key
                  CoreIrLower.ContextParameter.Name = $"__ctx_{sanitizeIdentifier key}"
                  CoreIrLower.ContextParameter.Type = memberTypes |> Map.tryFind key |> Option.flatten })
        parameters, contextMemberAliases name |> Map.ofList

    let coreIrContext (layoutIds: string list -> int) (functionSlots: string -> int -> int) (indirectCalls: int -> unit) : CoreIrEmit.EmitContext =
        { TagId =
            fun name ->
                match tagIdMap |> Map.tryFind name with
                | Some id -> id
                | None -> failwith $"Internal error: missing tag discriminant for #{name}"
          StringOffset =
            fun value ->
                match stringLiteralMap |> Map.tryFind value with
                | Some offset -> offset
                | None -> failwith $"Internal error: missing string literal offset for {value}"
          RecordLayoutId = layoutIds
          FunctionSlot = functionSlots
          IndirectCall = indirectCalls }

    // A specialisation is only called by the functions compiled with it; a definition that is not generic is exported.
    let emitFromCoreIr (context: CoreIrEmit.EmitContext) (func: CoreIr.Function) =
        let exportName = if func.TypeArguments.IsEmpty then Some func.Source else None
        CoreIrEmit.emitFunction context func exportName

    // Every definition that is not generic, the start function, and the specialisations and lifted functions they
    // need. Each function is emitted once here only to find out whether it can be (layouts get numbers of no
    // consequence), and again below, in order, for real.
    let lowered =
        let input: CoreIrLower.LowerInput =
            { Definitions = defs |> List.map (fun (name, _, expr) -> name, expr)
              ExprTypes = typed.InlinedExprTypes |> Map.fold (fun acc id ty -> acc |> Map.add id ty) checkedTypes
              CalleeTypes = typed.InlinedCalleeTypes |> Map.fold (fun acc id ty -> acc |> Map.add id ty) typed.CalleeTypes
              DefinitionTypes = typed.Types
              ContextParameters =
                functionContextRequirements
                |> Map.map (fun name _ -> coreIrContextParameters typed.TypeDefs typed.Types name)
              ContextArguments =
                fun name callId scopeKeys ->
                    try
                        Ok (contextArguments
                                functionContextRequirements functionContextTypes typeLayouts contextInstances functionWrappers
                                ambientContextMembers scopeKeys name callId)
                    with Failure message -> Error message
              RecordConstructors = typeLayouts.Keys |> Set.ofSeq
              NominalWrappers = nominalWrappers
              Lifted =
                [ yield! liftedOrigins |> Map.toList |> List.map (fun (name, origin) -> name, CoreIrLower.LiftedLambda(origin.Lambda, origin.Captured))
                  yield! functionWrappers |> Map.toList |> List.map (fun (wrapped, wrapper) -> wrapper, CoreIrLower.FunctionWrapper wrapped) ]
                |> Map.ofList
              TopLevel = topLevelExprs
              NamedTypes =
                typed.TypeDefs
                |> Map.filter (fun _ info -> not info.IsContext)
                |> Map.map (fun name info -> TyNominal(name, info.Underlying, info.IsPrivate)) }
        CoreIrLower.lowerModule input (emitFromCoreIr (coreIrContext (fun _ -> 0) (fun _ _ -> 0) ignore) >> ignore)

    // The first thing that cannot be compiled is reported where it is written.
    match lowered.Failures with
    | (_, reason, span) :: _ ->
        let message =
            if reason.StartsWith "internal error: " then $"Internal error: {reason.Substring 16}"
            else $"The WebAssembly backend cannot compile {reason} yet"
        match span with
        | Some span -> unsupportedAt span message
        | None -> failwith message
    | [] -> ()

    /// The text of a function from the core IR, noting the helpers it calls and the record layouts it allocates.
    let fromCoreIr (func: CoreIr.Function) =
        let emitted =
            emitFromCoreIr
                (coreIrContext (recordLayoutId recordTable) (functionValueSlot functionTable) (functionTable.IndirectArities.Add >> ignore))
                func
        for helper in emitted.Helpers do
            usedHelpers.Add helper |> ignore
        emitted.Text

    // Each definition that is not generic, followed by the specialisations of it: a generic definition has only those.
    let definitionFunctions =
        defs
        |> List.collect (fun (name, _, _) ->
            [ yield! lowered.Roots |> Map.tryFind name |> Option.map fst |> Option.toList
              yield! lowered.Specialisations |> Map.tryFind name |> Option.defaultValue [] |> List.map fst ])

    let renderedFunctions = definitionFunctions |> List.map fromCoreIr |> String.concat "\n\n"

    let startFunction = lowered.Roots |> Map.tryFind CoreIrLower.startFunctionName |> Option.map fst

    let renderedStartFunction, startDirective =
        match startFunction with
        | Some func ->
            let emitted =
                CoreIrEmit.emitStart
                    (coreIrContext (recordLayoutId recordTable) (functionValueSlot functionTable) (functionTable.IndirectArities.Add >> ignore))
                    func
            for helper in emitted.Helpers do
                usedHelpers.Add helper |> ignore
            emitted.Text, "  (start $__nanyx_start)"
        | None -> "", ""

    let report =
        { Functions = [ yield! definitionFunctions |> List.map _.Name; yield! startFunction |> Option.map _.Name |> Option.toList ]
          Specialisations =
            defs
            |> List.collect (fun (name, _, _) ->
                lowered.Specialisations |> Map.tryFind name |> Option.defaultValue [] |> List.map (fun (func, _) -> func.Name, sanitizeIdentifier name)) }

    let moduleUsesDbgHelpers = moduleUsesRegularDbg || moduleUsesDbgString

    let usedHelperSet = closeHelpers Set.empty (usedHelpers |> Seq.toList)
    let moduleUsesStandardStreams =
        [ Helpers.writeStdout; Helpers.writeStderr; Helpers.readStdinByte ] |> List.exists usedHelperSet.Contains
    let moduleReadsStdin = usedHelperSet.Contains Helpers.readStdinByte
    // Anything that writes to stdout or stderr, or reads stdin, shares the WASI import, the memory and the
    // newline byte.
    let moduleUsesIoHelpers = moduleUsesDbgHelpers || moduleUsesStandardStreams
    let moduleUsesHeap = moduleUsesHeap || usedHelperSet.Contains Helpers.alloc
    let renderedHelpers =
        helperDependencies
        |> List.map fst
        |> List.filter usedHelperSet.Contains
        |> List.map (fun name -> runtimeHelpers |> Map.find name)
        |> String.concat "\n"

    let dbgPrelude =
        if moduleUsesIoHelpers then
            [ "  (import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write (param i32 i32 i32 i32) (result i32)))"
              if moduleReadsStdin then
                  "  (import \"wasi_snapshot_preview1\" \"fd_read\" (func $fd_read (param i32 i32 i32 i32) (result i32)))"
              if usedHelperSet.Contains Helpers.procExit then
                  "  (import \"wasi_snapshot_preview1\" \"proc_exit\" (func $proc_exit (param i32)))"
              "  (memory 1)"
              "  (export \"memory\" (memory 0))"
              if moduleUsesDbgHelpers then
                  $"  (data (i32.const {dbgDataBaseOffset}) \"dbg: \")"
                  $"  (data (i32.const {dbgStrNewlineOffset}) \"\\0a\")"
                  yield! dbgFunctions ]
            |> String.concat "\n"
        else
            ""

    // Every function body has been rendered by now, so the function-value slots are final.
    let contextFnList = functionTable.Values.Values |> Seq.toList

    let contextTypeDefs =
        (contextFnList |> List.map ctxFnArity) @ (functionTable.IndirectArities |> Seq.toList)
        |> Set.ofList
        |> Set.toList
        |> List.sort
        |> List.map (fun arity ->
            let paramSig =
                [ for _ in 1 .. arity -> "i32" ]
                |> String.concat " "
            if String.IsNullOrWhiteSpace paramSig then
                $"  (type $ctx_fn_{arity} (func (result i32)))"
            else
                $"  (type $ctx_fn_{arity} (func (param {paramSig}) (result i32)))")
        |> String.concat "\n"

    // A `call_indirect` needs a table to validate, even in a module that never makes a closure to put in it
    // (a function that only calls the closures it is given).
    let contextTable =
        if contextFnList.IsEmpty && Seq.isEmpty functionTable.IndirectArities then
            ""
        else
            $"  (table $ctx_table {contextFnList.Length} funcref)"

    let contextElem =
        if contextFnList.IsEmpty then
            ""
        else
            let ordered = contextFnList |> List.sortBy ctxFnSlot
            let elems = ordered |> List.map (fun ctxFn -> $"${ctxFnName ctxFn}") |> String.concat " "
            $"  (elem (i32.const 0) {elems})"

    let contextPrelude =
        [ contextTypeDefs; contextTable; contextElem ]
        |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> String.concat "\n"

    let dbgTagImports =
        dbgTagNames
        |> List.map (fun tagName ->
            let importName = $"dbg_tag_{sanitizeImportSuffix tagName}"
            $"  (import \"env\" \"{importName}\" (func ${importName}))")

    let dbgTagPayloadImports =
        dbgTagPayloadNames
        |> List.map (fun tagName ->
            let importName = $"dbg_tag_payload_{sanitizeImportSuffix tagName}"
            $"  (import \"env\" \"{importName}\" (func ${importName} (param i32)))")

    let dataSegments =
        stringLiteralData
        |> List.map (fun (_, offset, bytes) ->
            $"  (data (i32.const {offset}) \"{escapeWasmData bytes}\")")
        |> String.concat "\n"

    // Add memory if we have string literals, heap allocation or reads of tag cells, but no dbg helpers (dbgPrelude
    // already includes memory)
    let readsTagCells = usedHelperSet.Contains Helpers.tagCode || usedHelperSet.Contains Helpers.tagEquals
    let needsMemory = (not (List.isEmpty stringLiteralData) || moduleUsesHeap || readsTagCells) && not moduleUsesIoHelpers
    let memorySection =
        if needsMemory then
            "  (memory 1)\n  (export \"memory\" (memory 0))"
        else
            ""

    // Add heap global if we use heap allocation
    let heapSection =
        if moduleUsesHeap then
            $"  (global $heap_ptr (mut i32) (i32.const {heapStartOffset}))"
        else
            ""

    // Imports must precede every function definition in a WebAssembly module, so the host imports for
    // `dbg(#tag)` come before the prelude (which defines $dbg and friends).
    let allImports =
        [ yield! dbgTagImports
          yield! dbgTagPayloadImports
          if not (String.IsNullOrWhiteSpace dbgPrelude) then dbgPrelude
          if not (String.IsNullOrWhiteSpace memorySection) then memorySection
          if not (String.IsNullOrWhiteSpace heapSection) then heapSection
          if not (String.IsNullOrWhiteSpace renderedHelpers) then renderedHelpers
          if not (String.IsNullOrWhiteSpace contextPrelude) then contextPrelude ]
        |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> String.concat "\n\n"

    if String.IsNullOrWhiteSpace renderedFunctions && String.IsNullOrWhiteSpace allImports && String.IsNullOrWhiteSpace dataSegments then
        "(module)", report
    else
        let parts =
            [ if not (String.IsNullOrWhiteSpace allImports) then allImports
              if not (String.IsNullOrWhiteSpace dataSegments) then dataSegments
              if not (String.IsNullOrWhiteSpace renderedFunctions) then renderedFunctions
              if not (String.IsNullOrWhiteSpace renderedStartFunction) then renderedStartFunction
              if not (String.IsNullOrWhiteSpace startDirective) then startDirective ]
        let body = String.concat "\n\n" parts
        $"(module\n{body}\n)", report

let transpileTypedModuleToWat (typed: TypedModule) : string =
    transpileTypedModuleWithReport typed |> fst

/// `transpileTypedModuleToWat`, with what the backend cannot compile reported as a diagnostic at the place in the source
/// it is written: the innermost expression it is in that has one, or else the definition.
let tryTranspileTypedModuleToWat (typed: TypedModule) : Result<string, Diagnostic> =
    try
        Ok (transpileTypedModuleToWat typed)
    with
    | UnsupportedConstruct(message, span) -> Error (Diagnostics.at DiagnosticCodes.UnsupportedByBackend span message)
    | Failure message -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend message)
    | ex -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend $"Internal error: {ex.Message}")
