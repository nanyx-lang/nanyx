/// WebAssembly text from the core IR (`CoreIr.fs`).
///
/// Every value is still an `i32`, as in `CodeGen.fs`, and the code is laid out as `CodeGen.fs` lays it out, so that a
/// function compiles to the same text whichever path it takes. A temporary is not stored: its value is computed
/// where it is used, as a nested expression would be. An operand that stores to a local on the way to its value (a
/// `def` or a `match` among a call's arguments, say) is a temporary whose value is the whole sequence, stores and all
/// (see `Atoms` in `CoreIrLower.fs`), so no store ever happens while a temporary waits to be used.
module Transpiler.Wasm.CoreIrEmit

open System.Collections.Generic
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr

/// What the emitter needs from the module being compiled.
type EmitContext =
    { /// A tag's number, from which its code is made (see `Wat.tagCode`).
      TagId: string -> int
      /// Where a string literal's bytes are in the data segment.
      StringOffset: string -> int
      /// The number of the record layout with these fields, in the order they are stored, numbering it if it is new.
      RecordLayoutId: string list -> int
      /// The function table slot of the function of this name and arity, giving it one if it has none.
      FunctionSlot: string -> int -> int
      /// Notes an indirect call of this arity, so the module declares a type for it.
      IndirectCall: int -> unit }

/// A function as WebAssembly text, and the runtime helpers (see `WasmRuntime.Helpers`) it calls.
type Emitted =
    { Text: string
      Helpers: Set<string> }

let private join (separator: string) (parts: string seq) =
    parts |> Seq.filter (fun part -> part <> "") |> String.concat separator

/// The temporaries an expression uses directly, which it computes as part of its own code.
let rec private tempsUsedBy (expr: Expr) : Var list =
    let ofAtoms atoms = atoms |> List.choose (function AVar v when v.Kind = Temp -> Some v | _ -> None)
    match expr.Kind with
    | Atom atom -> ofAtoms [ atom ]
    | Prim(_, atoms)
    | Call(_, atoms)
    | MakeList atoms
    | CallRuntime(_, atoms, _)
    | MakeClosure(_, _, atoms)
    | CallClosure(_, atoms) -> ofAtoms atoms
    | MakeRecord fields -> ofAtoms (List.map snd fields)
    | UpdateRecord(record, _, updates) -> ofAtoms (record :: List.map snd updates)
    | MakeTag(_, atom)
    | Field(atom, _)
    | Dbg(atom, _)
    | If(atom, _, _) -> ofAtoms [ atom ]
    | ForEach loop -> ofAtoms (ForLoop.sourceAtoms loop)
    | Dup(_, body)
    | Drop(_, body)
    | DropAfter(body, _)
    | Release(_, body)
    | Reuse(_, body) -> tempsUsedBy body
    | Let _
    | Do _
    | Match _
    | Interpolate _
    | BoxFloat _
    | Panic _ -> []

/// The code that gives up the value on top of the stack, of type `ty`: its drop function if it is on the heap.
let private dropValue (ty: IrType) =
    if CoreIrDrop.isHeapType ty then $"call ${CoreIrDrop.dropName ty}" else "drop"

/// The code that drops each of `vars`, kept in the locals `localOf` gives.
let private dropVars (localOf: Var -> string) (vars: Var list) =
    vars
    |> List.filter (fun var -> CoreIrDrop.isHeapType var.Type)
    |> List.map (fun var -> $"local.get ${localOf var}\n    {dropValue var.Type}")
    |> String.concat "\n    "

type private FunctionEmitter(context: EmitContext) =
    let helpers = HashSet<string>()
    // The locals the counting needs beyond the IR's own: one for each temporary dropped after the instruction that
    // borrowed it, and one for each value a construct has to read twice.
    let extraLocals = ResizeArray<string>()
    let droppedTemps = Dictionary<int, string>()

    let helper (name: string) =
        helpers.Add name |> ignore
        name

    let freshLocal (prefix: string) =
        let name = $"{prefix}_{extraLocals.Count}"
        extraLocals.Add name
        name

    let localOf (var: Var) =
        match droppedTemps.TryGetValue var.Id with
        | true, local -> local
        | _ -> var.Name

    member _.Helpers = Set.ofSeq helpers

    member _.ExtraLocals = List.ofSeq extraLocals

    /// The code that takes one more reference to each of `vars`.
    member _.DupVars (vars: Var list) =
        vars
        |> List.filter (fun var -> CoreIrDrop.isHeapType var.Type)
        |> List.map (fun var -> $"local.get ${var.Name}\n    call ${CoreIrDrop.dupName var.Type}\n    drop")
        |> String.concat "\n    "

    /// The code that gives up an object an arm took apart (see `CoreIrReuse`). When the arm holds the only reference,
    /// the names it binds keep the fields' references, the fields it does not bind are dropped, and the block is freed
    /// or kept in the token; otherwise the names take references of their own and the object's count goes down.
    member this.Release (released: Released) =
        let object = released.Object.Name
        let block = $"local.get ${object}\n      i32.const {released.CountOffset}\n      i32.sub"
        let unique =
            [ for offset, bound, fieldTy in released.Fields do
                  if bound.IsNone && CoreIrDrop.isHeapType fieldTy then
                      $"local.get ${object}\n      i32.load offset={offset}\n      call ${CoreIrDrop.dropName fieldTy}"
              match released.Token with
              | Some token -> $"{block}\n      local.set ${token.Name}"
              | None -> $"{block}\n      call ${helper Helpers.free}" ]
        let shared =
            [ this.DupVars (released.Fields |> List.choose (fun (_, bound, _) -> bound))
              $"{block}\n      call ${helper Helpers.release}\n      drop"
              match released.Token with
              | Some token -> $"i32.const 0\n      local.set ${token.Name}"
              | None -> "" ]
        let lines (code: string list) = code |> List.filter ((<>) "") |> String.concat "\n      "
        let count = $"local.get ${object}\n    i32.const {released.CountOffset}\n    i32.sub\n    i32.load"
        $"{count}\n    i32.const 1\n    i32.eq\n    if\n      {lines unique}\n    else\n      {lines shared}\n    end"

    /// How a hole of type `holeType` is measured and written (see `Interpolation`).
    member _.HoleKind (holeType: HoleType) : Interpolation.HoleKind =
        match holeType with
        | HoleString -> Interpolation.StringHole
        | HoleInt -> Interpolation.IntHole
        | HoleBool -> Interpolation.BoolHole
        | HoleFloat -> Interpolation.FloatHole
        | HoleTag cases ->
            helper Helpers.tagCode |> ignore
            cases
            |> List.map (fun (name, payload) ->
                let payloadKind =
                    payload
                    |> Option.map (function
                        | IrString -> "string"
                        | IrInt -> "int"
                        | _ -> "bool")
                name, tagCode (context.TagId name), payloadKind)
            |> Interpolation.TagHole

    /// The string built from `parts`, whose holes are already in their locals.
    member this.Interpolation (parts: InterpolationPart list) =
        let lengthCode =
            parts
            |> List.map (function
                | Text text -> Interpolation.textLength text
                | Hole(var, holeType) -> Interpolation.holeLength (this.HoleKind holeType) $"local.get ${var.Name}")
            |> String.concat "\n    "
        let writeCode =
            parts
            |> List.map (function
                | Text text -> Interpolation.textWrite text
                | Hole(var, holeType) -> Interpolation.holeWrite (this.HoleKind holeType) $"local.get ${var.Name}")
            |> String.concat "\n    "
        helper Helpers.stringAlloc |> ignore
        Interpolation.measureAndWrite lengthCode writeCode

    /// The code of a sequence: lets and `do`s ending in a value. `tail` says whether that value is what the
    /// function returns, so that a call there is a tail call.
    member this.Sequence (tail: bool) (expr: Expr) : string =
        // The temporaries defined but not yet used, by variable, and those not used by another temporary's value, in
        // the order they were defined: the ones a store has to compute first.
        let deferred = Dictionary<int, Expr>()
        let waiting = ResizeArray<Var>()
        let onStack = HashSet<int>()
        let parts = ResizeArray<string>()

        let claim (value: Expr) =
            for temp in tempsUsedBy value do
                waiting.RemoveAll(fun v -> v.Id = temp.Id) |> ignore

        // A store with a temporary still waiting to be used would have to leave it on the stack beneath the store,
        // under whatever its user pushes first. The lowering never makes one (see `Atoms` in `CoreIrLower.fs`).
        let flush () =
            if waiting.Count > 0 then internalError "a store in the core IR while a temporary waits to be used"

        let rec walk (expr: Expr) =
            match expr.Kind with
            | Let(var, value, body) when var.Kind = Temp ->
                claim value
                deferred.[var.Id] <- value
                waiting.Add var
                walk body
            | Let(var, value, body) ->
                claim value
                flush ()
                parts.Add(join "\n    " [ this.Value false deferred onStack value; $"local.set ${var.Name}" ])
                walk body
            | Do(value, rest) ->
                claim value
                flush ()
                parts.Add(join "\n    " [ this.Value false deferred onStack value; dropValue value.Type ])
                walk rest
            // Counting leaves the stack as it is, so it can happen while temporaries wait to be used.
            | Dup(vars, body) ->
                parts.Add(this.DupVars vars)
                walk body
            | Drop(vars, body) ->
                parts.Add(dropVars localOf vars)
                walk body
            | Release(released, body) ->
                parts.Add(this.Release released)
                walk body
            | _ ->
                claim expr
                if waiting.Count > 0 then internalError "a temporary in the core IR is never used"
                parts.Add(this.Value tail deferred onStack expr)
        walk expr
        if deferred.Count > 0 || onStack.Count > 0 then internalError "a temporary in the core IR is used out of order"
        join "\n    " parts

    /// The code of a value that is not a `Let` or `Do`.
    member this.Value (tail: bool) (deferred: Dictionary<int, Expr>) (onStack: HashSet<int>) (expr: Expr) : string =
        // The code of each atom in order. A temporary already on the stack has none, so it has to come before anything
        // that pushes a value (`pushedBefore`, or an earlier atom), or the values would be in the wrong order.
        let atomsAfter (pushedBefore: bool) (items: Atom list) =
            let mutable pushed = pushedBefore
            items
            |> List.map (fun atom ->
                match atom with
                | AVar v when v.Kind = Temp && onStack.Contains v.Id ->
                    if pushed then internalError "a temporary in the core IR is used out of order"
                    onStack.Remove v.Id |> ignore
                    ""
                | _ ->
                    pushed <- true
                    this.Atom deferred onStack atom)
            |> join "\n    "
        let atoms = atomsAfter false
        // Each value, then its store into its slot of the record whose pointer is on the stack.
        let stores (slotsAndValues: (int * Atom) list) =
            slotsAndValues
            |> List.map (fun (slot, value) ->
                join "\n    " [ atomsAfter true [ value ]; $"i32.const {slot * 4}\n    call ${helper Helpers.recordSet}" ])
        match expr.Kind with
        | Atom atom -> atoms [ atom ]
        | Prim(Binary op, operands) ->
            match wasmBinaryInstruction op with
            | Some instruction -> join "\n    " [ atoms operands; instruction ]
            | None -> internalError $"no instruction for the operator {op}"
        | Prim(Not, operands) -> join "\n    " [ atoms operands; "i32.eqz" ]
        | Prim(FloatBinary op, [ left; right ]) ->
            // Each operand is unboxed; an arithmetic result is boxed, and a comparison's is the i32 it leaves.
            let instruction =
                if op = "%" then Some $"call ${helper Helpers.floatRem}" else wasmFloatInstruction op
            match instruction with
            | Some instruction ->
                let compute = join "\n    " [ atoms [ left ]; "f64.load"; atomsAfter true [ right ]; "f64.load"; instruction ]
                if [ "eq"; "ne"; "lt"; "le"; "gt"; "ge" ] |> List.exists instruction.EndsWith then compute
                else $"{compute}\n    call ${helper Helpers.floatBox}"
            | None -> internalError $"no instruction for the operator {op} on floats"
        | Prim(FloatBinary op, _) -> internalError $"the operator {op} on floats with other than two operands"
        | BoxFloat value -> $"f64.const {wasmFloatConst value}\n    call ${helper Helpers.floatBox}"
        | Panic messages ->
            // Writes each message to stderr and exits with 1. `unreachable` after the exit, which never returns, lets
            // the code stand wherever a value is expected.
            let writes = messages |> List.map (fun message -> $"i32.const {context.StringOffset message}\n    call ${helper Helpers.writeStderr}")
            writes @ [ "i32.const 1"; $"call ${helper Helpers.procExit}"; "unreachable" ] |> String.concat "\n    "
        | ForEach loop -> this.ForLoop loop (fun atom -> atoms [ atom ])
        | Call(name, args) ->
            let call = if tail then "return_call" else "call"
            join "\n    " [ atoms args; $"{call} ${name}" ]
        | MakeTag(name, payload) ->
            join "\n    " [ $"i32.const {tagCode (context.TagId name)}"; atomsAfter true [ payload ]; $"call ${helper Helpers.tagBox}" ]
        | MakeRecord fields ->
            // A slot per field, with the layout's number as the info word. The record is allocated before its fields
            // are evaluated, and its pointer stays on the stack meanwhile.
            let layoutId = context.RecordLayoutId (List.map fst fields)
            join "\n    " [
                $"i32.const {fields.Length * 4}\n    i32.const {layoutId}\n    call ${helper Helpers.newObject}"
                yield! stores (fields |> List.mapi (fun slot (_, value) -> slot, value)) ]
        | Field(record, slot) ->
            // The field's value, with a reference of its own.
            let dup = if CoreIrDrop.isHeapType expr.Type then $"call ${CoreIrDrop.dupName expr.Type}" else ""
            join "\n    " [ atoms [ record ]; $"i32.const {slot * 4}\n    i32.add\n    i32.load"; dup ]
        | UpdateRecord(record, fieldCount, updates) ->
            // A copy of the record, layout and all, with the updated fields overwritten in place. The copy takes a
            // reference to each field on the heap it keeps.
            let kept =
                match atomType record with
                | IrRecord fields ->
                    fields
                    |> List.indexed
                    |> List.filter (fun (slot, (_, fieldTy)) -> CoreIrDrop.isHeapType fieldTy && not (updates |> List.exists (fst >> (=) slot)))
                | _ -> []
            let keepCode =
                match kept with
                | [] -> ""
                | _ ->
                    let copy = freshLocal "__update"
                    let dups =
                        kept
                        |> List.map (fun (slot, (_, fieldTy)) ->
                            $"local.get ${copy}\n    i32.load offset={slot * 4}\n    call ${CoreIrDrop.dupName fieldTy}\n    drop")
                    String.concat "\n    " (($"local.tee ${copy}") :: dups)
            join "\n    " [
                atoms [ record ]
                $"i32.const {fieldCount * 4}\n    call ${helper Helpers.recordCopy}"
                keepCode
                yield! stores updates ]
        | MakeList items ->
            // The elements left to right, then the empty list, then a cons for each from the back.
            join "\n    " [
                atoms items
                "i32.const 0"
                yield! items |> List.map (fun _ -> $"call ${helper Helpers.listCons}") ]
        | CallRuntime(name, ([ first; _ ] as args), _) when
            name = Helpers.listConcat && (match atomType first with IrList element -> CoreIrDrop.isHeapType element | _ -> false) ->
            // Copying cells of elements on the heap takes a reference to each element (see `CoreIrDrop`).
            let element = match atomType first with IrList element -> element | _ -> IrUnit
            helper Helpers.listCons |> ignore
            join "\n    " [ atoms args; $"call ${CoreIrDrop.listConcatName element}" ]
        | CallRuntime(name, ([ first; _ ] as args), _) when name = Helpers.valueEquals ->
            join "\n    " [ atoms args; $"call ${CoreIrEquality.equalsName (atomType first)}" ]
        | CallRuntime(name, ([ first; _ ] as args), _) when name = Helpers.valueCompare ->
            join "\n    " [ atoms args; $"call ${CoreIrOrdering.compareName (atomType first)}" ]
        | CallRuntime(name, ([ first ] as args), _) when name = Helpers.valueHash ->
            join "\n    " [ atoms args; $"call ${CoreIrHashing.hashName (atomType first)}" ]
        | CallRuntime(name, args, returnsValue) ->
            // Every value is an i32, so a helper that returns nothing leaves unit, 0.
            join "\n    " [ atoms args; $"call ${helper name}"; (if returnsValue then "" else "i32.const 0") ]
        | MakeClosure(name, arity, captured) ->
            // A block of 4-byte slots: the function's table slot, then what it captures, with the table slot of the
            // function that drops what it captures as the info word (see `CoreIrDrop`). The block is allocated before
            // anything is stored, and its pointer stays on the stack meanwhile.
            let values =
                $"i32.const {context.FunctionSlot name arity}"
                :: (captured |> List.map (fun value -> atomsAfter true [ value ]))
            let stores =
                values |> List.mapi (fun index code -> $"{code}\n    i32.const {index * 4}\n    call ${helper Helpers.recordSet}")
            let dropSlot = context.FunctionSlot (CoreIrDrop.environmentDropName (captured |> List.map atomType)) 1
            join "\n    " [ $"i32.const {values.Length * 4}\n    i32.const {dropSlot}\n    call ${helper Helpers.newObject}"; yield! stores ]
        | CallClosure(closure, args) ->
            // The closure is passed first, as the environment, and read again for the table slot it holds.
            let read = $"local.get ${closure.Name}"
            let arity = args.Length + 1
            context.IndirectCall arity
            let call = if tail then "return_call" else "call"
            join "\n    " [ read; atomsAfter true args; read; "i32.load"; $"{call}_indirect (type $ctx_fn_{arity})" ]
        | Interpolate parts -> this.Interpolation parts
        | Dbg(value, form) ->
            match form with
            | DbgNumber(scratch, _) -> join "\n    " [ atoms [ value ]; $"local.tee ${scratch.Name}\n    call $dbg\n    local.get ${scratch.Name}" ]
            | DbgString(scratch, _) -> join "\n    " [ atoms [ value ]; $"local.tee ${scratch.Name}\n    call $dbg_str\n    local.get ${scratch.Name}" ]
            | DbgFloat(scratch, _) ->
                // The float is printed through a string of its own, dropped once printed.
                let text = freshLocal "__dbg_text"
                join "\n    " [
                    atoms [ value ]
                    $"local.tee ${scratch.Name}\n    call ${helper Helpers.floatToString}\n    local.tee ${text}\n    call $dbg_str"
                    $"local.get ${text}\n    {dropValue IrString}\n    local.get ${scratch.Name}" ]
            | DbgTag(name, scratch) ->
                // The host has a function per tag that `dbg` prints.
                $"i32.const {tagCode (context.TagId name)}\n    local.set ${scratch.Name}\n    call $dbg_tag_{sanitizeIdentifier name}\n    local.get ${scratch.Name}"
            | DbgTagPayload(name, scratch) ->
                join "\n    " [
                    atoms [ value ]
                    $"local.tee ${scratch.Name}\n    call $dbg_tag_payload_{sanitizeIdentifier name}\n    i32.const {tagCode (context.TagId name)}\n    local.get ${scratch.Name}\n    call ${helper Helpers.tagBox}" ]
            | DbgTagValue(hole, holeType, _) ->
                // Printed through the string `"{value}"` would build; the hole still holds the value to leave.
                helper Helpers.alloc |> ignore
                let text = freshLocal "__dbg_text"
                $"{this.Interpolation [ Hole(hole, holeType) ]}\n    local.tee ${text}\n    call $dbg_str\n    local.get ${text}\n    {dropValue IrString}\n    local.get ${hole.Name}"
        | If(cond, ifTrue, ifFalse) ->
            let condCode = atoms [ cond ]
            $"{condCode}\n    (if (result i32)\n      (then\n        {this.Sequence tail ifTrue}\n      )\n      (else\n        {this.Sequence tail ifFalse}\n      )\n    )"
        | Match(scrutinees, arms) -> this.Arms tail scrutinees arms
        | Dup(vars, body) -> join "\n    " [ this.DupVars vars; this.Value tail deferred onStack body ]
        | Drop(vars, body) -> join "\n    " [ dropVars localOf vars; this.Value tail deferred onStack body ]
        | Release(released, body) -> join "\n    " [ this.Release released; this.Value tail deferred onStack body ]
        // An allocation into the block a reuse token holds, if it holds one (see `CoreIrReuse`).
        | Reuse(token, { Kind = CallRuntime(_, args, _) }) ->
            join "\n    " [ $"local.get ${token.Name}"; atomsAfter true args; $"call ${helper Helpers.listConsAt}" ]
        | Reuse(token, { Kind = MakeTag(name, payload) }) ->
            join "\n    " [
                $"local.get ${token.Name}\n    i32.const {tagCode (context.TagId name)}"
                atomsAfter true [ payload ]
                $"call ${helper Helpers.tagBoxAt}" ]
        | Reuse(token, { Kind = MakeRecord fields }) ->
            let layoutId = context.RecordLayoutId (List.map fst fields)
            join "\n    " [
                $"local.get ${token.Name}\n    i32.const {fields.Length * 4}\n    i32.const {layoutId}\n    call ${helper Helpers.reuseObject}"
                yield! stores (fields |> List.mapi (fun slot (_, value) -> slot, value)) ]
        | Reuse _ -> internalError "a reuse of a block by something other than an allocation"
        | DropAfter(body, vars) ->
            // A temporary among `vars` is kept in a local as its value is pushed, to be dropped once `body` is done.
            // Nothing can follow a tail call, so `body` is not one.
            for var in vars do
                if var.Kind = Temp then droppedTemps.[var.Id] <- freshLocal "__owned"
            join "\n    " [ this.Value false deferred onStack body; dropVars localOf vars ]
        | Let _
        | Do _ -> this.Sequence tail expr

    member this.Atom (deferred: Dictionary<int, Expr>) (onStack: HashSet<int>) (atom: Atom) : string =
        match atom with
        | AVar v when v.Kind = Temp ->
            match deferred.TryGetValue v.Id with
            | true, value ->
                deferred.Remove v.Id |> ignore
                match droppedTemps.TryGetValue v.Id with
                | true, local -> $"{this.Value false deferred onStack value}\n    local.tee ${local}"
                | _ -> this.Value false deferred onStack value
            | _ -> internalError "a temporary in the core IR is used twice"
        | AVar v -> $"local.get ${v.Name}"
        | AInt value -> $"i32.const {value}"
        | ABool value -> if value then "i32.const 1" else "i32.const 0"
        | AUnit -> "i32.const 0"
        | AString value -> $"i32.const {context.StringOffset value}"
        | ATag name -> $"i32.const {tagCode (context.TagId name)}"

    /// A `for` loop: each time round, every stream gives its next element (or ends the loop), the body's value is
    /// dropped, and every stream moves on. The loop is unit.
    member this.ForLoop (loop: ForLoop) (atomCode: Atom -> string) : string =
        let depth = loop.Depth
        let done' = $"br_if $__for_done_{depth}"
        // What each stream does before the loop, at the top of each time round, and at the bottom.
        let start (stream: ForStream) =
            match stream.Source with
            | OverRange(first, last) -> [ atomCode first; $"local.set ${stream.Element.Name}"; atomCode last; $"local.set ${stream.End.Name}" ]
            | OverRangeFrom first -> [ atomCode first; $"local.set ${stream.Element.Name}" ]
            | OverList list -> [ atomCode list; $"local.set ${stream.Cursor.Name}" ]
        let next (stream: ForStream) =
            match stream.Source with
            | OverRange _ -> [ $"local.get ${stream.Element.Name}"; $"local.get ${stream.End.Name}"; "i32.gt_s"; done' ]
            | OverRangeFrom _ -> []
            | OverList _ ->
                let cursor = stream.Cursor.Name
                [ $"local.get ${cursor}"; "i32.eqz"; done'; $"local.get ${cursor}"; "i32.load"; $"local.set ${stream.Element.Name}" ]
        let advance (stream: ForStream) =
            match stream.Source with
            | OverRange _
            | OverRangeFrom _ ->
                let element = stream.Element.Name
                [ $"local.get ${element}"; "i32.const 1"; "i32.add"; $"local.set ${element}" ]
            | OverList _ ->
                let cursor = stream.Cursor.Name
                [ $"local.get ${cursor}"; "i32.const 4"; "i32.add"; "i32.load"; $"local.set ${cursor}" ]
        let bodyCode = this.Sequence false loop.Body
        let bodyDrop = dropValue loop.Body.Type
        // The starting atoms are read in the order `tempsUsedBy` gives them.
        let startCode = loop.Streams |> List.collect start |> String.concat "\n    "
        let inLoop =
            List.collect next loop.Streams @ [ bodyCode; bodyDrop ] @ List.collect advance loop.Streams @ [ $"br $__for_next_{depth}" ]
            |> String.concat "\n        "
        $"{startCode}\n    block $__for_done_{depth}\n      loop $__for_next_{depth}\n        {inLoop}\n      end\n    end\n    i32.const 0"

    /// Tests each arm's patterns in turn, as `CodeGen.fs` does: a chain of `if`s ending in `unreachable`.
    member this.Arms (tail: bool) (scrutinees: Var list) (arms: Arm list) : string =
        let tagPayloadCode valueCode = $"{valueCode}\n      i32.load offset=4"
        let fieldCode valueCode slot = $"{valueCode}\n      i32.const {slot * 4}\n      i32.add\n      i32.load"
        // The list after `count` cells, and the head of the list after `index` cells.
        let listTailCode valueCode count = valueCode + String.replicate count "\n      i32.const 4\n      i32.add\n      i32.load"
        let listElementCode valueCode index = $"{listTailCode valueCode index}\n      i32.load"
        let guarded first second =
            $"{first}\n      (if (result i32)\n        (then\n          {second})\n        (else i32.const 0))"
        let allOf conditions =
            match conditions |> List.filter (fun condition -> condition <> "i32.const 1") with
            | [] -> "i32.const 1"
            | [ single ] -> single
            | many -> (many |> String.concat "\n      ") + String.replicate (many.Length - 1) "\n      i32.and"
        let rec condition (pattern: Pattern) (valueCode: string) =
            match pattern with
            | PWildcard
            | PBind _ -> "i32.const 1"
            | PInt value -> $"{valueCode}\n      i32.const {value}\n      i32.eq"
            | PBool value ->
                let flag = if value then 1 else 0
                $"{valueCode}\n      i32.const {flag}\n      i32.eq"
            | PTag(name, payload) ->
                let discriminant =
                    $"{valueCode}\n      call ${helper Helpers.tagCode}\n      i32.const {tagCode (context.TagId name)}\n      i32.eq"
                match payload with
                | None -> discriminant
                | Some inner ->
                    // Only look at the payload once the code matches: only then is the value a cell.
                    match condition inner (tagPayloadCode valueCode) with
                    | "i32.const 1" -> discriminant
                    | payloadCheck -> guarded discriminant payloadCheck
            | PFields fields -> fields |> List.map (fun (slot, inner) -> condition inner (fieldCode valueCode slot)) |> allOf
            | PString value ->
                $"{valueCode}\n      i32.const {context.StringOffset value}\n      call ${helper Helpers.stringEquals}"
            | PGuard(var, guard) -> $"{valueCode}\n      local.set ${var.Name}\n      {this.Sequence false guard}"
            // Left to right, each part tested only once those before it match: `#some(_) & { ... }`'s predicate runs
            // only on a `#some`.
            | PAll parts ->
                match parts |> List.map (fun part -> condition part valueCode) |> List.filter ((<>) "i32.const 1") with
                | [] -> "i32.const 1"
                | checks -> List.reduceBack guarded checks
            | PList(elements, rest) ->
                let lengthInstruction = if rest.IsSome then "i32.ge_s" else "i32.eq"
                let lengthCheck =
                    $"{valueCode}\n      call ${helper Helpers.listLength}\n      i32.const {elements.Length}\n      {lengthInstruction}"
                let elementChecks =
                    elements |> List.mapi (fun index element -> condition element (listElementCode valueCode index)) |> allOf
                let restCheck =
                    match rest with
                    | Some restPattern -> condition restPattern (listTailCode valueCode elements.Length)
                    | None -> "i32.const 1"
                // The elements only exist once the length check holds.
                match allOf [ elementChecks; restCheck ] with
                | "i32.const 1" -> lengthCheck
                | elementsAndRest -> guarded lengthCheck elementsAndRest
        let rec binds (pattern: Pattern) (valueCode: string) =
            match pattern with
            | PBind var -> [ $"{valueCode}\n        local.set ${var.Name}" ]
            | PTag(_, Some inner) -> binds inner (tagPayloadCode valueCode)
            | PFields fields -> fields |> List.collect (fun (slot, inner) -> binds inner (fieldCode valueCode slot))
            | PAll parts -> parts |> List.collect (fun part -> binds part valueCode)
            | PList(elements, rest) ->
                (elements |> List.mapi (fun index element -> binds element (listElementCode valueCode index)) |> List.concat)
                @ (rest |> Option.map (fun restPattern -> binds restPattern (listTailCode valueCode elements.Length)) |> Option.defaultValue [])
            | _ -> []
        let rec emit (remaining: Arm list) =
            match remaining with
            | [] -> "unreachable"
            | arm :: rest ->
                let conditions =
                    List.map2 (fun pattern (scrutinee: Var) -> condition pattern $"local.get ${scrutinee.Name}") arm.Patterns scrutinees
                let allConditions =
                    match conditions with
                    | [] -> "i32.const 1"
                    | [ single ] -> single
                    | many -> (many |> String.concat "\n      ") + String.replicate (many.Length - 1) "\n      i32.and"
                let bindCode =
                    List.map2 (fun pattern (scrutinee: Var) -> binds pattern $"local.get ${scrutinee.Name}") arm.Patterns scrutinees
                    |> List.concat
                    |> String.concat "\n    "
                let armCode = join "\n    " [ bindCode; this.Sequence tail arm.Body ]
                $"{allConditions}\n    (if (result i32)\n      (then\n        {armCode}\n      )\n      (else\n        {emit rest}\n      )\n    )"
        emit arms

/// The locals a function declares beyond its parameters, in the order `CodeGen.fs` declares them: its own locals, the
/// scrutinees', what patterns bind, what interpolated strings need (their scratch locals, then their holes by depth),
/// and its scratch locals.
let private declaredLocals (body: Expr) : string list =
    let found = ResizeArray<Var>()
    // The holes of the interpolated strings, and of the strings `dbg` would print a tag through, by depth and place.
    let holes = ResizeArray<int * int>()
    let interpolates = ref false
    // The locals of the `for` loops, in the order the old path declares them: each loop's, then those of the loops in
    // its body.
    let loops = ResizeArray<Var>()
    let holeAt depth =
        interpolates.Value <- true
        holes.Add((depth, 0))
    let holePlace (name: string) =
        match name.Substring("__interp_hole_".Length).Split('_') with
        | [| depth; index |] -> int depth, int index
        | _ -> internalError $"{name} is not a hole local"
    let rec pattern (p: Pattern) =
        match p with
        | PBind var -> found.Add var
        | PGuard(var, guard) ->
            found.Add var
            walk guard
        | PTag(_, Some inner) -> pattern inner
        | PFields fields -> fields |> List.iter (snd >> pattern)
        | PAll parts -> parts |> List.iter pattern
        | PList(elements, rest) ->
            elements |> List.iter pattern
            rest |> Option.iter pattern
        | _ -> ()
    and walk (expr: Expr) =
        match expr.Kind with
        | Let(var, value, body) ->
            match var.Kind with
            | Temp -> ()
            | InterpolationHole -> holes.Add(holePlace var.Name)
            | _ -> found.Add var
            walk value
            walk body
        | Do(value, rest) ->
            walk value
            walk rest
        | If(_, ifTrue, ifFalse) ->
            walk ifTrue
            walk ifFalse
        | Match(_, arms) ->
            for arm in arms do
                arm.Patterns |> List.iter pattern
                walk arm.Body
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Reuse(_, body) -> walk body
        | Release(released, body) ->
            released.Token |> Option.iter found.Add
            walk body
        | Atom _
        | Prim _
        | Call _
        | MakeTag _
        | MakeRecord _
        | Field _
        | UpdateRecord _
        | MakeList _
        | CallRuntime _
        | MakeClosure _
        | CallClosure _
        | BoxFloat _
        | Panic _ -> ()
        | Interpolate _ -> interpolates.Value <- true
        | ForEach loop ->
            ForLoop.locals loop |> List.iter loops.Add
            walk loop.Body
        | Dbg(_, form) ->
            match form with
            | DbgNumber(scratch, holeDepth)
            | DbgString(scratch, holeDepth)
            | DbgFloat(scratch, holeDepth) ->
                found.Add scratch
                holeDepth |> Option.iter holeAt
            | DbgTag(_, scratch)
            | DbgTagPayload(_, scratch) -> found.Add scratch
            | DbgTagValue(_, _, scratch) ->
                interpolates.Value <- true
                found.Add scratch
    walk body
    let ofKind kind = found |> Seq.filter (fun var -> var.Kind = kind) |> Seq.map _.Name |> List.ofSeq
    let interpolationLocals =
        if interpolates.Value then
            Interpolation.scratchLocals
            @ (holes |> Seq.distinct |> Seq.sort |> Seq.map (fun (depth, index) -> Interpolation.holeLocalName depth index) |> List.ofSeq)
        else []
    [ yield! ofKind Local
      yield! ofKind MatchTemp
      yield! ofKind PatternBound
      yield! interpolationLocals
      yield! loops |> Seq.map _.Name
      yield! ofKind Scratch ]
    |> List.distinct

/// `func` as a WebAssembly function, exported under `exportName` if given.
let emitFunction (context: EmitContext) (func: Function) (exportName: string option) : Emitted =
    let emitter = FunctionEmitter(context)
    let bodyCode = emitter.Sequence true func.Body
    let header =
        [ func.Params |> List.map (fun p -> $"(param ${p.Name} i32)") |> String.concat " "
          "(result i32)"
          declaredLocals func.Body @ emitter.ExtraLocals |> List.map (fun name -> $"(local ${name} i32)") |> String.concat " " ]
        |> join " "
    let text =
        match exportName with
        | Some exported -> $"  (func ${func.Name} {header}\n    {bodyCode}\n  )\n  (export \"{exported}\" (func ${func.Name}))"
        | None -> $"  (func ${func.Name} {header}\n    {bodyCode}\n  )"
    { Text = text; Helpers = emitter.Helpers }

/// The start function, `func` (see `CoreIrLower.StartFunction`): each top-level expression in turn, its value
/// dropped. It takes nothing and returns nothing.
let emitStart (context: EmitContext) (func: Function) : Emitted =
    let emitter = FunctionEmitter(context)
    let rec expressions (expr: Expr) =
        match expr.Kind with
        | Do(value, rest) -> value :: expressions rest
        | Atom AUnit -> []
        | _ -> internalError "the start function is not a sequence of top-level expressions"
    let bodyCode =
        expressions func.Body
        |> List.map (fun value -> $"{emitter.Sequence false value}\n    {dropValue value.Type}")
        |> String.concat "\n    "
    let header =
        match declaredLocals func.Body @ emitter.ExtraLocals |> List.map (fun name -> $"(local ${name} i32)") |> String.concat " " with
        | "" -> ""
        | locals -> " " + locals
    { Text = $"  (func ${func.Name}{header}\n    {bodyCode}\n  )"; Helpers = emitter.Helpers }
