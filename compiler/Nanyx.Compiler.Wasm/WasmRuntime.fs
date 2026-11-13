/// The WebAssembly runtime every compiled module draws on: the helper functions the backend emits on demand and
/// where things live in linear memory.
module internal Transpiler.Wasm.WasmRuntime

/// How a value of the program's is written in memory: the WebAssembly value type it is held in, and how many bytes of
/// it are written (see `CoreIrLayout.storage`).
type Storage =
    /// An `i32` in 4 bytes: a pointer, a code, a bool, a boxed float, an `I32` or a `U32`.
    | Word
    /// An `i64` in 8 bytes: an int or a `U64`.
    | Wide
    /// The low byte of an `i32`: an `I8` or a `U8`.
    | Byte
    /// The low 2 bytes of an `i32`: an `I16` or a `U16`.
    | Half

    /// The WebAssembly value type the value is held in, in locals and on the stack.
    member this.ValueType =
        match this with
        | Wide -> "i64"
        | Word
        | Byte
        | Half -> "i32"

    /// How many bytes the value takes in memory.
    member this.Bytes =
        match this with
        | Byte -> 1
        | Half -> 2
        | Word -> 4
        | Wide -> 8

    /// The instruction that writes the value at the address under it on the stack.
    member this.Store =
        match this with
        | Byte -> "i32.store8"
        | Half -> "i32.store16"
        | Word -> "i32.store"
        | Wide -> "i64.store"

    /// An instruction that reads the value's bits back, zero-extended: enough to copy it, not to compute with it, which
    /// `CoreIrLayout.load` is for.
    member this.CopyLoad =
        match this with
        | Byte -> "i32.load8_u"
        | Half -> "i32.load16_u"
        | Word -> "i32.load"
        | Wide -> "i64.load"

    /// Every way a value is stored, each with its own variant of the storing helpers.
    static member All = [ Word; Wide; Byte; Half ]

/// Where a tag cell's payload is: after its code (see the memory model at `runtimeHelpers`).
let tagPayloadOffset = 4

/// The header in front of an object on the heap, before its pointer (see the memory model at `runtimeHelpers`).
module Header =
    /// A list cell's, a tag cell's or a float box's: its count. `newCell` makes it.
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
    let alloc = "__nanyx_alloc"
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
    let floatBox = "__nanyx_float_box"
    let floatToString = "__nanyx_float_to_string"
    /// An `F32` is boxed as a float is, in a block of 4 bytes, and written as text by a helper of its own.
    let f32Box = "__nanyx_f32_box"
    let f32ToString = "__nanyx_f32_to_string"
    /// `%` on floats, which WebAssembly has no instruction for.
    let floatRem = "__nanyx_float_rem"
    let writeStdout = NyxCompiler.Intrinsics.writeStdout
    let writeStderr = NyxCompiler.Intrinsics.writeStderr
    let readStdinByte = NyxCompiler.Intrinsics.readStdinByte
    let exit = NyxCompiler.Intrinsics.exit
    let stringLength = NyxCompiler.Intrinsics.stringLength
    let stringByteAt = NyxCompiler.Intrinsics.stringByteAt
    let stringSlice = NyxCompiler.Intrinsics.stringSlice
    let stringFromByte = NyxCompiler.Intrinsics.stringFromByte
    let listCons = NyxCompiler.Intrinsics.listCons
    let floatSqrt = NyxCompiler.Intrinsics.floatSqrt
    let floatFloor = NyxCompiler.Intrinsics.floatFloor
    let floatCeil = NyxCompiler.Intrinsics.floatCeil
    let floatTrunc = NyxCompiler.Intrinsics.floatTrunc
    let floatToInt = NyxCompiler.Intrinsics.floatToInt
    let intToFloat = NyxCompiler.Intrinsics.intToFloat
    let free = "__nanyx_free"
    let newCell = "__nanyx_new_cell"
    let newObject = "__nanyx_new_object"
    let release = "__nanyx_release"
    let retain = "__nanyx_retain"
    let reuseObject = "__nanyx_reuse_object"
    let listConsAt = "__nanyx_list_cons_at"
    let tagBoxAt = "__nanyx_tag_box_at"
    /// A string in double quotes, escaped as a string literal is written: what `dbg` prints a string as.
    let stringQuote = "__nanyx_string_quote"
    /// The letter after the `\` that escapes a byte in a string literal, or 0 for a byte written as it is.
    let escapeByte = "__nanyx_escape_byte"
    /// WASI's `proc_exit`, imported rather than a helper of its own: asking for it declares the import.
    let procExit = "proc_exit"
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
    /// own, except `listCons`, which takes both over, and `listConcat`, which copies the first list's cells and makes
    /// the second their tail.
    let operandUse (name: string) (index: int) =
        if name = listCons || (name = listConcat && index = 1) then Owned else Borrowed

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

/// The lowest address the table of free-list heads starts at. A module whose string literals end past it puts the table
/// after them instead (see `heapBaseAfter`).
let minimumHeapBase = 4096

/// Where the table of free-list heads starts in a module whose data segments end at `dataEnd`: at `minimumHeapBase`,
/// or past the data, 8-byte aligned so that the blocks `alloc` hands out stay 8-byte aligned.
let heapBaseAfter (dataEnd: int) = max minimumHeapBase ((dataEnd + 7) &&& ~~~7)

/// The largest block size, in bytes, with a free list of its own; larger blocks share one list.
let largestSizeClass = 32

/// Where `$heap_ptr` starts, given where the table of free-list heads starts (`heapBase`): after the table, one word
/// per small size and one for the large blocks.
let heapStartOffset (heapBase: int) = heapBase + largestSizeClass + 4

/// The pages of memory a module starts with: enough to hold everything below `heapStart`, and at least one.
let initialMemoryPages (heapStart: int) = max 1 ((heapStart + 65535) / 65536)

/// What a module compiled with memory checks adds (see `checkedRuntimeHelpers`): two counters, and the functions that
/// stop the program when a block is freed twice or an object is counted after it was freed. They write what went
/// wrong to stderr and exit through WASI `proc_exit` rather than trap, so that a host sees an exit code, not a
/// hardware exception.
module MemoryChecks =
    /// The global counting the blocks `alloc` handed out that `free` has not taken back.
    let liveBlocks = "live_blocks"
    /// The global counting every call to `alloc`.
    let allocations = "allocations"
    let failDoubleFree = "__nanyx_fail_double_free"
    let failUseAfterFree = "__nanyx_fail_use_after_free"
    let checkLive = "__nanyx_check_live"
    let doubleFreeMessage = "Memory check failed: a block was freed twice.\n"
    let useAfterFreeMessage = "Memory check failed: an object was counted after it was freed.\n"
    /// The string literals the checks write, which a checked module lays out with its own.
    let messages = [ doubleFreeMessage; useAfterFreeMessage ]
    /// The exit code a failed check stops the program with (`EX_SOFTWARE`, an internal error).
    let exitCode = 70
    /// What `free` writes over every word of a block but the first (which links the free list), so that reading a
    /// field of a freed object gives a pointer far outside memory: following it traps.
    let poison = 0xDEADBEEF
    /// The bit `free` sets in a block's size word, and `alloc` clears: sizes are multiples of 4, so it is never part
    /// of one.
    let freedMark = 1

let private lines (items: string list) = items |> List.map (fun line -> "  " + line) |> String.concat "\n"

// Leaves on the stack the address of the head of the free list for a block of `$size` bytes, in the table at
// `heapBase`.
let private freeListHead (heapBase: int) =
    [ "  local.get $size"
      $"  i32.const {largestSizeClass}"
      "  i32.le_u"
      "  if (result i32)"
      $"    i32.const {heapBase - 4}"
      "    local.get $size"
      "    i32.add"
      "  else"
      $"    i32.const {heapBase + largestSizeClass}"
      "  end" ]

// Adds `delta` to the global `name`.
let private bump (name: string) (delta: int) =
    [ $"  global.get ${name}"; $"  i32.const {delta}"; "  i32.add"; $"  global.set ${name}" ]

/// `alloc`, with the free-list heads at `heapBase`. With `checks`, it counts the call and the block it hands out, and
/// clears the freed mark of a block it takes from a free list.
let private allocHelper (checks: bool) (heapBase: int) =
    lines
        [ yield $"(func ${Helpers.alloc} (param $size i32) (result i32)"
          yield "  (local $link i32)"
          yield "  (local $ptr i32)"
          if checks then
              yield! bump MemoryChecks.allocations 1
              yield! bump MemoryChecks.liveBlocks 1
          yield "  local.get $size"
          yield "  i32.const 3"
          yield "  i32.add"
          yield "  i32.const -4"
          yield "  i32.and"
          yield "  local.tee $size"
          yield "  i32.eqz"
          yield "  if"
          yield "    i32.const 4"
          yield "    local.set $size"
          yield "  end"
          // Walk the free list from its head, taking the first block that is big enough: on a small size's
          // list that is the first block. A freed mark makes a size odd, which leaves the comparison unchanged.
          yield! freeListHead heapBase
          yield "  local.set $link"
          yield "  block $empty"
          yield "    loop $search"
          yield "      local.get $link"
          yield "      i32.load"
          yield "      local.tee $ptr"
          yield "      i32.eqz"
          yield "      br_if $empty"
          yield "      local.get $ptr"
          yield "      i32.const 4"
          yield "      i32.sub"
          yield "      i32.load"
          yield "      local.get $size"
          yield "      i32.ge_u"
          yield "      if"
          yield "        local.get $link"
          yield "        local.get $ptr"
          yield "        i32.load"
          yield "        i32.store"
          if checks then
              yield "        local.get $ptr"
              yield "        i32.const 4"
              yield "        i32.sub"
              yield "        local.get $ptr"
              yield "        i32.const 4"
              yield "        i32.sub"
              yield "        i32.load"
              yield $"        i32.const {~~~MemoryChecks.freedMark}"
              yield "        i32.and"
              yield "        i32.store"
          yield "        local.get $ptr"
          yield "        return"
          yield "      end"
          yield "      local.get $ptr"
          yield "      local.set $link"
          yield "      br $search"
          yield "    end"
          yield "  end"
          yield "  global.get $heap_ptr"
          yield "  i32.const 4"
          yield "  i32.add"
          yield "  local.tee $ptr"
          yield "  local.get $size"
          yield "  i32.add"
          yield "  global.set $heap_ptr"
          yield "  block $fits"
          yield "    loop $grow"
          yield "      global.get $heap_ptr"
          yield "      memory.size"
          yield "      i32.const 65536"
          yield "      i32.mul"
          yield "      i32.le_u"
          yield "      br_if $fits"
          yield "      i32.const 1"
          yield "      memory.grow"
          yield "      i32.const -1"
          yield "      i32.eq"
          yield "      if"
          yield "        unreachable"
          yield "      end"
          yield "      br $grow"
          yield "    end"
          yield "  end"
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  local.get $size"
          yield "  i32.store"
          yield "  local.get $ptr"
          yield ")" ]

/// `free`, with the free-list heads at `heapBase`. With `checks`, it stops the program on a block that is already free,
/// fills the block with `MemoryChecks.poison`, marks it freed, and counts it.
let private freeHelper (checks: bool) (heapBase: int) =
    lines
        [ yield $"(func ${Helpers.free} (param $ptr i32)"
          yield "  (local $size i32)"
          yield "  (local $head i32)"
          if checks then
              yield "  (local $offset i32)"
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  i32.load"
          yield "  local.set $size"
          if checks then
              yield "  local.get $size"
              yield $"  i32.const {MemoryChecks.freedMark}"
              yield "  i32.and"
              yield "  if"
              yield $"    call ${MemoryChecks.failDoubleFree}"
              yield "  end"
              yield "  i32.const 4"
              yield "  local.set $offset"
              yield "  block $poisoned"
              yield "    loop $poison"
              yield "      local.get $offset"
              yield "      local.get $size"
              yield "      i32.ge_u"
              yield "      br_if $poisoned"
              yield "      local.get $ptr"
              yield "      local.get $offset"
              yield "      i32.add"
              yield $"      i32.const {MemoryChecks.poison}"
              yield "      i32.store"
              yield "      local.get $offset"
              yield "      i32.const 4"
              yield "      i32.add"
              yield "      local.set $offset"
              yield "      br $poison"
              yield "    end"
              yield "  end"
              yield "  local.get $ptr"
              yield "  i32.const 4"
              yield "  i32.sub"
              yield "  local.get $size"
              yield $"  i32.const {MemoryChecks.freedMark}"
              yield "  i32.or"
              yield "  i32.store"
              yield! bump MemoryChecks.liveBlocks -1
          yield! freeListHead heapBase
          yield "  local.set $head"
          yield "  local.get $ptr"
          yield "  local.get $head"
          yield "  i32.load"
          yield "  i32.store"
          yield "  local.get $head"
          yield "  local.get $ptr"
          yield "  i32.store"
          yield ")" ]

// Gives up one reference to the object whose count is at `$count`: 1 when it was the last, and the caller then
// drops the object's fields and frees it; 0 otherwise. An object whose count is 0 is never freed. With `checks`, it
// first stops the program on an object that has been freed.
let private releaseHelper (checks: bool) =
    lines
        [ yield $"(func ${Helpers.release} (param $count i32) (result i32)"
          yield "  (local $n i32)"
          if checks then
              yield "  local.get $count"
              yield $"  call ${MemoryChecks.checkLive}"
          yield "  local.get $count"
          yield "  i32.load"
          yield "  local.tee $n"
          yield "  i32.eqz"
          yield "  if"
          yield "    i32.const 0"
          yield "    return"
          yield "  end"
          yield "  local.get $n"
          yield "  i32.const 1"
          yield "  i32.eq"
          yield "  if"
          yield "    i32.const 1"
          yield "    return"
          yield "  end"
          yield "  local.get $count"
          yield "  local.get $n"
          yield "  i32.const 1"
          yield "  i32.sub"
          yield "  i32.store"
          yield "  i32.const 0"
          yield ")" ]

// Takes one more reference to the object whose count is at `$count`, unless it is never freed (a count of 0). With
// `checks`, it first stops the program on an object that has been freed.
let private retainHelper (checks: bool) =
    lines
        [ yield $"(func ${Helpers.retain} (param $count i32)"
          yield "  (local $n i32)"
          if checks then
              yield "  local.get $count"
              yield $"  call ${MemoryChecks.checkLive}"
          yield "  local.get $count"
          yield "  i32.load"
          yield "  local.tee $n"
          yield "  if"
          yield "    local.get $count"
          yield "    local.get $n"
          yield "    i32.const 1"
          yield "    i32.add"
          yield "    i32.store"
          yield "  end"
          yield ")" ]

/// The globals and functions the checked `alloc`, `free`, `retain` and `release` use, given where the free-list heads
/// are (`heapBase`) and where each of `MemoryChecks.messages` is. They call `writeStderr` and WASI `proc_exit`, which
/// the module must have. `checkLive`
/// stops the program when the object whose count is at `$count` is on the heap and its block is marked freed: an
/// object's count is the first word of its block, so its size word is the one before. A string literal is below the
/// heap and has no size word.
let memoryCheckFunctions (heapBase: int) (messageOffset: string -> int) =
    let fail (name: string) (message: string) =
        lines
            [ $"(func ${name}"
              $"  i32.const {messageOffset message}"
              $"  call ${Helpers.writeStderr}"
              $"  i32.const {MemoryChecks.exitCode}"
              $"  call ${Helpers.procExit}"
              ")" ]
    [ $"  (global ${MemoryChecks.liveBlocks} (mut i32) (i32.const 0))"
      $"  (global ${MemoryChecks.allocations} (mut i32) (i32.const 0))"
      fail MemoryChecks.failDoubleFree MemoryChecks.doubleFreeMessage
      fail MemoryChecks.failUseAfterFree MemoryChecks.useAfterFreeMessage
      lines
          [ $"(func ${MemoryChecks.checkLive} (param $count i32)"
            "  local.get $count"
            $"  i32.const {heapStartOffset heapBase}"
            "  i32.ge_u"
            "  if"
            "    local.get $count"
            "    i32.const 4"
            "    i32.sub"
            "    i32.load"
            $"    i32.const {MemoryChecks.freedMark}"
            "    i32.and"
            "    if"
            $"      call ${MemoryChecks.failUseAfterFree}"
            "    end"
            "  end"
            ")" ] ]
    |> String.concat "\n"

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
let private storingHelpers (storage: Storage) =
    let bytes = storage.Bytes
    let valueType = storage.ValueType
    let name helper = Helpers.forStorage helper storage
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
/// (code, payload). An int takes 8 bytes in a field, a head or a payload, and every other value 4 (see
/// `CoreIrLayout`).
///
/// Every object on the heap has a header: its reference count, then its info word if it has one (a string's length,
/// a record's layout, a closure's drop function). So the count is at `p - 8` for strings, records and closures, made
/// by `newObject`, and at `p - 4` for list cells, tag cells and float boxes, made by `newCell`. A count of 0 marks
/// an object that is never freed: a string literal in the data segment. `retain` takes a reference and `release`
/// gives one up. The header's sizes are in `Header`. A helper borrows its operands and gives back a value of its own,
/// which reference counting relies on (see `CoreIrCounting.operands`), except those `Helpers.operandUse` names.
///
/// The allocator hands out 4-byte-aligned blocks, so a pointer is never odd. Every block has its size (the
/// requested size rounded up to 4 bytes, and at least 4) in the word before it, and `free` puts a block back on the
/// free list for its size: blocks of up to 32 bytes have a list per size, larger ones share one list searched
/// first-fit. The list heads are a table at `heapBase`, past the string literals (see `heapBaseAfter` and
/// `freeListHead`), and a free block keeps the next block of its list in its first word. `alloc` takes from a list
/// when it can, and otherwise bumps `$heap_ptr`, which starts after the table, growing the memory when it runs out.
///
/// With `checks`, `alloc`, `free`, `retain` and `release` also count blocks and stop the program on a double free or a
/// use after free, drawing on `memoryCheckFunctions`.
let private helpersWith (checks: bool) (heapBase: int) : Map<string, string> =
    [ Helpers.alloc, allocHelper checks heapBase
      Helpers.free, freeHelper checks heapBase
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
            "  local.get $block"
            "  local.get $info"
            $"  i32.store offset={Header.infoOffset}"
            "  local.get $block"
            $"  i32.const {Header.withInfo}"
            "  i32.add"
            ")" ]
      for storage in Storage.All do
          yield! storingHelpers storage
      yield! SizedIntegerCode.helpers
      Helpers.retain, retainHelper checks
      Helpers.copyBytes,
      lines
          [ $"(func ${Helpers.copyBytes} (param $dst i32) (param $src i32) (param $len i32)"
            "  (local $i i32)"
            "  block $done"
            "    loop $copy"
            "      local.get $i"
            "      local.get $len"
            "      i32.ge_s"
            "      br_if $done"
            "      local.get $dst"
            "      local.get $i"
            "      i32.add"
            "      local.get $src"
            "      local.get $i"
            "      i32.add"
            "      i32.load8_u"
            "      i32.store8"
            "      local.get $i"
            "      i32.const 1"
            "      i32.add"
            "      local.set $i"
            "      br $copy"
            "    end"
            "  end"
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
      // Writes a string to stdout (fd 1) with the iovec at 0 and nwritten at 8, like the dbg helpers.
      Helpers.writeStdout,
      lines
          [ $"(func ${Helpers.writeStdout} (param $s i32)"
            "  i32.const 0"
            "  local.get $s"
            "  i32.store"
            "  i32.const 4"
            "  local.get $s"
            $"  call ${Helpers.stringLength}"
            "  i32.store"
            "  i32.const 1"
            "  i32.const 0"
            "  i32.const 1"
            "  i32.const 8"
            "  call $fd_write"
            "  drop"
            ")" ]
      // The same as writing to stdout, on stderr (fd 2).
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
      // Reads one byte of stdin (fd 0) into offset 12 through the iovec at 0, with the count read at 8. Gives
      // back -1 when nothing was read (the end of the input) or the read failed. A call `f()` to an intrinsic
      // passes no argument for the unit, so it takes none.
      Helpers.readStdinByte,
      lines
          [ $"(func ${Helpers.readStdinByte} (result i32)"
            "  i32.const 0"
            "  i32.const 12"
            "  i32.store"
            "  i32.const 4"
            "  i32.const 1"
            "  i32.store"
            "  i32.const 0"
            "  i32.const 0"
            "  i32.const 1"
            "  i32.const 8"
            "  call $fd_read"
            "  if (result i32)"
            "    i32.const -1"
            "  else"
            "    i32.const 8"
            "    i32.load"
            "    if (result i32)"
            "      i32.const 12"
            "      i32.load8_u"
            "    else"
            "      i32.const -1"
            "    end"
            "  end"
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
      // A float is boxed: an 8-byte block holding the f64, passed around by pointer.
      Helpers.floatBox,
      lines
          [ $"(func ${Helpers.floatBox} (param $value f64) (result i32)"
            "  (local $box i32)"
            "  i32.const 8"
            $"  call ${Helpers.newCell}"
            "  local.set $box"
            "  local.get $box"
            "  local.get $value"
            "  f64.store"
            "  local.get $box"
            ")" ]
      Helpers.f32Box,
      lines
          [ $"(func ${Helpers.f32Box} (param $value f32) (result i32)"
            "  (local $box i32)"
            "  i32.const 4"
            $"  call ${Helpers.newCell}"
            "  local.set $box"
            "  local.get $box"
            "  local.get $value"
            "  f32.store"
            "  local.get $box"
            ")" ]
      // Formats a boxed float, or a boxed `F32`, as the shortest decimal that reads back as the same value (see
      // `FloatText`).
      yield!
          FloatText.helpers
              Helpers.floatToString
              Helpers.f32ToString
              { NewObject = Helpers.newObject
                Alloc = Helpers.alloc
                Free = Helpers.free
                CopyBytes = Helpers.copyBytes }
          |> List.map (fun (name, body) -> name, lines body)
      // The float intrinsics that are one WebAssembly instruction on the unboxed value, boxed again.
      yield!
          [ Helpers.floatSqrt, "f64.sqrt"
            Helpers.floatFloor, "f64.floor"
            Helpers.floatCeil, "f64.ceil"
            Helpers.floatTrunc, "f64.trunc" ]
          |> List.map (fun (name, instruction) ->
              name,
              lines
                  [ $"(func ${name} (param $box i32) (result i32)"
                    "  local.get $box"
                    "  f64.load"
                    $"  {instruction}"
                    $"  call ${Helpers.floatBox}"
                    ")" ])
      // A boxed float as an int: `i64.trunc_f64_s` traps on NaN, an infinity or a value out of the range of `int`.
      Helpers.floatToInt,
      lines
          [ $"(func ${Helpers.floatToInt} (param $box i32) (result i64)"
            "  local.get $box"
            "  f64.load"
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
      // An int as a boxed float: `f64.convert_i64_s` rounds to the nearest float, and is exact up to 2^53.
      Helpers.intToFloat,
      lines
          [ $"(func ${Helpers.intToFloat} (param $value i64) (result i32)"
            "  local.get $value"
            "  f64.convert_i64_s"
            $"  call ${Helpers.floatBox}"
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
      // The truncated remainder, C's `fmod`, on unboxed floats: `a - trunc(a / b) * b`, with the sign of `a`. When `b`
      // is infinite the quotient is zero and `0 * b` NaN, so the remainder is `a` itself (NaN when `a` is infinite).
      Helpers.floatRem,
      lines
          [ $"(func ${Helpers.floatRem} (param $a f64) (param $b f64) (result f64)"
            "  local.get $b"
            "  f64.abs"
            "  f64.const inf"
            "  f64.eq"
            "  if (result f64)"
            "    local.get $a"
            "    local.get $a"
            "    local.get $a"
            "    f64.sub"
            "    f64.add"
            "  else"
            "    local.get $a"
            "    local.get $a"
            "    local.get $b"
            "    f64.div"
            "    f64.trunc"
            "    local.get $b"
            "    f64.mul"
            "    f64.sub"
            "  end"
            "  local.get $a"
            "  f64.copysign"
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
            ")" ] ]
    |> Map.ofList

/// The WebAssembly helper functions, keyed by name, with the free-list heads at `heapBase` (see `helpersWith`).
let runtimeHelpers (heapBase: int) = helpersWith false heapBase

/// `runtimeHelpers`, with an allocator and reference counts that check themselves (see `MemoryChecks`).
let checkedRuntimeHelpers (heapBase: int) = helpersWith true heapBase

/// Where the string literals start. They run on for as long as they need, and the heap starts after them (see
/// `heapBaseAfter`).
let stringDataBaseOffset = 256

let dbgDataBaseOffset = 128

let dbgStrNewlineOffset = dbgDataBaseOffset + 96  // the newline byte, at 224

/// What the helpers that write a float or an `F32` as text call.
let private floatTextDependencies =
    [ Helpers.newObject
      Helpers.alloc
      Helpers.free
      Helpers.copyBytes
      FloatText.bigSet
      FloatText.bigMulSmall
      FloatText.bigMulPow10
      FloatText.bigAdd
      FloatText.bigSub
      FloatText.bigCompare ]

// The variants of the storing helper `name`, one for each `Storage`, each with what it calls.
let private storingDependencies (name: string) (calls: Storage -> string list) =
    Storage.All |> List.map (fun storage -> Helpers.forStorage name storage, calls storage)

// The runtime helpers the lowered code asked for, closed over what they call themselves.
let helperDependencies =
    [ Helpers.alloc, []
      Helpers.free, []
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
      Helpers.stringConcat, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.copyBytes ]
      Helpers.stringEquals, [ Helpers.stringLength ]
      Helpers.writeStdout, [ Helpers.stringLength ]
      Helpers.writeStderr, [ Helpers.stringLength ]
      Helpers.readStdinByte, []
      Helpers.exit, [ Helpers.procExit ]
      yield! storingDependencies Helpers.listCons (fun _ -> [ Helpers.newCell ])
      yield! storingDependencies Helpers.recordSet (fun _ -> [])
      Helpers.recordCopy, [ Helpers.newObject; Helpers.copyBytes ]
      yield! storingDependencies Helpers.tagBox (fun _ -> [ Helpers.newCell ])
      Helpers.tagCode, []
      Helpers.floatBox, [ Helpers.newCell ]
      Helpers.floatToString, floatTextDependencies
      Helpers.f32Box, [ Helpers.newCell ]
      Helpers.f32ToString, floatTextDependencies
      FloatText.bigSet, []
      FloatText.bigMulSmall, []
      FloatText.bigMulPow10, [ FloatText.bigMulSmall ]
      FloatText.bigAdd, []
      FloatText.bigSub, []
      FloatText.bigCompare, []
      yield! storingDependencies Helpers.listConcat (fun storage -> [ Helpers.forStorage Helpers.listCons storage ])
      Helpers.reuseObject, [ Helpers.newObject ]
      yield! storingDependencies Helpers.listConsAt (fun storage -> [ Helpers.forStorage Helpers.listCons storage ])
      yield! storingDependencies Helpers.tagBoxAt (fun storage -> [ Helpers.forStorage Helpers.tagBox storage ])
      Helpers.floatSqrt, [ Helpers.floatBox ]
      Helpers.floatFloor, [ Helpers.floatBox ]
      Helpers.floatCeil, [ Helpers.floatBox ]
      Helpers.floatTrunc, [ Helpers.floatBox ]
      Helpers.floatToInt, []
      Helpers.intToFloat, [ Helpers.floatBox ]
      Helpers.floatRem, []
      Helpers.escapeByte, []
      Helpers.stringQuote, [ Helpers.stringLength; Helpers.stringAlloc; Helpers.escapeByte ]
      Helpers.intAdd, []
      Helpers.intSub, []
      Helpers.intMul, []
      Helpers.intToIndex, [] ]
    @ (SizedIntegerCode.helpers |> List.map (fun (name, _) -> name, []))

/// The helpers that read and write no linear memory themselves: they compute on their operands, or leave that to the
/// helpers they call (see `helperDependencies`). Every other helper touches it, so a module that uses one (see
/// `closeHelpers`) declares a memory.
let private helpersWithoutMemory =
    set [ yield Helpers.stringSlice
          yield Helpers.stringConcat
          yield Helpers.exit
          yield FloatText.bigMulPow10
          yield Helpers.intToFloat
          yield Helpers.floatRem
          yield Helpers.escapeByte
          yield Helpers.intAdd
          yield Helpers.intSub
          yield Helpers.intMul
          yield Helpers.intToIndex
          yield! SizedIntegerCode.helpers |> List.map fst ]

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

// `$dbg_str` prints a string after "dbg: ", then a newline: what `dbg` prints (see `CoreIrLowerDbg`).
let dbgFunctions =
        [ "  (func $dbg_str (param $ptr i32)"
          "    (local $len i32)"
          "    local.get $ptr"
          $"    i32.const {Header.infoBefore}"
          "    i32.sub"
          "    i32.load"
          "    local.set $len"
          "    i32.const 0"
          $"    i32.const {dbgDataBaseOffset}"
          "    i32.store"
          "    i32.const 4"
          "    i32.const 5"
          "    i32.store"
          "    i32.const 1"
          "    i32.const 0"
          "    i32.const 1"
          "    i32.const 8"
          "    call $fd_write"
          "    drop"
          "    i32.const 0"
          "    local.get $ptr"
          "    i32.store"
          "    i32.const 4"
          "    local.get $len"
          "    i32.store"
          "    i32.const 1"
          "    i32.const 0"
          "    i32.const 1"
          "    i32.const 8"
          "    call $fd_write"
          "    drop"
          "    i32.const 0"
          $"    i32.const {dbgStrNewlineOffset}"
          "    i32.store"
          "    i32.const 4"
          "    i32.const 1"
          "    i32.store"
          "    i32.const 1"
          "    i32.const 0"
          "    i32.const 1"
          "    i32.const 8"
          "    call $fd_write"
          "    drop"
          "  )" ]
