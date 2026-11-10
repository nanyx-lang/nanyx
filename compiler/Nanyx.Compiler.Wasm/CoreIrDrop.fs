/// The drop and dup functions of a module: for each type of object on the heap that its functions use, one that gives
/// up a reference to a value of that type and, when it was the last, drops the object's fields and frees it, and one
/// that takes a reference (see "Object header" in `compiler/design/memory.md`). Every type is concrete, so each drop
/// function knows which of its object's fields are pointers.
module Transpiler.Wasm.CoreIrDrop

open System.Collections.Generic
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr

/// Whether a value of this type can be a pointer to an object on the heap. A union of tags is one when some tag has a
/// payload, though a value of it is a pointer only when it is not a code.
let isHeapType (ty: IrType) =
    match ty with
    | IrString
    | IrFloat
    | IrList _
    | IrRecord _
    | IrFunction _ -> true
    | IrTags tags -> tags |> List.exists (snd >> Option.isSome)
    | IrInt
    | IrBool
    | IrUnit
    | IrError -> false

/// How far before an object's pointer its count is: 8 for an object with an info word (a string, a record, a
/// closure), else 4.
let countOffset (ty: IrType) =
    match ty with
    | IrString
    | IrRecord _
    | IrFunction _ -> 8
    | _ -> 4

/// The name of the drop function of a type on the heap.
let dropName (ty: IrType) = "__nanyx_drop_" + sanitizeIdentifier (IrType.mangle ty)

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
            match ty with
            | IrList element -> add element
            | IrRecord fields -> fields |> List.iter (snd >> add)
            | IrTags tags -> tags |> List.iter (snd >> Option.iter add)
            | _ -> ()

    let variable (v: Var) = add v.Type

    let rec pattern (p: Pattern) =
        match p with
        | PBind v -> variable v
        | PGuard(v, guard) ->
            variable v
            expr guard
        | PTag(_, payload) -> payload |> Option.iter pattern
        | PFields fields -> fields |> List.iter (snd >> pattern)
        | PAll parts -> parts |> List.iter pattern
        | PList(elements, rest) ->
            elements |> List.iter pattern
            rest |> Option.iter pattern
        | PWildcard
        | PInt _
        | PBool _
        | PString _ -> ()

    and expr (e: Expr) =
        add e.Type
        match e.Kind with
        | MakeClosure(_, _, captured) ->
            let capturedTypes = captured |> List.map atomType
            capturedTypes |> List.iter add
            if not (environments.Contains capturedTypes) then environments.Add capturedTypes
        | Let(v, value, body) ->
            variable v
            expr value
            expr body
        | Do(first, rest) ->
            expr first
            expr rest
        | If(_, ifTrue, ifFalse) ->
            expr ifTrue
            expr ifFalse
        | Match(scrutinees, arms) ->
            scrutinees |> List.iter variable
            for arm in arms do
                arm.Patterns |> List.iter pattern
                expr arm.Body
        | ForEach loop ->
            loop.Streams |> List.iter (fun stream -> variable stream.Element)
            expr loop.Body
        | CallRuntime(name, [ first; _ ], _) when name = Helpers.listConcat ->
            match atomType first with
            | IrList element when isHeapType element && not (listConcats.Contains element) -> listConcats.Add element
            | _ -> ()
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Release(_, body)
        | Reuse(_, body) -> expr body
        | Atom _
        | Prim _
        | Call _
        | MakeTag _
        | MakeRecord _
        | Field _
        | UpdateRecord _
        | MakeList _
        | CallRuntime _
        | CallClosure _
        | Interpolate _
        | Dbg _
        | BoxFloat _
        | Panic _ -> ()

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
      if not drops.ListConcats.IsEmpty then Helpers.listCons ]

/// The code that drops the value at `load` (which leaves it on the stack), if it is of a type on the heap.
let private dropField (ty: IrType) (load: string list) =
    if isHeapType ty then load @ [ $"call ${dropName ty}" ] else []

/// The code that frees the object at `$p`, a value of type `ty`, once its count has run out.
let private free (ty: IrType) =
    [ "local.get $p"; $"i32.const {countOffset ty}"; "i32.sub"; $"call ${Helpers.free}" ]

/// Gives up the reference `$p` and, when it was the last, runs `fields` and frees the object.
let private releaseThen (ty: IrType) (fields: string list) =
    [ "local.get $p"
      $"i32.const {countOffset ty}"
      "i32.sub"
      $"call ${Helpers.release}"
      "if"
      yield! fields |> List.map (fun line -> "  " + line)
      yield! free ty |> List.map (fun line -> "  " + line)
      "end" ]

/// The body of the drop function of `ty`, which takes the value in `$p`.
let private dropBody (tagId: string -> int option) (ty: IrType) : string list =
    match ty with
    | IrString
    | IrFloat -> releaseThen ty []
    | IrRecord fields ->
        fields
        |> List.mapi (fun slot (_, fieldTy) -> dropField fieldTy [ "local.get $p"; $"i32.load offset={slot * 4}" ])
        |> List.concat
        |> releaseThen ty
    | IrFunction _ ->
        // The function in the closure's info word drops what it captures.
        releaseThen ty [ "local.get $p"; "local.get $p"; "i32.const 4"; "i32.sub"; "i32.load"; "call_indirect (type $ctx_fn_1)"; "drop" ]
    | IrTags tags ->
        // A code is not on the heap. A cell holds the code, then the payload: when every tag with a payload has one of
        // the same type, the payload needs no code to tell its type.
        let payloads = tags |> List.choose (fun (name, payload) -> payload |> Option.map (fun payloadTy -> name, payloadTy))
        let payloadDrop payloadTy = dropField payloadTy [ "local.get $p"; "i32.load offset=4" ]
        let dropPayload =
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
        [ "local.get $p"; "i32.const 1"; "i32.and"; "if"; "  return"; "end"; yield! releaseThen ty dropPayload ]
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
          "    i32.const 4"
          "    i32.sub"
          $"    call ${Helpers.release}"
          "    i32.eqz"
          "    br_if $done"
          yield! dropField element [ "local.get $p"; "i32.load" ] |> List.map (fun line -> "    " + line)
          "    local.get $p"
          "    i32.load offset=4"
          "    local.set $tail"
          yield! free ty |> List.map (fun line -> "    " + line)
          "    local.get $tail"
          "    local.set $p"
          "    br $next"
          "  end"
          "end" ]
    | IrInt
    | IrBool
    | IrUnit
    | IrError -> failwith $"Internal error: a drop function for {IrType.show ty}, which is not on the heap"

/// The body of the dup function of `ty`, which takes the value in `$p` and leaves it.
let private dupBody (ty: IrType) : string list =
    let notOnHeap =
        match ty with
        | IrList _ -> [ "local.get $p"; "i32.eqz"; "br_if $shared" ]
        | IrTags _ -> [ "local.get $p"; "i32.const 1"; "i32.and"; "br_if $shared" ]
        | _ -> []
    [ "block $shared"
      yield! notOnHeap |> List.map (fun line -> "  " + line)
      "  local.get $p"
      $"  i32.const {countOffset ty}"
      "  i32.sub"
      $"  call ${Helpers.retain}"
      "end"
      "local.get $p" ]

let private lines (items: string list) = items |> List.map (fun line -> "  " + line) |> String.concat "\n"

/// The functions of `drops`, as WebAssembly text: the drop and dup functions of each type, the function that drops
/// each closure environment, and the `++` of each list. The function that drops a closure's captures is called through
/// the function table, so it has the type of a closure of one parameter (`$ctx_fn_1`), and returns 0. `tagId` gives
/// the number of each tag the module numbers; a tag it does not is never made, so has no cells to drop.
let render (tagId: string -> int option) (drops: Drops) : string list =
    let typeDrops =
        drops.Types
        |> List.collect (fun ty ->
            [ lines [
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
        |> List.map (fun element ->
            lines [
                $"(func ${listConcatName element} (param $a i32) (param $b i32) (result i32)"
                "  local.get $a"
                "  i32.eqz"
                "  if (result i32)"
                "    local.get $b"
                "  else"
                "    local.get $a"
                "    i32.load"
                $"    call ${dupName element}"
                "    local.get $a"
                "    i32.load offset=4"
                "    local.get $b"
                $"    call ${listConcatName element}"
                $"    call ${Helpers.listCons}"
                "  end"
                ")" ])
    let environmentDrops =
        drops.Environments
        |> List.distinctBy environmentDropName
        |> List.map (fun captured ->
            lines [
                $"(func ${environmentDropName captured} (param $p i32) (result i32)"
                yield!
                    captured
                    |> List.mapi (fun index capturedTy -> dropField capturedTy [ "local.get $p"; $"i32.load offset={(index + 1) * 4}" ])
                    |> List.concat
                    |> List.map (fun line -> "  " + line)
                "  i32.const 0"
                ")" ])
    typeDrops @ environmentDrops @ listConcats
