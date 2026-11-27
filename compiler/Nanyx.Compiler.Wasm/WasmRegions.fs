/// The runtime helpers of the regions `memory` blocks free their mutable objects with when they end (#2232, #2376; see
/// `CoreIrRegions` for an object's layout). Each `memory` block running has a region, and the regions make a stack: a
/// block that starts pushes one, and the region of the innermost block running is in a global. A new mutable object goes
/// on the innermost region. A value stored where it may outlive its block is promoted first (see `CoreIrPromotion`): each
/// object it holds that is on a region inner to the one it is stored into moves onto that one. So when a block ends,
/// nothing the program can still reach refers to what is left on its region, and it frees that.
///
/// A region is a block laid out as an object's links are: no drop function, then the first object on its list, the
/// region it is inside (none for the outermost), the last object on its list, and its depth, 1 for the outermost. The
/// list is circular, so the region is the list's sentinel, and an object is linked and unlinked without asking whether
/// it is first or last.
module internal Transpiler.Wasm.WasmRegions

open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WatText

/// The names of the helpers. The two the standard library calls are its intrinsics.
module Names =
    let regionEnter = NyxCompiler.Intrinsics.regionEnter
    let regionExit = NyxCompiler.Intrinsics.regionExit
    /// Puts a new object on the innermost region's list: `object drop ->`, with `drop` the table slot of the function that
    /// drops what it holds.
    let regionTrack = "__nanyx_region_track"
    /// Takes an object off its region's list, once counting has given up its last reference: `object ->`.
    let regionUnlink = "__nanyx_region_unlink"
    /// Moves an object onto a region, if it is on an inner one, and says whether it did: `object region -> moved`.
    let regionMove = "__nanyx_region_move"
    /// Moves every object of the regions inside a region onto it: `region ->`. Promotion falls back on it for a value it
    /// can't follow, such as a closure.
    let regionMerge = "__nanyx_region_merge"
    /// Not a function: the globals the others share, the innermost region and the outermost one.
    let regionGlobals = "__nanyx_region_globals"
    /// Drops what each object on a region holds and frees it, and the region: `region ->`.
    let regionEnd = "__nanyx_region_end"
    /// Not helpers: a value stored into a mutable object, which promotes the value onto the object's region, and one
    /// stored where only the outermost region is sure to outlive it (see `CoreIrPromotion`).
    let regionStore = "__nanyx_region_store"
    let regionStoreOutermost = "__nanyx_region_store_outermost"

/// The global that holds the innermost region while a `memory` block runs, and 0 otherwise, and the one that holds the
/// outermost region.
let innermostGlobal = "$__nanyx_region"
let outermostGlobal = "$__nanyx_region_outermost"

/// The offsets of a node's fields (see `CoreIrRegions`): an object's owner is the region it is on, and a region's is the
/// region it is inside. A region's depth follows its links.
let private dropOffset = 0
let private nextOffset = 4
let ownerOffset = 8
let private prevOffset = 12
let private depthOffset = 16
let private regionSize = 20

/// How far before a tracked object's pointer its block starts: its count and its info word, as a record's.
let private headerSize = 8

let private globalsText =
    lines [ $"(global {innermostGlobal} (mut i32) (i32.const 0))"; $"(global {outermostGlobal} (mut i32) (i32.const 0))" ]

/// The code that puts `$object` on `$region`'s list, as its first object, and makes the region its owner.
let private link =
    [ "  local.get $region"
      $"  i32.load offset={nextOffset}"
      "  local.set $first"
      "  local.get $object"
      "  local.get $first"
      $"  i32.store offset={nextOffset}"
      "  local.get $object"
      "  local.get $region"
      $"  i32.store offset={prevOffset}"
      "  local.get $object"
      "  local.get $region"
      $"  i32.store offset={ownerOffset}"
      "  local.get $first"
      "  local.get $object"
      $"  i32.store offset={prevOffset}"
      "  local.get $region"
      "  local.get $object"
      $"  i32.store offset={nextOffset}" ]

/// The code that joins the objects either side of `$object`.
let private unlink =
    [ "  local.get $object"
      $"  i32.load offset={prevOffset}"
      "  local.get $object"
      $"  i32.load offset={nextOffset}"
      $"  i32.store offset={nextOffset}"
      "  local.get $object"
      $"  i32.load offset={nextOffset}"
      "  local.get $object"
      $"  i32.load offset={prevOffset}"
      $"  i32.store offset={prevOffset}" ]

/// A block starts: it pushes a region, its list empty, inside the innermost one.
let private regionEnterHelper =
    lines
        [ $"(func ${Names.regionEnter}"
          "  (local $region i32)"
          "  (local $outer i32)"
          $"  global.get {innermostGlobal}"
          "  local.set $outer"
          $"  i32.const {regionSize}"
          $"  call ${WasmAllocator.Names.alloc}"
          "  local.tee $region"
          "  local.get $region"
          $"  i32.store offset={nextOffset}"
          "  local.get $region"
          "  local.get $region"
          $"  i32.store offset={prevOffset}"
          "  local.get $region"
          "  local.get $outer"
          $"  i32.store offset={ownerOffset}"
          "  local.get $region"
          "  local.get $outer"
          "  if (result i32)"
          "    local.get $outer"
          $"    i32.load offset={depthOffset}"
          "    i32.const 1"
          "    i32.add"
          "  else"
          "    local.get $region"
          $"    global.set {outermostGlobal}"
          "    i32.const 1"
          "  end"
          $"  i32.store offset={depthOffset}"
          "  local.get $region"
          $"  global.set {innermostGlobal}"
          ")" ]

/// A block ends: it pops its region and ends it. Its result has been promoted onto the region it is inside already.
let private regionExitHelper =
    lines
        [ $"(func ${Names.regionExit}"
          "  (local $region i32)"
          $"  global.get {innermostGlobal}"
          "  local.tee $region"
          $"  i32.load offset={ownerOffset}"
          $"  global.set {innermostGlobal}"
          "  local.get $region"
          $"  call ${Names.regionEnd}"
          $"  global.get {innermostGlobal}"
          "  i32.eqz"
          "  if"
          "    i32.const 0"
          $"    global.set {outermostGlobal}"
          "  end"
          ")" ]

/// Links `$object` in on the innermost region.
let private regionTrackHelper =
    lines
        [ $"(func ${Names.regionTrack} (param $object i32) (param $drop i32)"
          "  (local $region i32)"
          "  (local $first i32)"
          $"  global.get {innermostGlobal}"
          "  local.set $region"
          "  local.get $object"
          "  local.get $drop"
          $"  i32.store offset={dropOffset}"
          yield! link
          ")" ]

let private regionUnlinkHelper = lines [ $"(func ${Names.regionUnlink} (param $object i32)"; yield! unlink; ")" ]

/// Moves `$object` onto `$region` when the region it is on is deeper, and says whether it did.
let private regionMoveHelper =
    lines
        [ $"(func ${Names.regionMove} (param $object i32) (param $region i32) (result i32)"
          "  (local $first i32)"
          "  local.get $object"
          $"  i32.load offset={ownerOffset}"
          $"  i32.load offset={depthOffset}"
          "  local.get $region"
          $"  i32.load offset={depthOffset}"
          "  i32.le_u"
          "  if"
          "    i32.const 0"
          "    return"
          "  end"
          yield! unlink
          yield! link
          "  i32.const 1"
          ")" ]

/// Moves every object of each region inside `$region`, from the innermost out, onto it.
let private regionMergeHelper =
    lines
        [ $"(func ${Names.regionMerge} (param $region i32)"
          "  (local $inner i32)"
          "  (local $object i32)"
          "  (local $first i32)"
          $"  global.get {innermostGlobal}"
          "  local.set $inner"
          "  block $done"
          "    loop $regions"
          "      local.get $inner"
          "      local.get $region"
          "      i32.eq"
          "      br_if $done"
          "      block $empty"
          "        loop $objects"
          "          local.get $inner"
          $"          i32.load offset={nextOffset}"
          "          local.tee $object"
          "          local.get $inner"
          "          i32.eq"
          "          br_if $empty"
          yield! unlink |> List.map (fun line -> "        " + line)
          yield! link |> List.map (fun line -> "        " + line)
          "          br $objects"
          "        end"
          "      end"
          "      local.get $inner"
          $"      i32.load offset={ownerOffset}"
          "      local.set $inner"
          "      br $regions"
          "    end"
          "  end"
          ")" ]

/// A loop over the objects on `$region`'s list, from the first, with each in `$object` while `body` runs. It reads the
/// next object before `body` runs, so `body` may free the object, but may not take another off the list.
let private eachObject (label: string) (body: string list) =
    [ "  local.get $region"
      $"  i32.load offset={nextOffset}"
      "  local.set $object"
      $"  block {label}_done"
      $"    loop {label}"
      "      local.get $object"
      "      local.get $region"
      "      i32.eq"
      $"      br_if {label}_done"
      "      local.get $object"
      $"      i32.load offset={nextOffset}"
      "      local.set $next"
      yield! body |> List.map (fun line -> "      " + line)
      "      local.get $next"
      "      local.set $object"
      $"      br {label}"
      "    end"
      "  end" ]

/// Ends `$region`: takes a reference to each object on its list, so that none is freed or taken off the list while
/// the next steps run, drops what each holds, which gives up the references the objects hold to each other, and frees
/// each, whatever its count says. Nothing the program can still reach refers to one, so this frees a cycle through
/// them too. Then frees the region.
let private regionEndHelper =
    lines
        [ $"(func ${Names.regionEnd} (param $region i32)"
          "  (local $object i32)"
          "  (local $next i32)"
          yield! eachObject "$pin" [ "local.get $object"; $"i32.const {headerSize}"; "i32.sub"; $"call ${WasmAllocator.Names.retain}" ]
          yield!
              eachObject
                  "$contents"
                  [ "local.get $object"
                    "local.get $object"
                    $"i32.load offset={dropOffset}"
                    "call_indirect (type $ctx_fn_1)"
                    "drop" ]
          yield! eachObject "$free" [ "local.get $object"; $"i32.const {headerSize}"; "i32.sub"; $"call ${WasmAllocator.Names.free}" ]
          "  local.get $region"
          $"  call ${WasmAllocator.Names.free}"
          ")" ]

/// Each helper, by name.
let helpers =
    [ Names.regionGlobals, globalsText
      Names.regionEnter, regionEnterHelper
      Names.regionExit, regionExitHelper
      Names.regionTrack, regionTrackHelper
      Names.regionUnlink, regionUnlinkHelper
      Names.regionMove, regionMoveHelper
      Names.regionMerge, regionMergeHelper
      Names.regionEnd, regionEndHelper ]

/// What each helper calls.
let dependencies =
    [ Names.regionGlobals, []
      Names.regionEnter, [ Names.regionGlobals; WasmAllocator.Names.alloc ]
      Names.regionExit, [ Names.regionGlobals; Names.regionEnd ]
      Names.regionTrack, [ Names.regionGlobals ]
      Names.regionUnlink, []
      Names.regionMove, []
      Names.regionMerge, [ Names.regionGlobals ]
      Names.regionEnd, [ WasmAllocator.Names.retain; WasmAllocator.Names.free ] ]
