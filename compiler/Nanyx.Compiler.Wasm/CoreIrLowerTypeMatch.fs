/// How the lowering (see `CoreIrLower`) runs a `match` on a `Type(a)` (see `TypeMatches`), and the functions that build
/// the descriptions it matches.
///
/// A `Type` value is the number of the type it stands for (see `CoreIrTypeIds`), and the module's `__describe` builds
/// the description of the type of a number: a tag of the union `TypeValues.descriptionUnion`. A function specialised
/// at a type it knows picks the arm for it at compile time, and a `#list(elem)` arm then knows the element type too. One
/// called at a field's or an element's type, which is only known when the program runs, has that type's values boxed
/// (see `CoreIrLowerTypes.substitute`): it matches on the description the type's number finds, and a value of the type
/// an arm narrows `a` to is unboxed where it is used (see `FunctionLowering.Identifier`).
///
/// A boxed value is in its canonical form: a list's, a set's, a map's or an array's elements are boxed in turn, so that
/// a `#list(elem)` arm finds a list of boxes whatever the elements are; any other value is boxed as it is. A field's
/// `get` and a case's `get` give their values so.
module internal Transpiler.Wasm.CoreIrLowerTypeMatch

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerScope

/// The module's function that builds the description of the type of a number.
let describeFunction = "__describe"

/// What lowering a `Type` match, or a function that builds descriptions, needs from the function being lowered.
type TypeMatchTools =
    { /// A variable of this name, kind and type, named as it is.
      NewVar: string -> VarKind -> IrType -> Var
      /// A parameter's or local's name: as written, made unique within the function.
      UniqueName: string -> string
      ToIrType: Ty -> IrType
      TypeIds: CoreIrTypeIds.TypeIds
      /// The type definitions in scope, by name (see `LowerInput.TypeDefinitions`).
      TypeDefinitions: Map<string, TypeDefInfo>
      /// The types of the nodes, as the type checker has them: an arm's pattern has the type the arm narrows `a` to.
      ExprTypes: Map<NodeId, Ty>
      /// Makes a type the checker never learns, by its number, the type a match found it is, from here on.
      BindPackaged: int -> Ty -> unit
      /// The type of an expression, at the function's type arguments.
      TypeOf: Expression -> IrType
      /// Lowers an expression to a value that is not a `Let`, and passes that on.
      Complex: Scope -> Expression -> (Expr -> Expr) -> Expr
      /// Lowers an arm's body in tail position, adapted to the `match`'s type.
      Branch: Scope -> Expression -> IrType -> Expr
      /// The name to call a definition by at a type, as the prelude's `Maps.convertedStore` is called.
      Callee: string -> Ty -> string
      /// The function that builds a part of a description, asked for by its name and how it is lowered.
      Request: string -> Lowering -> string
      /// The function a box of a value of this type is a closure over (see `CoreIrLowerBoxes.readerFunction`).
      Reader: IrType -> string
      /// An atom, a value of its type, as a value of the given type, boxed or unboxed where they differ (see
      /// `CoreIrLowerBoxes.convert`), passed on.
      Convert: Atom -> IrType -> (Atom -> Expr) -> Expr }

/// The IR type of a type's description.
let descriptionType (tools: TypeMatchTools) = tools.ToIrType (TypeValues.descriptionUnion boxedTy boxedTy)

/// `value` in a new local, passed to `k`.
let bindLocal (tools: TypeMatchTools) (name: string) (value: Expr) (k: Var -> Expr) : Expr =
    let local = tools.NewVar (tools.UniqueName name) Local value.Type
    let body = k local
    Expr.make (Let(local, value, body)) body.Type

/// `values` each in a new local, in order, passed to `k`.
let rec private bindLocals (tools: TypeMatchTools) (name: string) (values: Expr list) (k: Atom list -> Expr) : Expr =
    match values with
    | [] -> k []
    | value :: rest -> bindLocal tools name value (fun local -> bindLocals tools name rest (fun atoms -> k (AVar local :: atoms)))

/// The tag of the description of `ty`, a type with nothing left to settle. A nominal type is described by what it is
/// made from.
let rec private tagOf (ty: Ty) : string =
    let ty = Unifier.unalias ty
    match TypeValues.builtInDescriptions |> List.tryFind (snd >> (=) ty), ty with
    | Some (tag, _), _ -> tag
    | None, Lists.ListType _ -> "list"
    | None, Arrays.ArrayType _ -> "array"
    | None, Sets.SetType _ -> "set"
    | None, Maps.MapType _ -> "map"
    | None, TyFunc _ -> TypeValues.functionTag
    | None, (TyTag _ | TyUnion _) -> TypeValues.unionTag
    | None, TyRecord _ -> TypeValues.recordTag
    | None, (TyNominal _ as nominal) -> tagOf (RecursiveNominals.underlying nominal)
    | None, other -> cannot $"a description of the type '{Unifier.tyToString other}'"

/// What the types the checker never learns in `narrowed`, the type an arm says `a` is, are where `a` is `actual`: none
/// if the arm doesn't match it.
let rec private packagedIn (narrowed: Ty) (actual: Ty) (found: Map<int, Ty>) : Map<int, Ty> option =
    match narrowed, Unifier.unalias actual with
    | TypeValues.PackagedType id, _ -> Some (found |> Map.add id actual)
    | TyApply(name, parts), TyApply(actualName, actualParts) when name = actualName && parts.Length = actualParts.Length ->
        List.zip parts actualParts |> List.fold (fun found (part, actualPart) -> found |> Option.bind (packagedIn part actualPart)) (Some found)
    | _, unaliased -> if narrowed = unaliased then Some found else None

/// What the arm whose pattern is `pattern` finds of `standsFor`, a known type, if it matches it.
let private armFinds (tools: TypeMatchTools) (standsFor: Ty) (pattern: Parser.Program.Pattern) : Map<int, Ty> option =
    match pattern.Kind with
    | WildcardPattern
    | ElsePattern
    | IdentifierPattern _ -> Some Map.empty
    | TagPattern(tag, _) ->
        match tools.ExprTypes |> Map.tryFind pattern.Id with
        | Some narrowed -> packagedIn narrowed standsFor Map.empty
        | None -> if tagOf standsFor = tag then Some Map.empty else None
    | _ -> cannot "this pattern in a match on a type"

/// The pattern a description's payload of the IR type `payloadTy` is matched by, for `pattern`, with what it binds added
/// to `vars`.
let private payloadPattern (tools: TypeMatchTools) (payloadTy: IrType) (pattern: Parser.Program.Pattern) (vars: Map<string, Var>) : Pattern * Map<string, Var> =
    let bind (pattern: Parser.Program.Pattern) (ty: IrType) (vars: Map<string, Var>) =
        match pattern.Kind with
        | IdentifierPattern name ->
            let var = tools.NewVar (tools.UniqueName $"__payload_{name}") PatternBound ty
            PBind var, vars |> Map.add name var
        | _ -> PWildcard, vars
    match pattern.Kind, payloadTy with
    | TuplePattern(items, false), IrRecord fields ->
        let parts, vars =
            List.zip items fields
            |> List.indexed
            |> List.mapFold (fun vars (slot, (item, (_, fieldTy))) -> bind item fieldTy vars |> fun (part, vars) -> (slot, part), vars) vars
        PFields parts, vars
    | _ -> bind pattern payloadTy vars

/// Lowers `expr`, a `match` on `value`, a `Type(standsFor)` at the function's type arguments, against `arms`, and passes
/// its value to `k`. Where `standsFor` is known, only the arm that matches it is lowered, and what it finds of the types
/// the checker never learns is bound from there on. Otherwise every arm is, against the description the type's number
/// finds when the program runs.
let lower (tools: TypeMatchTools) (scope: Scope) (expr: Expression) (value: Expression) (standsFor: Ty) (arms: (Parser.Program.Pattern list * Expression) list) (k: Expr -> Expr) : Expr =
    let ty = tools.TypeOf expr
    let known = not (holdsBoxed standsFor) && (freeVars standsFor).IsEmpty
    // A nominal type's description depends on the module whose `type` made the value (see `CoreIrTypeIds`), which only
    // the program knows when it runs: every arm that could match it is lowered, its structure's or `#nominal`'s.
    let nominal = match Unifier.unalias standsFor with TyNominal _ -> true | _ -> false
    let finds (pattern: Parser.Program.Pattern) =
        match pattern.Kind with
        | TagPattern(tag, _) when nominal && tag = TypeValues.nominalTag -> Some Map.empty
        | _ -> armFinds tools standsFor pattern
    let chosen =
        let candidates () = arms |> List.choose (fun (patterns, body) -> finds (List.head patterns) |> Option.map (fun found -> List.head patterns, body, found))
        if not known then arms |> List.map (fun (patterns, body) -> List.head patterns, body, Map.empty)
        elif nominal then candidates ()
        else List.truncate 1 (candidates ())
    match chosen with
    // The one arm that matches a known type, which reads nothing of its description.
    | [ ({ Kind = WildcardPattern | ElsePattern | TagPattern(_, None) }, body, found) ] ->
        found |> Map.iter tools.BindPackaged
        k (tools.Branch scope body ty)
    | _ ->
        tools.Complex scope value (fun lowered ->
            bindLocal tools "__type" lowered (fun typeVar ->
                let description = descriptionType tools
                let payloadOf tag =
                    match description with
                    | IrTags tags -> tags |> List.tryFind (fst >> (=) tag) |> Option.bind snd
                    | _ -> None
                let lowerArm (pattern: Parser.Program.Pattern, body: Expression, found: Map<int, Ty>) =
                    found |> Map.iter tools.BindPackaged
                    let matched, vars =
                        match pattern.Kind with
                        | IdentifierPattern name -> PWildcard, scope.Vars |> Map.add name typeVar
                        | TagPattern(tag, Some payload) ->
                            let inner, vars = payloadPattern tools (payloadOf tag |> Option.defaultValue IrUnit) payload scope.Vars
                            PTag(tag, Some inner), vars
                        | TagPattern(tag, None) -> PTag(tag, None), scope.Vars
                        | _ -> PWildcard, scope.Vars
                    { Patterns = [ matched ]; Body = tools.Branch { scope with Vars = vars } body ty }
                let lowered = chosen |> List.map lowerArm
                let catchesAll = lowered |> List.exists (fun arm -> arm.Patterns = [ PWildcard ])
                let trap = { Patterns = [ PWildcard ]; Body = Expr.make (Crash([ "No arm of this match on a type matches it" ], false)) ty }
                let described = Expr.make (Call(describeFunction, [ AVar typeVar ])) description
                bindLocal tools "__description" described (fun descriptionVar ->
                    k (Expr.make (Match([ descriptionVar ], lowered @ (if catchesAll then [] else [ trap ]))) ty))))

/// The name a type's description gives it, and the types it is applied to: an alias's or a nominal type's, or none.
let private namingOf (ty: Ty) : string * Ty list =
    match ty with
    | TyAlias(name, args, _) -> name, args
    | TyNominal(id, args, _, _) -> id.Name, args
    | _ -> "", []

/// A record of the IR type `recordTy`, its fields' values by name, in the order its fields are stored, passed to `k`.
let private makeRecord (tools: TypeMatchTools) (recordTy: IrType) (values: Map<string, Expr>) (k: Atom -> Expr) : Expr =
    match recordTy with
    | IrRecord fields ->
        bindLocals tools "__part" (fields |> List.map (fun (name, _) -> values.[name])) (fun atoms ->
            bindLocal tools "__record" (Expr.make (MakeRecord(List.zip (List.map fst fields) atoms)) recordTy) (AVar >> k))
    | other -> internalError $"a description's record of type '{IrType.show other}'"

/// A list of `items`, each made by passing on to `k` an atom, as a value of the IR type `listTy`.
let private makeList (tools: TypeMatchTools) (listTy: IrType) (items: ((Atom -> Expr) -> Expr) list) (k: Atom -> Expr) : Expr =
    let rec gather remaining acc =
        match remaining with
        | [] -> bindLocal tools "__list" (Expr.make (MakeList(List.rev acc)) listTy) (AVar >> k)
        | item :: rest -> item (fun atom -> gather rest (atom :: acc))
    gather items []

/// The string `text` as an expression.
let private text (text: string) = Expr.make (Atom(AString text)) IrString

/// The definition of the alias or nominal type `ty` is, if it has one in scope.
let private definitionOf (tools: TypeMatchTools) (ty: Ty) : TypeDefInfo option =
    match ty with
    | TyAlias(name, _, _) -> tools.TypeDefinitions |> Map.tryFind name
    | TyNominal(id, _, _, _) -> tools.TypeDefinitions |> Map.tryFind id.Name |> Option.filter (fun info -> info.Home = id.Home)
    | _ -> None

/// The module `home` as a type's description names it: a standard-library module by its import path, a file by its name.
let private moduleName (home: ModuleOrigin) =
    match home with
    | StandardLibraryModule name -> StandardLibrary.importPrefix + name
    | SourceFile path -> System.IO.Path.GetFileNameWithoutExtension path
    | InMemory -> ""

/// Whether a nominal type whose home is `home` shows its structure to code in the file `view`: only to its home module's.
let private shownTo (view: string) (home: ModuleOrigin) = ModuleOrigins.sourceFileName home = view

/// The description of `ty`, a type with nothing left to settle, as code in the file `view` sees it, as a value of the
/// description's IR type. Its parts are numbered as seen from there too.
let private descriptionOf (tools: TypeMatchTools) (ty: Ty, view: string) : Expr =
    let description = descriptionType tools
    let typeId (part: Ty) = Expr.make (Atom(AInt(tools.TypeIds.Of(part, view)))) IrInt
    let tagged (tag: string) (payload: Atom) = Expr.make (MakeTag(tag, payload)) description
    let name, parameters = namingOf ty
    let definition = definitionOf tools ty
    let naming (parts: (string * ((Atom -> Expr) -> Expr) list * IrType) option) (k: Map<string, Expr> -> Expr) =
        let parameterTy = tools.ToIrType (TyApply(TypeValues.someType, []))
        let parameterItems =
            parameters |> List.map (fun parameter k -> makeRecord tools parameterTy (Map.ofList [ "type", typeId parameter ]) k)
        let named =
            [ "name", text name
              "module", text (definition |> Option.map (_.Home >> moduleName) |> Option.defaultValue "")
              "doc", text (definition |> Option.map _.Doc |> Option.defaultValue "") ]
        makeList tools (IrList parameterTy) parameterItems (fun parameterList ->
            let named = named @ [ "parameters", Expr.make (Atom parameterList) (IrList parameterTy) ]
            match parts with
            | Some (partsName, items, partTy) ->
                makeList tools (IrList partTy) items (fun partList -> k (Map.ofList (named @ [ partsName, Expr.make (Atom partList) (IrList partTy) ])))
            | None -> k (Map.ofList named))
    let closure (functionName: string) (closureTy: IrType) = Expr.make (Atom(AStaticClosure(functionName, 2, closureTy))) closureTy
    let attributesOf (fieldName: string) =
        let attributeTy = tools.ToIrType TypeValues.attributeTy
        let pairTy = IrRecord [ "0", IrString; "1", IrString ]
        let attributes = definition |> Option.bind (_.FieldAttributes >> Map.tryFind fieldName) |> Option.defaultValue []
        let items =
            attributes
            |> List.map (fun (attribute: FieldAttribute) k ->
                let pairs = attribute.Arguments |> List.map (fun (key, value) k -> makeRecord tools pairTy (Map.ofList [ "0", text key; "1", text value ]) k)
                makeList tools (IrList pairTy) pairs (fun arguments ->
                    makeRecord tools attributeTy (Map.ofList [ "name", text attribute.Name; "arguments", Expr.make (Atom arguments) (IrList pairTy) ]) k))
        items, IrList attributeTy
    match tagOf ty, Unifier.unalias ty with
    // A nominal type shows nothing more than its name outside its home module.
    | _, TyNominal(id, _, _, _) when not (shownTo view id.Home) ->
        naming None (fun values -> makeRecord tools (tools.ToIrType (TyApply(TypeValues.nominalInfo, []))) values (tagged TypeValues.nominalTag))
    | tag, _ when TypeValues.builtInDescriptions |> List.exists (fst >> (=) tag) -> Expr.make (Atom(ATag tag)) description
    | tag, _ when tag = TypeValues.functionTag -> Expr.make (Atom(ATag tag)) description
    | tag, (Lists.ListType element | Sets.SetType element | Arrays.ArrayType element) ->
        bindLocal tools "__element" (typeId element) (fun element -> tagged tag (AVar element))
    | tag, Maps.MapType(key, value) ->
        let entryTy = IrRecord [ "0", IrInt; "1", IrInt ]
        makeRecord tools entryTy (Map.ofList [ "0", typeId key; "1", typeId value ]) (tagged tag)
    | tag, shape when tag = TypeValues.recordTag ->
        // The fields in the order the type writes them, and any it doesn't by name.
        let written (fields: Map<string, Ty>) (order: FieldOrder) =
            let named = order.Names |> List.filter fields.ContainsKey
            (named @ (fields.Keys |> Seq.filter (fun name -> not (List.contains name named)) |> List.ofSeq))
            |> List.map (fun name -> name, fields.[name])
        let fields =
            match shape with
            | TyRecord(fields, order) -> written fields order
            | TyNominal _ as nominal ->
                match Unifier.unalias (RecursiveNominals.underlying nominal) with
                | TyRecord(fields, order) -> written fields order
                | _ -> []
            | _ -> []
        let fieldTy = tools.ToIrType (TyApply(TypeValues.field, [ boxedTy ]))
        let getTy = IrFunction(IrBox, IrBox, [], false)
        let fieldItems =
            fields
            |> List.map (fun (fieldName, fieldType) k ->
                let getter = tools.Request $"__get_{tools.TypeIds.Of(ty, view)}_{fieldName}" (FieldGetting(ty, fieldName))
                let attributes, attributesTy = attributesOf fieldName
                makeList tools attributesTy attributes (fun attributeList ->
                    let values =
                        [ "name", text fieldName; "type", typeId fieldType; "get", closure getter getTy
                          "attributes", Expr.make (Atom attributeList) attributesTy ]
                    makeRecord tools fieldTy (Map.ofList values) k))
        let maker = tools.Request $"__make_{tools.TypeIds.Of(ty, view)}" (MakingRecord ty)
        naming (Some ("fields", fieldItems, fieldTy)) (fun values ->
            let values = values |> Map.add makeMember (closure maker (IrFunction(IrList IrBox, IrBox, [], false)))
            makeRecord tools (tools.ToIrType (TyApply(TypeValues.recordInfo, [ boxedTy ]))) values (tagged TypeValues.recordTag))
    | tag, shape when tag = TypeValues.unionTag ->
        let rec cases (ty: Ty) =
            match Unifier.unalias ty with
            | TyTag(caseName, payload) -> [ caseName, payload ]
            | TyUnion items -> items |> List.collect cases
            | TyNominal _ as nominal -> cases (RecursiveNominals.underlying nominal)
            | _ -> []
        let caseTy = tools.ToIrType (TyApply(TypeValues.case, [ boxedTy ]))
        let getTy = tools.ToIrType (TyFunc(boxedTy, TyUnion [ TyTag("some", Some boxedTy); TyTag("none", None) ], []))
        let caseItems =
            cases shape
            |> List.distinctBy fst
            |> List.map (fun (caseName, payload) k ->
                let getter = tools.Request $"__case_{tools.TypeIds.Of(ty, view)}_{caseName}" (CaseGetting(ty, caseName))
                let payloadTy = payload |> Option.defaultValue (TyPrimitive "unit")
                let builder = tools.Request $"__build_case_{tools.TypeIds.Of(ty, view)}_{caseName}" (CaseBuilding(ty, caseName))
                let values =
                    [ "name", text caseName; "type", typeId payloadTy; "get", closure getter getTy
                      "build", closure builder (IrFunction(IrBox, IrBox, [], false)) ]
                makeRecord tools caseTy (Map.ofList values) k)
        naming (Some ("cases", caseItems, caseTy)) (fun values ->
            makeRecord tools (tools.ToIrType (TyApply(TypeValues.unionInfo, [ boxedTy ]))) values (tagged TypeValues.unionTag))
    | tag, _ -> cannot $"a description with the tag '#{tag}'"

/// The function `__describe`: the description of the type of the number it is given. The descriptions it builds number
/// the types of their fields and elements in turn, so it goes on until every type numbered has its description.
let describingFunction (tools: TypeMatchTools) (functionName: string) : Function =
    let description = descriptionType tools
    let id = tools.NewVar "__id" Param IrInt
    let arms = ResizeArray<Arm>()
    let mutable next = 0
    while next < tools.TypeIds.Count do
        arms.Add { Patterns = [ PInt(int64 next) ]; Body = descriptionOf tools (tools.TypeIds.At next) }
        next <- next + 1
    arms.Add { Patterns = [ PWildcard ]; Body = Expr.make (Crash([ "A type that has no description" ], false)) description }
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ id ]
      Result = description
      Body = Expr.make (Match([ id ], List.ofSeq arms)) description
      Place = None }
/// The name of the function that gives the elements of a value of `collection` in their canonical form: by the type,
/// since collections laid out alike, a list of pairs and a map, have different canonical forms.
let private canonicalName (collection: Ty) = "__canonical_" + Identifiers.sanitizeIdentifier (Unifier.tyToString collection)

/// `atom`, a value of the type `ty`, boxed in its canonical form (see the module's summary), passed to `k`.
let rec private canonical (tools: TypeMatchTools) (ty: Ty) (atom: Atom) (k: Atom -> Expr) : Expr =
    let box (value: Atom) = bindLocal tools "__box" (Expr.make (MakeClosure(tools.Reader (atomType value), 1, [ value ])) IrBox) (AVar >> k)
    match Unifier.unalias ty with
    | Lists.ListType _
    | Sets.SetType _
    | Maps.MapType _ as collection ->
        let elements = tools.Request (canonicalName collection) (Canonicalising collection)
        bindLocal tools "__elements" (Expr.make (Call(elements, [ atom ])) (canonicalElements tools collection)) (AVar >> box)
    | Arrays.ArrayType element ->
        let listTy = Lists.ofType element
        let elements = tools.Request (canonicalName listTy) (Canonicalising listTy)
        let call name args ty = Expr.make (CallRuntime(name, args, true)) ty
        bindLocal tools "__list" (call Intrinsics.arrayToList [ atom ] (tools.ToIrType listTy)) (fun list ->
            bindLocal tools "__elements" (Expr.make (Call(elements, [ AVar list ])) (IrList IrBox)) (fun boxed ->
                bindLocal tools "__array" (call Intrinsics.arrayOfList [ AVar boxed ] (IrArray IrBox)) (AVar >> box)))
    | _ -> box atom

/// The IR type of the collection `collection` in its canonical form: a list of a box for each element, and a map's or a
/// set's store with its keys and values boxed.
and canonicalElements (tools: TypeMatchTools) (collection: Ty) : IrType =
    match Unifier.unalias collection with
    | Maps.MapType _ -> tools.ToIrType (Maps.ofTypes boxedTy boxedTy)
    | Sets.SetType _ -> tools.ToIrType (Sets.ofType boxedTy)
    | _ -> IrList IrBox

/// The function a closure that boxes a value of `ty` in its canonical form is over: it takes the closure's environment
/// and the value.
let canonicalisingValueFunction (tools: TypeMatchTools) (functionName: string) (ty: Ty) : Function =
    let environment = tools.NewVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord ])
    let value = tools.NewVar "__value" Param (tools.ToIrType ty)
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; value ]
      Result = IrBox
      Body = canonical tools ty (AVar value) (fun boxed -> Expr.make (Atom boxed) IrBox)
      Place = None }

/// The function that gives the store of a value of `collection`, a map or a set, with each key and value converted
/// from `from` to `into`, the type each is in the store it is given and the one it gives, by a closure over the
/// function `converter` asks for at the type: the prelude's `Maps.convertedStore`, which keeps the store's shape and
/// hashes as they are. So boxing the keys of a map never hashes a box: nothing hashes or compares a key again.
let storeConvertingFunction
    (tools: TypeMatchTools) (functionName: string) (collection: Ty) (from: Ty -> Ty) (into: Ty -> Ty) (converter: Ty -> string) : Function =
    let closureOf (ty: Ty) =
        let closureTy = IrFunction(tools.ToIrType (from ty), tools.ToIrType (into ty), [], false)
        Expr.make (Atom(AStaticClosure(converter ty, 2, closureTy))) closureTy
    let call (name: string) (args: (Ty * Atom) list) (resultTy: Ty) =
        let instanceTy = TyFunc(TyRecord(args |> List.mapi (fun index (ty, _) -> string index, ty) |> Map.ofList, FieldOrder.none), resultTy, [])
        Expr.make (Call(tools.Callee name instanceTy, List.map snd args)) (tools.ToIrType resultTy)
    let converterTy (ty: Ty) = TyFunc(from ty, into ty, [])
    let given, result, body =
        match Unifier.unalias collection with
        | Maps.MapType(key, value) ->
            let store = tools.NewVar "__store" Param (tools.ToIrType (Maps.ofTypes (from key) (from value)))
            let resultTy = Maps.storeOf (into key) (into value)
            store, tools.ToIrType resultTy,
            bindLocal tools "__keys" (closureOf key) (fun keys ->
                bindLocal tools "__values" (closureOf value) (fun values ->
                    call Maps.convertedStore [ Maps.storeOf (from key) (from value), AVar store; converterTy key, AVar keys; converterTy value, AVar values ] resultTy))
        | Sets.SetType element ->
            let store = tools.NewVar "__store" Param (tools.ToIrType (Sets.ofType (from element)))
            let resultTy = Sets.storeOf (into element)
            store, tools.ToIrType resultTy,
            bindLocal tools "__elements" (closureOf element) (fun elements ->
                call Sets.convertedStore [ Sets.storeOf (from element), AVar store; converterTy element, AVar elements ] resultTy)
        | other -> internalError $"a store of '{Unifier.tyToString other}'"
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ given ]
      Result = result
      Body = body
      Place = None }

/// The function that gives the elements of a list, each boxed in its canonical form.
let private listCanonicalisingFunction (tools: TypeMatchTools) (functionName: string) (elementTy: Ty) (collection: Ty) : Function =
    let given = tools.ToIrType collection
    let element = match given with IrList element -> element | other -> internalError $"canonical elements of '{IrType.show other}'"
    let result = canonicalElements tools collection
    let list = tools.NewVar "__list" Param given
    let head = tools.NewVar "__head" Local element
    let tail = tools.NewVar "__tail" Local given
    let rest k = bindLocal tools "__rest" (Expr.make (Call(functionName, [ AVar tail ])) result) k
    let cons (converted: Atom) = rest (fun rest -> Expr.make (CallRuntime(Helpers.listCons, [ converted; AVar rest ], true)) result)
    let arms =
        [ { Patterns = [ PList([ PBind head ], Some (PBind tail)) ]; Body = canonical tools elementTy (AVar head) cons }
          { Patterns = [ PWildcard ]; Body = Expr.make (MakeList []) result } ]
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ list ]
      Result = result
      Body = Expr.make (Match([ list ], arms)) result
      Place = None }

/// The function that gives a value of `collection` with its elements, or a map's keys and values, each boxed in its
/// canonical form: a list of boxes for a list, and a store of boxes for a map or a set, its shape and hashes as they are.
let canonicalisingFunction (tools: TypeMatchTools) (functionName: string) (collection: Ty) : Function =
    match Unifier.unalias collection with
    | Lists.ListType elementTy -> listCanonicalisingFunction tools functionName elementTy collection
    | Maps.MapType _
    | Sets.SetType _ ->
        let converter (ty: Ty) = tools.Request ("__canonical_value_" + Identifiers.sanitizeIdentifier (Unifier.tyToString ty)) (CanonicalisingValue ty)
        storeConvertingFunction tools functionName collection id (fun _ -> boxedTy) converter
    | other -> internalError $"canonical elements of '{Unifier.tyToString other}'"

/// The environment a closure that captures nothing is given, and the box it is called with.
let getterParameters (tools: TypeMatchTools) =
    tools.NewVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord ]), tools.NewVar "__boxed" Param IrBox

/// The `get` of the field `fieldName` of the record type `recordType`: it takes a value of the type, boxed, and gives
/// the field's value boxed in its canonical form.
let fieldGettingFunction (tools: TypeMatchTools) (functionName: string) (recordType: Ty) (fieldName: string) : Function =
    let environment, boxed = getterParameters tools
    let recordTy = tools.ToIrType recordType
    let slot, fieldTy =
        match IrType.unfold recordTy with
        | IrRecord fields ->
            let slot = fields |> List.findIndex (fst >> (=) fieldName)
            slot, snd fields.[slot]
        | other -> internalError $"the field '{fieldName}' of '{IrType.show other}'"
    let fieldType =
        let rec fieldsOf (ty: Ty) =
            match Unifier.unalias ty with
            | TyRecord(fields, _) -> fields
            | TyNominal _ as nominal -> fieldsOf (RecursiveNominals.underlying nominal)
            | _ -> Map.empty
        (fieldsOf recordType).[fieldName]
    let body =
        bindLocal tools "__record" (Expr.make (CallClosure(boxed, [], Borrowing)) recordTy) (fun record ->
            bindLocal tools "__field" (Expr.make (Field(AVar record, slot)) fieldTy) (fun field ->
                canonical tools fieldType (AVar field) (fun result -> Expr.make (Atom result) IrBox)))
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; boxed ]
      Result = IrBox
      Body = body
      Place = None }

/// The `get` of the case `caseName` of the union `unionType`: it takes a value of the type, boxed, and gives
/// `#some(payload)`, boxed in its canonical form, for a value of that case, and `#none` for any other.
let caseGettingFunction (tools: TypeMatchTools) (functionName: string) (unionType: Ty) (caseName: string) : Function =
    let environment, boxed = getterParameters tools
    let unionTy = tools.ToIrType unionType
    let resultTy = tools.ToIrType (TyUnion [ TyTag("some", Some boxedTy); TyTag("none", None) ])
    let payload =
        let rec find (ty: Ty) =
            match Unifier.unalias ty with
            | TyTag(name, payload) when name = caseName -> Some payload
            | TyUnion items -> items |> List.tryPick find
            | TyNominal _ as nominal -> find (RecursiveNominals.underlying nominal)
            | _ -> None
        find unionType |> Option.flatten
    let some (atom: Atom) = Expr.make (MakeTag("some", atom)) resultTy
    let matched =
        match payload, IrType.unfold unionTy with
        | Some payloadType, IrTags tags ->
            let payloadVar = tools.NewVar (tools.UniqueName "__payload") PatternBound (tags |> List.find (fst >> (=) caseName) |> snd |> Option.get)
            { Patterns = [ PTag(caseName, Some (PBind payloadVar)) ]; Body = canonical tools payloadType (AVar payloadVar) some }
        | _ -> { Patterns = [ PTag(caseName, None) ]; Body = canonical tools (TyPrimitive "unit") AUnit some }
    let others = { Patterns = [ PWildcard ]; Body = Expr.make (Atom(ATag "none")) resultTy }
    let body =
        bindLocal tools "__value" (Expr.make (CallClosure(boxed, [], Borrowing)) unionTy) (fun value ->
            Expr.make (Match([ value ], [ matched; others ])) resultTy)
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ environment; boxed ]
      Result = resultTy
      Body = body
      Place = None }
