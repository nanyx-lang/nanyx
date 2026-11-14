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
open Transpiler.Wasm.CoreIrLowerKnown
open Transpiler.Wasm.CoreIrLowerScope
open Transpiler.Wasm.CoreIrLower

/// The most specialisations one definition may have: a definition that calls itself at ever bigger types
/// (polymorphic recursion) would otherwise have no end of them.
let private maxSpecialisations = 32

/// A WebAssembly name for a specialisation, such as `id__int`: its definition's name, then its type arguments, then
/// `__known` if it is specialised on known context members (see `CoreIrLowerKnown`).
let private specialisationName (key: SpecKey) =
    sanitizeIdentifier key.Definition
    + (key.TypeArguments |> List.map (IrType.mangle >> sanitizeIdentifier >> sprintf "__%s") |> String.concat "")
    + (if key.Known.IsEmpty then "" else "__known")

/// Lowers every definition that is not generic, the start function, and the specialisations and lifted functions they
/// need, and runs `finish` over each function (the emitter, which can still fail). A generic definition exists only as
/// its specialisations. What cannot be lowered or finished is in `Failures`, where it is written as far as is known.
let lowerModule (input: LowerInput) (finish: Function -> 'T) : Lowered<'T> =
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
                let defName = specKey.Definition
                let count = match counts.TryGetValue defName with | true, n -> n | _ -> 0
                if count >= maxSpecialisations then cannot $"more than {maxSpecialisations} specialisations of '{defName}'"
                counts.[defName] <- count + 1
                // A lifted function of a definition that is not generic keeps its own name.
                let candidate =
                    if specKey.TypeArguments.IsEmpty && specKey.Known.IsEmpty then sanitizeIdentifier defName
                    else
                        let candidate = freeName usedNames.Contains (specialisationName specKey)
                        usedNames.Add candidate |> ignore
                        candidate
                names.[specKey] <- candidate
                pending.Enqueue((specKey, bindings, lowering))
                candidate
        order.Add key
        let lowering = FunctionLowering(input, definitions, subst, key, request)
        let attempt () =
            try
                let func = lowering.Function how key.Definition functionName key.TypeArguments
                Ok (func, finish func)
            with
            | CannotLower reason -> Error (NotYet reason, lowering.CurrentSpan)
            | InternalError reason
            | Failure reason -> Error (Internal reason, lowering.CurrentSpan)
            | ex -> Error (Internal ex.Message, lowering.CurrentSpan)
        // A definition that holds errors deferred to run time may be what cannot be lowered because of them (a value of
        // no known type to interpolate, say): it reports them as soon as it is called instead.
        let messages =
            match definitions |> Map.tryFind key.Definition with
            | Some expr when how <> AsStart -> deferredMessages expr
            | _ -> []
        results.[key] <-
            match attempt () with
            | Error _ when not messages.IsEmpty ->
                let trap = FunctionLowering(input, definitions, subst, key, request)
                try
                    let func = trap.Trap key.Definition functionName key.TypeArguments messages
                    Ok (func, finish func)
                with
                | CannotLower reason -> Error (NotYet reason, None)
                | InternalError reason
                | Failure reason -> Error (Internal reason, None)
            | result -> result

    // Lowers `key`, then the specialisations and lifted functions it asks for, and those they ask for.
    let lowerWithRequests (key: SpecKey) (subst: Map<int, Ty>) (how: Lowering) (functionName: string) =
        lowerOne key subst how functionName
        while pending.Count > 0 do
            let key, bindings, how = pending.Dequeue()
            lowerOne key bindings how names.[key]

    let isGeneric (name: string) =
        match input.DefinitionTypes |> Map.tryFind name with
        | Some ty -> not (freeVars ty).IsEmpty
        | None -> false

    if not input.TopLevel.IsEmpty then
        lowerWithRequests (SpecKey.plain startFunctionName []) Map.empty AsStart startFunctionName

    // A generic definition is only lowered at the type arguments it is used at, and a lifted function where a closure
    // over it is made: what it captures is known there.
    for (name, _) in input.Definitions do
        if not (input.Lifted.ContainsKey name) && not (isGeneric name) then
            lowerWithRequests (SpecKey.plain name []) Map.empty AsDefined (sanitizeIdentifier name)

    // A definition may be generic only because an error left its type open. Unless it was specialised, it is lowered
    // with its type variables defaulted, so that it still exists, and reports its errors when it is called.
    for (name, expr) in input.Definitions do
        let specialised = names.Keys |> Seq.exists (fun key -> key.Definition = name)
        if not (input.Lifted.ContainsKey name) && isGeneric name && not specialised && not (deferredMessages expr).IsEmpty then
            lowerWithRequests (SpecKey.plain name []) Map.empty AsDefined (sanitizeIdentifier name)

    let lowered =
        order
        |> Seq.distinct
        |> Seq.choose (fun key -> match results.[key] with Ok lowered -> Some (key, lowered) | Error _ -> None)
        |> List.ofSeq
    let isRoot (key: SpecKey) = key.TypeArguments.IsEmpty && key.Known.IsEmpty
    { Roots =
        lowered
        |> List.filter (fst >> isRoot)
        |> List.map (fun (key, lowered) -> key.Definition, lowered)
        |> Map.ofList
      Specialisations =
        lowered
        |> List.filter (fst >> isRoot >> not)
        |> List.groupBy (fun (key, _) -> key.Definition)
        |> List.map (fun (name, items) -> name, items |> List.map snd)
        |> Map.ofList
      Failures =
        order
        |> Seq.distinct
        |> Seq.choose (fun key ->
            match results.[key] with
            | Error (reason, span) -> Some (key.Definition, reason, span |> Option.orElse (definitions |> Map.tryFind key.Definition |> Option.map _.Span))
            | Ok _ -> None)
        |> List.ofSeq }
