/// The boxes a context member's own type variables are held in (`IrBox`), and how the lowering (see `CoreIrLower`)
/// converts a value to and from a type with them.
///
/// A context without parameters whose member is generic in type variables of its own (`ret: a -> #some(a) | r` in
/// `Proc`) has the member's value made once, where its instance is, and each call has those variables at the types it
/// has: `ret(1)` at int, `ret("s")` at string. So the member's type has their values boxed (see
/// `CoreIrLowerTypes.boxOwnVariables`), and the value of the instance is lowered with them boxed too. A box is a
/// closure block over a function that returns the value, so it is passed, kept and dropped the same way whatever the
/// value is. A call boxes what it passes and unboxes what it is given back, and a function it passes or is given is
/// adapted to do the same (see `CoreIrLowerMembers.coerce`).
module internal Transpiler.Wasm.CoreIrLowerBoxes

open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

/// Whether a value of `given` must be converted to be a value of `expected`: somewhere in it, one of them has a value
/// boxed where the other does not. Types that differ only in the tags of their unions, or in a function's context
/// members, hold their values alike: a function's members are `CoreIrLowerMembers.coerce`'s to adapt.
/// A recursive type, as a map's store is, is looked into once: inside itself, it holds what it holds already.
let rec private needsConversionSeen (seen: Set<IrType * IrType>) (given: IrType) (expected: IrType) : bool =
    let needsConversion = needsConversionSeen seen
    match given, expected with
    | _ when given = expected -> false
    | IrBox, _
    | _, IrBox -> true
    | IrRecursive _, IrRecursive _ ->
        not (seen.Contains((given, expected))) && needsConversionSeen (seen.Add((given, expected))) (IrType.unfold given) (IrType.unfold expected)
    | IrTags givenTags, IrTags expectedTags ->
        givenTags
        |> List.exists (fun (name, payload) ->
            match payload, expectedTags |> List.tryFind (fst >> (=) name) |> Option.bind snd with
            | Some givenPayload, Some expectedPayload -> needsConversion givenPayload expectedPayload
            | _ -> false)
    | IrRecord givenFields, IrRecord expectedFields when List.map fst givenFields = List.map fst expectedFields ->
        List.exists2 (fun (_, givenTy) (_, expectedTy) -> needsConversion givenTy expectedTy) givenFields expectedFields
    | IrList givenElement, IrList expectedElement
    | IrArray givenElement, IrArray expectedElement -> needsConversion givenElement expectedElement
    | IrFunction(givenInput, givenOutput, _, _), IrFunction(expectedInput, expectedOutput, _, _) ->
        needsConversion expectedInput givenInput || needsConversion givenOutput expectedOutput
    | _ -> false

/// Whether a value of `given` must be converted to be a value of `expected` (see above).
let needsConversion (given: IrType) (expected: IrType) : bool = needsConversionSeen Set.empty given expected

/// Whether a value of `ty` holds a box, or a function of `ty` takes or returns one.
let rec holdsBox (ty: IrType) : bool =
    match ty with
    | IrBox -> true
    | IrTags tags -> tags |> List.exists (snd >> Option.exists holdsBox)
    | IrRecord fields -> fields |> List.exists (snd >> holdsBox)
    | IrList element
    | IrArray element -> holdsBox element
    | IrRecursive body -> holdsBox body
    | IrFunction(input, output, _, _) -> holdsBox input || holdsBox output
    | _ -> false

/// What converting needs from the function being lowered.
type BoxTools =
    { /// A local of this name, made unique, and type.
      NewLocal: string -> IrType -> Var
      /// The function a box of a value of this type is a closure over (see `readerFunction`), by its name in
      /// WebAssembly text.
      Reader: IrType -> string
      /// The function that converts a list of values of the first type to one of the second (see
      /// `listConverterFunction`), by its name in WebAssembly text.
      ListConverter: IrType -> IrType -> string
      /// The function that converts a value of the first recursive type to one of the second, as a map's store with
      /// its keys boxed to one with them as they are (see `recursiveConverterFunction`), by its name in WebAssembly text.
      RecursiveConverter: IrType -> IrType -> string
      /// Converts a part of a value: a function through an adapter, anything else by `convert`. What it passes on is
      /// never a temporary, so code that stores can come before it is used.
      Convert: Atom -> IrType -> (Atom -> Expr) -> Expr }

/// `value`, bound to a local of its type, passed to `k`.
let private bindLocal (tools: BoxTools) (name: string) (value: Expr) (k: Var -> Expr) : Expr =
    let local = tools.NewLocal name value.Type
    let body = k local
    Expr.make (Let(local, value, body)) body.Type

/// `atom`, a value of its type, as a value of `expected`, passed to `k`: boxed where `expected` has a box, unboxed where
/// it has the value, and a tag's payload, a record's fields or a list's elements that need it converted in turn into a
/// new cell, record or list.
/// A value that needs nothing converted is passed on as it is. A function is not converted here: `tools.Convert` adapts
/// it.
let rec convert (tools: BoxTools) (atom: Atom) (expected: IrType) (k: Atom -> Expr) : Expr =
    let given = atomType atom
    match given, expected with
    | _ when not (needsConversion given expected) -> k atom
    | _, IrBox ->
        // The box captures the value; its function returns it.
        bindLocal tools "__box" (Expr.make (MakeClosure(tools.Reader given, 1, [ atom ])) IrBox) (AVar >> k)
    | IrBox, _ ->
        // A closure is read twice to call it, so the box is in a local.
        bindLocal tools "__boxed" (Expr.make (Atom atom) IrBox) (fun box ->
            bindLocal tools "__unboxed" (Expr.make (CallClosure(box, [], Borrowing)) expected) (AVar >> k))
    | IrTags givenTags, IrTags expectedTags ->
        // Each tag whose payload needs converting is made again around the converted payload; any other is the value.
        bindLocal tools "__converting" (Expr.make (Atom atom) given) (fun value ->
            let converted =
                givenTags
                |> List.choose (fun (name, payload) ->
                    match payload, expectedTags |> List.tryFind (fst >> (=) name) |> Option.bind snd with
                    | Some givenPayload, Some expectedPayload when needsConversion givenPayload expectedPayload ->
                        let bound = tools.NewLocal "__payload" givenPayload
                        let body =
                            tools.Convert (AVar bound) expectedPayload (fun payload ->
                                Expr.make (MakeTag(name, payload)) expected)
                        Some { Patterns = [ PTag(name, Some (PBind bound)) ]; Body = body }
                    | _ -> None)
            let others = { Patterns = [ PWildcard ]; Body = Expr.make (Atom(AVar value)) expected }
            bindLocal tools "__converted" (Expr.make (Match([ value ], converted @ [ others ])) expected) (AVar >> k))
    | IrRecord givenFields, IrRecord expectedFields ->
        // A new record of the fields, each converted.
        bindLocal tools "__converting" (Expr.make (Atom atom) given) (fun record ->
            let rec fields (remaining: ((string * IrType) * IrType) list) (slot: int) (acc: (string * Atom) list) =
                match remaining with
                | [] -> bindLocal tools "__converted" (Expr.make (MakeRecord(List.rev acc)) expected) (AVar >> k)
                | ((name, fieldTy), expectedTy) :: rest ->
                    bindLocal tools "__field" (Expr.make (Field(AVar record, slot)) fieldTy) (fun field ->
                        tools.Convert (AVar field) expectedTy (fun converted -> fields rest (slot + 1) ((name, converted) :: acc)))
            fields (List.zip givenFields (List.map snd expectedFields)) 0 [])
    | IrList givenElement, IrList expectedElement ->
        // A new list of the elements, each converted, by a loop that starts with nothing converted (see
        // `listConverterFunction`). The list is bound first, as it may be a temporary that waits for no store.
        bindLocal tools "__list" (Expr.make (Atom atom) given) (fun list ->
            bindLocal tools "__none" (Expr.make (MakeList []) expected) (fun reversed ->
                bindLocal tools "__none" (Expr.make (MakeList []) expected) (fun made ->
                    bindLocal tools "__converted" (Expr.make (Call(tools.ListConverter givenElement expectedElement, [ AVar list; AVar reversed; AVar made ])) expected) (AVar >> k))))
    | IrRecursive _, IrRecursive _ ->
        // What it is made of, converted, by a function that calls itself where the type holds itself (#2881).
        bindLocal tools "__converted" (Expr.make (Call(tools.RecursiveConverter given expected, [ atom ])) expected) (AVar >> k)
    | IrArray givenElement, IrArray expectedElement ->
        // A new array of the elements, each converted as a list's are.
        let call name args ty = Expr.make (CallRuntime(name, args, true)) ty
        bindLocal tools "__elements" (call NyxCompiler.Intrinsics.arrayToList [ atom ] (IrList givenElement)) (fun elements ->
            bindLocal tools "__none" (Expr.make (MakeList []) (IrList expectedElement)) (fun reversed ->
                bindLocal tools "__none" (Expr.make (MakeList []) (IrList expectedElement)) (fun made ->
                    let converted = Expr.make (Call(tools.ListConverter givenElement expectedElement, [ AVar elements; AVar reversed; AVar made ])) (IrList expectedElement)
                    bindLocal tools "__converted" converted (fun list ->
                        bindLocal tools "__converted" (call NyxCompiler.Intrinsics.arrayOfList [ AVar list ] expected) (AVar >> k)))))
    | _ -> internalError $"a conversion from '{IrType.show given}' to '{IrType.show expected}'"

/// The function `functionName` that converts a list of values of `given` to a list of values of `expected`: a new list
/// of the same length, each element converted (see `convert`). It is a loop, calling itself only in tail position, so it
/// converts a list of any length (#2683): it takes the elements still to convert, those converted so far, the last
/// first, and the converted list made of those, which it is called with empty. Once every element is converted, it puts
/// each converted one in front of the list it makes, so that they come out in order.
let listConverterFunction (tools: BoxTools) (newVar: string -> VarKind -> IrType -> Var) (functionName: string) (given: IrType) (expected: IrType) : Function =
    let converted = IrList expected
    let remaining = newVar "__list" Param (IrList given)
    let reversed = newVar "__reversed" Param converted
    let made = newVar "__made" Param converted
    let head = newVar "__head" Local given
    let tail = newVar "__tail" Local (IrList given)
    let convertedHead = newVar "__converted_head" Local expected
    let convertedTail = newVar "__converted_tail" Local converted
    let cons element list = Expr.make (CallRuntime(Helpers.listCons, [ element; list ], true)) converted
    // The locals the box tools bind here are named apart from this function's own variables, which `newVar` names: the
    // tools name theirs where the conversion was asked for, and a local of a parameter's name would be the parameter.
    let call args = Expr.make (Call(functionName, args)) converted
    let convertOne =
        tools.Convert (AVar head) expected (fun element ->
            bindLocal tools "__reversed_next" (cons element (AVar reversed)) (fun reversed -> call [ AVar tail; AVar reversed; AVar made ]))
    let putBack =
        bindLocal tools "__made_next" (cons (AVar convertedHead) (AVar made)) (fun made -> call [ AVar remaining; AVar convertedTail; AVar made ])
    let putBackAll =
        Expr.make
            (Match(
                [ reversed ],
                [ { Patterns = [ PList([ PBind convertedHead ], Some (PBind convertedTail)) ]; Body = putBack }
                  { Patterns = [ PWildcard ]; Body = Expr.make (Atom(AVar made)) converted } ]
            ))
            converted
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ remaining; reversed; made ]
      Result = converted
      Body =
        Expr.make
            (Match(
                [ remaining ],
                [ { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = convertOne }
                  { Patterns = [ PWildcard ]; Body = putBackAll } ]
            ))
            converted
      Place = None }

/// The function `functionName` that converts a value of `given`, a recursive type, to a value of `expected`: what it is
/// made of, converted (see `convert`), where a part that is the type itself is converted by this function again. A map's
/// store is one, so a map with its keys boxed, as a field's `get` gives it (see `CoreIrLowerTypeMatch`), is one with its
/// keys as they are, and back.
let recursiveConverterFunction (tools: BoxTools) (newVar: string -> VarKind -> IrType -> Var) (functionName: string) (given: IrType) (expected: IrType) : Function =
    let value = newVar "__value" Param given
    let body =
        bindLocal tools "__unfolded" (Expr.make (Atom(AVar value)) (IrType.unfold given)) (fun unfolded ->
            tools.Convert (AVar unfolded) (IrType.unfold expected) (fun converted -> Expr.make (Atom converted) expected))
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ value ]
      Result = expected
      Body = body
      Place = None }

/// The function `functionName` a box of a value of `ty` is a closure over: it takes the box, which holds the value
/// after its table slot, and returns the value.
let readerFunction (newVar: string -> VarKind -> IrType -> Var) (functionName: string) (ty: IrType) : Function =
    let box = newVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord; "1", ty ])
    let value = newVar "__value" Local ty
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ box ]
      Result = ty
      Body = Expr.make (Let(value, Expr.make (Field(AVar box, 1)) ty, Expr.make (Atom(AVar value)) ty)) ty
      Place = None }
