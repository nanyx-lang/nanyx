module Transpiler.Wasm.CodeGen

open System
open System.Text
open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat

/// Something the backend cannot compile, and where it is written: what `tryTranspileTypedModuleToWat` reports as a
/// diagnostic.
exception UnsupportedConstruct of message: string * span: SourceSpan

/// Fails with `message`, placed at `span`.
let private unsupportedAt (span: SourceSpan) (message: string) : 'a = raise (UnsupportedConstruct(message, span))

/// What a broken invariant of the backend (see `CoreIr.InternalError`) says to the user: the one place it gets its
/// prefix.
let private internalErrorMessage (reason: string) = $"Internal error: {reason}"

/// What a function the core IR could not lower or finish says to the user.
let lowerFailureMessage (failure: CoreIrLowerInput.LowerFailure) =
    match failure with
    | CoreIrLowerInput.Internal reason -> internalErrorMessage reason
    | CoreIrLowerInput.NotYet reason -> $"The WebAssembly backend cannot compile {reason} yet"

type private ContextFn = string * int * int

/// The module's function table (`$ctx_table`), as far as functions used as values are concerned: a top-level
/// function referenced without being called, or a lifted lambda, gets a slot here and is called through
/// `call_indirect`. Shared by every function of a module and read when the table is rendered.
type private FunctionTable =
    { /// Function name -> (WebAssembly name, slot, arity).
      Values: System.Collections.Generic.Dictionary<string, ContextFn>
      /// The next free slot.
      mutable NextSlot: int
      /// The arities of every indirect call made through a local, so a `$ctx_fn_N` type exists for each.
      IndirectArities: System.Collections.Generic.HashSet<int> }

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
      CheckMemory: bool }

    static member Default = { CheckMemory = false }

/// The names a module compiled with `CheckMemory` exports.
module CheckedExports =
    /// The start function, which runs the program's top-level code.
    let start = CoreIrLowerInput.startFunctionName
    /// How many blocks the program allocated and has not freed.
    let liveBlocks = MemoryChecks.liveBlocks
    /// How many blocks the program allocated.
    let allocations = MemoryChecks.allocations
    /// The top of the heap: the most it has grown to.
    let heapPointer = "heap_ptr"
    /// The exit code a program stops with when a memory check fails.
    let failureExitCode = MemoryChecks.exitCode

let private ctxFnName (name: string, _, _) = name
let private ctxFnSlot (_, slot: int, _) = slot
let private ctxFnArity (_, _, arity: int) = arity

// Extract context type expressions from a TypeWithContext or TypeContext annotation
let private extractContextTypes (typeExpr: TypeExpr option) : TypeExpr list =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, _))) -> contexts
    | Some (Kind (TypeContext contexts)) -> contexts
    | _ -> []

// Get the inner type (without context) from a TypeWithContext or TypeContext
let private unwrapContextType (typeExpr: TypeExpr option) : TypeExpr option =
    match typeExpr with
    | Some (Kind (TypeWithContext(_, inner))) -> Some inner
    | Some (Kind (TypeContext _)) -> None  // TypeContext alone has no inner type
    | other -> other


/// The keys of the members of the contexts each definition in `defs` requires, by the definition's name, from the
/// members of each context in `typeLayouts`. For a parameterised context the key is the instance's member (`toString`
/// of `ToString(a)`), and for a plain one it is the member's name (`println`).
let private contextMemberKeys (defs: (string * TypeExpr option * Expression) list) (typeLayouts: Map<string, string list>) : Map<string, ContextMemberKey list> =
    defs
    |> List.choose (fun (name, typeOpt, _) ->
        let keys =
            extractContextTypes typeOpt
            |> List.collect (fun ctxType ->
                let baseName =
                    match ctxType.Kind with
                    | TypeName n
                    | TypeApply(n, _) -> n
                    | _ -> "unknown"
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue [] |> List.map (contextMemberKey (Some ctxType)))
        if keys.IsEmpty then None else Some (name, keys))
    |> Map.ofList


///// Whether `name` is a nominal type's, qualified by its module block or not (`$Money`, `main.money.$Money`).
let private isNominalTypeName (name: string) =
    let own = match name.LastIndexOf '.' with -1 -> name | dot -> name.Substring(dot + 1)
    own.StartsWith "$"

/// The function-table slot of a top-level function used as a value, assigning one on first use.
let private functionValueSlot (table: FunctionTable) (name: string) (arity: int) : int =
    match table.Values.TryGetValue name with
    | true, ctxFn -> ctxFnSlot ctxFn
    | _ ->
        let slot = table.NextSlot
        table.NextSlot <- slot + 1
        table.Values.[name] <- (sanitizeIdentifier name, slot, arity)
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
    let map = entries |> List.map (fun (value, baseOffset, _) -> value, baseOffset + 8) |> Map.ofList
    map, entries

/// Where the string literals laid out as `entries` (see `buildStringLiteralMap`) end.
let private stringDataEnd (entries: (string * int * byte array) list) =
    entries |> List.fold (fun dataEnd (_, offset, bytes) -> max dataEnd (offset + bytes.Length)) stringDataBaseOffset

/// The data segments that lay out `entries` (see `buildStringLiteralMap`).
let private renderDataSegments (entries: (string * int * byte array) list) =
    entries
    |> List.map (fun (_, offset, bytes) -> $"  (data (i32.const {offset}) \"{escapeWasmData bytes}\")")
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
          yield "  (import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write (param i32 i32 i32 i32) (result i32)))"
          yield $"  (import \"wasi_snapshot_preview1\" \"proc_exit\" (func ${Helpers.procExit} (param i32)))"
      yield "  (memory 1)"
      yield "  (export \"memory\" (memory 0))"
      yield $"  (global $heap_ptr (mut i32) (i32.const {heapStartOffset heapBase}))"
      yield helpers |> Map.find Helpers.alloc
      yield helpers |> Map.find Helpers.free
      yield $"  (export \"alloc\" (func ${Helpers.alloc}))"
      yield $"  (export \"free\" (func ${Helpers.free}))"
      if options.CheckMemory then
          yield renderDataSegments messageData
          yield memoryCheckFunctions heapBase (fun message -> messageOffsets |> Map.find message)
          for name in [ Helpers.retain; Helpers.release; Helpers.stringLength; Helpers.writeStderr ] do
              yield helpers |> Map.find name
          yield $"  (export \"retain\" (func ${Helpers.retain}))"
          yield $"  (export \"release\" (func ${Helpers.release}))"
          yield $"  (export \"{CheckedExports.liveBlocks}\" (global ${MemoryChecks.liveBlocks}))"
          yield $"  (export \"{CheckedExports.allocations}\" (global ${MemoryChecks.allocations}))"
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

/// Where a call to `name`, the call at node `callId`, finds each member of the contexts the callee requires, in the
/// order it requires them, given the keys of the members in scope there (`scopeKeys`). Each is found where the type
/// checker found its context: an instance in scope (a `use`, or a context the caller's type declares), or one built
/// from attached definitions. One the type checker left to be found by name is the member of that key in scope, or a
/// compatible one (the same context and member, at other type arguments), or else the ambient instance's member. One of
/// the keys in `passed` is not found: the callers of the function's value pass it.
let private contextArguments
    (requirements: Map<string, ContextMemberKey list>)
    (contextTypes: Map<string, TypeExpr list>)
    (typeLayouts: Map<string, string list>)
    (contextInstances: Map<NodeId, ContextInstance option list>)
    (functionWrappers: Map<string, string>)
    (ambientMembers: Map<ContextMemberKey, string>)
    (scopeKeys: Set<ContextMemberKey>)
    (passed: Set<ContextMemberKey>)
    (name: string)
    (callId: NodeId)
    : CoreIrLowerInput.ContextArgument list =
    let required = requirements |> Map.tryFind name |> Option.defaultValue []

    // A member of the same context at other type arguments, not already passed: a required `toString` of
    // `ToString(a)` can be an available one of `ToString(int)`. The first by its key's text is taken.
    let findCompatible (used: Set<ContextMemberKey>) (requiredKey: ContextMemberKey) : ContextMemberKey option =
        scopeKeys
        |> Seq.sortBy ContextMemberKey.text
        |> Seq.tryFind (fun availableKey -> not (used.Contains availableKey) && ContextMemberKey.sameMember requiredKey availableKey)

    // The member in scope, else the ambient instance's.
    let memberFor (requiredKey: ContextMemberKey) (resolvedKey: ContextMemberKey) =
        if scopeKeys.Contains resolvedKey then CoreIrLowerInput.MemberInScope resolvedKey
        else
            match ambientMembers |> Map.tryFind resolvedKey with
            | Some wrapper -> CoreIrLowerInput.ClosureOverWrapper wrapper
            | None ->
                let available = (List.ofSeq scopeKeys) @ (ambientMembers |> Map.toList |> List.map fst) |> List.map ContextMemberKey.text |> String.concat ", "
                failwith $"Context function {ContextMemberKey.text requiredKey} not available in current scope. Available: [{available}]"

    // Where the type checker found each context the callee requires, as each required member's instance, in the order
    // of `required`: none for a call it found nothing for. A function that does not write its type may also require
    // ambient contexts, after the parameterised ones its type is given (see `ContextInference`): they are not passed.
    let instances =
        match contextInstances |> Map.tryFind callId, contextTypes |> Map.tryFind name with
        | Some found, Some types when found.Length >= types.Length ->
            List.zip types (List.truncate types.Length found)
            |> List.collect (fun (contextType, instance) ->
                let baseName = match contextType.Kind with TypeName n | TypeApply(n, _) -> n | _ -> "unknown"
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue [] |> List.map (fun memberName -> memberName, instance))
            |> fun (perMember: (string * ContextInstance option) list) -> if perMember.Length = required.Length then Some perMember else None
        | _ -> None

    required
    |> List.mapi (fun index requiredKey -> requiredKey, instances |> Option.map (fun perMember -> perMember.[index]))
    |> List.fold (fun (acc: CoreIrLowerInput.ContextArgument list, used: Set<ContextMemberKey>) (requiredKey, instance) ->
        match instance with
        // A member its value's callers pass.
        | _ when passed.Contains requiredKey -> acc @ [ CoreIrLowerInput.PassedByCaller ], used
        // An instance in scope, as written where it is: the members of a `use` or of the context a function's type
        // declares. A `use` of a record value, which the type checker found to be the instance by its shape, binds
        // its members by their names alone.
        | Some (memberName, Some (InstanceInScope written)) ->
            let writtenKey = contextMemberKey (Some written) memberName
            let key = if not (scopeKeys.Contains writtenKey) && scopeKeys.Contains (NamedMember memberName) then NamedMember memberName else writtenKey
            acc @ [ memberFor requiredKey key ], used.Add key
        // An instance built from the attached definitions: each member is one of them, a function passed as a closure
        // over its wrapper, or a value passed as it is.
        | Some (memberName, Some (InstanceSynthesized members)) ->
            let definition = members |> List.find (fun (n, _) -> n = memberName) |> snd
            let argument =
                match functionWrappers |> Map.tryFind definition with
                | Some wrapper -> CoreIrLowerInput.ClosureOverWrapper wrapper
                | None -> CoreIrLowerInput.DefinitionValue definition
            acc @ [ argument ], used
        // An instance derived from the parts of a type with variables in it: a closure over its definition and the
        // instances the definition requires.
        | Some (_, Some (InstanceDerived(_, definition, at))) ->
            match functionWrappers |> Map.tryFind definition with
            | Some wrapper -> acc @ [ CoreIrLowerInput.DerivedClosure(wrapper, at) ], used
            | None -> CoreIr.internalError $"the derived definition '{definition}' has no wrapper"
        | _ ->
            let resolvedKey =
                if scopeKeys.Contains requiredKey then requiredKey
                else findCompatible used requiredKey |> Option.defaultValue requiredKey
            acc @ [ memberFor requiredKey resolvedKey ], used.Add resolvedKey) ([], Set.empty)
    |> fst

let private extractTypeLayouts (module': Module) : Map<string, string list> * Map<string, Map<string, string>> =
    let rec typeExprToTypeString (t: TypeExpr) : string =
        match t.Kind with
        | TypeName name -> name
        | TypeFunc(param, ret) -> $"({typeExprToTypeString param}) -> ({typeExprToTypeString ret})"
        | TypeUnit -> "()"
        | TypeTuple items -> items |> List.map typeExprToTypeString |> String.concat ", " |> sprintf "(%s)"
        | _ -> "unknown"
    let results =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, Kind (TypeRecord fields)))) ->
                let fieldInfo =
                    fields
                    |> List.choose (function
                        | TypeField(fieldName, _, _, typeOpt, _) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName)
                        | TypeMember(fieldName, typeOpt) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName))
                let sortedFieldNames = fieldInfo |> List.map fst |> List.sort
                let fieldTypeMap = fieldInfo |> Map.ofList
                Some (name, sortedFieldNames, fieldTypeMap)
            | _ -> None)
    let layouts = results |> List.map (fun (n, fields, _) -> (n, fields)) |> Map.ofList
    let fieldTypes = results |> List.map (fun (n, _, types) -> (n, types)) |> Map.ofList
    layouts, fieldTypes

/// The members of each context without parameters in `typeDefs`, each with its key and declared type, in the order
/// `typeLayouts` stores them (see `CoreIrLowerTypes.ContextLayouts`).
let private contextLayoutsOf (typeDefs: Map<string, TypeDefInfo>) (typeLayouts: Map<string, string list>) : CoreIrLowerTypes.ContextLayouts =
    typeDefs
    |> Map.filter (fun _ info -> info.IsContext && info.Parameters.IsEmpty)
    |> Map.toList
    |> List.choose (fun (name, info) ->
        match Unifier.unalias info.Underlying, typeLayouts |> Map.tryFind name with
        | TyRecord members, Some layout ->
            Some (name, layout |> List.choose (fun memberName -> members |> Map.tryFind memberName |> Option.map (fun ty -> contextMemberKey None memberName, ty)))
        | _ -> None)
    |> Map.ofList

/// The functions a module was compiled into, from the core IR (see "Core IR" in `docs/src/content/work-in-progress/memory-management.md`).
type BackendReport =
    { /// The functions, by their names in WebAssembly text, specialisations and the start function included.
      Functions: string list
      /// The specialisations among them, each with the definition it specialises.
      Specialisations: (string * string) list }

/// `typed` as WebAssembly text compiled with `options`, and the functions it was compiled into.
let transpileTypedModuleWithOptions (options: WasmOptions) (typed: TypedModule) : string * BackendReport =
    let contextInstances = typed.ContextInstances
    let checkedTypes = typed.ExprTypes
    let module' = typed.Module
    // The definitions synthesized instances are made of are passed as function values.
    let synthesizedMembers =
        contextInstances
        |> Map.values
        |> Seq.collect (List.choose (function
            | Some (InstanceSynthesized members) -> Some (List.map snd members)
            | Some (InstanceDerived(_, definition, _)) -> Some [ definition ]
            | _ -> None) >> List.concat)
        |> Set.ofSeq
    // A lambda lifted out of a function that requires contexts captures the function's members, by the names of the
    // parameters they come in, so that it can call them and pass them on.
    let memberCaptures =
        let defs = module' |> List.choose (function Kind (Def (ValueDef(_, name, typeOpt, expr))) -> Some(name, typeOpt, expr) | _ -> None)
        contextMemberKeys defs (fst (extractTypeLayouts module')) |> Map.map (fun _ keys -> keys |> List.map contextMemberParameter)
    // The members of each context without parameters, as a function value that requires the context takes them.
    let contextLayouts: CoreIrLowerTypes.ContextLayouts = contextLayoutsOf typed.TypeDefs (fst (extractTypeLayouts module'))
    // A lambda is passed the members of the contexts its type requires at each call, so it does not capture them.
    let passedMembers (lambda: Expression) =
        match checkedTypes |> Map.tryFind lambda.Id with
        | Some (TyFunc(_, _, contexts)) ->
            match CoreIrLowerTypes.toIrTypeIn contextLayouts (TyFunc(TyPrimitive "unit", TyPrimitive "unit", contexts)) with
            | CoreIr.IrFunction(_, _, members) -> members |> List.map fst |> Set.ofList
            | _ -> Set.empty
        | _ -> Set.empty
    let module', liftedOrigins, functionWrappers, useKeys = LambdaLifting.lift synthesizedMembers memberCaptures passedMembers typed.UseContexts module'
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

    let nominalWrappers =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, body))) when isNominalTypeName name && (match body.Kind with TypeRecord _ -> false | _ -> true) -> Some name
            | _ -> None)
        |> Set.ofList

    // The contexts each function's annotation requires (`TypeWithContext`), and the keys of their members (see
    // `contextMemberKeys`).
    let functionContextTypes =
        defs
        |> List.choose (fun (name, typeOpt, _) ->
            match extractContextTypes typeOpt with
            | [] -> None
            | contextTypes -> Some (name, contextTypes))
        |> Map.ofList

    let functionContextRequirements = contextMemberKeys defs typeLayouts

    // The ambient context instance: a module-level function named like a member of a declared context stands
    // in for that member whenever a callee requires the context and the caller has nothing else to supply.
    // This is how the runtime-provided `Console` (the functions `nanyx/console` exports) reaches `@entry`.
    // Closure conversion made a wrapper for each such function, so the member is a closure over it.
    // Lambda lifting made those wrappers only where something may need them.
    let ambientContextMembers =
        LambdaLifting.ambientMemberDefinitions module'
        |> List.filter (fun (_, definition) -> not (functionContextRequirements.ContainsKey definition))
        |> List.choose (fun (memberName, definition) -> functionWrappers |> Map.tryFind definition |> Option.map (fun wrapper -> NamedMember memberName, wrapper))
        |> Map.ofList

    let functionTable: FunctionTable =
        { Values = System.Collections.Generic.Dictionary<string, ContextFn>()
          NextSlot = 0
          IndirectArities = System.Collections.Generic.HashSet<int>() }

    let recordTable: RecordTable =
        { LayoutIds = System.Collections.Generic.Dictionary<string list, int>() }

    let usedHelpers = System.Collections.Generic.HashSet<string>()

    /// The other names a function can call the members of the contexts it requires by, each with the key it stands
    /// for: a member of a parameterised context (`show` of `Show(a)`) by its own name (`show`), as the type checker
    /// allows, when no other context the function declares has a member of that name.
    let contextMemberAliases (name: string) : (string * ContextMemberKey) list =
        let keys = functionContextRequirements |> Map.tryFind name |> Option.defaultValue []
        functionContextTypes
        |> Map.tryFind name
        |> Option.defaultValue []
        |> List.collect (fun ctxType ->
            match ctxType.Kind with
            | TypeApply(baseName, _) ->
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue []
                |> List.map (fun memberName -> memberName, contextMemberKey (Some ctxType) memberName)
            | _ -> [])
        |> List.groupBy fst
        |> List.choose (fun (memberName, aliased) ->
            match aliased with
            | [ (_, key) ] when List.contains key keys && not (List.contains (NamedMember memberName) keys) -> Some (memberName, key)
            | _ -> None)

    /// The members of the contexts `name` requires, as the core IR takes them: each with its type, from the context's
    /// definition with its parameters as the function's annotation gives them, where that can be worked out.
    let coreIrContextParameters (typeDefs: Map<string, TypeDefInfo>) (definitionTypes: Map<string, Ty>) (name: string) =
        // The function's own type variables, by the names its annotation gives them.
        let rec namedVars (ty: Ty) : (string * Ty) list =
            match ty with
            | TyVar { Name = Some varName } -> [ varName, ty ]
            | TyVar _ | TyPrimitive _ -> []
            | TyNominal(_, underlying, _) -> namedVars underlying
            | TyFunc(input, output, _) -> namedVars input @ namedVars output
            | TyRecord fields -> fields |> Map.toList |> List.collect (snd >> namedVars)
            | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> namedVars)
            | TyTag(_, payload) -> payload |> Option.map namedVars |> Option.defaultValue []
            | TyApply(_, args) -> args |> List.collect namedVars
            | TyUnion items -> items |> List.collect namedVars
            | TyAlias(_, args, underlying) -> (args |> List.collect namedVars) @ namedVars underlying
        // The type variables the annotation writes, each matched to the one in the same place in the definition's
        // type: inference may have merged a written variable with another and dropped its name.
        let rec matchAnnotation (annotation: TypeExpr) (ty: Ty) : (string * Ty) list =
            match annotation.Kind, Unifier.unalias ty with
            | (TypeName varName | TypeVar varName), TyVar _ -> [ varName, ty ]
            | TypeFunc(input, output), TyFunc(input', output', _) -> matchAnnotation input input' @ matchAnnotation output output'
            // A function type written with the contexts it requires, `[c] a -> b`.
            | TypeWithContext(_, inner), _ -> matchAnnotation inner ty
            | TypeTuple items, TyRecord fields ->
                items |> List.mapi (fun index item -> fields |> Map.tryFind (string index) |> Option.map (matchAnnotation item) |> Option.defaultValue []) |> List.concat
            | TypeApply(applied, items), TyApply(applied', items') when applied = applied' && items.Length = items'.Length ->
                List.map2 matchAnnotation items items' |> List.concat
            | TypeRecord written, TyRecord fields ->
                written
                |> List.collect (function
                    | TypeField(fieldName, _, _, Some fieldType, _) ->
                        fields |> Map.tryFind fieldName |> Option.map (matchAnnotation fieldType) |> Option.defaultValue []
                    | _ -> [])
            // A tag's payload, and each tag of a union by its name, as the union's tags need not be in the same order.
            | TypeTag(tag, Some payload), TyTag(tag', Some payload') when tag = tag' -> matchAnnotation payload payload'
            | TypeUnion written, TyUnion items ->
                written
                |> List.collect (fun item ->
                    match item.Kind with
                    | TypeTag(tag, Some _) ->
                        items
                        |> List.tryFind (function TyTag(tag', Some _) -> tag = tag' | _ -> false)
                        |> Option.map (matchAnnotation item)
                        |> Option.defaultValue []
                    | _ -> [])
            | _ -> []
        let annotated =
            match defs |> List.tryFind (fun (defName, _, _) -> defName = name), definitionTypes |> Map.tryFind name with
            | Some (_, typeOpt, _), Some ty ->
                match unwrapContextType typeOpt with
                | Some annotation -> matchAnnotation annotation ty
                | None -> []
            | _ -> []
        let vars =
            (definitionTypes |> Map.tryFind name |> Option.map namedVars |> Option.defaultValue []) @ annotated |> Map.ofList
        let argumentTy (argument: TypeExpr) =
            match argument.Kind with
            | TypeName ("int" | "string" | "bool" | "unit" | "float" as primitive) -> Some (TyPrimitive primitive)
            | TypeName varName
            | TypeVar varName -> vars |> Map.tryFind varName
            | _ -> None
        let memberTypes =
            functionContextTypes
            |> Map.tryFind name
            |> Option.defaultValue []
            |> List.collect (fun ctxType ->
                let baseName, arguments =
                    match ctxType.Kind with
                    | TypeApply(baseName, arguments) -> baseName, arguments
                    | TypeName baseName -> baseName, []
                    | _ -> "unknown", []
                let memberTy (memberName: string) =
                    match typeDefs |> Map.tryFind baseName with
                    | Some info when info.Parameters.Length = arguments.Length ->
                        let argumentTys = arguments |> List.map argumentTy
                        if argumentTys |> List.forall Option.isSome then
                            let parameters = List.zip (info.Parameters |> List.map _.Id) (argumentTys |> List.map Option.get) |> Map.ofList
                            let rec bind (ty: Ty) =
                                match ty with
                                | TyVar v when parameters.ContainsKey v.Id -> parameters.[v.Id]
                                | TyFunc(input, output, contexts) -> TyFunc(bind input, bind output, contexts)
                                | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> bind))
                                | TyTag(tag, payload) -> TyTag(tag, payload |> Option.map bind)
                                | TyApply(applied, args) -> TyApply(applied, args |> List.map bind)
                                | TyUnion items -> TyUnion(items |> List.map bind)
                                | TyAlias(alias, args, underlying) -> TyAlias(alias, args |> List.map bind, bind underlying)
                                | other -> other
                            match Unifier.unalias info.Underlying with
                            | TyRecord members -> members |> Map.tryFind memberName |> Option.map bind
                            | _ -> None
                        else None
                    | _ -> None
                typeLayouts |> Map.tryFind baseName |> Option.defaultValue []
                |> List.map (fun memberName -> contextMemberKey (Some ctxType) memberName, memberTy memberName))
            |> Map.ofList
        let parameters =
            functionContextRequirements.[name]
            |> List.map (fun key ->
                { CoreIrLowerInput.ContextParameter.Key = key
                  CoreIrLowerInput.ContextParameter.Name = contextMemberParameter key
                  CoreIrLowerInput.ContextParameter.Type = memberTypes |> Map.tryFind key |> Option.flatten })
        parameters, contextMemberAliases name |> Map.ofList

    // A specialisation is only called by the functions compiled with it; a definition that is not generic is exported.
    let emitFromCoreIr (context: CoreIrEmit.EmitContext) (func: CoreIr.Function) =
        let exportName = if func.TypeArguments.IsEmpty then Some func.Source else None
        CoreIrEmit.emitFunction context func exportName

    // Every definition that is not generic, the start function, and the specialisations and lifted functions they
    // need. Each function is emitted once here only to find out whether it can be (tags, strings and layouts get
    // numbers of no consequence), and again below, in order, for real.
    let probeContext: CoreIrEmit.EmitContext =
        { TagId = (fun _ -> 0)
          StringOffset = (fun _ -> 0)
          RecordLayoutId = (fun _ -> 0)
          FunctionSlot = (fun _ _ -> 0)
          IndirectCall = ignore }
    let lowered =
        let input: CoreIrLowerInput.LowerInput =
            { Definitions = defs |> List.map (fun (name, _, expr) -> name, expr)
              ExprTypes = typed.InlinedExprTypes |> Map.fold (fun acc id ty -> acc |> Map.add id ty) checkedTypes
              CalleeTypes = typed.InlinedCalleeTypes |> Map.fold (fun acc id ty -> acc |> Map.add id ty) typed.CalleeTypes
              DefinitionTypes = typed.Types
              ContextParameters =
                functionContextRequirements
                |> Map.map (fun name _ -> coreIrContextParameters typed.TypeDefs typed.Types name)
              ContextArguments =
                fun name callId scopeKeys passed ->
                    try
                        Ok (contextArguments
                                functionContextRequirements functionContextTypes typeLayouts contextInstances functionWrappers
                                ambientContextMembers scopeKeys passed name callId)
                    with Failure message -> Error message
              ContextLayouts = contextLayouts
              AmbientMembers = ambientContextMembers
              UseKeys = useKeys
              BuiltInHoles =
                contextInstances |> Map.filter (fun _ found -> found = [ Some InstanceBuiltIn ]) |> Map.keys |> Set.ofSeq
              RecordConstructors = typeLayouts.Keys |> Set.ofSeq
              NominalWrappers = nominalWrappers
              Lifted =
                [ yield! liftedOrigins |> Map.toList |> List.map (fun (name, origin) -> name, CoreIrLowerInput.LiftedLambda(origin.Lambda, origin.Captured, origin.Owner))
                  yield! functionWrappers |> Map.toList |> List.map (fun (wrapped, wrapper) -> wrapper, CoreIrLowerInput.FunctionWrapper wrapped) ]
                |> Map.ofList
              TopLevel = topLevelExprs
              NamedTypes =
                typed.TypeDefs
                |> Map.filter (fun _ info -> not info.IsContext)
                |> Map.map (fun _ info -> TyNominal(TypeDefInfo.nominalId info, info.Underlying, info.IsPrivate)) }
        CoreIrLowerModule.lowerModule input (emitFromCoreIr probeContext >> ignore)

    // The first thing that cannot be compiled is reported where it is written.
    match lowered.Failures with
    | (_, failure, span) :: _ ->
        let message = lowerFailureMessage failure
        match span with
        | Some span -> unsupportedAt span message
        | None -> failwith message
    | [] -> ()

    // Each definition that is not generic, followed by the specialisations of it: a generic definition has only those.
    // Then the adapters between function types that require different contexts (see `FunctionLowering.Coerce` in `CoreIrLower.fs`).
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

    // Reference counting, inserted over the whole module at once, since which parameters a function owns depends on
    // the functions it calls; then drop specialisation and reuse.
    let definitionFunctions, startFunction =
        let all = definitionFunctions @ Option.toList startFunction
        let counted = CoreIrCounting.insert (CoreIrCounting.inferOwnership all) all |> CoreIrReuse.insert
        List.take definitionFunctions.Length counted, startFunction |> Option.map (fun _ -> List.last counted)

    // The tags, strings and dbg forms of exactly the functions emitted below.
    let facts = CoreIrFacts.collect (definitionFunctions @ Option.toList startFunction)
    let tagIdMap = facts.Tags |> List.mapi (fun index name -> name, index + 1) |> Map.ofList
    // A checked module lays out what its memory checks write with its own literals.
    let literals =
        if options.CheckMemory then facts.Strings @ (MemoryChecks.messages |> List.except facts.Strings)
        else facts.Strings
    let stringLiteralMap, stringLiteralData = buildStringLiteralMap literals
    // The free-list heads and the heap go after the literals, however far they run.
    let heapBase = heapBaseAfter (stringDataEnd stringLiteralData)
    let memoryDirective = $"  (memory {initialMemoryPages (heapStartOffset heapBase)})"

    let context: CoreIrEmit.EmitContext =
        { TagId =
            fun name ->
                match tagIdMap |> Map.tryFind name with
                | Some id -> id
                | None -> CoreIr.internalError $"missing tag discriminant for #{name}"
          StringOffset =
            fun value ->
                match stringLiteralMap |> Map.tryFind value with
                | Some offset -> offset
                | None -> CoreIr.internalError $"missing string literal offset for {value}"
          RecordLayoutId = recordLayoutId recordTable
          FunctionSlot = functionValueSlot functionTable
          IndirectCall = functionTable.IndirectArities.Add >> ignore }

    /// The text of a function from the core IR, noting the helpers it calls and the record layouts it allocates.
    let fromCoreIr (func: CoreIr.Function) =
        let emitted = emitFromCoreIr context func
        for helper in emitted.Helpers do
            usedHelpers.Add helper |> ignore
        emitted.Text

    let renderedFunctions = definitionFunctions |> List.map fromCoreIr |> String.concat "\n\n"

    let renderedStartFunction, startDirective =
        match startFunction with
        | Some func ->
            let emitted = CoreIrEmit.emitStart context func
            for helper in emitted.Helpers do
                usedHelpers.Add helper |> ignore
            let directive =
                if options.CheckMemory then $"  (export \"{CheckedExports.start}\" (func ${CoreIrLowerInput.startFunctionName}))"
                else $"  (start ${CoreIrLowerInput.startFunctionName})"
            emitted.Text, directive
        | None -> "", ""

    // A drop and a dup function for each type on the heap the functions above use, a function for what each of their
    // closures captures, and `++` for each list of elements on the heap. A tag no function names has no cells to drop.
    let drops = CoreIrDrop.collect (definitionFunctions @ Option.toList startFunction)
    let renderedDrops = CoreIrDrop.render (fun name -> tagIdMap |> Map.tryFind name) drops |> String.concat "\n\n"
    for helper in CoreIrDrop.helpersUsed drops do
        usedHelpers.Add helper |> ignore
    if drops.Types |> List.exists (function CoreIr.IrFunction _ -> true | _ -> false) then
        functionTable.IndirectArities.Add 1 |> ignore

    // An equality function for each record, list and union of tags `==` compares, and for each of their parts that
    // needs one.
    let equalityTypes = CoreIrEquality.collect (definitionFunctions @ Option.toList startFunction)
    let renderedEqualities = CoreIrEquality.render (fun name -> tagIdMap |> Map.tryFind name) equalityTypes |> String.concat "\n\n"
    for helper in CoreIrEquality.helpersUsed equalityTypes do
        usedHelpers.Add helper |> ignore

    // An ordering function for each type the ordering intrinsic or an operator orders, and for each of its parts.
    let orderingTypes = CoreIrOrdering.collect (definitionFunctions @ Option.toList startFunction)
    let renderedOrderings = CoreIrOrdering.render orderingTypes |> String.concat "\n\n"
    for helper in CoreIrOrdering.helpersUsed orderingTypes do
        usedHelpers.Add helper |> ignore

    // A hash function for each type the hash intrinsic hashes, and for each of its parts.
    let hashingTypes = CoreIrHashing.collect (definitionFunctions @ Option.toList startFunction)
    let renderedHashes = CoreIrHashing.render (fun name -> tagIdMap |> Map.tryFind name) hashingTypes |> String.concat "\n\n"
    for helper in CoreIrHashing.helpersUsed hashingTypes do
        usedHelpers.Add helper |> ignore

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
    let usedHelperSet = closeHelpers Set.empty (usedHelpers |> Seq.toList)
    // Exiting is not a stream, but its WASI import is declared with theirs.
    let moduleUsesStandardStreams =
        [ Helpers.writeStdout; Helpers.writeStderr; Helpers.readStdinByte; Helpers.procExit ] |> List.exists usedHelperSet.Contains
    let moduleReadsStdin = usedHelperSet.Contains Helpers.readStdinByte
    // Anything that writes to stdout or stderr, or reads stdin, shares the WASI import, the memory and the
    // newline byte.
    let moduleUsesIoHelpers = facts.UsesDbgHelpers || moduleUsesStandardStreams
    // Everything that allocates does it through `$alloc`.
    let moduleUsesHeap = usedHelperSet.Contains Helpers.alloc
    let renderedHelpers =
        let helpers = if options.CheckMemory then checkedRuntimeHelpers heapBase else runtimeHelpers heapBase
        [ yield!
              helperDependencies
              |> List.map fst
              |> List.filter usedHelperSet.Contains
              |> List.map (fun name -> helpers |> Map.find name)
          if checksMemory then
              yield memoryCheckFunctions heapBase (fun message -> stringLiteralMap |> Map.find message)
              yield $"  (export \"{CheckedExports.liveBlocks}\" (global ${MemoryChecks.liveBlocks}))"
              yield $"  (export \"{CheckedExports.allocations}\" (global ${MemoryChecks.allocations}))"
          if options.CheckMemory && moduleUsesHeap then
              yield $"  (export \"{CheckedExports.heapPointer}\" (global $heap_ptr))" ]
        |> String.concat "\n"

    let dbgPrelude =
        if moduleUsesIoHelpers then
            [ "  (import \"wasi_snapshot_preview1\" \"fd_write\" (func $fd_write (param i32 i32 i32 i32) (result i32)))"
              if moduleReadsStdin then
                  "  (import \"wasi_snapshot_preview1\" \"fd_read\" (func $fd_read (param i32 i32 i32 i32) (result i32)))"
              if usedHelperSet.Contains Helpers.procExit then
                  "  (import \"wasi_snapshot_preview1\" \"proc_exit\" (func $proc_exit (param i32)))"
              memoryDirective
              "  (export \"memory\" (memory 0))"
              if facts.UsesDbgHelpers then
                  $"  (data (i32.const {dbgDataBaseOffset}) \"dbg: \")"
                  $"  (data (i32.const {dbgStrNewlineOffset}) \"\\0a\")"
                  yield! dbgFunctions ]
            |> String.concat "\n"
        else
            ""

    // Every function body has been rendered by now, so the function-value slots are final.
    let contextFnList = functionTable.Values.Values |> Seq.toList

    let contextTypeDefs =
        (contextFnList |> List.map ctxFnArity) @ (functionTable.IndirectArities |> Seq.toList)
        |> Set.ofList
        |> Set.toList
        |> List.sort
        |> List.map (fun arity ->
            let paramSig =
                [ for _ in 1 .. arity -> "i32" ]
                |> String.concat " "
            if String.IsNullOrWhiteSpace paramSig then
                $"  (type $ctx_fn_{arity} (func (result i32)))"
            else
                $"  (type $ctx_fn_{arity} (func (param {paramSig}) (result i32)))")
        |> String.concat "\n"

    // A `call_indirect` needs a table to validate, even in a module that never makes a closure to put in it
    // (a function that only calls the closures it is given).
    let contextTable =
        if contextFnList.IsEmpty && Seq.isEmpty functionTable.IndirectArities then
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

    let contextPrelude =
        [ contextTypeDefs; contextTable; contextElem ]
        |> List.filter (fun line -> not (String.IsNullOrWhiteSpace line))
        |> String.concat "\n"

    let dataSegments = renderDataSegments stringLiteralData

    // Add memory if we have string literals or any emitted function touches memory, but no dbg helpers (dbgPrelude
    // already includes memory). Reading the rendered text keeps this true of whatever the helpers and functions do.
    let functionsUseMemory =
        [ renderedHelpers; renderedFunctions; renderedDrops; renderedEqualities; renderedOrderings; renderedHashes; renderedStartFunction ]
        |> List.exists Wat.usesMemory
    let needsMemory =
        (not (List.isEmpty stringLiteralData) || functionsUseMemory) && not moduleUsesIoHelpers
    let memorySection =
        if needsMemory then
            memoryDirective + "\n  (export \"memory\" (memory 0))"
        else
            ""

    // Add heap global if we use heap allocation
    let heapSection =
        if moduleUsesHeap then
            $"  (global $heap_ptr (mut i32) (i32.const {heapStartOffset heapBase}))"
        else
            ""

    // Imports must precede every function definition in a WebAssembly module, so the prelude, which imports WASI's
    // functions, comes first.
    let allImports =
        [ if not (String.IsNullOrWhiteSpace dbgPrelude) then dbgPrelude
          if not (String.IsNullOrWhiteSpace memorySection) then memorySection
          if not (String.IsNullOrWhiteSpace heapSection) then heapSection
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
              if not (String.IsNullOrWhiteSpace renderedFunctions) then renderedFunctions
              if not (String.IsNullOrWhiteSpace renderedDrops) then renderedDrops
              if not (String.IsNullOrWhiteSpace renderedEqualities) then renderedEqualities
              if not (String.IsNullOrWhiteSpace renderedOrderings) then renderedOrderings
              if not (String.IsNullOrWhiteSpace renderedHashes) then renderedHashes
              if not (String.IsNullOrWhiteSpace renderedStartFunction) then renderedStartFunction
              if not (String.IsNullOrWhiteSpace startDirective) then startDirective ]
        let body = String.concat "\n\n" parts
        $"(module\n{body}\n)", report

/// `typed` as WebAssembly text, and the functions it was compiled into.
let transpileTypedModuleWithReport (typed: TypedModule) : string * BackendReport =
    transpileTypedModuleWithOptions WasmOptions.Default typed

let transpileTypedModuleToWat (typed: TypedModule) : string =
    transpileTypedModuleWithReport typed |> fst

/// `typed` as WebAssembly text compiled with `options`, with what the backend cannot compile reported as a diagnostic
/// at the place in the source it is written: the innermost expression it is in that has one, or else the definition.
let tryTranspileTypedModuleWith (options: WasmOptions) (typed: TypedModule) : Result<string, Diagnostic> =
    try
        Ok (transpileTypedModuleWithOptions options typed |> fst)
    with
    | UnsupportedConstruct(message, span) -> Error (Diagnostics.at DiagnosticCodes.UnsupportedByBackend span message)
    | CoreIr.InternalError reason -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend (internalErrorMessage reason))
    | Failure message -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend message)
    | ex -> Error (Diagnostics.general DiagnosticCodes.UnsupportedByBackend (internalErrorMessage ex.Message))

/// `tryTranspileTypedModuleWith` the default options.
let tryTranspileTypedModuleToWat (typed: TypedModule) : Result<string, Diagnostic> =
    tryTranspileTypedModuleWith WasmOptions.Default typed
