/// The WebAssembly runtime every compiled module draws on: the helper functions the backend emits on demand and
/// where things live in linear memory.
module internal Transpiler.Wasm.WasmRuntime

/// The names of the WebAssembly helper functions the backend emits on demand (see `runtimeHelpers`).
module Helpers =
    let alloc = "__nanyx_alloc"
    let copyBytes = "__nanyx_copy_bytes"
    let stringAlloc = "__nanyx_string_alloc"
    let stringConcat = "__nanyx_string_concat"
    let stringEquals = "__nanyx_string_equals"
    let listLength = "__nanyx_list_length"
    let listConcat = "__nanyx_list_concat"
    let recordSet = "__nanyx_record_set"
    let recordCopy = "__nanyx_record_copy"
    let tagBox = "__nanyx_tag_box"
    let tagCode = "__nanyx_tag_code"
    /// Not a helper: `==` on a record, a list or tags, which calls the equality function of its operands' type (see
    /// `CoreIrEquality`).
    let valueEquals = "__nanyx_equals"
    let floatBox = "__nanyx_float_box"
    let floatToString = "__nanyx_float_to_string"
    let writeStdout = NyxCompiler.Intrinsics.writeStdout
    let writeStderr = NyxCompiler.Intrinsics.writeStderr
    let readStdinByte = NyxCompiler.Intrinsics.readStdinByte
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
    /// WASI's `proc_exit`, imported rather than a helper of its own: asking for it declares the import.
    let procExit = "proc_exit"

let heapBaseOffset = 4096  // Start heap after string data region

/// The largest block size, in bytes, with a free list of its own; larger blocks share one list.
let largestSizeClass = 32

/// Where `$heap_ptr` starts: after the table of free-list heads, one word per small size and one for the large
/// blocks.
let heapStartOffset = heapBaseOffset + largestSizeClass + 4

/// The WebAssembly helper functions, keyed by name. The ones named after a compiler intrinsic implement
/// that intrinsic; the rest back language operators (`+` and `==` on strings, list literals and patterns)
/// and the allocator they share. Only the helpers a module uses are emitted.
///
/// Memory model: a string is a pointer to its UTF-8 bytes, with its byte length in the 4 bytes before the
/// pointer and a 0 byte after; a list is 0 (empty) or a pointer to an 8-byte cell of (head, tail); a record (or
/// tuple) is a pointer to a 4-byte slot per field in field-name order, with its layout's number (see
/// `RecordTable`) in the 4 bytes before the pointer. A tag without a payload is its code (see `tagCode`), which
/// is odd; a tag with one is a pointer to an 8-byte cell of (code, payload).
///
/// Every object on the heap has a header: its reference count, then its info word if it has one (a string's length,
/// a record's layout, a closure's drop function). So the count is at `p - 8` for strings, records and closures, made
/// by `newObject`, and at `p - 4` for list cells, tag cells and float boxes, made by `newCell`. A count of 0 marks
/// an object that is never freed: a string literal in the data segment. `retain` takes a reference and `release`
/// gives one up. A helper borrows its operands and gives back a value of its own, which reference counting relies on
/// (see `CoreIrCounting.operands`): only `listCons` takes its operands over, and `listConcat` its second.
///
/// The allocator hands out 4-byte-aligned blocks, so a pointer is never odd. Every block has its size (the
/// requested size rounded up to 4 bytes, and at least 4) in the word before it, and `free` puts a block back on the
/// free list for its size: blocks of up to 32 bytes have a list per size, larger ones share one list searched
/// first-fit. The list heads are a table at `heapBaseOffset` (see `freeListHead`), and a free block keeps the next
/// block of its list in its first word. `alloc` takes from a list when it can, and otherwise bumps `$heap_ptr`,
/// growing the memory when it runs out.
let runtimeHelpers : Map<string, string> =
    let lines (items: string list) = items |> List.map (fun line -> "  " + line) |> String.concat "\n"
    // Leaves on the stack the address of the head of the free list for a block of `$size` bytes.
    let freeListHead =
        [ "  local.get $size"
          $"  i32.const {largestSizeClass}"
          "  i32.le_u"
          "  if (result i32)"
          $"    i32.const {heapBaseOffset - 4}"
          "    local.get $size"
          "    i32.add"
          "  else"
          $"    i32.const {heapBaseOffset + largestSizeClass}"
          "  end" ]
    [ Helpers.alloc,
      lines
          [ yield $"(func ${Helpers.alloc} (param $size i32) (result i32)"
            yield "  (local $link i32)"
            yield "  (local $ptr i32)"
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
            // list that is the first block.
            yield! freeListHead
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
      Helpers.free,
      lines
          [ yield $"(func ${Helpers.free} (param $ptr i32)"
            yield "  (local $size i32)"
            yield "  (local $head i32)"
            yield "  local.get $ptr"
            yield "  i32.const 4"
            yield "  i32.sub"
            yield "  i32.load"
            yield "  local.set $size"
            yield! freeListHead
            yield "  local.set $head"
            yield "  local.get $ptr"
            yield "  local.get $head"
            yield "  i32.load"
            yield "  i32.store"
            yield "  local.get $head"
            yield "  local.get $ptr"
            yield "  i32.store"
            yield ")" ]
      // An object of `$size` bytes with a count of one and no info word: `size -> p`.
      Helpers.newCell,
      lines
          [ $"(func ${Helpers.newCell} (param $size i32) (result i32)"
            "  (local $block i32)"
            "  local.get $size"
            "  i32.const 4"
            "  i32.add"
            $"  call ${Helpers.alloc}"
            "  local.tee $block"
            "  i32.const 1"
            "  i32.store"
            "  local.get $block"
            "  i32.const 4"
            "  i32.add"
            ")" ]
      // An object of `$size` bytes with a count of one and the info word `$info`: `size info -> p`.
      Helpers.newObject,
      lines
          [ $"(func ${Helpers.newObject} (param $size i32) (param $info i32) (result i32)"
            "  (local $block i32)"
            "  local.get $size"
            "  i32.const 8"
            "  i32.add"
            $"  call ${Helpers.alloc}"
            "  local.tee $block"
            "  i32.const 1"
            "  i32.store"
            "  local.get $block"
            "  local.get $info"
            "  i32.store offset=4"
            "  local.get $block"
            "  i32.const 8"
            "  i32.add"
            ")" ]
      // Gives up one reference to the object whose count is at `$count`: 1 when it was the last, and the caller then
      // drops the object's fields and frees it; 0 otherwise. An object whose count is 0 is never freed.
      Helpers.release,
      lines
          [ $"(func ${Helpers.release} (param $count i32) (result i32)"
            "  (local $n i32)"
            "  local.get $count"
            "  i32.load"
            "  local.tee $n"
            "  i32.eqz"
            "  if"
            "    i32.const 0"
            "    return"
            "  end"
            "  local.get $n"
            "  i32.const 1"
            "  i32.eq"
            "  if"
            "    i32.const 1"
            "    return"
            "  end"
            "  local.get $count"
            "  local.get $n"
            "  i32.const 1"
            "  i32.sub"
            "  i32.store"
            "  i32.const 0"
            ")" ]
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
            "  i32.store offset=4"
            "  local.get $block"
            "  i32.const 8"
            "  i32.add"
            ")" ]
      // `listCons` into the block of a reuse token, when it is not 0: `block head tail -> cell`.
      Helpers.listConsAt,
      lines
          [ $"(func ${Helpers.listConsAt} (param $block i32) (param $head i32) (param $tail i32) (result i32)"
            "  (local $cell i32)"
            "  local.get $block"
            "  i32.eqz"
            "  if"
            "    local.get $head"
            "    local.get $tail"
            $"    call ${Helpers.listCons}"
            "    return"
            "  end"
            "  local.get $block"
            "  i32.const 4"
            "  i32.add"
            "  local.tee $cell"
            "  local.get $head"
            "  i32.store"
            "  local.get $cell"
            "  local.get $tail"
            "  i32.store offset=4"
            "  local.get $cell"
            ")" ]
      // `tagBox` into the block of a reuse token, when it is not 0: `block code payload -> cell`.
      Helpers.tagBoxAt,
      lines
          [ $"(func ${Helpers.tagBoxAt} (param $block i32) (param $code i32) (param $payload i32) (result i32)"
            "  (local $cell i32)"
            "  local.get $block"
            "  i32.eqz"
            "  if"
            "    local.get $code"
            "    local.get $payload"
            $"    call ${Helpers.tagBox}"
            "    return"
            "  end"
            "  local.get $block"
            "  i32.const 4"
            "  i32.add"
            "  local.tee $cell"
            "  local.get $code"
            "  i32.store"
            "  local.get $cell"
            "  local.get $payload"
            "  i32.store offset=4"
            "  local.get $cell"
            ")" ]
      // Takes one more reference to the object whose count is at `$count`, unless it is never freed (a count of 0).
      Helpers.retain,
      lines
          [ $"(func ${Helpers.retain} (param $count i32)"
            "  (local $n i32)"
            "  local.get $count"
            "  i32.load"
            "  local.tee $n"
            "  if"
            "    local.get $count"
            "    local.get $n"
            "    i32.const 1"
            "    i32.add"
            "    i32.store"
            "  end"
            ")" ]
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
            "  i32.const 4"
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
      // Stores one field of a freshly allocated record and hands the record pointer back, so a record
      // literal is built entirely on the operand stack: `base value offset -> base`. That keeps nested
      // literals safe, since an inner allocation can never disturb an outer one held on the stack.
      Helpers.recordSet,
      lines
          [ $"(func ${Helpers.recordSet} (param $base i32) (param $value i32) (param $offset i32) (result i32)"
            "  local.get $base"
            "  local.get $offset"
            "  i32.add"
            "  local.get $value"
            "  i32.store"
            "  local.get $base"
            ")" ]
      // A new record with the layout and the `$size` bytes of fields of the record `$src`: `src size -> record`.
      Helpers.recordCopy,
      lines
          [ $"(func ${Helpers.recordCopy} (param $src i32) (param $size i32) (result i32)"
            "  (local $dst i32)"
            "  local.get $size"
            "  local.get $src"
            "  i32.const 4"
            "  i32.sub"
            "  i32.load"
            $"  call ${Helpers.newObject}"
            "  local.tee $dst"
            "  local.get $src"
            "  local.get $size"
            $"  call ${Helpers.copyBytes}"
            "  local.get $dst"
            ")" ]
      // A tag with a payload: `code payload -> cell`.
      Helpers.tagBox,
      lines
          [ $"(func ${Helpers.tagBox} (param $code i32) (param $payload i32) (result i32)"
            "  (local $cell i32)"
            "  i32.const 8"
            $"  call ${Helpers.newCell}"
            "  local.tee $cell"
            "  local.get $code"
            "  i32.store"
            "  local.get $cell"
            "  local.get $payload"
            "  i32.store offset=4"
            "  local.get $cell"
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
      // A float is boxed: an 8-byte block holding the f64, passed around by pointer like every other
      // value, so locals, record slots and calls stay 32-bit.
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
      // Formats a boxed float as `-?int.frac` with the fraction rounded to six digits and trailing zeros
      // dropped (keeping one): 6.28, 4.0, -1.5, 0.333333.
      Helpers.floatToString,
      lines
          [ $"(func ${Helpers.floatToString} (param $box i32) (result i32)"
            "  (local $x f64)"
            "  (local $base i32)"
            "  (local $cursor i32)"
            "  (local $int i64)"
            "  (local $frac i64)"
            "  (local $tmp i64)"
            "  (local $count i32)"
            "  (local $pos i32)"
            "  (local $scale i64)"
            "  local.get $box"
            "  f64.load"
            "  local.set $x"
            "  ;; a string of a sign, up to 20 integer digits, the point, 6 digits and a terminator; $base is its length"
            "  i32.const 32"
            "  i32.const 0"
            $"  call ${Helpers.newObject}"
            "  i32.const 4"
            "  i32.sub"
            "  local.set $base"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            "  local.set $cursor"
            "  local.get $x"
            "  f64.const 0"
            "  f64.lt"
            "  if"
            "    local.get $cursor"
            "    i32.const 45"
            "    i32.store8"
            "    local.get $cursor"
            "    i32.const 1"
            "    i32.add"
            "    local.set $cursor"
            "    local.get $x"
            "    f64.neg"
            "    local.set $x"
            "  end"
            "  local.get $x"
            "  i64.trunc_sat_f64_u"
            "  local.set $int"
            "  local.get $x"
            "  local.get $int"
            "  f64.convert_i64_u"
            "  f64.sub"
            "  f64.const 1000000"
            "  f64.mul"
            "  f64.const 0.5"
            "  f64.add"
            "  i64.trunc_sat_f64_u"
            "  local.set $frac"
            "  local.get $frac"
            "  i64.const 1000000"
            "  i64.ge_u"
            "  if"
            "    local.get $int"
            "    i64.const 1"
            "    i64.add"
            "    local.set $int"
            "    i64.const 0"
            "    local.set $frac"
            "  end"
            "  ;; integer digits: count them, then write them back to front"
            "  i32.const 0"
            "  local.set $count"
            "  local.get $int"
            "  local.set $tmp"
            "  loop $count_digits"
            "    local.get $count"
            "    i32.const 1"
            "    i32.add"
            "    local.set $count"
            "    local.get $tmp"
            "    i64.const 10"
            "    i64.div_u"
            "    local.set $tmp"
            "    local.get $tmp"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $count_digits"
            "  end"
            "  local.get $cursor"
            "  local.get $count"
            "  i32.add"
            "  i32.const 1"
            "  i32.sub"
            "  local.set $pos"
            "  local.get $int"
            "  local.set $tmp"
            "  loop $write_digits"
            "    local.get $pos"
            "    local.get $tmp"
            "    i64.const 10"
            "    i64.rem_u"
            "    i32.wrap_i64"
            "    i32.const 48"
            "    i32.add"
            "    i32.store8"
            "    local.get $pos"
            "    i32.const 1"
            "    i32.sub"
            "    local.set $pos"
            "    local.get $tmp"
            "    i64.const 10"
            "    i64.div_u"
            "    local.set $tmp"
            "    local.get $tmp"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $write_digits"
            "  end"
            "  local.get $cursor"
            "  local.get $count"
            "  i32.add"
            "  local.set $cursor"
            "  local.get $cursor"
            "  i32.const 46"
            "  i32.store8"
            "  local.get $cursor"
            "  i32.const 1"
            "  i32.add"
            "  local.set $cursor"
            "  ;; six fraction digits, most significant first"
            "  i64.const 100000"
            "  local.set $scale"
            "  loop $write_frac"
            "    local.get $cursor"
            "    local.get $frac"
            "    local.get $scale"
            "    i64.div_u"
            "    i64.const 10"
            "    i64.rem_u"
            "    i32.wrap_i64"
            "    i32.const 48"
            "    i32.add"
            "    i32.store8"
            "    local.get $cursor"
            "    i32.const 1"
            "    i32.add"
            "    local.set $cursor"
            "    local.get $scale"
            "    i64.const 10"
            "    i64.div_u"
            "    local.set $scale"
            "    local.get $scale"
            "    i64.const 0"
            "    i64.ne"
            "    br_if $write_frac"
            "  end"
            "  ;; drop trailing zeros, keeping one digit after the point"
            "  block $trim_done"
            "    loop $trim"
            "      local.get $cursor"
            "      i32.const 1"
            "      i32.sub"
            "      i32.load8_u"
            "      i32.const 48"
            "      i32.ne"
            "      br_if $trim_done"
            "      local.get $cursor"
            "      i32.const 2"
            "      i32.sub"
            "      i32.load8_u"
            "      i32.const 46"
            "      i32.eq"
            "      br_if $trim_done"
            "      local.get $cursor"
            "      i32.const 1"
            "      i32.sub"
            "      local.set $cursor"
            "      br $trim"
            "    end"
            "  end"
            "  local.get $base"
            "  local.get $cursor"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            "  i32.sub"
            "  i32.store"
            "  local.get $cursor"
            "  i32.const 0"
            "  i32.store8"
            "  local.get $base"
            "  i32.const 4"
            "  i32.add"
            ")" ]
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
      // A boxed float as an int: `i32.trunc_f64_s` traps on NaN, an infinity or a value out of the range of `int`.
      Helpers.floatToInt,
      lines
          [ $"(func ${Helpers.floatToInt} (param $box i32) (result i32)"
            "  local.get $box"
            "  f64.load"
            "  i32.trunc_f64_s"
            ")" ]
      // An int as a boxed float: `f64.convert_i32_s` is exact for every `int`.
      Helpers.intToFloat,
      lines
          [ $"(func ${Helpers.intToFloat} (param $value i32) (result i32)"
            "  local.get $value"
            "  f64.convert_i32_s"
            $"  call ${Helpers.floatBox}"
            ")" ]
      Helpers.listCons,
      lines
          [ $"(func ${Helpers.listCons} (param $head i32) (param $tail i32) (result i32)"
            "  (local $cell i32)"
            "  i32.const 8"
            $"  call ${Helpers.newCell}"
            "  local.set $cell"
            "  local.get $cell"
            "  local.get $head"
            "  i32.store"
            "  local.get $cell"
            "  i32.const 4"
            "  i32.add"
            "  local.get $tail"
            "  i32.store"
            "  local.get $cell"
            ")" ]
      Helpers.listLength,
      lines
          [ $"(func ${Helpers.listLength} (param $list i32) (result i32)"
            "  (local $count i32)"
            "  block $done"
            "    loop $walk"
            "      local.get $list"
            "      i32.eqz"
            "      br_if $done"
            "      local.get $count"
            "      i32.const 1"
            "      i32.add"
            "      local.set $count"
            "      local.get $list"
            "      i32.const 4"
            "      i32.add"
            "      i32.load"
            "      local.set $list"
            "      br $walk"
            "    end"
            "  end"
            "  local.get $count"
            ")" ]
      // a ++ b copies the cells of a in front of b (b itself is shared, lists being immutable).
      Helpers.listConcat,
      lines
          [ $"(func ${Helpers.listConcat} (param $a i32) (param $b i32) (result i32)"
            "  local.get $a"
            "  i32.eqz"
            "  if (result i32)"
            "    local.get $b"
            "  else"
            "    local.get $a"
            "    i32.load"
            "    local.get $a"
            "    i32.const 4"
            "    i32.add"
            "    i32.load"
            "    local.get $b"
            $"    call ${Helpers.listConcat}"
            $"    call ${Helpers.listCons}"
            "  end"
            ")" ] ]
    |> Map.ofList

let stringDataBaseOffset = 256

let dbgDataBaseOffset = 128

// dbg writes an integer's sign and digits directly after the "dbg: " prefix (at 133), so that one
// fd_write from the prefix to the end of the digits prints the whole line.
let dbgDigitBufferOffset = dbgDataBaseOffset + 5
let dbgStrNewlineOffset = dbgDataBaseOffset + 96  // newline byte at 224 (clear of dbg's buffer)

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
      Helpers.listCons, [ Helpers.newCell ]
      Helpers.recordSet, []
      Helpers.recordCopy, [ Helpers.newObject; Helpers.copyBytes ]
      Helpers.tagBox, [ Helpers.newCell ]
      Helpers.tagCode, []
      Helpers.floatBox, [ Helpers.newCell ]
      Helpers.floatToString, [ Helpers.newObject ]
      Helpers.listLength, []
      Helpers.listConcat, [ Helpers.listCons ]
      Helpers.reuseObject, [ Helpers.newObject ]
      Helpers.listConsAt, [ Helpers.listCons ]
      Helpers.tagBoxAt, [ Helpers.tagBox ]
      Helpers.floatSqrt, [ Helpers.floatBox ]
      Helpers.floatFloor, [ Helpers.floatBox ]
      Helpers.floatCeil, [ Helpers.floatBox ]
      Helpers.floatTrunc, [ Helpers.floatBox ]
      Helpers.floatToInt, []
      Helpers.intToFloat, [ Helpers.floatBox ] ]
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

// The `dbg` helpers: `$dbg` prints an integer, `$dbg_str` a string, each prefixed with "dbg: ".
let dbgFunctions =
        [ "  (func $dbg (param $x i32)"
          "    (local $n i32)"
          "    (local $isneg i32)"
          "    (local $pos i32)"
          "    (local $start i32)"
          "    (local $lo i32)"
          "    (local $hi i32)"
          "    (local $tmp i32)"
          "    local.get $x"
          "    local.set $n"
          "    local.get $n"
          "    i32.const 0"
          "    i32.lt_s"
          "    if"
          "      i32.const 1"
          "      local.set $isneg"
          "      local.get $n"
          "      i32.const -1"
          "      i32.mul"
          "      local.set $n"
          "    end"
          $"    i32.const {dbgDigitBufferOffset}"
          "    local.set $pos"
          "    local.get $isneg"
          "    if"
          "      local.get $pos"
          "      i32.const 45"
          "      i32.store8"
          "      local.get $pos"
          "      i32.const 1"
          "      i32.add"
          "      local.set $pos"
          "    end"
          "    local.get $n"
          "    i32.const 0"
          "    i32.eq"
          "    if"
          "      local.get $pos"
          "      i32.const 48"
          "      i32.store8"
          "      local.get $pos"
          "      i32.const 1"
          "      i32.add"
          "      local.set $pos"
          "    else"
          "      local.get $pos"
          "      local.set $start"
          "      loop $digits"
          "        local.get $pos"
          "        local.get $n"
          "        i32.const 10"
          "        i32.rem_u"
          "        i32.const 48"
          "        i32.add"
          "        i32.store8"
          "        local.get $pos"
          "        i32.const 1"
          "        i32.add"
          "        local.set $pos"
          "        local.get $n"
          "        i32.const 10"
          "        i32.div_u"
          "        local.set $n"
          "        local.get $n"
          "        i32.const 0"
          "        i32.ne"
          "        br_if $digits"
          "      end"
          "      local.get $start"
          "      local.set $lo"
          "      local.get $pos"
          "      i32.const 1"
          "      i32.sub"
          "      local.set $hi"
          "      block $rev_done"
          "        loop $rev"
          "          local.get $lo"
          "          local.get $hi"
          "          i32.ge_u"
          "          br_if $rev_done"
          "          local.get $lo"
          "          i32.load8_u"
          "          local.set $tmp"
          "          local.get $lo"
          "          local.get $hi"
          "          i32.load8_u"
          "          i32.store8"
          "          local.get $hi"
          "          local.get $tmp"
          "          i32.store8"
          "          local.get $lo"
          "          i32.const 1"
          "          i32.add"
          "          local.set $lo"
          "          local.get $hi"
          "          i32.const 1"
          "          i32.sub"
          "          local.set $hi"
          "          br $rev"
          "        end"
          "      end"
          "    end"
          "    local.get $pos"
          "    i32.const 10"
          "    i32.store8"
          "    local.get $pos"
          "    i32.const 1"
          "    i32.add"
          "    local.set $pos"
          "    i32.const 0"
          $"    i32.const {dbgDataBaseOffset}"
          "    i32.store"
          "    i32.const 4"
          "    local.get $pos"
          $"    i32.const {dbgDataBaseOffset}"
          "    i32.sub"
          "    i32.store"
          "    i32.const 1"
          "    i32.const 0"
          "    i32.const 1"
          "    i32.const 8"
          "    call $fd_write"
          "    drop"
          "  )"
          "  (func $dbg_str (param $ptr i32)"
          "    (local $len i32)"
          "    local.get $ptr"
          "    i32.const 4"
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
