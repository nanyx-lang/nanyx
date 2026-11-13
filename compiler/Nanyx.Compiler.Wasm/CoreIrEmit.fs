/// WebAssembly text from the core IR (`CoreIr.fs`).
///
/// Each value is held in the WebAssembly type of its type (see `CoreIrLayout`): an int in an `i64`, everything else in
/// an `i32`. A local of an int is named with `_i64` after the variable's name (see `localName`), since variables of the
/// same kind and name share a local, and may be of different types. A temporary is not stored: its value is computed
/// where it is used, as a nested expression would be. An operand that stores to a local on the way to its value (a
/// `def` or a `match` among a call's arguments, say) is a temporary whose value is the whole sequence, stores and all
/// (see `Atoms` in `CoreIrLower.fs`), so no store ever happens while a temporary waits to be used.
module Transpiler.Wasm.CoreIrEmit

open System.Collections.Generic
open NyxCompiler
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
      /// Where the static closure over the function of this name and arity is in the data segment, giving the function
      /// a table slot if it has none.
      StaticClosure: string -> int -> int
      /// The number of the record layout with these fields, in the order they are stored, numbering it if it is new.
      RecordLayoutId: string list -> int
      /// The function table slot of the function of this name and arity, giving it one if it has none.
      FunctionSlot: string -> int -> int
      /// Notes an indirect call of a function of these parameters and this result (WebAssembly value types), so the
      /// module declares a type for it (see `Wat.indirectTypeName`).
      IndirectCall: string list * string -> unit
      /// The types of the parameters and the result of the top-level function of this name, if the module has it, so
      /// that a value of no known type (see `IrError`) is passed as the parameter takes it and the result taken as the
      /// call's, or the other way round.
      Signature: string -> (IrType list * IrType) option }

/// A function as WebAssembly text, and the runtime helpers (see `WasmRuntime.Helpers`) it calls.
type Emitted =
    { Text: string
      Helpers: Set<string> }

let private join (separator: string) (parts: string seq) =
    parts |> Seq.filter (fun part -> part <> "") |> String.concat separator

/// The name of the local `var` is kept in: its name, with `_i64` after it for a value held in an `i64`.
let private localName (var: Var) =
    match CoreIrLayout.valueType var.Type with
    | "i32" -> var.Name
    | valueType -> $"{var.Name}_{valueType}"

/// The helpers whose parameters and results of type int are `i64`s. The others take and give `i32`s, which the
/// emitter converts an int to and from.
let private takesInts = set [ Helpers.intToFloat; Helpers.floatToInt ]

/// The intrinsics that add, subtract and multiply integers wrapping around, each with its instruction's operation.
let private wrappingOperations =
    Map.ofList [ Intrinsics.wrappingAdd, "add"; Intrinsics.wrappingSub, "sub"; Intrinsics.wrappingMul, "mul" ]

/// The integer type of a value of `ty`, as `SizedIntegerCode` takes it: a sized integer, or `None` for an int.
let private integerType (ty: IrType) =
    match ty with
    | IrSized integer -> Some integer
    | IrInt -> None
    | other -> internalError $"a conversion of {IrType.show other}, which is not an integer"

/// The operators that give a number, where the others on numbers give a bool.
let private arithmetic = set [ "+"; "-"; "*"; "/"; "%" ]

/// The code that converts a value of `from` on the stack to one of `into`, when they are held differently: only a
/// value of no known type (see `IrError`) is held differently from a value it is taken as. It is an `i32`, and an int
/// that becomes one keeps its low 32 bits.
let private conversion (from: IrType) (into: IrType) =
    match CoreIrLayout.valueType from, CoreIrLayout.valueType into with
    | fromType, intoType when fromType = intoType -> ""
    | "i64", _ -> "i32.wrap_i64"
    | _ -> "i64.extend_i32_s"

/// The helpers that take an int as an index into a string, which they are given clamped (see `Helpers.intToIndex`).
/// Any other helper is given an int's low 32 bits.
let private takesIndices = set [ Helpers.stringByteAt; Helpers.stringSlice ]

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
    | UpdateRecord(record, updates) -> ofAtoms (record :: List.map snd updates)
    | MakeTag(_, atom)
    | Field(atom, _)
    | Assign(_, atom)
    | SetField(_, _, atom)
    | Dbg atom
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
    | While _
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
    // The locals the counting needs beyond the IR's own, with their value types: one for each temporary dropped after
    // the instruction that borrowed it, and one for each value a construct has to read twice.
    let extraLocals = ResizeArray<string * string>()
    let droppedTemps = Dictionary<int, string>()

    let helper (name: string) =
        helpers.Add name |> ignore
        name

    let freshLocal (prefix: string) (valueType: string) =
        let name = $"{prefix}_{extraLocals.Count}"
        extraLocals.Add((name, valueType))
        name

    let localOf (var: Var) =
        match droppedTemps.TryGetValue var.Id with
        | true, local -> local
        | _ -> localName var

    member _.Helpers = Set.ofSeq helpers

    member _.ExtraLocals = List.ofSeq extraLocals

    /// The code that takes one more reference to each of `vars`.
    member _.DupVars (vars: Var list) =
        vars
        |> List.filter (fun var -> CoreIrDrop.isHeapType var.Type)
        |> List.map (fun var -> $"local.get ${localName var}\n    call ${CoreIrDrop.dupName var.Type}\n    drop")
        |> String.concat "\n    "

    /// The code that gives up an object an arm took apart (see `CoreIrReuse`). When the arm holds the only reference,
    /// the names it binds keep the fields' references, the fields it does not bind are dropped, and the block is freed
    /// or kept in the token; otherwise the names take references of their own and the object's count goes down.
    member this.Release (released: Released) =
        let object = localName released.Object
        let block = $"local.get ${object}\n      i32.const {released.CountOffset}\n      i32.sub"
        let unique =
            [ for offset, bound, fieldTy in released.Fields do
                  if bound.IsNone && CoreIrDrop.isHeapType fieldTy then
                      $"local.get ${object}\n      i32.load offset={offset}\n      call ${CoreIrDrop.dropName fieldTy}"
              match released.Token with
              | Some token -> $"{block}\n      local.set ${localName token}"
              | None -> $"{block}\n      call ${helper Helpers.free}" ]
        let shared =
            [ this.DupVars (released.Fields |> List.choose (fun (_, bound, _) -> bound))
              $"{block}\n      call ${helper Helpers.release}\n      drop"
              match released.Token with
              | Some token -> $"i32.const 0\n      local.set ${localName token}"
              | None -> "" ]
        let lines (code: string list) = code |> List.filter ((<>) "") |> String.concat "\n      "
        let count = $"local.get ${object}\n    i32.const {released.CountOffset}\n    i32.sub\n    i32.load"
        $"{count}\n    i32.const 1\n    i32.eq\n    if\n      {lines unique}\n    else\n      {lines shared}\n    end"

    /// How a hole of type `holeType` is measured and written (see `Interpolation`).
    member _.HoleKind (holeType: HoleType) : Interpolation.HoleKind =
        match holeType with
        | HoleString -> Interpolation.StringHole
        | HoleInt -> Interpolation.IntHole
        | HoleSized SizedInteger.U64 -> Interpolation.UnsignedHole
        | HoleSized _ -> Interpolation.IntHole
        | HoleBool -> Interpolation.BoolHole
        | HoleFloat -> Interpolation.FloatHole
        | HoleTag cases ->
            helper Helpers.tagCode |> ignore
            cases
            |> List.map (fun (name, payload) ->
                let payloadKind =
                    payload
                    |> Option.map (function
                        | IrString -> Interpolation.StringPayload
                        | IrInt -> Interpolation.IntPayload
                        | _ -> Interpolation.BoolPayload)
                name, tagCode (context.TagId name), payloadKind)
            |> Interpolation.TagHole

    /// The string built from `parts`, whose holes are already in their locals.
    member this.Interpolation (parts: InterpolationPart list) =
        // A hole's value, read as an `i64` where it is an integer held in an `i32`.
        let holeValue (var: Var) (holeType: HoleType) =
            match holeType with
            | HoleSized integer when CoreIrLayout.valueType (IrSized integer) = "i32" ->
                let signedness = if SizedInteger.isSigned integer then "s" else "u"
                $"local.get ${localName var}\n    i64.extend_i32_{signedness}"
            | _ -> $"local.get ${localName var}"
        let lengthCode =
            parts
            |> List.map (function
                | Text text -> Interpolation.textLength text
                | Hole(var, holeType) -> Interpolation.holeLength (this.HoleKind holeType) (holeValue var holeType))
            |> String.concat "\n    "
        let writeCode =
            parts
            |> List.map (function
                | Text text -> Interpolation.textWrite text
                | Hole(var, holeType) -> Interpolation.holeWrite (this.HoleKind holeType) (holeValue var holeType))
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
                parts.Add(join "\n    " [ this.Value false deferred onStack value; $"local.set ${localName var}" ])
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

    /// The code of a sequence whose value is taken as a value of `ty` (see `conversion`): a branch of an `if` or a
    /// `match`, or a function's body. Nothing can follow a tail call, so a sequence that needs converting makes none.
    member this.SequenceAs (tail: bool) (ty: IrType) (expr: Expr) : string =
        match conversion expr.Type ty with
        | "" -> this.Sequence tail expr
        | converted -> $"{this.Sequence false expr}\n    {converted}"

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
        // The helper of `Helpers.storingValues` `name` for a value of `ty`.
        let storing (name: string) (ty: IrType) = helper (Helpers.forStorage name (CoreIrLayout.storage ty))
        // Each value, then its store at its offset in the record whose pointer is on the stack.
        let stores (offsetsAndValues: (int * Atom) list) =
            offsetsAndValues
            |> List.map (fun (offset, value) ->
                join "\n    " [ atomsAfter true [ value ]; $"i32.const {offset}\n    call ${storing Helpers.recordSet (atomType value)}" ])
        // The fields of a new record, each at its offset.
        let fieldStores (values: Atom list) = stores (List.zip (CoreIrLayout.offsets (List.map atomType values)) values)
        let recordFields (record: IrType) =
            match record with
            | IrRecord fields -> fields
            | other -> internalError $"a field of {IrType.show other}, which is not a record"
        match expr.Kind with
        | Atom atom -> atoms [ atom ]
        | Prim(Binary op, operands) when operands |> List.exists (atomType >> (=) IrInt) ->
            // An operand of no known type (see `IrError`) beside an int is taken as one.
            let operandCode =
                operands
                |> List.mapi (fun index operand ->
                    join "\n    " [ atomsAfter (index > 0) [ operand ]; (if atomType operand = IrInt then "" else "i64.extend_i32_s") ])
                |> join "\n    "
            let instruction =
                match op with
                | "+" -> $"call ${helper Helpers.intAdd}"
                | "-" -> $"call ${helper Helpers.intSub}"
                | "*" -> $"call ${helper Helpers.intMul}"
                | _ ->
                    match wasmIntInstruction op with
                    | Some instruction -> instruction
                    | None -> internalError $"no instruction for the operator {op} on ints"
            // Arithmetic gives an int and a comparison a bool, taken as the type the expression has.
            let given = if arithmetic.Contains op then IrInt else IrBool
            join "\n    " [ operandCode; instruction; conversion given expr.Type ]
        | Prim(Binary op, operands) when operands |> List.exists (atomType >> function IrSized _ -> true | _ -> false) ->
            this.SizedBinary op operands atomsAfter expr.Type
        | Prim(Binary op, operands) ->
            match wasmBinaryInstruction op with
            | Some instruction -> join "\n    " [ atoms operands; instruction ]
            | None -> internalError $"no instruction for the operator {op}"
        | Prim(Not, operands) -> join "\n    " [ atoms operands; "i32.eqz" ]
        | Prim(FloatBinary op, [ left; right ]) ->
            // Each operand is unboxed; an arithmetic result is boxed, and a comparison's is the i32 it leaves. An `F32` is
            // an `f32`, whose remainder is taken as a float's, which is exact, and narrowed back.
            let isF32 = atomType left = IrF32
            let load = if isF32 then "f32.load" else "f64.load"
            let widen = if isF32 && op = "%" then "f64.promote_f32" else ""
            let instruction =
                if op = "%" then Some (join "\n    " [ $"call ${helper Helpers.floatRem}"; (if isF32 then "f32.demote_f64" else "") ])
                elif isF32 then wasmFloatInstruction op |> Option.map (fun instruction -> instruction.Replace("f64.", "f32."))
                else wasmFloatInstruction op
            match instruction with
            | Some instruction ->
                let compute = join "\n    " [ atoms [ left ]; load; widen; atomsAfter true [ right ]; load; widen; instruction ]
                if [ "eq"; "ne"; "lt"; "le"; "gt"; "ge" ] |> List.exists instruction.EndsWith then compute
                else
                    let box = if isF32 then Helpers.f32Box else Helpers.floatBox
                    $"{compute}\n    call ${helper box}"
            | None -> internalError $"no instruction for the operator {op} on floats"
        | Prim(FloatBinary op, _) -> internalError $"the operator {op} on floats with other than two operands"
        | BoxFloat value when expr.Type = IrF32 -> $"f32.const {wasmFloatConst value}\n    call ${helper Helpers.f32Box}"
        | BoxFloat value -> $"f64.const {wasmFloatConst value}\n    call ${helper Helpers.floatBox}"
        | Panic messages ->
            // Writes each message to stderr and exits with 1. `unreachable` after the exit, which never returns, lets
            // the code stand wherever a value is expected.
            let writes = messages |> List.map (fun message -> $"i32.const {context.StringOffset message}\n    call ${helper Helpers.writeStderr}")
            writes @ [ "i32.const 1"; $"call ${helper Helpers.procExit}"; "unreachable" ] |> String.concat "\n    "
        | ForEach loop -> this.ForLoop loop (fun atom -> atoms [ atom ])
        | While loop -> this.WhileLoop loop
        | Call(name, args) ->
            match context.Signature name with
            | Some(parameterTys, resultTy) when parameterTys.Length = args.Length ->
                let arguments =
                    List.zip args parameterTys
                    |> List.mapi (fun index (arg, parameterTy) -> join "\n    " [ atomsAfter (index > 0) [ arg ]; conversion (atomType arg) parameterTy ])
                // Nothing can follow a tail call, so a result that needs converting comes from a plain call.
                match conversion resultTy expr.Type with
                | "" -> join "\n    " [ yield! arguments; $"""{(if tail then "return_call" else "call")} ${name}""" ]
                | resultConversion -> join "\n    " [ yield! arguments; $"call ${name}"; resultConversion ]
            | _ -> join "\n    " [ atoms args; $"""{(if tail then "return_call" else "call")} ${name}""" ]
        | MakeTag(name, payload) ->
            join "\n    " [
                $"i32.const {tagCode (context.TagId name)}"
                atomsAfter true [ payload ]
                $"call ${storing Helpers.tagBox (atomType payload)}" ]
        | MakeRecord fields ->
            // Each field at its offset, with the layout's number as the info word. The record is allocated before its
            // fields are evaluated, and its pointer stays on the stack meanwhile.
            let layoutId = context.RecordLayoutId (List.map fst fields)
            let values = List.map snd fields
            join "\n    " [
                $"i32.const {CoreIrLayout.totalSize (List.map atomType values)}\n    i32.const {layoutId}\n    call ${helper Helpers.newObject}"
                yield! fieldStores values ]
        | Field(record, slot) ->
            // The field's value, with a reference of its own.
            let fields = recordFields (atomType record)
            let dup = if CoreIrDrop.isHeapType expr.Type then $"call ${CoreIrDrop.dupName expr.Type}" else ""
            join "\n    " [ atoms [ record ]; CoreIrLayout.load expr.Type (CoreIrLayout.fieldOffset fields slot); dup ]
        | UpdateRecord(record, updates) ->
            // A copy of the record, layout and all, with the updated fields overwritten in place. The copy takes a
            // reference to each field on the heap it keeps.
            let fields = recordFields (atomType record)
            let kept =
                fields
                |> List.indexed
                |> List.filter (fun (slot, (_, fieldTy)) -> CoreIrDrop.isHeapType fieldTy && not (updates |> List.exists (fst >> (=) slot)))
            let keepCode =
                match kept with
                | [] -> ""
                | _ ->
                    let copy = freshLocal "__update" "i32"
                    let dups =
                        kept
                        |> List.map (fun (slot, (_, fieldTy)) ->
                            $"local.get ${copy}\n    {CoreIrLayout.load fieldTy (CoreIrLayout.fieldOffset fields slot)}\n    call ${CoreIrDrop.dupName fieldTy}\n    drop")
                    String.concat "\n    " (($"local.tee ${copy}") :: dups)
            join "\n    " [
                atoms [ record ]
                $"i32.const {CoreIrLayout.recordSize fields}\n    call ${helper Helpers.recordCopy}"
                keepCode
                yield! stores (updates |> List.map (fun (slot, value) -> CoreIrLayout.fieldOffset fields slot, value)) ]
        | Assign(cell, value) ->
            // The new value is computed before the old one is dropped, since it may be made from it.
            let dropOld = if CoreIrDrop.isHeapType cell.Type then $"local.get ${localName cell}\n    {dropValue cell.Type}" else ""
            join "\n    " [ atoms [ value ]; dropOld; $"local.set ${localName cell}"; "i32.const 0" ]
        | SetField(record, slot, value) ->
            let fieldTy = atomType value
            let offset = CoreIrLayout.fieldOffset (recordFields record.Type) slot
            let held = freshLocal "__set" (CoreIrLayout.valueType fieldTy)
            let dropOld =
                if CoreIrDrop.isHeapType fieldTy then $"local.get ${localName record}\n    {CoreIrLayout.load fieldTy offset}\n    {dropValue fieldTy}" else ""
            join "\n    " [
                atoms [ value ]
                $"local.set ${held}"
                dropOld
                $"local.get ${localName record}\n    local.get ${held}\n    {CoreIrLayout.store fieldTy offset}"
                "i32.const 0" ]
        | MakeList items ->
            // The elements left to right, then the empty list, then a cons for each from the back.
            let element =
                match expr.Type with
                | IrList element -> element
                | other -> internalError $"a list literal of type {IrType.show other}"
            join "\n    " [
                atoms items
                "i32.const 0"
                yield! items |> List.map (fun _ -> $"call ${storing Helpers.listCons element}") ]
        | CallRuntime(name, ([ first; _ ] as args), _) when
            name = Helpers.listConcat && (match atomType first with IrList element -> CoreIrDrop.isHeapType element | _ -> false) ->
            // Copying cells of elements on the heap takes a reference to each element (see `CoreIrDrop`).
            let element = match atomType first with IrList element -> element | _ -> IrUnit
            helper Helpers.listCons |> ignore
            join "\n    " [ atoms args; $"call ${CoreIrDrop.listConcatName element}" ]
        | CallRuntime(name, ([ first; _ ] as args), _) when name = Helpers.listConcat ->
            let element = match atomType first with IrList element -> element | _ -> IrUnit
            join "\n    " [ atoms args; $"call ${storing Helpers.listConcat element}" ]
        | CallRuntime(name, ([ head; _ ] as args), _) when name = Helpers.listCons ->
            join "\n    " [ atoms args; $"call ${storing Helpers.listCons (atomType head)}" ]
        | CallRuntime(name, ([ first; _ ] as args), _) when name = Helpers.valueEquals ->
            join "\n    " [ atoms args; $"call ${CoreIrEquality.equalsName (atomType first)}" ]
        // An order and a hash are ints, which the functions that compute them give as `i32`s.
        | CallRuntime(name, ([ first; _ ] as args), _) when name = Helpers.valueCompare ->
            join "\n    " [ atoms args; $"call ${CoreIrOrdering.compareName (atomType first)}"; "i64.extend_i32_s" ]
        | CallRuntime(name, ([ first ] as args), _) when name = Helpers.valueHash ->
            join "\n    " [ atoms args; $"call ${CoreIrHashing.hashName (atomType first)}"; "i64.extend_i32_s" ]
        | CallRuntime(name, args, _) when wrappingOperations.ContainsKey name ->
            // The instruction on the type it is taken at, keeping the bits that type holds.
            let operation = wrappingOperations.[name]
            let code =
                match expr.Type with
                | IrInt -> [ $"i64.{operation}" ]
                | IrSized integer -> SizedIntegerCode.wrapping integer operation
                | other -> internalError $"{name} at {IrType.show other}, which is not an integer"
            join "\n    " [ atoms args; yield! code ]
        | CallRuntime(name, [ arg ], _) when name = Intrinsics.intCast ->
            // An integer as one of another integer type, which the prelude has checked it fits.
            join "\n    " [ atoms [ arg ]; yield! SizedIntegerCode.cast (integerType (atomType arg)) (integerType expr.Type) ]
        | CallRuntime(name, [ arg ], _) when name = Intrinsics.toFloat ->
            // A number as the nearest float or `F32`, in a box of its own.
            let into, box = if expr.Type = IrF32 then "f32", Helpers.f32Box else "f64", Helpers.floatBox
            let converted =
                match atomType arg with
                | IrFloat -> [ "f64.load"; (if into = "f32" then "f32.demote_f64" else "") ]
                | IrF32 -> [ "f32.load"; (if into = "f64" then "f64.promote_f32" else "") ]
                | integer -> [ SizedIntegerCode.toFloat (integerType integer) into ]
            join "\n    " [ atoms [ arg ]; yield! converted; $"call ${helper box}" ]
        | CallRuntime(name, [ arg ], _) when name = Intrinsics.fromInt ->
            // An int as a sized integer, by the helper of the type it is taken as, or as the nearest `F32`.
            match expr.Type with
            | IrSized integer -> join "\n    " [ atoms [ arg ]; $"call ${helper (SizedIntegerCode.fromIntHelper integer)}" ]
            | IrF32 -> join "\n    " [ atoms [ arg ]; "f32.convert_i64_s"; $"call ${helper Helpers.f32Box}" ]
            | other -> internalError $"{Intrinsics.fromInt} at {IrType.show other}, which is not a sized integer"
        | CallRuntime(name, args, returnsValue) ->
            // An int argument and an int result are converted where the helper holds them in `i32`s, and a helper that
            // returns nothing leaves unit, 0.
            let converts = not (takesInts.Contains name)
            let arguments =
                args
                |> List.mapi (fun index arg ->
                    let conversion =
                        if not converts || atomType arg <> IrInt then ""
                        elif takesIndices.Contains name then $"call ${helper Helpers.intToIndex}"
                        else "i32.wrap_i64"
                    join "\n    " [ atomsAfter (index > 0) [ arg ]; conversion ])
            let result =
                // Exiting never returns: `unreachable` lets the call stand wherever a value of any type is expected.
                if name = Helpers.exit then "unreachable"
                elif not returnsValue then "i32.const 0"
                elif converts && expr.Type = IrInt then "i64.extend_i32_s"
                else ""
            join "\n    " [ yield! arguments; $"call ${helper name}"; result ]
        | MakeClosure(name, arity, captured) ->
            // The function's table slot, then what it captures, each at its offset, with the table slot of the function
            // that drops what it captures as the info word (see `CoreIrDrop`). The block is allocated before anything is
            // stored, and its pointer stays on the stack meanwhile.
            let capturedTys = captured |> List.map atomType
            let offsets = CoreIrLayout.closureOffsets capturedTys
            let slotStore = $"i32.const {context.FunctionSlot name arity}\n    i32.const 0\n    call ${helper Helpers.recordSet}"
            let dropSlot = context.FunctionSlot (CoreIrDrop.environmentDropName capturedTys) 1
            join "\n    " [
                $"i32.const {CoreIrLayout.closureSize capturedTys}\n    i32.const {dropSlot}\n    call ${helper Helpers.newObject}"
                slotStore
                yield! stores (List.zip offsets.Tail captured) ]
        | CallClosure(closure, args) ->
            // The closure is passed first, as the environment, and read again for the table slot it holds.
            let read = $"local.get ${localName closure}"
            let parameters = "i32" :: (args |> List.map (atomType >> CoreIrLayout.valueType))
            let result = CoreIrLayout.valueType expr.Type
            context.IndirectCall(parameters, result)
            let call = if tail then "return_call" else "call"
            join "\n    " [ read; atomsAfter true args; read; "i32.load"; $"{call}_indirect (type ${indirectTypeName parameters result})" ]
        | Interpolate parts -> this.Interpolation parts
        | Dbg text -> join "\n    " [ atoms [ text ]; "call $dbg_str"; "i32.const 0" ]
        | If(cond, ifTrue, ifFalse) ->
            let condCode = atoms [ cond ]
            let result = CoreIrLayout.valueType expr.Type
            $"{condCode}\n    (if (result {result})\n      (then\n        {this.SequenceAs tail expr.Type ifTrue}\n      )\n      (else\n        {this.SequenceAs tail expr.Type ifFalse}\n      )\n    )"
        | Match(scrutinees, arms) -> this.Arms tail expr.Type scrutinees arms
        | Dup(vars, body) -> join "\n    " [ this.DupVars vars; this.Value tail deferred onStack body ]
        | Drop(vars, body) -> join "\n    " [ dropVars localOf vars; this.Value tail deferred onStack body ]
        | Release(released, body) -> join "\n    " [ this.Release released; this.Value tail deferred onStack body ]
        // An allocation into the block a reuse token holds, if it holds one (see `CoreIrReuse`).
        | Reuse(token, { Kind = CallRuntime(_, ([ head; _ ] as args), _) }) ->
            join "\n    " [ $"local.get ${localName token}"; atomsAfter true args; $"call ${storing Helpers.listConsAt (atomType head)}" ]
        | Reuse(token, { Kind = MakeTag(name, payload) }) ->
            join "\n    " [
                $"local.get ${localName token}\n    i32.const {tagCode (context.TagId name)}"
                atomsAfter true [ payload ]
                $"call ${storing Helpers.tagBoxAt (atomType payload)}" ]
        | Reuse(token, { Kind = MakeRecord fields }) ->
            let layoutId = context.RecordLayoutId (List.map fst fields)
            let values = List.map snd fields
            join "\n    " [
                $"local.get ${localName token}\n    i32.const {CoreIrLayout.totalSize (List.map atomType values)}\n    i32.const {layoutId}\n    call ${helper Helpers.reuseObject}"
                yield! fieldStores values ]
        | Reuse _ -> internalError "a reuse of a block by something other than an allocation"
        | DropAfter(body, vars) ->
            // A temporary among `vars` is kept in a local as its value is pushed, to be dropped once `body` is done.
            // Nothing can follow a tail call, so `body` is not one.
            for var in vars do
                if var.Kind = Temp then droppedTemps.[var.Id] <- freshLocal "__owned" (CoreIrLayout.valueType var.Type)
            join "\n    " [ this.Value false deferred onStack body; dropVars localOf vars ]
        | Let _
        | Do _ -> this.Sequence tail expr

    /// The code of the operator `op` on `operands`, one of them at least a sized integer, written by `atomsAfter`, taken
    /// as a value of `ty`. An operand of no known type (see `IrError`) beside a sized integer is taken as one.
    member _.SizedBinary (op: string) (operands: Atom list) (atomsAfter: bool -> Atom list -> string) (ty: IrType) =
        let integer = operands |> List.pick (atomType >> function IrSized integer -> Some integer | _ -> None)
        let sized = IrSized integer
        let operandCode =
            operands
            |> List.mapi (fun index operand -> join "\n    " [ atomsAfter (index > 0) [ operand ]; conversion (atomType operand) sized ])
            |> join "\n    "
        let instruction =
            match SizedIntegerCode.binary integer op with
            | Some (SizedIntegerCode.Instruction instruction) -> instruction
            | Some (SizedIntegerCode.CallHelper name) -> $"call ${helper name}"
            | None -> internalError $"no instruction for the operator {op} on {IrType.show sized}"
        join "\n    " [ operandCode; instruction; conversion (if arithmetic.Contains op then sized else IrBool) ty ]

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
        | AVar v -> $"local.get ${localName v}"
        | AInt value -> $"i64.const {value}"
        | ASized(integer, value) -> $"{CoreIrLayout.valueType (IrSized integer)}.const {value}"
        | ABool value -> if value then "i32.const 1" else "i32.const 0"
        | AUnit -> "i32.const 0"
        | AString value -> $"i32.const {context.StringOffset value}"
        | ATag name -> $"i32.const {tagCode (context.TagId name)}"
        | AStaticClosure(name, arity, _) -> $"i32.const {context.StaticClosure name arity}"

    /// A `for` loop: each time round, every stream gives its next element (or ends the loop), the body's value is
    /// dropped, and every stream moves on. The loop is unit.
    member this.ForLoop (loop: ForLoop) (atomCode: Atom -> string) : string =
        let depth = loop.Depth
        let done' = $"br_if $__for_done_{depth}"
        // What each stream does before the loop, at the top of each time round, and at the bottom.
        let start (stream: ForStream) =
            match stream.Source with
            | OverRange(first, last) -> [ atomCode first; $"local.set ${localName stream.Element}"; atomCode last; $"local.set ${localName stream.End}" ]
            | OverRangeFrom first -> [ atomCode first; $"local.set ${localName stream.Element}" ]
            | OverList list -> [ atomCode list; $"local.set ${localName stream.Cursor}" ]
        // A range counts in ints; a list's cursor moves to each cell's tail, after its head.
        let next (stream: ForStream) =
            match stream.Source with
            | OverRange _ -> [ $"local.get ${localName stream.Element}"; $"local.get ${localName stream.End}"; "i64.gt_s"; done' ]
            | OverRangeFrom _ -> []
            | OverList _ ->
                let cursor = localName stream.Cursor
                [ $"local.get ${cursor}"; "i32.eqz"; done'; $"local.get ${cursor}"; CoreIrLayout.load stream.Element.Type 0; $"local.set ${localName stream.Element}" ]
        let advance (stream: ForStream) =
            match stream.Source with
            | OverRange _
            | OverRangeFrom _ ->
                let element = localName stream.Element
                [ $"local.get ${element}"; "i64.const 1"; $"call ${helper Helpers.intAdd}"; $"local.set ${element}" ]
            | OverList _ ->
                let cursor = localName stream.Cursor
                [ $"local.get ${cursor}"; $"i32.load offset={CoreIrLayout.tailOffset stream.Element.Type}"; $"local.set ${cursor}" ]
        let bodyCode = this.Sequence false loop.Body
        let bodyDrop = dropValue loop.Body.Type
        // The starting atoms are read in the order `tempsUsedBy` gives them.
        let startCode = loop.Streams |> List.collect start |> String.concat "\n    "
        let inLoop =
            List.collect next loop.Streams @ [ bodyCode; bodyDrop ] @ List.collect advance loop.Streams @ [ $"br $__for_next_{depth}" ]
            |> String.concat "\n        "
        $"{startCode}\n    block $__for_done_{depth}\n      loop $__for_next_{depth}\n        {inLoop}\n      end\n    end\n    i32.const 0"

    /// A `while` loop: each time round, the condition ends the loop when it is false, and the body's value is dropped.
    /// The loop is unit.
    member this.WhileLoop (loop: WhileLoop) : string =
        let depth = loop.Depth
        let inLoop =
            [ this.Sequence false loop.Condition
              "i32.eqz"
              $"br_if $__while_done_{depth}"
              this.Sequence false loop.Body
              dropValue loop.Body.Type
              $"br $__while_next_{depth}" ]
            |> String.concat "\n        "
        $"block $__while_done_{depth}\n      loop $__while_next_{depth}\n        {inLoop}\n      end\n    end\n    i32.const 0"

    /// Tests each arm's patterns in turn: a chain of `if`s ending in `unreachable`.
    member this.Arms (tail: bool) (resultTy: IrType) (scrutinees: Var list) (arms: Arm list) : string =
        // The payload of the tag `name` of a value of `ty`, with its type.
        let tagPayloadCode (ty: IrType) (name: string) valueCode =
            let payloadTy =
                match IrType.unfold ty with
                | IrTags tags -> tags |> List.tryPick (fun (tag, payload) -> if tag = name then payload else None)
                | _ -> None
            match payloadTy with
            | Some payloadTy -> $"{valueCode}\n      {CoreIrLayout.load payloadTy CoreIrLayout.payloadOffset}", payloadTy
            | None -> internalError $"a pattern on the payload of #{name}, which {IrType.show ty} does not give one"
        // The field in `slot` of a record of `ty`, with its type.
        let fieldCode (ty: IrType) valueCode slot =
            match ty with
            | IrRecord fields -> $"{valueCode}\n      {CoreIrLayout.load (snd fields.[slot]) (CoreIrLayout.fieldOffset fields slot)}", snd fields.[slot]
            | other -> internalError $"a pattern on the fields of {IrType.show other}, which is not a record"
        let elementOf (ty: IrType) =
            match IrType.unfold ty with
            | IrList element -> element
            | other -> internalError $"a list pattern on {IrType.show other}"
        // The list after `count` cells, and the head of the list after `index` cells, of a list of `ty`.
        let listTailCode (ty: IrType) valueCode count =
            valueCode + String.replicate count $"\n      i32.load offset={CoreIrLayout.tailOffset (elementOf ty)}"
        let listElementCode (ty: IrType) valueCode index =
            $"{listTailCode ty valueCode index}\n      {CoreIrLayout.load (elementOf ty) 0}"
        let guarded first second =
            $"{first}\n      (if (result i32)\n        (then\n          {second})\n        (else i32.const 0))"
        // 1 when every one of `conditions` is.
        let conjunction conditions =
            match conditions with
            | [] -> "i32.const 1"
            | [ single ] -> single
            | many -> (many |> String.concat "\n      ") + String.replicate (many.Length - 1) "\n      i32.and"
        // The same, leaving out the conditions that always hold.
        let allOf conditions = conditions |> List.filter (fun condition -> condition <> "i32.const 1") |> conjunction
        // Whether the value `valueCode` leaves, of type `ty`, matches `pattern`: 1 when it does.
        let rec condition (pattern: Pattern) (valueCode: string) (ty: IrType) =
            match pattern with
            | PWildcard
            | PBind _ -> "i32.const 1"
            | PInt value ->
                // An int, or a sized integer held as its type is.
                let held = match ty with IrSized _ -> CoreIrLayout.valueType ty | _ -> "i64"
                $"{valueCode}\n      {held}.const {value}\n      {held}.eq"
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
                    match tagPayloadCode ty name valueCode ||> condition inner with
                    | "i32.const 1" -> discriminant
                    | payloadCheck -> guarded discriminant payloadCheck
            | PFields fields -> fields |> List.map (fun (slot, inner) -> fieldCode ty valueCode slot ||> condition inner) |> allOf
            | PString value ->
                $"{valueCode}\n      i32.const {context.StringOffset value}\n      call ${helper Helpers.stringEquals}"
            | PGuard(var, guard) -> $"{valueCode}\n      local.set ${localName var}\n      {this.Sequence false guard}"
            // Left to right, each part tested only once those before it match: `#some(_) & { ... }`'s predicate runs
            // only on a `#some`.
            | PAll parts ->
                match parts |> List.map (fun part -> condition part valueCode ty) |> List.filter ((<>) "i32.const 1") with
                | [] -> "i32.const 1"
                | checks -> List.reduceBack guarded checks
            | PList(elements, rest) ->
                // Only the cells the pattern needs are looked at: one for each element, each checked only once the cell
                // before it exists, and then, without a rest, that the list ends there.
                let cellChecks = List.init elements.Length (fun index -> $"{listTailCode ty valueCode index}\n      i32.const 0\n      i32.ne")
                let endCheck =
                    match rest with
                    | Some _ -> []
                    | None -> [ $"{listTailCode ty valueCode elements.Length}\n      i32.eqz" ]
                let lengthCheck =
                    match cellChecks @ endCheck with
                    | [] -> "i32.const 1"
                    | checks -> List.reduceBack guarded checks
                let elementChecks =
                    elements
                    |> List.mapi (fun index element -> condition element (listElementCode ty valueCode index) (elementOf ty))
                    |> allOf
                let restCheck =
                    match rest with
                    | Some restPattern -> condition restPattern (listTailCode ty valueCode elements.Length) ty
                    | None -> "i32.const 1"
                // The elements only exist once the length check holds.
                match lengthCheck, allOf [ elementChecks; restCheck ] with
                | _, "i32.const 1" -> lengthCheck
                | "i32.const 1", elementsAndRest -> elementsAndRest
                | _, elementsAndRest -> guarded lengthCheck elementsAndRest
        // The code that binds the names `pattern` binds in the value `valueCode` leaves, of type `ty`.
        let rec binds (pattern: Pattern) (valueCode: string) (ty: IrType) =
            match pattern with
            | PBind var -> [ $"{valueCode}\n        local.set ${localName var}" ]
            | PTag(name, Some inner) -> tagPayloadCode ty name valueCode ||> binds inner
            | PFields fields -> fields |> List.collect (fun (slot, inner) -> fieldCode ty valueCode slot ||> binds inner)
            | PAll parts -> parts |> List.collect (fun part -> binds part valueCode ty)
            | PList(elements, rest) ->
                (elements |> List.mapi (fun index element -> binds element (listElementCode ty valueCode index) (elementOf ty)) |> List.concat)
                @ (rest
                   |> Option.map (fun restPattern -> binds restPattern (listTailCode ty valueCode elements.Length) ty)
                   |> Option.defaultValue [])
            | _ -> []
        let rec emit (remaining: Arm list) =
            match remaining with
            | [] -> "unreachable"
            | arm :: rest ->
                let conditions =
                    List.map2 (fun pattern (scrutinee: Var) -> condition pattern $"local.get ${localName scrutinee}" scrutinee.Type) arm.Patterns scrutinees
                let allConditions = conjunction conditions
                let bindCode =
                    List.map2 (fun pattern (scrutinee: Var) -> binds pattern $"local.get ${localName scrutinee}" scrutinee.Type) arm.Patterns scrutinees
                    |> List.concat
                    |> String.concat "\n    "
                let armCode = join "\n    " [ bindCode; this.SequenceAs tail resultTy arm.Body ]
                let result = CoreIrLayout.valueType resultTy
                $"{allConditions}\n    (if (result {result})\n      (then\n        {armCode}\n      )\n      (else\n        {emit rest}\n      )\n    )"
        emit arms

/// The locals a function declares beyond its parameters, in this order: its own locals, the scrutinees', what patterns
/// bind, what interpolated strings need (their scratch locals, then their holes by depth), and its scratch locals.
let private declaredLocals (body: Expr) : (string * string) list =
    let found = ResizeArray<Var>()
    // The holes of the interpolated strings, by depth and place.
    let holes = ResizeArray<int * int * Var>()
    let interpolates = ref false
    // The locals of the `for` loops: each loop's, then those of the loops in its body.
    let loops = ResizeArray<Var>()
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
            | InterpolationHole(depth, index) -> holes.Add((depth, index, var))
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
        | Assign _
        | SetField _
        | MakeList _
        | CallRuntime _
        | MakeClosure _
        | CallClosure _
        | Dbg _
        | BoxFloat _
        | Panic _ -> ()
        | Interpolate _ -> interpolates.Value <- true
        | ForEach loop ->
            ForLoop.locals loop |> List.iter loops.Add
            walk loop.Body
        | While loop ->
            walk loop.Condition
            walk loop.Body
    walk body
    let declared (var: Var) = localName var, CoreIrLayout.valueType var.Type
    let ofKind kind = found |> Seq.filter (fun var -> var.Kind = kind) |> Seq.map declared |> List.ofSeq
    let interpolationLocals =
        if interpolates.Value then
            (Interpolation.scratchLocals |> List.map (fun name -> name, "i32"))
            @ [ Interpolation.intLocal, "i64" ]
            @ (holes |> Seq.sortBy (fun (depth, index, var) -> depth, index, localName var) |> Seq.map (fun (_, _, var) -> declared var) |> List.ofSeq)
        else []
    [ yield! ofKind Local
      yield! ofKind MatchTemp
      yield! ofKind PatternBound
      yield! interpolationLocals
      yield! loops |> Seq.map declared
      yield! ofKind Scratch ]
    |> List.distinct

/// The declarations of `locals`, each a name and its value type.
let private localDeclarations (locals: (string * string) list) =
    locals |> List.map (fun (name, valueType) -> $"(local ${name} {valueType})") |> String.concat " "

/// `func` as a WebAssembly function, exported under `exportName` if given.
let emitFunction (context: EmitContext) (func: Function) (exportName: string option) : Emitted =
    let emitter = FunctionEmitter(context)
    let bodyCode = emitter.SequenceAs true func.Result func.Body
    let header =
        [ func.Params |> List.map (fun p -> $"(param ${localName p} {CoreIrLayout.valueType p.Type})") |> String.concat " "
          $"(result {CoreIrLayout.valueType func.Result})"
          localDeclarations (declaredLocals func.Body @ emitter.ExtraLocals) ]
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
    let notExpressions () = internalError "the start function is not a sequence of top-level expressions"
    let expressions =
        match Chain.split func.Body with
        | links, { Kind = Atom AUnit } -> links |> List.map (fun link -> match link.Kind with Do(value, _) -> value | _ -> notExpressions ())
        | _ -> notExpressions ()
    let bodyCode =
        expressions
        |> List.map (fun value -> $"{emitter.Sequence false value}\n    {dropValue value.Type}")
        |> String.concat "\n    "
    let header =
        match localDeclarations (declaredLocals func.Body @ emitter.ExtraLocals) with
        | "" -> ""
        | locals -> " " + locals
    { Text = $"  (func ${func.Name}{header}\n    {bodyCode}\n  )"; Helpers = emitter.Helpers }
