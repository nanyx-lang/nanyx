/// The WebAssembly binary format (`.wasm`) of a module written as WebAssembly text: an assembler of its own, so that
/// the compiler writes `.wasm` without Wasmtime, which a browser build of the compiler doesn't have (#1235). It reads
/// what the backend writes: the module fields `type`, `import`, `func`, `table`, `memory`, `global`, `export`,
/// `start`, `elem` and `data`, with exports written inside a definition too, and instructions written flat or folded.
/// The instructions are those of WebAssembly 2.0's numeric, variable, memory and control instructions, with
/// `return_call` and `return_call_indirect` from tail calls. Function names go in the `name` section, so a trap's
/// backtrace names them as it does for the text, and the place markers the backend writes (`;;@ main.nyx:12`) go in a
/// table of places, so the runtime can name the file and line of each frame.
module Transpiler.Wasm.WasmBinary

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open Transpiler.Wasm.WatSyntax

/// The name of the custom section that holds the table of places. It holds six vectors, written as in the rest of the module.
/// The names of files and functions, numbered from 1. The frames, numbered from 1, each a function's name (0 for the
/// function the code is in), a file's name, a line, a column (0 for none, as a definition's frame has), and the frame of
/// the call it was inlined at (0 for none), which
/// comes before it: a place is its innermost frame, and the frames of the calls it was inlined through follow from it.
/// For each stretch of code, in order, how far after the last one it starts (the first, from the start of the
/// module) and its place, the number of its innermost frame. And each debug probe (see `CoreIrProbes`), in order of
/// its number: the number, its kind (see `probeKindCode`), for an entry the function's name (else 0), and its place. And each
/// function with a definition marker (`;;@def main.nyx:3`), in order: its name, as the name section gives it, and the
/// frame of its definition, which has no function's name. And the files of the standard library's, each by its name's
/// number (see `WatSyntax.Library`), so that a crash's report can name the program's code that called it. And each
/// function whose name a stack shows as it is written (see `WatSyntax.Named`): its name, as the name section gives it,
/// and the name it is written by, each by its number. A function's
/// code starts a stretch of place 0, none, so
/// that the code before its first marker has none. Each number is an unsigned LEB128. The runtime reads it to name the
/// file and line of each frame of a trap, and of each function in a profile (see `Nanyx.Runtime.SpanTable`).
let spanTableName = "nanyx.spans"


/// What follows an instruction's opcode.
type private Immediate =
    | NoImmediate
    /// A memory access's offset and alignment, with its natural alignment as a power of two.
    | MemoryAccess of naturalAlignment: int
    | I32Value
    | I64Value
    | F32Value
    | F64Value
    | LocalIndex
    | GlobalIndex
    | FunctionIndex
    | LabelIndex
    | LabelTable
    /// A function type, and the table it calls through (`call_indirect`, `return_call_indirect`).
    | IndirectCall
    /// The memory's index, always 0 (`memory.size`, `memory.grow`, `memory.fill`).
    | MemoryIndex
    /// A destination memory and a source memory, both 0 (`memory.copy`).
    | TwoMemories

/// Each plain instruction's opcode bytes and immediate. `block`, `loop`, `if`, `else`, `end` and `select` are read
/// apart (see `Assembler.Instruction`).
let private instructions: IDictionary<string, byte list * Immediate> =
    let one (code: int) (immediate: Immediate) = [ byte code ], immediate
    let prefixed (code: int) (immediate: Immediate) = [ 0xFCuy; byte code ], immediate
    let numeric (first: int) (names: string list) = names |> List.mapi (fun index name -> name, one (first + index) NoImmediate)
    dict
        [ "unreachable", one 0x00 NoImmediate
          "nop", one 0x01 NoImmediate
          "br", one 0x0C LabelIndex
          "br_if", one 0x0D LabelIndex
          "br_table", one 0x0E LabelTable
          "return", one 0x0F NoImmediate
          "call", one 0x10 FunctionIndex
          "call_indirect", one 0x11 IndirectCall
          "return_call", one 0x12 FunctionIndex
          "return_call_indirect", one 0x13 IndirectCall
          "drop", one 0x1A NoImmediate
          "local.get", one 0x20 LocalIndex
          "local.set", one 0x21 LocalIndex
          "local.tee", one 0x22 LocalIndex
          "global.get", one 0x23 GlobalIndex
          "global.set", one 0x24 GlobalIndex
          "i32.load", one 0x28 (MemoryAccess 2)
          "i64.load", one 0x29 (MemoryAccess 3)
          "f32.load", one 0x2A (MemoryAccess 2)
          "f64.load", one 0x2B (MemoryAccess 3)
          "i32.load8_s", one 0x2C (MemoryAccess 0)
          "i32.load8_u", one 0x2D (MemoryAccess 0)
          "i32.load16_s", one 0x2E (MemoryAccess 1)
          "i32.load16_u", one 0x2F (MemoryAccess 1)
          "i64.load8_s", one 0x30 (MemoryAccess 0)
          "i64.load8_u", one 0x31 (MemoryAccess 0)
          "i64.load16_s", one 0x32 (MemoryAccess 1)
          "i64.load16_u", one 0x33 (MemoryAccess 1)
          "i64.load32_s", one 0x34 (MemoryAccess 2)
          "i64.load32_u", one 0x35 (MemoryAccess 2)
          "i32.store", one 0x36 (MemoryAccess 2)
          "i64.store", one 0x37 (MemoryAccess 3)
          "f32.store", one 0x38 (MemoryAccess 2)
          "f64.store", one 0x39 (MemoryAccess 3)
          "i32.store8", one 0x3A (MemoryAccess 0)
          "i32.store16", one 0x3B (MemoryAccess 1)
          "i64.store8", one 0x3C (MemoryAccess 0)
          "i64.store16", one 0x3D (MemoryAccess 1)
          "i64.store32", one 0x3E (MemoryAccess 2)
          "memory.size", one 0x3F MemoryIndex
          "memory.grow", one 0x40 MemoryIndex
          "i32.const", one 0x41 I32Value
          "i64.const", one 0x42 I64Value
          "f32.const", one 0x43 F32Value
          "f64.const", one 0x44 F64Value
          yield!
              numeric 0x45 [ "i32.eqz"; "i32.eq"; "i32.ne"; "i32.lt_s"; "i32.lt_u"; "i32.gt_s"; "i32.gt_u"; "i32.le_s"; "i32.le_u"; "i32.ge_s"; "i32.ge_u" ]
          yield!
              numeric 0x50 [ "i64.eqz"; "i64.eq"; "i64.ne"; "i64.lt_s"; "i64.lt_u"; "i64.gt_s"; "i64.gt_u"; "i64.le_s"; "i64.le_u"; "i64.ge_s"; "i64.ge_u" ]
          yield! numeric 0x5B [ "f32.eq"; "f32.ne"; "f32.lt"; "f32.gt"; "f32.le"; "f32.ge" ]
          yield! numeric 0x61 [ "f64.eq"; "f64.ne"; "f64.lt"; "f64.gt"; "f64.le"; "f64.ge" ]
          yield!
              numeric
                  0x67
                  [ "i32.clz"; "i32.ctz"; "i32.popcnt"; "i32.add"; "i32.sub"; "i32.mul"; "i32.div_s"; "i32.div_u"; "i32.rem_s"; "i32.rem_u"
                    "i32.and"; "i32.or"; "i32.xor"; "i32.shl"; "i32.shr_s"; "i32.shr_u"; "i32.rotl"; "i32.rotr" ]
          yield!
              numeric
                  0x79
                  [ "i64.clz"; "i64.ctz"; "i64.popcnt"; "i64.add"; "i64.sub"; "i64.mul"; "i64.div_s"; "i64.div_u"; "i64.rem_s"; "i64.rem_u"
                    "i64.and"; "i64.or"; "i64.xor"; "i64.shl"; "i64.shr_s"; "i64.shr_u"; "i64.rotl"; "i64.rotr" ]
          yield!
              numeric
                  0x8B
                  [ "f32.abs"; "f32.neg"; "f32.ceil"; "f32.floor"; "f32.trunc"; "f32.nearest"; "f32.sqrt"; "f32.add"; "f32.sub"; "f32.mul"
                    "f32.div"; "f32.min"; "f32.max"; "f32.copysign" ]
          yield!
              numeric
                  0x99
                  [ "f64.abs"; "f64.neg"; "f64.ceil"; "f64.floor"; "f64.trunc"; "f64.nearest"; "f64.sqrt"; "f64.add"; "f64.sub"; "f64.mul"
                    "f64.div"; "f64.min"; "f64.max"; "f64.copysign" ]
          yield!
              numeric
                  0xA7
                  [ "i32.wrap_i64"; "i32.trunc_f32_s"; "i32.trunc_f32_u"; "i32.trunc_f64_s"; "i32.trunc_f64_u"; "i64.extend_i32_s"
                    "i64.extend_i32_u"; "i64.trunc_f32_s"; "i64.trunc_f32_u"; "i64.trunc_f64_s"; "i64.trunc_f64_u"; "f32.convert_i32_s"
                    "f32.convert_i32_u"; "f32.convert_i64_s"; "f32.convert_i64_u"; "f32.demote_f64"; "f64.convert_i32_s"; "f64.convert_i32_u"
                    "f64.convert_i64_s"; "f64.convert_i64_u"; "f64.promote_f32"; "i32.reinterpret_f32"; "i64.reinterpret_f64"
                    "f32.reinterpret_i32"; "f64.reinterpret_i64"; "i32.extend8_s"; "i32.extend16_s"; "i64.extend8_s"; "i64.extend16_s"
                    "i64.extend32_s" ]
          yield!
              [ "i32.trunc_sat_f32_s"; "i32.trunc_sat_f32_u"; "i32.trunc_sat_f64_s"; "i32.trunc_sat_f64_u"; "i64.trunc_sat_f32_s"
                "i64.trunc_sat_f32_u"; "i64.trunc_sat_f64_s"; "i64.trunc_sat_f64_u" ]
              |> List.mapi (fun index name -> name, prefixed index NoImmediate)
          "memory.copy", prefixed 10 TwoMemories
          "memory.fill", prefixed 11 MemoryIndex ]

/// Bytes written one after another.
type private Bytes = ResizeArray<byte>

let private unsigned (out: Bytes) (value: uint64) =
    let mutable value = value
    let mutable going = true
    while going do
        let low = byte (value &&& 0x7FUL)
        value <- value >>> 7
        if value = 0UL then
            out.Add low
            going <- false
        else
            out.Add(low ||| 0x80uy)

let private signed (out: Bytes) (value: int64) =
    let mutable value = value
    let mutable going = true
    while going do
        let low = byte (value &&& 0x7FL)
        value <- value >>> 7
        if (value = 0L && low &&& 0x40uy = 0uy) || (value = -1L && low &&& 0x40uy <> 0uy) then
            out.Add low
            going <- false
        else
            out.Add(low ||| 0x80uy)

let private name (out: Bytes) (text: string) =
    let bytes = Encoding.UTF8.GetBytes text
    unsigned out (uint64 bytes.Length)
    out.AddRange bytes

/// Writes `items`' count, then each of them with `write`.
let private vector (out: Bytes) (items: 'a seq) (write: Bytes -> 'a -> unit) =
    let items = List.ofSeq items
    unsigned out (uint64 items.Length)
    for item in items do
        write out item

/// A module that can't be assembled: the text is not a module this assembler reads, or names something it doesn't
/// declare.
exception WasmAssemblyError of string

let private fail (message: string) = raise (WasmAssemblyError message)

let private valueType (written: string) =
    match written with
    | "i32" -> 0x7Fuy
    | "i64" -> 0x7Euy
    | "f32" -> 0x7Duy
    | "f64" -> 0x7Cuy
    | "funcref" -> 0x70uy
    | "externref" -> 0x6Fuy
    | _ -> fail $"an unknown value type {written}"

/// An integer as WebAssembly text writes one: signed or not, decimal or `0x` hexadecimal, with `_` between digits. A
/// value too big for an `int64` is read as a `uint64`, as `i64.const` reads it, and kept in the same 64 bits.
let private integer (written: string) : int64 =
    let text = written.Replace("_", "")
    let negative = text.StartsWith "-"
    let digits = text.TrimStart('-', '+')
    let magnitude =
        if digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
            UInt64.Parse(digits.Substring 2, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        else
            UInt64.Parse(digits, CultureInfo.InvariantCulture)
    if negative then -(int64 magnitude) else int64 magnitude

/// A float as WebAssembly text writes one: decimal, `inf`, `nan` or `nan:0x...` (its payload), with a sign or not.
let private floatValue (written: string) : float =
    let text = written.Replace("_", "")
    let negative = text.StartsWith "-"
    let unsignedText = text.TrimStart('-', '+')
    let magnitude =
        match unsignedText with
        | "inf" -> Double.PositiveInfinity
        | "nan" -> Double.NaN
        | _ when unsignedText.StartsWith "nan:" -> Double.NaN
        | _ when unsignedText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) -> fail $"a hexadecimal float {written}, which this assembler doesn't read"
        | _ -> Double.Parse(unsignedText, NumberStyles.Float, CultureInfo.InvariantCulture)
    if negative then -magnitude else magnitude

/// Whether `node` is the list `(keyword ...)`.
let private isList (keyword: string) (node: SExpr) =
    match node with
    | List(Atom head :: _) -> head = keyword
    | _ -> false

/// The `$name` a definition opens with, if it has one, and the rest of it.
let private ownName (items: SExpr list) =
    match items with
    | Atom id :: rest when id.StartsWith "$" -> Some id, rest
    | _ -> None, items

/// The names `(export "name")` written inside a definition gives it, and the rest of the definition.
let private inlineExports (items: SExpr list) =
    let exports = items |> List.choose (function List [ Atom "export"; Text bytes ] -> Some(Encoding.UTF8.GetString bytes) | _ -> None)
    exports, items |> List.filter (isList "export" >> not)

/// What an import brings in or an export gives out.
type private ExternalKind =
    | Func
    | Table
    | Memory
    | Global

    /// The byte the binary format writes for the kind.
    member kind.Code =
        match kind with
        | Func -> 0uy
        | Table -> 1uy
        | Memory -> 2uy
        | Global -> 3uy

/// The kind the keyword `func`, `table`, `memory` or `global` names.
let private externalKind (keyword: string) =
    match keyword with
    | "func" -> Some Func
    | "table" -> Some Table
    | "memory" -> Some Memory
    | "global" -> Some Global
    | _ -> None

/// The limits `min max?` among `items`, failing with `failure` when they are not there.
let private limitsOf (failure: string) (items: SExpr list) =
    match items |> List.choose (function Atom limit when Char.IsDigit limit.[0] -> Some(uint64 (integer limit)) | _ -> None) with
    | [ least ] -> least, None
    | [ least; most ] -> least, Some most
    | _ -> fail failure

/// Writes the limits of a memory or a table.
let private writeLimits (out: Bytes) (least: uint64, most: uint64 option) =
    match most with
    | None ->
        out.Add 0x00uy
        unsigned out least
    | Some most ->
        out.Add 0x01uy
        unsigned out least
        unsigned out most

/// The offset an active segment opens with, `(offset ...)` or a single folded instruction, and the rest of it,
/// failing with `failure` when it has none.
let private segmentOffset (failure: string) (items: SExpr list) =
    match items with
    | List(Atom "offset" :: offset) :: rest -> offset, rest
    | (List _ as offset) :: rest -> [ offset ], rest
    | _ -> fail failure

/// A function's parameters, each with its name if it has one, and its results, as `(param ...)` and `(result ...)`
/// write them among `items`.
let private signature (items: SExpr list) =
    let parameters =
        items
        |> List.collect (function
            | List [ Atom "param"; Atom id; Atom ty ] when id.StartsWith "$" -> [ Some id, valueType ty ]
            | List(Atom "param" :: types) -> types |> List.map (function Atom ty -> None, valueType ty | _ -> fail "a parameter that is not a type")
            | _ -> [])
    let results =
        items
        |> List.collect (function
            | List(Atom "result" :: types) -> types |> List.map (function Atom ty -> valueType ty | _ -> fail "a result that is not a type")
            | _ -> [])
    parameters, results

/// A function's type: its parameters' and results' value types.
type private FunctionType = { Parameters: byte list; Results: byte list }

type private Function =
    { Name: string option
      Type: int
      /// The names of its parameters, then its locals, by index.
      Locals: Dictionary<string, int>
      /// The value type of each of its locals, after its parameters.
      LocalTypes: byte list
      Body: SExpr list }

type private Global = { Type: byte; Mutable: bool; Init: SExpr list }

/// An `import`: its module, its name and what it brings in, as the import section writes the last.
type private Import = { Module: string; Name: string; Description: byte list }

/// The module's definitions, read from its fields in order and numbered as WebAssembly numbers them: imports first in
/// each index space.
type private Assembler(fields: SExpr list) =
    let types = ResizeArray<FunctionType>()
    let typeNames = Dictionary<string, int>()
    let functionNames = Dictionary<string, int>()
    let globalNames = Dictionary<string, int>()
    let memoryNames = Dictionary<string, int>()
    let tableNames = Dictionary<string, int>()
    let imports = ResizeArray<Import>()
    let functions = ResizeArray<Function>()
    let globals = ResizeArray<Global>()
    let memories = ResizeArray<uint64 * uint64 option>()
    let tables = ResizeArray<byte * uint64 * uint64 option>()
    let exports = ResizeArray<string * ExternalKind * int>()
    let elements = ResizeArray<int * SExpr list * SExpr list>()
    let data = ResizeArray<int * SExpr list * byte array>()
    let mutable start: int option = None
    // The files of the standard library's (see `WatSyntax.Library`).
    let libraryFiles = ResizeArray<string>()
    // The names functions are written by (see `WatSyntax.Named`).
    let writtenNames = ResizeArray<string * string>()
    // The names of the functions, by index, for the name section.
    let functionNamesByIndex = SortedDictionary<int, string>()
    let mutable importedFunctions = 0
    let mutable importedGlobals = 0
    let mutable importedMemories = 0
    let mutable importedTables = 0

    /// The index of the type with `parameters` and `results`, adding it if there is none.
    member _.TypeOf(parameters: byte list, results: byte list) =
        let wanted = { Parameters = parameters; Results = results }
        match Seq.tryFindIndex ((=) wanted) types with
        | Some index -> index
        | None ->
            types.Add wanted
            types.Count - 1

    /// The type a type use among `items` names, `(type $t)`, or the one its `(param ...)` and `(result ...)` write,
    /// with its parameters' names.
    member this.TypeUse(items: SExpr list) =
        let parameters, results = signature items
        let index =
            match items |> List.tryPick (function List [ Atom "type"; Atom reference ] -> Some reference | _ -> None) with
            | Some reference -> Assembler.Resolve(typeNames, reference, "type")
            | None -> this.TypeOf(List.map snd parameters, results)
        index, parameters |> List.map fst

    /// The index `reference` names: a number, or a `$name` among `names`.
    static member Resolve(names: Dictionary<string, int>, reference: string, kind: string) =
        if reference.StartsWith "$" then
            match names.TryGetValue reference with
            | true, index -> index
            | false, _ -> fail $"an unknown {kind} {reference}"
        else
            int (integer reference)

    member this.Read() =
        let fields =
            match fields with
            | [ List(Atom "module" :: items) ] -> items |> List.skipWhile (function Atom id -> id.StartsWith "$" | _ -> false)
            | _ -> fail "text that is not one (module ...)"
        // Types first, since a type use names them; then the definitions of each index space, imports first.
        for field in fields do
            match field with
            | List(Atom "type" :: rest) ->
                let id, rest = ownName rest
                match rest with
                | [ List(Atom "func" :: signatureItems) ] ->
                    let parameters, results = signature signatureItems
                    types.Add { Parameters = List.map snd parameters; Results = results }
                    id |> Option.iter (fun id -> typeNames.[id] <- types.Count - 1)
                | _ -> fail "a type that is not a function's"
            | _ -> ()
        let isImport (field: SExpr) =
            match field with
            | List(Atom "import" :: _) -> true
            | List(Atom _ :: rest) -> rest |> List.exists (isList "import")
            | _ -> false
        for field in fields |> List.filter isImport do
            this.Import field
        let isLibrary (field: SExpr) = match field with Library _ | Named _ -> true | _ -> false
        for field in fields |> List.filter (fun field -> not (isImport field) && not (isLibrary field)) do
            this.Define field
        for field in fields do
            match field with
            | Library file -> libraryFiles.Add file
            | Named(functionName, written) -> writtenNames.Add((functionName, written))
            | _ -> ()
        for field in fields do
            match field with
            | List [ Atom "start"; Atom reference ] -> start <- Some(Assembler.Resolve(functionNames, reference, "function"))
            | List(Atom "export" :: Text exported :: [ List [ Atom keyword; Atom reference ] ]) ->
                match externalKind keyword with
                | Some kind ->
                    exports.Add((Encoding.UTF8.GetString exported, kind, Assembler.Resolve(this.NamesOf kind, reference, keyword)))
                | None -> fail $"an export of an unknown kind {keyword}"
            | _ -> ()

    /// The names of the definitions of `kind`.
    member _.NamesOf(kind: ExternalKind) =
        match kind with
        | Func -> functionNames
        | Table -> tableNames
        | Memory -> memoryNames
        | Global -> globalNames

    /// Gives the definition of `kind` at `index` its `$name`, if it has one, and exports it under each of `exported`.
    member this.Declare(kind: ExternalKind, index: int, id: string option, exported: string list) =
        id |> Option.iter (fun id ->
            (this.NamesOf kind).[id] <- index
            if kind = Func then functionNamesByIndex.[index] <- id.Substring 1)
        for name in exported do
            exports.Add((name, kind, index))

    /// An import, written as `(import "module" "name" (func ...))` or inside a definition, `(func $f (import ...) ...)`.
    member this.Import(field: SExpr) =
        let moduleName, importName, keyword, items =
            match field with
            | List [ Atom "import"; Text moduleName; Text importName; List(Atom kind :: items) ] -> moduleName, importName, kind, items
            | List(Atom kind :: items) ->
                match items |> List.tryPick (function List [ Atom "import"; Text m; Text n ] -> Some(m, n) | _ -> None) with
                | Some(m, n) -> m, n, kind, items |> List.filter (isList "import" >> not)
                | None -> fail "an import without a module and a name"
            | _ -> fail "an import that is not a list"
        let id, items = ownName items
        let exported, items = inlineExports items
        let kind =
            match externalKind keyword with
            | Some kind -> kind
            | None -> fail $"an import of an unknown kind {keyword}"
        let out = Bytes()
        out.Add kind.Code
        match kind with
        | Func ->
            let typeIndex, _ = this.TypeUse items
            this.Declare(Func, importedFunctions, id, exported)
            importedFunctions <- importedFunctions + 1
            unsigned out (uint64 typeIndex)
        | Memory ->
            this.Declare(Memory, importedMemories, id, exported)
            importedMemories <- importedMemories + 1
            writeLimits out (limitsOf "limits that are not a minimum and an optional maximum" items)
        | Global ->
            this.Declare(Global, importedGlobals, id, exported)
            importedGlobals <- importedGlobals + 1
            let ty, isMutable = Assembler.GlobalType items
            out.Add ty
            out.Add(if isMutable then 1uy else 0uy)
        | Table ->
            this.Declare(Table, importedTables, id, exported)
            importedTables <- importedTables + 1
            let limits, refType = items |> List.partition (function Atom limit -> Char.IsDigit limit.[0] | _ -> false)
            out.Add(match refType with [ Atom ty ] -> valueType ty | _ -> 0x70uy)
            writeLimits out (limitsOf "limits that are not a minimum and an optional maximum" limits)
        imports.Add { Module = Encoding.UTF8.GetString moduleName; Name = Encoding.UTF8.GetString importName; Description = List.ofSeq out }

    /// A global's value type and whether it is mutable: `i32` or `(mut i32)`, the first of `items`.
    static member GlobalType(items: SExpr list) =
        match items with
        | Atom ty :: _ -> valueType ty, false
        | List [ Atom "mut"; Atom ty ] :: _ -> valueType ty, true
        | _ -> fail "a global without a type"

    /// A definition that is not an import.
    member this.Define(field: SExpr) =
        match field with
        | List(Atom "func" :: items) ->
            let id, items = ownName items
            let exported, items = inlineExports items
            this.Declare(Func, importedFunctions + functions.Count, id, exported)
            // The type use and the locals come first; a flat body can have `(result ...)` of its own, a block's.
            let header = items |> List.takeWhile (fun item -> isList "type" item || isList "param" item || isList "result" item || isList "local" item)
            let body = List.skip header.Length items
            let items = header
            let typeIndex, parameterNames = this.TypeUse items
            let locals = Dictionary<string, int>()
            let parameterNames =
                if parameterNames.IsEmpty then List.replicate types.[typeIndex].Parameters.Length None else parameterNames
            parameterNames |> List.iteri (fun index name -> name |> Option.iter (fun name -> locals.[name] <- index))
            let declared =
                items
                |> List.collect (function
                    | List [ Atom "local"; Atom id; Atom ty ] when id.StartsWith "$" -> [ Some id, valueType ty ]
                    | List(Atom "local" :: localTypes) -> localTypes |> List.map (function Atom ty -> None, valueType ty | _ -> fail "a local that is not a type")
                    | _ -> [])
            declared |> List.iteri (fun index (name, _) -> name |> Option.iter (fun name -> locals.[name] <- parameterNames.Length + index))
            functions.Add { Name = id; Type = typeIndex; Locals = locals; LocalTypes = List.map snd declared; Body = body }
        | List(Atom "memory" :: items) ->
            let id, items = ownName items
            let exported, items = inlineExports items
            this.Declare(Memory, importedMemories + memories.Count, id, exported)
            memories.Add(limitsOf "a memory without its size" items)
        | List(Atom "global" :: items) ->
            let id, items = ownName items
            let exported, items = inlineExports items
            this.Declare(Global, importedGlobals + globals.Count, id, exported)
            let ty, isMutable = Assembler.GlobalType items
            globals.Add { Type = ty; Mutable = isMutable; Init = List.tail items }
        | List(Atom "table" :: items) ->
            let id, items = ownName items
            let exported, items = inlineExports items
            this.Declare(Table, importedTables + tables.Count, id, exported)
            let least, most = limitsOf "a table without its size" items
            let refType = items |> List.tryPick (function Atom ty when not (Char.IsDigit ty.[0]) -> Some(valueType ty) | _ -> None)
            tables.Add((defaultArg refType 0x70uy, least, most))
        | List(Atom "elem" :: items) ->
            let _, items = ownName items
            let table, items =
                match items with
                | List [ Atom "table"; Atom reference ] :: rest -> Assembler.Resolve(tableNames, reference, "table"), rest
                | _ -> 0, items
            let offset, items = segmentOffset "an element segment without an offset" items
            let items = items |> List.filter (fun item -> item <> Atom "func")
            elements.Add((table, offset, items))
        | List(Atom "data" :: items) ->
            let _, items = ownName items
            let memory, items =
                match items with
                | List [ Atom "memory"; Atom reference ] :: rest -> Assembler.Resolve(memoryNames, reference, "memory"), rest
                | _ -> 0, items
            let offset, items = segmentOffset "a passive data segment, which this assembler doesn't write" items
            let bytes = items |> Seq.collect (function Text bytes -> bytes | _ -> fail "data that is not a string") |> Array.ofSeq
            data.Add((memory, offset, bytes))
        | List(Atom("type" | "start" | "export") :: _) -> ()
        | List(Atom kind :: _) -> fail $"a module field of an unknown kind {kind}"
        | _ -> fail "a module field that is not a list"

    member _.Types = types
    member _.TypeNames = typeNames
    member _.FunctionNames = functionNames
    member _.GlobalNames = globalNames
    member _.Imports = imports
    member _.Functions = functions
    member _.LibraryFiles = libraryFiles
    member _.WrittenNames = writtenNames
    member _.Globals = globals
    member _.Memories = memories
    member _.Tables = tables
    member _.TableNames = tableNames
    member _.Exports = exports
    member _.Elements = elements
    member _.Data = data
    member _.Start = start
    member _.FunctionNamesByIndex = functionNamesByIndex

/// Writes the code of instructions, flat or folded, for a function whose locals are `locals` (empty for a constant
/// expression), keeping the labels of the blocks it is in, and adds to `places` each place marker's frames with where
/// in `out` the code after it starts, and to `probes` each probe's number and kind with the frames of the place marker
/// before it.
type private CodeWriter
    (
        assembler: Assembler,
        locals: Dictionary<string, int>,
        out: Bytes,
        places: ResizeArray<int * (string option * string * int * int) list>,
        probes: ResizeArray<int * string * string option * (string option * string * int * int) list>
    ) =
    let labels = Stack<string option>()
    // The frames of the last place marker, which a probe's marker after it is at.
    let mutable lastFrames = []

    /// The depth of the block a branch to `reference` leaves: its label, or a number.
    member _.Label(reference: string) =
        if reference.StartsWith "$" then
            match labels |> Seq.tryFindIndex ((=) (Some reference)) with
            | Some depth -> depth
            | None -> fail $"a branch to an unknown label {reference}"
        else
            int (integer reference)

    /// A block's type, from the `(type ...)`, `(param ...)` and `(result ...)` that open `items`, and the rest.
    member _.BlockType(items: SExpr list) =
        let typeItems = items |> List.takeWhile (fun item -> isList "type" item || isList "param" item || isList "result" item)
        let rest = List.skip typeItems.Length items
        match signature typeItems, typeItems |> List.exists (isList "type") with
        | ([], []), false -> out.Add 0x40uy
        | ([], [ result ]), false -> out.Add result
        | _ ->
            let index, _ = assembler.TypeUse typeItems
            signed out (int64 index)
        rest

    /// Opens a block: `block`, `loop` or `if`, with its label and type, and gives the rest of `items`.
    member this.Open(opcode: byte, items: SExpr list) =
        let label, items = ownName items
        out.Add opcode
        let rest = this.BlockType items
        labels.Push label
        rest

    /// Writes the immediate of an instruction, read from the front of `items`, and gives the rest.
    member this.Immediate(immediate: Immediate, items: SExpr list) : SExpr list =
        let atom (items: SExpr list) =
            match items with
            | Atom value :: rest -> value, rest
            | _ -> fail "an instruction without its immediate"
        match immediate with
        | NoImmediate -> items
        | MemoryAccess natural ->
            let options = items |> List.takeWhile (function Atom option -> option.StartsWith "offset=" || option.StartsWith "align=" | _ -> false)
            let valueOf (key: string) =
                options |> List.tryPick (function Atom option when option.StartsWith key -> Some(integer (option.Substring key.Length)) | _ -> None)
            let alignment = valueOf "align=" |> Option.map (fun align -> int (Math.Log2(float align))) |> Option.defaultValue natural
            unsigned out (uint64 alignment)
            unsigned out (uint64 (defaultArg (valueOf "offset=") 0L))
            List.skip options.Length items
        | I32Value ->
            let value, rest = atom items
            signed out (int64 (int32 (integer value)))
            rest
        | I64Value ->
            let value, rest = atom items
            signed out (integer value)
            rest
        | F32Value ->
            let value, rest = atom items
            out.AddRange(BitConverter.GetBytes(float32 (floatValue value)))
            rest
        | F64Value ->
            let value, rest = atom items
            out.AddRange(BitConverter.GetBytes(floatValue value))
            rest
        | LocalIndex ->
            let reference, rest = atom items
            unsigned out (uint64 (Assembler.Resolve(locals, reference, "local")))
            rest
        | GlobalIndex ->
            let reference, rest = atom items
            unsigned out (uint64 (Assembler.Resolve(assembler.GlobalNames, reference, "global")))
            rest
        | FunctionIndex ->
            let reference, rest = atom items
            unsigned out (uint64 (Assembler.Resolve(assembler.FunctionNames, reference, "function")))
            rest
        | LabelIndex ->
            let reference, rest = atom items
            unsigned out (uint64 (this.Label reference))
            rest
        | LabelTable ->
            let targets = items |> List.takeWhile (function Atom target -> target.StartsWith "$" || Char.IsDigit target.[0] | _ -> false)
            if targets.IsEmpty then fail "a br_table without a default"
            let depths = targets |> List.map (function Atom target -> this.Label target | _ -> 0)
            vector out (List.take (depths.Length - 1) depths) (fun out depth -> unsigned out (uint64 depth))
            unsigned out (uint64 (List.last depths))
            List.skip targets.Length items
        | IndirectCall ->
            let table, items =
                match items with
                | Atom reference :: rest when reference.StartsWith "$" || Char.IsDigit reference.[0] -> Assembler.Resolve(assembler.TableNames, reference, "table"), rest
                | _ -> 0, items
            let typeItems = items |> List.takeWhile (fun item -> isList "type" item || isList "param" item || isList "result" item)
            let index, _ = assembler.TypeUse typeItems
            unsigned out (uint64 index)
            unsigned out (uint64 table)
            List.skip typeItems.Length items
        | MemoryIndex ->
            out.Add 0x00uy
            items
        | TwoMemories ->
            out.Add 0x00uy
            out.Add 0x00uy
            items

    /// Writes the instructions `items`, flat or folded.
    member this.Instructions(items: SExpr list) =
        let mutable rest = items
        while not rest.IsEmpty do
            rest <-
                match rest with
                | List folded :: rest ->
                    this.Folded folded
                    rest
                | Atom keyword :: rest -> this.Flat(keyword, rest)
                | Placed frames :: rest ->
                    places.Add((out.Count, frames))
                    lastFrames <- frames
                    rest
                | Probed(id, kind, functionName) :: rest ->
                    probes.Add((id, kind, functionName, lastFrames))
                    rest
                // A function's definition is read by `assemble`.
                | Defined _ :: rest -> rest
                | Library _ :: _ -> fail "a standard library's file among instructions"
                | Named _ :: _ -> fail "a function's written name among instructions"
                | Text _ :: _ -> fail "a string among instructions"
                | [] -> []

    /// Writes the flat instruction `keyword`, reading its immediates from the front of `rest`, and gives what follows.
    member this.Flat(keyword: string, rest: SExpr list) : SExpr list =
        // A label may follow `else` and `end`: it names the block they belong to.
        let skipLabel (rest: SExpr list) =
            match rest with
            | Atom label :: rest when label.StartsWith "$" -> rest
            | _ -> rest
        match keyword with
        | "block" -> this.Open(0x02uy, rest)
        | "loop" -> this.Open(0x03uy, rest)
        | "if" -> this.Open(0x04uy, rest)
        | "else" ->
            out.Add 0x05uy
            skipLabel rest
        | "end" ->
            out.Add 0x0Buy
            if labels.Count = 0 then fail "an end that closes no block"
            labels.Pop() |> ignore
            skipLabel rest
        | "select" ->
            match rest with
            | List(Atom "result" :: types) :: rest ->
                out.Add 0x1Cuy
                vector out types (fun out ty -> match ty with Atom ty -> out.Add(valueType ty) | _ -> fail "a select's result that is not a type")
                rest
            | _ ->
                out.Add 0x1Buy
                rest
        | _ ->
            match instructions.TryGetValue keyword with
            | true, (opcode, immediate) ->
                out.AddRange opcode
                this.Immediate(immediate, rest)
            | false, _ -> fail $"an unknown instruction {keyword}"

    /// Writes a folded instruction, `(op immediates... operands...)`: its operands first, then the instruction.
    member this.Folded(items: SExpr list) =
        match items with
        | Atom("block" | "loop" as keyword) :: rest ->
            let body = this.Open((if keyword = "block" then 0x02uy else 0x03uy), rest)
            this.Instructions body
            out.Add 0x0Buy
            labels.Pop() |> ignore
        | Atom "if" :: rest ->
            let label, rest = ownName rest
            let typeItems = rest |> List.takeWhile (fun item -> isList "type" item || isList "param" item || isList "result" item)
            let rest = List.skip typeItems.Length rest
            let condition = rest |> List.filter (fun item -> not (isList "then" item || isList "else" item))
            this.Instructions condition
            out.Add 0x04uy
            this.BlockType typeItems |> ignore
            labels.Push label
            for item in rest do
                match item with
                | List(Atom "then" :: body) -> this.Instructions body
                | List(Atom "else" :: body) ->
                    out.Add 0x05uy
                    this.Instructions body
                | _ -> ()
            out.Add 0x0Buy
            labels.Pop() |> ignore
        | Atom keyword :: rest ->
            // The immediates come first, then the operands, which are folded instructions themselves.
            let probe = Bytes()
            let operandsFrom =
                let writer = CodeWriter(assembler, locals, probe, ResizeArray(), ResizeArray())
                writer.CopyLabels labels
                writer.Flat(keyword, rest)
            this.Instructions operandsFrom
            out.AddRange probe
        | _ -> fail "a folded instruction that is not a list"

    /// Takes on the labels of the blocks `outer` is in, for a folded instruction's immediates.
    member _.CopyLabels(outer: Stack<string option>) =
        for label in Seq.rev outer do
            labels.Push label

/// The number a probe's kind has in the table of places, after its stretches: 1 for a function's entry, 2 for a
/// statement and 3 for a function's return (see `CoreIrProbes`).
let private probeKindCode (kind: string) =
    match kind with
    | "entry" -> 1
    | "statement" -> 2
    | "return" -> 3
    | _ -> fail $"a probe of an unknown kind, {kind}"

/// Writes a section: its id, its size and its contents.
let private section (out: Bytes) (id: byte) (contents: Bytes) =
    out.Add id
    unsigned out (uint64 contents.Count)
    out.AddRange contents

/// A constant expression, `(i32.const 0)`, ended.
let private constant (assembler: Assembler) (out: Bytes) (items: SExpr list) =
    CodeWriter(assembler, Dictionary(), out, ResizeArray(), ResizeArray()).Instructions items
    out.Add 0x0Buy

/// The binary format of the module that the WebAssembly text `wat` writes. It runs on a large stack (see
/// `Nanyx.LargeStack`), as writing a folded instruction recurses once per level it nests, and an else-if chain of 2,000
/// branches is 2,000 `if`s, each in the `else` of the one before: more than a 1 MB stack holds.
let assemble (wat: string) : byte array =
    Nanyx.LargeStack.run <| fun () ->
        let assembler = Assembler(read wat)
        assembler.Read()
        // The functions' code first: a `call_indirect` or a block may add a type.
        let code = Bytes()
        // Each place marker's frames, and where in `code` the code after it starts; and where each body starts, with no
        // frames, so that code before a body's first marker has no place.
        let places = ResizeArray<int * (string option * string * int * int) list>()
        // Each probe's number and kind, and the frames of its place.
        let probes = ResizeArray<int * string * string option * (string option * string * int * int) list>()
        // Each function's name, as the name section gives it, with the file and line of its definition.
        let definitions = ResizeArray<string * string * int>()
        vector code assembler.Functions (fun out func ->
            match func.Name, func.Body |> List.tryPick (function Defined(file, line) -> Some(file, line) | _ -> None) with
            | Some id, Some(file, line) -> definitions.Add((id.Substring 1, file, line))
            | _ -> ()
            let body = Bytes()
            let groups =
                func.LocalTypes
                |> List.fold (fun groups ty ->
                    match groups with
                    | (count, last) :: rest when last = ty -> (count + 1, ty) :: rest
                    | _ -> (1, ty) :: groups) []
                |> List.rev
            vector body groups (fun out (count, ty) ->
                unsigned out (uint64 count)
                out.Add ty)
            let bodyPlaces = ResizeArray()
            CodeWriter(assembler, func.Locals, body, bodyPlaces, probes).Instructions func.Body
            body.Add 0x0Buy
            unsigned out (uint64 body.Count)
            places.Add((out.Count, []))
            for offset, frames in bodyPlaces do
                places.Add((out.Count + offset, frames))
            out.AddRange body)
        let globals = Bytes()
        vector globals assembler.Globals (fun out global' ->
            out.Add global'.Type
            out.Add(if global'.Mutable then 1uy else 0uy)
            constant assembler out global'.Init)
        let elements = Bytes()
        vector elements assembler.Elements (fun out (table, offset, functions) ->
            if table = 0 then
                out.Add 0x00uy
            else
                out.Add 0x02uy
                unsigned out (uint64 table)
            constant assembler out offset
            if table <> 0 then out.Add 0x00uy
            vector out functions (fun out reference ->
                match reference with
                | Atom reference -> unsigned out (uint64 (Assembler.Resolve(assembler.FunctionNames, reference, "function")))
                | _ -> fail "an element that is not a function"))
        let dataSegments = Bytes()
        vector dataSegments assembler.Data (fun out (memory, offset, bytes) ->
            if memory = 0 then
                out.Add 0x00uy
            else
                out.Add 0x02uy
                unsigned out (uint64 memory)
            constant assembler out offset
            unsigned out (uint64 bytes.Length)
            out.AddRange bytes)
        let out = Bytes()
        out.AddRange [| 0x00uy; 0x61uy; 0x73uy; 0x6Duy; 0x01uy; 0x00uy; 0x00uy; 0x00uy |]
        let types = Bytes()
        vector types assembler.Types (fun out ty ->
            out.Add 0x60uy
            vector out ty.Parameters (fun out parameter -> out.Add parameter)
            vector out ty.Results (fun out result -> out.Add result))
        section out 1uy types
        if assembler.Imports.Count > 0 then
            let imports = Bytes()
            vector imports assembler.Imports (fun out import ->
                name out import.Module
                name out import.Name
                out.AddRange import.Description)
            section out 2uy imports
        if assembler.Functions.Count > 0 then
            let declared = Bytes()
            vector declared assembler.Functions (fun out func -> unsigned out (uint64 func.Type))
            section out 3uy declared
        if assembler.Tables.Count > 0 then
            let tables = Bytes()
            vector tables assembler.Tables (fun out (refType, least, most) ->
                out.Add refType
                writeLimits out (least, most))
            section out 4uy tables
        if assembler.Memories.Count > 0 then
            let memories = Bytes()
            vector memories assembler.Memories writeLimits
            section out 5uy memories
        if assembler.Globals.Count > 0 then section out 6uy globals
        if assembler.Exports.Count > 0 then
            let exports = Bytes()
            vector exports assembler.Exports (fun out (exported, kind, index) ->
                name out exported
                out.Add kind.Code
                unsigned out (uint64 index))
            section out 7uy exports
        match assembler.Start with
        | Some index ->
            let start = Bytes()
            unsigned start (uint64 index)
            section out 8uy start
        | None -> ()
        if assembler.Elements.Count > 0 then section out 9uy elements
        // Where the code section's contents start, past its id and size.
        let codeStart = out.Count + 1 + (let size = Bytes() in unsigned size (uint64 code.Count); size.Count)
        if assembler.Functions.Count > 0 then section out 10uy code
        if assembler.Data.Count > 0 then section out 11uy dataSegments
        // The name section: the functions' names, so that a backtrace names them.
        if assembler.FunctionNamesByIndex.Count > 0 then
            let names = Bytes()
            name names "name"
            let functionNames = Bytes()
            vector functionNames assembler.FunctionNamesByIndex (fun out (KeyValue(index, functionName)) ->
                unsigned out (uint64 index)
                name out functionName)
            names.Add 0x01uy
            unsigned names (uint64 functionNames.Count)
            names.AddRange functionNames
            section out 0uy names
        // The table of places (see `spanTableName`).
        if places |> Seq.exists (fun (_, frames) -> not frames.IsEmpty) || probes.Count > 0 || definitions.Count > 0 then
            let strings = Dictionary<string, int>()
            let stringNumber (text: string) =
                match strings.TryGetValue text with
                | true, number -> number
                | _ ->
                    strings.[text] <- strings.Count + 1
                    strings.Count
            // Each frame once, with the frame of the call it was inlined at, which is numbered first.
            let frames = Dictionary<int * int * int * int * int, int>()
            let rec frameNumber (place: (string option * string * int * int) list) =
                match place with
                | [] -> 0
                | (functionName, file, line, column) :: outer ->
                    let key = (functionName |> Option.map stringNumber |> Option.defaultValue 0), stringNumber file, line, column, frameNumber outer
                    match frames.TryGetValue key with
                    | true, number -> number
                    | _ ->
                        frames.[key] <- frames.Count + 1
                        frames.Count
            let stretches = places |> Seq.map (fun (offset, place) -> codeStart + offset, frameNumber place) |> List.ofSeq
            // Each probe, by its number, then its kind, the function's name for an entry, and its place.
            let probed =
                probes
                |> Seq.sortBy (fun (id, _, _, _) -> id)
                |> Seq.map (fun (id, kind, functionName, frames) ->
                    id, probeKindCode kind, (functionName |> Option.map stringNumber |> Option.defaultValue 0), frameNumber frames)
                |> List.ofSeq
            // Each function's name and the frame of its definition.
            let defined = definitions |> Seq.map (fun (functionName, file, line) -> stringNumber functionName, frameNumber [ None, file, line, 0 ]) |> List.ofSeq
            // Each function's name and the name it is written by.
            let named = assembler.WrittenNames |> Seq.map (fun (functionName, written) -> stringNumber functionName, stringNumber written) |> List.ofSeq
            // The strings and frames are all numbered by now.
            let table = Bytes()

            name table spanTableName
            vector table (strings |> Seq.sortBy _.Value |> Seq.map _.Key) name
            vector table (frames |> Seq.sortBy _.Value |> Seq.map _.Key) (fun out (functionName, file, line, column, outer) ->
                for number in [ functionName; file; line; column; outer ] do
                    unsigned out (uint64 number))
            let mutable last = 0
            vector table stretches (fun out (offset, place) ->
                unsigned out (uint64 (offset - last))
                unsigned out (uint64 place)
                last <- offset)

            vector table probed (fun out (id, kind, functionName, place) ->
                for number in [ id; kind; functionName; place ] do
                    unsigned out (uint64 number))
            vector table defined (fun out (functionName, place) ->
                unsigned out (uint64 functionName)
                unsigned out (uint64 place))
            // The files of the standard library's that frames name.
            let library = assembler.LibraryFiles |> Seq.choose (fun file -> match strings.TryGetValue file with | true, number -> Some number | _ -> None) |> List.ofSeq
            vector table library (fun out number -> unsigned out (uint64 number))
            vector table named (fun out (functionName, written) ->
                unsigned out (uint64 functionName)
                unsigned out (uint64 written))
            section out 0uy table
        out.ToArray()

/// The name of the custom section a test build lists its tests in (see `withTests`).
let testTableName = "nanyx.tests"

/// A test as a test build's module lists it (see `withTests`): its module, its name, the file, line and column it is
/// written at, each from 1, whether it is marked `@test(#io)`, and the crash it expects, if it is `@test(#crashes)`.
type ListedTest =
    { Module: string
      Name: string
      File: string
      Line: int
      Column: int
      Io: bool
      Crashes: NyxCompiler.ExpectedCrash option }

/// `wasm`, a test build's module, with the custom section `testTableName` after its others, listing `tests` at the
/// indexes its entry point runs them at (see `NyxCompiler.Package.testRootSource`): a vector of each test's module,
/// name, file, line, column, and flags, 1 if it is `#io`, 2 if it expects a crash and 4 if that crash's message must
/// contain a text, which follows them; the strings as names are written and the numbers as unsigned LEB128s. The runtime reads it to find a test by its index or its name, to say where it is, and to know
/// which contexts it runs with (see `Nanyx.Runtime.TestTable`).
let withTests (tests: ListedTest list) (wasm: byte array) : byte array =
    let out = Bytes(wasm)
    let table = Bytes()
    name table testTableName
    vector table tests (fun out test ->
        name out test.Module
        name out test.Name
        name out test.File
        unsigned out (uint64 test.Line)
        unsigned out (uint64 test.Column)
        let flags =
            (if test.Io then 1UL else 0UL)
            ||| (match test.Crashes with
                 | None -> 0UL
                 | Some NyxCompiler.AnyCrash -> 2UL
                 | Some(NyxCompiler.CrashContaining _) -> 6UL)
        unsigned out flags
        match test.Crashes with
        | Some(NyxCompiler.CrashContaining text) -> name out text
        | _ -> ())
    section out 0uy table
    out.ToArray()
