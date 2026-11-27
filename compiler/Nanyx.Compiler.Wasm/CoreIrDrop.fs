/// The drop and dup functions of a module: for each type of object on the heap that its functions use, one that gives
/// up a reference to a value of that type and, when it was the last, drops the object's fields and frees it, and one
/// that takes a reference (see "Object header" in `docs/src/content/deep-dives/memory-management.md`). Every type is concrete, so each drop
/// function knows which of its object's fields are pointers.
module Transpiler.Wasm.CoreIrDrop

open System.Collections.Generic
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.WatText

/// Whether a value of this type can be a pointer to an object on the heap. A union of tags is one when some tag has a
/// payload, though a value of it is a pointer only when it is not a code.
let rec isHeapType (ty: IrType) =
    match ty with
    | IrRecursive _ -> isHeapType (IrType.unfold ty)
    | IrSelf _ -> internalError "a recursive type's own type outside it"
    | IrString
    | IrList _
    | IrArray _
    | IrRecord _
    | IrFunction _
    | IrBox -> true
    | IrTags tags -> tags |> List.exists (snd >> Option.isSome)
    | IrInt
    | IrSized _
    | IrFloat
    | IrF32
    | IrBool
    | IrUnit
    | IrError
    | IrWord -> false

/// Whether a value of `ty` is a tag of a union with no payloads, which is its own code: never a cell, so its code is read
/// with no call of `Helpers.tagCode`.
let tagIsCode (ty: IrType) =
    match IrType.unfold ty with
    | IrTags _ -> not (isHeapType ty)
    | _ -> false

/// The name of the drop function of a type on the heap.
let dropName (ty: IrType) = "__nanyx_drop_" + sanitizeIdentifier (IrType.mangle ty)

/// Whether the drop function of `ty`, called while another drop is under way, puts an object whose last reference it
/// gives up on a list to drop later, rather than dropping its fields at once: a recursive type's does, and a
/// function value's, so that dropping a value nested deeply takes no stack. Every chain of drop functions that calls
/// itself goes through one of them: a function value's calls its environment's, which may drop a function value.
let private defers (ty: IrType) =
    match ty with
    | IrRecursive _
    | IrFunction _
    | IrBox -> true
    | _ -> false

/// The name of the function that drops an object of a type that `defers` whose last reference has been given up: its
/// fields, then the object itself.
let private deadDropName (ty: IrType) = "__nanyx_drop_dead_" + sanitizeIdentifier (IrType.mangle ty)

/// The global that holds the first of the objects of a type that `defers` waiting to be dropped. Each holds the next in
/// its count, which it no longer needs.
let private pendingName (ty: IrType) = "__nanyx_drop_pending_" + sanitizeIdentifier (IrType.mangle ty)

/// The global that is 1 while a drop is under way, so that a drop function called inside it leaves its object waiting.
let private droppingName = "__nanyx_dropping"

/// The function that drops the objects waiting, until there are none.
let private drainName = "__nanyx_drop_pending"

/// The name of the dup function of a type on the heap, which takes one more reference to its value and leaves it.
let dupName (ty: IrType) = "__nanyx_dup_" + sanitizeIdentifier (IrType.mangle ty)

/// The name of the function `++` calls on lists of an element on the heap: it copies the first list's cells, so it
/// takes a reference to each element it copies.
let listConcatName (element: IrType) = "__nanyx_list_concat_" + sanitizeIdentifier (IrType.mangle element)

/// The name of the function that drops what a closure captures, by the types of its captures in the order its block
/// holds them. Closures that capture nothing on the heap share one that drops nothing.
let environmentDropName (captured: IrType list) =
    if captured |> List.exists isHeapType then
        "__nanyx_drop_env" + (captured |> List.map (IrType.mangle >> sanitizeIdentifier >> sprintf "__%s") |> String.concat "")
    else
        "__nanyx_drop_env"

/// What a module drops: the types on the heap its functions use (with the types of their fields), what its
/// closures capture, and the elements of the lists on the heap it joins with `++`.
type Drops =
    { Types: IrType list
      Environments: IrType list list
      ListConcats: IrType list }

/// The drops of a module made of `functions`, each in the order the functions first use it.
let collect (functions: Function list) : Drops =
    let types = ResizeArray<IrType>()
    let seen = HashSet<IrType>()
    let environments = ResizeArray<IrType list>()
    let listConcats = ResizeArray<IrType>()

    let rec add (ty: IrType) =
        if isHeapType ty && seen.Add ty then
            types.Add ty
            match IrType.unfold ty with
            | IrList element
            | IrArray element -> add element
            | IrRecord fields -> fields |> List.iter (snd >> add)
            | IrTags tags -> tags |> List.iter (snd >> Option.iter add)
            | _ -> ()

    let variable (v: Var) = add v.Type

    // The expression's own type and what it binds, then the expressions inside it (see `Expr.children`), the last in
    // tail position, so that a chain as long as a function takes no stack to walk.
    let rec expr (e: Expr) =
        add e.Type
        match e.Kind with
        | MakeClosure(_, _, captured) ->
            let capturedTypes = captured |> List.map atomType
            capturedTypes |> List.iter add
            if not (environments.Contains capturedTypes) then environments.Add capturedTypes
        | Let(v, _, _) -> variable v
        | Match(scrutinees, arms) ->
            scrutinees |> List.iter variable
            for arm in arms do
                for p in arm.Patterns do
                    Pattern.binds p |> List.iter variable
                    Pattern.guards p |> List.iter (fst >> variable)
        | ForEach loop -> loop.Streams |> List.iter (fun stream -> variable stream.Element)
        | CallRuntime(name, [ first; _ ], _) when name = Helpers.listConcat ->
            match atomType first with
            | IrList element when isHeapType element && not (listConcats.Contains element) -> listConcats.Add element
            | _ -> ()
        // The text a probe's printer writes a local as, which it gives up once the runtime has it.
        | Probe(_, locals) when not locals.IsEmpty -> add IrString
        | _ -> ()
        match Expr.children e with
        | [] -> ()
        | children ->
            for child in List.take (children.Length - 1) children do
                expr child
            expr (List.last children)

    for func in functions do
        func.Params |> List.iter variable
        add func.Result
        expr func.Body
    { Types = List.ofSeq types
      Environments = List.ofSeq environments
      ListConcats = List.ofSeq listConcats }

/// The runtime helpers the functions of `drops` call.
let helpersUsed (drops: Drops) : string list =
    [ if not drops.Types.IsEmpty || not drops.Environments.IsEmpty then
          Helpers.release
          Helpers.retain
          Helpers.free
      if not drops.ListConcats.IsEmpty then Helpers.listCons
      if drops.Types |> List.exists (function CoreIrRegions.RegionBox _ -> true | _ -> false) then WasmRegions.Names.regionUnlink ]

/// The code that drops the value at `load` (which leaves it on the stack), if it is of a type on the heap.
let private dropField (ty: IrType) (load: string list) =
    if isHeapType ty then load @ [ $"call ${dropName ty}" ] else []

/// The code that frees the object at `$p`, a value of type `ty`, once its count has run out.
let private free (ty: IrType) =
    [ "local.get $p"; $"i32.const {CoreIrLayout.headerSize ty}"; "i32.sub"; $"call ${Helpers.free}" ]

/// Gives up the reference `$p` and, when it was the last, runs `fields` and frees the object.
let private releaseThen (ty: IrType) (fields: string list) =
    [ "local.get $p"
      $"i32.const {CoreIrLayout.headerSize ty}"
      "i32.sub"
      $"call ${Helpers.release}"
      "if"
      yield! fields |> List.map (fun line -> "  " + line)
      yield! free ty |> List.map (fun line -> "  " + line)
      "end" ]

/// The locals, and the code, that drop the fields on the heap of the object at `$p`, a value of `ty` (not a list) whose
/// last reference has been given up, before it is freed.
let private fieldDrops (tagId: string -> int option) (ty: IrType) : string list * string list =
    match IrType.unfold ty with
    | IrString -> [], []
    | IrRecord fields ->
        // A box comes off its region's list first (see `CoreIrRegions`).
        let unlink =
            match ty with
            | CoreIrRegions.RegionBox _ -> [ "local.get $p"; $"call ${WasmRegions.Names.regionUnlink}" ]
            | _ -> []
        [],
        unlink
        @ (fields
           |> List.mapi (fun slot (_, fieldTy) -> dropField fieldTy [ "local.get $p"; CoreIrLayout.load fieldTy (CoreIrLayout.fieldOffset fields slot) ])
           |> List.concat)
    | IrFunction _
    | IrBox ->
        // The function in the closure's info word drops what it captures.
        [], [ "local.get $p"; "local.get $p"; $"i32.const {Header.infoBefore}"; "i32.sub"; "i32.load"; "call_indirect (type $ctx_fn_1)"; "drop" ]
    | IrTags tags ->
        // A cell holds the code, then the payload: when every tag with a payload has one of the same type, the payload
        // needs no code to tell its type.
        let payloads = tags |> List.choose (fun (name, payload) -> payload |> Option.map (fun payloadTy -> name, payloadTy))
        let payloadDrop payloadTy = dropField payloadTy [ "local.get $p"; CoreIrLayout.load IrWord CoreIrLayout.payloadOffset ]
        [],
        match payloads |> List.map snd |> List.distinct with
        | [ single ] -> payloadDrop single
        | _ ->
            payloads
            |> List.filter (snd >> isHeapType)
            |> List.choose (fun (name, payloadTy) -> tagId name |> Option.map (fun id -> id, payloadTy))
            |> List.collect (fun (id, payloadTy) ->
                [ "local.get $p"
                  "i32.load"
                  $"i32.const {tagCode id}"
                  "i32.eq"
                  "if"
                  yield! payloadDrop payloadTy |> List.map (fun line -> "  " + line)
                  "end" ])
    | IrArray element when not (isHeapType element) -> [], []
    | IrArray element ->
        // Each element, from the first to the last, after the length.
        let size = CoreIrLayout.size element
        [ "(local $at i32)"; "(local $end i32)" ],
        [ "local.get $p"
          $"i32.const {CoreIrLayout.arrayElementOffset element 0}"
          "i32.add"
          "local.tee $at"
          "local.get $p"
          $"i32.load offset={CoreIrLayout.arrayLengthOffset}"
          $"i32.const {size}"
          "i32.mul"
          "i32.add"
          "local.set $end"
          "block $done"
          "  loop $next"
          "    local.get $at"
          "    local.get $end"
          "    i32.ge_u"
          "    br_if $done"
          yield! dropField element [ "local.get $at"; CoreIrLayout.load element 0 ] |> List.map (fun line -> "    " + line)
          "    local.get $at"
          $"    i32.const {size}"
          "    i32.add"
          "    local.set $at"
          "    br $next"
          "  end"
          "end" ]
    | IrList _
    | IrInt
    | IrSized _
    | IrFloat
    | IrF32
    | IrBool
    | IrUnit
    | IrError
    | IrWord
    | IrRecursive _
    | IrSelf _ -> internalError $"the fields of '{IrType.show ty}', which is not an object with fields"

/// The body of the drop function of `ty`, which takes the value in `$p`.
let private dropBody (tagId: string -> int option) (ty: IrType) : string list =
    match IrType.unfold ty with
    // A code is not on the heap.
    | IrTags _ -> [ "local.get $p"; "i32.const 1"; "i32.and"; "if"; "  return"; "end"; yield! releaseThen ty (snd (fieldDrops tagId ty)) ]
    | IrList element ->
        // Cell by cell along the list, as long as each was held only by the one before it, so that a long list takes
        // no stack to drop.
        [ "(local $tail i32)"
          "block $done"
          "  loop $next"
          "    local.get $p"
          "    i32.eqz"
          "    br_if $done"
          "    local.get $p"
          $"    i32.const {CoreIrLayout.headerSize ty}"
          "    i32.sub"
          $"    call ${Helpers.release}"
          "    i32.eqz"
          "    br_if $done"
          yield! dropField element [ "local.get $p"; "i32.load" ] |> List.map (fun line -> "    " + line)
          "    local.get $p"
          $"    i32.load offset={CoreIrLayout.tailOffset element}"
          "    local.set $tail"
          yield! free ty |> List.map (fun line -> "    " + line)
          "    local.get $tail"
          "    local.set $p"
          "    br $next"
          "  end"
          "end" ]
    | IrString
    | IrRecord _
    | IrFunction _
    | IrBox
    | IrArray _ ->
        let locals, fields = fieldDrops tagId ty
        locals @ releaseThen ty fields
    | IrInt
    | IrSized _
    | IrFloat
    | IrF32
    | IrBool
    | IrUnit
    | IrError
    | IrWord
    | IrRecursive _
    | IrSelf _ -> internalError $"a drop function for '{IrType.show ty}', which is not on the heap"

/// The body of the function that drops the object at `$p`, a value of `ty`, a type that `defers`, whose last reference
/// has been given up: its fields, then the object. A list's tail is given up as any other list would be.
let private deadDropBody (tagId: string -> int option) (ty: IrType) : string list =
    match IrType.unfold ty with
    | IrList element ->
        [ "(local $tail i32)"
          yield! dropField element [ "local.get $p"; "i32.load" ]
          "local.get $p"
          $"i32.load offset={CoreIrLayout.tailOffset element}"
          "local.set $tail"
          yield! free ty
          "local.get $tail"
          $"call ${dropName ty}" ]
    | _ ->
        let locals, fields = fieldDrops tagId ty
        locals @ fields @ free ty

/// The code that leaves 1 when the value in `$p`, of type `ty`, is not an object on the heap (an empty list or a tag's
/// code), and 0 when it is; nothing when every value of `ty` is one.
let private notOnHeap (ty: IrType) =
    match IrType.unfold ty with
    | IrList _ -> [ "local.get $p"; "i32.eqz" ]
    | IrTags _ -> [ "local.get $p"; "i32.const 1"; "i32.and" ]
    | _ -> []

/// The body of the drop function of a type that `defers`, which takes the value in `$p`. It gives up the reference
/// and, when it was the last: while a drop is under way, puts the object on the type's list of those waiting, and
/// otherwise drops it at once, then the objects that left waiting.
let private deferringDropBody (ty: IrType) : string list =
    let count = [ "local.get $p"; $"i32.const {CoreIrLayout.headerSize ty}"; "i32.sub" ]
    [ match notOnHeap ty with
      | [] -> ()
      | test -> yield! test @ [ "if"; "  return"; "end" ]
      yield! count
      $"call ${Helpers.release}"
      "i32.eqz"
      "if"
      "  return"
      "end"
      $"global.get ${droppingName}"
      "if"
      yield! count |> List.map (fun line -> "  " + line)
      $"  global.get ${pendingName ty}"
      "  i32.store"
      "  local.get $p"
      $"  global.set ${pendingName ty}"
      "  return"
      "end"
      "i32.const 1"
      $"global.set ${droppingName}"
      "local.get $p"
      $"call ${deadDropName ty}"
      $"call ${drainName}"
      "i32.const 0"
      $"global.set ${droppingName}" ]

/// The function that drops the objects waiting of each of `deferred`, the types that `defers`, until there are none.
/// Dropping one may leave more waiting, of its own type or another.
let private drainFunction (deferred: IrType list) : string list =
    [ $"(func ${drainName}"
      "  (local $p i32)"
      "  loop $next"
      yield!
          deferred
          |> List.collect (fun ty ->
              [ $"global.get ${pendingName ty}"
                "local.tee $p"
                "if"
                "  local.get $p"
                $"  i32.const {CoreIrLayout.headerSize ty}"
                "  i32.sub"
                "  i32.load"
                $"  global.set ${pendingName ty}"
                "  local.get $p"
                $"  call ${deadDropName ty}"
                "  br $next"
                "end" ])
          |> List.map (fun line -> "    " + line)
      "  end"
      ")" ]

/// The body of the dup function of `ty`, which takes the value in `$p` and leaves it.
let private dupBody (ty: IrType) : string list =
    let notOnHeap =
        match notOnHeap ty with
        | [] -> []
        | test -> test @ [ "br_if $shared" ]
    [ "block $shared"
      yield! notOnHeap |> List.map (fun line -> "  " + line)
      "  local.get $p"
      $"  i32.const {CoreIrLayout.headerSize ty}"
      "  i32.sub"
      $"  call ${Helpers.retain}"
      "end"
      "local.get $p" ]


/// The functions of `drops`, as WebAssembly text: the drop and dup functions of each type, the function that drops
/// each closure environment, and the `++` of each list. The function that drops a closure's captures is called through
/// the function table, so it has the type of a closure of one parameter (`$ctx_fn_1`), and returns 0. `tagId` gives
/// the number of each tag the module numbers; a tag it does not is never made, so has no cells to drop. A type that
/// `defers` has two drop functions, the one others call, which leaves its object waiting during a drop, and the one
/// that drops an object whose last reference is given up, and the module then has the globals and the function that
/// drop the objects waiting.
let render (tagId: string -> int option) (drops: Drops) : string list =
    let typeDrops =
        drops.Types
        |> List.collect (fun ty ->
            [ if defers ty then
                  lines [
                      $"(func ${dropName ty} (param $p i32)"
                      $"  ;; {IrType.show ty}"
                      yield! deferringDropBody ty |> List.map (fun line -> "  " + line)
                      ")" ]
                  lines [
                      $"(func ${deadDropName ty} (param $p i32)"
                      yield! deadDropBody tagId ty |> List.map (fun line -> "  " + line)
                      ")" ]
              else
                  lines [
                      $"(func ${dropName ty} (param $p i32)"
                      $"  ;; {IrType.show ty}"
                      yield! dropBody tagId ty |> List.map (fun line -> "  " + line)
                      ")" ]
              lines [
                  $"(func ${dupName ty} (param $p i32) (result i32)"
                  yield! dupBody ty |> List.map (fun line -> "  " + line)
                  ")" ] ])
    let listConcats =
        drops.ListConcats
        |> List.map (fun element -> listConcatFunction (listConcatName element) [ $"call ${dupName element}" ] (CoreIrLayout.storage element))
    let environmentDrops =
        drops.Environments
        |> List.distinctBy environmentDropName
        |> List.map (fun captured ->
            lines [
                $"(func ${environmentDropName captured} (param $p i32) (result i32)"
                yield!
                    List.zip captured (CoreIrLayout.closureOffsets captured).Tail
                    |> List.map (fun (capturedTy, offset) -> dropField capturedTy [ "local.get $p"; CoreIrLayout.load capturedTy offset ])
                    |> List.concat
                    |> List.map (fun line -> "  " + line)
                "  i32.const 0"
                ")" ])
    // The function that drops what a box holds when its region ends, called through the function table (see
    // `CoreIrRegions`).
    let contentsDrops =
        drops.Types
        |> List.choose (fun ty ->
            match ty with
            | CoreIrRegions.RegionBox held ->
                let fields = match ty with IrRecord fields -> fields | _ -> []
                let offset = CoreIrLayout.fieldOffset fields CoreIrRegions.boxValueSlot
                Some (
                    lines [
                        $"(func ${CoreIrRegions.dropContentsName ty} (param $p i32) (result i32)"
                        yield! dropField held [ "local.get $p"; CoreIrLayout.load held offset ] |> List.map (fun line -> "  " + line)
                        "  i32.const 0"
                        ")" ])
            | _ -> None)
    let pending =
        match drops.Types |> List.filter defers with
        | [] -> []
        | deferred ->
            [ lines [
                  $"(global ${droppingName} (mut i32) (i32.const 0))"
                  yield! deferred |> List.map (fun ty -> $"(global ${pendingName ty} (mut i32) (i32.const 0))") ]
              lines (drainFunction deferred) ]
    typeDrops @ environmentDrops @ listConcats @ contentsDrops @ pending
