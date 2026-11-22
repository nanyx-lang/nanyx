/// The runtime helpers of the region a `memory` block frees its mutable objects with when it ends (#2232; see
/// `CoreIrRegions` for an object's layout). The outermost `memory` block running has the region: a block that starts
/// while another runs adds to its depth, and only the outermost one makes the region when it starts and ends it when it
/// ends. Every block whose `Memory` an object can be made under runs inside the outermost one, so an object on the
/// region outlives nothing that can refer to it. A cycle made in an inner block is freed when the outermost one ends.
///
/// The region is a block of three words laid out as an object's links are: no drop function, then the first and the
/// last object on its list, which is circular, so the region is the list's sentinel, and an object is linked and
/// unlinked without asking whether it is first or last.
module internal Transpiler.Wasm.WasmRegions

open Transpiler.Wasm.WasmAllocator

/// The names of the helpers. The two the standard library calls are its intrinsics.
module Names =
    let regionEnter = NyxCompiler.Intrinsics.regionEnter
    let regionExit = NyxCompiler.Intrinsics.regionExit
    /// Puts a new object on the region's list: `object drop ->`, with `drop` the table slot of the function that drops
    /// what it holds.
    let regionTrack = "__nanyx_region_track"
    /// Takes an object off the region's list, once counting has given up its last reference: `object ->`.
    let regionUnlink = "__nanyx_region_unlink"
    /// Not a function: the globals the others share, the region and the depth of the blocks running.
    let regionGlobals = "__nanyx_region_globals"
    /// Drops what each object on the region holds and frees it, and the region: `region ->`.
    let regionEnd = "__nanyx_region_end"

/// The global that holds the region while a `memory` block runs, and 0 otherwise, and the one that counts the blocks
/// running.
let private regionGlobal = "$__nanyx_region"
let private depthGlobal = "$__nanyx_region_depth"

/// The offsets of a node's fields (see `CoreIrRegions`), and the size of a region's block.
let private dropOffset = 0
let private nextOffset = 4
let private prevOffset = 8
let private regionSize = 12

/// How far before a tracked object's pointer its block starts: its count and its info word, as a record's.
let private headerSize = 8

let private globalsText =
    lines [ $"(global {regionGlobal} (mut i32) (i32.const 0))"; $"(global {depthGlobal} (mut i32) (i32.const 0))" ]

/// A block starts: the outermost makes the region, its list empty.
let private regionEnterHelper =
    lines
        [ $"(func ${Names.regionEnter}"
          "  (local $region i32)"
          $"  global.get {depthGlobal}"
          "  i32.eqz"
          "  if"
          $"    i32.const {regionSize}"
          $"    call ${WasmAllocator.Names.alloc}"
          "    local.tee $region"
          "    local.get $region"
          $"    i32.store offset={nextOffset}"
          "    local.get $region"
          "    local.get $region"
          $"    i32.store offset={prevOffset}"
          "    local.get $region"
          $"    global.set {regionGlobal}"
          "  end"
          $"  global.get {depthGlobal}"
          "  i32.const 1"
          "  i32.add"
          $"  global.set {depthGlobal}"
          ")" ]

/// A block ends: the outermost ends the region.
let private regionExitHelper =
    lines
        [ $"(func ${Names.regionExit}"
          $"  global.get {depthGlobal}"
          "  i32.const 1"
          "  i32.sub"
          $"  global.set {depthGlobal}"
          $"  global.get {depthGlobal}"
          "  i32.eqz"
          "  if"
          $"    global.get {regionGlobal}"
          $"    call ${Names.regionEnd}"
          "    i32.const 0"
          $"    global.set {regionGlobal}"
          "  end"
          ")" ]

/// Links `$object` in after the region, as its first object.
let private regionTrackHelper =
    lines
        [ $"(func ${Names.regionTrack} (param $object i32) (param $drop i32)"
          "  (local $region i32)"
          "  (local $first i32)"
          $"  global.get {regionGlobal}"
          "  local.set $region"
          "  local.get $object"
          "  local.get $drop"
          $"  i32.store offset={dropOffset}"
          "  local.get $region"
          $"  i32.load offset={nextOffset}"
          "  local.set $first"
          "  local.get $object"
          "  local.get $first"
          $"  i32.store offset={nextOffset}"
          "  local.get $object"
          "  local.get $region"
          $"  i32.store offset={prevOffset}"
          "  local.get $first"
          "  local.get $object"
          $"  i32.store offset={prevOffset}"
          "  local.get $region"
          "  local.get $object"
          $"  i32.store offset={nextOffset}"
          ")" ]

/// Joins the objects either side of `$object`.
let private regionUnlinkHelper =
    lines
        [ $"(func ${Names.regionUnlink} (param $object i32)"
          "  local.get $object"
          $"  i32.load offset={prevOffset}"
          "  local.get $object"
          $"  i32.load offset={nextOffset}"
          $"  i32.store offset={nextOffset}"
          "  local.get $object"
          $"  i32.load offset={nextOffset}"
          "  local.get $object"
          $"  i32.load offset={prevOffset}"
          $"  i32.store offset={prevOffset}"
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
/// each, whatever its count says. Nothing the program can still reach refers to one (see "The guarantee the runtime
/// relies on" in `docs/src/content/advanced/memory.md`), so this frees a cycle through them too. Then frees the region.
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
      Names.regionEnd, regionEndHelper ]

/// What each helper calls.
let dependencies =
    [ Names.regionGlobals, []
      Names.regionEnter, [ Names.regionGlobals; WasmAllocator.Names.alloc ]
      Names.regionExit, [ Names.regionGlobals; Names.regionEnd ]
      Names.regionTrack, [ Names.regionGlobals ]
      Names.regionUnlink, []
      Names.regionEnd, [ WasmAllocator.Names.retain; WasmAllocator.Names.free ] ]
