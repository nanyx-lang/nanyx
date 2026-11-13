/// Lowers a whole module to the core IR: each definition that is not generic, the start function, and the
/// specialisations and lifted functions they ask for, one at a time from a queue (see `CoreIrLower.FunctionLowering`).
module Transpiler.Wasm.CoreIrLowerModule

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerSyntax
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLower

/// The most specialisations one definition may have: a definition that calls itself at ever bigger types
/// (polymorphic recursion) would otherwise have no end of them.
let private maxSpecialisations = 32

/// A WebAssembly name for `name` specialised at `typeArguments`, such as `id__int`.
let private specialisationName (name: string) (typeArguments: IrType list) =
    sanitizeIdentifier name + (typeArguments |> List.map (IrType.mangle >> sanitizeIdentifier >> sprintf "__%s") |> String.concat "")

/// Lowers every definition that is not generic, the start function, and the specialisations and lifted functions they
/// need, and runs `finish` over each function (the emitter, which can still fail). A generic definition exists only as
/// its specialisations. What cannot be lowered or finished is in `Failures`, where it is written as far as is known.
let private lowerModuleOnThisThread (input: LowerInput) (finish: Function -> 'T) : Lowered<'T> =
    let definitions = input.Definitions |> Map.ofList
    let names = System.Collections.Generic.Dictionary<SpecKey, string>()
    let usedNames = System.Collections.Generic.HashSet<string>(input.Definitions |> Seq.map (fst >> sanitizeIdentifier))
    let pending = System.Collections.Generic.Queue<SpecKey * Map<int, Ty> * Lowering>()
    let counts = System.Collections.Generic.Dictionary<string, int>()
    // Every function lowered, by its key, with the specialisations it calls; or why it could not be.
    let results = System.Collections.Generic.Dictionary<SpecKey, Result<Function * 'T, LowerFailure * SourceSpan option>>()
    let order = ResizeArray<SpecKey>()

    let lowerOne (key: SpecKey) (subst: Map<int, Ty>) (how: Lowering) (functionName: string) =
        let request (specKey: SpecKey) (bindings: Map<int, Ty>) (lowering: Lowering) =
            match names.TryGetValue specKey with
            | true, existing -> existing
            | _ ->
                let defName, typeArguments = specKey
                let count = match counts.TryGetValue defName with | true, n -> n | _ -> 0
                if count >= maxSpecialisations then cannot $"more than {maxSpecialisations} specialisations of '{defName}'"
                counts.[defName] <- count + 1
                // A lifted function of a definition that is not generic keeps its own name.
                let candidate =
                    if typeArguments.IsEmpty then sanitizeIdentifier defName
                    else
                        let baseName = specialisationName defName typeArguments
                        let mutable candidate = baseName
                        let mutable index = 1
                        while usedNames.Contains candidate do
                            candidate <- $"{baseName}_{index}"
                            index <- index + 1
                        usedNames.Add candidate |> ignore
                        candidate
                names.[specKey] <- candidate
                pending.Enqueue((specKey, bindings, lowering))
                candidate
        order.Add key
        let lowering = FunctionLowering(input, definitions, subst, key, request)
        let attempt () =
            try
                let func = lowering.Function how (fst key) functionName (snd key)
                Ok (func, finish func)
            with
            | CannotLower reason -> Error (NotYet reason, lowering.CurrentSpan)
            | InternalError reason
            | Failure reason -> Error (Internal reason, lowering.CurrentSpan)
            | ex -> Error (Internal ex.Message, lowering.CurrentSpan)
        // A definition that holds errors deferred to run time may be what cannot be lowered because of them (a value of
        // no known type to interpolate, say): it reports them as soon as it is called instead.
        let messages =
            match definitions |> Map.tryFind (fst key) with
            | Some expr when how <> AsStart -> deferredMessages expr
            | _ -> []
        results.[key] <-
            match attempt () with
            | Error _ when not messages.IsEmpty ->
                let trap = FunctionLowering(input, definitions, subst, key, request)
                try
                    let func = trap.Trap (fst key) functionName (snd key) messages
                    Ok (func, finish func)
                with
                | CannotLower reason -> Error (NotYet reason, None)
                | InternalError reason
                | Failure reason -> Error (Internal reason, None)
            | result -> result

    let isGeneric (name: string) =
        match input.DefinitionTypes |> Map.tryFind name with
        | Some ty -> not (freeVars ty).IsEmpty
        | None -> false

    if not input.TopLevel.IsEmpty then
        lowerOne (startFunctionName, []) Map.empty AsStart startFunctionName
        while pending.Count > 0 do
            let key, bindings, how = pending.Dequeue()
            lowerOne key bindings how names.[key]

    // A generic definition is only lowered at the type arguments it is used at, and a lifted function where a closure
    // over it is made: what it captures is known there.
    for (name, _) in input.Definitions do
        if not (input.Lifted.ContainsKey name) && not (isGeneric name) then
            lowerOne (name, []) Map.empty AsDefined (sanitizeIdentifier name)
        while pending.Count > 0 do
            let key, bindings, how = pending.Dequeue()
            lowerOne key bindings how names.[key]

    // A definition may be generic only because an error left its type open. Unless it was specialised, it is lowered
    // with its type variables defaulted, so that it still exists, and reports its errors when it is called.
    for (name, expr) in input.Definitions do
        let specialised = names.Keys |> Seq.exists (fun (defName, _) -> defName = name)
        if not (input.Lifted.ContainsKey name) && isGeneric name && not specialised && not (deferredMessages expr).IsEmpty then
            lowerOne (name, []) Map.empty AsDefined (sanitizeIdentifier name)
            while pending.Count > 0 do
                let key, bindings, how = pending.Dequeue()
                lowerOne key bindings how names.[key]

    let lowered =
        order
        |> Seq.distinct
        |> Seq.choose (fun key -> match results.[key] with Ok lowered -> Some (key, lowered) | Error _ -> None)
        |> List.ofSeq
    { Roots =
        lowered
        |> List.filter (fun ((_, typeArguments), _) -> typeArguments.IsEmpty)
        |> List.map (fun ((name, _), lowered) -> name, lowered)
        |> Map.ofList
      Specialisations =
        lowered
        |> List.filter (fun ((_, typeArguments), _) -> not typeArguments.IsEmpty)
        |> List.groupBy (fun ((name, _), _) -> name)
        |> List.map (fun (name, items) -> name, items |> List.map snd)
        |> Map.ofList
      Failures =
        order
        |> Seq.distinct
        |> Seq.choose (fun ((name, _) as key) ->
            match results.[key] with
            | Error (reason, span) -> Some (name, reason, span |> Option.orElse (definitions |> Map.tryFind name |> Option.map _.Span))
            | Ok _ -> None)
        |> List.ofSeq }

/// The lowering itself recurses only as deep as an expression nests, but the passes `finish` runs over each function
/// (reference counting, reuse, emitting) recurse once per `let` of a function's body, so it runs on a large stack.
let lowerModule (input: LowerInput) (finish: Function -> 'T) : Lowered<'T> =
    Nanyx.LargeStack.run (fun () -> lowerModuleOnThisThread input finish)
