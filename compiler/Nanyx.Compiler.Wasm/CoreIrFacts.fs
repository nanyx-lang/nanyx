/// What a module needs besides its functions, worked out from the core IR it is emitted from: the tags it numbers,
/// the string literals and static closures in its data segment, the foreign functions it imports, whether `dbg` prints,
/// whether its host keeps state in memory, and whether it has debug probes.
module Transpiler.Wasm.CoreIrFacts

open System.Collections.Generic
open Transpiler.Wasm.CoreIr

type ModuleFacts =
    { /// Every tag the emitter asks the number of, sorted by name.
      Tags: string list
      /// Every string the emitter asks the data-segment offset of, in the order the functions' IR first names them.
      Strings: string list
      /// Every closure that captures nothing (see `AStaticClosure`), by its function's name and arity, in the order the
      /// functions' IR first names them.
      StaticClosures: (string * int) list
      /// Every foreign function a call is made to (see `CallImport`), in the order the functions' IR first names them.
      Imports: NyxCompiler.Extern list
      /// Whether some `dbg` prints, through `$dbg_str` or the runtime's `nanyx:debug` `dbg`.
      UsesDbgHelpers: bool
      /// Whether some function reads the address of the block the module sets aside for its host.
      UsesHostState: bool
      /// Whether some function writes a float or an `F32` as text, which needs the scratch block the module sets aside
      /// for it (see `FloatText.scratchBytes`).
      UsesFloatText: bool
      /// Whether a function reads the table of powers of five, so that the module needs it in its data (see
      /// `PowersOfFive`).
      UsesPowersOfFive: bool
      /// Whether some function has a debug probe (see `Probe`), which imports `nanyx:debug`.
      UsesProbes: bool }

/// The facts of a module made of `functions`.
let collect (functions: Function list) : ModuleFacts =
    let tags = HashSet<string>()
    let strings = ResizeArray<string>()
    let staticClosures = ResizeArray<string * int>()
    let imports = ResizeArray<NyxCompiler.Extern>()
    let usesDbgHelpers = ref false
    let usesHostState = ref false
    let usesFloatText = ref false
    let usesPowersOfFive = ref false
    let usesProbes = ref false

    // A temporary's value is emitted where the temporary is used, not where it is bound, so it is looked at there too:
    // the strings are then in the order the code uses them.
    let deferred = Dictionary<int, Expr>()

    let holeType (ty: HoleType) =
        match ty with
        | HoleTag cases -> cases |> List.iter (fst >> tags.Add >> ignore)
        | HoleString
        | HoleInt
        | HoleSized _
        | HoleBool
        | HoleFloat -> ()

    let rec atom (a: Atom) =
        match a with
        | AString value -> strings.Add value
        | ATag name -> tags.Add name |> ignore
        | AStaticClosure(name, arity, _) -> staticClosures.Add((name, arity))
        | AVar v when v.Kind = Temp ->
            match deferred.TryGetValue v.Id with
            | true, value ->
                deferred.Remove v.Id |> ignore
                expr value
            | _ -> ()
        | AVar _
        | AInt _
        | ASized _
        | ABool _
        | AUnit -> ()

    and pattern (p: Pattern) =
        match p with
        | PTag(name, _) ->
            tags.Add name |> ignore
            Pattern.parts p |> List.iter pattern
        | PString value -> strings.Add value
        | PGuard(_, guard) -> expr guard
        | _ -> Pattern.parts p |> List.iter pattern

    /// What `e` names itself, not counting the expressions inside it.
    and own (e: Expr) =
        match e.Kind with
        | Atom a -> atom a
        | CallRuntime(name, _, _) when name = NyxCompiler.Intrinsics.hostState -> usesHostState.Value <- true
        | CallRuntime(name, atoms, _) when name = WasmRuntime.Helpers.floatToString || name = WasmRuntime.Helpers.f32ToString ->
            usesFloatText.Value <- true
            atoms |> List.iter atom
        | CallRuntime(name, atoms, _) when name = PowersOfFive.helper ->
            usesPowersOfFive.Value <- true
            atoms |> List.iter atom
        | Prim(_, atoms)
        | Call(_, atoms)
        | MakeList atoms
        | CallRuntime(_, atoms, _)
        | MakeClosure(_, _, atoms)
        | CallClosure(_, atoms, _) -> atoms |> List.iter atom
        | CopyArrayInto(target, at, source) -> [ target; at; source ] |> List.iter atom
        | CallImport(declared, atoms) ->
            imports.Add declared
            atoms |> List.iter atom
        | MakeTag(name, payload) ->
            tags.Add name |> ignore
            atom payload
        | MakeRecord fields -> fields |> List.iter (snd >> atom)
        | Field(record, _) -> atom record
        | Assign(_, value)
        | SetField(_, _, value) -> atom value
        | UpdateRecord(record, updates) ->
            atom record
            updates |> List.iter (snd >> atom)
        | Interpolate parts ->
            // A literal part the string copies is a string literal (see `Interpolation.textWrite`).
            parts
            |> List.iter (function
                | Hole(_, ty) -> holeType ty
                | Text text when Interpolation.copiesText text -> strings.Add text
                | Text _ -> ())
        // The file's name is passed to the runtime, and what the program writes before the value written if the runtime
        // leaves it to the program (see `CoreIrEmit`).
        | Dbg(site, text) ->
            strings.Add site.File
            strings.Add site.Prefix
            atom text
            usesDbgHelpers.Value <- true
        | Crash(messages, _) ->
            strings.AddRange messages
            // What `Helpers.crashed` is given: the messages as one.
            strings.Add(String.concat "" messages)
        | CrashWith message -> atom message
        // A local's name is written beside its text, from the data segment.
        | Probe(_, locals) ->
            usesProbes.Value <- true
            locals |> List.iter (fun local -> strings.Add local.Name)
        | ForEach loop -> ForLoop.sourceAtoms loop |> List.iter atom
        | If(cond, _, _) -> atom cond
        | _ -> ()

    // What the expression names itself, then the expressions inside it (see `Expr.children`), the last in tail
    // position, so that a chain as long as a function takes no stack to walk.
    and expr (e: Expr) =
        match e.Kind with
        | Let(var, value, body) when var.Kind = Temp ->
            deferred.[var.Id] <- value
            expr body
        // A pattern's strings are named in their places among its guards, so a match walks its arms' patterns itself.
        | Match(_, arms) ->
            for arm in arms do
                arm.Patterns |> List.iter pattern
                expr arm.Body
        | _ ->
            own e
            match Expr.children e with
            | [] -> ()
            | children ->
                for child in List.take (children.Length - 1) children do
                    expr child
                expr (List.last children)

    for func in functions do
        expr func.Body

    { Tags = tags |> Seq.sort |> List.ofSeq
      Strings = strings |> Seq.distinct |> List.ofSeq
      StaticClosures = staticClosures |> Seq.distinct |> List.ofSeq
      Imports = imports |> Seq.distinct |> List.ofSeq
      UsesDbgHelpers = usesDbgHelpers.Value
      UsesHostState = usesHostState.Value
      UsesFloatText = usesFloatText.Value
      UsesPowersOfFive = usesPowersOfFive.Value
      UsesProbes = usesProbes.Value }
