/// WebAssembly text from the core IR (`CoreIr.fs`).
///
/// Each value is held in the WebAssembly type of its type (see `CoreIrLayout`): an int in an `i64`, a float in an `f64`,
/// an `F32` in an `f32`, everything else in an `i32`. A local of a value not held in an `i32` is named with its value
/// type after the variable's name, `_i64`, `_f64` or `_f32` (see `localName`), since variables of the same kind and name
/// share a local, and may be of different types. A temporary is not stored: its value is computed
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
open Transpiler.Wasm.WatPieces
open Transpiler.Wasm.CoreIrEmitImports
open Transpiler.Wasm.CoreIrEmitIntrinsics
open Transpiler.Wasm.CoreIrEmitLocals
open Transpiler.Wasm.CoreIrEmitReads

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
      Signature: string -> (IrType list * IrType) option
      /// Where the block the module sets aside for its host is (see `WasmRuntime.hostStateSize`).
      HostState: int
      /// Whether the module is compiled with memory checks (see `WasmAllocator.MemoryChecks`), which count the records
      /// updated in place.
      CheckMemory: bool
      /// The name a program shows for the source file at this path, as a span names it (see `PackageModules.shownFile`), when
      /// the code is marked with the places it was made from (see `placeMarker`); none when it isn't.
      ShownFile: (string -> string) option
      /// Whether the function of this name takes over the argument it is given at this place, rather than borrowing it
      /// (see `CoreIrCounting`): a probe gives a printer that does a reference of its own.
      TakesOver: string -> int -> bool
      /// A new probe's number, unique in the module, which the runtime knows it by (see `WasmBinary.spanTableName`).
      ProbeId: unit -> int
      /// Whether a `dbg` gives what it prints to the runtime's `nanyx:debug` `dbg` first, which a module the runtime runs
      /// has (see `WasmOptions.Probes`), and a page asks for (`WasmOptions.DbgToEmbedder`): the host decides where it
      /// goes. Without it, or where the runtime leaves it to the program, the program writes it to its stderr through
      /// `$dbg_str`.
      DbgToRuntime: bool
      /// The functions that are state machines, which can stop at a call and carry on later (see `CoreIrSplitPoints`).
      StateMachines: Set<string>
      /// Whether each call of a function value that can wait stops once first (see `WasmOptions.YieldAtEveryWait`).
      YieldAtEveryWait: bool
      /// Whether each state machine has a drop entry, which frees a frame of it without resuming it (see
      /// `WasmOptions.FrameDrops`).
      FrameDrops: bool
      /// The variables a function borrows, by id, which hold no reference of their own (see `CoreIrCounting.borrowedIn`).
      BorrowedIn: Function -> Set<int> }

/// The marker that says the code after it, up to the next one, was made from source at `place`: a line comment,
/// `;;@ main.nyx:12:5`, its file, line and column, that `WasmBinary` writes into the module's table of places (see
/// `WatSyntax.Placed`). Code inlined from a function says which, and where the call was, for each call it was inlined
/// through, innermost first: `;;@ prelude.nyx:18:3 | crash | main.nyx:12:5`.
let placeMarker (shownFile: string -> string) (place: Place) =
    let rec frames (place: Place) =
        let here = $"{shownFile place.File}:{place.Line}:{place.Column}"
        match place.InlinedAt with
        | Some(name, site) -> $"{here} | {name} | {frames site}"
        | None -> here
    $";;@ {frames place}"

/// The marker that says the places in the file a place marker names `shown` are in the standard library (see
/// `WatSyntax.Library`), so that a crash's report names the program's code that called it: a line comment among the
/// module's fields, `;;@lib prelude.nyx`.
let libraryMarker (shown: string) = $"  ;;@lib {shown}"

/// Whether `file`, a place's, is a file of the standard library's, which is named by its module (`nanyx/prelude.nyx`)
/// rather than by a path.
let isLibraryFile (file: string) =
    file.StartsWith NyxCompiler.StandardLibrary.importPrefix && not (System.IO.Path.IsPathRooted file)

/// The marker that says the function it starts was defined at `place`, the file and line of its definition: a line
/// comment, `;;@def main.nyx:3`, that `WasmBinary` writes into the module's table of places (see `WatSyntax.Defined`).
let definitionMarker (shownFile: string -> string) (place: Place) = $";;@def {shownFile place.File}:{place.Line}"

/// A function as WebAssembly text, the runtime helpers (see `WasmRuntime.Helpers`) it calls, and whether its own code
/// reads or writes linear memory.
type Emitted =
    { Text: string
      Helpers: Set<string>
      UsesMemory: bool
      /// The table slot of the resume entry and the name of the drop entry of a state machine that has one (see
      /// `EmitContext.FrameDrops`).
      FrameDrop: (int * string) option }

let private join (separator: string) (parts: string seq) =
    parts |> Seq.filter (fun part -> part <> "") |> String.concat separator

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

    // Whether the code reads or writes linear memory itself, besides what the helpers it calls do.
    let mutable usesMemory = false

    // The place marker of the value whose code is being written, if it has one (see `Value`).
    let mutable currentMarker: string option = None

    // The code of each value, sequence and remaining match written out of line (see `WatPieces`), which `Expand` puts
    // back once the function's code is done.
    let pieces = WatPieces()

    let helper (name: string) =
        helpers.Add name |> ignore
        name

    /// `code`, which reads or writes linear memory.
    let touching (code: string) =
        usesMemory <- true
        code

    let load (ty: IrType) (offset: int) = touching (CoreIrLayout.load ty offset)

    let store (ty: IrType) (offset: int) = touching (CoreIrLayout.store ty offset)

    let freshLocal (prefix: string) (valueType: string) =
        let name = $"{prefix}_{extraLocals.Count}"
        extraLocals.Add((name, valueType))
        name

    // The names of the function's parameters and locals, which a probe writes only those of (see `Probe`).
    let mutable declared = Set.empty<string>

    // The local the function reads whether the runtime is probing from, which it copies from the global when it starts
    // (see `ProbingCopy`): a local can stay in a register, where each probe's read of the global would load it.
    let probingLocal = lazy (freshLocal "__probing" "i32")

    // The local a probe holds a local's text in while the runtime reads it (see `Probe`).
    let probeText = lazy (freshLocal "__probe_text" "i32")

    // The split points of the function, when it is a state machine (see `CoreIrSplitPoints`), its name and its result,
    // and the locals it keeps its state in and the temporaries it keeps in locals (see `TaskLowering`).
    let mutable plan: CoreIrSplitPoints.SplitPlan option = None
    let mutable machine = ("", IrUnit)
    let storedTemps = Dictionary<int, string>()
    let resumeLocal = lazy (freshLocal "__resume" "i32")
    let childLocal = lazy (freshLocal "__child" "i32")
    let frameLocal = lazy (freshLocal "__frame" "i32")

    let localOf (var: Var) =
        match droppedTemps.TryGetValue var.Id with
        | true, local -> local
        | _ ->
            match storedTemps.TryGetValue var.Id with
            | true, local -> local
            | _ -> localName var

    // Whether the state machine's code `expr` holds a split point.
    let holds (expr: Expr) = plan |> Option.exists (fun plan -> CoreIrSplitPoints.holds plan expr)

    // The states inside `expr`, if it holds any.
    let rangeOf (expr: Expr) =
        match plan with
        | Some plan -> match plan.Ranges.TryGetValue expr with | true, range -> Some range | _ -> None
        | None -> None

    // `code`, which leaves the stack as it found it, run only when the function is not resuming, or resumes in `range`.
    let unlessResuming (range: (int * int) option) (code: string) = TaskLowering.onlyWhen (TaskLowering.runsAt resumeLocal.Value range) code

    // The code that leaves 1 when the function is not resuming, or else whether it resumes in `range`, and otherwise
    // what `normally` leaves (see `TaskLowering`).
    let choosing (normally: string) (range: (int * int) option) =
        let resuming = match range with Some range -> TaskLowering.resumesIn resumeLocal.Value range | None -> "i32.const 0"
        $"local.get ${resumeLocal.Value}\n    i32.eqz\n    (if (result i32)\n      (then\n        {normally})\n      (else\n        {resuming}))"

    member _.Helpers = Set.ofSeq helpers

    /// Makes the function a state machine with the split points `found`, named `name` and giving a value of type
    /// `result`.
    member _.Machine(found: CoreIrSplitPoints.SplitPlan, name: string, result: IrType) =
        plan <- Some found
        machine <- (name, result)

    /// The code a state machine starts with, which restores what it kept when it is resumed (see `TaskLowering.prologue`).
    member _.Prologue =
        match plan with
        | Some plan ->
            helper Helpers.free |> ignore
            let saved = plan.Saved |> Map.map (fun _ vars -> vars |> List.distinctBy localOf |> List.map (fun var -> localOf var, CoreIrLayout.valueType var.Type))
            [ TaskLowering.prologue resumeLocal.Value childLocal.Value frameLocal.Value saved ]
        | None -> []

    /// What each state of the state machine keeps that holds something, by its index among the variables the state
    /// keeps (see `TaskLowering.dropEntry`): each variable on the heap but those in `borrowed`, and each of the reuse
    /// tokens `tokens`.
    member _.Held (borrowed: Set<int>) (tokens: Set<int>) : Map<int, (int * TaskLowering.Held) list> =
        match plan with
        | Some plan ->
            helper Helpers.free |> ignore
            let held (var: Var) =
                if tokens.Contains var.Id then Some TaskLowering.Token
                elif CoreIrDrop.isHeapType var.Type && not (borrowed.Contains var.Id) then
                    Some(TaskLowering.Reference(CoreIrDrop.dropName var.Type))
                else None
            plan.Saved
            |> Map.map (fun _ vars ->
                vars |> List.distinctBy localOf |> List.indexed |> List.choose (fun (index, var) -> held var |> Option.map (fun value -> index, value)))
        | None -> Map.empty

    member _.UsesMemory = usesMemory

    /// The function's code `code` with what each value, sequence and match in it wrote out of line put back.
    member _.Expand (code: string) = pieces.Expand code

    /// The code that copies whether the runtime is probing into the function's local of it, if a probe in the function
    /// reads it, which the function starts with.
    member _.ProbingCopy =
        if probingLocal.IsValueCreated then [ $"global.get ${probing}"; $"local.set ${probingLocal.Value}" ] else []

    /// The names of the function's parameters and locals (see `Probe`).
    member _.Declared with set (names: Set<string>) = declared <- names

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
                      let field = touching $"i32.load offset={offset}"
                      $"local.get ${object}\n      {field}\n      call ${CoreIrDrop.dropName fieldTy}"
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
        let count = $"local.get ${object}\n    i32.const {released.CountOffset}\n    i32.sub\n    " + touching "i32.load"
        $"{count}\n    i32.const 1\n    i32.eq\n    if\n      {lines unique}\n    else\n      {lines shared}\n    end"

    /// The code that frees the block a reuse token holds, if it holds one (see `FreeToken`).
    member _.FreeToken (token: Var) =
        $"local.get ${localName token}\n    if\n      local.get ${localName token}\n      call ${helper Helpers.free}\n    end"

    /// How a hole of type `holeType` is measured and written (see `Interpolation`).
    member _.HoleKind (holeType: HoleType) : Interpolation.HoleKind =
        // An int is measured and written by helpers (see `Interpolation.intLength`).
        let intHole kind =
            helper Helpers.intTextLength |> ignore
            helper Helpers.intTextWrite |> ignore
            kind
        match holeType with
        | HoleString -> Interpolation.StringHole
        | HoleInt -> intHole Interpolation.IntHole
        | HoleSized SizedInteger.U64 -> intHole Interpolation.UnsignedHole
        | HoleSized _ -> intHole Interpolation.IntHole
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
                        | IrInt -> intHole Interpolation.IntPayload
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
                | Text text -> Interpolation.textWrite context.StringOffset text
                | Hole(var, holeType) -> Interpolation.holeWrite (this.HoleKind holeType) (holeValue var holeType))
            |> String.concat "\n    "
        helper Helpers.stringAlloc |> ignore
        touching (Interpolation.measureAndWrite lengthCode writeCode)

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

        // In a state machine resuming at a split point in this chain, each link before the last one that holds a split
        // point runs only if the state is in it, or the function is not resuming (see `TaskLowering`).
        let guarded =
            let links, last = Chain.split expr
            let linkHolds (link: Expr) = match link.Kind with Let(_, value, _) | Do(value, _) -> holds value | _ -> false
            let lastHolding = if holds last then links.Length else links |> List.tryFindIndexBack linkHolds |> Option.defaultValue 0
            HashSet<Expr>(List.take lastHolding links, HashIdentity.Reference)
        let guard (link: Expr) (code: string) =
            if not (guarded.Contains link) then code
            else
                match link.Kind with
                | Let(_, value, _) | Do(value, _) -> unlessResuming (rangeOf value) code
                | _ -> unlessResuming None code

        // Where the chain's probes are, and what each may write.
        let chainReads =
            lazy
                (let links, last = Chain.split expr
                 let places = Dictionary<Expr, int>(HashIdentity.Reference)
                 links |> List.iteri (fun place link -> places.[link] <- place)
                 ChainReads(links, last), places)

        // A temporary a state machine keeps in a local (see `SplitPlan.StoredTemps`) is computed where a temporary that
        // waits would be, at its use: after the counting that comes between, and before the next code that runs. The
        // lowering counts before it computes a temporary that waits, as in `f(x, x.length)`, where `x.length` gives up
        // the reference to `x` that the count before the call took (#2490). So the stored temporaries wait in
        // `storedParts`, after the counting links that follow them and the counting a later stored value starts with.
        let storedParts = ResizeArray<string>()
        let addPart (code: string) =
            parts.AddRange storedParts
            storedParts.Clear()
            parts.Add code
        // Counting peeled off a stored value runs once, before the function first stops: never again as it resumes.
        let counting (link: Expr) (code: string) = if guarded.Contains link then unlessResuming None code else code
        let rec peelCounting (link: Expr) (value: Expr) =
            match value.Kind with
            | Dup(vars, inner) when storedParts.Count > 0 ->
                parts.Add(counting link (this.DupVars vars))
                peelCounting link inner
            | Drop(vars, inner) when storedParts.Count > 0 ->
                parts.Add(counting link (dropVars localOf vars))
                peelCounting link inner
            | _ -> value

        let rec walk (expr: Expr) =
            match expr.Kind with
            | Let(var, value, body) when var.Kind = Temp && not (plan |> Option.exists (fun plan -> plan.StoredTemps.Contains var.Id)) ->
                claim value
                deferred.[var.Id] <- value
                waiting.Add var
                walk body
            | Let(var, value, body) when var.Kind = Temp ->
                claim value
                flush ()
                storedTemps.[var.Id] <- freshLocal "__kept" (CoreIrLayout.valueType var.Type)
                let value = peelCounting expr value
                storedParts.Add(guard expr (join "\n    " [ this.Value false deferred onStack value; $"local.set ${localOf var}" ]))
                walk body
            | Let(var, value, body) ->
                claim value
                flush ()
                addPart (guard expr (join "\n    " [ this.Value false deferred onStack value; $"local.set ${localOf var}" ]))
                walk body
            // A probe stores nothing a temporary waits on, so it can stand among them. Behind a stored value it waits
            // with it, so that the counting after it still comes before the value is computed, as it does where there
            // is no probe: a return probe stands between the call that gives a stored argument and a tail call that
            // takes a reference to a variable that call last reads (#2591).
            | Do({ Kind = Probe(kind, locals) } as probe, rest) ->
                let reads, places = chainReads.Value
                let code = guard expr (this.Probe probe kind locals (reads.AliveAfter places.[expr]))
                if storedParts.Count > 0 then storedParts.Add code else addPart code
                walk rest
            | Do(value, rest) ->
                claim value
                flush ()
                addPart (guard expr (join "\n    " [ this.Value false deferred onStack value; dropValue value.Type ]))
                walk rest
            // Counting leaves the stack as it is, so it can happen while temporaries wait to be used.
            | Dup(vars, body) ->
                parts.Add(guard expr (this.DupVars vars))
                walk body
            | Drop(vars, body) ->
                parts.Add(guard expr (dropVars localOf vars))
                walk body
            | Release(released, body) ->
                parts.Add(guard expr (this.Release released))
                walk body
            | FreeToken(token, body) ->
                parts.Add(guard expr (this.FreeToken token))
                walk body
            | _ ->
                claim expr
                if waiting.Count > 0 then internalError "a temporary in the core IR is never used"
                addPart (this.Value tail deferred onStack expr)
        walk expr
        if deferred.Count > 0 || onStack.Count > 0 then internalError "a temporary in the core IR is used out of order"
        pieces.Add(join "\n    " parts)

    /// The code of the probe `probe` (see `CoreIrProbes`): when the runtime is probing (see `ProbingCopy`), a call with the probe's number,
    /// and, if the runtime asks, each of its locals `alive` has, written by its printer, passed with its name. Before it
    /// go the marker of its place and its own, `;;@probe 3 statement` (with the function's name after `entry`), from which `WasmBinary` tells the runtime where
    /// each probe is.
    member this.Probe (probe: Expr) (kind: ProbeKind) (locals: ProbeLocal list) (isAlive: Var -> bool) : string =
        let id = context.ProbeId ()
        let place =
            match probe.Place, context.ShownFile with
            | Some place, Some shownFile -> placeMarker shownFile place
            | _ -> ""
        let kindName =
            match kind with
            | Entry functionName -> $"entry {functionName}"
            | ProbeKind.Statement -> "statement"
            | Return -> "return"
        let writes =
            locals
            |> List.filter (fun local -> isAlive local.Var && declared.Contains (localName local.Var))
            |> List.map (fun local ->
                let text = probeText.Value
                join "\n    " [
                    $"i32.const {context.StringOffset local.Name}"
                    (if context.TakesOver local.Printer 0 then this.DupVars [ local.Var ] else "")
                    $"local.get ${localName local.Var}"
                    $"call ${local.Printer}"
                    $"local.tee ${text}"
                    $"call ${probeLocal}"
                    $"local.get ${text}"
                    dropValue IrString ])
        join "\n    " [
            place
            $";;@probe {id} {kindName}"
            $"local.get ${probingLocal.Value}"
            "if"
            $"i32.const {id}"
            $"call ${probeCall}"
            "if"
            yield! writes
            "end"
            "end" ]

    /// The code of a sequence whose value is taken as a value of `ty` (see `conversion`): a branch of an `if` or a
    /// `match`, or a function's body. Nothing can follow a tail call, so a sequence that needs converting makes none.
    member this.SequenceAs (tail: bool) (ty: IrType) (expr: Expr) : string =
        match conversion expr.Type ty with
        | "" -> this.Sequence tail expr
        | converted -> $"{this.Sequence false expr}\n    {converted}"

    /// The code of a value that is not a `Let` or `Do`, after the marker of where it was written when it calls or can
    /// trap (see `placeMarker`).
    member this.Value (tail: bool) (deferred: Dictionary<int, Expr>) (onStack: HashSet<int>) (expr: Expr) : string =
        match expr.Place, context.ShownFile with
        | Some place, Some shownFile when marked expr.Kind ->
            let marker = placeMarker shownFile place
            let around = currentMarker
            currentMarker <- Some marker
            let code = this.SplitValue tail deferred onStack expr
            currentMarker <- around
            pieces.Add $"{marker}\n    {code}"
        | _ -> pieces.Add(this.SplitValue tail deferred onStack expr)

    /// The code of a value that may be a split point (see `CoreIrSplitPoints`): in a state machine, a call it can stop
    /// in and resume; in a function that is not one, a call it runs to the end (see `TaskLowering`).
    member private this.SplitValue (tail: bool) (deferred: Dictionary<int, Expr>) (onStack: HashSet<int>) (expr: Expr) : string =
        // The type of what the callee gives, which its resume entry gives too, and the code that converts it to the
        // call's, as `Call` converts it.
        let calleeResult =
            match expr.Kind with
            | Call(name, _) -> context.Signature name |> Option.map snd |> Option.defaultValue expr.Type
            | _ -> expr.Type
        let resumeType () =
            let result = CoreIrLayout.valueType calleeResult
            context.IndirectCall(TaskLowering.resumeTypeOf result)
            TaskLowering.resumeTypeName result
        match plan with
        | Some found when found.States.ContainsKey expr ->
            let state = found.States.[expr]
            let name, result = machine
            let resumeSlot = context.FunctionSlot (TaskLowering.Names.resumeEntry name) 1
            helper Helpers.alloc |> ignore
            helper Helpers.free |> ignore
            let stop (at: int) =
                let kept = found.Saved.[at] |> List.distinctBy localOf |> List.map (fun var -> localOf var, CoreIrLayout.valueType var.Type)
                TaskLowering.stopAfter frameLocal.Value resumeSlot at kept (CoreIrLayout.valueType result)
            let resumable (at: int) (resultType: string) (resumeType: string) (direct: TaskLowering.DirectResume option) (call: string) =
                TaskLowering.resumable resumeLocal.Value childLocal.Value at resumeType resultType direct call
            // A call of a function value first stops once, when every wait does (see `WasmOptions.YieldAtEveryWait`).
            let yielded, state =
                if CoreIrSplitPoints.statesOf context.YieldAtEveryWait expr = 2 then
                    let unitResume = (context.IndirectCall(TaskLowering.resumeTypeOf "i32"); TaskLowering.resumeTypeName "i32")
                    // Skipped when resuming at the call, after it.
                    let yieldCode = join "\n    " [ resumable state "i32" unitResume None $"call ${TaskLowering.Names.yieldOnce}"; "drop"; stop state ]
                    [ unlessResuming (Some (state, state)) yieldCode ], state + 1
                else [], state
            // Nothing follows a tail call: the callee's frame takes the function's place, so it keeps nothing.
            if tail then join "\n    " [ yield! yielded; this.UnmarkedValue true deferred onStack expr ]
            else
                let resumed = conversion calleeResult expr.Type
                let call = this.UnmarkedValue false deferred onStack expr
                let direct = TaskLowering.directResume context.StateMachines context.Signature context.FunctionSlot expr
                let call = resumable state (CoreIrLayout.valueType expr.Type) (resumeType ()) direct call
                let call = if resumed = "" then call else internalError "a call that can stop whose value needs converting"
                join "\n    " [ yield! yielded; call; stop state ]
        | None when not context.StateMachines.IsEmpty && CoreIrSplitPoints.isSplit context.StateMachines expr ->
            if conversion calleeResult expr.Type <> "" then internalError "a call that can stop whose value needs converting"
            let call = this.UnmarkedValue false deferred onStack expr
            let value = freshLocal "__blocked" (CoreIrLayout.valueType expr.Type)
            let frame = freshLocal "__blocked_frame" "i32"
            join "\n    " [ call; TaskLowering.blockOn value frame (resumeType ()) ]
        | _ -> this.UnmarkedValue tail deferred onStack expr

    member private this.UnmarkedValue (tail: bool) (deferred: Dictionary<int, Expr>) (onStack: HashSet<int>) (expr: Expr) : string =
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
            match IrType.unfold record with
            | IrRecord fields -> fields
            | other -> internalError $"a field of '{IrType.show other}', which is not a record"
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
                | "//" | "%" -> $"call ${helper (DivisionCode.intHelper op).Value}"
                | _ ->
                    match wasmIntInstruction op with
                    | Some instruction -> instruction
                    | None -> internalError $"no instruction for the operator `{op}` on ints"
            // Arithmetic gives an int and a comparison a bool, taken as the type the expression has.
            let given = if arithmetic.Contains op then IrInt else IrBool
            join "\n    " [ operandCode; instruction; conversion given expr.Type ]
        | Prim(Binary op, operands) when operands |> List.exists (atomType >> function IrSized _ -> true | _ -> false) ->
            this.SizedBinary op operands atomsAfter expr.Type
        | Prim(Binary op, operands) ->
            match wasmBinaryInstruction op with
            | Some instruction -> join "\n    " [ atoms operands; instruction ]
            | None -> internalError $"no instruction for the operator `{op}`"
        | Prim(Not, operands) -> join "\n    " [ atoms operands; "i32.eqz" ]
        | Prim(FloatBinary op, [ left; right ]) ->
            // The instruction on the values; a comparison's result is the i32 it leaves. An `F32` is an `f32`, whose `//`
            // and `%`, helpers (see `DivisionCode`), are taken as a float's, which are exact, and narrowed back.
            let isF32 = atomType left = IrF32
            let floored = DivisionCode.floatHelper op
            let widen = if isF32 && floored.IsSome then "f64.promote_f32" else ""
            let instruction =
                if floored.IsSome then Some (join "\n    " [ $"call ${helper floored.Value}"; (if isF32 then "f32.demote_f64" else "") ])
                elif isF32 then wasmFloatInstruction op |> Option.map (fun instruction -> instruction.Replace("f64.", "f32."))
                else wasmFloatInstruction op
            match instruction with
            | Some instruction ->
                join "\n    " [ atoms [ left ]; widen; atomsAfter true [ right ]; widen; instruction ]
            | None -> internalError $"no instruction for the operator `{op}` on floats"
        | Prim(FloatBinary op, _) -> internalError $"the operator `{op}` on floats with other than two operands"
        | FloatConstant value when expr.Type = IrF32 -> $"f32.const {wasmFloatConst value}"
        | FloatConstant value -> $"f64.const {wasmFloatConst value}"
        | Probe(kind, locals) -> join "\n    " [ this.Probe expr kind locals (fun _ -> false); "i32.const 0" ]
        | Crash messages ->
            // Writes each message to stderr and traps in `Helpers.crashed`, given them all as one, so the runtime reports
            // them and the stack. `unreachable` after it, which is never reached, lets the code stand wherever a value is
            // expected.
            let writes = messages |> List.map (fun message -> $"i32.const {context.StringOffset message}\n    call ${helper Helpers.writeStderr}")
            let all = context.StringOffset(String.concat "" messages)
            writes @ [ $"i32.const {all}"; $"call ${helper Helpers.crashed}"; "unreachable" ] |> String.concat "\n    "
        | CrashWith message ->
            // As `Crash`, with a message made at run time, which `Helpers.crashed` is given too.
            join "\n    " [ atoms [ message ]; $"call ${helper Helpers.writeStderr}"; atoms [ message ]; $"call ${helper Helpers.crashed}"; "unreachable" ]
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
            join "\n    " [ atoms [ record ]; load expr.Type (CoreIrLayout.fieldOffset fields slot); dup ]
        | UpdateRecord(record, updates) -> this.UpdateRecord record (recordFields (atomType record)) updates atomsAfter storing

        | Assign(cell, value) ->
            // The new value is computed before the old one is dropped, since it may be made from it.
            let dropOld = if CoreIrDrop.isHeapType cell.Type then $"local.get ${localName cell}\n    {dropValue cell.Type}" else ""
            join "\n    " [ atoms [ value ]; dropOld; $"local.set ${localName cell}"; "i32.const 0" ]
        | SetField(record, slot, value) ->
            let fieldTy = atomType value
            let offset = CoreIrLayout.fieldOffset (recordFields record.Type) slot
            let held = freshLocal "__set" (CoreIrLayout.valueType fieldTy)
            let dropOld =
                if CoreIrDrop.isHeapType fieldTy then $"local.get ${localName record}\n    {load fieldTy offset}\n    {dropValue fieldTy}" else ""
            join "\n    " [
                atoms [ value ]
                $"local.set ${held}"
                dropOld
                $"local.get ${localName record}\n    local.get ${held}\n    {store fieldTy offset}"
                "i32.const 0" ]
        | MakeList items ->
            // The elements left to right, then the empty list, then a cons for each from the back.
            let element =
                match expr.Type with
                | IrList element -> element
                | other -> internalError $"a list literal of type '{IrType.show other}'"
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
                | other -> internalError $"'{name}' at '{IrType.show other}', which is not an integer"
            join "\n    " [ atoms args; yield! code ]
        | CallRuntime(name, args, _) when bitwiseOperations.ContainsKey name -> join "\n    " [ atoms args; yield! bitwiseOperations.[name] ]
        // What a finished task gave back, through the function it keeps it as (see `WasmScheduler`).
        | CallRuntime(name, [ handle ], _) when name = Intrinsics.taskResult ->
            let result = CoreIrLayout.valueType expr.Type
            context.IndirectCall(TaskLowering.resumeTypeOf result)
            join "\n    " [ atoms [ handle ]; yield! WasmScheduler.readResult (freshLocal "__result" "i32") (TaskLowering.resumeTypeName result) |> List.map touching ]
        | CallRuntime(name, [ arg ], _) when name = Intrinsics.intCast ->
            // An integer as one of another integer type, which the prelude has checked it fits.
            join "\n    " [ atoms [ arg ]; yield! SizedIntegerCode.cast (integerType (atomType arg)) (integerType expr.Type) ]
        | CallRuntime(name, [ arg ], _) when name = Intrinsics.toFloat ->
            // A number as the nearest float or `F32`.
            let into = if expr.Type = IrF32 then "f32" else "f64"
            let converted =
                match atomType arg with
                | IrFloat -> if into = "f32" then "f32.demote_f64" else ""
                | IrF32 -> if into = "f64" then "f64.promote_f32" else ""
                | integer -> SizedIntegerCode.toFloat (integerType integer) into
            join "\n    " [ atoms [ arg ]; converted ]
        | CallRuntime(name, [ arg ], _) when name = Intrinsics.fromInt ->
            // An int as a sized integer, by the helper of the type it is taken as, or as the nearest `F32`.
            match expr.Type with
            | IrSized integer -> join "\n    " [ atoms [ arg ]; $"call ${helper (SizedIntegerCode.fromIntHelper integer)}" ]
            | IrF32 -> join "\n    " [ atoms [ arg ]; "f32.convert_i64_s" ]
            | other -> internalError $"'{Intrinsics.fromInt}' at '{IrType.show other}', which is not a sized integer"
        | CallRuntime(name, args, _) when memoryAccesses.ContainsKey name ->
            let instruction, writes = memoryAccesses.[name]
            join "\n    " [ atoms args; touching instruction; (if writes then "i32.const 0" else "") ]
        | CallRuntime(name, [ size ], _) when name = Intrinsics.memoryAllocate -> join "\n    " [ atoms [ size ]; $"call ${helper Helpers.alloc}" ]
        | CallRuntime(name, [ address ], _) when name = Intrinsics.memoryFree ->
            join "\n    " [ atoms [ address ]; $"call ${helper Helpers.free}"; "i32.const 0" ]
        // A string is the address of its bytes, with its length in the word before them.
        | CallRuntime(name, [ text ], _) when name = Intrinsics.stringAddress -> atoms [ text ]
        | CallRuntime(name, _, _) when name = Intrinsics.hostState -> touching $"i32.const {context.HostState}"
        // Never returns: `unreachable` lets the call stand wherever a value of any type is expected.
        | CallRuntime(name, [ message ], _) when name = Intrinsics.crashed ->
            join "\n    " [ atoms [ message ]; $"call ${helper Helpers.crashed}"; "unreachable" ]
        | CallRuntime(name, [ text ], _) when name = Intrinsics.keepAlive -> join "\n    " [ atoms [ text ]; "drop"; "i32.const 0" ]
        | CallRuntime(name, [ text ], _) when name = Intrinsics.stringByteLength ->
            join "\n    " [ atoms [ text ]; $"call ${helper Helpers.stringLength}" ]
        | CallRuntime(name, args, _) when arrayIntrinsics.Contains name -> this.ArrayIntrinsic atoms expr.Type name args
        | CopyArrayInto(target, at, source) -> this.CopyArrayInto atoms target at source
        // A list cell's tail is given up and the list, which the cell takes over, is stored in its place.
        | CallRuntime(name, [ cell; tail ], _) when name = Intrinsics.listSetTail ->
            let listTy = atomType cell
            let offset =
                match listTy with
                | IrList element -> CoreIrLayout.tailOffset element
                | other -> internalError $"'{name}' on '{IrType.show other}', which is not a list"
            let at = freshLocal "__cell" "i32"
            let held = freshLocal "__tail" "i32"
            join "\n    " [
                atoms [ cell ]
                $"local.set ${at}"
                atoms [ tail ]
                $"local.set ${held}"
                $"local.get ${at}\n    {load listTy offset}\n    {dropValue listTy}"
                $"local.get ${at}\n    local.get ${held}\n    {store listTy offset}"
                "i32.const 0" ]
        // A list cell's head is given up and the value, which the cell takes over, is stored in its place.
        | CallRuntime(name, [ cell; value ], _) when name = Intrinsics.listSetHead ->
            let element =
                match atomType cell with
                | IrList element -> element
                | other -> internalError $"'{name}' on '{IrType.show other}', which is not a list"
            let at = freshLocal "__cell" "i32"
            let held = freshLocal "__head" (CoreIrLayout.valueType element)
            let dropOld =
                if CoreIrDrop.isHeapType element then $"local.get ${at}\n    {load element 0}\n    {dropValue element}" else ""
            join "\n    " [
                atoms [ cell ]
                $"local.set ${at}"
                atoms [ value ]
                $"local.set ${held}"
                dropOld
                $"local.get ${at}\n    local.get ${held}\n    {store element 0}"
                "i32.const 0" ]
        // A new object goes on its region's list with the table slot of the function that drops what it holds (see
        // `CoreIrRegions`).
        | CallRuntime(name, [ object ], _) when name = WasmRegions.Names.regionTrack ->
            let slot = context.FunctionSlot (CoreIrRegions.dropContentsName (atomType object)) 1
            join "\n    " [ atoms [ object ]; $"i32.const {slot}"; $"call ${helper name}"; "i32.const 0" ]
        // A value stored where it may outlive its `memory` block is promoted first (see `CoreIrPromotion`): onto the region
        // of the object it is stored into, onto the outermost region, or, for a block's result, onto the region of the
        // block around it, if there is one, before the block's own region ends.
        | CallRuntime(name, [ container; value ], _) when name = WasmRegions.Names.regionStore ->
            let promote = if CoreIrRegions.canHoldMutable (atomType value) then [ atoms [ value ]; atomsAfter true [ container ]; touching $"i32.load offset={WasmRegions.ownerOffset}"; $"call ${CoreIrRegions.promoteName (atomType value)}" ] else []
            join "\n    " [ yield! promote; "i32.const 0" ]
        | CallRuntime(name, [ value ], _) when name = WasmRegions.Names.regionStoreOutermost ->
            let promote = if CoreIrRegions.canHoldMutable (atomType value) then [ atoms [ value ]; $"global.get {WasmRegions.outermostGlobal}"; $"call ${CoreIrRegions.promoteName (atomType value)}" ] else []
            helper WasmRegions.Names.regionGlobals |> ignore
            join "\n    " [ yield! promote; "i32.const 0" ]
        | CallRuntime(name, [ result ], _) when name = WasmRegions.Names.regionExit ->
            let outer = freshLocal "__outer_region" "i32"
            // A temporary result is computed here, where it is read, into a local of its own: the promotion reads it
            // only when there is a block around this one, and a result that needs none doesn't read it, but a
            // temporary is computed once, where it is read, and has to be (#2906).
            let computed, read =
                match result with
                | AVar var when var.Kind = Temp ->
                    let held = freshLocal "__block_result" (CoreIrLayout.valueType var.Type)
                    [ atoms [ result ]; $"local.set ${held}" ], $"local.get ${held}"
                | _ -> [], atoms [ result ]
            let promote =
                if CoreIrRegions.canHoldMutable (atomType result) then
                    [ $"global.get {WasmRegions.innermostGlobal}"
                      touching $"i32.load offset={WasmRegions.ownerOffset}"
                      $"local.tee ${outer}"
                      "if"
                      read
                      $"local.get ${outer}"
                      $"call ${CoreIrRegions.promoteName (atomType result)}"
                      "end" ]
                else []
            join "\n    " [ yield! computed; yield! promote; $"call ${helper name}"; "i32.const 0" ]
        // Whether the reference the intrinsic takes over is the only one, read from the count before it is given up. An
        // immortal object's count is 0, so it is never held exclusively.
        | CallRuntime(name, [ value ], _) when name = Intrinsics.isExclusive ->
            let ty = atomType value
            match IrType.unfold ty with
            | IrList _
            | IrTags _ -> internalError $"'{name}' on '{IrType.show ty}', which is not always on the heap"
            | _ when not (CoreIrDrop.isHeapType ty) -> internalError $"'{name}' on '{IrType.show ty}', which is not on the heap"
            | _ -> ()
            let held = freshLocal "__exclusive" "i32"
            let result = freshLocal "__is_exclusive" "i32"
            join "\n    " [
                atoms [ value ]
                $"local.set ${held}"
                $"local.get ${held}\n    i32.const {CoreIrLayout.headerSize ty}\n    i32.sub"
                touching "i32.load"
                "i32.const 1\n    i32.eq"
                $"local.set ${result}"
                $"local.get ${held}\n    {dropValue ty}"
                $"local.get ${result}" ]
        | CallImport(declared, args) ->
            // Each argument is the value type the import takes, and so is what it returns; an import that returns
            // nothing leaves unit, 0.
            if args.Length <> declared.Parameters.Length then
                internalError $"a call to the import {declared.Module}.{declared.Name} with {args.Length} arguments for {declared.Parameters.Length} parameters"
            let result = if declared.Result.IsNone then "i32.const 0" else ""
            join "\n    " [ atoms args; $"call ${importName declared}"; result ]
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
        | CallClosure(closure, args, entry) ->
            // The closure is passed first, as the environment, and read again for the table slot it holds: the
            // borrowing entry's, with the owning entry in the slot after it (see `CoreIrClosureEntries`).
            let read = $"local.get ${localName closure}"
            let parameters = "i32" :: (args |> List.map (atomType >> CoreIrLayout.valueType))
            let result = CoreIrLayout.valueType expr.Type
            context.IndirectCall(parameters, result)
            let call = if tail then "return_call" else "call"
            let slot =
                match entry with
                | Borrowing -> touching "i32.load"
                | Owning -> join "\n    " [ touching "i32.load"; "i32.const 1"; "i32.add" ]
            join "\n    " [ read; atomsAfter true args; read; slot; $"{call}_indirect (type ${indirectTypeName parameters result})" ]
        | Interpolate parts -> this.Interpolation parts
        | Dbg(site, text) ->
            let prefix = this.Atom deferred onStack (AString site.Prefix)
            if context.DbgToRuntime then
                // The runtime is given it first, and the program writes it itself if the runtime leaves it to it.
                let held = freshLocal "__dbg_text" "i32"
                join "\n    "
                    [ this.Atom deferred onStack (AString site.File)
                      $"i32.const {site.Line}"
                      $"i32.const {site.Column}"
                      atomsAfter true [ text ]
                      $"local.tee ${held}"
                      $"call ${dbgImport}"
                      "i32.eqz"
                      "if"
                      prefix
                      $"local.get ${held}"
                      "call $dbg_str"
                      "end"
                      "i32.const 0" ]
            else join "\n    " [ prefix; atomsAfter true [ text ]; "call $dbg_str"; "i32.const 0" ]
        | If(cond, ifTrue, ifFalse) ->
            // A state machine resuming in a branch takes that branch.
            let condCode = if holds expr then choosing (atoms [ cond ]) (rangeOf ifTrue) else atoms [ cond ]
            let result = CoreIrLayout.valueType expr.Type
            $"{condCode}\n    (if (result {result})\n      (then\n        {this.SequenceAs tail expr.Type ifTrue}\n      )\n      (else\n        {this.SequenceAs tail expr.Type ifFalse}\n      )\n    )"
        | Match(scrutinees, arms) -> this.Arms tail expr.Type scrutinees arms
        // What counting does before a body a state machine resumes in has been done already.
        | Dup(vars, body) -> join "\n    " [ this.Resumed body (this.DupVars vars); this.Value tail deferred onStack body ]
        | Drop(vars, body) -> join "\n    " [ this.Resumed body (dropVars localOf vars); this.Value tail deferred onStack body ]
        | Release(released, body) -> join "\n    " [ this.Resumed body (this.Release released); this.Value tail deferred onStack body ]
        | FreeToken(token, body) -> join "\n    " [ this.Resumed body (this.FreeToken token); this.Value tail deferred onStack body ]
        // An allocation into the block a reuse token holds, if it holds one (see `CoreIrReuse`).
        | Reuse(token, { Kind = CallRuntime(name, ([ head; _ ] as args), _) }) when Helpers.reusingVariant name = Some Helpers.listConsAt ->
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
            // A temporary a state machine keeps in a local is there already.
            for var in vars do
                if var.Kind = Temp && not (storedTemps.ContainsKey var.Id) then
                    droppedTemps.[var.Id] <- freshLocal "__owned" (CoreIrLayout.valueType var.Type)
            join "\n    " [ this.Value false deferred onStack body; dropVars localOf vars ]
        | Let _
        | Do _ -> this.Sequence tail expr

    /// `code`, the code that comes before `body`, which leaves the stack as it found it: skipped when a state machine
    /// resumes in `body`.
    member _.Resumed (body: Expr) (code: string) = if holds body then unlessResuming None code else code

    /// The code of the array intrinsic `name` (one of `arrayIntrinsics`) on `args`, whose code `atoms` gives, for a value
    /// of type `ty` (see `CoreIrArrays`).
    member _.ArrayIntrinsic (atoms: Atom list -> string) (ty: IrType) (name: string) (args: Atom list) : string =
        // A mutable array is given as its box, whose array is read with no reference of its own: the box lives until the
        // intrinsic is done (see `CoreIrRegions.mutableArray`).
        let arrayTy (array: Atom) = CoreIrRegions.arrayIn (atomType array)
        let arrayAt (array: Atom) =
            match atomType array with
            | CoreIrRegions.RegionBox held ->
                let fields = match CoreIrRegions.boxType held with IrRecord fields -> fields | _ -> []
                join "\n    " [ atoms [ array ]; load held (CoreIrLayout.fieldOffset fields CoreIrRegions.boxValueSlot) ]
            | _ -> atoms [ array ]
        match args with
        // An array's length is the first word of its block, and its element at an index, which the standard library has
        // checked is in range, is after it, with a reference of its own taken (see `CoreIrArrays`).
        | [ array ] when name = Intrinsics.arrayLength ->
            join "\n    " [ arrayAt array; touching $"i32.load offset={CoreIrLayout.arrayLengthOffset}"; "i64.extend_i32_u" ]
        // A mutable array's element at an index, which the standard library has checked is in range, is given up and
        // the value, which the array takes over, is stored in its place, as `SetField` does. A new array's element is
        // written the same way, with nothing to give up, since it was never written.
        | [ array; index; value ] when name = Intrinsics.marraySet || name = Intrinsics.arrayWrite ->
            let element = arrayElement (arrayTy array)
            let offset = CoreIrLayout.arrayElementOffset element 0
            let slot = freshLocal "__slot" "i32"
            let held = freshLocal "__set" (CoreIrLayout.valueType element)
            let dropOld =
                if name = Intrinsics.marraySet && CoreIrDrop.isHeapType element then $"local.get ${slot}\n    {load element offset}\n    {dropValue element}"
                else ""
            join "\n    " [
                arrayAt array
                atoms [ index ]
                "i32.wrap_i64"
                $"i32.const {CoreIrLayout.size element}"
                "i32.mul"
                "i32.add"
                $"local.set ${slot}"
                atoms [ value ]
                $"local.set ${held}"
                dropOld
                $"local.get ${slot}\n    local.get ${held}\n    {store element offset}"
                "i32.const 0" ]
        // A new array: a block of its length, none for a length of 0 or less, then room for its elements, which the
        // standard library writes before anything reads them.
        | [ count ] when name = Intrinsics.arrayNew ->
            let element = arrayElement ty
            let requested = freshLocal "__count" "i64"
            let length = freshLocal "__length" "i32"
            let array = freshLocal "__array" "i32"
            join "\n    " [
                atoms [ count ]
                $"local.tee ${requested}"
                "i64.const 0"
                $"local.get ${requested}\n    i64.const 0\n    i64.gt_s"
                "select"
                "i32.wrap_i64"
                $"local.tee ${length}"
                $"i32.const {CoreIrLayout.size element}\n    i32.mul"
                $"i32.const {CoreIrLayout.arrayElementOffset element 0}\n    i32.add"
                $"call ${helper Helpers.newCell}"
                $"local.tee ${array}"
                $"local.get ${length}"
                touching $"i32.store offset={CoreIrLayout.arrayLengthOffset}"
                $"local.get ${array}" ]
        | [ array; index ] when name = Intrinsics.arrayGet ->
            let element = arrayElement (arrayTy array)
            join "\n    " [
                arrayAt array
                atoms [ index ]
                "i32.wrap_i64"
                $"i32.const {CoreIrLayout.size element}"
                "i32.mul"
                "i32.add"
                load element (CoreIrLayout.arrayElementOffset element 0)
                (if CoreIrDrop.isHeapType element then $"call ${CoreIrDrop.dupName element}" else "") ]
        | [ list ] when name = Intrinsics.arrayOfList ->
            join "\n    " [ atoms [ list ]; $"call ${CoreIrArrays.ofListName (arrayElement ty)}" ]
        | [ array ] when name = Intrinsics.arrayToList ->
            join "\n    " [ arrayAt array; $"call ${CoreIrArrays.toListName (arrayElement (arrayTy array))}" ]
        | _ -> internalError $"the array intrinsic '{name}' with {args.Length} arguments"

    /// The code that copies a spread array's elements, in `source`, into a new one, in `target`, from the index `at`: a loop
    /// over the two blocks in step, each element with a reference of its own. `atoms` gives the atoms' code.
    member _.CopyArrayInto (atoms: Atom list -> string) (target: Atom) (at: Atom) (source: Atom) : string =
        let element = arrayElement (atomType source)
        let size = CoreIrLayout.size element
        let first = CoreIrLayout.arrayElementOffset element 0
        let into = freshLocal "__into" "i32"
        let from = freshLocal "__from" "i32"
        let left = freshLocal "__left" "i32"
        join "\n    " [
            atoms [ target; at ]
            $"i32.wrap_i64\n    i32.const {size}\n    i32.mul\n    i32.add"
            $"local.set ${into}"
            atoms [ source ]
            $"local.tee ${from}"
            touching $"i32.load offset={CoreIrLayout.arrayLengthOffset}"
            $"local.set ${left}"
            $"block ${into}_done"
            $"loop ${into}_copy"
            $"local.get ${left}\n    i32.eqz\n    br_if ${into}_done"
            $"local.get ${into}\n    local.get ${from}\n    {load element first}"
            (if CoreIrDrop.isHeapType element then $"call ${CoreIrDrop.dupName element}" else "")
            store element first
            $"local.get ${into}\n    i32.const {size}\n    i32.add\n    local.set ${into}"
            $"local.get ${from}\n    i32.const {size}\n    i32.add\n    local.set ${from}"
            $"local.get ${left}\n    i32.const 1\n    i32.sub\n    local.set ${left}"
            $"br ${into}_copy"
            "end"
            "end"
            "i32.const 0" ]

    /// The code of `record with` the `updates`, each a field's slot in `fields` and its new value, written by `atomsAfter`
    /// and stored by the helpers `storing` names. The update takes the record over. When it holds the only reference, it
    /// updates the record in place: it drops the old value of each field on the heap it replaces and gives back the same
    /// pointer. Otherwise it copies the record, layout and all, taking a reference to each field on the heap it keeps,
    /// and gives up its reference to the original, which `release` checks and does in one. The new values are computed
    /// first, since they may be read from the fields they replace.
    member _.UpdateRecord
        (record: Atom)
        (fields: (string * IrType) list)
        (updates: (int * Atom) list)
        (atomsAfter: bool -> Atom list -> string)
        (storing: string -> IrType -> string)
        : string =
        let held = freshLocal "__update" "i32"
        let updated =
            updates |> List.map (fun (slot, value) -> slot, value, freshLocal "__updated" (CoreIrLayout.valueType (atomType value)))
        let isUpdated slot = updates |> List.exists (fst >> (=) slot)
        // `code` on the value of each field on the heap that `keep` picks, read from the original record.
        let eachField (keep: int -> bool) (code: IrType -> string) =
            fields
            |> List.indexed
            |> List.filter (fun (slot, (_, fieldTy)) -> CoreIrDrop.isHeapType fieldTy && keep slot)
            |> List.map (fun (slot, (_, fieldTy)) ->
                $"local.get ${held}\n      {load fieldTy (CoreIrLayout.fieldOffset fields slot)}\n      {code fieldTy}")
        let inPlace =
            eachField isUpdated dropValue
            @ (if context.CheckMemory then [ WasmAllocator.countReuse "" |> String.concat "\n      " ] else [])
            @ [ $"local.get ${held}" ]
        let copied =
            $"local.get ${held}\n      i32.const {CoreIrLayout.recordSize fields}\n      call ${helper Helpers.recordCopy}"
            :: eachField (isUpdated >> not) (fun fieldTy -> $"call ${CoreIrDrop.dupName fieldTy}\n      drop")
        join "\n    " [
            // The record and the new values, in the order they are evaluated, are set into their locals from the last.
            atomsAfter false (record :: List.map snd updates)
            yield! updated |> List.rev |> List.map (fun (_, _, local) -> $"local.set ${local}")
            $"local.set ${held}"
            $"local.get ${held}\n    i32.const {CoreIrLayout.headerSize (atomType record)}\n    i32.sub\n    call ${helper Helpers.release}"
            "if (result i32)"
            "  " + String.concat "\n      " inPlace
            "else"
            "  " + String.concat "\n      " copied
            "end"
            yield!
                updated
                |> List.map (fun (slot, value, local) ->
                    $"local.get ${local}\n    i32.const {CoreIrLayout.fieldOffset fields slot}\n    call ${storing Helpers.recordSet (atomType value)}") ]

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
            | None -> internalError $"no instruction for the operator `{op}` on '{IrType.show sized}'"
        join "\n    " [ operandCode; instruction; conversion (if arithmetic.Contains op then sized else IrBool) ty ]

    member this.Atom (deferred: Dictionary<int, Expr>) (onStack: HashSet<int>) (atom: Atom) : string =
        match atom with
        | AVar v when v.Kind = Temp && storedTemps.ContainsKey v.Id -> $"local.get ${storedTemps.[v.Id]}"
        | AVar v when v.Kind = Temp ->
            match deferred.TryGetValue v.Id with
            | true, value ->
                deferred.Remove v.Id |> ignore
                // The value's code has markers of its own, so what follows it, the code of the value it is an operand
                // of, is marked again with that value's place.
                let code = this.Value false deferred onStack value
                let code = match currentMarker with Some marker -> $"{code}\n    {marker}" | None -> code
                match droppedTemps.TryGetValue v.Id with
                | true, local -> $"{code}\n    local.tee ${local}"
                | _ -> code
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
            | OverStore(store, walk) -> atomCode store :: CoreIrEmitStoreWalk.start touching stream walk
        // A range counts in ints; a list's cursor moves to each cell's tail, after its head.
        let next (stream: ForStream) =
            match stream.Source with
            | OverRange _ -> [ $"local.get ${localName stream.Element}"; $"local.get ${localName stream.End}"; "i64.gt_s"; done' ]
            | OverRangeFrom _ -> []
            | OverList _ ->
                let cursor = localName stream.Cursor
                [ $"local.get ${cursor}"; "i32.eqz"; done'; $"local.get ${cursor}"; load stream.Element.Type 0; $"local.set ${localName stream.Element}" ]
            | OverStore(_, walk) -> CoreIrEmitStoreWalk.next touching depth done' stream walk
        let advance (stream: ForStream) =
            match stream.Source with
            | OverRange _
            | OverRangeFrom _ ->
                let element = localName stream.Element
                [ $"local.get ${element}"; "i64.const 1"; $"call ${helper Helpers.intAdd}"; $"local.set ${element}" ]
            | OverList _ ->
                let cursor = localName stream.Cursor
                [ $"local.get ${cursor}"; touching $"i32.load offset={CoreIrLayout.tailOffset stream.Element.Type}"; $"local.set ${cursor}" ]
            | OverStore(_, walk) -> CoreIrEmitStoreWalk.advance touching stream walk
        let bodyCode = this.Sequence false loop.Body
        let bodyDrop = dropValue loop.Body.Type
        // The starting atoms are read in the order `tempsUsedBy` gives them. A state machine resuming in the body has
        // started the loop and taken the element already.
        let startCode = loop.Streams |> List.collect start |> String.concat "\n    " |> this.Resumed loop.Body
        let nextCode = loop.Streams |> List.collect next |> String.concat "\n        " |> this.Resumed loop.Body
        let inLoop =
            [ nextCode; bodyCode; bodyDrop ] @ List.collect advance loop.Streams @ [ $"br $__for_next_{depth}" ]
            |> String.concat "\n        "
        $"{startCode}\n    block $__for_done_{depth}\n      loop $__for_next_{depth}\n        {inLoop}\n      end\n    end\n    i32.const 0"

    /// A `while` loop: each time round, the condition ends the loop when it is false, and the body's value is dropped.
    /// The loop is unit.
    member this.WhileLoop (loop: WhileLoop) : string =
        let depth = loop.Depth
        // A state machine resuming in the body carries on round the loop it was in.
        let condition = this.Sequence false loop.Condition
        let condition = if holds loop.Body then choosing condition (rangeOf loop.Body) else condition
        let inLoop =
            [ condition
              "i32.eqz"
              $"br_if $__while_done_{depth}"
              this.Sequence false loop.Body
              dropValue loop.Body.Type
              $"br $__while_next_{depth}" ]
            |> String.concat "\n        "
        $"block $__while_done_{depth}\n      loop $__while_next_{depth}\n        {inLoop}\n      end\n    end\n    i32.const 0"

    /// Tests each arm's patterns in turn: a chain of `if`s ending in `unreachable`.
    member this.Arms (tail: bool) (resultTy: IrType) (scrutinees: Var list) (arms: Arm list) : string =
        // The code of each scrutinee of a union with payloads that two or more arms test a tag of, read once into a
        // local before the first arm, by the code that reads the scrutinee (#2309).
        let codeLocals = Dictionary<string, string>()
        let rec testsTag (pattern: Pattern) =
            match pattern with
            | PTag _ -> true
            | PAll parts -> parts |> List.exists testsTag
            | _ -> false
        let readCodes =
            [ for index, scrutinee in List.indexed scrutinees do
                  let testing = arms |> List.filter (fun arm -> testsTag arm.Patterns.[index]) |> List.length
                  if testing >= 2 && not (CoreIrDrop.tagIsCode scrutinee.Type) then
                      let local = freshLocal "__code" "i32"
                      codeLocals.[$"local.get ${localName scrutinee}"] <- local
                      $"local.get ${localName scrutinee}\n    call ${helper Helpers.tagCode}\n    local.set ${local}" ]
        // The code of the tag the value `valueCode` leaves, of type `ty`, is: a value of a union with no payloads is its
        // own code.
        let tagCodeOf (valueCode: string) (ty: IrType) =
            match codeLocals.TryGetValue valueCode with
            | true, local -> $"local.get ${local}"
            | _ when CoreIrDrop.tagIsCode ty -> valueCode
            | _ -> $"{valueCode}\n      call ${helper Helpers.tagCode}"
        // The payload of the tag `name` of a value of `ty`, with its type.
        let tagPayloadCode (ty: IrType) (name: string) valueCode =
            let payloadTy =
                match IrType.unfold ty with
                | IrTags tags -> tags |> List.tryPick (fun (tag, payload) -> if tag = name then payload else None)
                | _ -> None
            match payloadTy with
            | Some payloadTy -> $"{valueCode}\n      {load payloadTy CoreIrLayout.payloadOffset}", payloadTy
            | None -> internalError $"a pattern on the payload of `#{name}`, which '{IrType.show ty}' does not give one"
        // The field in `slot` of a record of `ty`, with its type.
        let fieldCode (ty: IrType) valueCode slot =
            match IrType.unfold ty with
            | IrRecord fields -> $"{valueCode}\n      {load (snd fields.[slot]) (CoreIrLayout.fieldOffset fields slot)}", snd fields.[slot]
            | other -> internalError $"a pattern on the fields of '{IrType.show other}', which is not a record"
        let elementOf (ty: IrType) =
            match IrType.unfold ty with
            | IrList element -> element
            | other -> internalError $"a list pattern on '{IrType.show other}'"
        // The list after `count` cells, and the head of the list after `index` cells, of a list of `ty`.
        let listTailCode (ty: IrType) valueCode count =
            if count = 0 then valueCode
            else
                let tail = touching $"i32.load offset={CoreIrLayout.tailOffset (elementOf ty)}"
                valueCode + String.replicate count $"\n      {tail}"
        let listElementCode (ty: IrType) valueCode index =
            $"{listTailCode ty valueCode index}\n      {load (elementOf ty) 0}"
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
            | PFloat value ->
                let held = CoreIrLayout.valueType ty
                join "\n      " [ valueCode; $"{held}.const {wasmFloatConst value}"; $"{held}.eq" ]
            | PBool value ->
                let flag = if value then 1 else 0
                $"{valueCode}\n      i32.const {flag}\n      i32.eq"
            | PTag(name, payload) ->
                let discriminant =
                    $"{tagCodeOf valueCode ty}\n      i32.const {tagCode (context.TagId name)}\n      i32.eq"
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
                // A state machine resuming in an arm takes it, and what it binds is restored already.
                let allConditions =
                    if arms |> List.exists (fun arm -> holds arm.Body) then choosing (conjunction conditions) (rangeOf arm.Body)
                    else conjunction conditions
                let bindCode =
                    List.map2 (fun pattern (scrutinee: Var) -> binds pattern $"local.get ${localName scrutinee}" scrutinee.Type) arm.Patterns scrutinees
                    |> List.concat
                    |> String.concat "\n    "
                    |> this.Resumed arm.Body
                let armCode = join "\n    " [ bindCode; this.SequenceAs tail resultTy arm.Body ]
                let result = CoreIrLayout.valueType resultTy
                $"{allConditions}\n    (if (result {result})\n      (then\n        {armCode}\n      )\n      (else\n        {pieces.Add(emit rest)}\n      )\n    )"
        join "\n    " [ yield! readCodes; emit arms ]

/// `code` without each place marker that says what the one before it said (see `placeMarker`).
let private withoutRepeatedMarkers (code: string) =
    let mutable last = ""
    code.Split('\n')
    |> Array.filter (fun line ->
        let trimmed = line.Trim()
        if not (trimmed.StartsWith ";;@") then true
        elif trimmed = last then false
        else
            last <- trimmed
            true)
    |> String.concat "\n"

/// `func` as a WebAssembly function, exported under `exportName` if given.
let emitFunction (context: EmitContext) (func: Function) (exportName: string option) : Emitted =
    let emitter = FunctionEmitter(context)
    emitter.Declared <- (func.Params |> List.map localName) @ (declaredLocals func.Body |> List.map fst) |> Set.ofList
    // A state machine can stop at its split points, and has a resume entry that carries on (see `TaskLowering`).
    let found =
        if context.StateMachines.Contains func.Name then CoreIrSplitPoints.plan context.StateMachines context.YieldAtEveryWait func else None
    found |> Option.iter (fun found -> emitter.Machine(found, func.Name, func.Result))
    let bodyCode = emitter.SequenceAs true func.Result func.Body
    let defined =
        match func.Place, context.ShownFile with
        | Some place, Some shownFile -> [ definitionMarker shownFile place ]
        | _ -> []
    let bodyCode = defined @ emitter.ProbingCopy @ emitter.Prologue @ [ emitter.Expand bodyCode ] |> String.concat "\n    " |> withoutRepeatedMarkers
    let header =
        [ func.Params |> List.map (fun p -> $"(param ${localName p} {CoreIrLayout.valueType p.Type})") |> String.concat " "
          $"(result {CoreIrLayout.valueType func.Result})"
          localDeclarations (declaredLocals func.Body @ emitter.ExtraLocals) ]
        |> join " "
    let definition = $"  (func ${func.Name} {header}\n    {bodyCode}\n  )"
    let text =
        match exportName with
        | Some exported -> definition + "\n" + $"""  (export "{exported}" (func ${func.Name}))"""
        | None -> definition
    let resume =
        match found with
        | Some _ -> [ TaskLowering.resumeEntry func.Name (func.Params |> List.map (_.Type >> CoreIrLayout.valueType)) (CoreIrLayout.valueType func.Result) ]
        | None -> []
    // A state machine's drop entry frees a frame of it without resuming it.
    let drop =
        match found with
        | Some _ when context.FrameDrops ->
            Some(TaskLowering.dropEntry func.Name (emitter.Held (context.BorrowedIn func) (CoreIrCounting.reuseTokens func)))
        | _ -> None
    { Text = String.concat "\n" (text :: resume @ Option.toList drop)
      Helpers = emitter.Helpers
      UsesMemory = emitter.UsesMemory
      FrameDrop = drop |> Option.map (fun _ -> context.FunctionSlot (TaskLowering.Names.resumeEntry func.Name) 1, TaskLowering.Names.dropEntry func.Name) }

/// The start function, `func` (see `CoreIrLower.StartFunction`): the code `before`, then the top-level expressions, with
/// what was inlined into them, each value dropped, then the code `after`. It takes nothing and returns nothing, and
/// makes no tail call.
let emitStart (context: EmitContext) (func: Function) (before: string list) (after: string list) : Emitted =
    let emitter = FunctionEmitter(context)
    emitter.Declared <- declaredLocals func.Body |> List.map fst |> Set.ofList
    let body = emitter.Expand $"{emitter.Sequence false func.Body}\n    {dropValue func.Body.Type}"
    let bodyCode = before @ emitter.ProbingCopy @ body :: after |> String.concat "\n    " |> withoutRepeatedMarkers
    let header =
        match localDeclarations (declaredLocals func.Body @ emitter.ExtraLocals) with
        | "" -> ""
        | locals -> " " + locals
    { Text = $"  (func ${func.Name}{header}\n    {bodyCode}\n  )"; Helpers = emitter.Helpers; UsesMemory = emitter.UsesMemory; FrameDrop = None }
