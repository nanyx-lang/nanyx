/// The core IR: the program between the typed AST and WebAssembly text (see "Core IR" in
/// `docs/src/content/deep-dives/memory-management.md`). It is in A-normal form, so every intermediate value has a name and a place, and every
/// node has a concrete type: generic functions are specialised for each set of type arguments they are used at as the
/// IR is built (`CoreIrLower.fs`), and code is generated from it (`CoreIrEmit.fs`).
module Transpiler.Wasm.CoreIr

open NyxCompiler

/// A concrete type. There are no type variables: a generic function is only ever lowered at concrete types.
type IrType =
    | IrInt
    /// A sized integer other than `int`: a `U64` is held in an `i64`, and any other in an `i32`, sign-extended or
    /// zero-extended from its width as it is signed or not (see `CoreIrLayout`).
    | IrSized of SizedInteger
    | IrBool
    | IrUnit
    | IrString
    /// A float: an `f64`, on the heap only as a part of something else.
    | IrFloat
    /// An `F32`: an `f32`, on the heap only as a part of something else.
    | IrF32
    /// A value of no known type, because of an error deferred to run time (a debug build of a module with errors).
    | IrError
    /// A value that is one of these tags, each with the type of its payload if it has one, sorted by name. A single
    /// tag's type is a union of one.
    | IrTags of (string * IrType option) list
    /// A record, its fields in the order they are stored: sorted by name. A tuple is a record whose fields are its
    /// positions, and a nominal type is the type of its shape.
    | IrRecord of (string * IrType) list
    /// A list of values of this type: 0 when empty, else a pointer to a cell of (head, tail).
    | IrList of IrType
    /// An array of values of this type: a pointer to a block of its length, an `i32`, then its elements one after
    /// another, each stored as a list cell's head is (see `CoreIrLayout.arrayElementOffset`).
    | IrArray of IrType
    /// A function value: a closure block holding the function's table slot and what it captures. The input is a
    /// record of the parameters when there are several, as a function's type has it. The members are those of the
    /// contexts the function requires where it is called, by key, each with its type: a call passes them after its
    /// arguments, in this order (contexts without parameters by name, then instances of parameterised ones by key, and
    /// each context's members in its layout's order). `waits` says whether the function requires `Async`, so that a call
    /// of it can wait (see `docs/src/content/deep-dives/async.md`): `Async` has no members, so nothing else says so.
    | IrFunction of IrType * IrType * members: (ContextMemberKey * IrType) list * waits: bool
    /// A value of one of a context member's own type variables, which the member's value is made once for, whatever type
    /// each call has it at: held in a box, a closure block whose function returns the value, so it is passed, kept and
    /// dropped the same way whatever the value is (see `CoreIrLowerBoxes`).
    | IrBox
    /// A type that holds itself, as a type alias that names itself in the payload of one of its tags does
    /// (`type Tree(a) = #leaf | #node(Tree(a), a, Tree(a))`): the type is its body, with each `IrSelf` in it the whole
    /// type again. Code that reads what the type is made of reads `IrType.unfold` of it.
    | IrRecursive of IrType
    /// In the body of an `IrRecursive`, an `IrRecursive` around it: `IrSelf 0` the innermost, `IrSelf 1` the one around
    /// that, and so on, as a type that holds a map of itself, `type Json = ... | #object(map(string, Json))`, holds
    /// `Json` inside the map's store, itself recursive (see `Maps.storeOf`).
    | IrSelf of int
    /// A 32-bit word that is no value of the program's: a function's slot in the table, which a closure block holds
    /// first, a cursor along a list's cells, or the block a reuse token holds.
    | IrWord

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
    /// The local a hole of an interpolated string is evaluated into: how deeply the string is nested in other strings'
    /// holes, and the hole's place among the string's holes. Its name is `Interpolation.holeLocalName` of the two.
    | InterpolationHole of depth: int * index: int
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
    /// A sized integer constant, in its bits: a `U64` past the largest `int` is negative here.
    | ASized of SizedInteger * int64
    | ABool of bool
    | AUnit
    /// A string literal, which lives in the data segment.
    | AString of string
    /// A tag without a payload, which is its code.
    | ATag of string
    /// A closure over a lifted function (as `MakeClosure` names it) that captures nothing, of this function type. It is
    /// the same block every time, so it lives in the data segment, as a string literal does.
    | AStaticClosure of string * arity: int * IrType

/// Which entry of the function a closure holds a call through it calls (see "Closures" in
/// `docs/src/content/deep-dives/memory-management.md`). Every function a closure is over has two, which differ
/// only in whether they own their environment, the closure itself.
type ClosureEntry =
    /// The entry in the closure's table slot, which borrows the closure: the caller keeps its reference for the call.
    | Borrowing
    /// The entry in the slot after it (`CoreIrClosureEntries.owningName`), which takes the caller's reference over: a
    /// tail call of a closure that dies there, which the caller could not drop after the call.
    | Owning

type PrimOp =
    /// An operator on ints or booleans: `+`, `==`, `and`, ...
    | Binary of string
    /// An operator on floats or on `F32`s, on their values.
    | FloatBinary of string
    | Not

/// How a hole of an interpolated string is printed.
type HoleType =
    | HoleString
    | HoleInt
    /// A sized integer, written in decimal as an int is.
    | HoleSized of SizedInteger
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

/// Where the source an expression was lowered from is written: its file, as its span names it, and its line and column,
/// each from 1.
/// What a crash's stack reports for the code made from it (see `CoreIrEmit`'s markers). A place is not part of what an
/// expression is: every place equals every other, so two expressions that differ only in where they were written
/// compare equal, as the passes that compare expressions expect.
[<CustomEquality; CustomComparison>]
type Place =
    { File: string
      Line: int
      /// The column it starts at, from 1, which a crash's report names (#2950); a stack names the line alone.
      Column: int
      /// For code inlined from a function, that function's name and the place of the call it was inlined at (see
      /// `CoreIrInline`), so that a stack shows the frame the call would have had.
      InlinedAt: (string * Place) option }

    override _.Equals(other) = other :? Place
    override _.GetHashCode() = 0

    interface System.IComparable with
        member _.CompareTo _ = 0

module Place =
    /// The place of the source `span` starts at; none for a span the compiler made up.
    let ofSpan (span: Parser.Program.SourceSpan) : Place option =
        if Parser.Program.SourceSpan.isNone span then None
        else Some { File = span.File; Line = span.StartLine + 1; Column = span.StartColumn + 1; InlinedAt = None }

/// The place the expressions made now come from: set by the lowering as it lowers each source expression, so that
/// `Expr.make` gives every expression made from one its place; none outside the lowering.
type Places private () =
    [<System.ThreadStatic; DefaultValue>]
    static val mutable private current: Place option

    static member Current
        with get () = Places.current
        and set (place: Place option) = Places.current <- place

type Expr =
    { Kind: ExprKind
      Type: IrType
      /// Where the source it was lowered from is written; none for what a pass after the lowering made.
      Place: Place option }

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
    /// Copies a record, and replaces the fields in these slots: `record with f = v`.
    | UpdateRecord of Atom * (int * Atom) list
    /// Gives the cell this local is (see `CellSyntax`) the atom's value, dropping the value it held, and is unit.
    | Assign of Var * Atom
    /// Writes the atom's value into this slot of the record in the local, dropping the value it held, and is unit: how a
    /// box a lambda shares is changed (see `CellSyntax`).
    | SetField of Var * int * Atom
    /// Builds a list of these elements, in order.
    | MakeList of Atom list
    /// A call to a runtime helper (see `WasmRuntime.Helpers`): an intrinsic, or the helper an operator on strings, lists
    /// or tags needs. One that returns nothing (an intrinsic whose result is unit) leaves unit, as every value must.
    | CallRuntime of string * Atom list * returnsValue: bool
    /// Copies the elements of the array in `source` into the array in `target` from the index `at`, each with a reference
    /// of its own, as a collection literal's spread is (see `CoreIrLowerArrayLiterals`), and is unit. It is written out
    /// where it is used, a loop over the two blocks in step.
    | CopyArrayInto of target: Atom * at: Atom * source: Atom
    /// A call to a foreign function (see `ExternSyntax`): the module's import of it, with the atoms as its arguments,
    /// each a number of the value type it takes. One that returns nothing leaves unit.
    | CallImport of Extern * Atom list
    /// Allocates the closure block of a lifted function (by its name in WebAssembly text, and how many parameters it
    /// takes, the environment included) over the values it captures, one or more (see `closureOver`).
    | MakeClosure of string * arity: int * Atom list
    /// A call through a function value: the closure block is passed first, as the environment, and its first slot is
    /// the function's table slot. The closure is read twice, so it is a parameter or a local, never a temporary. The
    /// lowering calls the borrowing entry, and reference counting picks the owning one where the closure dies at a tail
    /// call (see `CoreIrCounting`).
    | CallClosure of Var * Atom list * ClosureEntry
    /// Builds an interpolated string from its text and its holes, which are already evaluated into their locals.
    | Interpolate of InterpolationPart list
    /// What `dbg(value)` prints in a debug build: the value's text (see `CoreIrLowerDbg`), after where the `dbg` is
    /// written (`[main.nyx:12] `), then a newline, on the program's stderr, or wherever the runtime's `nanyx:debug`
    /// `dbg` puts it (see `CoreIrEmit`). It is unit.
    | Dbg of site: DbgSite * text: Atom
    /// A float constant: an `f64`, or an `f32` where the expression is an `F32`.
    | FloatConstant of float
    /// An error deferred to run time, or a crash: writes these messages to stderr and ends the program, with a trap in
    /// `Helpers.crashed`, from which the runtime reports the stack. One that is deferred, an error the compiler found,
    /// says so to the runtime first (see `Helpers.deferredCrash`), so that a test that expects a crash doesn't pass by
    /// reaching it (#3078).
    | Crash of messages: string list * deferred: bool
    /// A crash whose message is made as the program runs, an `assert`'s that shows its values (see `AssertSyntax`):
    /// writes the string to stderr and ends the program, as `Crash` does.
    | CrashWith of Atom
    /// A debug build's probe (see `CoreIrProbes`): tells the runtime the program is at the start of a function, a
    /// statement, or a function's end, at the expression's place, and, if the runtime asks, writes each of these locals
    /// that is still alive there. It is unit, and uses none of its locals: a probe never keeps a value alive.
    | Probe of ProbeKind * ProbeLocal list
    /// A `for` loop: evaluates its body for each element, and is unit.
    | ForEach of ForLoop
    /// A `while` loop: evaluates its condition, a bool, and while it is true its body, and is unit.
    | While of WhileLoop
    /// `let x = value in body`. The value is never a `Let` itself: blocks are flattened.
    | Let of Var * Expr * Expr
    /// Evaluates the first expression for its effects, drops its value, then evaluates the second.
    | Do of Expr * Expr
    | If of Atom * Expr * Expr
    /// Tests the scrutinees against each arm's patterns in turn and evaluates the first arm that matches.
    | Match of Var list * Arm list
    /// Gives each of these variables' values one more reference, then evaluates the expression (see
    /// `CoreIrCounting`).
    | Dup of Var list * Expr
    /// Drops each of these variables' values, then evaluates the expression.
    | Drop of Var list * Expr
    /// Evaluates the expression, then drops each of these variables' values: operands it only borrowed (temporaries
    /// among them), and variables whose last use it was.
    | DropAfter of Expr * Var list
    /// Gives up an object a `match` arm took apart and does not use itself, then evaluates the expression: what a
    /// `dup` of the names the arm binds followed by a `drop` of the object does, without them when the arm holds the
    /// only reference (see `CoreIrReuse`).
    | Release of Released * Expr
    /// An allocation that writes into the block the token holds, if it holds one, instead of allocating.
    | Reuse of token: Var * Expr
    /// Frees the block the token holds, if it holds one, then evaluates the expression: a branch that makes no
    /// allocation to reuse the block in, where another branch does (see `CoreIrReuse`).
    | FreeToken of token: Var * Expr

/// Where a probe is (see `Probe`).
/// Where a `dbg` is written: the file, as the program names it (see `PackageModules.shownFile`), and the line and column, counted from 1.
and DbgSite =
    { File: string
      Line: int
      Column: int }

    /// What the program writes before the value: `[main.nyx:12] `, or `[12] ` for a file with no name.
    member this.Prefix = if this.File = "" then $"[{this.Line}] " else $"[{this.File}:{this.Line}] "

and ProbeKind =
    /// The start of the function of this name, where the runtime pushes a frame onto its shadow stack.
    | Entry of functionName: string
    /// A statement's start.
    | Statement
    /// A function's end, where the runtime pops its frame: before the call a function ends in, so that a tail call
    /// stays one, or after the value it gives back.
    | Return

/// A local a probe writes if the runtime asks: its name in the source, its variable, and the printer of its type (see
/// `CoreIrLowerDbg`).
and ProbeLocal = { Name: string; Var: Var; Printer: string }

/// An object a `match` arm releases (see `Release`).
and Released =
    { Object: Var
      /// Each field of the object on the heap: where it is, the name the arm binds it to and uses, if any, and its
      /// type.
      Fields: (int * Var option * IrType) list
      /// How far before the object's pointer its count is.
      CountOffset: int
      /// The local that gets the object's block to reuse, or 0 when the arm did not hold the only reference.
      Token: Var option }

/// A `for` loop over lists, ranges and maps' and sets' stores in step, at a depth (how many loops enclose it) that names its locals. It ends
/// when the first of its streams does.
and ForLoop =
    { Streams: ForStream list
      Depth: int
      Body: Expr }

/// A `while` loop, at a depth (how many loops enclose it) that names its labels.
and WhileLoop =
    { Condition: Expr
      Depth: int
      Body: Expr }

/// One of the sequences a `for` loop walks, and the locals that walk it.
and ForStream =
    { Source: ForSource
      Cursor: Var
      End: Var
      Element: Var }

and ForSource =
    | OverList of Atom
    /// An array's elements, by index: the stream's `Cursor` is the address of the next element, and its `End` how many
    /// are left, read from the array's length once, since an `Array` can't change (#2207).
    | OverArray of Atom
    /// The ints from the first to the second, both included.
    | OverRange of Atom * Atom
    /// The ints from the atom on, without end: only walked in step with a source that ends.
    | OverRangeFrom of Atom
    /// The entries of a map's store, or a set's elements, in the order of its trie (see `Maps.storeOf`), walked with the
    /// locals of the `StoreWalk`.
    | OverStore of Atom * StoreWalk

/// How a `for` loop walks a map's or a set's store: the stream's `Cursor` is the cell of the bucket it is in, and these
/// hold the rest. It goes into the store's nodes one level at a time, taking each node's buckets and nodes in the order
/// of their five-bit values, reading a bucket straight from its node's array, and keeping the value of the node it
/// took at each level in five bits of `Path` (a trie is no deeper than 13 levels, the last of four bits, so they fit an
/// `int`). It goes back up by going down again from the root along `Path`: it makes nothing, and counts no reference,
/// as a list's walk counts none.
and StoreWalk =
    { /// The store's type (see `Maps.storeOf`).
      StoreType: IrType
      /// Whether the loop is given each entry, a map's, or each entry's key, a set's element.
      Keys: bool
      /// The store the loop walks.
      Root: Var
      /// The bit of the node it goes into next.
      Pending: Var
      /// The payload of the node it is in: its bits and arrays.
      Node: Var
      /// The bits of the node it is in that have a bucket.
      LeafBits: Var
      /// The array of the buckets of the node it is in.
      Leaves: Var
      /// The bits of the nodes of the node it is in it has not gone into yet.
      Remaining: Var
      /// Where it is in `Leaves`: the bucket it takes next.
      LeafIndex: Var
      /// Where in `Leaves` the buckets before the next node it goes into end.
      LeafEnd: Var
      /// How many nodes it is inside.
      Depth: Var
      /// The child it took at each level.
      Path: Var }

and Arm =
    { Patterns: Pattern list
      Body: Expr }

and Pattern =
    | PWildcard
    /// An integer equal to this one, in the bits it is held in (see `intBits`).
    | PInt of int64
    /// A float equal to this literal, compared in the width the value is held in: an `f32` for an `F32`.
    | PFloat of float
    | PBool of bool
    /// A tag, and the pattern its payload must match if it has one.
    | PTag of string * Pattern option
    /// A record's fields, by the slot each is stored in, and the pattern each must match.
    | PFields of (int * Pattern) list
    /// A string equal to this literal.
    | PString of string
    /// A value for which this expression, a guard's predicate inlined, computes true each time the arm is tested, with
    /// the value in the variable: `| { >= limit * 2 } ->`.
    | PGuard of Var * Expr
    /// A list whose first elements match these patterns, and the rest of which matches the last pattern if there is
    /// one, or is empty if not.
    | PList of Pattern list * Pattern option
    /// Binds the value matched here to a variable.
    | PBind of Var
    /// A value every one of these patterns matches, binding what each of them binds: `| n & { > 100 } ->`.
    | PAll of Pattern list

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
      Body: Expr
      /// Where the definition it was lowered from is written: its file and the line it starts on. None for a function
      /// the compiler makes up, such as a runtime helper or an adapter, and for a def it made, such as a script's
      /// top-level code. A profile gives each function this place (see `WasmBinary.spanTableName`).
      Place: Place option }

/// A broken invariant of the backend: a bug in the compiler, not something it does not compile yet. The message says
/// what went wrong without the "Internal error: " the diagnostic gives it.
exception InternalError of string

/// Fails with a broken invariant of the backend (see `InternalError`).
let internalError message : 'a = raise (InternalError message)

module IrType =
    /// What a recursive type is made of: its body, with each `IrSelf` that stands for it the type itself, so that what
    /// is inside a recursive type in it refers to the type by itself and not by an `IrSelf`. Any other type as it is.
    let unfold (ty: IrType) : IrType =
        let rec replaceAt (depth: int) inner =
            let replace = replaceAt depth
            match inner with
            | IrSelf index when index = depth -> ty
            | IrSelf _ -> inner
            | IrRecursive body -> IrRecursive(replaceAt (depth + 1) body)
            | IrTags tags -> IrTags(tags |> List.map (fun (name, payload) -> name, Option.map replace payload))
            | IrRecord fields -> IrRecord(fields |> List.map (fun (name, fieldTy) -> name, replace fieldTy))
            | IrList element -> IrList(replace element)
            | IrArray element -> IrArray(replace element)
            | IrFunction(input, output, members, waits) ->
                IrFunction(replace input, replace output, members |> List.map (fun (key, memberTy) -> key, replace memberTy), waits)
            | IrInt
            | IrSized _
            | IrBool
            | IrUnit
            | IrString
            | IrFloat
            | IrF32
            | IrError
            | IrBox
            | IrWord -> inner
        match ty with
        | IrRecursive body -> replaceAt 0 body
        | _ -> ty

    /// Whether a value of `ty` is a floating-point number: a float or an `F32`.
    let isFloat (ty: IrType) = ty = IrFloat || ty = IrF32

    /// Whether a value of `ty` is a number: an int, a sized integer, a float or an `F32`.
    let isNumber (ty: IrType) =
        match ty with
        | IrInt | IrSized _ | IrFloat | IrF32 -> true
        | _ -> false

    /// Whether a record's fields are a tuple's: its positions, `0`, `1`, ...
    let isTuple (fields: (string * IrType) list) =
        Set.ofList (List.map fst fields) = Set.ofList (List.init fields.Length string)

    /// The slot and type of each element of a tuple of two or more, in the tuple's order; `None` for any other type.
    ///
    /// A function value whose input is such a tuple takes one WebAssembly argument for each element, whether its lambda
    /// names one parameter (`{ t -> ... }`, `{ (a, b) -> ... }`) or one per element (`{ a, b -> ... }`), and whether the
    /// call writes the elements (`f(a, b)`) or a tuple (`f(t)`): `int, int -> int` and `(int, int) -> int` are one type.
    let tupleElements (ty: IrType) : (int * IrType) list option =
        match ty with
        | IrRecord fields when fields.Length > 1 && isTuple fields ->
            List.init fields.Length (fun index ->
                let slot = fields |> List.findIndex (fst >> (=) (string index))
                slot, snd fields.[slot])
            |> Some
        | _ -> None

    /// The input of a function value of type `ty` that the lifted function `parameterNames` (its environment first)
    /// takes whole, though it names several parameters: a record that is not a tuple, as a call through the value
    /// passes it (`(channel: string, n: int) -> int` called as `f(channel = "abc", n = 1)`). Each parameter is bound to
    /// its field.
    let recordTakenWhole (parameterNames: string list) (ty: IrType) =
        match parameterNames, ty with
        | _ :: _ :: _ :: _, IrFunction(IrRecord _ as input, _, _, _) when (tupleElements input).IsNone -> Some input
        | _ -> None

    /// How many parameters the lifted function `parameterNames` (its environment first) takes as a function value of
    /// type `ty`: a lone parameter whose type is a tuple takes the tuple's elements (see `tupleElements`), one whose type
    /// is unit takes nothing, as a function written without parameters does, several whose input is a record that is not
    /// a tuple take it whole (see `recordTakenWhole`), and the members of the contexts the function requires follow.
    let closureArity (parameterNames: string list) (ty: IrType) =
        let members =
            match ty with
            | IrFunction(_, _, members, _) -> members.Length
            | _ -> 0
        let values =
            match parameterNames, ty with
            | _ when (recordTakenWhole parameterNames ty).IsSome -> 2
            | [ _; _ ], IrFunction(IrUnit, _, _, _) -> 1
            | [ _; _ ], IrFunction(input, _, _, _) ->
                match tupleElements input with
                | Some elements -> 1 + elements.Length
                | None -> 2
            | _ -> parameterNames.Length
        values + members

    /// Whether a value of `ty` is, or holds, a tag, a function or an array, which have no order.
    let rec holdsUnordered (ty: IrType) =
        match ty with
        | IrArray _
        | IrTags _
        | IrFunction _
        | IrBox
        | IrError -> true
        // A type holds itself only in the payload of a tag.
        | IrRecursive _
        | IrSelf _ -> true
        | IrRecord fields -> fields |> List.exists (snd >> holdsUnordered)
        | IrList element -> holdsUnordered element
        | IrInt
        | IrSized _
        | IrBool
        | IrUnit
        | IrString
        | IrFloat
        | IrF32
        | IrWord -> false

    let rec show (ty: IrType) =
        match ty with
        | IrInt -> "int"
        | IrSized integer -> SizedInteger.name integer
        | IrWord -> "word"
        | IrBool -> "bool"
        | IrUnit -> "unit"
        | IrString -> "string"
        | IrFloat -> "float"
        | IrF32 -> "F32"
        | IrError -> "?error"
        | IrBox -> "box"
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
        | IrArray element -> $"array({show element})"
        | IrFunction(input, output, [], false) -> $"({show input} -> {show output})"
        | IrFunction(input, output, members, waits) ->
            let keys = (if waits then [ "Async" ] else []) @ (members |> List.map (fst >> ContextMemberKey.text)) |> String.concat ", "
            $"([{keys}] {show input} -> {show output})"
        | IrRecursive body -> $"rec self. {show body}"
        | IrSelf 0 -> "self"
        | IrSelf index -> $"self{index}"

    /// The type as part of a WebAssembly name, such as `list_string` in `id__list_string`.
    let rec mangle (ty: IrType) =
        match ty with
        | IrTags tags ->
            tags
            |> List.map (fun (tag, payload) ->
                match payload with
                | Some payloadTy -> $"{tag}_{mangle payloadTy}"
                | None -> tag)
            |> String.concat "_"
            |> sprintf "tags_%s"
        | IrRecord fields ->
            fields
            |> List.map (fun (field, fieldTy) -> $"{field}_{mangle fieldTy}")
            |> String.concat "_"
            |> sprintf "rec_%s"
        | IrList element -> $"list_{mangle element}"
        | IrArray element -> $"array_{mangle element}"
        | IrFunction(input, output, members, waits) ->
            let keys = members |> List.map (fst >> ContextMemberKey.text) |> String.concat "_"
            let contexts = if members.IsEmpty then "" else $"_ctx_{keys}"
            let waiting = if waits then "_waits" else ""
            $"fn_{mangle input}_to_{mangle output}{contexts}{waiting}"
        | IrRecursive body -> $"mu_{mangle body}"
        | _ -> show ty

/// The bits an integer constant is held in: its low 64, so that a `U64` past the largest `int` is negative.
let intBits (value: bigint) : int64 = int64 (uint64 (value &&& ((1I <<< 64) - 1I)))

/// The integer constant `value` as an atom of the type `ty` it is written at: a sized integer's, or else an int's.
let intAtom (value: bigint) (ty: IrType) =
    match ty with
    | IrSized integer -> ASized(integer, intBits value)
    | _ -> AInt(intBits value)

/// The type of an atom's value.
let atomType (atom: Atom) =
    match atom with
    | AVar var -> var.Type
    | AInt _ -> IrInt
    | ASized(integer, _) -> IrSized integer
    | ABool _ -> IrBool
    | AUnit -> IrUnit
    | AString _ -> IrString
    | ATag name -> IrTags [ name, None ]
    | AStaticClosure(_, _, ty) -> ty

/// The closure of type `ty` over the lifted function `name`, which takes `arity` parameters, the environment included,
/// and `captured`: a block allocated as it runs, or static data when it captures nothing.
let closureOver (name: string) (arity: int) (captured: Atom list) (ty: IrType) : Expr =
    match captured with
    | [] -> { Kind = Atom(AStaticClosure(name, arity, ty)); Type = ty; Place = Places.Current }
    | _ -> { Kind = MakeClosure(name, arity, captured); Type = ty; Place = Places.Current }

module StoreWalk =
    /// The locals `walk` walks its store in, in the order the loop declares them.
    let vars (walk: StoreWalk) : Var list =
        [ walk.Root; walk.Pending; walk.Node; walk.LeafBits; walk.Leaves; walk.Remaining; walk.LeafIndex; walk.LeafEnd; walk.Depth; walk.Path ]

    /// `walk` with each of its locals made what `f` makes it.
    let mapVars (f: Var -> Var) (walk: StoreWalk) : StoreWalk =
        { walk with
            Root = f walk.Root
            Pending = f walk.Pending
            Node = f walk.Node
            LeafBits = f walk.LeafBits
            Leaves = f walk.Leaves
            Remaining = f walk.Remaining
            LeafIndex = f walk.LeafIndex
            LeafEnd = f walk.LeafEnd
            Depth = f walk.Depth
            Path = f walk.Path }

module ForLoop =
    /// The atoms a loop's streams start from, in the order its code reads them.
    let sourceAtoms (loop: ForLoop) =
        loop.Streams |> List.collect (fun stream ->
            match stream.Source with
            | OverList list -> [ list ]
            | OverArray array -> [ array ]
            | OverRange(first, last) -> [ first; last ]
            | OverRangeFrom first -> [ first ]
            | OverStore(store, _) -> [ store ])

    /// The locals a loop walks its streams in, which its body does not see from outside.
    let locals (loop: ForLoop) =
        loop.Streams |> List.collect (fun stream ->
            [ stream.Cursor; stream.End; stream.Element ]
            @ (match stream.Source with
               | OverStore(_, walk) -> StoreWalk.vars walk
               | _ -> []))

module Pattern =
    /// The patterns directly inside `pattern`, in the order they are tested: a tag's payload, the fields, the parts of
    /// an and-pattern, a list's elements and then its rest.
    let parts (pattern: Pattern) : Pattern list =
        match pattern with
        | PTag(_, Some inner) -> [ inner ]
        | PFields fields -> fields |> List.map snd
        | PAll parts -> parts
        | PList(elements, rest) -> elements @ Option.toList rest
        | PTag(_, None)
        | PBind _
        | PGuard _
        | PWildcard
        | PInt _
        | PFloat _
        | PBool _
        | PString _ -> []

    /// `pattern` with `f` applied to each pattern directly inside it: those `parts` gives.
    let mapParts (f: Pattern -> Pattern) (pattern: Pattern) : Pattern =
        match pattern with
        | PTag(name, Some inner) -> PTag(name, Some (f inner))
        | PFields fields -> PFields(fields |> List.map (fun (slot, inner) -> slot, f inner))
        | PAll parts -> PAll(List.map f parts)
        | PList(elements, rest) -> PList(List.map f elements, Option.map f rest)
        | PTag(_, None)
        | PBind _
        | PGuard _
        | PWildcard
        | PInt _
        | PFloat _
        | PBool _
        | PString _ -> pattern

    /// The guards in `pattern`, each with the variable that holds the value it tests.
    let rec guards (pattern: Pattern) : (Var * Expr) list =
        match pattern with
        | PGuard(var, guard) -> [ var, guard ]
        | _ -> parts pattern |> List.collect guards

    /// `pattern` with `f` applied to each of its guards.
    let rec mapGuards (f: Expr -> Expr) (pattern: Pattern) : Pattern =
        match pattern with
        | PGuard(var, guard) -> PGuard(var, f guard)
        | _ -> mapParts (mapGuards f) pattern

    /// The variables `pattern` binds, in order. A guard's variable is not one: it holds the value only for its test.
    let rec binds (pattern: Pattern) : Var list =
        match pattern with
        | PBind var -> [ var ]
        | _ -> parts pattern |> List.collect binds

module Expr =
    /// The expression of `kind`, of type `ty`, at the place the lowering is at (see `Places`).
    let make (kind: ExprKind) (ty: IrType) : Expr = { Kind = kind; Type = ty; Place = Places.Current }

    /// The integer operators that cannot trap, as `/` and `%` can: a value made with one of them that nothing reads need
    /// not be made. Their operands are atoms, evaluated before: `and` and `or` are one only where their right operand
    /// can be evaluated whether it is needed or not (see `isSafeInstruction`), and an `if` otherwise.
    let safeOperators = set [ "=="; "!="; "<"; "<="; ">"; ">="; "and"; "or" ]

    /// Whether `e` is an atom, or one instruction on atoms that writes nothing, calls nothing and cannot trap: evaluating
    /// it where its value is not needed costs only the instruction.
    let isSafeInstruction (e: Expr) =
        match e.Kind with
        | Atom _
        | Field _
        | Prim(Not, _)
        | Prim(FloatBinary _, _) -> true
        | Prim(Binary op, _) -> safeOperators.Contains op
        | _ -> false

    /// `var` bound to `value` for `body`, of `body`'s type.
    let letIn (var: Var) (value: Expr) (body: Expr) : Expr = make (Let(var, value, body)) body.Type

    /// `value` run for its effect, and then `body`, of `body`'s type.
    let doThen (value: Expr) (body: Expr) : Expr = make (Do(value, body)) body.Type

    /// The expressions directly inside `e`, in the order its code runs them: a let's or a do's value and then what
    /// follows it, an if's branches, each arm's guards and then its body, a loop's condition and body, and what a
    /// count instruction wraps. An instruction on atoms has none. A walk over the IR recurses through these, and a rewrite
    /// through `map`, so a new case is added to those two and nowhere else. What follows a link of a chain is the last child, and a function's body is
    /// a chain as long as the function: a walk must recurse into the last child in tail position (or walk the chain in
    /// a loop, see `Chain`), or a long function overflows the stack.
    let children (e: Expr) : Expr list =
        match e.Kind with
        | Let(_, value, body)
        | Do(value, body) -> [ value; body ]
        | If(_, ifTrue, ifFalse) -> [ ifTrue; ifFalse ]
        | Match(_, arms) -> arms |> List.collect (fun arm -> (arm.Patterns |> List.collect Pattern.guards |> List.map snd) @ [ arm.Body ])
        | ForEach loop -> [ loop.Body ]
        | While loop -> [ loop.Condition; loop.Body ]
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Release(_, body)
        | Reuse(_, body)
        | FreeToken(_, body) -> [ body ]
        | Atom _
        | Prim _
        | Call _
        | MakeTag _
        | MakeRecord _
        | Field _
        | UpdateRecord _
        | Assign _
        | SetField _
        | MakeList _
        | CallRuntime _
        | CopyArrayInto _
        | CallImport _
        | MakeClosure _
        | CallClosure _
        | Interpolate _
        | Dbg _
        | FloatConstant _
        | Crash _
        | CrashWith _
        | Probe _ -> []

    /// `e` with `f` applied to each expression directly inside it, those `children` gives, in the same order. Mapping
    /// a link of a chain maps what follows it too, so a pass that rewrites a long chain splits it (see `Chain`) and
    /// maps only the expression it ends in.
    let map (f: Expr -> Expr) (e: Expr) : Expr =
        let kind =
            match e.Kind with
            | Let(var, value, body) -> Let(var, f value, f body)
            | Do(value, body) -> Do(f value, f body)
            | If(cond, ifTrue, ifFalse) -> If(cond, f ifTrue, f ifFalse)
            | Match(scrutinees, arms) ->
                Match(scrutinees, arms |> List.map (fun arm -> { arm with Patterns = List.map (Pattern.mapGuards f) arm.Patterns; Body = f arm.Body }))
            | ForEach loop -> ForEach { loop with Body = f loop.Body }
            | While loop -> While { loop with Condition = f loop.Condition; Body = f loop.Body }
            | Dup(vars, body) -> Dup(vars, f body)
            | Drop(vars, body) -> Drop(vars, f body)
            | DropAfter(body, vars) -> DropAfter(f body, vars)
            | Release(released, body) -> Release(released, f body)
            | Reuse(token, body) -> Reuse(token, f body)
            | FreeToken(token, body) -> FreeToken(token, f body)
            | Atom _
            | Prim _
            | Call _
            | MakeTag _
            | MakeRecord _
            | Field _
            | UpdateRecord _
            | Assign _
            | SetField _
            | MakeList _
            | CallRuntime _
            | CopyArrayInto _
            | CallImport _
            | MakeClosure _
            | CallClosure _
            | Interpolate _
            | Dbg _
            | FloatConstant _
            | Crash _
            | CrashWith _
            | Probe _ -> e.Kind
        { e with Kind = kind }

/// The statements of a block: a chain of `Let`s, `Do`s, `Dup`s, `Drop`s, `Release`s and `FreeToken`s, each followed by the next. A
/// function's body is a chain as long as the function, so a pass walks one in a loop, and recurses only as deep as an
/// expression nests.
module Chain =
    /// The expression that follows `e` in a chain, if `e` is a link of one.
    let next (e: Expr) : Expr option =
        match e.Kind with
        | Let(_, _, next)
        | Do(_, next)
        | Dup(_, next)
        | Drop(_, next)
        | Release(_, next)
        | FreeToken(_, next) -> Some next
        | _ -> None

    /// The links of the chain `e` starts, outermost first, and the expression the chain ends in (`e` itself when it
    /// starts none).
    let split (e: Expr) : Expr list * Expr =
        let links = ResizeArray<Expr>()
        let mutable current = e
        let mutable following = next e
        while following.IsSome do
            links.Add current
            current <- following.Value
            following <- next current
        List.ofSeq links, current

    /// `link` with `following` in place of the expression that follows it.
    let relink (link: Expr) (following: Expr) : Expr =
        let kind =
            match link.Kind with
            | Let(var, value, _) -> Let(var, value, following)
            | Do(value, _) -> Do(value, following)
            | Dup(vars, _) -> Dup(vars, following)
            | Drop(vars, _) -> Drop(vars, following)
            | Release(released, _) -> Release(released, following)
            | FreeToken(token, _) -> FreeToken(token, following)
            | _ -> internalError "only a link of a chain is relinked"
        { link with Kind = kind }

    /// The chain of `links` (outermost first) rebuilt around `last`, each link given by `rebuild` the expression that
    /// now follows it.
    let rebuild (rebuild: 'Link -> Expr -> Expr) (links: 'Link list) (last: Expr) : Expr =
        List.foldBack rebuild links last

/// The IR as text, for reading and for tests.
module Print =
    let private atom (a: Atom) =
        match a with
        | AVar v -> v.Name
        | AInt value -> string value
        | ASized(integer, value) -> $"{value}:{SizedInteger.name integer}"
        | ABool value -> if value then "true" else "false"
        | AUnit -> "()"
        | AString value -> $"\"{value}\""
        | ATag name -> $"#{name}"
        | AStaticClosure(name, _, _) -> $"closure {name}()"

    let rec private pattern (indent: string) (p: Pattern) =
        match p with
        | PWildcard -> "_"
        | PInt value -> string value
        | PFloat value -> string value
        | PBool value -> if value then "true" else "false"
        | PTag(name, Some payload) -> $"#{name}({pattern indent payload})"
        | PTag(name, None) -> $"#{name}"
        | PFields fields ->
            let shown = fields |> List.map (fun (slot, inner) -> $"{slot}: {pattern indent inner}") |> String.concat ", "
            $"({shown})"
        | PString value -> $"\"{value}\""
        | PGuard(v, guard) -> $"{{ {v.Name} -> {expr indent guard} }}"
        | PAll parts -> parts |> List.map (pattern indent) |> String.concat " & "
        | PList(elements, rest) ->
            let shown = elements |> List.map (pattern indent)
            let shownRest = rest |> Option.map (fun r -> [ $"...{pattern indent r}" ]) |> Option.defaultValue []
            let all = shown @ shownRest |> String.concat ", "
            $"[{all}]"
        | PBind v -> v.Name

    and private expr (indent: string) (e: Expr) : string =
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
        | UpdateRecord(record, updates) ->
            let shown = updates |> List.map (fun (slot, value) -> $"[{slot}] = {atom value}") |> String.concat ", "
            $"{atom record} with {shown}"
        | Assign(var, value) -> $"{var.Name} := {atom value}"
        | SetField(record, slot, value) -> $"{record.Name}[{slot}] := {atom value}"
        | MakeList items -> $"[{atoms items}]"
        | CallRuntime(name, args, _) -> $"{name}({atoms args})"
        | CopyArrayInto(target, at, source) -> $"copyInto({atoms [ target; at; source ]})"
        | CallImport(declared, args) -> $"import {declared.Module}.{declared.Name}({atoms args})"
        | MakeClosure(name, _, captured) -> $"closure {name}({atoms captured})"
        | CallClosure(v, args, Borrowing) -> $"{v.Name}({atoms args})"
        | CallClosure(v, args, Owning) -> $"owning {v.Name}({atoms args})"
        | Interpolate parts ->
            parts
            |> List.map (function
                | Text text -> text
                | Hole(v, _) -> "{" + v.Name + "}")
            |> String.concat ""
            |> sprintf "\"%s\""
        | Dbg(site, text) -> $"dbg({site.Prefix.Trim()}, {atom text})"
        | FloatConstant value -> string value
        | Crash(messages, _) -> $"""crash({messages |> String.concat ""})"""
        | CrashWith message -> $"crash({atom message})"
        | Probe(kind, locals) -> $"""probe {kind}({locals |> List.map _.Var.Name |> String.concat ", "})"""
        | ForEach loop ->
            let inner = indent + "  "
            let source (stream: ForStream) =
                match stream.Source with
                | OverList list -> atom list
                | OverArray array -> atom array
                | OverRange(first, last) -> $"{atom first}..{atom last}"
                | OverRangeFrom first -> $"{atom first}.."
                | OverStore(store, _) -> atom store
            let elements = loop.Streams |> List.map _.Element.Name |> String.concat ", "
            let sources = loop.Streams |> List.map source |> String.concat ", "
            $"for {elements} in {sources} do\n{inner}{expr inner loop.Body}"
        | While loop ->
            let inner = indent + "  "
            $"while {expr inner loop.Condition} do\n{inner}{expr inner loop.Body}"
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
                    let patterns = arm.Patterns |> List.map (pattern indent) |> String.concat ", "
                    $"\n{indent}| {patterns} ->\n{inner}{expr inner arm.Body}")
                |> String.concat ""
            $"match {scrutineeNames}{renderedArms}"
        | Dup(vars, body) -> $"""dup {vars |> List.map _.Name |> String.concat ", "}\n{indent}{expr indent body}"""
        | Drop(vars, body) -> $"""drop {vars |> List.map _.Name |> String.concat ", "}\n{indent}{expr indent body}"""
        | DropAfter(value, vars) -> $"""{expr indent value} then drop {vars |> List.map _.Name |> String.concat ", "}"""
        | Release(released, body) ->
            let token = released.Token |> Option.map (fun token -> $" into {token.Name}") |> Option.defaultValue ""
            $"release {released.Object.Name}{token}\n{indent}{expr indent body}"
        | Reuse(token, value) -> $"{expr indent value} reusing {token.Name}"
        | FreeToken(token, body) -> $"free {token.Name}\n{indent}{expr indent body}"

    let func (f: Function) =
        let parameters = f.Params |> List.map (fun p -> $"{p.Name}: {IrType.show p.Type}") |> String.concat ", "
        let body = expr "  " f.Body
        $"fn {f.Name}({parameters}): {IrType.show f.Result} =\n  {body}"
