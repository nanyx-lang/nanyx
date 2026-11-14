/// The boxes a context member's own type variables are held in (`IrBox`), and how the lowering (see `CoreIrLower`)
/// converts a value to and from a type with them.
///
/// A context without parameters whose member is generic in type variables of its own (`ret: a -> #some(a) | r` in
/// `Proc`) has the member's value made once, where its instance is, and each call has those variables at the types it
/// has: `ret(1)` at int, `ret("s")` at string. So the member's type has their values boxed (see
/// `CoreIrLowerTypes.boxOwnVariables`), and the value of the instance is lowered with them boxed too. A box is a
/// closure block over a function that returns the value, so it is passed, kept and dropped the same way whatever the
/// value is. A call boxes what it passes and unboxes what it is given back, and a function it passes or is given is
/// adapted to do the same (see `FunctionLowering.Coerce`).
module internal Transpiler.Wasm.CoreIrLowerBoxes

open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

/// Whether a value of `given` must be converted to be a value of `expected`: somewhere in it, one of them has a value
/// boxed where the other does not. Types that differ only in the tags of their unions, or in a function's context
/// members, hold their values alike: a function's members are `Coerce`'s to adapt.
let rec needsConversion (given: IrType) (expected: IrType) : bool =
    match given, expected with
    | _ when given = expected -> false
    | IrBox, _
    | _, IrBox -> true
    | IrTags givenTags, IrTags expectedTags ->
        givenTags
        |> List.exists (fun (name, payload) ->
            match payload, expectedTags |> List.tryFind (fst >> (=) name) |> Option.bind snd with
            | Some givenPayload, Some expectedPayload -> needsConversion givenPayload expectedPayload
            | _ -> false)
    | IrRecord givenFields, IrRecord expectedFields when List.map fst givenFields = List.map fst expectedFields ->
        List.exists2 (fun (_, givenTy) (_, expectedTy) -> needsConversion givenTy expectedTy) givenFields expectedFields
    | IrList givenElement, IrList expectedElement -> needsConversion givenElement expectedElement
    | IrFunction(givenInput, givenOutput, _), IrFunction(expectedInput, expectedOutput, _) ->
        needsConversion expectedInput givenInput || needsConversion givenOutput expectedOutput
    | _ -> false

/// Whether a value of `ty` holds a box, or a function of `ty` takes or returns one.
let rec holdsBox (ty: IrType) : bool =
    match ty with
    | IrBox -> true
    | IrTags tags -> tags |> List.exists (snd >> Option.exists holdsBox)
    | IrRecord fields -> fields |> List.exists (snd >> holdsBox)
    | IrList element -> holdsBox element
    | IrFunction(input, output, _) -> holdsBox input || holdsBox output
    | _ -> false

/// What converting needs from the function being lowered.
type BoxTools =
    { /// A local of this name, made unique, and type.
      NewLocal: string -> IrType -> Var
      /// The function a box of a value of this type is a closure over (see `readerFunction`), by its name in
      /// WebAssembly text.
      Reader: IrType -> string
      /// Converts a part of a value: a function through an adapter, anything else by `convert`.
      Convert: Atom -> IrType -> (Atom -> Expr) -> Expr }

/// `value`, bound to a local of its type, passed to `k`.
let private bindLocal (tools: BoxTools) (name: string) (value: Expr) (k: Var -> Expr) : Expr =
    let local = tools.NewLocal name value.Type
    let body = k local
    Expr.make (Let(local, value, body)) body.Type

/// `atom`, a value of its type, as a value of `expected`, passed to `k`: boxed where `expected` has a box, unboxed where
/// it has the value, and a tag's payload or a record's fields that need it converted in turn into a new cell or record.
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
            bindLocal tools "__unboxed" (Expr.make (CallClosure(box, [])) expected) (AVar >> k))
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
    | IrList _, IrList _ -> cannot $"a list of {IrType.show given} passed to or from a context member as one of {IrType.show expected}"
    | _ -> internalError $"a conversion from {IrType.show given} to {IrType.show expected}"

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
      Body = Expr.make (Let(value, Expr.make (Field(AVar box, 1)) ty, Expr.make (Atom(AVar value)) ty)) ty }
