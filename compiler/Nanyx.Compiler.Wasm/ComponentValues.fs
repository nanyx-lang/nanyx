/// Values crossing a component's boundary (see `ComponentExports`): how the canonical ABI lays out a value of a WIT type
/// in memory and passes it as core values (see CanonicalABI.md in the component model), and the code that makes a Nanyx
/// value from those, lifting it, or writes one into them, lowering it. A Nanyx value is of the `IrType` the backend
/// lowered its type to, which is made as its WIT type is: a record's fields by name, a variant's cases by tag.
module internal Transpiler.Wasm.ComponentValues

open NyxCompiler
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.WasmRuntime

/// `offset` rounded up to a multiple of `alignment`.
let private alignTo (alignment: int) (offset: int) = (offset + alignment - 1) / alignment * alignment

/// How many bytes a variant of `cases` keeps which case it is in.
let private discriminantSize (cases: (string * WitType option) list) =
    if cases.Length <= 256 then 1
    elif cases.Length <= 65536 then 2
    else 4

/// The alignment of a value of `ty` in memory.
let rec alignment (ty: WitType) =
    match ty with
    | WitType.Bool
    | WitType.S8
    | WitType.U8 -> 1
    | WitType.S16
    | WitType.U16 -> 2
    | WitType.S32
    | WitType.U32
    | WitType.F32
    | WitType.String
    | WitType.List _ -> 4
    | WitType.S64
    | WitType.U64
    | WitType.F64 -> 8
    | WitType.Record _
    | WitType.Tuple _ -> WitType.children ty |> List.map alignment |> List.fold max 1
    | WitType.Variant(_, cases) -> max (discriminantSize cases) (payloadAlignment cases)

and private payloadAlignment (cases: (string * WitType option) list) =
    cases |> List.choose snd |> List.map alignment |> List.fold max 1

/// How many bytes a value of `ty` takes in memory, its alignment included.
let rec size (ty: WitType) =
    match ty with
    | WitType.Bool
    | WitType.S8
    | WitType.U8 -> 1
    | WitType.S16
    | WitType.U16 -> 2
    | WitType.S32
    | WitType.U32
    | WitType.F32 -> 4
    | WitType.S64
    | WitType.U64
    | WitType.F64
    | WitType.String
    | WitType.List _ -> 8
    | WitType.Record _
    | WitType.Tuple _ ->
        let items = WitType.children ty
        match items with
        | [] -> 0
        | items -> alignTo (alignment ty) (List.last (offsets items) + size (List.last items))
    | WitType.Variant(_, cases) ->
        let payloads = cases |> List.choose snd |> List.map size |> List.fold max 0
        alignTo (alignment ty) (payloadOffset cases + payloads)

/// Where each of `items`, laid out one after another as a record's fields are, is.
and offsets (items: WitType list) =
    items
    |> List.fold (fun (next, acc) item -> let at = alignTo (alignment item) next in at + size item, at :: acc) (0, [])
    |> snd
    |> List.rev

/// Where a variant of `cases` keeps its payload: after its discriminant.
and payloadOffset (cases: (string * WitType option) list) = alignTo (payloadAlignment cases) (discriminantSize cases)

/// The core type that holds either of two, where a variant's cases pass different ones in one place.
let private join (a: string) (b: string) =
    if a = b then a
    elif (a = "i32" && b = "f32") || (a = "f32" && b = "i32") then "i32"
    else "i64"

/// The core types a value of `ty` is passed as: a variant as its discriminant, then the values of the largest of its
/// payloads, each place joined (`join`) over the cases.
let rec flat (ty: WitType) : string list =
    match ty with
    | WitType.S64
    | WitType.U64 -> [ "i64" ]
    | WitType.F32 -> [ "f32" ]
    | WitType.F64 -> [ "f64" ]
    | WitType.String
    | WitType.List _ -> [ "i32"; "i32" ]
    | WitType.Record _
    | WitType.Tuple _ -> WitType.children ty |> List.collect flat
    | WitType.Variant(_, cases) ->
        let payloads = cases |> List.choose snd |> List.map flat
        let width = payloads |> List.map List.length |> List.fold max 0
        let at (index: int) = payloads |> List.choose (List.tryItem index) |> List.reduce join
        "i32" :: List.init width at
    | WitType.Bool
    | WitType.S8
    | WitType.S16
    | WitType.S32
    | WitType.U8
    | WitType.U16
    | WitType.U32 -> [ "i32" ]

/// Whether a value of `ty` in memory points to blocks of its own: a string's bytes or a list's elements.
let rec ownsBlocks (ty: WitType) =
    match ty with
    | WitType.String
    | WitType.List _ -> true
    | ty -> WitType.children ty |> List.exists ownsBlocks

let private loadScalar (ty: WitType) =
    match ty with
    | WitType.Bool
    | WitType.U8 -> "i32.load8_u"
    | WitType.S8 -> "i32.load8_s"
    | WitType.U16 -> "i32.load16_u"
    | WitType.S16 -> "i32.load16_s"
    | WitType.S64
    | WitType.U64 -> "i64.load"
    | WitType.F32 -> "f32.load"
    | WitType.F64 -> "f64.load"
    | _ -> "i32.load"

let private storeScalar (ty: WitType) =
    match ty with
    | WitType.Bool
    | WitType.U8
    | WitType.S8 -> "i32.store8"
    | WitType.U16
    | WitType.S16 -> "i32.store16"
    | WitType.S64
    | WitType.U64 -> "i64.store"
    | WitType.F32 -> "f32.store"
    | WitType.F64 -> "f64.store"
    | _ -> "i32.store"

/// The discriminant of a variant of `cases` as an instruction reads it, or writes it.
let private loadDiscriminant (cases: (string * WitType option) list) =
    match discriminantSize cases with
    | 1 -> "i32.load8_u"
    | 2 -> "i32.load16_u"
    | _ -> "i32.load"

let private storeDiscriminant (cases: (string * WitType option) list) =
    match discriminantSize cases with
    | 1 -> "i32.store8"
    | 2 -> "i32.store16"
    | _ -> "i32.store"

let private at (instruction: string) (offset: int) = if offset = 0 then instruction else $"{instruction} offset={offset}"

/// The conversion of a core value of type `from` to `into`, where a variant's case reads a value its place joined.
let private convert (from: string) (into: string) =
    match from, into with
    | from, into when from = into -> []
    | "i64", "i32" -> [ "i32.wrap_i64" ]
    | "i32", "f32" -> [ "f32.reinterpret_i32" ]
    | "i64", "f32" -> [ "i32.wrap_i64"; "f32.reinterpret_i32" ]
    | "i64", "f64" -> [ "f64.reinterpret_i64" ]
    | from, into -> internalError $"a {from} read as a {into}"

/// The fields of a Nanyx record, as the backend stores them.
let private recordFields (ir: IrType) =
    match IrType.unfold ir with
    | IrRecord fields -> fields
    | other -> internalError $"a WIT record or tuple of type '{IrType.show other}'"

/// The Nanyx type of the payload of the tag `tag` of a union of `ir`.
let private payloadType (ir: IrType) (tag: string) =
    match IrType.unfold ir with
    | IrTags tags ->
        match tags |> List.tryFind (fst >> (=) tag) |> Option.bind snd with
        | Some payload -> payload
        | None -> internalError $"no payload for the tag '#{tag}'"
    | other -> internalError $"a WIT variant of type '{IrType.show other}'"

let private listElement (ir: IrType) =
    match IrType.unfold ir with
    | IrList element -> element
    | other -> internalError $"a WIT list of type '{IrType.show other}'"

/// The Nanyx type of the WIT record or tuple `ty`'s fields, in its order, each with its slot in the Nanyx record.
let private fieldsOf (ty: WitType) (ir: IrType) =
    let fields = recordFields ir
    let names =
        match ty with
        | WitType.Record(_, witFields) -> List.map fst witFields
        | _ -> WitType.children ty |> List.mapi (fun index _ -> string index)
    names
    |> List.map (fun name ->
        match fields |> List.tryFindIndex (fst >> (=) name) with
        | Some slot -> slot, snd fields.[slot]
        | None -> internalError $"no field '{name}' in a record of type '{IrType.show ir}'")

/// Writes the code of one function that lifts and lowers values, with the locals it needs and the helpers it calls.
/// `tagCode` gives the code of a tag, and `recordLayout` the info word of a record of the fields named.
type Generator(tagCode: string -> int, recordLayout: string list -> int, useHelper: string -> unit) =
    let locals = ResizeArray<string * string>()

    /// A new local of the core type `core`.
    member _.Local(core: string) =
        let name = $"$__c{locals.Count}"
        locals.Add((name, core))
        name

    /// The declarations of the locals made.
    member _.Declarations = locals |> Seq.map (fun (name, core) -> $"(local {name} {core})") |> List.ofSeq

    /// A call of the runtime helper `name`.
    member _.Call(name: string) =
        useHelper name
        $"call ${name}"

    member this.Storing (name: string) (ir: IrType) = this.Call(Helpers.forStorage name (CoreIrLayout.storage ir))

    /// The code that drops the Nanyx value in `local`, of `ir`, if it is on the heap.
    member _.Drop (ir: IrType) (local: string) =
        if CoreIrDrop.isHeapType ir then [ $"local.get {local}"; $"call ${CoreIrDrop.dropName ir}" ] else []

    /// A new block of `bytes` bytes, on the stack, aligned to `alignment`.
    member this.Alloc (alignment: int) (bytes: string list) =
        bytes @ [ this.Call(if alignment > 4 then Helpers.allocAligned8 else Helpers.alloc) ]

    /// The string of the `length` bytes at the address in `address`, a block the module owns, which is freed.
    member this.LiftString (address: string) (length: string) =
        [ $"local.get {address}"
          $"local.get {length}"
          this.Call Helpers.stringFromMemory
          $"local.get {length}"
          "if"
          $"  local.get {address}"
          $"  {this.Call Helpers.free}"
          "end" ]

    /// The list of the `count` elements of `item`, of `ir`, at the address in `address`, a block the module owns, which
    /// is freed with the blocks the elements own. The list is made from its last element.
    member this.LiftList (item: WitType) (ir: IrType) (address: string) (count: string) =
        let element = listElement ir
        let list = this.Local "i32"
        let index = this.Local "i32"
        let at = this.Local "i32"
        let label = list.TrimStart '$'
        [ "i32.const 0"
          $"local.set {list}"
          $"local.get {count}"
          $"local.set {index}"
          $"block $done{label}"
          $"  loop $next{label}"
          $"    local.get {index}"
          "    i32.eqz"
          $"    br_if $done{label}"
          $"    local.get {index}"
          "    i32.const 1"
          "    i32.sub"
          $"    local.set {index}"
          $"    local.get {address}"
          $"    local.get {index}"
          $"    i32.const {size item}"
          "    i32.mul"
          "    i32.add"
          $"    local.set {at}"
          yield! this.LiftMemory item element at 0 |> List.map (fun line -> "    " + line)
          $"    local.get {list}"
          $"    {this.Storing Helpers.listCons element}"
          $"    local.set {list}"
          $"    br $next{label}"
          "  end"
          "end"
          $"local.get {count}"
          "if"
          $"  local.get {address}"
          $"  {this.Call Helpers.free}"
          "end"
          $"local.get {list}" ]

    /// A new Nanyx record of `ir` whose fields, in `slots`, are what `values` leave on the stack, each in turn.
    member this.LiftRecord (ir: IrType) (slots: (int * IrType) list) (values: string list list) =
        let fields = recordFields ir
        let record = this.Local "i32"
        [ $"i32.const {CoreIrLayout.recordSize fields}"
          $"i32.const {recordLayout (List.map fst fields)}"
          this.Call Helpers.newObject
          $"local.set {record}"
          for (slot, fieldIr), value in List.zip slots values do
              $"local.get {record}"
              yield! value
              CoreIrLayout.store fieldIr (CoreIrLayout.fieldOffset fields slot)
          $"local.get {record}" ]

    /// The Nanyx value of the case of `cases` that the discriminant in `discriminant` says, of `ir`, its payload what
    /// `payload` leaves on the stack.
    member this.LiftVariant (ir: IrType) (cases: (string * WitType option) list) (discriminant: string) (payload: string -> WitType -> IrType -> string list) =
        let rec chain (index: int) (remaining: (string * WitType option) list) =
            match remaining with
            | [] -> [ "unreachable" ]
            | (tag, case) :: rest ->
                let value =
                    match case with
                    | None -> [ $"i32.const {tagCode tag}" ]
                    | Some item ->
                        let itemIr = payloadType ir tag
                        [ $"i32.const {tagCode tag}"; yield! payload tag item itemIr; this.Storing Helpers.tagBox itemIr ]
                [ $"local.get {discriminant}"
                  $"i32.const {index}"
                  "i32.eq"
                  "if (result i32)"
                  yield! value |> List.map (fun line -> "  " + line)
                  "else"
                  yield! chain (index + 1) rest |> List.map (fun line -> "  " + line)
                  "end" ]
        chain 0 cases

    /// The Nanyx value, of `ir`, of the value of `ty` at `offset` past the address in `address`, which the module owns,
    /// with the blocks it points to, which are freed.
    member this.LiftMemory (ty: WitType) (ir: IrType) (address: string) (offset: int) : string list =
        match ty with
        | WitType.String
        | WitType.List _ ->
            let pointer = this.Local "i32"
            let count = this.Local "i32"
            [ $"local.get {address}"
              at "i32.load" offset
              $"local.set {pointer}"
              $"local.get {address}"
              at "i32.load" (offset + 4)
              $"local.set {count}"
              match ty with
              | WitType.List item -> yield! this.LiftList item ir pointer count
              | _ -> yield! this.LiftString pointer count ]
        | WitType.Record _
        | WitType.Tuple _ ->
            let items = WitType.children ty
            let slots = fieldsOf ty ir
            let values =
                List.zip3 items slots (offsets items)
                |> List.map (fun (item, (_, itemIr), itemOffset) -> this.LiftMemory item itemIr address (offset + itemOffset))
            this.LiftRecord ir slots values
        | WitType.Variant(_, cases) ->
            let discriminant = this.Local "i32"
            [ $"local.get {address}"
              at (loadDiscriminant cases) offset
              $"local.set {discriminant}"
              yield!
                  this.LiftVariant ir cases discriminant (fun _ item itemIr ->
                      this.LiftMemory item itemIr address (offset + payloadOffset cases)) ]
        | scalar -> [ $"local.get {address}"; at (loadScalar scalar) offset ]

    /// The Nanyx value, of `ir`, of the value of `ty` passed as the core values in the locals `values`, each with its
    /// core type, which come to as many as `flat ty` has.
    member this.LiftFlat (ty: WitType) (ir: IrType) (values: (string * string) list) : string list =
        let read (index: int) (into: string) =
            let local, from = values.[index]
            $"local.get {local}" :: convert from into
        match ty with
        | WitType.String
        | WitType.List _ ->
            let pointer = this.Local "i32"
            let count = this.Local "i32"
            [ yield! read 0 "i32"
              $"local.set {pointer}"
              yield! read 1 "i32"
              $"local.set {count}"
              match ty with
              | WitType.List item -> yield! this.LiftList item ir pointer count
              | _ -> yield! this.LiftString pointer count ]
        | WitType.Record _
        | WitType.Tuple _ ->
            let items = WitType.children ty
            let slots = fieldsOf ty ir
            let starts = items |> List.scan (fun start item -> start + (flat item).Length) 0
            let lifted =
                List.zip3 items slots (List.take items.Length starts)
                |> List.map (fun (item, (_, itemIr), start) -> this.LiftFlat item itemIr (values |> List.skip start |> List.take (flat item).Length))
            this.LiftRecord ir slots lifted
        | WitType.Variant(_, cases) ->
            let discriminant = this.Local "i32"
            [ yield! read 0 "i32"
              $"local.set {discriminant}"
              yield!
                  this.LiftVariant ir cases discriminant (fun _ item itemIr ->
                      let places = values |> List.skip 1 |> List.take (flat item).Length
                      // Each value of the payload is read as its own type from the place its cases share.
                      let converted =
                          List.zip places (flat item)
                          |> List.map (fun ((local, from), into) ->
                              let own = this.Local into
                              [ $"local.get {local}"; yield! convert from into; $"local.set {own}" ], (own, into))
                      (converted |> List.collect fst) @ this.LiftFlat item itemIr (List.map snd converted)) ]
        | scalar -> read 0 (List.head (flat scalar))

    /// The code that writes the Nanyx value in `value`, of `ir`, as a value of `ty` at `offset` past the address in
    /// `address`: a string's bytes and a list's elements copied to new blocks, which the value then points to.
    member this.LowerMemory (ty: WitType) (ir: IrType) (value: string) (address: string) (offset: int) : string list =
        match ty with
        | WitType.String ->
            let length = this.Local "i32"
            let block = this.Local "i32"
            [ $"local.get {value}"
              this.Call Helpers.stringLength
              $"local.set {length}"
              $"local.get {length}"
              "if (result i32)"
              yield! this.Alloc 1 [ $"local.get {length}" ] |> List.map (fun line -> "  " + line)
              "else"
              "  i32.const 0"
              "end"
              $"local.set {block}"
              $"local.get {block}"
              $"local.get {value}"
              $"local.get {length}"
              this.Call Helpers.copyBytes
              $"local.get {address}"
              $"local.get {block}"
              at "i32.store" offset
              $"local.get {address}"
              $"local.get {length}"
              at "i32.store" (offset + 4) ]
        | WitType.List item ->
            let element = listElement ir
            let count = this.Local "i32"
            let cell = this.Local "i32"
            let block = this.Local "i32"
            let index = this.Local "i32"
            let at' = this.Local "i32"
            let head = this.Local(CoreIrLayout.valueType element)
            let label = count.TrimStart '$'
            let walk (body: string list) =
                [ $"local.get {value}"
                  $"local.set {cell}"
                  $"block $done{label}"
                  $"  loop $next{label}"
                  $"    local.get {cell}"
                  "    i32.eqz"
                  $"    br_if $done{label}"
                  yield! body |> List.map (fun line -> "    " + line)
                  $"    local.get {cell}"
                  $"    i32.load offset={CoreIrLayout.tailOffset element}"
                  $"    local.set {cell}"
                  $"    br $next{label}"
                  "  end"
                  "end" ]
            [ "i32.const 0"
              $"local.set {count}"
              yield! walk [ $"local.get {count}"; "i32.const 1"; "i32.add"; $"local.set {count}" ]
              $"local.get {count}"
              "if (result i32)"
              yield! this.Alloc (alignment item) [ $"local.get {count}"; $"i32.const {size item}"; "i32.mul" ] |> List.map (fun line -> "  " + line)
              "else"
              "  i32.const 0"
              "end"
              $"local.set {block}"
              "i32.const 0"
              $"local.set {index}"
              yield!
                  walk
                      [ $"local.get {block}"
                        $"local.get {index}"
                        $"i32.const {size item}"
                        "i32.mul"
                        "i32.add"
                        $"local.set {at'}"
                        $"local.get {cell}"
                        CoreIrLayout.load element 0
                        $"local.set {head}"
                        yield! this.LowerMemory item element head at' 0
                        $"local.get {index}"
                        "i32.const 1"
                        "i32.add"
                        $"local.set {index}" ]
              $"local.get {address}"
              $"local.get {block}"
              at "i32.store" offset
              $"local.get {address}"
              $"local.get {count}"
              at "i32.store" (offset + 4) ]
        | WitType.Record _
        | WitType.Tuple _ ->
            let items = WitType.children ty
            let fields = recordFields ir
            List.zip3 items (fieldsOf ty ir) (offsets items)
            |> List.collect (fun (item, (slot, itemIr), itemOffset) ->
                let field = this.Local(CoreIrLayout.valueType itemIr)
                [ $"local.get {value}"
                  CoreIrLayout.load itemIr (CoreIrLayout.fieldOffset fields slot)
                  $"local.set {field}"
                  yield! this.LowerMemory item itemIr field address (offset + itemOffset) ])
        | WitType.Variant(_, cases) ->
            let code = this.Local "i32"
            let rec chain (index: int) (remaining: (string * WitType option) list) =
                match remaining with
                | [] -> [ "unreachable" ]
                | (tag, case) :: rest ->
                    let payload =
                        match case with
                        | None -> []
                        | Some item ->
                            let itemIr = payloadType ir tag
                            let held = this.Local(CoreIrLayout.valueType itemIr)
                            [ $"local.get {value}"
                              CoreIrLayout.load itemIr CoreIrLayout.payloadOffset
                              $"local.set {held}"
                              yield! this.LowerMemory item itemIr held address (offset + payloadOffset cases) ]
                    [ $"local.get {code}"
                      $"i32.const {tagCode tag}"
                      "i32.eq"
                      "if"
                      $"  local.get {address}"
                      $"  i32.const {index}"
                      $"  {at (storeDiscriminant cases) offset}"
                      yield! payload |> List.map (fun line -> "  " + line)
                      "else"
                      yield! chain (index + 1) rest |> List.map (fun line -> "  " + line)
                      "end" ]
            [ $"local.get {value}"
              this.Call Helpers.tagCode
              $"local.set {code}"
              yield! chain 0 cases ]
        | scalar -> [ $"local.get {address}"; $"local.get {value}"; at (storeScalar scalar) offset ]

    /// The core value the Nanyx value in `value`, of `ir`, is passed as, a value of `ty`, which is passed as at most one
    /// (`flat ty`): a number, or a record of one, or a variant whose cases have no payloads.
    member this.LowerFlat (ty: WitType) (ir: IrType) (value: string) : string list =
        match ty with
        | WitType.Record _
        | WitType.Tuple _ ->
            let fields = recordFields ir
            List.zip (WitType.children ty) (fieldsOf ty ir)
            |> List.collect (fun (item, (slot, itemIr)) ->
                let field = this.Local(CoreIrLayout.valueType itemIr)
                [ $"local.get {value}"
                  CoreIrLayout.load itemIr (CoreIrLayout.fieldOffset fields slot)
                  $"local.set {field}"
                  yield! this.LowerFlat item itemIr field ])
        | WitType.Variant(_, cases) ->
            let rec chain (index: int) (remaining: (string * WitType option) list) =
                match remaining with
                | [] -> [ "unreachable" ]
                | (tag, _) :: rest ->
                    [ $"local.get {value}"
                      $"i32.const {tagCode tag}"
                      "i32.eq"
                      "if (result i32)"
                      $"  i32.const {index}"
                      "else"
                      yield! chain (index + 1) rest |> List.map (fun line -> "  " + line)
                      "end" ]
            chain 0 cases
        | WitType.String
        | WitType.List _ -> internalError "a string or a list passed as one core value"
        | _ -> [ $"local.get {value}" ]

    /// The code that frees the blocks the value of `ty` at `offset` past the address in `address` points to, which
    /// `LowerMemory` made.
    member this.FreeMemory (ty: WitType) (address: string) (offset: int) : string list =
        if not (ownsBlocks ty) then []
        else
            match ty with
            | WitType.String ->
                // An empty string has no block.
                let block = this.Local "i32"
                [ $"local.get {address}"
                  at "i32.load" offset
                  $"local.tee {block}"
                  "if"
                  $"  local.get {block}"
                  $"  {this.Call Helpers.free}"
                  "end" ]
            | WitType.List item ->
                let block = this.Local "i32"
                let index = this.Local "i32"
                let at' = this.Local "i32"
                let label = block.TrimStart '$'
                [ $"local.get {address}"
                  at "i32.load" offset
                  $"local.set {block}"
                  if ownsBlocks item then
                      $"local.get {address}"
                      at "i32.load" (offset + 4)
                      $"local.set {index}"
                      $"block $done{label}"
                      $"  loop $next{label}"
                      $"    local.get {index}"
                      "    i32.eqz"
                      $"    br_if $done{label}"
                      $"    local.get {index}"
                      "    i32.const 1"
                      "    i32.sub"
                      $"    local.set {index}"
                      $"    local.get {block}"
                      $"    local.get {index}"
                      $"    i32.const {size item}"
                      "    i32.mul"
                      "    i32.add"
                      $"    local.set {at'}"
                      yield! this.FreeMemory item at' 0 |> List.map (fun line -> "    " + line)
                      $"    br $next{label}"
                      "  end"
                      "end"
                  $"local.get {block}"
                  "if"
                  $"  local.get {block}"
                  $"  {this.Call Helpers.free}"
                  "end" ]
            | WitType.Record _
            | WitType.Tuple _ ->
                let items = WitType.children ty
                List.zip items (offsets items) |> List.collect (fun (item, itemOffset) -> this.FreeMemory item address (offset + itemOffset))
            | WitType.Variant(_, cases) ->
                let discriminant = this.Local "i32"
                [ $"local.get {address}"
                  at (loadDiscriminant cases) offset
                  $"local.set {discriminant}"
                  for index, (_, case) in List.indexed cases do
                      match case with
                      | Some item when ownsBlocks item ->
                          $"local.get {discriminant}"
                          $"i32.const {index}"
                          "i32.eq"
                          "if"
                          yield! this.FreeMemory item address (offset + payloadOffset cases) |> List.map (fun line -> "  " + line)
                          "end"
                      | _ -> () ]
            | _ -> []
