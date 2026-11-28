/// How the lowering (see `CoreIrLower`) runs a type description's `build`, which makes a value from its parts (see
/// `TypeDescriptions.memberType`).
///
/// A record's description holds, as `TypeValues.descriptionMembers` doesn't say, a function that makes a value of the
/// record from its fields' values, boxed in their canonical form (see `CoreIrLowerTypeMatch`), in the order the
/// description has its fields: `__make`. Its `build` is a closure over the description and a function of its own that
/// calls the function `build` is given with each field in turn, gathers their values, and makes the record with
/// `__make`, or gives the first failure, whatever its type is. A case's `build` makes a value of its union from its
/// payload, boxed.
module internal Transpiler.Wasm.CoreIrLowerBuild

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerScope
open Transpiler.Wasm.CoreIrLowerTypeMatch

/// The name of the function that gives a value of `collection` from its elements in their canonical form.
let private decanonicalName (collection: Ty) = "__decanonical_" + Identifiers.sanitizeIdentifier (Unifier.tyToString collection)

/// `boxed`, a box of a value of the type `ty` in its canonical form, unboxed into a value of `ty` as it is laid out,
/// passed to `k`: the inverse of `CoreIrLowerTypeMatch.canonical`.
let rec decanonical (tools: TypeMatchTools) (ty: Ty) (boxed: Var) (k: Atom -> Expr) : Expr =
    let unboxed (into: IrType) (k: Var -> Expr) = bindLocal tools "__unboxed" (Expr.make (CallClosure(boxed, [], Borrowing)) into) k
    match Unifier.unalias ty with
    | Lists.ListType _
    | Sets.SetType _
    | Maps.MapType _ as collection ->
        let elements = tools.Request (decanonicalName collection) (Decanonicalising collection)
        unboxed (canonicalElements tools collection) (fun canonicalList ->
            bindLocal tools "__elements" (Expr.make (Call(elements, [ AVar canonicalList ])) (tools.ToIrType collection)) (AVar >> k))
    | Arrays.ArrayType element ->
        let listTy = Lists.ofType element
        let elements = tools.Request (decanonicalName listTy) (Decanonicalising listTy)
        let call name args ty = Expr.make (CallRuntime(name, args, true)) ty
        unboxed (IrArray IrBox) (fun boxes ->
            bindLocal tools "__boxes" (call Intrinsics.arrayToList [ AVar boxes ] (IrList IrBox)) (fun list ->
                bindLocal tools "__elements" (Expr.make (Call(elements, [ AVar list ])) (tools.ToIrType listTy)) (fun values ->
                    bindLocal tools "__array" (call Intrinsics.arrayOfList [ AVar values ] (tools.ToIrType ty)) (AVar >> k))))
    // A value of a type still only known when the program runs stays boxed.
    | _ when tools.ToIrType ty = IrBox -> k (AVar boxed)
    | _ -> unboxed (tools.ToIrType ty) (AVar >> k)

/// `store`, a map's or a set's store in its canonical form, its keys and values boxed, as the map or set of `collection`
/// it holds, passed to `k`, if `collection` is laid out otherwise: a map an arm of a `Type` match narrows to
/// `map(string, v)`, which a field's `get` gave with its keys boxed (#2881). None if it is laid out so already.
let decanonicalStore (tools: TypeMatchTools) (collection: Ty) (store: Atom) (k: Atom -> Expr) : Expr option =
    match Unifier.unalias collection with
    | Maps.MapType _
    | Sets.SetType _ when atomType store = canonicalElements tools collection && atomType store <> tools.ToIrType collection ->
        let elements = tools.Request (decanonicalName collection) (Decanonicalising collection)
        Some (bindLocal tools "__elements" (Expr.make (Call(elements, [ store ])) (tools.ToIrType collection)) (AVar >> k))
    | _ -> None

/// The function a closure that unboxes a value of `ty` from its canonical form is over: it takes the closure's
/// environment and the box.
let decanonicalisingValueFunction (tools: TypeMatchTools) (functionName: string) (ty: Ty) : Function =
    let environment = tools.NewVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord ])
    let boxed = tools.NewVar "__boxed" Param IrBox
    let result = tools.ToIrType ty
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; boxed ]
      Result = result
      Body = decanonical tools ty boxed (fun value -> Expr.make (Atom value) result)
      Place = None }

/// The function that gives a list from its elements in their canonical form.
let private listDecanonicalisingFunction (tools: TypeMatchTools) (functionName: string) (elementTy: Ty) (collection: Ty) : Function =
    let given = canonicalElements tools collection
    let result = tools.ToIrType collection
    let element = match given with IrList element -> element | other -> internalError $"canonical elements of '{IrType.show other}'"
    let list = tools.NewVar "__list" Param given
    let head = tools.NewVar "__head" Local element
    let tail = tools.NewVar "__tail" Local given
    let cons (value: Atom) =
        bindLocal tools "__rest" (Expr.make (Call(functionName, [ AVar tail ])) result) (fun rest ->
            Expr.make (CallRuntime(Helpers.listCons, [ value; AVar rest ], true)) result)
    let arms =
        [ { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = decanonical tools elementTy head cons }
          { Patterns = [ PWildcard ]; Body = Expr.make (MakeList []) result } ]
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ list ]
      Result = result
      Body = Expr.make (Match([ list ], arms)) result
      Place = None }

/// The function that gives a value of `collection`, a list, a set or a map, from its canonical form: the inverse of
/// `CoreIrLowerTypeMatch.canonicalisingFunction`. A map's or a set's store keeps its shape and hashes, so nothing is
/// hashed again.
let decanonicalisingFunction (tools: TypeMatchTools) (functionName: string) (collection: Ty) : Function =
    match Unifier.unalias collection with
    | Lists.ListType elementTy -> listDecanonicalisingFunction tools functionName elementTy collection
    | Maps.MapType _
    | Sets.SetType _ ->
        let converter (ty: Ty) = tools.Request ("__decanonical_value_" + Identifiers.sanitizeIdentifier (Unifier.tyToString ty)) (DecanonicalisingValue ty)
        storeConvertingFunction tools functionName collection (fun _ -> boxedTy) id converter
    | other -> internalError $"decanonical elements of '{Unifier.tyToString other}'"

/// The fields of the record type `recordType`, in the order its description has them (see
/// `CoreIrLowerTypeMatch.descriptionOf`).
let private fieldsOf (recordType: Ty) : (string * Ty) list =
    let written (fields: Map<string, Ty>) (order: FieldOrder) =
        let named = order.Names |> List.filter fields.ContainsKey
        (named @ (fields.Keys |> Seq.filter (fun name -> not (List.contains name named)) |> List.ofSeq))
        |> List.map (fun name -> name, fields.[name])
    match Unifier.unalias recordType with
    | TyRecord(fields, order) -> written fields order
    | TyNominal _ as nominal ->
        match Unifier.unalias (RecursiveNominals.underlying nominal) with
        | TyRecord(fields, order) -> written fields order
        | _ -> []
    | _ -> []

/// The function a record description's `__make` is a closure over: it takes its fields' values, boxed, in the order the
/// description has its fields, and gives the record, boxed.
let makingFunction (tools: TypeMatchTools) (functionName: string) (recordType: Ty) : Function =
    let environment = tools.NewVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord ])
    let values = tools.NewVar "__values" Param (IrList IrBox)
    let recordTy = tools.ToIrType recordType
    let fields = fieldsOf recordType
    // Named apart from the parameters, `__env` and `__values`, which a field of either name would otherwise hide.
    let boxes = fields |> List.map (fun (name, _) -> tools.NewVar (tools.UniqueName $"__field_{name}") PatternBound IrBox)
    let rec unboxAll (remaining: ((string * Ty) * Var) list) (acc: (string * Atom) list) =
        match remaining with
        | [] ->
            let byName = Map.ofList acc
            // A recursive alias of a record, which names itself in a field, is made of the record its body is.
            let slots = match IrType.unfold recordTy with IrRecord slots -> slots | other -> internalError $"a record made of '{IrType.show other}'"
            bindLocal tools "__record" (Expr.make (MakeRecord(slots |> List.map (fun (name, _) -> name, byName.[name]))) recordTy) (fun record ->
                bindLocal tools "__box" (Expr.make (MakeClosure(tools.Reader recordTy, 1, [ AVar record ])) IrBox) (fun box -> Expr.make (Atom(AVar box)) IrBox))
        | ((name, fieldType), box) :: rest -> decanonical tools fieldType box (fun value -> unboxAll rest ((name, value) :: acc))
    let arms =
        [ { Patterns = [ PList(boxes |> List.map PBind, None) ]; Body = unboxAll (List.zip fields boxes) [] }
          { Patterns = [ PWildcard ]; Body = Expr.make (Crash([ "A record made from the wrong number of fields" ], false)) IrBox } ]
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; values ]
      Result = IrBox
      Body = Expr.make (Match([ values ], arms)) IrBox
      Place = None }

/// The `build` of the case `caseName` of the union `unionType`: it takes the payload, boxed in its canonical form, and
/// gives the value of that case, boxed.
let caseBuildingFunction (tools: TypeMatchTools) (functionName: string) (unionType: Ty) (caseName: string) : Function =
    let environment, boxed = getterParameters tools
    let unionTy = tools.ToIrType unionType
    let payload =
        let rec find (ty: Ty) =
            match Unifier.unalias ty with
            | TyTag(name, payload) when name = caseName -> Some payload
            | TyUnion items -> items |> List.tryPick find
            | TyNominal _ as nominal -> find (RecursiveNominals.underlying nominal)
            | _ -> None
        find unionType |> Option.flatten
    let box (value: Atom) =
        bindLocal tools "__box" (Expr.make (MakeClosure(tools.Reader unionTy, 1, [ value ])) IrBox) (fun box -> Expr.make (Atom(AVar box)) IrBox)
    let body =
        match payload with
        | Some payloadType ->
            decanonical tools payloadType boxed (fun value -> bindLocal tools "__value" (Expr.make (MakeTag(caseName, value)) unionTy) (AVar >> box))
        | None -> bindLocal tools "__value" (Expr.make (Atom(ATag caseName)) unionTy) (AVar >> box)
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; boxed ]
      Result = IrBox
      Body = body
      Place = None }

/// `value`, a value of the union `given` whose tag is not `#some`, as a value of the union `expected`, which has the same
/// failures, passed to `k`: each failure is made again as one of `expected`.
let private asFailure (tools: TypeMatchTools) (value: Var) (expected: IrType) (k: Atom -> Expr) : Arm list =
    match value.Type with
    | IrTags tags ->
        tags
        |> List.filter (fst >> (<>) "some")
        |> List.map (fun (tag, payloadTy) ->
            match payloadTy with
            | Some payloadTy ->
                let payload = tools.NewVar (tools.UniqueName "__failure") PatternBound payloadTy
                { Patterns = [ PTag(tag, Some (PBind payload)) ]
                  Body = bindLocal tools "__failed" (Expr.make (MakeTag(tag, AVar payload)) expected) (AVar >> k) }
            | None -> { Patterns = [ PTag(tag, None) ]; Body = bindLocal tools "__failed" (Expr.make (Atom(ATag tag)) expected) (AVar >> k) })
    | other -> internalError $"a build's failure of type '{IrType.show other}'"

/// The parts of the type of a record description's `build`, `buildTy`: the function it is given, which gives a field's
/// value, boxed, or a failure; and what it gives, the record or the failure.
let private buildParts (buildTy: IrType) =
    match buildTy with
    | IrFunction(IrFunction(fieldTy, givenTy, [], _) as functionTy, resultTy, [], _) -> fieldTy, functionTy, givenTy, resultTy
    | other -> internalError $"a build of type '{IrType.show other}'"

/// The union a record description's `build`, of type `buildTy`, gathers its fields' values in, boxed: `#some` of them,
/// or the first failure.
let private gatheredType (buildTy: IrType) =
    let _, _, givenTy, _ = buildParts buildTy
    match givenTy with
    | IrTags tags -> IrTags(tags |> List.map (fun (tag, payload) -> if tag = "some" then tag, Some (IrList IrBox) else tag, payload))
    | other -> internalError $"a build's field of type '{IrType.show other}'"

/// The function that gives each field's value a record description's `build`, of type `buildTy`, is given: it takes the
/// fields and that function, calls it with each field in turn, and gives `#some` of their values, or the first failure.
let buildingFieldsFunction (tools: TypeMatchTools) (functionName: string) (buildTy: IrType) : Function =
    let fieldTy, functionTy, givenTy, _ = buildParts buildTy
    let gathered = gatheredType buildTy
    let fields = tools.NewVar "__fields" Param (IrList fieldTy)
    let given = tools.NewVar "__give" Param functionTy
    let field = tools.NewVar "__field" Local fieldTy
    let others = tools.NewVar "__others" Local (IrList fieldTy)
    let some (atom: Atom) = Expr.make (MakeTag("some", atom)) gathered
    let next =
        bindLocal tools "__given" (Expr.make (CallClosure(given, [ AVar field ], Borrowing)) givenTy) (fun result ->
            let value = tools.NewVar (tools.UniqueName "__value") PatternBound IrBox
            let rest =
                bindLocal tools "__rest" (Expr.make (Call(functionName, [ AVar others; AVar given ])) gathered) (fun rest ->
                    let values = tools.NewVar (tools.UniqueName "__values") PatternBound (IrList IrBox)
                    let cons =
                        bindLocal tools "__all" (Expr.make (CallRuntime(Helpers.listCons, [ AVar value; AVar values ], true)) (IrList IrBox)) (AVar >> some)
                    let arms = { Patterns = [ PTag("some", Some (PBind values)) ]; Body = cons } :: asFailure tools rest gathered (fun atom -> Expr.make (Atom atom) gathered)
                    Expr.make (Match([ rest ], arms)) gathered)
            let arms = { Patterns = [ PTag("some", Some (PBind value)) ]; Body = rest } :: asFailure tools result gathered (fun atom -> Expr.make (Atom atom) gathered)
            Expr.make (Match([ result ], arms)) gathered)
    let none = bindLocal tools "__none" (Expr.make (MakeList []) (IrList IrBox)) (AVar >> some)
    let arms =
        [ { Patterns = [ PList([ PBind field ], Some (PBind others)) ]; Body = next }
          { Patterns = [ PWildcard ]; Body = none } ]
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ fields; given ]
      Result = gathered
      Body = Expr.make (Match([ fields ], arms)) gathered
      Place = None }

/// The function a record description's `build`, of type `buildTy`, is a closure over: its environment holds the
/// description, and it takes the function that gives each field's value.
let buildingFunction (tools: TypeMatchTools) (functionName: string) (buildTy: IrType) : Function =
    let _, functionTy, _, resultTy = buildParts buildTy
    let infoTy = tools.ToIrType (TyApply(TypeValues.recordInfo, [ boxedTy ]))
    let environment = tools.NewVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord; "1", infoTy ])
    let given = tools.NewVar "__give" Param functionTy
    let gathered = gatheredType buildTy
    let slotOf (name: string) = match infoTy with IrRecord slots -> slots |> List.findIndex (fst >> (=) name) | _ -> internalError "a record's description"
    let gatherer = tools.Request $"__build_fields_{IrType.mangle buildTy}" (BuildingFields buildTy)
    let made = match resultTy with IrTags tags -> tags |> List.find (fst >> (=) "some") |> snd |> Option.get | other -> internalError $"a build giving '{IrType.show other}'"
    let body =
        bindLocal tools "__info" (Expr.make (Field(AVar environment, 1)) infoTy) (fun info ->
            bindLocal tools "__fields" (Expr.make (Field(AVar info, slotOf "fields")) (IrList(tools.ToIrType (TyApply(TypeValues.field, [ boxedTy ]))))) (fun fields ->
                bindLocal tools "__gathered" (Expr.make (Call(gatherer, [ AVar fields; AVar given ])) gathered) (fun result ->
                    let values = tools.NewVar (tools.UniqueName "__values") PatternBound (IrList IrBox)
                    let make =
                        bindLocal tools "__make" (Expr.make (Field(AVar info, slotOf makeMember)) (IrFunction(IrList IrBox, IrBox, [], false))) (fun maker ->
                            bindLocal tools "__made" (Expr.make (CallClosure(maker, [ AVar values ], Borrowing)) IrBox) (fun boxed ->
                                tools.Convert (AVar boxed) made (fun record -> Expr.make (MakeTag("some", record)) resultTy)))
                    let arms = { Patterns = [ PTag("some", Some (PBind values)) ]; Body = make } :: asFailure tools result resultTy (fun atom -> Expr.make (Atom atom) resultTy)
                    Expr.make (Match([ result ], arms)) resultTy)))
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; given ]
      Result = resultTy
      Body = body
      Place = None }

/// `r.build`, the `build` of the record description `record`, at `expr`, of the IR type `buildTy`: a closure over the
/// description (see `buildingFunction`), passed to `k`.
let building (tools: TypeMatchTools) (scope: Scope) (record: Expression) (buildTy: IrType) (k: Expr -> Expr) : Expr =
    let builder = tools.Request $"__build_{IrType.mangle buildTy}" (Building buildTy)
    tools.Complex scope record (fun info ->
        bindLocal tools "__info" info (fun info -> k (Expr.make (MakeClosure(builder, 2, [ AVar info ])) buildTy)))
