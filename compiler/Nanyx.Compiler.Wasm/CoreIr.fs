/// The core IR: the program between the typed AST and WebAssembly text (see "Core IR" in
/// `compiler/design/memory.md`). It is in A-normal form, so every intermediate value has a name and a place, and every
/// node has a concrete type: generic functions are specialised for each set of type arguments they are used at as the
/// IR is built (`CoreIrLower.fs`), and code is generated from it (`CoreIrEmit.fs`).
module Transpiler.Wasm.CoreIr

/// A concrete type. There are no type variables: a generic function is only ever lowered at concrete types.
type IrType =
    | IrInt
    | IrBool
    | IrUnit
    | IrString
    /// A float, in a box of its own: the value is a pointer to the `f64`.
    | IrFloat
    /// A value that is one of these tags, each with the type of its payload if it has one, sorted by name. A single
    /// tag's type is a union of one.
    | IrTags of (string * IrType option) list
    /// A record, its fields in the order they are stored: sorted by name. A tuple is a record whose fields are its
    /// positions, and a nominal type is the type of its shape.
    | IrRecord of (string * IrType) list
    /// A list of values of this type: 0 when empty, else a pointer to a cell of (head, tail).
    | IrList of IrType
    /// A function value: a closure block holding the function's table slot and what it captures. The input is a
    /// record of the parameters when there are several, as a function's type has it.
    | IrFunction of IrType * IrType

/// What a variable is, which decides how it is stored. The lowering names every variable as it is to be written in
/// WebAssembly text; variables of the same kind and name share a local.
type VarKind =
    | Param
    /// A local the program declares (a `def` in a block).
    | Local
    /// A `match`'s scrutinee, held while its arms test it. Matches that are never in progress at once (siblings, or
    /// one in another's scrutinee) share these, numbered by how many scrutinees enclose them.
    | MatchTemp
    /// A name a pattern binds inside the value it takes apart, such as `v` in `#some(v)`. Sibling arms share these.
    | PatternBound
    /// An intermediate value that the source does not name: an argument, an operand, a payload. It is used exactly
    /// once, by the next instruction to need it, so it never needs a local of its own: its value stays on the stack.
    | Temp
    /// The local a hole of an interpolated string is evaluated into, named by how deeply the string is nested in
    /// other strings' holes and by its place among the string's holes (see `Interpolation.holeLocalName`).
    | InterpolationHole
    /// A local some construct needs for its own use, such as the one `dbg` holds its value in.
    | Scratch
    /// A local of a `for` loop: where it is in its list, where its range ends, or its element. Loops that are never in
    /// progress at once share these, numbered by how many loops enclose them.
    | ForLoop

type Var =
    { Id: int
      Name: string
      Kind: VarKind
      Type: IrType }

/// A value that needs no computing: a variable or a constant.
type Atom =
    | AVar of Var
    | AInt of int64
    | ABool of bool
    | AUnit
    /// A string literal, which lives in the data segment.
    | AString of string
    /// A tag without a payload, which is its code.
    | ATag of string

type PrimOp =
    /// An operator on ints or booleans: `+`, `==`, `and`, ...
    | Binary of string
    /// An operator on floats: each operand is unboxed, and an arithmetic result boxed.
    | FloatBinary of string
    | Not

type Pattern =
    | PWildcard
    | PInt of int64
    | PBool of bool
    /// A tag, and the pattern its payload must match if it has one.
    | PTag of string * Pattern option
    /// A record's fields, by the slot each is stored in, and the pattern each must match.
    | PFields of (int * Pattern) list
    /// A string equal to this literal.
    | PString of string
    /// A value that compares to this one by the operator: `| > 5 ->`.
    | PGuard of string * Atom
    /// A list whose first elements match these patterns, and the rest of which matches the last pattern if there is
    /// one, or is empty if not.
    | PList of Pattern list * Pattern option
    /// Binds the value matched here to a variable.
    | PBind of Var

/// How a hole of an interpolated string is printed.
type HoleType =
    | HoleString
    | HoleInt
    | HoleBool
    /// A float, formatted to a string when the hole is evaluated.
    | HoleFloat
    /// A tag: each case the hole's type names, in the order the type names them, with the type of its payload if it
    /// has one (a string, an int or a bool).
    | HoleTag of (string * IrType option) list

type InterpolationPart =
    | Text of string
    /// A hole, already evaluated into its local.
    | Hole of Var * HoleType

/// How `dbg` prints its value, which it then leaves. Each holds the value in a scratch local meanwhile.
type DbgForm =
    /// An int or a bool, printed as a number. `Some depth` when the value is not a literal: the old path then
    /// declares the locals it would print a tag through (an interpolated string at that depth), whatever the type.
    | DbgNumber of Var * int option
    /// A string, printed as it is. `Some depth` as for a number.
    | DbgString of Var * int option
    /// A float, formatted and printed. `Some depth` as for a number.
    | DbgFloat of Var * int option
    /// A tag literal without a payload, printed by a host function of its own.
    | DbgTag of string * Var
    /// A tag literal with a payload (the atom), printed by a host function of its own.
    | DbgTagPayload of string * Var
    /// A value of a union of tags, printed as `"{value}"` would print it: the hole is already evaluated.
    | DbgTagValue of Var * HoleType * Var

/// Where a function value that is called comes from. The old path evaluates it twice (once to pass it, once to read
/// its slot), so it is something that can be: a variable, or a top-level value.
type ClosureRef =
    | ClosureVar of Var
    | ClosureValue of string

type Expr =
    { Kind: ExprKind
      Type: IrType }

and ExprKind =
    | Atom of Atom
    | Prim of PrimOp * Atom list
    /// A call to a top-level function, by its name in WebAssembly text (a specialisation's name for a generic one).
    | Call of string * Atom list
    /// Allocates the cell of a tag with a payload.
    | MakeTag of string * Atom
    /// Allocates a record with these fields, in the order they are stored, which is also the order they are evaluated.
    | MakeRecord of (string * Atom) list
    /// Reads the field stored in this slot of a record.
    | Field of Atom * int
    /// Copies a record of this many fields, and replaces the fields in these slots: `record with f = v`.
    | UpdateRecord of Atom * int * (int * Atom) list
    /// Builds a list of these elements, in order.
    | MakeList of Atom list
    /// A call to a runtime helper (see `WasmRuntime.Helpers`): an intrinsic, or the helper an operator on strings, lists
    /// or tags needs. One that returns nothing (an intrinsic whose result is unit) leaves unit, as every value must.
    | CallRuntime of string * Atom list * returnsValue: bool
    /// Allocates the closure block of a lifted function (by its name in WebAssembly text, and how many parameters it
    /// takes, the environment included) over the values it captures.
    | MakeClosure of string * arity: int * Atom list
    /// A call through a function value: the closure block is passed first, as the environment, and its first slot is
    /// the function's table slot.
    | CallClosure of ClosureRef * Atom list
    /// Builds an interpolated string from its text and its holes, which are already evaluated into their locals.
    | Interpolate of InterpolationPart list
    /// `dbg(value)`: prints the value (the atom, unless the form says otherwise) and leaves it.
    | Dbg of Atom * DbgForm
    /// Boxes a float constant.
    | BoxFloat of float
    /// A `for` loop: evaluates its body for each element, and is unit.
    | ForEach of ForLoop
    /// `let x = value in body`. The value is never a `Let` itself: blocks are flattened.
    | Let of Var * Expr * Expr
    /// Evaluates the first expression for its effects, drops its value, then evaluates the second.
    | Do of Expr * Expr
    | If of Atom * Expr * Expr
    /// Tests the scrutinees against each arm's patterns in turn and evaluates the first arm that matches.
    | Match of Var list * Arm list

/// A `for` loop over a list or a range, at a depth (how many loops enclose it) that names its locals.
and ForLoop =
    { Source: ForSource
      Depth: int
      Cursor: Var
      End: Var
      Element: Var
      Body: Expr }

and ForSource =
    | OverList of Atom
    /// The ints from the first to the second, both included.
    | OverRange of Atom * Atom

and Arm =
    { Patterns: Pattern list
      Body: Expr }

/// A top-level function, as it is emitted: a generic definition yields one per instantiation.
type Function =
    { /// The function's name in WebAssembly text.
      Name: string
      /// The definition it was lowered from.
      Source: string
      /// The type arguments it was specialised for, in the order of the definition's type variables; none for a
      /// definition that is not generic.
      TypeArguments: IrType list
      Params: Var list
      Result: IrType
      Body: Expr }

module IrType =
    let rec show (ty: IrType) =
        match ty with
        | IrInt -> "int"
        | IrBool -> "bool"
        | IrUnit -> "unit"
        | IrString -> "string"
        | IrFloat -> "float"
        | IrTags tags ->
            tags
            |> List.map (fun (name, payload) ->
                match payload with
                | Some payloadTy -> $"#{name}({show payloadTy})"
                | None -> $"#{name}")
            |> String.concat " | "
        | IrRecord fields ->
            let shown = fields |> List.map (fun (name, fieldTy) -> $"{name}: {show fieldTy}") |> String.concat ", "
            $"({shown})"
        | IrList element -> $"list({show element})"
        | IrFunction(input, output) -> $"({show input} -> {show output})"

/// The IR as text, for reading and for tests.
module Print =
    let private atom (a: Atom) =
        match a with
        | AVar v -> v.Name
        | AInt value -> string value
        | ABool value -> if value then "true" else "false"
        | AUnit -> "()"
        | AString value -> $"\"{value}\""
        | ATag name -> $"#{name}"

    let rec private pattern (p: Pattern) =
        match p with
        | PWildcard -> "_"
        | PInt value -> string value
        | PBool value -> if value then "true" else "false"
        | PTag(name, Some payload) -> $"#{name}({pattern payload})"
        | PTag(name, None) -> $"#{name}"
        | PFields fields ->
            let shown = fields |> List.map (fun (slot, inner) -> $"{slot}: {pattern inner}") |> String.concat ", "
            $"({shown})"
        | PString value -> $"\"{value}\""
        | PGuard(op, value) -> $"{op} {atom value}"
        | PList(elements, rest) ->
            let shown = elements |> List.map pattern
            let shownRest = rest |> Option.map (fun r -> [ $"...{pattern r}" ]) |> Option.defaultValue []
            let all = shown @ shownRest |> String.concat ", "
            $"[{all}]"
        | PBind v -> v.Name

    let rec private expr (indent: string) (e: Expr) : string =
        let atoms (items: Atom list) = items |> List.map atom |> String.concat ", "
        match e.Kind with
        | Atom a -> atom a
        | Prim(Binary op, [ left; right ]) -> $"{atom left} {op} {atom right}"
        | Prim(Binary op, operands) -> $"({op} {atoms operands})"
        | Prim(FloatBinary op, operands) -> $"(float {op} {atoms operands})"
        | Prim(Not, operands) -> $"not({atoms operands})"
        | Call(name, args) -> $"{name}({atoms args})"
        | MakeTag(name, payload) -> $"#{name}({atom payload})"
        | MakeRecord fields ->
            let shown = fields |> List.map (fun (name, value) -> $"{name} = {atom value}") |> String.concat ", "
            $"({shown})"
        | Field(record, slot) -> $"{atom record}[{slot}]"
        | UpdateRecord(record, _, updates) ->
            let shown = updates |> List.map (fun (slot, value) -> $"[{slot}] = {atom value}") |> String.concat ", "
            $"{atom record} with {shown}"
        | MakeList items -> $"[{atoms items}]"
        | CallRuntime(name, args, _) -> $"{name}({atoms args})"
        | MakeClosure(name, _, captured) -> $"closure {name}({atoms captured})"
        | CallClosure(ClosureVar v, args) -> $"{v.Name}({atoms args})"
        | CallClosure(ClosureValue name, args) -> $"{name}()({atoms args})"
        | Interpolate parts ->
            parts
            |> List.map (function
                | Text text -> text
                | Hole(v, _) -> "{" + v.Name + "}")
            |> String.concat ""
            |> sprintf "\"%s\""
        | Dbg(value, _) -> $"dbg({atom value})"
        | BoxFloat value -> string value
        | ForEach loop ->
            let inner = indent + "  "
            let source =
                match loop.Source with
                | OverList list -> atom list
                | OverRange(first, last) -> $"{atom first}..{atom last}"
            $"for {loop.Element.Name} in {source} do\n{inner}{expr inner loop.Body}"
        | Let(v, value, body) -> $"let {v.Name}: {IrType.show v.Type} = {expr indent value}\n{indent}{expr indent body}"
        | Do(first, rest) -> $"do {expr indent first}\n{indent}{expr indent rest}"
        | If(cond, ifTrue, ifFalse) ->
            let inner = indent + "  "
            $"if {atom cond} then\n{inner}{expr inner ifTrue}\n{indent}else\n{inner}{expr inner ifFalse}"
        | Match(scrutinees, arms) ->
            let inner = indent + "  "
            let scrutineeNames = scrutinees |> List.map _.Name |> String.concat ", "
            let renderedArms =
                arms
                |> List.map (fun arm ->
                    let patterns = arm.Patterns |> List.map pattern |> String.concat ", "
                    $"\n{indent}| {patterns} ->\n{inner}{expr inner arm.Body}")
                |> String.concat ""
            $"match {scrutineeNames}{renderedArms}"

    let func (f: Function) =
        let parameters = f.Params |> List.map (fun p -> $"{p.Name}: {IrType.show p.Type}") |> String.concat ", "
        let body = expr "  " f.Body
        $"fn {f.Name}({parameters}): {IrType.show f.Result} =\n  {body}"
