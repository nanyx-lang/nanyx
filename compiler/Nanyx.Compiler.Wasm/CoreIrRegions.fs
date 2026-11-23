/// The mutable objects a `memory` block frees when it ends (#2232). The outermost `memory` block running has a region
/// (see `WasmRegions`), and each mutable object made while it runs is on the region's list: counting still frees it as
/// soon as nothing refers to it, and it comes off the list then, and when the block ends it drops what each object still
/// on the list holds and frees it, which breaks any cycle through them. The layout and the helpers are described in
/// "Regions" in `docs/src/content/deep-dives/memory-management.md`.
module internal Transpiler.Wasm.CoreIrRegions

open Transpiler.Wasm.CoreIr

/// The fields in front of what a tracked object holds: the table slot of the function that drops what it holds, then
/// the next and the previous object on its region's list. Their names start with `-`, which no field of the program's
/// can, so they sort before every other field and are stored first, and no record of the program's is taken for one.
let dropField = "-region-drop"
let nextField = "-region-next"
let prevField = "-region-prev"

/// Where each of the fields is, from the object's pointer. A region is a block laid out the same way, with no drop
/// function, so the list's links are at the same offsets in every node of it.
let dropOffset = 0
let nextOffset = 4
let prevOffset = 8

/// The type of a link: a word, which no count follows.
let private linkType = IrSized NyxCompiler.SizedInteger.I32

/// The field a box's value is: after the links.
let boxValueField = "0"

/// The slot of a box's value among its fields.
let boxValueSlot = 3

/// The type of a box (an `MRef`'s) holding a value of `held`: a record of the links, then the value.
let boxType (held: IrType) =
    IrRecord [ dropField, linkType; nextField, linkType; prevField, linkType; boxValueField, held ]

/// A box's type, with the type of what it holds.
let (|RegionBox|_|) (ty: IrType) =
    match ty with
    | IrRecord [ (drop, _); (next, _); (prev, _); (value, held) ] when drop = dropField && next = nextField && prev = prevField && value = boxValueField ->
        Some held
    | _ -> None

/// The function that drops what a box of type `box` holds, called through the table with the box when its region ends:
/// it takes the box and gives back 0, as an environment's drop function does.
let dropContentsName (box: IrType) = "__nanyx_region_drop_" + Identifiers.sanitizeIdentifier (IrType.mangle box)
