/// The allocator every module that uses the heap draws on: where the heap and its free lists are, `alloc` and `free`,
/// the reference counts' `retain` and `release`, and what a module compiled with memory checks adds to them.
module internal Transpiler.Wasm.WasmAllocator

/// The names of the allocator's functions, and of what its memory checks call (`WasmRuntime.Helpers` names them for
/// the rest of the backend).
module Names =
    let alloc = "__nanyx_alloc"
    let free = "__nanyx_free"
    let release = "__nanyx_release"
    let retain = "__nanyx_retain"
    let writeStderr = NyxCompiler.Intrinsics.writeStderr
    let procExit = "proc_exit"

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

let lines (items: string list) = items |> List.map (fun line -> "  " + line) |> String.concat "\n"

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
let allocHelper (checks: bool) (heapBase: int) =
    lines
        [ yield $"(func ${Names.alloc} (param $size i32) (result i32)"
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
let freeHelper (checks: bool) (heapBase: int) =
    lines
        [ yield $"(func ${Names.free} (param $ptr i32)"
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
let releaseHelper (checks: bool) =
    lines
        [ yield $"(func ${Names.release} (param $count i32) (result i32)"
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
let retainHelper (checks: bool) =
    lines
        [ yield $"(func ${Names.retain} (param $count i32)"
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
              $"  call ${Names.writeStderr}"
              $"  i32.const {MemoryChecks.exitCode}"
              $"  call ${Names.procExit}"
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
