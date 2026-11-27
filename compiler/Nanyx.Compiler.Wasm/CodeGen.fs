module Transpiler.Wasm.CodeGen

open System
open System.Text
open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CodeGenContexts

/// Why the backend stops: what `tryTranspileTypedModuleToWat` reports.
exception BackendFailure of Diagnostic

/// What a broken invariant of the backend (see `CoreIr.InternalError`) says to the user: the one place it gets its
/// prefix, and asks for a bug report.
let private internalErrorMessage (reason: string) =
    $"The compiler broke one of its own rules ({reason}): report it at https://github.com/nanyx-lang/nanyx/issues"

/// A broken invariant of the backend, at `span` if it is known.
let private internalErrorDiagnostic (reason: string) (span: SourceSpan option) =
    match span with
    | Some span -> Diagnostics.at DiagnosticCodes.BackendInternalError span (internalErrorMessage reason)
    | None -> Diagnostics.general DiagnosticCodes.BackendInternalError (internalErrorMessage reason)

/// What a function the core IR could not lower or finish is reported as: a feature the backend does not compile yet,
/// or a broken invariant, at `span` if it is known.
let lowerFailureDiagnostic (failure: CoreIrLowerInput.LowerFailure) (span: SourceSpan option) : Diagnostic =
    match failure, span with
    | CoreIrLowerInput.Internal reason, _ -> internalErrorDiagnostic reason span
    | CoreIrLowerInput.NotYet reason, Some span ->
        Diagnostics.at DiagnosticCodes.UnsupportedByBackend span $"It can't compile {reason} yet"
    | CoreIrLowerInput.NotYet reason, None ->
        Diagnostics.general DiagnosticCodes.UnsupportedByBackend $"It can't compile {reason} yet"

type private ContextFn = string * int * int

/// The module's function table (`$ctx_table`), as far as functions used as values are concerned: a top-level
/// function referenced without being called, or a lifted lambda, gets a slot here and is called through
/// `call_indirect`. Shared by every function of a module and read when the table is rendered.
type private FunctionTable =
    { /// Function name -> (WebAssembly name, slot, arity).
      Values: System.Collections.Generic.Dictionary<string, ContextFn>
      /// The next free slot.
      mutable NextSlot: int
      /// The parameters and result of every indirect call made through a local, so a type exists for each (see
      /// `Wat.indirectTypeName`).
      IndirectSignatures: System.Collections.Generic.HashSet<string list * string> }

/// The record layouts the module allocates, each numbered as the functions are emitted. Every record carries its
/// layout's number.
type private RecordTable =
    { /// Field names in storage order -> the layout's number.
      LayoutIds: System.Collections.Generic.Dictionary<string list, int> }

/// How a module is compiled.
type WasmOptions =
    { /// Check the heap as the program runs: the allocator counts the blocks it hands out and takes back, and stops
      /// the program on a block freed twice or an object counted after it was freed, with a message on stderr and
      /// `CheckedExports.failureExitCode` (see `WasmRuntime.MemoryChecks`). The module exports its start function
      /// rather than running it when instantiated, and exports the counters and `$heap_ptr`, so that a host can see
      /// what was left on the heap once the program has run (see `CheckedExports`).
      CheckMemory: bool
      /// Inline calls in the core IR (see `CoreIrInline`). Off only for working on the compiler (`--no-inline`): it
      /// tells whether a misbehaving program is the inliner's doing.
      Inline: bool
      /// A release build (`--release`): `dbg(x)` compiles to `x`.
      Release: bool
      /// The root of the package the module is in, which a debug build's `dbg` names its file from (see
      /// `CoreIrLowerInput.DbgBuild`); none for a script, whose `dbg` names its file alone.
      SourceRoot: string option
      /// Mark the code with the places in the source it was made from (see `CoreIrEmit.placeMarker`), which `WasmBinary`
      /// writes into the module's table of places, so a crash's stack names the file and line of each frame. Debug and
      /// release builds both have it: the command line turns it on for every program it writes or runs. Off in
      /// `Default`, so that the text a test reads is the code alone.
      Places: bool
      /// Put debug probes in the code (see `CoreIrProbes`), which import `nanyx:debug` from the runtime: what a debug
      /// build the runtime runs has, and the debugger is built on. A release build has none, and nor does a module written
      /// to a file, which another WebAssembly runtime may run. They need `Places`, to say where each probe is. A `dbg`
      /// in such a module gives what it prints to the runtime's `nanyx:debug` `dbg` too (see `EmitContext.DbgToRuntime`).
      Probes: bool
      /// Report a crash's message to the runtime's `nanyx:crash` `report` before the trap that ends the program (see
      /// `CrashReport`), so that the runtime has it as data: what the command line sets for the programs it runs itself.
      /// A module written to a file, a component, or a page's has no such import, and writes the message to stderr alone,
      /// as every module does.
      CrashToRuntime: bool
      /// Give what a `dbg` prints to the embedder's `nanyx:debug` `dbg` first, as a module with `Probes` does, but with
      /// no probes: what a page that runs the program imports, so that `dbg` is kept apart from what the program writes
      /// to stderr (see `EmitContext.DbgToRuntime`).
      DbgToEmbedder: bool
      /// Export the start function, as `CheckedExports.start`, rather than running it when the module is instantiated, so
      /// that the embedder can reach the module's memory before the program runs, and calls it itself: what a page does,
      /// whose imports read the text the program writes from the memory the module exports. `CheckMemory` does it too.
      ExportStart: bool
      /// Write the core module of a WASI 0.3 component with this entry (see `ComponentAbi`): the program run by the
      /// entry's export rather than when the module is instantiated, `cabi_realloc` exported, and the runtime's own
      /// writes and exits on `wasi:cli` rather than WASI preview 1. Not with `CheckMemory`, `Probes` or `DbgToEmbedder`,
      /// whose imports and exports no WASI world has.
      Component: ComponentEntry option
      /// The functions a component's core module exports for the program's definitions marked `@component`, each with
      /// the name it exports it under (see `ComponentExports`). Only with `Component`.
      ComponentExports: (string * ComponentFunction) list
      /// Make each call of a function value that can wait stop once and be resumed before it is made (see
      /// `TaskLowering`), so that a test runs every state machine's way of stopping and carrying on.
      YieldAtEveryWait: bool
      /// Give each state machine a drop entry beside its resume entry, and the module the helper
      /// `TaskLowering.Names.frameDrop`, which frees a stopped frame and every frame it stopped in, with what each keeps,
      /// without resuming them (#2426).
      /// Nothing in the language drops a frame yet, so a program has none; a test turns it on.
      FrameDrops: bool }

    static member Default =
        { CheckMemory = false
          Inline = true
          Release = false
          SourceRoot = None
          Places = false
          Probes = false
          CrashToRuntime = false
          DbgToEmbedder = false
          ExportStart = false
          Component = None
          ComponentExports = []
          YieldAtEveryWait = false
          FrameDrops = false }

/// What a module the runtime runs reports a crash through (see `WasmOptions.CrashToRuntime`): the runtime's `nanyx:crash`
/// `report`, given the address and the length of the message's bytes, which `Helpers.crashed` calls before it traps.
module CrashReport =
    let report: Extern =
        { Module = "nanyx:crash"; Name = "report"; Parameters = [ ExternValue.I32; ExternValue.I32 ]; Result = None }

    /// `Helpers.crashed`, reporting the message.
    let crashed =
        let report = CoreIrEmitImports.importName report
        [ $"  (func ${Helpers.crashed} (param $message i32)"
          "    local.get $message"
          "    local.get $message"
          $"    call ${Helpers.stringLength}"
          $"    call ${report}"
          "    unreachable"
          "  )" ]
        |> String.concat "\n"

/// The names a module compiled with `CheckMemory` exports.
module CheckedExports =
    /// The start function, which runs the program's top-level code.
    let start = CoreIrLowerInput.startFunctionName
    /// How many blocks the program allocated and has not freed.
    let liveBlocks = MemoryChecks.liveBlocks
    /// How many blocks the program allocated.
    let allocations = MemoryChecks.allocations
    /// How many bytes the blocks the program allocated took, all told: an `i64`.
    let bytesAllocated = MemoryChecks.bytesAllocated
    /// The most bytes the program's blocks took at once.
    let peakLiveBytes = MemoryChecks.peakLiveBytes
    /// The highest the top of the heap has been, as an address.
    let peakHeap = MemoryChecks.peakHeap
    /// How many objects the program built in the block of one it gave up, rather than in a new one.
    let reusedInPlace = MemoryChecks.reusedInPlace
    /// The counters a checked module exports, which `memoryCheckExports` declares.
    let counters = [ liveBlocks; allocations; bytesAllocated; peakLiveBytes; peakHeap; reusedInPlace ]
    /// The top of the heap now: blocks freed at the top give it back, so it can be lower than `peakHeap`.
    let heapPointer = "heap_ptr"
    /// The exit code a program stops with when a memory check fails.
    let failureExitCode = MemoryChecks.exitCode

/// The exports of the counters a checked module has (see `CheckedExports.counters`), each under its global's name.
let private memoryCheckExports =
    CheckedExports.counters
    |> List.map (fun name -> $"""  (export "{name}" (global ${name}))""")
    |> String.concat "\n"

let private ctxFnName (name: string, _, _) = name
let private ctxFnSlot (_, slot: int, _) = slot
let private ctxFnArity (_, _, arity: int) = arity

/// The function-table slot of a top-level function used as a value, assigning one on first use. A function in
/// `owningEntries` has the owning entry it names in the slot after it (see `CoreIrClosureEntries`).
let private functionValueSlot (table: FunctionTable) (owningEntries: Map<string, string>) (name: string) (arity: int) : int =
    match table.Values.TryGetValue name with
    | true, ctxFn -> ctxFnSlot ctxFn
    | _ ->
        let slot = table.NextSlot
        table.NextSlot <- slot + 1
        table.Values.[name] <- (sanitizeIdentifier name, slot, arity)
        match owningEntries |> Map.tryFind name with
        | Some owning ->
            table.NextSlot <- slot + 2
            table.Values.[CoreIrClosureEntries.owningName name] <- (sanitizeIdentifier owning, slot + 1, arity)
        | None -> ()
        slot


let private escapeWasmData (bytes: byte array) =
    let sb = StringBuilder()
    for b in bytes do
        let c = char b
        if b >= 0x20uy && b <= 0x7Euy && c <> '"' && c <> '\\' then
            sb.Append c |> ignore
        else
            sb.Append(sprintf "\\%02x" b) |> ignore
    sb.ToString()

/// Where each string literal is in the data segment, and the segment's bytes. A literal is laid out as a string on the
/// heap is (see `WasmRuntime.runtimeHelpers`), with a count of 0 so that it is never freed, and padded so that the
/// next one is 4-byte aligned. The heap starts after them (see `stringDataEnd`).
let private buildStringLiteralMap (literals: string list) =
    let mutable offset = stringDataBaseOffset
    let entries =
        literals
        |> List.map (fun value ->
            let bytes = Encoding.UTF8.GetBytes value
            let countBytes = BitConverter.GetBytes 0
            let lengthBytes = BitConverter.GetBytes bytes.Length
            let unpadded = countBytes.Length + lengthBytes.Length + bytes.Length + 1
            let padding = Array.zeroCreate ((4 - unpadded % 4) % 4)
            let dataBytes = Array.concat [ countBytes; lengthBytes; bytes; [| 0uy |]; padding ]
            let currentOffset = offset
            offset <- offset + dataBytes.Length
            value, currentOffset, dataBytes)
    let map = entries |> List.map (fun (value, baseOffset, _) -> value, baseOffset + Header.withInfo) |> Map.ofList
    map, entries

/// Where the string literals laid out as `entries` (see `buildStringLiteralMap`) end.
let private stringDataEnd (entries: (string * int * byte array) list) =
    entries |> List.fold (fun dataEnd (_, offset, bytes) -> max dataEnd (offset + bytes.Length)) stringDataBaseOffset

/// How many bytes a static closure (see `CoreIr.AStaticClosure`) takes in the data segment: its header, then its
/// function's table slot.
let private staticClosureBytes = 8 + CoreIrLayout.closureSize []

/// Where each of `closures` (by its function's name) is in the data segment, laid out from `start`, which is 4-byte
/// aligned.
let private staticClosureOffsets (closures: (string * int) list) (start: int) =
    closures |> List.mapi (fun index (name, _) -> name, start + index * staticClosureBytes + 8) |> Map.ofList

/// The data segment of the static closure at `offset` over the function in the table slot `slot`. It is laid out as a
/// closure on the heap is (see `CoreIrEmit`'s `MakeClosure`), with a count of 0 so that it is never freed, and an
/// info word of 0, since nothing ever drops it.
let private staticClosureData (offset: int) (slot: int) =
    let bytes = Array.concat [ BitConverter.GetBytes 0; BitConverter.GetBytes 0; BitConverter.GetBytes slot ]
    $"""  (data (i32.const {offset - 8}) "{escapeWasmData bytes}")"""

/// The data segments that lay out `entries` (see `buildStringLiteralMap`).
let private renderDataSegments (entries: (string * int * byte array) list) =
    entries
    |> List.map (fun (_, offset, bytes) -> $"""  (data (i32.const {offset}) "{escapeWasmData bytes}")""")
    |> String.concat "\n"

/// A module of just the allocator, exporting `alloc`, `free` and the memory, so that tests can drive it directly. With
/// `CheckMemory`, it is the checked allocator, and exports `retain`, `release` and the counters too; a failed check
/// writes to stderr and exits through WASI.
let allocatorModuleWat (options: WasmOptions) =
    let messageOffsets, messageData = buildStringLiteralMap MemoryChecks.messages
    let heapBase = heapBaseAfter (stringDataEnd messageData)
    let helpers = if options.CheckMemory then checkedRuntimeHelpers heapBase else runtimeHelpers heapBase
    [ yield "(module"
      if options.CheckMemory then
          yield """  (import "wasi_snapshot_preview1" "fd_write" (func $fd_write (param i32 i32 i32 i32) (result i32)))"""
          yield $"""  (import "wasi_snapshot_preview1" "proc_exit" (func ${Helpers.procExit} (param i32)))"""
      yield "  (memory 1)"
      yield """  (export "memory" (memory 0))"""
      yield $"  (global $heap_ptr (mut i32) (i32.const {heapStartOffset heapBase}))"
      yield helpers |> Map.find Helpers.alloc
      yield helpers |> Map.find Helpers.free
      yield $"""  (export "alloc" (func ${Helpers.alloc}))"""
      yield $"""  (export "free" (func ${Helpers.free}))"""
      if options.CheckMemory then
          yield renderDataSegments messageData
          yield memoryCheckFunctions heapBase (fun message -> messageOffsets |> Map.find message)
          for name in [ Helpers.retain; Helpers.release; Helpers.stringLength; Helpers.writeStderr ] do
              yield helpers |> Map.find name
          yield $"""  (export "retain" (func ${Helpers.retain}))"""
          yield $"""  (export "release" (func ${Helpers.release}))"""
          yield memoryCheckExports
      yield ")" ]
    |> String.concat "\n"

/// The number of the record layout with `fieldNames` (in storage order), numbering it if it is new.
let private recordLayoutId (table: RecordTable) (fieldNames: string list) =
    let layouts = table.LayoutIds
    match layouts.TryGetValue fieldNames with
    | true, id -> id
    | _ ->
        let id = layouts.Count + 1
        layouts.[fieldNames] <- id
        id

/// The functions a module was compiled into, from the core IR (see "Core IR" in `docs/src/content/deep-dives/memory-management.md`).
type BackendReport =
    { /// The functions, by their names in WebAssembly text, specialisations and the start function included.
      Functions: string list
      /// The specialisations among them, each with the definition it specialises.
      Specialisations: (string * string) list }

/// The foreign functions `typed` declares (see `ExternSyntax`), and its component imports (see `ComponentImports`), by
/// name, and its items without them: each is an import of the module, not a function it defines, or a function the
/// module defines for a component import. Two that import one function with different types are an error, which
/// the type checker reports only for two in one module.
let private foreignFunctions (typed: TypedModule) : Map<string, Extern> * TopLevelItem list =
    let imports = ComponentInterface.imports typed
    let declared =
        typed.Module
        |> List.choose (fun item ->
            match item.Kind with
            // A component import is a foreign function the module defines itself (see `ComponentImports`).
            | Def (ValueDef(_, name, _, ({ Kind = ComponentImportSyntax.Import _ } as value))) ->
                match imports |> List.tryFind (fun func -> func.Export.Definition = name) with
                | Some func -> Some (name, ComponentInterface.externOf func, value.Span)
                | None -> raise (BackendFailure (internalErrorDiagnostic $"the import '{name}' has a type that can't cross a component's boundary" (Some value.Span)))
            | Def (ValueDef(_, name, _, ({ Kind = ExternSyntax.Import(importModule, importName) } as value))) ->
                match typed.Types |> Map.tryFind name |> Option.bind Externs.signatureOfType with
                | Some (parameters, result) ->
                    Some (name, { Module = importModule; Name = importName; Parameters = parameters; Result = result }, value.Span)
                | None -> raise (BackendFailure (internalErrorDiagnostic $"the foreign function '{name}' has a type other than value types" (Some value.Span)))
            | _ -> None)
    match Externs.conflicts declared with
    | error :: _ -> raise (BackendFailure error)
    | [] ->
        let names = declared |> List.map (fun (name, _, _) -> name) |> Set.ofList
        let others =
            typed.Module
            |> List.filter (function
                | Kind (Def (ValueDef(_, name, _, _))) -> not (names.Contains name)
                | _ -> true)
        declared |> List.map (fun (name, declared, _) -> name, declared) |> Map.ofList, others

/// Whether a local function's value carries the members of `context`, one of the contexts it requires (see
/// `localContextsOf`): a parameterised context, or a context variable, which is the def around it's, as `c` in
/// `def go: [c] int -> int` inside a `[c]` def.
let private carried (context: Ty) =
    match context with
    | TyVar _ -> true
    | _ -> isInstance context

/// The local function `local` whose value is `lambda`, if it requires parameterised contexts or context variables,
/// written or inferred: its symbol, the contexts as the type checker writes them (`TypedModule.LocalContexts`), its
/// lambda's type, and the contexts in that type's variables. The type checker keeps them out of the lambda's type, so
/// they are put there for the backend: a function value carries their members.
let private localContextOf (typed: TypedModule) (checkedTypes: Map<NodeId, Ty>) (local: LambdaLifting.LocalDefinition) (lambda: Expression) =
    match checkedTypes |> Map.tryFind lambda.Id with
    | Some lambdaTy ->
        typed.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> symbol.Kind = LocalValue && symbol.Name = local.Name && SourceSpan.contains local.Statement symbol.Declaration)
        |> Seq.sortBy _.Declaration.Start
        |> Seq.tryHead
        |> Option.bind (fun symbol ->
            // A context the def writes is its alias, `Show(Point)` with its members; one inferred is applied.
            let applied (context: Ty) =
                match context with
                | TyAlias(name, args, _) -> TyApply(name, args)
                | other -> other
            match typed.LocalContexts |> Map.tryFind symbol.Id, typed.ContextTypes |> Map.tryFind symbol.Id |> Option.map (List.map applied), typed.SymbolTypes |> Map.tryFind symbol.Id with
            | Some written, Some contexts, Some symbolTy when written.Length = contexts.Length && List.exists carried contexts ->
                try
                    let bindings = CoreIrLowerTypes.bindScheme Map.empty symbolTy lambdaTy
                    Some (symbol.Id, written, lambdaTy, contexts |> List.map (CoreIrLowerTypes.substitute bindings))
                with CoreIrLowerTypes.CannotLower _ -> None
            | _ -> None)
    | None -> None

/// Each local function that requires parameterised contexts or context variables, by its lambda's node (see
/// `localContextOf`).
let private localContextsOf (typed: TypedModule) (checkedTypes: Map<NodeId, Ty>) (liftedOrigins: Map<string, LambdaLifting.LiftedOrigin>) =
    liftedOrigins
    |> Map.values
    |> Seq.choose (fun origin ->
        origin.Local
        |> Option.bind (fun local -> localContextOf typed checkedTypes local origin.Lambda)
        |> Option.map (fun found -> origin.Lambda.Id, found))
    |> Map.ofSeq

/// The other names a function can call the members of the contexts it requires by, each with the key it stands
/// for: a member of a parameterised context (`show` of `Show(a)`) by its own name (`show`), as the type checker
/// allows, when no other context the function declares has a member of that name.
let private contextMemberAliases
    (functionContextRequirements: Map<string, ContextMemberKey list>)
    (functionContextTypes: Map<string, TypeExpr list>)
    (typeLayouts: Map<string, string list>)
    (name: string)
    : (string * ContextMemberKey) list =
    let keys = functionContextRequirements |> Map.tryFind name |> Option.defaultValue []
    functionContextTypes
    |> Map.tryFind name
    |> Option.defaultValue []
    |> List.collect (fun ctxType ->
        match ctxType.Kind with
        | TypeApply(baseName, _) ->
            layoutOf typeLayouts baseName |> Option.defaultValue []
            |> List.map (fun memberName -> memberName, contextMemberKey (Some ctxType) memberName)
        | _ -> [])
    |> List.groupBy fst
    |> List.choose (fun (memberName, aliased) ->
        match aliased with
        | [ (_, key) ] when List.contains key keys && not (List.contains (NamedMember memberName) keys) -> Some (memberName, key)
        | _ -> None)

/// The members of the contexts `name` requires, as the core IR takes them: each with its type, from the context's
/// definition with its parameters as the function's annotation gives them, where that can be worked out.
let private coreIrContextParameters
    (defs: (string * TypeExpr option * Expression) list)
    (functionContextTypes: Map<string, TypeExpr list>)
    (typeLayouts: Map<string, string list>)
    (functionContextRequirements: Map<string, ContextMemberKey list>)
    (typeDefs: Map<string, TypeDefInfo>)
    (definitionTypes: Map<string, Ty>)
    (exprTypes: Map<NodeId, Ty>)
    (name: string) =
    // The function's own type variables, by the names its annotation gives them. A context's associated type may be
    // written only in the contexts, `elem` in `[Iter(c)] c -> int`; one its parameters or result write is that one.
    let rec namedVars (ty: Ty) : (string * Ty) list =
        match ty with
        | TyVar { Name = Some varName } -> [ varName, ty ]
        | TyFunc(input, output, contexts) -> List.collect namedVars contexts @ namedVars input @ namedVars output
        | TyContexts _ -> []
        | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> namedVars)
        | _ -> Ty.children ty |> List.collect namedVars
    let annotated =
        match defs |> List.tryFind (fun (defName, _, _) -> defName = name), definitionTypes |> Map.tryFind name with
        | Some (_, typeOpt, value), Some ty ->
            match unwrapContextType typeOpt value with
            | Some annotation -> matchAnnotation annotation ty
            | None -> []
        | _ -> []
    let argumentTy = writtenArgumentType (definitionTypes |> Map.tryFind name |> Option.map namedVars |> Option.defaultValue []) annotated
    // The variables its body's types name, by name.
    let bodyVars =
        lazy (
            match defs |> List.tryFind (fun (defName, _, _) -> defName = name) with
            | Some (_, _, value) -> SyntaxTree.expressionsWithin value |> List.choose (fun expr -> exprTypes |> Map.tryFind expr.Id) |> List.collect namedVars
            | None -> [])
    let contextTypes = functionContextTypes |> Map.tryFind name |> Option.defaultValue []
    // The names of the associated types its contexts write, which another of its contexts may name too, as
    // `ToString(elem)` does in `[Iterable(c), ToString(elem)] c -> ()`.
    let associatedNames =
        contextTypes
        |> List.collect (fun ctxType ->
            match ctxType.Kind with
            | TypeApply(baseName, arguments) ->
                match typeDefs |> Map.tryFind baseName with
                | Some info when info.Parameters.Length = arguments.Length ->
                    arguments
                    |> List.skip (arguments.Length - info.AssociatedTypes.Length)
                    |> List.choose (fun arg -> match arg.Kind with TypeVar varName -> Some varName | _ -> None)
                | _ -> []
            | _ -> [])
        |> Set.ofList
    let memberTypes =
        contextTypes
        |> List.collect (fun ctxType ->
            let baseName, arguments =
                match ctxType.Kind with
                | TypeApply(baseName, arguments) -> baseName, arguments
                | TypeName baseName -> baseName, []
                | _ -> "unknown", []
            let memberTy (memberName: string) =
                match typeDefs |> Map.tryFind baseName with
                | Some info when info.Parameters.Length = arguments.Length ->
                    // An associated type the function's own type does not write, `elem` in `[Iter(c)] c -> int`, whether
                    // in its own context or another, is the variable of its name in its body's types, which each call binds (see
                    // `TypedModule.AssociatedArguments`), or, where the body has none, held boxed, as a member's own type
                    // variables are (see `CoreIrLowerTypes.boxOwnVariables`).
                    let argumentTys =
                        arguments
                        |> List.mapi (fun index arg ->
                            match argumentTy arg, arg.Kind with
                            | None, TypeVar varName when index >= info.Parameters.Length - info.AssociatedTypes.Length || associatedNames.Contains varName ->
                                bodyVars.Value |> List.tryFind (fst >> (=) varName) |> Option.map snd |> Option.orElse (Some CoreIrLowerTypes.boxedTy)
                            | found, _ -> found)
                    if argumentTys |> List.forall Option.isSome then
                        let parameters = List.zip (info.Parameters |> List.map _.Id) (argumentTys |> List.map Option.get) |> Map.ofList
                        let rec bind (ty: Ty) =
                            match ty with
                            | TyVar v when parameters.ContainsKey v.Id -> parameters.[v.Id]
                            | TyFunc(input, output, contexts) -> TyFunc(bind input, bind output, contexts)
                            | TyRecord(fields, order) -> TyRecord(fields |> Map.map (fun _ -> bind), order)
                            | TyTag(tag, payload) -> TyTag(tag, payload |> Option.map bind)
                            | TyApply(applied, args) -> TyApply(applied, args |> List.map bind)
                            | TyUnion items -> TyUnion(items |> List.map bind)
                            | TyAlias(alias, args, underlying) -> TyAlias(alias, args |> List.map bind, bind underlying)
                            | TyRecursive _
                            | TySelf _ -> Ty.map bind ty
                            | other -> other
                        // A context without parameters has its own type variables boxed, as in its layout.
                        let laidOut = if info.Parameters.IsEmpty then CoreIrLowerTypes.boxOwnVariables else id
                        match Unifier.unalias info.Underlying with
                        | TyRecord(members, _) -> members |> Map.tryFind memberName |> Option.map (bind >> laidOut)
                        | _ -> None
                    else None
                | _ -> None
            layoutOf typeLayouts baseName |> Option.defaultValue []
            |> List.map (fun memberName -> contextMemberKey (Some ctxType) memberName, memberTy memberName))
        |> Map.ofList
    let parameters =
        functionContextRequirements.[name]
        |> List.map (fun key ->
            { CoreIrLowerInput.ContextParameter.Key = key
              CoreIrLowerInput.ContextParameter.Name = contextMemberParameter key
              CoreIrLowerInput.ContextParameter.Type = memberTypes |> Map.tryFind key |> Option.flatten })
    parameters, contextMemberAliases functionContextRequirements functionContextTypes typeLayouts name |> Map.ofList

// A specialisation is only called by the functions compiled with it; a definition that is not generic is exported,
// unless the function is one of its specialisations on known context members (see `CoreIrLowerKnown`), or the
// definition declares contexts, whose members no host could pass.
let private exportNameOf (functionContextRequirements: Map<string, ContextMemberKey list>) (specialisations: Set<string>) (func: CoreIr.Function) =
    if func.TypeArguments.IsEmpty && not (specialisations.Contains func.Name) && not (functionContextRequirements.ContainsKey func.Source) then
        Some func.Source
    else None

let private emitFromCoreIr (functionContextRequirements: Map<string, ContextMemberKey list>) (context: CoreIrEmit.EmitContext) (specialisations: Set<string>) (func: CoreIr.Function) =
    CoreIrEmit.emitFunction context func (exportNameOf functionContextRequirements specialisations func)

/// What lowering a module gives: its definitions, the keys of the context members each requires, and its functions
/// in the core IR.
type private LoweredModule =
    { Definitions: (string * TypeExpr option * Expression) list
      ContextRequirements: Map<string, ContextMemberKey list>
      Lowered: CoreIrLowerInput.Lowered<unit> }

/// `typed` lowered to the core IR, with the first thing that cannot be compiled reported where it is written.
let private lowerTypedModule (options: WasmOptions) (typed: TypedModule) : LoweredModule =
    let contextInstances = typed.ContextInstances
    let checkedTypes = typed.ExprTypes
    let allExprTypes = typed.InlinedExprTypes |> Map.fold (fun acc id ty -> acc |> Map.add id ty) checkedTypes
    let externs, module' = foreignFunctions typed
    // The definitions synthesized instances are made of are passed as function values.
    let synthesizedMembers =
        contextInstances
        |> Map.values
        |> Seq.collect (List.choose (function
            | Some (InstanceSynthesized members) -> Some (List.map snd members)
            | Some (InstanceDerived(_, definition, _)) -> Some [ definition ]
            | _ -> None) >> List.concat)
        |> Set.ofSeq
    // A lambda lifted out of a function that requires contexts captures the function's members it may call, by the names
    // of the parameters they come in, so that it can call them and pass them on.
    let memberCaptures =
        let defs = module' |> List.choose (function Kind (Def (ValueDef(_, name, typeOpt, expr))) -> Some(name, typeOpt, expr) | _ -> None)
        contextMemberKeys defs (fst (extractTypeLayouts module'))
    // A function that is passed context members where it is called: one whose type requires contexts, or context variables,
    // or a local function that requires parameterised ones, whose call may be passed one the def around it has.
    let localsRequiringContexts =
        typed.LocalContexts |> Map.keys |> Seq.choose (fun symbol -> typed.Symbols |> Map.tryFind symbol |> Option.map _.Name) |> Set.ofSeq
    let requiresContexts (name: string) =
        memberCaptures.ContainsKey name
        || localsRequiringContexts.Contains name
        || match typed.Types |> Map.tryFind name |> Option.map Unifier.unalias with
           | Some (TyFunc(_, _, contexts)) -> not (Unifier.writtenContexts contexts).IsEmpty
           | _ -> false
    // The members of each context, as a function value that requires the context takes them.
    let contextLayouts = contextLayoutsOf typed.TypeDefs (fst (extractTypeLayouts module'))
    // A lambda is passed the members of the contexts its type requires at each call, so it does not capture them, and
    // a local function those of the parameterised contexts it requires too, which its lambda's type leaves out (see
    // `localContextOf`). Those are keyed as the local writes them (`Eq_b_==`), as a lifted local has them in scope (see
    // `CoreIrLowerInstances.byWrittenKeys`), since a key needs the type variable's name: a lambda inside the local
    // captures them from it by those keys (#2813).
    let passedMembers (local: LambdaLifting.LocalDefinition option) (lambda: Expression) =
        let ofType =
            match checkedTypes |> Map.tryFind lambda.Id with
            | Some (TyFunc(_, _, contexts)) -> CoreIrLowerTypes.contextMembersOf contextLayouts contexts |> List.map fst
            | _ -> []
        let ofLocal =
            local
            |> Option.bind (fun local -> localContextOf typed checkedTypes local lambda)
            |> Option.map (fun (_, written, _, _) -> CoreIrLowerInstances.writtenKeys contextLayouts written)
            |> Option.defaultValue []
        ofType @ ofLocal |> Set.ofList
    let module', liftedOrigins, functionWrappers, useKeys, useBindingContexts = LambdaLifting.lift (externs |> Map.map (fun _ declared -> declared.Parameters.Length)) synthesizedMembers memberCaptures requiresContexts passedMembers typed.UseContexts (typed.AmbientMembers |> List.map _.Definition) module'
    let localContexts = localContextsOf typed checkedTypes liftedOrigins
    let withLocalContexts (exprTypes: Map<NodeId, Ty>) =
        localContexts
        |> Map.fold (fun (acc: Map<NodeId, Ty>) node (_, _, lambdaTy, contexts) ->
            match Unifier.unalias lambdaTy with
            | TyFunc(input, output, own) -> acc |> Map.add node (TyFunc(input, output, own @ List.filter carried contexts))
            | _ -> acc) exprTypes
    // Each name that refers to one of those functions, by its node, with the contexts it requires and its lambda's type.
    let localCallees =
        let bySymbol = localContexts |> Map.values |> Seq.map (fun (symbol, _, lambdaTy, contexts) -> symbol, (contexts, lambdaTy)) |> Map.ofSeq
        typed.References |> Map.filter (fun _ symbol -> bySymbol.ContainsKey symbol) |> Map.map (fun _ symbol -> bySymbol.[symbol])
    let defs =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, typeOpt, expr))) -> Some(name, typeOpt, expr)
            | _ -> None)

    let topLevelExprs =
        module'
        |> List.choose (function
            | Kind (Expr expr) -> Some expr
            | _ -> None)

    let typeLayouts, typeFieldTypes = extractTypeLayouts module'

    let shapeWrappers =
        SyntaxTree.typeDefinitions module'
        |> List.choose (function
            | TypeDef(name, _, _, body) when (match body.Kind with TypeRecord _ -> false | _ -> true) -> Some name
            | _ -> None)
        |> Set.ofList

    // The contexts each function's annotation requires (`TypeWithContext`), and the keys of their members (see
    // `contextMemberKeys`).
    let functionContextTypes =
        defs
        |> List.choose (fun (name, typeOpt, value) ->
            match extractContextTypes typeOpt value with
            | [] -> None
            | contextTypes -> Some (name, contextTypes))
        |> Map.ofList

    let functionContextRequirements = contextMemberKeys defs typeLayouts

    // The ambient context instance: the definition the type checker found for each member of an ambient context
    // (`TypedModule.AmbientMembers`) stands in for that member whenever a callee requires the context and the caller
    // has nothing else to supply.
    // This is how the runtime-provided `Console` (the members of the `Console` the host's prelude brings in) reaches the top level of a
    // module. A function that requires the context, as its type writes or as it was inferred to, is passed the members
    // of the instance where it is called: an `@entry` is passed its host's.
    // Closure conversion made a wrapper for each such function, so the member is a closure over it.
    // Lambda lifting made those wrappers only where something may need them.
    let ambientContextMembers =
        typed.AmbientMembers
        |> List.distinctBy _.Member
        |> List.filter (fun ambient -> not (functionContextRequirements.ContainsKey ambient.Definition))
        |> List.choose (fun ambient -> functionWrappers |> Map.tryFind ambient.Definition |> Option.map (fun wrapper -> NamedMember ambient.Member, wrapper))
        |> Map.ofList

    // Every definition that is not generic, the start function, and the specialisations and lifted functions they
    // need. Each function is emitted once here only to find out whether it can be (tags, strings and layouts get
    // numbers of no consequence), and again below, in order, for real.
    let probeContext: CoreIrEmit.EmitContext =
        { TagId = (fun _ -> 0)
          StringOffset = (fun _ -> 0)
          StaticClosure = (fun _ _ -> 0)
          RecordLayoutId = (fun _ -> 0)
          FunctionSlot = (fun _ _ -> 0)
          IndirectCall = ignore
          Signature = (fun _ -> None)
          HostState = 0
          CheckMemory = false
          ShownFile = None
          TakesOver = (fun _ _ -> false)
          ProbeId = (fun () -> 0)
          DbgToRuntime = false
          StateMachines = Set.empty
          YieldAtEveryWait = false
          FrameDrops = false
          BorrowedIn = fun _ -> Set.empty }
    let lowered =
        let input: CoreIrLowerInput.LowerInput =
            { Definitions = defs |> List.map (fun (name, _, expr) -> name, expr)
              DefinitionSpans =
                // A def's symbol is declared at its name, on the line its `def` starts on, in the file of its value.
                let declared =
                    typed.Symbols
                    |> Map.values
                    |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue)
                    |> Seq.map (fun symbol -> (symbol.Name, symbol.Declaration.File), symbol.Declaration)
                    |> Map.ofSeq
                defs
                |> List.choose (fun (name, _, expr) -> declared |> Map.tryFind (name, expr.Span.File) |> Option.map (fun span -> name, span))
                |> Map.ofList
              Externs = externs
              ExprTypes =
                allExprTypes
                |> carryPlainContexts typed.TypeDefs functionContextTypes (functionWrappers |> Map.toList |> List.map (fun (function', wrapper) -> wrapper, function') |> Map.ofList) module'
                |> withLocalContexts
              CalleeTypes = typed.InlinedCalleeTypes |> Map.fold (fun acc id ty -> acc |> Map.add id ty) typed.CalleeTypes
              AssociatedArguments = typed.AssociatedArguments
              DefinitionTypes = typed.Types
              ContextParameters =
                functionContextRequirements
                |> Map.map (fun name _ -> coreIrContextParameters defs functionContextTypes typeLayouts functionContextRequirements typed.TypeDefs typed.Types allExprTypes name)
              ContextArguments =
                fun name callId scopeKeys passed ->
                    try
                        Ok (contextArguments
                                functionContextRequirements functionContextTypes typeLayouts contextInstances functionWrappers
                                ambientContextMembers scopeKeys passed name callId)
                    with Failure message -> Error message
              FoundMember =
                fun callId index memberName scopeKeys ->
                    contextInstances
                    |> Map.tryFind callId
                    |> Option.bind (List.tryItem index >> Option.flatten)
                    |> Option.bind (foundMember functionWrappers scopeKeys memberName)
              LocalCallees = localCallees
              ContextLayouts = contextLayouts
              AmbientMembers = ambientContextMembers
              UseKeys = useKeys
              UseBindingContexts = useBindingContexts
              BuiltInHoles =
                contextInstances |> Map.filter (fun _ found -> found = [ Some InstanceBuiltIn ]) |> Map.keys |> Set.ofSeq
              TopLevelReferences =
                typed.References
                |> Map.filter (fun node _ -> not (typed.ImportReferences.ContainsKey node))
                |> Map.toSeq
                |> Seq.choose (fun (node, symbol) ->
                    typed.Symbols
                    |> Map.tryFind symbol
                    |> Option.filter (fun declared -> declared.Kind = TopLevelValue)
                    |> Option.map (fun declared -> node, declared.Declaration))
                |> Map.ofSeq
              RecordConstructors = typeLayouts.Keys |> Set.ofSeq
              ShapeWrappers = shapeWrappers
              Lifted =
                [ yield! liftedOrigins |> Map.toList |> List.map (fun (name, origin) ->
                    let written = localContexts |> Map.tryFind origin.Lambda.Id |> Option.map (fun (_, written, _, _) -> written) |> Option.defaultValue []
                    name, CoreIrLowerInput.LiftedLambda(origin.Lambda, origin.Captured, origin.Owner, written))
                  yield! functionWrappers |> Map.toList |> List.map (fun (wrapped, wrapper) -> wrapper, CoreIrLowerInput.FunctionWrapper wrapped) ]
                |> Map.ofList
              TopLevel = topLevelExprs
              NamedTypes =
                typed.TypeDefs
                |> Map.filter (fun _ info -> not info.IsContext)
                |> Map.map (fun _ info -> TyNominal(TypeDefInfo.nominalId info, info.Parameters |> List.map TyVar, info.Underlying, info.IsPrivate))
              Dbg = if options.Release then CoreIrLowerInput.Stripped else CoreIrLowerInput.PrintedFrom options.SourceRoot
              Probes = options.Probes
              TypeIds = CoreIrTypeIds.TypeIds()
              TypeDefinitions = typed.TypeDefs }
        try CoreIrLowerModule.lowerModule input (emitFromCoreIr functionContextRequirements probeContext Set.empty >> ignore)
        // What the passes after the lowering make has no place, even after a lowering that stopped part way.
        finally CoreIr.Places.Current <- None

    // The first thing that cannot be compiled is reported where it is written.
    match lowered.Failures with
    | (_, failure, span) :: _ -> raise (BackendFailure (lowerFailureDiagnostic failure span))
    | [] -> ()

    { Definitions = defs; ContextRequirements = functionContextRequirements; Lowered = lowered }

/// The module's functions inlined, cut down to what the program can run, and reference counted, with the names of the
/// owning entries kept (see `CoreIrClosureEntries`).
let private optimised (options: WasmOptions) (cachedNames: Set<string>) (exported: string list) (definitionFunctions: CoreIr.Function list) (startFunction: CoreIr.Function option) =
    // Inlining, over the whole module at once, since a function is inlined as it is once calls are inlined into it.
    let definitionFunctions, startFunction =
        if options.Inline then
            let inlined = CoreIrInline.inlineCalls (startFunction |> Option.map _.Name) exported cachedNames (definitionFunctions @ Option.toList startFunction)
            List.take definitionFunctions.Length inlined, startFunction |> Option.map (fun _ -> List.last inlined)
        else definitionFunctions, startFunction

    // Only what the program can run (see `CoreIrReach`): a function inlined wherever it is called is not reached.
    let startFunction = startFunction |> Option.map CoreIrReach.withoutUnreadClosures
    let definitionFunctions =
        definitionFunctions |> List.map CoreIrReach.withoutUnreadClosures |> CoreIrReach.reachedFrom startFunction exported

    // The regions of blocks that nothing can put an object on (see `CoreIrUnusedRegions`).
    let definitionFunctions, startFunction =
        let all = CoreIrUnusedRegions.withoutUnused (definitionFunctions @ Option.toList startFunction)
        List.take definitionFunctions.Length all, startFunction |> Option.map (fun _ -> List.last all)

    // Reference counting, inserted over the whole module at once, since which parameters a function owns depends on
    // the functions it calls; then drop specialisation and reuse. Each function a closure is over gets its owning entry
    // first, and keeps it only if something can call it (see `CoreIrClosureEntries`).
    let closureTargets = CoreIrClosureEntries.targets (definitionFunctions @ Option.toList startFunction)
    let owningEntries = closureTargets |> Set.map CoreIrClosureEntries.owningName
    // How each function uses its parameters, which a probe reads too (see `CoreIrEmit.EmitContext.TakesOver`).
    let definitions =
        CoreIrClosureEntries.withOwningEntries closureTargets (CoreIrClosureEntries.memberCounts (definitionFunctions @ Option.toList startFunction)) definitionFunctions
    let all = definitions @ Option.toList startFunction
    let ownership = CoreIrCounting.inferOwnership all
    let counted = CoreIrCounting.insert ownership all |> CoreIrReuse.insert
    let start = startFunction |> Option.map (fun _ -> List.last counted)
    let definitionFunctions = CoreIrClosureEntries.withoutUncalled owningEntries (Option.toList start) (List.take definitions.Length counted)
    definitionFunctions, start, owningEntries, ownership

/// The functions the module's own functions need beside the runtime's, as WebAssembly text.
type private SupportFunctions =
    { Drops: string
      Equalities: string
      Orderings: string
      Hashes: string
      Arrays: string
      /// The types an equality, ordering or hash function is made for.
      Compared: CoreIr.IrType list }

/// The support functions `functions` need, with the runtime helpers they call added to `usedHelpers`.
let private supportFunctions
    (functions: CoreIr.Function list)
    (tagIdMap: Map<string, int>)
    (functionTable: FunctionTable)
    (usedHelpers: System.Collections.Generic.HashSet<string>)
    : SupportFunctions =
    // A drop and a dup function for each type on the heap the functions above use, a function for what each of their
    // closures captures, `++` for each list of elements on the heap, and a promote function for each type a value of is
    // stored where it may outlive its `memory` block. A tag no function names has no cells to drop.
    let drops, promoted = CoreIrDrop.collect functions, CoreIrPromotion.collect functions
    let renderedDrops = CoreIrDrop.render (fun name -> tagIdMap |> Map.tryFind name) drops @ CoreIrPromotion.render (fun name -> tagIdMap |> Map.tryFind name) promoted |> String.concat "\n\n"
    for helper in CoreIrDrop.helpersUsed drops @ CoreIrPromotion.helpersUsed promoted do
        usedHelpers.Add helper |> ignore
    if drops.Types |> List.exists (function CoreIr.IrFunction _ | CoreIr.IrBox -> true | _ -> false) then
        functionTable.IndirectSignatures.Add([ "i32" ], "i32") |> ignore

    // An equality function for each record, list and union of tags `==` compares, and for each of their parts that
    // needs one.
    let equalityTypes = CoreIrEquality.collect functions
    let renderedEqualities = CoreIrEquality.render (fun name -> tagIdMap |> Map.tryFind name) equalityTypes |> String.concat "\n\n"
    for helper in CoreIrEquality.helpersUsed equalityTypes do
        usedHelpers.Add helper |> ignore

    // An ordering function for each type the ordering intrinsic or an operator orders, and for each of its parts.
    let orderingTypes = CoreIrOrdering.collect functions
    let renderedOrderings = CoreIrOrdering.render (fun name -> tagIdMap |> Map.tryFind name) orderingTypes |> String.concat "\n\n"
    for helper in CoreIrOrdering.helpersUsed orderingTypes do
        usedHelpers.Add helper |> ignore

    // A hash function for each type the hash intrinsic hashes, and for each of its parts.
    let hashingTypes = CoreIrHashing.collect functions
    let renderedHashes = CoreIrHashing.render (fun name -> tagIdMap |> Map.tryFind name) hashingTypes |> String.concat "\n\n"
    for helper in CoreIrHashing.helpersUsed hashingTypes do
        usedHelpers.Add helper |> ignore

    // A function that makes an array from a list, and one that makes a list from an array, for each type of element.
    let arrayElements = CoreIrArrays.collect functions
    let renderedArrays = CoreIrArrays.render arrayElements |> String.concat "\n\n"
    for helper in CoreIrArrays.helpersUsed arrayElements do
        usedHelpers.Add helper |> ignore
    { Drops = renderedDrops
      Equalities = renderedEqualities
      Orderings = renderedOrderings
      Hashes = renderedHashes
      Arrays = renderedArrays
      Compared = equalityTypes @ orderingTypes @ hashingTypes }

/// The function table and the types of the indirect calls through it.
let private contextPrelude (functionTable: FunctionTable) =
    let contextFnList = functionTable.Values.Values |> Seq.toList

    // A type for every indirect call, and one of `i32`s for the arity of each function in the table.
    let contextTypeDefs =
        (contextFnList |> List.map (fun ctxFn -> List.replicate (ctxFnArity ctxFn) "i32", "i32")) @ (functionTable.IndirectSignatures |> Seq.toList)
        |> Set.ofList
        |> Set.toList
        |> List.sortBy (fun (parameters, result) -> parameters.Length, indirectTypeName parameters result)
        |> List.map (fun (parameters, result) ->
            let name = indirectTypeName parameters result
            match parameters with
            | [] -> $"  (type ${name} (func (result {result})))"
            | _ -> $"""  (type ${name} (func (param {String.concat " " parameters}) (result {result})))""")
        |> String.concat "\n"

    // A `call_indirect` needs a table to validate, even in a module that never makes a closure to put in it
    // (a function that only calls the closures it is given).
    let contextTable =
        if contextFnList.IsEmpty && Seq.isEmpty functionTable.IndirectSignatures then
            ""
        else
            $"  (table $ctx_table {contextFnList.Length} funcref)"

    let contextElem =
        if contextFnList.IsEmpty then
            ""
        else
            let ordered = contextFnList |> List.sortBy ctxFnSlot
            let elems = ordered |> List.map (fun ctxFn -> $"${ctxFnName ctxFn}") |> String.concat " "
            $"  (elem (i32.const 0) {elems})"

    [ contextTypeDefs; contextTable; contextElem ]
    |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
    |> String.concat "\n"

/// `typed` as WebAssembly text compiled with `options`, and the functions it was compiled into.
let transpileTypedModuleWithOptions (options: WasmOptions) (typed: TypedModule) : string * BackendReport =
    let lowered = lowerTypedModule options typed
    let defs = lowered.Definitions
    let functionContextRequirements = lowered.ContextRequirements
    let lowered = lowered.Lowered

    let functionTable: FunctionTable =
        { Values = System.Collections.Generic.Dictionary<string, ContextFn>()
          NextSlot = 0
          IndirectSignatures = System.Collections.Generic.HashSet<string list * string>() }

    let recordTable: RecordTable =
        { LayoutIds = System.Collections.Generic.Dictionary<string list, int>() }

    let usedHelpers = System.Collections.Generic.HashSet<string>()

    // Each definition that is not generic, followed by the specialisations of it: a generic definition has only those.
    // Then the adapters between function types that require different contexts (see `coerce` in `CoreIrLowerMembers.fs`).
    let definitionFunctions =
        let definitionNames = defs |> List.map (fun (name, _, _) -> name) |> Set.ofList
        [ yield!
              defs
              |> List.collect (fun (name, _, _) ->
                  [ yield! lowered.Roots |> Map.tryFind name |> Option.map fst |> Option.toList
                    yield! lowered.Specialisations |> Map.tryFind name |> Option.defaultValue [] |> List.map fst ])
          yield!
              lowered.Roots
              |> Map.filter (fun name _ -> not (definitionNames.Contains name) && name <> CoreIrLowerInput.startFunctionName)
              |> Map.values
              |> Seq.map fst ]

    let startFunction = lowered.Roots |> Map.tryFind CoreIrLowerInput.startFunctionName |> Option.map fst

    // The top-level values built once, read through a getter (see `CoreIrCachedValues`).
    let cachedNames =
        let values =
            defs
            |> List.choose (fun (name, _, expr) ->
                match expr.Kind with
                | Lambda _ -> None
                | _ -> Some name)
            |> Set.ofList
        definitionFunctions |> List.filter (CoreIrCachedValues.isCached values.Contains) |> List.map _.Name |> Set.ofList

    // The functions of the definitions a component exports, which the program needn't call (see `ComponentExports`).
    let exportTargets =
        options.ComponentExports
        |> List.map (fun (_, func) ->
            match lowered.Roots |> Map.tryFind func.Export.Definition with
            | Some (target, _) -> func.Export.Definition, target.Name
            | None -> CoreIr.internalError $"no function for the component's export '{func.Export.Definition}'")
        |> Map.ofList

    let definitionFunctions, startFunction, owningEntries, ownership =
        optimised options cachedNames (exportTargets |> Map.values |> List.ofSeq) definitionFunctions startFunction

    // The tags, strings and dbg forms of exactly the functions emitted below.
    let facts = CoreIrFacts.collect (definitionFunctions @ Option.toList startFunction)
    // With the tags of the types a component's exports take and give, which their functions may never write.
    let exportTags = options.ComponentExports |> List.collect (fun (_, func) -> List.map snd func.Parameters @ Option.toList func.Result) |> List.collect WitType.tags
    let tagIdMap = facts.Tags @ exportTags |> List.distinct |> List.sort |> List.mapi (fun index name -> name, index + 1) |> Map.ofList
    // A checked module lays out what its memory checks write with its own literals.
    let literals =
        if options.CheckMemory then facts.Strings @ (MemoryChecks.messages |> List.except facts.Strings)
        else facts.Strings
    let stringLiteralMap, stringLiteralData = buildStringLiteralMap literals
    // The static closures go after the literals, and the free-list heads and the heap after them, however far they run.
    let staticClosureMap = staticClosureOffsets facts.StaticClosures (stringDataEnd stringLiteralData)
    // The block the host keeps its state in goes after them, 8-byte aligned, in a module whose host uses it.
    let dataEnd = stringDataEnd stringLiteralData + facts.StaticClosures.Length * staticClosureBytes
    let hostStateAddress = (dataEnd + 7) &&& ~~~7
    let hostStateEnd = if facts.UsesHostState then hostStateAddress + hostStateSize else dataEnd
    // The table of powers of five that reading a float uses goes after it, 8-byte aligned (see `PowersOfFive`).
    let powersOfFiveAddress = (hostStateEnd + 7) &&& ~~~7
    let staticEnd = if facts.UsesPowersOfFive then powersOfFiveAddress + PowersOfFive.bytes else hostStateEnd
    // The scratch block that writing a float uses goes just below the heap, where the helpers find it (see `FloatText`).
    let heapBase = heapBaseAfter (if facts.UsesFloatText then staticEnd + FloatText.scratchBytes else staticEnd)
    let memoryDirective = $"  (memory {initialMemoryPages (heapStartOffset heapBase)})"

    // The owning entry of each function a closure is over that something can call, which has the table slot after the
    // function's: its copy if it was kept, or the function itself if its closures capture nothing.
    let withOwningEntry =
        let calledTypes = CoreIrClosureEntries.owningCallTypes (definitionFunctions @ Option.toList startFunction)
        let all = definitionFunctions @ Option.toList startFunction
        CoreIrClosureEntries.owningEntries all
        |> Map.filter (fun name owning ->
            owning <> name || all |> List.exists (fun func -> func.Name = name && calledTypes.Contains (CoreIrClosureEntries.signatureOf func)))

    // The parameters and result of each function, which a call converts a value of no known type to and from.
    let signatures =
        definitionFunctions @ Option.toList startFunction
        |> List.map (fun func -> func.Name, (func.Params |> List.map _.Type, func.Result))
        |> Map.ofList

    // The files of the standard library's that the markers name, as they name them (see `CoreIrEmit.libraryMarker`).
    let libraryFiles = System.Collections.Generic.SortedSet<string>()

    // How many probes the module has numbered (see `EmitContext.ProbeId`).
    let probes = ref 0

    // A component's entry is lifted with the callback ABI, so its start function can stop, and is a state machine when
    // it calls one (see `ComponentCallback`); any other start function runs them to the end.
    let startCanStop = options.Component = Some CliRun || options.Component = Some HttpHandler
    // The functions that can stop and carry on later (see `TaskLowering`). The getter of a cached value runs the
    // function that builds it to the end, so it is the function that builds it that can be a state machine.
    let machines =
        definitionFunctions @ Option.toList startFunction
        |> List.map (fun func -> if cachedNames.Contains func.Name then { func with Name = CoreIrCachedValues.buildName func.Name } else func)
        |> CoreIrSplitPoints.stateMachines (if startCanStop then "" else CoreIrLowerInput.startFunctionName)
    CoreIrSplitPoints.checkFunctionValues CoreIrLowerInput.startFunctionName (definitionFunctions @ Option.toList startFunction)

    let context: CoreIrEmit.EmitContext =
        { TagId =
            fun name ->
                match tagIdMap |> Map.tryFind name with
                | Some id -> id
                | None -> CoreIr.internalError $"missing tag discriminant for `#{name}`"
          StringOffset =
            fun value ->
                match stringLiteralMap |> Map.tryFind value with
                | Some offset -> offset
                | None -> CoreIr.internalError $"missing string literal offset for {value}"
          StaticClosure =
            fun name arity ->
                functionValueSlot functionTable withOwningEntry name arity |> ignore
                match staticClosureMap |> Map.tryFind name with
                | Some offset -> offset
                | None -> CoreIr.internalError $"missing static closure offset for {name}"
          RecordLayoutId = recordLayoutId recordTable
          FunctionSlot = functionValueSlot functionTable withOwningEntry
          IndirectCall = functionTable.IndirectSignatures.Add >> ignore
          Signature = fun name -> signatures |> Map.tryFind name
          HostState = hostStateAddress
          CheckMemory = options.CheckMemory
          ShownFile =
            if options.Places then
                Some(fun file ->
                    let shown = PackageModules.shownFile options.SourceRoot file
                    if CoreIrEmit.isLibraryFile file then libraryFiles.Add shown |> ignore
                    shown)
            else None
          TakesOver = fun name index -> CoreIrCounting.parameterUse ownership name index = Owned
          ProbeId = fun () -> (probes.Value <- probes.Value + 1; probes.Value)
          DbgToRuntime = options.Probes || options.DbgToEmbedder
          StateMachines = machines
          YieldAtEveryWait = options.YieldAtEveryWait
          FrameDrops = options.FrameDrops
          BorrowedIn = CoreIrCounting.borrowedIn ownership }

    let specialisations = lowered.Specialisations |> Map.values |> Seq.concat |> Seq.map (fun (func, _) -> func.Name) |> Set.ofSeq

    // Whether a function emitted below reads or writes linear memory in its own code.
    let mutable functionsTouchMemory = false

    let cachedValues = definitionFunctions |> List.filter (fun func -> cachedNames.Contains func.Name)

    // The table slot of each state machine's resume entry and the name of its drop entry (see `WasmOptions.FrameDrops`).
    let frameDrops = ResizeArray<int * string>()

    /// The indirect type of the resume entry of a state machine that gives a value of type `result` (see `TaskLowering`).
    let resumeType (result: CoreIr.IrType) =
        let valueType = CoreIrLayout.valueType result
        context.IndirectCall(TaskLowering.resumeTypeOf valueType)
        TaskLowering.resumeTypeName valueType

    /// The text of a function from the core IR, noting the helpers it calls and whether it touches memory. A cached
    /// value's is the function that builds it, then its getter under its own name.
    let fromCoreIr (func: CoreIr.Function) =
        let note (emitted: CoreIrEmit.Emitted) =
            for helper in emitted.Helpers do
                usedHelpers.Add helper |> ignore
            functionsTouchMemory <- functionsTouchMemory || emitted.UsesMemory
            emitted.FrameDrop |> Option.iter frameDrops.Add
            emitted.Text
        if cachedNames.Contains func.Name then
            let build = note (CoreIrEmit.emitFunction context { func with Name = CoreIrCachedValues.buildName func.Name } None)
            let resumes = if machines.Contains (CoreIrCachedValues.buildName func.Name) then Some (resumeType func.Result) else None
            build + "\n\n" + CoreIrCachedValues.getter context.ShownFile func (exportNameOf functionContextRequirements specialisations func) resumes
        // An owning entry is a copy of a function, never exported as well.
        elif owningEntries.Contains func.Name then note (CoreIrEmit.emitFunction context func None)
        else note (emitFromCoreIr functionContextRequirements context specialisations func)

    let renderedFunctions = definitionFunctions |> List.map fromCoreIr |> String.concat "\n\n"

    let renderedCachedGlobals = cachedValues |> List.collect CoreIrCachedValues.globals |> String.concat "\n"

    // The start function, and how the module runs it: the start directive or the component entry that runs it, or,
    // for an entry lifted with the callback ABI whose start function can stop, the entry (see `ComponentCallback`).
    let renderedStartFunction, startDirective, startEntries =
        match startFunction with
        | Some func when machines.Contains func.Name ->
            let emitted = CoreIrEmit.emitFunction context func None
            for helper in emitted.Helpers do
                usedHelpers.Add helper |> ignore
            functionsTouchMemory <- functionsTouchMemory || emitted.UsesMemory
            let entryKind = options.Component |> Option.defaultValue CliRun
            emitted.Text, "", [ ComponentCallback.startEntry entryKind func.Name (resumeType func.Result) (CoreIrCachedValues.teardown cachedValues) ]
        | Some func ->
            // A module with probes asks the runtime whether it is probing before anything else runs.
            let before =
                if facts.UsesProbes then [ $"call ${CoreIrEmitImports.probingEnabled}"; $"global.set ${CoreIrEmitImports.probing}" ] else []
            let emitted = CoreIrEmit.emitStart context func before (CoreIrCachedValues.teardown cachedValues)
            for helper in emitted.Helpers do
                usedHelpers.Add helper |> ignore
            functionsTouchMemory <- functionsTouchMemory || emitted.UsesMemory
            let directive =
                match options.Component with
                | Some entry -> ComponentAbi.entryFunction entry (Some CoreIrLowerInput.startFunctionName)
                | None when options.CheckMemory || options.ExportStart ->
                    $"""  (export "{CheckedExports.start}" (func ${CoreIrLowerInput.startFunctionName}))"""
                | None -> $"  (start ${CoreIrLowerInput.startFunctionName})"
            emitted.Text, directive, []
        | None -> "", (options.Component |> Option.map (fun entry -> ComponentAbi.entryFunction entry None) |> Option.defaultValue ""), []

    // The runtime's part of the state machines (see `TaskLowering`).
    // and the scheduler (see `WasmScheduler`), which a module that starts tasks, or runs a state machine to the end, has.
    let renderedTasks =
        if machines.IsEmpty && not (usedHelpers |> Seq.exists WasmScheduler.intrinsics.Contains) then ""
        else
            let yielding =
                if options.YieldAtEveryWait then
                    usedHelpers.Add Helpers.alloc |> ignore
                    usedHelpers.Add Helpers.free |> ignore
                    TaskLowering.yieldHelpers (context.FunctionSlot TaskLowering.Names.yieldResume 1)
                else []
            let dropping =
                if options.FrameDrops then
                    usedHelpers.Add Helpers.free |> ignore
                    [ TaskLowering.frameDrop (List.ofSeq frameDrops) ]
                else []
            for helper in WasmScheduler.dependencies do
                usedHelpers.Add helper |> ignore
            context.IndirectCall(TaskLowering.resumeTypeOf "i32")
            // The host's wait is a function value of an `I32`: its closure and the timeout.
            context.IndirectCall([ "i32"; "i32" ], "i32")
            let scheduler =
                WasmScheduler.helpers
                    (context.FunctionSlot WasmScheduler.Names.waitResume 1)
                    (context.FunctionSlot WasmScheduler.Names.scopeExitResume 1)
                    (context.FunctionSlot WasmScheduler.Names.hostWaitResume 1)
                    (context.FunctionSlot WasmScheduler.Names.scopeWaitAnyResume 1)
                    (context.FunctionSlot WasmScheduler.Names.cellDrop 1)
                    (TaskLowering.resumeTypeName "i32")
                    (indirectTypeName [ "i32"; "i32" ] "i32")
            TaskLowering.globals @ yielding @ dropping @ scheduler |> String.concat "\n"

    let support = supportFunctions (definitionFunctions @ Option.toList startFunction) tagIdMap functionTable usedHelpers


    let report =
        { Functions = [ yield! definitionFunctions |> List.map _.Name; yield! startFunction |> Option.map _.Name |> Option.toList ]
          Specialisations =
            defs
            |> List.collect (fun (name, _, _) ->
                lowered.Specialisations |> Map.tryFind name |> Option.defaultValue [] |> List.map (fun (func, _) -> func.Name, sanitizeIdentifier name)) }

    // The checked allocator and counts call the memory checks, which write to stderr and exit.
    let checksMemory =
        options.CheckMemory
        && [ Helpers.alloc; Helpers.free; Helpers.retain; Helpers.release ]
           |> List.exists (closeHelpers Set.empty (usedHelpers |> Seq.toList)).Contains
    if checksMemory then
        usedHelpers.Add Helpers.writeStderr |> ignore
        usedHelpers.Add Helpers.procExit |> ignore
    if options.Component.IsSome then
        for helper in ComponentAbi.reallocHelpers do
            usedHelpers.Add helper |> ignore
    // The functions a component exports for the program's definitions, which note the helpers they call.
    let exportFunctions =
        let target (definition: string) : ComponentExports.ExportTarget =
            let name = exportTargets.[definition]
            let func = definitionFunctions |> List.find (fun func -> func.Name = name)
            { Function = name
              Parameters = func.Params |> List.map _.Type
              Result = func.Result
              TakesOver = fun index -> CoreIrCounting.parameterUse ownership name index = Owned
              Resumes = if machines.Contains name then Some (resumeType func.Result) else None }
        let exportContext: ComponentExports.ExportContext =
            { TagCode = context.TagId >> Wat.tagCode
              RecordLayout = context.RecordLayoutId
              UseHelper = usedHelpers.Add >> ignore }
        ComponentExports.functions exportContext options.ComponentExports target, exportContext
    let (synchronousExports, exportEntries, exportReturns), exportContext = exportFunctions
    // The entries lifted with the callback ABI that can wait, and the scheduler they run (see `ComponentCallback`).
    let callbackFunctions =
        match startEntries @ exportEntries with
        | [] -> ""
        | entries -> ComponentCallback.functions entries (indirectTypeName [ "i32"; "i32" ] "i32")
    // The functions a component imports for the program's definitions that the program calls, each with the function
    // a call of it calls, which the module defines (see `ComponentImports`). Outside a component, nothing can call one.
    let componentImports =
        let all = ComponentInterface.imports typed |> List.map (fun func -> ComponentInterface.externOf func, func)
        all |> List.filter (fun (declared, _) -> facts.Imports |> List.contains declared)
    match options.Component, componentImports with
    | None, (_, func) :: _ ->
        let span =
            typed.Module
            |> List.tryPick (function
                | Kind (Def (ValueDef(_, name, _, value))) when name = func.Export.Definition -> Some value.Span
                | _ -> None)
        let message =
            $"'{func.Export.Definition}' imports '{func.Export.Name}' from {func.Export.Module}, which only a component built from the program can call: `nanyx build` builds one, and `nanyx run` and `nanyx test` can't run it"
        raise (BackendFailure(match span with Some span -> Diagnostics.at DiagnosticCodes.ComponentImportOutsideComponent span message | None -> Diagnostics.general DiagnosticCodes.ComponentImportOutsideComponent message))
    | _ -> ()
    let importFunctions = ComponentImports.functions exportContext componentImports
    // A crash reports its message to the runtime that runs the module, which reads its length (see `CrashReport`).
    let reportsCrashes = options.CrashToRuntime && options.Component.IsNone && usedHelpers.Contains Helpers.crashed
    if reportsCrashes then usedHelpers.Add Helpers.stringLength |> ignore
    let usedHelperSet = closeHelpers Set.empty (usedHelpers |> Seq.toList)
    // Ending a region calls what drops each of its objects' contents through the function table (see `WasmRegions`).
    if usedHelperSet.Contains WasmRegions.Names.regionExit then
        functionTable.IndirectSignatures.Add([ "i32" ], "i32") |> ignore
    // Exiting is not a stream, but its WASI import is declared with stderr's.
    let moduleUsesStandardStreams = [ Helpers.writeStderr; Helpers.procExit ] |> List.exists usedHelperSet.Contains
    // Anything that writes to stderr, or `dbg`, shares the WASI import, the memory and the newline byte. A component's
    // core module defines both of the runtime's WASI functions itself, on `wasi:cli`.
    let moduleUsesIoHelpers = facts.UsesDbgHelpers || moduleUsesStandardStreams || options.Component.IsSome
    // The WASI functions the runtime imports itself. A foreign function of one of them is the same import (see
    // `CoreIrEmitImports.importName`), so the module declares it once.
    let runtimeImports =
        [ if moduleUsesIoHelpers then CoreIrEmitImports.wasiFdWrite
          if usedHelperSet.Contains Helpers.procExit || options.Component.IsSome then CoreIrEmitImports.wasiProcExit
          if reportsCrashes then CrashReport.report ]
    // The foreign functions a component's core module defines itself (see `ComponentAbi.externs`).
    let definedExterns = options.Component |> Option.map ComponentAbi.externs |> Option.defaultValue []
    let isComponentImport (declared: Extern) = componentImports |> List.exists (fst >> (=) declared)
    let foreignImports =
        facts.Imports
        |> List.filter (isComponentImport >> not)
        |> List.filter (fun declared ->
            match definedExterns |> List.tryFind (fun defined -> defined.Module = declared.Module && defined.Name = declared.Name) with
            | Some defined when defined = declared -> false
            | Some defined ->
                raise (BackendFailure (Diagnostics.general DiagnosticCodes.ConflictingExterns $"A foreign function imports \"{declared.Name}\" from \"{declared.Module}\" with another type than the compiler defines it with: declare it as `{CoreIrEmitImports.importDeclaration defined |> fun text -> text.Trim()}`"))
            | None -> true)
        |> List.filter (fun declared ->
            match runtimeImports |> List.tryFind (fun runtime -> runtime.Module = declared.Module && runtime.Name = declared.Name) with
            | Some runtime when runtime = declared -> false
            | Some runtime ->
                raise (BackendFailure (Diagnostics.general DiagnosticCodes.ConflictingExterns $"A foreign function imports \"{declared.Name}\" from \"{declared.Module}\" with another type than the runtime's: declare it as `{CoreIrEmitImports.importDeclaration runtime |> fun text -> text.Trim()}`"))
            | None -> true)
        |> List.map CoreIrEmitImports.importDeclaration
        |> String.concat "\n"
    // Everything that allocates does it through `$alloc`, and `$free` gives the top of the heap back to it.
    let moduleUsesHeap = usedHelperSet.Contains Helpers.alloc || usedHelperSet.Contains Helpers.free
    let renderedHelpers =
        let helpers = if options.CheckMemory then checkedRuntimeHelpers heapBase else runtimeHelpers heapBase
        let helpers = if reportsCrashes then helpers |> Map.add Helpers.crashed CrashReport.crashed else helpers
        [ yield!
              helperDependencies
              |> List.map fst
              |> List.filter usedHelperSet.Contains
              |> List.map (fun name -> helpers |> Map.find name)
          match options.Component with
          | Some entry ->
              yield ComponentAbi.functions entry CoreIrEmitImports.importName
              yield synchronousExports
              yield callbackFunctions
              yield importFunctions
          | None -> ()
          if checksMemory then
              yield memoryCheckFunctions heapBase (fun message -> stringLiteralMap |> Map.find message)
              yield memoryCheckExports
          if options.CheckMemory && moduleUsesHeap then
              yield $"""  (export "{CheckedExports.heapPointer}" (global $heap_ptr))""" ]
        |> String.concat "\n"

    let dbgPrelude =
        if moduleUsesIoHelpers then
            [ match options.Component with
              | Some entry ->
                  ComponentAbi.imports entry
                  yield! exportReturns
                  if not componentImports.IsEmpty then ComponentImports.declarations componentImports
              | None -> yield! runtimeImports |> List.map CoreIrEmitImports.importDeclaration

              memoryDirective
              """  (export "memory" (memory 0))"""
              if facts.UsesDbgHelpers then
                  $"""  (data (i32.const {dbgStrNewlineOffset}) "\0a")"""
                  yield! dbgFunctions ]
            |> String.concat "\n"
        else
            ""

    // Every function body has been rendered by now, so the function-value slots are final.
    let contextPrelude = contextPrelude functionTable

    let dataSegments =
        [ renderDataSegments stringLiteralData
          yield!
              facts.StaticClosures
              |> List.map (fun (name, arity) -> staticClosureData staticClosureMap.[name] (functionValueSlot functionTable withOwningEntry name arity))
          if facts.UsesPowersOfFive then
              $"""  (global {PowersOfFive.addressGlobal} i32 (i32.const {powersOfFiveAddress}))"""
              $"""  (data (i32.const {powersOfFiveAddress}) "{escapeWasmData (PowersOfFive.data ())}")""" ]
        |> List.filter (fun segment -> segment <> "")
        |> String.concat "\n"

    // Add memory if we have static data or anything emitted touches memory, but no dbg helpers (dbgPrelude already
    // includes memory): a function's own code, a helper it calls, a drop function, or an equality, ordering or hash
    // function of a type other than a number, a bool or unit, which reads what it compares (a tag's code among it).
    let computedOnValue (ty: CoreIr.IrType) =
        match ty with
        | CoreIr.IrInt
        | CoreIr.IrSized _
        | CoreIr.IrBool
        | CoreIr.IrUnit -> true
        | _ -> false
    // A foreign function deals in addresses in the module's memory, so a module that imports one exports it.
    let functionsUseMemory =
        functionsTouchMemory
        || not facts.Imports.IsEmpty
        || usedHelperSet |> Set.exists touchesMemory
        || support.Drops <> ""
        || support.Compared |> List.exists (computedOnValue >> not)
    let needsMemory =
        (not (List.isEmpty stringLiteralData && List.isEmpty facts.StaticClosures) || functionsUseMemory) && not moduleUsesIoHelpers
    let memorySection =
        if needsMemory then
            memoryDirective + "\n" + """  (export "memory" (memory 0))"""
        else
            ""

    // Add heap global if we use heap allocation
    let heapSection =
        if moduleUsesHeap then
            $"  (global $heap_ptr (mut i32) (i32.const {heapStartOffset heapBase}))"
        else
            ""

    // Imports must precede every function definition in a WebAssembly module, so the foreign functions and the prelude,
    // which imports WASI's functions, come first.
    // What a module with probes imports from the runtime, and the global it keeps whether it is probing in; and `dbg`,
    // which a module the runtime runs gives what it prints to (see `EmitContext.DbgToRuntime`).
    let probeImports =
        [ if facts.UsesProbes then
              $"""  (import "{CoreIrEmitImports.probeModule}" "enabled" (func ${CoreIrEmitImports.probingEnabled} (result i32)))"""
              $"""  (import "{CoreIrEmitImports.probeModule}" "probe" (func ${CoreIrEmitImports.probeCall} (param i32) (result i32)))"""
              $"""  (import "{CoreIrEmitImports.probeModule}" "local" (func ${CoreIrEmitImports.probeLocal} (param i32 i32)))"""
          if facts.UsesDbgHelpers && (options.Probes || options.DbgToEmbedder) then
              $"""  (import "{CoreIrEmitImports.probeModule}" "dbg" (func ${CoreIrEmitImports.dbgImport} (param i32 i32 i32 i32) (result i32)))""" ]
        |> String.concat "\n"
    let probeGlobal = if facts.UsesProbes then $"  (global ${CoreIrEmitImports.probing} (mut i32) (i32.const 0))" else ""

    let allImports =
        [ if not (String.IsNullOrWhiteSpace foreignImports) then foreignImports
          if not (String.IsNullOrWhiteSpace probeImports) then probeImports
          if not (String.IsNullOrWhiteSpace dbgPrelude) then dbgPrelude
          if not (String.IsNullOrWhiteSpace memorySection) then memorySection
          if not (String.IsNullOrWhiteSpace heapSection) then heapSection
          if not (String.IsNullOrWhiteSpace probeGlobal) then probeGlobal
          if not (String.IsNullOrWhiteSpace renderedHelpers) then renderedHelpers
          if not (String.IsNullOrWhiteSpace contextPrelude) then contextPrelude ]
        |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> String.concat "\n\n"

    if String.IsNullOrWhiteSpace renderedFunctions && String.IsNullOrWhiteSpace allImports && String.IsNullOrWhiteSpace dataSegments then
        "(module)", report
    else
        let parts =
            [ if not (String.IsNullOrWhiteSpace allImports) then allImports
              if not (String.IsNullOrWhiteSpace dataSegments) then dataSegments
              if not (String.IsNullOrWhiteSpace renderedCachedGlobals) then renderedCachedGlobals
              if not (String.IsNullOrWhiteSpace renderedTasks) then renderedTasks
              if not (String.IsNullOrWhiteSpace renderedFunctions) then renderedFunctions
              if not (String.IsNullOrWhiteSpace support.Drops) then support.Drops
              if not (String.IsNullOrWhiteSpace support.Equalities) then support.Equalities
              if not (String.IsNullOrWhiteSpace support.Orderings) then support.Orderings
              if not (String.IsNullOrWhiteSpace support.Hashes) then support.Hashes
              if not (String.IsNullOrWhiteSpace support.Arrays) then support.Arrays
              if not (String.IsNullOrWhiteSpace renderedStartFunction) then renderedStartFunction
              if not (String.IsNullOrWhiteSpace startDirective) then startDirective
              if libraryFiles.Count > 0 then libraryFiles |> Seq.map CoreIrEmit.libraryMarker |> String.concat "\n" ]
        let body = String.concat "\n\n" parts
        $"(module\n{body}\n)", report

/// `typed` as WebAssembly text, and the functions it was compiled into.
let transpileTypedModuleWithReport (typed: TypedModule) : string * BackendReport =
    transpileTypedModuleWithOptions WasmOptions.Default typed

let transpileTypedModuleToWat (typed: TypedModule) : string =
    transpileTypedModuleWithReport typed |> fst

/// `typed` as WebAssembly text compiled with `options`, with what the backend cannot compile reported as a diagnostic
/// at the place in the source it is written: the innermost expression it is in that has one, or else the definition.
/// It runs on a large stack (see `Nanyx.LargeStack`), as lifting, lowering and emitting recurse once per operand of a
/// chain of operators and once per branch of an `else if` chain.
let tryTranspileTypedModuleWith (options: WasmOptions) (typed: TypedModule) : Result<string, Diagnostic> =
    Nanyx.LargeStack.run <| fun () ->
    try
        Ok (transpileTypedModuleWithOptions options typed |> fst)
    with
    | BackendFailure diagnostic -> Error diagnostic
    | CoreIr.InternalError reason -> Error (internalErrorDiagnostic reason None)
    | ex -> Error (internalErrorDiagnostic ex.Message None)

/// `tryTranspileTypedModuleWith` the default options.
let tryTranspileTypedModuleToWat (typed: TypedModule) : Result<string, Diagnostic> =
    tryTranspileTypedModuleWith WasmOptions.Default typed
