/// The WebAssembly runtime every compiled module draws on: the helper functions the backend emits on demand and
/// where things live in linear memory.
module internal Transpiler.Wasm.WasmRuntime

open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WatText

/// How a value of the program's is written in memory: the WebAssembly value type it is held in, and how many bytes of
/// it are written (see `CoreIrLayout.storage`).
type Storage =
    /// An `i32` in 4 bytes: a pointer, a code, a bool, an `I32` or a `U32`.
    | Word
    /// An `i64` in 8 bytes: an int or a `U64`.
    | Wide
    /// The low byte of an `i32`: an `I8` or a `U8`.
    | Byte
    /// The low 2 bytes of an `i32`: an `I16` or a `U16`.
    | Half
    /// An `f64` in 8 bytes: a float.
    | Double
    /// An `f32` in 4 bytes: an `F32`.
    | Single

    /// The WebAssembly value type the value is held in, in locals and on the stack.
    member this.ValueType =
        match this with
        | Wide -> "i64"
        | Double -> "f64"
        | Single -> "f32"
        | Word
        | Byte
        | Half -> "i32"

    /// How many bytes the value takes in memory.
    member this.Bytes =
        match this with
        | Byte -> 1
        | Half -> 2
        | Word
        | Single -> 4
        | Wide
        | Double -> 8

    /// The instruction that writes the value at the address under it on the stack.
    member this.Store =
        match this with
        | Byte -> "i32.store8"
        | Half -> "i32.store16"
        | Word -> "i32.store"
        | Wide -> "i64.store"
        | Double -> "f64.store"
        | Single -> "f32.store"

    /// An instruction that reads the value's bits back, zero-extended: enough to copy it, not to compute with it, which
    /// `CoreIrLayout.load` is for.
    member this.CopyLoad =
        match this with
        | Byte -> "i32.load8_u"
        | Half -> "i32.load16_u"
        | Word -> "i32.load"
        | Wide -> "i64.load"
        | Double -> "f64.load"
        | Single -> "f32.load"

    /// Every way a value is stored, each with its own variant of the storing helpers.
    static member All = [ Word; Wide; Byte; Half; Double; Single ]

/// Where a tag cell's payload is: after its code (see the memory model at `runtimeHelpers`).
let tagPayloadOffset = 4

/// The header in front of an object on the heap, before its pointer (see the memory model at `runtimeHelpers`).
module Header =
    /// A list cell's or a tag cell's: its count. `newCell` makes it.
    let countOnly = 4

    /// A string's, a record's or a closure's: its count, then its info word. `newObject` makes it.
    let withInfo = 8

    /// Where the info word is in a header `withInfo`: after the count.
    let infoOffset = countOnly

    /// How far before an object's pointer its info word is: it is the last word of the header.
    let infoBefore = withInfo - infoOffset

/// How a helper, or any instruction, uses an operand: it takes the reference over (a call that owns the parameter, a
/// field of a new object, a value returned), or only borrows it while it runs.
type Use =
    | Owned
    | Borrowed

/// The names of the WebAssembly helper functions the backend emits on demand (see `runtimeHelpers`).
module Helpers =
    let alloc = Names.alloc
    let allocAligned8 = Names.allocAligned8
    let copyBytes = "__nanyx_copy_bytes"
    let stringAlloc = "__nanyx_string_alloc"
    let stringConcat = "__nanyx_string_concat"
    let stringEquals = "__nanyx_string_equals"
    let listConcat = "__nanyx_list_concat"
    let recordSet = "__nanyx_record_set"
    let recordCopy = "__nanyx_record_copy"
    let tagBox = "__nanyx_tag_box"
    let tagCode = "__nanyx_tag_code"
    /// Not a helper: `==` on a record, a list or tags, which calls the equality function of its operands' type (see
    /// `CoreIrEquality`).
    let valueEquals = "__nanyx_equals"
    /// Not a helper: the ordering intrinsic, and `<` on a string, a tuple or a list, which call the ordering function
    /// of their operands' type (see `CoreIrOrdering`).
    let valueCompare = NyxCompiler.Intrinsics.compare
    /// Not a helper: the hash intrinsic, which calls the hash function of its operand's type (see `CoreIrHashing`).
    let valueHash = NyxCompiler.Intrinsics.hash
    let floatToString = "__nanyx_float_to_string"
    /// An `F32` is written as text by a helper of its own.
    let f32ToString = "__nanyx_f32_to_string"
    let writeStderr = NyxCompiler.Intrinsics.writeStderr
    let exit = NyxCompiler.Intrinsics.exit
    /// Ends the program once a crash or an error deferred to run time has written why: a trap in a function of this
    /// name, which the runtime knows, so that it reports the stack under what was written rather than the trap. It is
    /// given the message, which a module the runtime runs reports to it first (see `WasmOptions.CrashToRuntime`).
    let crashed = NyxCompiler.Intrinsics.crashed
    /// Says that the crash about to happen is an error the compiler found and deferred to run time, not a crash the
    /// program makes: a module the runtime runs tells it, through `nanyx:crash`'s `deferred`, so that a test that expects
    /// a crash doesn't pass by reaching one (see `CodeGen.CrashReport`, #3078); in any other it does nothing.
    let deferredCrash = "__nanyx_deferred"
    let stringLength = NyxCompiler.Intrinsics.stringLength
    let stringByteAt = NyxCompiler.Intrinsics.stringByteAt
    let stringSlice = NyxCompiler.Intrinsics.stringSlice
    let stringFromByte = NyxCompiler.Intrinsics.stringFromByte
    let stringFromMemory = NyxCompiler.Intrinsics.stringFromMemory
    let stringFromBytes = NyxCompiler.Intrinsics.stringFromBytes
    let stringJoin = NyxCompiler.Intrinsics.stringJoin
    let stringShiftBytes = NyxCompiler.Intrinsics.stringShiftBytes
    let stringNew = NyxCompiler.Intrinsics.stringNew
    let stringIndexBetween = NyxCompiler.Intrinsics.stringIndexBetween
    let stringWrite = NyxCompiler.Intrinsics.stringWrite
    let listCons = NyxCompiler.Intrinsics.listCons
    let floatSqrt = NyxCompiler.Intrinsics.floatSqrt
    let floatFloor = NyxCompiler.Intrinsics.floatFloor
    let floatCeil = NyxCompiler.Intrinsics.floatCeil
    let floatTrunc = NyxCompiler.Intrinsics.floatTrunc
    let floatToInt = NyxCompiler.Intrinsics.floatToInt
    let intToFloat = NyxCompiler.Intrinsics.intToFloat
    let floatFromBits = NyxCompiler.Intrinsics.floatFromBits
    let free = Names.free
    let newCell = "__nanyx_new_cell"
    let newObject = "__nanyx_new_object"
    let release = Names.release
    let retain = Names.retain
    let reuseObject = "__nanyx_reuse_object"
    let listConsAt = "__nanyx_list_cons_at"
    let tagBoxAt = "__nanyx_tag_box_at"
    /// A string in double quotes, escaped as a string literal is written: what `dbg` prints a string as.
    let stringQuote = "__nanyx_string_quote"
    /// The characters a debug build's printers may still write of the local a probe is writing, or -1 for no limit, as
    /// for `dbg` and `assert` (see `CoreIrLowerDbg`): a global, which a probe sets around each local it writes.
    let showBudget = "__nanyx_show_budget"
    /// How many characters a probe writes a local in when the runtime asks for the locals within a budget: enough for a
    /// small record or a short list, so that a statement writes at most a few thousand, however large its values.
    let showBudgetPerLocal = 256
    /// Whether a debug build's printers have written all the characters they may (see `showBudget`), so that a printer
    /// writes `…` rather than its value.
    let showSpent = "__nanyx_show_spent"
    /// Takes the length of a string a debug build's printer wrote from what they may still write (see `showBudget`).
    let showTake = "__nanyx_show_take"
    /// The string of one `char`, its code point's UTF-8 encoding: what `dbg` prints a char as, quoted.
    let charToString = "__nanyx_char_to_string"
    /// The letter after the `\` that escapes a byte in a string literal, or 0 for a byte written as it is.
    let escapeByte = "__nanyx_escape_byte"
    /// How many bytes an int takes written in decimal, read signed or not (see `Interpolation`).
    let intTextLength = "__nanyx_int_text_length"
    /// Writes an int in decimal at an address, read signed or not, and gives the address after it.
    let intTextWrite = "__nanyx_int_text_write"
    /// WASI's `proc_exit`, imported rather than a helper of its own: asking for it declares the import.
    let procExit = Names.procExit
    /// `+`, `-` and `*` on ints, which stop the program when the result does not fit (see `checkedIntHelper`).
    let intAdd = "__nanyx_int_add"
    let intSub = "__nanyx_int_sub"
    let intMul = "__nanyx_int_mul"
    /// An int as an index into a string: itself when it fits an `i32`, else the nearest `i32`, which is past either
    /// end of every string.
    let intToIndex = "__nanyx_int_to_index"

    /// The helpers that hold a value of the program's in memory, which have a variant for each way a value is stored
    /// (see `Storage`).
    let storingValues = [ listCons; listConsAt; tagBox; tagBoxAt; recordSet; listConcat ]

    /// How the helper `name` uses its operand at `index`. A helper borrows its operands and gives back a value of its
    /// own, except `listCons`, which takes both over, `listConcat`, which copies the first list's cells and makes the
    /// second their tail, the intrinsics that write an array's element, change a mutable array's element and a list
    /// cell's tail or head, which take the value over, the one that says whether a value is held exclusively, which takes
    /// over the reference it counts, and those that give a task its function and its result, which it keeps.
    let operandUse (name: string) (index: int) =
        if name = listCons
           || (name = listConcat && index = 1)
           || (name = NyxCompiler.Intrinsics.marraySet && index = 2)
           || (name = NyxCompiler.Intrinsics.arrayWrite && index = 2)
           || (name = NyxCompiler.Intrinsics.listSetTail && index = 1)
           || (name = NyxCompiler.Intrinsics.listSetHead && index = 1)
           || name = NyxCompiler.Intrinsics.isExclusive
           || name = NyxCompiler.Intrinsics.taskStart
           || name = NyxCompiler.Intrinsics.taskFinish then
            Owned
        else
            Borrowed

    /// The helper that does what the allocating helper `name` does in the block of a reuse token (see
    /// `CoreIrReuse`), if it has one.
    let reusingVariant (name: string) = if name = listCons then Some listConsAt else None

    /// The helper `name`, one of `storingValues`, for a value stored as `storage`: the helper itself for a `Word`.
    let forStorage (name: string) (storage: Storage) =
        match storage with
        | Word -> name
        | Wide -> $"{name}_i64"
        | Byte -> $"{name}_i8"
        | Half -> $"{name}_i16"
        | Double -> $"{name}_f64"
        | Single -> $"{name}_f32"

/// A function `name` for `a ++ b` on lists: it copies the cells of `a`, in order, in front of `b`, which it takes
/// over and shares (lists being immutable). `copyHead` runs on each head before it goes into its copy, to take a
/// reference to it when the elements are on the heap. It walks `a` in a loop, writing each new cell's tail once the
/// next cell exists, so a long `a` does not run out of stack. The elements are stored as `storage`, so each cell's tail is
/// after a head of its size.
let listConcatFunction (name: string) (copyHead: string list) (storage: Storage) =
    let tail = storage.Bytes
    lines
        [ yield $"(func ${name} (param $a i32) (param $b i32) (result i32)"
          yield "  (local $result i32)"
          yield "  (local $last i32)"
          yield "  (local $cell i32)"
          yield "  local.get $b"
          yield "  local.set $result"
          yield "  block $done"
          yield "    loop $copy"
          yield "      local.get $a"
          yield "      i32.eqz"
          yield "      br_if $done"
          yield "      local.get $a"
          yield $"      {storage.CopyLoad}"
          yield! copyHead |> List.map (fun line -> "      " + line)
          yield "      i32.const 0"
          yield $"      call ${Helpers.forStorage Helpers.listCons storage}"
          yield "      local.set $cell"
          yield "      local.get $last"
          yield "      if"
          yield "        local.get $last"
          yield "        local.get $cell"
          yield $"        i32.store offset={tail}"
          yield "      else"
          yield "        local.get $cell"
          yield "        local.set $result"
          yield "      end"
          yield "      local.get $cell"
          yield "      local.set $last"
          yield "      local.get $a"
          yield $"      i32.load offset={tail}"
          yield "      local.set $a"
          yield "      br $copy"
          yield "    end"
          yield "  end"
          yield "  local.get $last"
          yield "  if"
          yield "    local.get $last"
          yield "    local.get $b"
          yield $"    i32.store offset={tail}"
          yield "  end"
          yield "  local.get $result"
          yield ")" ]

/// The helpers of `Helpers.storingValues` for values stored as `storage`, keyed by name.
let private storingHelpers (checks: bool) (storage: Storage) =
    let bytes = storage.Bytes
    let valueType = storage.ValueType
    let name helper = Helpers.forStorage helper storage
    let reused = if checks then countReuse "  " else []
    [ // A list with `$head` in front of `$tail`: `head tail -> cell`.
      name Helpers.listCons,
      lines
          [ $"(func ${name Helpers.listCons} (param $head {valueType}) (param $tail i32) (result i32)"
            "  (local $cell i32)"
            $"  i32.const {bytes + 4}"
            $"  call ${Helpers.newCell}"
            "  local.tee $cell"
            "  local.get $head"
            $"  {storage.Store}"
            "  local.get $cell"
            "  local.get $tail"
            $"  i32.store offset={bytes}"
            "  local.get $cell"
            ")" ]
      // `listCons` into the block of a reuse token, when it is not 0: `block head tail -> cell`.
      name Helpers.listConsAt,
      lines
          [ $"(func ${name Helpers.listConsAt} (param $block i32) (param $head {valueType}) (param $tail i32) (result i32)"
            "  (local $cell i32)"
            "  local.get $block"
            "  i32.eqz"
            "  if"
            "    local.get $head"
            "    local.get $tail"
            $"    call ${name Helpers.listCons}"
            "    return"
            "  end"
            yield! reused
            "  local.get $block"
            $"  i32.const {Header.countOnly}"
            "  i32.add"
            "  local.tee $cell"
            "  local.get $head"
            $"  {storage.Store}"
            "  local.get $cell"
            "  local.get $tail"
            $"  i32.store offset={bytes}"
            "  local.get $cell"
            ")" ]
      // A tag with a payload: `code payload -> cell`.
      name Helpers.tagBox,
      lines
          [ $"(func ${name Helpers.tagBox} (param $code i32) (param $payload {valueType}) (result i32)"
            "  (local $cell i32)"
            $"  i32.const {tagPayloadOffset + bytes}"
            $"  call ${Helpers.newCell}"
            "  local.tee $cell"
            "  local.get $code"
            "  i32.store"
            "  local.get $cell"
            "  local.get $payload"
            $"  {storage.Store} offset={tagPayloadOffset}"
            "  local.get $cell"
            ")" ]
      // `tagBox` into the block of a reuse token, when it is not 0: `block code payload -> cell`.
      name Helpers.tagBoxAt,
      lines
          [ $"(func ${name Helpers.tagBoxAt} (param $block i32) (param $code i32) (param $payload {valueType}) (result i32)"
            "  (local $cell i32)"
            "  local.get $block"
            "  i32.eqz"
            "  if"
            "    local.get $code"
            "    local.get $payload"
            $"    call ${name Helpers.tagBox}"
            "    return"
            "  end"
            yield! reused
            "  local.get $block"
            $"  i32.const {Header.countOnly}"
            "  i32.add"
            "  local.tee $cell"
            "  local.get $code"
            "  i32.store"
            "  local.get $cell"
            "  local.get $payload"
            $"  {storage.Store} offset={tagPayloadOffset}"
            "  local.get $cell"
            ")" ]
      // Stores one field of a freshly allocated record and hands the record pointer back, so a record
      // literal is built entirely on the operand stack: `base value offset -> base`. That keeps nested
      // literals safe, since an inner allocation can never disturb an outer one held on the stack.
      name Helpers.recordSet,
      lines
          [ $"(func ${name Helpers.recordSet} (param $base i32) (param $value {valueType}) (param $offset i32) (result i32)"
            "  local.get $base"
            "  local.get $offset"
            "  i32.add"
            "  local.get $value"
            $"  {storage.Store}"
            "  local.get $base"
            ")" ]
      name Helpers.listConcat, listConcatFunction (name Helpers.listConcat) [] storage ]

/// `+`, `-` or `*` (`instruction`, such as `i64.add`) on two ints, which stops the program when the result does not fit
/// an int: it divides the smallest int by -1, which traps with "integer overflow", as WebAssembly's own overflow does.
/// `overflowed` leaves 1 when the result `$r` of `$a` and `$b` overflowed.
let private checkedIntHelper (name: string) (instruction: string) (overflowed: string list) =
    lines
        [ yield $"(func ${name} (param $a i64) (param $b i64) (result i64)"
          yield "  (local $r i64)"
          yield "  local.get $a"
          yield "  local.get $b"
          yield $"  {instruction}"
          yield "  local.set $r"
          yield! overflowed |> List.map (fun line -> "  " + line)
          yield "  if"
          yield $"    i64.const {System.Int64.MinValue}"
          yield "    i64.const -1"
          yield "    i64.div_s"
          yield "    drop"
          yield "  end"
          yield "  local.get $r"
          yield ")" ]

/// The WebAssembly helper functions, keyed by name. The ones named after a compiler intrinsic implement
/// that intrinsic; the rest back language operators (`+` and `==` on strings, list literals and patterns)
/// and the allocator they share. Only the helpers a module uses are emitted.
///
/// Memory model: a string is a pointer to its UTF-8 bytes, with its byte length in the 4 bytes before the
/// pointer and a 0 byte after; a list is 0 (empty) or a pointer to a cell of (head, tail); a record (or tuple) is a
/// pointer to its fields in field-name order, with its layout's number (see `RecordTable`) in the 4 bytes before the
/// pointer. A tag without a payload is its code (see `tagCode`), which is odd; a tag with one is a pointer to a cell of
/// (code, payload). An int or a float takes 8 bytes in a field, a head or a payload, a narrow integer 1 or 2, and every
/// other value 4 (see `CoreIrLayout`).
///
/// Every object on the heap has a header: its reference count, then its info word if it has one (a string's length,
/// a record's layout, a closure's drop function). So the count is at `p - 8` for strings, records and closures, made
/// by `newObject`, and at `p - 4` for list cells and tag cells, made by `newCell`. A count of 0 marks
/// an object that is never freed: a string literal in the data segment. `retain` takes a reference and `release`
/// gives one up. The header's sizes are in `Header`. A helper borrows its operands and gives back a value of its own,
/// which reference counting relies on (see `CoreIrCounting.operands`), except those `Helpers.operandUse` names.
///
/// The allocator hands out 4-byte-aligned blocks, so a pointer is never odd. Every block has its size (the
/// requested size rounded up to 4 bytes, and at least 4) in the word before it, with two flags in its low bits (see
/// `WasmAllocator.SizeWordBits`), and `free` puts a block back on the free list for its size: blocks of up to 256
/// bytes have a list per size, last in first out, and larger ones share one doubly linked list. `free` joins a large
/// block with the free large blocks either side of it, found by the size words at both ends of a free large block, and
/// gives a block that ends at `$heap_ptr` back to the heap; `alloc` takes the first large block big enough, splitting
/// off the rest when that is large too. The list heads are a table at `heapBase`, past the string literals (see
/// `heapBaseAfter` and `freeListHead`), and a free block keeps the next block of its list in its first word. `alloc` takes from a list
/// when it can, and otherwise bumps `$heap_ptr`, which starts after the table, growing the memory when it runs out
/// and trapping past 4 GB.
///
/// With `checks`, `alloc`, `free`, `retain` and `release` also count blocks and stop the program on a double free or a
/// use after free, drawing on `memoryCheckFunctions`.
/// How many bytes of standard input one `fd_read` asks for.


let private helpersWith (checks: bool) (heapBase: int) : Map<string, string> =
    [ Helpers.alloc, allocHelper checks heapBase
      Helpers.free, freeHelper checks heapBase
      Helpers.allocAligned8, allocAligned8Helper checks
      // An object of `$size` bytes with a count of one and no info word: `size -> p`.
      Helpers.newCell,
      lines
          [ $"(func ${Helpers.newCell} (param $size i32) (result i32)"
            "  (local $block i32)"
            "  local.get $size"
            $"  i32.const {Header.countOnly}"
            "  i32.add"
            $"  call ${Helpers.alloc}"
            "  local.tee $block"
            "  i32.const 1"
            "  i32.store"
            "  local.get $block"
            $"  i32.const {Header.countOnly}"
            "  i32.add"
            ")" ]
      // An object of `$size` bytes with a count of one and the info word `$info`: `size info -> p`.
      Helpers.newObject,
      lines
          [ $"(func ${Helpers.newObject} (param $size i32) (param $info i32) (result i32)"
            "  (local $block i32)"
            "  local.get $size"
            $"  i32.const {Header.withInfo}"
            "  i32.add"
            $"  call ${Helpers.alloc}"
            "  local.tee $block"
            "  i32.const 1"
            "  i32.store"
            "  local.get $block"
            "  local.get $info"
            $"  i32.store offset={Header.infoOffset}"
            "  local.get $block"
            $"  i32.const {Header.withInfo}"
            "  i32.add"
            ")" ]
      Helpers.release, releaseHelper checks
      // `newObject`, in the block `$block` of an object given up with a count of one when it is not 0 (a reuse
      // token): its count is still one, so only the info word is written. `block size info -> p`.
      Helpers.reuseObject,
      lines
          [ $"(func ${Helpers.reuseObject} (param $block i32) (param $size i32) (param $info i32) (result i32)"
            "  local.get $block"
            "  i32.eqz"
            "  if"
            "    local.get $size"
            "    local.get $info"
            $"    call ${Helpers.newObject}"
            "    return"
            "  end"
            if checks then yield! countReuse "  "
            "  local.get $block"
            "  local.get $info"
            $"  i32.store offset={Header.infoOffset}"
            "  local.get $block"
            $"  i32.const {Header.withInfo}"
            "  i32.add"
            ")" ]
      for storage in Storage.All do
          yield! storingHelpers checks storage
      yield! SizedIntegerCode.helpers
      yield! DivisionCode.helpers
      Helpers.retain, retainHelper checks
      Helpers.copyBytes,
      lines
          [ $"(func ${Helpers.copyBytes} (param $dst i32) (param $src i32) (param $len i32)"
            "  local.get $dst"
            "  local.get $src"
            "  local.get $len"
            "  memory.copy"
            ")" ]
      Helpers.stringLength,
      lines
          [ $"(func ${Helpers.stringLength} (param $s i32) (result i32)"
            "  local.get $s"
            $"  i32.const {Header.infoBefore}"
            "  i32.sub"
            "  i32.load"
            ")" ]
      Helpers.stringAlloc,
      lines
          [ $"(func ${Helpers.stringAlloc} (param $len i32) (result i32)"
            "  (local $s i32)"
            "  local.get $len"
            "  i32.const 1"
            "  i32.add"
            "  local.get $len"
            $"  call ${Helpers.newObject}"
            "  local.tee $s"
            "  local.get $len"
            "  i32.add"
            "  i32.const 0"
            "  i32.store8"
            "  local.get $s"
            ")" ]
      Helpers.stringByteAt,
      lines
          [ $"(func ${Helpers.stringByteAt} (param $s i32) (param $i i32) (result i32)"
            "  local.get $i"
            "  i32.const 0"
            "  i32.lt_s"
            "  local.get $i"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.ge_s"
            "  i32.or"
            "  if (result i32)"
            "    i32.const -1"
            "  else"
            "    local.get $s"
            "    local.get $i"
            "    i32.add"
            "    i32.load8_u"
            "  end"
            ")" ]
      Helpers.stringSlice,
      lines
          [ $"(func ${Helpers.stringSlice} (param $s i32) (param $start i32) (param $len i32) (result i32)"
            "  (local $total i32)"
            "  (local $out i32)"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  local.set $total"
            "  local.get $start"
            "  i32.const 0"
            "  i32.lt_s"
            "  if"
            "    i32.const 0"
            "    local.set $start"
            "  end"
            "  local.get $start"
            "  local.get $total"
            "  i32.gt_s"
            "  if"
            "    local.get $total"
            "    local.set $start"
            "  end"
            "  local.get $len"
            "  i32.const 0"
            "  i32.lt_s"
            "  if"
            "    i32.const 0"
            "    local.set $len"
            "  end"
            "  local.get $len"
            "  local.get $total"
            "  local.get $start"
            "  i32.sub"
            "  i32.gt_s"
            "  if"
            "    local.get $total"
            "    local.get $start"
            "    i32.sub"
            "    local.set $len"
            "  end"
            "  local.get $len"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $out"
            "  local.get $s"
            "  local.get $start"
            "  i32.add"
            "  local.get $len"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            ")" ]
      Helpers.stringFromByte,
      lines
          [ $"(func ${Helpers.stringFromByte} (param $byte i32) (result i32)"
            "  (local $out i32)"
            "  i32.const 1"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $out"
            "  local.get $byte"
            "  i32.store8"
            "  local.get $out"
            ")" ]
      // A char's UTF-8 encoding: a byte below 128, or a lead byte that says how many there are, then six bits a byte.
      Helpers.charToString,
      lines
          [ $"(func ${Helpers.charToString} (param $c i32) (result i32)"
            "  (local $n i32)"
            "  (local $out i32)"
            "  (local $i i32)"
            "  i32.const 1"
            "  local.get $c"
            "  i32.const 128"
            "  i32.ge_u"
            "  i32.add"
            "  local.get $c"
            "  i32.const 2048"
            "  i32.ge_u"
            "  i32.add"
            "  local.get $c"
            "  i32.const 65536"
            "  i32.ge_u"
            "  i32.add"
            "  local.tee $n"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $n"
            "  i32.const 1"
            "  i32.eq"
            "  if"
            "    local.get $out"
            "    local.get $c"
            "    i32.store8"
            "    local.get $out"
            "    return"
            "  end"
            "  local.get $out"
            "  i32.const 3840"
            "  local.get $n"
            "  i32.shr_u"
            "  i32.const 255"
            "  i32.and"
            "  local.get $c"
            "  local.get $n"
            "  i32.const 1"
            "  i32.sub"
            "  i32.const 6"
            "  i32.mul"
            "  i32.shr_u"
            "  i32.or"
            "  i32.store8"
            "  i32.const 1"
            "  local.set $i"
            "  block $done"
            "    loop $next"
            "      local.get $i"
            "      local.get $n"
            "      i32.ge_u"
            "      br_if $done"
            "      local.get $out"
            "      local.get $i"
            "      i32.add"
            "      local.get $c"
            "      local.get $n"
            "      i32.const 1"
            "      i32.sub"
            "      local.get $i"
            "      i32.sub"
            "      i32.const 6"
            "      i32.mul"
            "      i32.shr_u"
            "      i32.const 63"
            "      i32.and"
            "      i32.const 128"
            "      i32.or"
            "      i32.store8"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $next"
            "    end"
            "  end"
            "  local.get $out"
            ")" ]
      // A new string holding a copy of `$len` bytes at `$address`, for a host (see `nanyx/host.memory`).
      Helpers.stringFromMemory,
      lines
          [ $"(func ${Helpers.stringFromMemory} (param $address i32) (param $len i32) (result i32)"
            "  (local $out i32)"
            "  local.get $len"
            $"  call ${Helpers.stringAlloc}"
            "  local.tee $out"
            "  local.get $address"
            "  local.get $len"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            ")" ]
      Helpers.stringIndexBetween, lines (StringSearch.helper Helpers.stringIndexBetween Helpers.stringLength)
      // A new string of `$len` bytes, none for a length below 0, which the standard library writes before anything
      // reads it.
      Helpers.stringNew,
      lines
          [ $"(func ${Helpers.stringNew} (param $len i32) (result i32)"
            "  local.get $len"
            "  i32.const 0"
            "  local.get $len"
            "  i32.const 0"
            "  i32.gt_s"
            "  select"
            $"  call ${Helpers.stringAlloc}"
            ")" ]
      // `$count` bytes of `$source` from `$start` copied into the new string `$target` at `$at`. A stretch of up to 16
      // bytes is copied a byte at a time, which is several times faster than `memory.copy` for so few: what `replace`
      // and `padStart` mostly copy.
      Helpers.stringWrite,
      lines
          [ $"(func ${Helpers.stringWrite} (param $target i32) (param $at i32) (param $source i32) (param $start i32) (param $count i32)"
            "  (local $i i32)"
            "  local.get $target"
            "  local.get $at"
            "  i32.add"
            "  local.set $target"
            "  local.get $source"
            "  local.get $start"
            "  i32.add"
            "  local.set $source"
            "  local.get $count"
            "  i32.const 16"
            "  i32.gt_u"
            "  if"
            "    local.get $target"
            "    local.get $source"
            "    local.get $count"
            $"    call ${Helpers.copyBytes}"
            "    return"
            "  end"
            "  block $done"
            "    loop $each"
            "      local.get $i"
            "      local.get $count"
            "      i32.ge_s"
            "      br_if $done"
            "      local.get $target"
            "      local.get $i"
            "      i32.add"
            "      local.get $source"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      i32.store8"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $each"
            "    end"
            "  end"
            ")" ]
      // A new string holding the bytes of an array of `U8`s, which are one each after its length (see
      // `CoreIrLayout.arrayElementOffset`), copied at once.
      Helpers.stringFromBytes,
      lines
          [ $"(func ${Helpers.stringFromBytes} (param $bytes i32) (result i32)"
            "  (local $out i32)"
            "  (local $len i32)"
            "  local.get $bytes"
            "  i32.load"
            "  local.tee $len"
            $"  call ${Helpers.stringAlloc}"
            "  local.tee $out"
            "  local.get $bytes"
            "  i32.const 4"
            "  i32.add"
            "  local.get $len"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            ")" ]
      Helpers.stringJoin,
      lines (
          StringJoin.helper
              Helpers.stringJoin
              { StringLength = Helpers.stringLength
                StringAlloc = Helpers.stringAlloc
                CopyBytes = Helpers.copyBytes
                CellTail = Word.Bytes }
      )
      // A copy of `$s` with `$delta` added to each byte from `$low` to `$high`, made in one allocation.
      Helpers.stringShiftBytes,
      lines
          [ $"(func ${Helpers.stringShiftBytes} (param $s i32) (param $low i32) (param $high i32) (param $delta i32) (result i32)"
            "  (local $len i32)"
            "  (local $out i32)"
            "  (local $i i32)"
            "  (local $byte i32)"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  local.tee $len"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  block $done"
            "    loop $each"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_u"
            "      br_if $done"
            "      local.get $s"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      local.set $byte"
            "      local.get $out"
            "      local.get $i"
            "      i32.add"
            "      local.get $byte"
            "      local.get $delta"
            "      i32.add"
            "      local.get $byte"
            "      local.get $byte"
            "      local.get $low"
            "      i32.ge_u"
            "      local.get $byte"
            "      local.get $high"
            "      i32.le_u"
            "      i32.and"
            "      select"
            "      i32.store8"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $each"
            "    end"
            "  end"
            "  local.get $out"
            ")" ]
      Helpers.stringConcat,
      lines
          [ $"(func ${Helpers.stringConcat} (param $a i32) (param $b i32) (result i32)"
            "  (local $lenA i32)"
            "  (local $lenB i32)"
            "  (local $out i32)"
            "  local.get $a"
            $"  call ${Helpers.stringLength}"
            "  local.set $lenA"
            "  local.get $b"
            $"  call ${Helpers.stringLength}"
            "  local.set $lenB"
            "  local.get $lenA"
            "  local.get $lenB"
            "  i32.add"
            $"  call ${Helpers.stringAlloc}"
            "  local.set $out"
            "  local.get $out"
            "  local.get $a"
            "  local.get $lenA"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            "  local.get $lenA"
            "  i32.add"
            "  local.get $b"
            "  local.get $lenB"
            $"  call ${Helpers.copyBytes}"
            "  local.get $out"
            ")" ]
      Helpers.stringEquals,
      lines
          [ $"(func ${Helpers.stringEquals} (param $a i32) (param $b i32) (result i32)"
            "  (local $len i32)"
            "  (local $i i32)"
            "  local.get $a"
            $"  call ${Helpers.stringLength}"
            "  local.set $len"
            "  local.get $len"
            "  local.get $b"
            $"  call ${Helpers.stringLength}"
            "  i32.ne"
            "  if"
            "    i32.const 0"
            "    return"
            "  end"
            "  block $done"
            "    loop $compare"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_s"
            "      br_if $done"
            "      local.get $a"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      local.get $b"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      i32.ne"
            "      if"
            "        i32.const 0"
            "        return"
            "      end"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $compare"
            "    end"
            "  end"
            "  i32.const 1"
            ")" ]
      // Writes a string to stderr (fd 2) with the iovec at 0 and nwritten at 8, like the dbg helpers: what a deferred error
      // and a failed memory check write.
      Helpers.writeStderr,
      lines
          [ $"(func ${Helpers.writeStderr} (param $s i32)"
            "  i32.const 0"
            "  local.get $s"
            "  i32.store"
            "  i32.const 4"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.store"
            "  i32.const 2"
            "  i32.const 0"
            "  i32.const 1"
            "  i32.const 8"
            "  call $fd_write"
            "  drop"
            ")" ]
      // A new record with the layout and the `$size` bytes of fields of the record `$src`: `src size -> record`.
      Helpers.recordCopy,
      lines
          [ $"(func ${Helpers.recordCopy} (param $src i32) (param $size i32) (result i32)"
            "  (local $dst i32)"
            "  local.get $size"
            "  local.get $src"
            $"  i32.const {Header.infoBefore}"
            "  i32.sub"
            "  i32.load"
            $"  call ${Helpers.newObject}"
            "  local.tee $dst"
            "  local.get $src"
            "  local.get $size"
            $"  call ${Helpers.copyBytes}"
            "  local.get $dst"
            ")" ]
      // Which tag a tag value is: the value itself when it is a code (odd), else the code in its cell.
      Helpers.tagCode,
      lines
          [ $"(func ${Helpers.tagCode} (param $tag i32) (result i32)"
            "  local.get $tag"
            "  i32.const 1"
            "  i32.and"
            "  if (result i32)"
            "    local.get $tag"
            "  else"
            "    local.get $tag"
            "    i32.load"
            "  end"
            ")" ]
      // Formats a float, or an `F32`, as the shortest decimal that reads back as the same value (see
      // `FloatText`).
      yield!
          FloatText.helpers
              Helpers.floatToString
              Helpers.f32ToString
              { NewObject = Helpers.newObject
                CopyBytes = Helpers.copyBytes
                // A module that writes a float sets the scratch block aside just below its heap (see `CodeGen`).
                Scratch = heapBase - FloatText.scratchBytes }
          |> List.map (fun (name, body) -> name, lines body)
      // The float intrinsics that are one WebAssembly instruction.
      yield!
          [ Helpers.floatSqrt, "f64.sqrt"
            Helpers.floatFloor, "f64.floor"
            Helpers.floatCeil, "f64.ceil"
            Helpers.floatTrunc, "f64.trunc" ]
          |> List.map (fun (name, instruction) ->
              name,
              lines
                  [ $"(func ${name} (param $value f64) (result f64)"
                    "  local.get $value"
                    $"  {instruction}"
                    ")" ])
      // The float whose bits are an int's.
      Helpers.floatFromBits,
      lines
          [ $"(func ${Helpers.floatFromBits} (param $bits i64) (result f64)"
            "  local.get $bits"
            "  f64.reinterpret_i64"
            ")" ]
      // A word of the table of powers of five that reading a float uses (see `PowersOfFive`).
      PowersOfFive.helper, lines PowersOfFive.helperCode
      // A float as an int: `i64.trunc_f64_s` traps on NaN, an infinity or a value out of the range of `int`.
      Helpers.floatToInt,
      lines
          [ $"(func ${Helpers.floatToInt} (param $value f64) (result i64)"
            "  local.get $value"
            "  i64.trunc_f64_s"
            ")" ]
      // Ends the program through WASI's `proc_exit`, which never returns. Its result, `undefined`, is an i32 like every
      // value, so a call stands wherever a value is expected; `unreachable` validates as one.
      Helpers.exit,
      lines
          [ $"(func ${Helpers.exit} (param $code i32) (result i32)"
            "  local.get $code"
            $"  call ${Helpers.procExit}"
            "  unreachable"
            ")" ]
      Helpers.crashed,
      lines
          [ $"(func ${Helpers.crashed} (param $message i32)"
            "  unreachable"
            ")" ]
      Helpers.deferredCrash, lines [ $"(func ${Helpers.deferredCrash})" ]
      // An int as a float: `f64.convert_i64_s` rounds to the nearest float, and is exact up to 2^53.
      Helpers.intToFloat,
      lines
          [ $"(func ${Helpers.intToFloat} (param $value i64) (result f64)"
            "  local.get $value"
            "  f64.convert_i64_s"
            ")" ]
      Helpers.intAdd, checkedIntHelper Helpers.intAdd "i64.add" [ "local.get $a"; "local.get $r"; "i64.xor"; "local.get $b"; "local.get $r"; "i64.xor"; "i64.and"; "i64.const 0"; "i64.lt_s" ]
      Helpers.intSub, checkedIntHelper Helpers.intSub "i64.sub" [ "local.get $a"; "local.get $b"; "i64.xor"; "local.get $a"; "local.get $r"; "i64.xor"; "i64.and"; "i64.const 0"; "i64.lt_s" ]
      // The product overflowed when dividing it by `$a` does not give `$b` back. Dividing the smallest int by -1 traps
      // itself, which is what -1 times the smallest int should do.
      Helpers.intMul,
      checkedIntHelper
          Helpers.intMul
          "i64.mul"
          [ "local.get $a"; "i64.eqz"; "if (result i32)"; "  i32.const 0"; "else"; "  local.get $r"; "  local.get $a"; "  i64.div_s"; "  local.get $b"; "  i64.ne"; "end" ]
      Helpers.intToIndex,
      lines
          [ $"(func ${Helpers.intToIndex} (param $value i64) (result i32)"
            "  local.get $value"
            $"  i64.const {System.Int32.MinValue}"
            "  i64.lt_s"
            "  if"
            $"    i32.const {System.Int32.MinValue}"
            "    return"
            "  end"
            "  local.get $value"
            $"  i64.const {System.Int32.MaxValue}"
            "  i64.gt_s"
            "  if"
            $"    i32.const {System.Int32.MaxValue}"
            "    return"
            "  end"
            "  local.get $value"
            "  i32.wrap_i64"
            ")" ]
      Helpers.escapeByte,
      lines
          [ yield $"(func ${Helpers.escapeByte} (param $c i32) (result i32)"
            for byte, letter in [ '"', '"'; '\\', '\\'; '\n', 'n'; '\r', 'r'; '\t', 't' ] do
                yield "  local.get $c"
                yield $"  i32.const {int byte}"
                yield "  i32.eq"
                yield "  if"
                yield $"    i32.const {int letter}"
                yield "    return"
                yield "  end"
            yield "  i32.const 0"
            yield ")" ]
      Helpers.showSpent,
      lines
          [ $"(func ${Helpers.showSpent} (result i32)"
            $"  global.get ${Helpers.showBudget}"
            "  i32.eqz"
            ")" ]
      // No limit, -1, stays one; a limit stops at 0.
      Helpers.showTake,
      lines
          [ $"(func ${Helpers.showTake} (param $s i32)"
            "  (local $left i32)"
            $"  global.get ${Helpers.showBudget}"
            "  local.tee $left"
            "  i32.const 0"
            "  i32.le_s"
            "  if"
            "    return"
            "  end"
            "  local.get $left"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.sub"
            "  local.tee $left"
            "  i32.const 0"
            "  local.get $left"
            "  i32.const 0"
            "  i32.gt_s"
            "  select"
            $"  global.set ${Helpers.showBudget}"
            ")" ]
      // Measures the quoted string first, a byte for each byte and one more for each escaped, then writes it.
      Helpers.stringQuote,
      lines
          [ $"(func ${Helpers.stringQuote} (param $s i32) (result i32)"
            "  (local $len i32)"
            "  (local $i i32)"
            "  (local $n i32)"
            "  (local $out i32)"
            "  (local $at i32)"
            "  (local $c i32)"
            "  (local $e i32)"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  local.set $len"
            "  i32.const 2"
            "  local.set $n"
            "  block $measured"
            "    loop $measure"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_s"
            "      br_if $measured"
            "      local.get $n"
            "      i32.const 1"
            "      i32.add"
            "      local.get $s"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            $"      call ${Helpers.escapeByte}"
            "      i32.const 0"
            "      i32.ne"
            "      i32.add"
            "      local.set $n"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $measure"
            "    end"
            "  end"
            "  local.get $n"
            $"  call ${Helpers.stringAlloc}"
            "  local.tee $out"
            "  i32.const 34"
            "  i32.store8"
            "  local.get $out"
            "  i32.const 1"
            "  i32.add"
            "  local.set $at"
            "  i32.const 0"
            "  local.set $i"
            "  block $written"
            "    loop $write"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_s"
            "      br_if $written"
            "      local.get $s"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      local.tee $c"
            $"      call ${Helpers.escapeByte}"
            "      local.tee $e"
            "      if"
            "        local.get $at"
            "        i32.const 92"
            "        i32.store8"
            "        local.get $at"
            "        i32.const 1"
            "        i32.add"
            "        local.tee $at"
            "        local.get $e"
            "        i32.store8"
            "      else"
            "        local.get $at"
            "        local.get $c"
            "        i32.store8"
            "      end"
            "      local.get $at"
            "      i32.const 1"
            "      i32.add"
            "      local.set $at"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $write"
            "    end"
            "  end"
            "  local.get $at"
            "  i32.const 34"
            "  i32.store8"
            "  local.get $out"
            ")" ]
      // A digit, and one more for each ten times over; and one for the `-` of a negative int read signed. The smallest
      // int is its own negation: read unsigned, its magnitude is right.
      Helpers.intTextLength,
      lines
          [ $"(func ${Helpers.intTextLength} (param $n i64) (param $signed i32) (result i32)"
            "  (local $len i32)"
            "  i32.const 1"
            "  local.set $len"
            "  local.get $signed"
            "  if"
            "    local.get $n"
            "    i64.const 0"
            "    i64.lt_s"
            "    if"
            "      i32.const 2"
            "      local.set $len"
            "      i64.const 0"
            "      local.get $n"
            "      i64.sub"
            "      local.set $n"
            "    end"
            "  end"
            "  block $counted"
            "    loop $count"
            "      local.get $n"
            "      i64.const 10"
            "      i64.lt_u"
            "      br_if $counted"
            "      local.get $len"
            "      i32.const 1"
            "      i32.add"
            "      local.set $len"
            "      local.get $n"
            "      i64.const 10"
            "      i64.div_u"
            "      local.set $n"
            "      br $count"
            "    end"
            "  end"
            "  local.get $len"
            ")" ]
      // The `-` first, then the digits from the last back, from the end its length gives.
      Helpers.intTextWrite,
      lines
          [ $"(func ${Helpers.intTextWrite} (param $at i32) (param $n i64) (param $signed i32) (result i32)"
            "  (local $end i32)"
            "  local.get $signed"
            "  if"
            "    local.get $n"
            "    i64.const 0"
            "    i64.lt_s"
            "    if"
            "      local.get $at"
            "      i32.const 45"
            "      i32.store8"
            "      local.get $at"
            "      i32.const 1"
            "      i32.add"
            "      local.set $at"
            "      i64.const 0"
            "      local.get $n"
            "      i64.sub"
            "      local.set $n"
            "    end"
            "  end"
            "  local.get $at"
            "  local.get $n"
            "  i32.const 0"
            $"  call ${Helpers.intTextLength}"
            "  i32.add"
            "  local.tee $end"
            "  local.set $at"
            "  loop $digit"
            "    local.get $at"
            "    i32.const 1"
            "    i32.sub"
            "    local.tee $at"
            "    local.get $n"
            "    i64.const 10"
            "    i64.rem_u"
            "    i32.wrap_i64"
            "    i32.const 48"
            "    i32.add"
            "    i32.store8"
            "    local.get $n"
            "    i64.const 10"
            "    i64.div_u"
            "    local.tee $n"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $digit"
            "  end"
            "  local.get $end"
            ")" ]
      yield! WasmRegions.helpers ]
    |> Map.ofList

/// The WebAssembly helper functions, keyed by name, with the free-list heads at `heapBase` (see `helpersWith`).
let runtimeHelpers (heapBase: int) = helpersWith false heapBase

/// `runtimeHelpers`, with an allocator and reference counts that check themselves (see `MemoryChecks`).
let checkedRuntimeHelpers (heapBase: int) = helpersWith true heapBase

/// Where the string literals start. They run on for as long as they need, and the heap starts after them (see
/// `heapBaseAfter`).
let stringDataBaseOffset = 256

/// How many bytes the block the module sets aside for its host takes (see `nanyx/host.memory`'s `hostState`). A module
/// that uses it has it after its data segment, before the heap, where nothing else reads or writes it, so it is zero
/// when the program starts.
let hostStateSize = 4096

/// Where the newline byte `dbg` writes after its value lives, below the string literals.
let dbgStrNewlineOffset = 224

/// What the helpers that write a float or an `F32` as text call.
let private floatTextDependencies =
    [ Helpers.newObject
      Helpers.copyBytes
      FloatText.bigSet
      FloatText.bigMulSmall
      FloatText.bigMulPow10
      FloatText.bigAdd
      FloatText.bigSub
      FloatText.bigCompare
      FloatText.bigSubMul ]

// The variants of the storing helper `name`, one for each `Storage`, each with what it calls.
let private storingDependencies (name: string) (calls: Storage -> string list) =
    Storage.All |> List.map (fun storage -> Helpers.forStorage name storage, calls storage)

// The runtime helpers the lowered code asked for, closed over what they call themselves.
let helperDependencies =
    [ Helpers.alloc, []
      Helpers.free, []
      Helpers.allocAligned8, [ Helpers.alloc; Helpers.free ]
      Helpers.newCell, [ Helpers.alloc ]
      Helpers.newObject, [ Helpers.alloc ]
      Helpers.release, []
      Helpers.retain, []
      Helpers.copyBytes, []
      Helpers.stringLength, []
      Helpers.stringAlloc, [ Helpers.newObject ]
      Helpers.stringByteAt, [ Helpers.stringLength ]
      Helpers.stringSlice, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.copyBytes ]
      Helpers.stringFromByte, [ Helpers.stringAlloc ]
      Helpers.charToString, [ Helpers.stringAlloc ]
      Helpers.stringFromMemory, [ Helpers.stringAlloc; Helpers.copyBytes ]
      Helpers.stringConcat, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.copyBytes ]
      Helpers.stringEquals, [ Helpers.stringLength ]
      Helpers.writeStderr, [ Helpers.stringLength ]
      Helpers.exit, [ Helpers.procExit ]
      Helpers.crashed, []
      Helpers.deferredCrash, []
      yield! storingDependencies Helpers.listCons (fun _ -> [ Helpers.newCell ])
      yield! storingDependencies Helpers.recordSet (fun _ -> [])
      Helpers.recordCopy, [ Helpers.newObject; Helpers.copyBytes ]
      yield! storingDependencies Helpers.tagBox (fun _ -> [ Helpers.newCell ])
      Helpers.tagCode, []
      Helpers.floatToString, floatTextDependencies
      Helpers.f32ToString, floatTextDependencies
      FloatText.bigSet, []
      FloatText.bigMulSmall, []
      FloatText.bigMulPow10, [ FloatText.bigMulSmall ]
      FloatText.bigAdd, []
      FloatText.bigSub, []
      FloatText.bigCompare, []
      FloatText.bigSubMul, []
      yield! storingDependencies Helpers.listConcat (fun storage -> [ Helpers.forStorage Helpers.listCons storage ])
      Helpers.reuseObject, [ Helpers.newObject ]
      yield! storingDependencies Helpers.listConsAt (fun storage -> [ Helpers.forStorage Helpers.listCons storage ])
      yield! storingDependencies Helpers.tagBoxAt (fun storage -> [ Helpers.forStorage Helpers.tagBox storage ])
      Helpers.floatSqrt, []
      Helpers.floatFloor, []
      Helpers.floatCeil, []
      Helpers.floatTrunc, []
      Helpers.floatToInt, []
      Helpers.intToFloat, []
      Helpers.escapeByte, []
      Helpers.stringQuote, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.escapeByte ]
      Helpers.showSpent, []
      Helpers.showTake, [ Helpers.stringLength ]
      Helpers.intAdd, []
      Helpers.intSub, []
      Helpers.intMul, []
      Helpers.intToIndex, []
      Helpers.stringJoin, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.copyBytes ]
      Helpers.stringShiftBytes, [ Helpers.stringLength; Helpers.stringAlloc ]
      Helpers.intTextLength, []
      Helpers.intTextWrite, [ Helpers.intTextLength ]
      Helpers.stringFromBytes, [ Helpers.stringAlloc; Helpers.copyBytes ]
      Helpers.stringNew, [ Helpers.stringAlloc ]
      Helpers.stringWrite, [ Helpers.copyBytes ]
      Helpers.stringIndexBetween, [ Helpers.stringLength ]
      PowersOfFive.helper, []
      Helpers.floatFromBits, [] ]
    @ (SizedIntegerCode.helpers |> List.map (fun (name, _) -> name, SizedIntegerCode.dependenciesOf name))
    @ (DivisionCode.helpers |> List.map (fun (name, _) -> name, []))
    @ WasmRegions.dependencies

/// The helpers that read and write no linear memory themselves: they compute on their operands, or leave that to the
/// helpers they call (see `helperDependencies`). Every other helper touches it, so a module that uses one (see
/// `closeHelpers`) declares a memory.
let private helpersWithoutMemory =
    set [ yield Helpers.stringSlice
          yield Helpers.stringNew
          yield Helpers.stringConcat
          yield Helpers.exit
          yield Helpers.crashed
          yield Helpers.deferredCrash
          yield FloatText.bigMulPow10
          yield Helpers.floatSqrt
          yield Helpers.floatFloor
          yield Helpers.floatCeil
          yield Helpers.floatTrunc
          yield Helpers.floatToInt
          yield Helpers.intToFloat
          yield Helpers.floatFromBits
          yield Helpers.escapeByte
          yield Helpers.intAdd
          yield Helpers.intSub
          yield Helpers.intMul
          yield Helpers.intToIndex
          yield Helpers.intTextLength
          yield! SizedIntegerCode.helpers |> List.map fst
          yield! DivisionCode.helpers |> List.map fst ]

/// Whether the helper `name` reads or writes linear memory itself.
let touchesMemory (name: string) = not (helpersWithoutMemory.Contains name)

let rec closeHelpers (acc: Set<string>) (pending: string list) =
    match pending with
    | [] -> acc
    | name :: rest when acc.Contains name -> closeHelpers acc rest
    | name :: rest ->
        let deps =
            helperDependencies
            |> List.tryFind (fun (helper, _) -> helper = name)
            |> Option.map snd
            |> Option.defaultValue []
        closeHelpers (acc.Add name) (deps @ rest)

// `$dbg_str` prints its second string after its first, where the `dbg` was called, then a newline, to stderr: what `dbg`
// prints (see `CoreIrLowerDbg`). Each is a write of its own, from the buffer described at address 0: WASI may write only
// the first of several buffers given at once.
let dbgFunctions =
        let write (pointer: string) (length: string) =
            [ "    i32.const 0"
              $"    {pointer}"
              "    i32.store"
              "    i32.const 4"
              $"    {length}"
              "    i32.store"
              "    i32.const 2"
              "    i32.const 0"
              "    i32.const 1"
              "    i32.const 8"
              "    call $fd_write"
              "    drop" ]
        let lengthOf (string: string) = $"local.get {string}\n    i32.const {Header.infoBefore}\n    i32.sub\n    i32.load"
        [ "  (func $dbg_str (param $location i32) (param $text i32)"
          yield! write "local.get $location" (lengthOf "$location")
          yield! write "local.get $text" (lengthOf "$text")
          yield! write $"i32.const {dbgStrNewlineOffset}" "i32.const 1"
          "  )" ]
