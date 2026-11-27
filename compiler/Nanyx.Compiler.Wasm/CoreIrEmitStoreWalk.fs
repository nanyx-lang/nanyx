/// The code of a `for` loop's walk of a map's or a set's store (see `StoreWalk` in `CoreIr.fs` and `Maps.storeOf`): the
/// store's layout the walk reads, and the code at the top of each time round, which finds the next entry.
module Transpiler.Wasm.CoreIrEmitStoreWalk

open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrEmitLocals

/// The code that moves the cursor on to the next entry of the bucket it is in.
let advance (touching: string -> string) (stream: ForStream) (walk: StoreWalk) : string list =
    let cursor = localName stream.Cursor
    [ $"local.get ${cursor}"; touching $"i32.load offset={CoreIrLayout.tailOffset (CoreIrLayout.StoreLayout.entry walk.StoreType)}"; $"local.set ${cursor}" ]

let private indent (by: int) (lines: string list) = lines |> List.map (fun line -> String.replicate by " " + line)

/// The pieces of code the walk is made of, over its locals.
type private Walk(touching: string -> string, stream: ForStream, walk: StoreWalk) =
    let local (var: Var) = localName var
    let load (ty: IrType) (offset: int) = touching (CoreIrLayout.load ty offset)
    let fields = CoreIrLayout.StoreLayout.fields walk.StoreType

    member _.Cursor = local stream.Cursor
    member _.Pending = local walk.Pending
    member _.Node = local walk.Node
    member _.LeafBits = local walk.LeafBits
    member _.Leaves = local walk.Leaves
    member _.Remaining = local walk.Remaining
    member _.LeafIndex = local walk.LeafIndex
    member _.LeafEnd = local walk.LeafEnd
    member _.Level = local walk.Depth
    member _.Path = local walk.Path
    member _.Root = local walk.Root
    member _.Load ty offset = load ty offset

    /// The field at `slot` of the node payload on the stack.
    member _.Field(slot: int) = load (snd fields.[slot]) (CoreIrLayout.fieldOffset fields slot)

    /// A node's bits at `slot`, which fit 32, as an `i32`.
    member this.Bits(slot: int) = [ this.Field slot; "i32.wrap_i64" ]

    /// The payload of the node whose cell is on the stack.
    member _.PayloadOf = load (CoreIrLayout.StoreLayout.payload walk.StoreType) CoreIrLayout.payloadOffset

    /// The element of the array on the stack at the index on the stack: a bucket's list, or a node's cell.
    member _.ElementAt =
        [ "i32.const 4"; "i32.mul"; "i32.add"; touching $"i32.load offset={CoreIrLayout.arrayElementOffset (CoreIrLayout.StoreLayout.bucket walk.StoreType) 0}" ]

    /// `LeafEnd` set: how many buckets come before the next node it goes into, or all of them.
    member this.LeafEndSet =
        [ $"local.get ${this.LeafBits}"
          "i32.const 0"
          $"local.get ${this.Remaining}"
          "i32.sub"
          $"local.get ${this.Remaining}"
          "i32.and"
          "i32.const 1"
          "i32.sub"
          "i32.and"
          "i32.popcnt"
          $"local.set ${this.LeafEnd}" ]

    /// Goes into the node whose cell is on the stack, a level down, from its first bucket or node.
    member this.Enter =
        [ this.PayloadOf
          $"local.tee ${this.Node}"
          yield! this.Bits 0
          $"local.set ${this.LeafBits}"
          $"local.get ${this.Node}"
          this.Field 1
          $"local.set ${this.Leaves}"
          $"local.get ${this.Node}"
          yield! this.Bits 2
          $"local.set ${this.Remaining}"
          "i32.const 0"
          $"local.set ${this.LeafIndex}"
          yield! this.LeafEndSet
          $"local.get ${this.Level}"
          "i32.const 1"
          "i32.add"
          $"local.set ${this.Level}" ]

/// The code that starts the walk from the store on the stack: it goes into the root, or, for `#empty`, a code, which is
/// odd, stands at a level with nothing left, so that the walk ends at once.
let start (touching: string -> string) (stream: ForStream) (walk: StoreWalk) : string list =
    let w = Walk(touching, stream, walk)
    [ $"local.tee ${w.Root}"
      "i32.const 1"
      "i32.and"
      "if"
      "  i32.const 0"
      $"  local.set ${w.Remaining}"
      "  i32.const 0"
      $"  local.set ${w.LeafIndex}"
      "  i32.const 0"
      $"  local.set ${w.LeafEnd}"
      "  i32.const 1"
      $"  local.set ${w.Level}"
      "else"
      "  i32.const 0"
      $"  local.set ${w.Level}"
      $"  local.get ${w.Root}"
      yield! w.Enter |> indent 2
      "end"
      "i32.const 0"
      $"local.set ${w.Cursor}"
      "i64.const 0"
      $"local.set ${w.Path}" ]

/// The code at the top of each time round a `for` loop's walk of a map's or a set's store (see `StoreWalk`): it looks
/// for a bucket with an entry left in it, taking each node's buckets and nodes in the order of their five-bit values:
/// the buckets before its next node one after another from its array, then that node, gone into. It goes back up out
/// of a node with nothing left, and ends the loop (`done'`) once it is back out of the root; then it takes the entry, or
/// a set's element, borrowed.
let next (touching: string -> string) (depth: int) (done': string) (stream: ForStream) (walk: StoreWalk) : string list =
    let w = Walk(touching, stream, walk)
    let seek, found, up = $"$__store_seek_{depth}", $"$__store_found_{depth}", $"$__store_up_{depth}"
    // How many bits of the bits on the stack are below the bit on the stack: where its element is.
    let below = [ "i32.const 1"; "i32.sub"; "i32.and"; "i32.popcnt" ]
    // The five bits of `path` for the level on the stack, from its low end.
    let pathAt = [ "i32.const 5"; "i32.mul"; "i64.extend_i32_u"; "i64.shr_u"; "i64.const 31"; "i64.and"; "i32.wrap_i64" ]
    // How far up `path` the bits of the level of the node it is in are, as an `i64`.
    let shiftOfLevel = [ $"local.get ${w.Level}"; "i32.const 1"; "i32.sub"; "i32.const 5"; "i32.mul"; "i64.extend_i32_u" ]
    [ $"block {found}"
      $"  loop {seek}"
      // An entry left in the bucket it is in.
      $"    local.get ${w.Cursor}"
      $"    br_if {found}"
      // A bucket before the next node, read straight from the node's array, the cell of its first entry held in the
      // cursor. A bucket is never empty.
      $"    local.get ${w.LeafIndex}"
      $"    local.get ${w.LeafEnd}"
      "    i32.lt_u"
      "    if"
      $"      local.get ${w.Leaves}"
      $"      local.get ${w.LeafIndex}"
      yield! w.ElementAt |> indent 6
      $"      local.set ${w.Cursor}"
      $"      local.get ${w.LeafIndex}"
      "      i32.const 1"
      "      i32.add"
      $"      local.set ${w.LeafIndex}"
      $"      br {found}"
      "    end"
      // The next node, its lowest bit left, gone into, its five-bit value kept in the path.
      $"    local.get ${w.Remaining}"
      "    if"
      "      i32.const 0"
      $"      local.get ${w.Remaining}"
      "      i32.sub"
      $"      local.get ${w.Remaining}"
      "      i32.and"
      $"      local.tee ${w.Pending}"
      $"      local.get ${w.Remaining}"
      "      i32.xor"
      $"      local.set ${w.Remaining}"
      $"      local.get ${w.Path}"
      "      i64.const 31"
      yield! shiftOfLevel |> indent 6
      "      i64.shl"
      "      i64.const -1"
      "      i64.xor"
      "      i64.and"
      $"      local.get ${w.Pending}"
      "      i32.ctz"
      "      i64.extend_i32_u"
      yield! shiftOfLevel |> indent 6
      "      i64.shl"
      "      i64.or"
      $"      local.set ${w.Path}"
      $"      local.get ${w.Node}"
      $"      {w.Field 3}"
      $"      local.get ${w.Node}"
      yield! w.Bits 2 |> indent 6
      $"      local.get ${w.Pending}"
      yield! below |> indent 6
      yield! w.ElementAt |> indent 6
      yield! w.Enter |> indent 6
      $"      br {seek}"
      "    end"
      // Out of the node it is in, back up to the one above: down again from the root along the path, `pending`
      // counting the levels, and on from the bucket or node after the one it took there.
      $"    local.get ${w.Level}"
      "    i32.const 1"
      "    i32.sub"
      $"    local.tee ${w.Level}"
      "    i32.eqz"
      $"    {done'}"
      $"    local.get ${w.Root}"
      $"    {w.PayloadOf}"
      $"    local.set ${w.Node}"
      "    i32.const 0"
      $"    local.set ${w.Pending}"
      $"    block {up}_done"
      $"      loop {up}"
      $"        local.get ${w.Pending}"
      $"        local.get ${w.Level}"
      "        i32.const 1"
      "        i32.sub"
      "        i32.ge_u"
      $"        br_if {up}_done"
      $"        local.get ${w.Node}"
      $"        {w.Field 3}"
      $"        local.get ${w.Node}"
      yield! w.Bits 2 |> indent 8
      "        i32.const 1"
      $"        local.get ${w.Path}"
      $"        local.get ${w.Pending}"
      yield! pathAt |> indent 8
      "        i32.shl"
      yield! below |> indent 8
      yield! w.ElementAt |> indent 8
      $"        {w.PayloadOf}"
      $"        local.set ${w.Node}"
      $"        local.get ${w.Pending}"
      "        i32.const 1"
      "        i32.add"
      $"        local.set ${w.Pending}"
      $"        br {up}"
      "      end"
      "    end"
      // The bits up to the one it took at this level, `2 << value` less one, held in `pending`.
      "    i32.const 2"
      $"    local.get ${w.Path}"
      $"    local.get ${w.Level}"
      "    i32.const 1"
      "    i32.sub"
      yield! pathAt |> indent 4
      "    i32.shl"
      "    i32.const 1"
      "    i32.sub"
      $"    local.set ${w.Pending}"
      $"    local.get ${w.Node}"
      yield! w.Bits 0 |> indent 4
      $"    local.tee ${w.LeafBits}"
      $"    local.get ${w.Pending}"
      "    i32.and"
      "    i32.popcnt"
      $"    local.set ${w.LeafIndex}"
      $"    local.get ${w.Node}"
      $"    {w.Field 1}"
      $"    local.set ${w.Leaves}"
      $"    local.get ${w.Node}"
      yield! w.Bits 2 |> indent 4
      $"    local.get ${w.Pending}"
      "    i32.const -1"
      "    i32.xor"
      "    i32.and"
      $"    local.set ${w.Remaining}"
      yield! w.LeafEndSet |> indent 4
      $"    br {seek}"
      "  end"
      "end"
      // The entry, or a set's element, the bucket's cell holds.
      $"local.get ${w.Cursor}"
      w.Load (CoreIrLayout.StoreLayout.entry walk.StoreType) 0
      yield!
          (if walk.Keys then
               match CoreIrLayout.StoreLayout.entry walk.StoreType with
               | IrRecord fields -> [ w.Load stream.Element.Type (CoreIrLayout.fieldOffset fields 0) ]
               | other -> internalError $"a set's entry of '{IrType.show other}'"
           else [])
      $"local.set ${localName stream.Element}" ]
