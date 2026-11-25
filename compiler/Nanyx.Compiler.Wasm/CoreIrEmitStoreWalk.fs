/// The code of a `for` loop's walk of a map's or a set's store (see `StoreWalk` in `CoreIr.fs` and `Maps.storeOf`): the
/// store's layout the walk reads, and the code at the top of each time round, which finds the next entry.
module Transpiler.Wasm.CoreIrEmitStoreWalk

open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrEmitLocals

/// The parts of a map's or a set's store a `for` loop's walk reads (see `StoreWalk` and `Maps.storeOf`), from its IR
/// type: a bucket's payload, `(h, entries)`, and a node's, `(bits, children)`.
module private StoreLayout =
    /// The payload of the tag `tag` of the walk's store.
    let payload (walk: StoreWalk) (tag: string) : IrType =
        match IrType.unfold walk.StoreType with
        | IrTags tags ->
            tags |> List.tryFind (fst >> (=) tag) |> Option.bind snd |> Option.defaultWith (fun () -> internalError $"a store without a #{tag}")
        | other -> internalError $"a store of type '{IrType.show other}'"

    let private second (walk: StoreWalk) (tag: string) =
        match payload walk tag with
        | IrRecord fields when fields.Length = 2 -> fields
        | other -> internalError $"a store's #{tag} of '{IrType.show other}'"

    /// Where the second part of the payload of `tag`, a bucket's entries or a node's children, is.
    let secondOffset (walk: StoreWalk) (tag: string) = CoreIrLayout.fieldOffset (second walk tag) 1

    /// The type of a bucket's entries.
    let entries (walk: StoreWalk) = snd (second walk "bucket").[1]

    /// The type of an entry, a key and its value.
    let entry (walk: StoreWalk) =
        match entries walk with
        | IrList entry -> entry
        | other -> internalError $"a bucket's entries of '{IrType.show other}'"

    /// The type of a node's children.
    let childrenType (walk: StoreWalk) = snd (second walk "node").[1]

/// The code that starts the walk from the store on the stack: the root is the store to go into first.
let start (stream: ForStream) (walk: StoreWalk) : string list =
    [ $"local.tee ${localName walk.Root}"
      $"local.set ${localName walk.Pending}"
      "i32.const 0"
      $"local.set ${localName stream.Cursor}"
      "i32.const 0"
      $"local.set ${localName walk.Depth}"
      "i64.const 0"
      $"local.set ${localName walk.Path}" ]

/// The code that moves the cursor on to the next entry of the bucket it is in.
let advance (touching: string -> string) (stream: ForStream) (walk: StoreWalk) : string list =
    let cursor = localName stream.Cursor
    [ $"local.get ${cursor}"; touching $"i32.load offset={CoreIrLayout.tailOffset (StoreLayout.entry walk)}"; $"local.set ${cursor}" ]

/// The code at the top of each time round a `for` loop's walk of a map's or a set's store (see `StoreWalk`): it looks
/// for a bucket with an entry left in it, going into the store's nodes and back up out of them, and ends the loop
/// (`done'`) once it is back out of the root; then it takes the entry, or a set's element, borrowed.
let next (touching: string -> string) (bucketCode: int) (depth: int) (done': string) (stream: ForStream) (walk: StoreWalk) : string list =
    let local (var: Var) = localName var
    let cursor, pending, children, index, level, path, root =
        local stream.Cursor, local walk.Pending, local walk.Children, local walk.Index, local walk.Depth, local walk.Path, local walk.Root
    let seek, found, up = $"$__store_seek_{depth}", $"$__store_found_{depth}", $"$__store_up_{depth}"
    let load (ty: IrType) (offset: int) = touching (CoreIrLayout.load ty offset)
    let loadWord = touching "i32.load"
    let bucketPayload = load (StoreLayout.payload walk "bucket") CoreIrLayout.payloadOffset
    let bucketEntries = load (StoreLayout.entries walk) (StoreLayout.secondOffset walk "bucket")
    // The array of the children of the node at the top of the stack.
    let childrenOf = [ load (StoreLayout.payload walk "node") CoreIrLayout.payloadOffset; load (StoreLayout.childrenType walk) (StoreLayout.secondOffset walk "node") ]
    // The child of the array on the stack at the index on the stack.
    let childAt = [ "i32.const 4"; "i32.mul"; "i32.add"; touching $"i32.load offset={CoreIrLayout.arrayElementOffset walk.StoreType 0}" ]
    // The bits of `path` for the level on the stack, from its low end.
    let pathAt = [ "i32.const 5"; "i32.mul"; "i64.extend_i32_u"; "i64.shr_u"; "i64.const 31"; "i64.and"; "i32.wrap_i64" ]
    [ $"block {found}"
      $"  loop {seek}"
      // An entry left in the bucket it is in.
      $"    local.get ${cursor}"
      $"    br_if {found}"
      // A store to go into: the root, or a node's child.
      $"    local.get ${pending}"
      "    if"
      $"      local.get ${pending}"
      "      i32.const 1"
      "      i32.and"
      "      if"
      "        i32.const 0"
      $"        local.set ${pending}"
      $"        br {seek}"
      "      end"
      $"      local.get ${pending}"
      $"      {loadWord}"
      $"      i32.const {bucketCode}"
      "      i32.eq"
      "      if"
      $"        local.get ${pending}"
      $"        {bucketPayload}"
      $"        {bucketEntries}"
      $"        local.set ${cursor}"
      "        i32.const 0"
      $"        local.set ${pending}"
      $"        br {seek}"
      "      end"
      // A node: inside another, the index of the child it is there (the one before `index`) is kept in the path.
      $"      local.get ${level}"
      "      if"
      $"        local.get ${path}"
      "        i64.const 31"
      $"        local.get ${level}"
      "        i32.const 1"
      "        i32.sub"
      "        i32.const 5"
      "        i32.mul"
      "        i64.extend_i32_u"
      "        i64.shl"
      "        i64.const -1"
      "        i64.xor"
      "        i64.and"
      $"        local.get ${index}"
      "        i32.const 1"
      "        i32.sub"
      "        i64.extend_i32_u"
      $"        local.get ${level}"
      "        i32.const 1"
      "        i32.sub"
      "        i32.const 5"
      "        i32.mul"
      "        i64.extend_i32_u"
      "        i64.shl"
      "        i64.or"
      $"        local.set ${path}"
      "      end"
      $"      local.get ${pending}"
      yield! childrenOf |> List.map (fun line -> "      " + line)
      $"      local.set ${children}"
      "      i32.const 0"
      $"      local.set ${index}"
      $"      local.get ${level}"
      "      i32.const 1"
      "      i32.add"
      $"      local.set ${level}"
      "      i32.const 0"
      $"      local.set ${pending}"
      $"      br {seek}"
      "    end"
      // In no node, with nothing to go into: the walk is over.
      $"    local.get ${level}"
      "    i32.eqz"
      $"    {done'}"
      // The next child of the node it is in.
      $"    local.get ${index}"
      $"    local.get ${children}"
      $"    {loadWord}"
      "    i32.lt_u"
      "    if"
      $"      local.get ${index}"
      "      i32.const 1"
      "      i32.add"
      $"      local.set ${index}"
      // A bucket, as most children are, is gone into here, the child held in the cursor, and a node at the top. A
      // child is never empty.
      $"      local.get ${children}"
      $"      local.get ${index}"
      "      i32.const 1"
      "      i32.sub"
      yield! childAt |> List.map (fun line -> "      " + line)
      $"      local.tee ${cursor}"
      $"      {loadWord}"
      $"      i32.const {bucketCode}"
      "      i32.eq"
      "      if"
      $"        local.get ${cursor}"
      $"        {bucketPayload}"
      $"        {bucketEntries}"
      $"        local.set ${cursor}"
      $"        br {found}"
      "      end"
      $"      local.get ${cursor}"
      $"      local.set ${pending}"
      "      i32.const 0"
      $"      local.set ${cursor}"
      $"      br {seek}"
      "    end"
      // Out of the node it is in, back up to the one above: down again from the root along the path, `pending`
      // counting the levels, and on from the child after the one it took there.
      $"    local.get ${level}"
      "    i32.const 1"
      "    i32.sub"
      $"    local.tee ${level}"
      "    i32.eqz"
      $"    {done'}"
      $"    local.get ${root}"
      yield! childrenOf |> List.map (fun line -> "    " + line)
      $"    local.set ${children}"
      "    i32.const 0"
      $"    local.set ${pending}"
      $"    block {up}_done"
      $"      loop {up}"
      $"        local.get ${pending}"
      $"        local.get ${level}"
      "        i32.const 1"
      "        i32.sub"
      "        i32.ge_u"
      $"        br_if {up}_done"
      $"        local.get ${children}"
      $"        local.get ${path}"
      $"        local.get ${pending}"
      yield! pathAt |> List.map (fun line -> "        " + line)
      yield! childAt |> List.map (fun line -> "        " + line)
      yield! childrenOf |> List.map (fun line -> "        " + line)
      $"        local.set ${children}"
      $"        local.get ${pending}"
      "        i32.const 1"
      "        i32.add"
      $"        local.set ${pending}"
      $"        br {up}"
      "      end"
      "    end"
      "    i32.const 0"
      $"    local.set ${pending}"
      $"    local.get ${path}"
      $"    local.get ${level}"
      "    i32.const 1"
      "    i32.sub"
      yield! pathAt |> List.map (fun line -> "    " + line)
      "    i32.const 1"
      "    i32.add"
      $"    local.set ${index}"
      $"    br {seek}"
      "  end"
      "end"
      // The entry, or a set's element, the bucket's cell holds.
      $"local.get ${cursor}"
      load (StoreLayout.entry walk) 0
      yield!
          (if walk.Keys then
               match StoreLayout.entry walk with
               | IrRecord fields -> [ load stream.Element.Type (CoreIrLayout.fieldOffset fields 0) ]
               | other -> internalError $"a set's entry of '{IrType.show other}'"
           else [])
      $"local.set ${localName stream.Element}" ]
