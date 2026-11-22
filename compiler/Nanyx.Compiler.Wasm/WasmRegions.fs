/// The runtime helpers of regions, which a `memory` block frees its mutable objects with when it ends (#2232; see
/// `CoreIrRegions` for the layout). A region is a block of three words laid out as a tracked object's links are: no
/// drop function, then the first and the last object on its list, which is circular, so the region is the list's
/// sentinel and an object is linked and unlinked without asking whether it is first or last.
module internal Transpiler.Wasm.WasmRegions

open Transpiler.Wasm.WasmAllocator

/// The names of the helpers. The two the standard library calls are its intrinsics.
module Names =
    let regionNew = NyxCompiler.Intrinsics.regionNew
    let regionEnd = NyxCompiler.Intrinsics.regionEnd
    /// Puts a new object on its region's list: `object region drop ->`, with `drop` the table slot of the function that
    /// drops what it holds.
    let regionTrack = "__nanyx_region_track"
    /// Takes an object off its region's list, once counting has given up its last reference: `object ->`.
    let regionUnlink = "__nanyx_region_unlink"

/// The offsets of a node's fields (see `CoreIrRegions`), and the size of a region's block.
let private dropOffset = 0
let private nextOffset = 4
let private prevOffset = 8
let private regionSize = 12

/// How far before a tracked object's pointer its block starts: its count and its info word, as a record's.
let private headerSize = 8

/// A new region, its list empty: `-> region`.
let private regionNewHelper =
    lines
        [ $"(func ${Names.regionNew} (result i32)"
          "  (local $region i32)"
          $"  i32.const {regionSize}"
          $"  call ${WasmAllocator.Names.alloc}"
          "  local.tee $region"
          "  local.get $region"
          $"  i32.store offset={nextOffset}"
          "  local.get $region"
          "  local.get $region"
          $"  i32.store offset={prevOffset}"
          "  local.get $region"
          ")" ]

/// Links `$object` in after the region, as its first object.
let private regionTrackHelper =
    lines
        [ $"(func ${Names.regionTrack} (param $object i32) (param $region i32) (param $drop i32)"
          "  (local $first i32)"
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

/// A loop over the objects on `$region`'s list, from the first, with each in `$object` while `body` runs. `body` may
/// not take the object off the list; it reads the next one before it runs, so it may free the object.
let private eachObject (label: string) (body: string list) =
    [ "  local.get $region"
      $"  i32.load offset={nextOffset}"
      "  local.set $object"
      $"  block ${label}_done"
      $"    loop ${label}"
      "      local.get $object"
      "      local.get $region"
      "      i32.eq"
      $"      br_if ${label}_done"
      "      local.get $object"
      $"      i32.load offset={nextOffset}"
      "      local.set $next"
      yield! body |> List.map (fun line -> "      " + line)
      "      local.get $next"
      "      local.set $object"
      $"      br ${label}"
      "    end"
      "  end" ]

/// Ends `$region`: takes a reference to each object on its list, so that none is freed while the next steps run, drops
/// what each holds, which gives up the references the objects hold to each other, and frees each, whatever its count
/// says. Nothing outside the block refers to one (see "The guarantee the runtime relies on" in
/// `docs/src/content/advanced/memory.md`), so this frees a cycle through them too. Then frees the region.
let private regionEndHelper =
    lines
        [ $"(func ${Names.regionEnd} (param $region i32)"
          "  (local $object i32)"
          "  (local $next i32)"
          yield!
              eachObject "$pin" [ "local.get $object"; $"i32.const {headerSize}"; "i32.sub"; $"call ${WasmAllocator.Names.retain}" ]
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
    [ Names.regionNew, regionNewHelper
      Names.regionTrack, regionTrackHelper
      Names.regionUnlink, regionUnlinkHelper
      Names.regionEnd, regionEndHelper ]

/// What each helper calls.
let dependencies =
    [ Names.regionNew, [ WasmAllocator.Names.alloc ]
      Names.regionTrack, []
      Names.regionUnlink, []
      Names.regionEnd, [ WasmAllocator.Names.retain; WasmAllocator.Names.free ] ]
