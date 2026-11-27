/// The allocator every module that uses the heap draws on: where the heap and its free lists are, `alloc` and `free`,
/// the reference counts' `retain` and `release`, and what a module compiled with memory checks adds to them.
module internal Transpiler.Wasm.WasmAllocator

open Transpiler.Wasm.WatText

/// The names of the allocator's functions, and of what its memory checks call (`WasmRuntime.Helpers` names them for
/// the rest of the backend).
module Names =
    let alloc = "__nanyx_alloc"
    let allocAligned8 = "__nanyx_alloc_aligned8"
    let free = "__nanyx_free"
    let release = "__nanyx_release"
    let retain = "__nanyx_retain"
    let writeStderr = NyxCompiler.Intrinsics.writeStderr
    let procExit = "proc_exit"

/// The lowest address the table of free-list heads starts at. A module whose string literals end past it puts the table
/// after them instead (see `heapBaseAfter`).
let minimumHeapBase = 4096

/// Where the table of free-list heads starts in a module whose data segments end at `dataEnd`: at `minimumHeapBase`,
/// or past the data, 8-byte aligned. The blocks `alloc` hands out are only 4-byte aligned; `allocAligned8Helper`
/// hands out 8-byte aligned ones.
let heapBaseAfter (dataEnd: int) = max minimumHeapBase ((dataEnd + 7) &&& ~~~7)

/// The largest block size, in bytes, with a free list of its own; larger blocks share one list, which `alloc` searches
/// for the first block big enough (#1464).
let largestSizeClass = 256

/// The bits of a block's size word that are not its size, which is a multiple of 4. `freeLarge` is set on a free
/// block on the large list (and, in a module compiled with memory checks, on every freed block: see
/// `MemoryChecks.freedMark`). `previousFreeLarge` is set when the block just before this one is a free large block,
/// whose size is then also in its last word, just before this block's size word (a boundary tag), so that `free` finds
/// and joins the free blocks either side of a large block without walking the list (#1614).
module SizeWordBits =
    let freeLarge = 1
    let previousFreeLarge = 2
    /// The mask that leaves a size word's size.
    let size = -4

/// Where `$heap_ptr` starts, given where the table of free-list heads starts (`heapBase`): after the table, one word
/// per small size and one for the large blocks.
let heapStartOffset (heapBase: int) = heapBase + largestSizeClass + 4

/// The pages of memory a module starts with: enough to hold everything below `heapStart`, and at least one.
let initialMemoryPages (heapStart: int) = max 1 ((heapStart + 65535) / 65536)

/// What a module compiled with memory checks adds (see `checkedRuntimeHelpers`): counters, and the functions that
/// stop the program when a block is freed twice or an object is counted after it was freed. They write what went
/// wrong to stderr and exit through WASI `proc_exit` rather than trap, so that a host sees an exit code, not a
/// hardware exception.
module MemoryChecks =
    /// The global counting the blocks `alloc` handed out that `free` has not taken back.
    let liveBlocks = "live_blocks"
    /// The global counting every call to `alloc`.
    let allocations = "allocations"
    /// The `i64` global adding up the size of every block `alloc` handed out (the size in the word before it, not the
    /// word itself).
    let bytesAllocated = "bytes_allocated"
    /// The global adding up the size of the blocks `alloc` handed out that `free` has not taken back.
    let liveBytes = "live_bytes"
    /// The most `liveBytes` has been.
    let peakLiveBytes = "peak_live_bytes"
    /// The highest `$heap_ptr` has been: the most memory the heap has reached, as an address.
    let peakHeap = "peak_heap"
    /// The global counting the objects built in the block of an object given up with a count of one, rather than in a
    /// new one: a reuse token's (see `CoreIrReuse`), or a record updated in place.
    let reusedInPlace = "reused_in_place"
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
    /// of one. It is `SizeWordBits.freeLarge`, which a module without memory checks sets on free large blocks only.
    let freedMark = SizeWordBits.freeLarge


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

// Takes the free large block at `$node` off the large list, using the local `$scratch`. The list is doubly linked: a
// block's first word is the next block, and its second the word that links to it, the next block's of the one before
// or the list's head. Indented by `indent`.
let private unlink (indent: string) (node: string) (scratch: string) =
    [ $"local.get ${node}"
      "i32.load offset=4"
      $"local.get ${node}"
      "i32.load"
      "i32.store"
      $"local.get ${node}"
      "i32.load"
      $"local.tee ${scratch}"
      "if"
      $"  local.get ${scratch}"
      $"  local.get ${node}"
      "  i32.load offset=4"
      "  i32.store offset=4"
      "end" ]
    |> List.map (fun line -> indent + line)

// Adds `delta` to the global `name`.
let private bump (name: string) (delta: int) =
    [ $"  global.get ${name}"; $"  i32.const {delta}"; "  i32.add"; $"  global.set ${name}" ]

/// Adds one to `MemoryChecks.reusedInPlace`, indented by `indent`: what a module compiled with memory checks does where
/// it builds an object in the block of one it gave up.
let countReuse (indent: string) =
    bump MemoryChecks.reusedInPlace 1 |> List.map (fun line -> indent + line.TrimStart())

// Sets the global `peak` to the global `value` when that is higher.
let private raisePeak (peak: string) (value: string) =
    [ $"  global.get ${value}"
      $"  global.get ${peak}"
      "  i32.gt_u"
      "  if"
      $"    global.get ${value}"
      $"    global.set ${peak}"
      "  end" ]

// Counts the bytes of the block at `$ptr`, which `alloc` is about to hand out, indented by `indent`.
let private countBlockBytes (indent: string) =
    [ "  local.get $ptr"
      "  i32.const 4"
      "  i32.sub"
      "  i32.load"
      $"  i32.const {SizeWordBits.size}"
      "  i32.and"
      "  local.set $bytes"
      $"  global.get ${MemoryChecks.bytesAllocated}"
      "  local.get $bytes"
      "  i64.extend_i32_u"
      "  i64.add"
      $"  global.set ${MemoryChecks.bytesAllocated}"
      $"  global.get ${MemoryChecks.liveBytes}"
      "  local.get $bytes"
      "  i32.add"
      $"  global.set ${MemoryChecks.liveBytes}"
      yield! raisePeak MemoryChecks.peakLiveBytes MemoryChecks.liveBytes ]
    |> List.map (fun line -> indent + line)

/// `alloc`, with the free-list heads at `heapBase`. With `checks`, it counts the call, the block it hands out and its
/// bytes, and the highest the heap reaches, and clears the freed mark of a block it takes from a free list.
let allocHelper (checks: bool) (heapBase: int) =
    // Counts the block at `$ptr` where `alloc` hands it out, indented by `indent`.
    let handOut (indent: string) = if checks then countBlockBytes indent else []
    lines
        [ yield $"(func ${Names.alloc} (param $size i32) (result i32)"
          yield "  (local $link i32)"
          yield "  (local $ptr i32)"
          yield "  (local $found i32)"
          yield "  (local $rest i32)"
          yield "  (local $next i32)"
          if checks then
              yield "  (local $bytes i32)"
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
          yield! freeListHead heapBase
          yield "  local.set $link"
          yield "  local.get $size"
          yield $"  i32.const {largestSizeClass}"
          yield "  i32.le_u"
          yield "  if"
          // A small size's list holds blocks of exactly that size: take the first.
          yield "    local.get $link"
          yield "    i32.load"
          yield "    local.tee $ptr"
          yield "    if"
          yield "      local.get $link"
          yield "      local.get $ptr"
          yield "      i32.load"
          yield "      i32.store"
          // Its size word is as `alloc` wrote it, with the bit of the block before it: clear the freed mark.
          if checks then
              yield "      local.get $ptr"
              yield "      i32.const 4"
              yield "      i32.sub"
              yield "      local.get $ptr"
              yield "      i32.const 4"
              yield "      i32.sub"
              yield "      i32.load"
              yield $"      i32.const {~~~MemoryChecks.freedMark}"
              yield "      i32.and"
              yield "      i32.store"
          yield! handOut "    "
          yield "      local.get $ptr"
          yield "      return"
          yield "    end"
          yield "  else"
          // The large blocks' list: take the first block big enough, and split off what it has to spare when that is
          // a large block itself. A free large block's size word has no `previousFreeLarge`, as `free` joins it to
          // the block before when that is free and large.
          yield "    block $empty"
          yield "      loop $search"
          yield "        local.get $link"
          yield "        i32.load"
          yield "        local.tee $ptr"
          yield "        i32.eqz"
          yield "        br_if $empty"
          yield "        local.get $ptr"
          yield "        i32.const 4"
          yield "        i32.sub"
          yield "        i32.load"
          yield $"        i32.const {SizeWordBits.size}"
          yield "        i32.and"
          yield "        local.tee $found"
          yield "        local.get $size"
          yield "        i32.ge_u"
          yield "        if"
          yield "          local.get $found"
          yield "          local.get $size"
          yield "          i32.sub"
          yield $"          i32.const {largestSizeClass + 8}"
          yield "          i32.ge_u"
          yield "          if"
          // The rest, after this block's `$size` bytes and the rest's own size word, takes its place on the list,
          // with its size at both ends. The block after it still follows a free large block.
          yield "            local.get $ptr"
          yield "            local.get $size"
          yield "            i32.add"
          yield "            i32.const 4"
          yield "            i32.add"
          yield "            local.tee $rest"
          yield "            i32.const 4"
          yield "            i32.sub"
          yield "            local.get $found"
          yield "            local.get $size"
          yield "            i32.sub"
          yield "            i32.const 4"
          yield "            i32.sub"
          yield "            local.tee $next"
          yield $"            i32.const {SizeWordBits.freeLarge}"
          yield "            i32.or"
          yield "            i32.store"
          yield "            local.get $rest"
          yield "            local.get $next"
          yield "            i32.add"
          yield "            i32.const 4"
          yield "            i32.sub"
          yield "            local.get $next"
          yield "            i32.store"
          yield "            local.get $rest"
          yield "            local.get $ptr"
          yield "            i32.load"
          yield "            local.tee $next"
          yield "            i32.store"
          yield "            local.get $rest"
          yield "            local.get $link"
          yield "            i32.store offset=4"
          yield "            local.get $link"
          yield "            local.get $rest"
          yield "            i32.store"
          yield "            local.get $next"
          yield "            if"
          yield "              local.get $next"
          yield "              local.get $rest"
          yield "              i32.store offset=4"
          yield "            end"
          yield "            local.get $size"
          yield "            local.set $found"
          yield "          else"
          yield! unlink "            " "ptr" "next"
          // The block after it, which a free large block never ends the heap before, no longer follows a free one.
          yield "            local.get $ptr"
          yield "            local.get $found"
          yield "            i32.add"
          yield "            local.get $ptr"
          yield "            local.get $found"
          yield "            i32.add"
          yield "            i32.load"
          yield $"            i32.const {~~~SizeWordBits.previousFreeLarge}"
          yield "            i32.and"
          yield "            i32.store"
          yield "          end"
          yield "          local.get $ptr"
          yield "          i32.const 4"
          yield "          i32.sub"
          yield "          local.get $found"
          yield "          i32.store"
          yield! handOut "        "
          yield "          local.get $ptr"
          yield "          return"
          yield "        end"
          yield "        local.get $ptr"
          yield "        local.set $link"
          yield "        br $search"
          yield "      end"
          yield "    end"
          yield "  end"
          // Nothing free fits: bump `$heap_ptr`. Past 4 GB the end of the block wraps around, which is out of memory.
          yield "  global.get $heap_ptr"
          yield "  i32.const 4"
          yield "  i32.add"
          yield "  local.tee $ptr"
          yield "  local.get $size"
          yield "  i32.add"
          yield "  local.tee $rest"
          yield "  local.get $ptr"
          yield "  i32.lt_u"
          yield "  if"
          yield "    unreachable"
          yield "  end"
          yield "  local.get $rest"
          yield "  global.set $heap_ptr"
          if checks then
              yield! raisePeak MemoryChecks.peakHeap "heap_ptr"
          // The pages the heap needs now, worked out in 64 bits so that 4 GB of them does not wrap to 0.
          yield "  global.get $heap_ptr"
          yield "  i64.extend_i32_u"
          yield "  i64.const 65535"
          yield "  i64.add"
          yield "  i64.const 16"
          yield "  i64.shr_u"
          yield "  i32.wrap_i64"
          yield "  memory.size"
          yield "  i32.sub"
          yield "  local.tee $rest"
          yield "  i32.const 0"
          yield "  i32.gt_s"
          yield "  if"
          yield "    local.get $rest"
          yield "    memory.grow"
          yield "    i32.const -1"
          yield "    i32.eq"
          yield "    if"
          yield "      unreachable"
          yield "    end"
          yield "  end"
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  local.get $size"
          yield "  i32.store"
          yield! handOut ""
          yield "  local.get $ptr"
          yield ")" ]

/// `free`, with the free-list heads at `heapBase`. With `checks`, it stops the program on a block that is already free,
/// fills the block with `MemoryChecks.poison`, marks it freed, and counts it.
let freeHelper (checks: bool) (heapBase: int) =
    let largeHead = heapBase + largestSizeClass
    lines
        [ yield $"(func ${Names.free} (param $ptr i32)"
          yield "  (local $header i32)"
          yield "  (local $size i32)"
          yield "  (local $next i32)"
          yield "  (local $joined i32)"
          yield "  (local $link i32)"
          if checks then
              yield "  (local $offset i32)"
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  i32.load"
          yield "  local.tee $header"
          yield $"  i32.const {SizeWordBits.size}"
          yield "  i32.and"
          yield "  local.set $size"
          if checks then
              yield "  local.get $header"
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
              yield "  local.get $header"
              yield $"  i32.const {MemoryChecks.freedMark}"
              yield "  i32.or"
              yield "  i32.store"
              yield! bump MemoryChecks.liveBlocks -1
              yield $"  global.get ${MemoryChecks.liveBytes}"
              yield "  local.get $size"
              yield "  i32.sub"
              yield $"  global.set ${MemoryChecks.liveBytes}"
          yield! freeListHead heapBase
          yield "  local.set $next"
          yield "  local.get $size"
          yield $"  i32.const {largestSizeClass}"
          yield "  i32.le_u"
          yield "  if"
          yield "    local.get $ptr"
          yield "    local.get $next"
          yield "    i32.load"
          yield "    i32.store"
          yield "    local.get $next"
          yield "    local.get $ptr"
          yield "    i32.store"
          yield "    return"
          yield "  end"
          // A large block is joined to the free large blocks either side of it, found by their size words, without
          // walking the list. The block after, unless this one ends at the top of the heap: its size word says whether
          // it is free and large.
          yield "  local.get $ptr"
          yield "  local.get $size"
          yield "  i32.add"
          yield "  global.get $heap_ptr"
          yield "  i32.ne"
          yield "  if"
          yield "    local.get $ptr"
          yield "    local.get $size"
          yield "    i32.add"
          yield "    i32.const 4"
          yield "    i32.add"
          yield "    local.tee $next"
          yield "    i32.const 4"
          yield "    i32.sub"
          yield "    i32.load"
          yield "    local.tee $joined"
          yield $"    i32.const {SizeWordBits.freeLarge}"
          yield "    i32.and"
          yield "    local.get $joined"
          yield $"    i32.const {SizeWordBits.size}"
          yield "    i32.and"
          yield "    local.tee $joined"
          yield $"    i32.const {largestSizeClass}"
          yield "    i32.gt_u"
          yield "    i32.and"
          yield "    if"
          yield! unlink "      " "next" "link"
          yield "      local.get $size"
          yield "      local.get $joined"
          yield "      i32.add"
          yield "      i32.const 4"
          yield "      i32.add"
          yield "      local.set $size"
          yield "    end"
          yield "  end"
          // The block before, when this one's size word says it is free and large: its size is in its last word.
          yield "  local.get $header"
          yield $"  i32.const {SizeWordBits.previousFreeLarge}"
          yield "  i32.and"
          yield "  if"
          yield "    local.get $ptr"
          yield "    i32.const 4"
          yield "    i32.sub"
          yield "    local.get $ptr"
          yield "    i32.const 8"
          yield "    i32.sub"
          yield "    i32.load"
          yield "    local.tee $joined"
          yield "    i32.sub"
          yield "    local.set $ptr"
          yield! unlink "    " "ptr" "link"
          yield "    local.get $size"
          yield "    local.get $joined"
          yield "    i32.add"
          yield "    i32.const 4"
          yield "    i32.add"
          yield "    local.set $size"
          yield "  end"
          // A block that ends at the top of the heap goes back to it, rather than on the list.
          yield "  local.get $ptr"
          yield "  local.get $size"
          yield "  i32.add"
          yield "  global.get $heap_ptr"
          yield "  i32.eq"
          yield "  if"
          yield "    local.get $ptr"
          yield "    i32.const 4"
          yield "    i32.sub"
          yield "    global.set $heap_ptr"
          yield "    return"
          yield "  end"
          // Otherwise it goes on the front of the list, with its size at both ends, and the block after it is told it
          // follows a free large block.
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  local.get $size"
          yield $"  i32.const {SizeWordBits.freeLarge}"
          yield "  i32.or"
          yield "  i32.store"
          yield "  local.get $ptr"
          yield "  local.get $size"
          yield "  i32.add"
          yield "  local.tee $next"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  local.get $size"
          yield "  i32.store"
          yield "  local.get $next"
          yield "  local.get $next"
          yield "  i32.load"
          yield $"  i32.const {SizeWordBits.previousFreeLarge}"
          yield "  i32.or"
          yield "  i32.store"
          yield "  local.get $ptr"
          yield $"  i32.const {largeHead}"
          yield "  i32.load"
          yield "  local.tee $next"
          yield "  i32.store"
          yield "  local.get $ptr"
          yield $"  i32.const {largeHead}"
          yield "  i32.store offset=4"
          yield "  local.get $next"
          yield "  if"
          yield "    local.get $next"
          yield "    local.get $ptr"
          yield "    i32.store offset=4"
          yield "  end"
          yield $"  i32.const {largeHead}"
          yield "  local.get $ptr"
          yield "  i32.store"
          yield ")" ]

/// `allocAligned8`: a block of `$size` bytes at an address that is a multiple of 8, which `free` takes back like any
/// other. `alloc` hands out blocks that are only 4-byte aligned, so it asks `alloc` for 12 bytes more. When that block
/// is not 8-byte aligned, its first 8 bytes become a block of their own, which it frees, and the rest, after a size
/// word of its own, starts 12 bytes on, at a multiple of 8. A block of 8 bytes is small, so the block after it is
/// never told it follows a free large block. With `checks`, it counts the block it keeps as one more handed out and
/// live, and takes the 4 bytes of its size word off the bytes counted.
let allocAligned8Helper (checks: bool) =
    lines
        [ yield $"(func ${Names.allocAligned8} (param $size i32) (result i32)"
          yield "  (local $ptr i32)"
          yield "  (local $header i32)"
          yield "  local.get $size"
          yield "  i32.const 12"
          yield "  i32.add"
          yield $"  call ${Names.alloc}"
          yield "  local.tee $ptr"
          yield "  i32.const 4"
          yield "  i32.and"
          yield "  i32.eqz"
          yield "  if"
          yield "    local.get $ptr"
          yield "    return"
          yield "  end"
          // The kept block's size word, 8 bytes on, holds what is left of the size; the first block's keeps the bits
          // it has (whether the block before it is free and large) with a size of 8.
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  i32.load"
          yield "  local.set $header"
          yield "  local.get $ptr"
          yield "  local.get $header"
          yield $"  i32.const {SizeWordBits.size}"
          yield "  i32.and"
          yield "  i32.const 12"
          yield "  i32.sub"
          yield "  i32.store offset=8"
          yield "  local.get $ptr"
          yield "  i32.const 4"
          yield "  i32.sub"
          yield "  local.get $header"
          yield $"  i32.const {~~~SizeWordBits.size}"
          yield "  i32.and"
          yield "  i32.const 8"
          yield "  i32.or"
          yield "  i32.store"
          if checks then
              yield! bump MemoryChecks.allocations 1
              yield! bump MemoryChecks.liveBlocks 1
              yield! bump MemoryChecks.liveBytes -4
              yield $"  global.get ${MemoryChecks.bytesAllocated}"
              yield "  i64.const 4"
              yield "  i64.sub"
              yield $"  global.set ${MemoryChecks.bytesAllocated}"
          yield "  local.get $ptr"
          yield $"  call ${Names.free}"
          yield "  local.get $ptr"
          yield "  i32.const 12"
          yield "  i32.add"
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
      $"  (global ${MemoryChecks.bytesAllocated} (mut i64) (i64.const 0))"
      $"  (global ${MemoryChecks.liveBytes} (mut i32) (i32.const 0))"
      $"  (global ${MemoryChecks.peakLiveBytes} (mut i32) (i32.const 0))"
      $"  (global ${MemoryChecks.peakHeap} (mut i32) (i32.const {heapStartOffset heapBase}))"
      $"  (global ${MemoryChecks.reusedInPlace} (mut i32) (i32.const 0))"
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
