/// What a module needs besides its functions, worked out from the core IR it is emitted from: the tags it numbers,
/// the string literals in its data segment, and whether `dbg` prints.
module Transpiler.Wasm.CoreIrFacts

open System.Collections.Generic
open Transpiler.Wasm.CoreIr

type ModuleFacts =
    { /// Every tag the emitter asks the number of, sorted by name.
      Tags: string list
      /// Every string the emitter asks the data-segment offset of, in the order the functions' IR first names them.
      Strings: string list
      /// Whether some `dbg` prints through `$dbg_str`.
      UsesDbgHelpers: bool }

/// The facts of a module made of `functions`.
let collect (functions: Function list) : ModuleFacts =
    let tags = HashSet<string>()
    let strings = ResizeArray<string>()
    let usesDbgHelpers = ref false

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
        | PTag(name, payload) ->
            tags.Add name |> ignore
            payload |> Option.iter pattern
        | PString value -> strings.Add value
        | PFields fields -> fields |> List.iter (snd >> pattern)
        | PAll parts -> parts |> List.iter pattern
        | PGuard(_, guard) -> expr guard
        | PList(elements, rest) ->
            elements |> List.iter pattern
            rest |> Option.iter pattern
        | PWildcard
        | PInt _
        | PBool _
        | PBind _ -> ()

    and expr (e: Expr) =
        match e.Kind with
        | Atom a -> atom a
        | Prim(_, atoms)
        | Call(_, atoms)
        | MakeList atoms
        | CallRuntime(_, atoms, _)
        | MakeClosure(_, _, atoms)
        | CallClosure(_, atoms) -> atoms |> List.iter atom
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
            parts |> List.iter (function Hole(_, ty) -> holeType ty | Text _ -> ())
        | Dbg text ->
            atom text
            usesDbgHelpers.Value <- true
        | BoxFloat _ -> ()
        | Panic messages -> strings.AddRange messages
        | ForEach loop ->
            ForLoop.sourceAtoms loop |> List.iter atom
            expr loop.Body
        | While loop ->
            expr loop.Condition
            expr loop.Body
        | Let(var, value, body) when var.Kind = Temp ->
            deferred.[var.Id] <- value
            expr body
        | Let(_, value, body) ->
            expr value
            expr body
        | Do(first, rest) ->
            expr first
            expr rest
        | If(cond, ifTrue, ifFalse) ->
            atom cond
            expr ifTrue
            expr ifFalse
        | Match(_, arms) ->
            for arm in arms do
                arm.Patterns |> List.iter pattern
                expr arm.Body
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Release(_, body)
        | Reuse(_, body) -> expr body

    for func in functions do
        expr func.Body

    { Tags = tags |> Seq.sort |> List.ofSeq
      Strings = strings |> Seq.distinct |> List.ofSeq
      UsesDbgHelpers = usesDbgHelpers.Value }
