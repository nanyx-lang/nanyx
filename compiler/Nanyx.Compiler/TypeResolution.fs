/// Reading the types written in the source: the primitive and built-in types, the names definitions are attached to a
/// type under, a type annotation as a `Ty`, the errors for type names that name no type, and type definitions.
module NyxCompiler.TypeResolution

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.Visibility

let internal primitiveType name =
    TyPrimitive name

let internal literalType literal =
    match literal with
    | StringLit _ -> primitiveType "string"
    | IntLit _ -> primitiveType "int"
    | FloatLit _ -> primitiveType "float"
    | BoolLit _ -> primitiveType "bool"

let internal listType elementTy =
    TyApply("list", [ elementTy ])

let internal numericType =
    TyPrimitive "int"

let internal tupleFieldName index =
    string index

let private lookupPrimitive (name: string) =
    match name with
    | "int"
    | "float"
    | "bool"
    | "string"
    | "unit" -> Some (primitiveType name)
    | "undefined" -> Some Unifier.bottomType
    | _ when SizedNumber.isName name -> Some (primitiveType name)
    | _ -> None

/// Whether `name` is a type the compiler provides, such as `int` or `list`, rather than one the program defines.
/// A type's capitalised name, `List`, is written as its alias, `list`, by `BuiltinTypeNames` before inference.
let internal isBuiltInTypeName (name: string) =
    (lookupPrimitive name).IsSome
    || name = "list"
    || name = "seq"
    || Maps.isTypeName name
    || Sets.isTypeName name

/// Whether the type `name` is attached to exists; `span` is where the definition's name is written.
let internal ensureAttachedTypeExists (state: InferState) (currentModuleOpt: string option) (name: Identifier) (span: SourceSpan) : Result<unit, Diagnostic list> =
    let firstDot = name.IndexOf(".")
    if firstDot <= 0 then
        Ok ()
    else
        let lastDot = name.LastIndexOf(".")
        let candidateTypeName = name.Substring(0, lastDot)
        if state.TypeDefs.ContainsKey candidateTypeName || isBuiltInTypeName candidateTypeName then
            Ok ()
        else
            if firstDot = lastDot then
                match currentModuleOpt with
                | Some currentModule when currentModule = candidateTypeName -> Ok ()
                | _ -> Error [ Diagnostics.at DiagnosticCodes.UnknownAttachedType span $"Unknown attached type '{candidateTypeName}' for definition '{name}'" ]
            else
                Ok ()

/// The names definitions can be attached to a value of type `ty` under, nearest first: each alias it is written
/// with, then the nominal type or the built-in type's name (`string`, `list`). Empty when the type has no name, such
/// as a union, a record written out in full, or a type that is not known yet.
let rec attachedTypeNames (ty: Ty) : string list =
    match ty with
    | TyAlias(name, _, underlying) -> name :: attachedTypeNames underlying
    | TyNominal(id, _, _) -> [ id.Name ]
    | TyPrimitive name -> [ name ]
    | TyApply("list", _) -> [ "list" ]
    | TyApply("seq", _) -> [ "seq" ]
    | Maps.MapType _ -> [ Maps.typeName ]
    | Sets.SetType _ -> [ Sets.typeName ]
    | _ -> []

/// The attached definition `x \funcName` calls: `Name.funcName` for the first name of `x`'s type that has one in
/// scope. None when `funcName` is already qualified, or no name of the type attaches it, so the pipe calls a plain
/// `funcName` instead. Only names in `env` count, so a module imported with `as` is reached as `\t.string.trim`.
let internal tryResolveAttachedFunction (env: TypeEnv) (lhsType: Ty) (funcName: string) : string option =
    if funcName.Contains(".") then
        None
    else
        attachedTypeNames lhsType
        |> List.map (fun typeName -> $"{typeName}.{funcName}")
        |> List.tryFind (fun name -> env.ContainsKey name)

/// The names of the built-in properties, whatever type has them.
let builtInPropertyNames = [ "length" ]

/// The built-in property `name` of a value of type `ty`, and the property's type, if the type has one of that name:
/// a string's or a list's `length`.
let builtInProperty (ty: Ty) (name: string) : (BuiltInProperty * Ty) option =
    match Unifier.unalias ty, name with
    | TyPrimitive "string", "length" -> Some (StringLength, TyPrimitive "int")
    | TyApply("list", _), "length" -> Some (ListLength, TyPrimitive "int")
    | _ -> None

/// The error for reading `memberName` with a dot from a value of type `ty` that has no such field or property.
/// `receiver` is the value as written, if it is a name. A definition attached to the type under that name is
/// reached with a pipe, not a dot, so the message says so when there is one.
let internal unknownMember (env: TypeEnv) (span: SourceSpan) (receiver: string option) (ty: Ty) (memberName: string) : Diagnostic =
    let rec isRecord (ty: Ty) =
        match Unifier.unalias ty with
        | TyRecord _
        | TyOpenRecord _ -> true
        | TyNominal(_, underlying, _) -> isRecord underlying
        | _ -> false
    let missing =
        match receiver with
        | Some name when isRecord ty -> $"Record '{name}' has no member '{memberName}'"
        | None when isRecord ty -> $"Unknown member '{memberName}'"
        | _ -> $"'{Unifier.tyToString ty}' has no property '{memberName}'"
    let message =
        match tryResolveAttachedFunction env ty memberName with
        | Some attached ->
            let piped = receiver |> Option.defaultValue "x"
            $"{missing}. '{attached}' is attached to its type: call it with a pipe, as in `{piped} \\{memberName}`"
        | None -> missing
    Diagnostics.at DiagnosticCodes.UnknownMember span message

/// The alias `name`, defined by `info`, written without arguments: a generic one is applied to its own parameters.
let private aliasType (name: string) (info: TypeDefInfo) : Ty =
    TyAlias(name, info.Parameters |> List.map TyVar, info.Underlying)

/// Whether `info` defines a nominal type.
/// Whether a type named `name` is nominal: its own name starts with `$`, whether or not it is qualified with a module
/// block's name (`money.$Money`).
let private isNominalName (name: string) =
    let own = match name.LastIndexOf '.' with -1 -> name | dot -> name.Substring(dot + 1)
    own.StartsWith("$")

let internal isNominal (info: TypeDefInfo) =
    isNominalName info.Name

/// The type the name `name`, defined by `info`, stands for written without arguments: the nominal type, or the alias.
let internal namedType (name: string) (info: TypeDefInfo) : Ty =
    if isNominal info then TyNominal(TypeDefInfo.nominalId info, info.Underlying, info.IsPrivate) else aliasType name info

/// The type definition a type name written in the module means: none for a name only an aliased import brings in,
/// which has to be written with the alias.
let internal tryFindTypeDef (name: string) (state: InferState) : TypeDefInfo option =
    if state.HiddenTypeNames.Contains name then None else state.TypeDefs |> Map.tryFind name

let internal tryInferAttachedLambdaInput (state: InferState) (name: Identifier) (expr: Expression) : Ty option =
    match expr.Kind with
    | Lambda(args, _, _) when name.Contains(".") && args.Length = 1 ->
        let dotIndex = name.LastIndexOf(".")
        let typeName = name.Substring(0, dotIndex)
        match tryFindTypeDef typeName state with
        | Some info -> Some (namedType typeName info)
        | None -> lookupPrimitive typeName
    | _ -> None

let private isContextDef (modifiers: TypeDefModifier list) =
    modifiers |> List.contains Context

let internal tupleRecordType (items: Ty list) =
    items
    |> List.mapi (fun index ty -> tupleFieldName index, ty)
    |> Map.ofList
    |> TyRecord

let internal inputTypeFromArgs argTys =
    match argTys with
    | [] -> TyPrimitive "unit"
    | [ single ] -> single
    | _ -> tupleRecordType argTys

let private isPrivateType modifiers =
    modifiers |> List.exists (fun modifier -> modifier = TypeDefModifier.Private)


let rec internal typeExprToTy (state: InferState) (typeExpr: TypeExpr) : Ty * InferState =
    match typeExpr.Kind with
    | TypeName name ->
        match lookupPrimitive name with
        | Some prim -> prim, state
        | None ->
            match tryFindTypeDef name state with
            | Some info -> namedType name info, recordTypeReference typeExpr info state
            | None -> TyPrimitive name, state
    | TypeVar name ->
        match state.TypeVars |> Map.tryFind name with
        | Some existing -> TyVar existing, state
        | None ->
            // The variable keeps the annotation's name, so types are shown with the names the user wrote.
            let var = { Id = state.NextId; Name = Some name }
            TyVar var, { state with NextId = state.NextId + 1; TypeVars = state.TypeVars |> Map.add name var }
    // Each `_` is a variable of its own, with no name to share (see `TypeHoles`). It is recorded by node, as an
    // expression's type is, so an editor can show what it was inferred to be.
    | TypeHole ->
        let ty, next = freshVar state
        ty, { next with ExprTypes = next.ExprTypes |> Map.add typeExpr.Id ty }
    | TypeUnit -> TyPrimitive "unit", state
    | TypeTuple items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                let ty, next = typeExprToTy current item
                current <- next
                ty)
        tupleRecordType tys, current
    | TypeRecord fields ->
        let mutable current = state
        let fieldMap =
            fields
            |> List.choose (function
                | TypeField(name, _, typeOpt, _) -> Some (name, typeOpt)
                | TypeMember(name, typeOpt) -> Some (name, typeOpt))
            |> List.map (fun (name, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some t -> typeExprToTy current t
                    | None -> freshVar current
                current <- next
                name, ty)
            |> Map.ofList
        TyRecord fieldMap, current
    | TypeApply(name, args) ->
        let mutable current = state
        let argTys =
            args
            |> List.map (fun arg ->
                let ty, next = typeExprToTy current arg
                current <- next
                ty)
        if name = "list" then
            let elementTy =
                match argTys with
                | [] -> TyPrimitive "unit"
                | [single] -> single
                | multiple -> tupleRecordType multiple
            TyApply("list", [elementTy]), current
        elif Maps.isTypeName name && argTys.Length = 2 then
            Maps.ofTypes argTys.[0] argTys.[1], current
        elif Sets.isTypeName name && argTys.Length = 1 then
            Sets.ofType argTys.[0], current
        else
            match tryFindTypeDef name current with
            | Some info when not (isNominal info) ->
                let current = recordTypeReference typeExpr info current
                if info.Parameters.Length = argTys.Length then
                    TyAlias(name, argTys, Unifier.applyToParameters info.Parameters argTys info.Underlying), current
                else
                    TyApply(name, argTys), current
            | Some info -> TyApply(name, argTys), recordTypeReference typeExpr info current
            | None -> TyApply(name, argTys), current
    | TypeFunc(arg, ret) ->
        let argTy, next = typeExprToTy state arg
        let retTy, nextState = typeExprToTy next ret
        TyFunc(argTy, retTy, []), nextState
    | TypeTag(name, payloadOpt) ->
        match payloadOpt with
        | Some payload ->
            let payloadTy, next = typeExprToTy state payload
            TyTag(name, Some payloadTy), next
        | None -> TyTag(name, None), state
    | TypeUnion items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                let ty, next = typeExprToTy current item
                current <- next
                ty)
        // A union's members are tags: an alias of a tag or of a union is spliced in as what it stands for.
        let members =
            tys
            |> List.collect (fun ty ->
                match Unifier.unalias ty with
                | TyUnion inner -> inner
                | TyTag _ as tag -> [ tag ]
                | _ -> [ ty ])
        TyUnion members, current
    | TypeIntersection items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                let ty, next = typeExprToTy current item
                current <- next
                ty)
        TyUnion tys, current
    | TypeContext _ ->
        let ty, next = freshVar state
        ty, next
    | TypeWithContext(contexts, inner) ->
        let innerTy, next = typeExprToTy state inner
        WrittenContexts.withContexts typeExprToTy next contexts innerTy
    | TypeConstraint(_, baseType, _) ->
        typeExprToTy state baseType
    | TypeWhere(baseType, _) ->
        typeExprToTy state baseType
    // The value was inferred before the annotation was read (see `inferTypeOfValues`), and its type is the
    // one it was given there: a polymorphic value's type instantiated afresh. A `typeof` where there are no
    // values to name, such as in a type definition, is reported elsewhere.
    | TypeOf value ->
        match state.ExprTypes |> Map.tryFind value.Id with
        | Some ty -> ty, state
        | None -> freshVar state

/// The values named by each `typeof` in `typeExpr`, in the order they are written.
let rec internal typeOfValues (typeExpr: TypeExpr) : Expression list =
    let ofList items = items |> List.collect typeOfValues
    match typeExpr.Kind with
    | TypeOf value -> [ value ]
    | TypeName _ | TypeVar _ | TypeHole | TypeUnit -> []
    | TypeTuple items | TypeUnion items | TypeIntersection items | TypeContext items -> ofList items
    | TypeRecord fields ->
        fields
        |> List.collect (function
            | TypeField(_, _, Some t, _) | TypeMember(_, Some t) -> typeOfValues t
            | _ -> [])
    | TypeApply(_, args) -> ofList args
    | TypeFunc(arg, ret) -> typeOfValues arg @ typeOfValues ret
    | TypeTag(_, payload) -> payload |> Option.map typeOfValues |> Option.defaultValue []
    | TypeWithContext(contexts, inner) -> ofList contexts @ typeOfValues inner
    | TypeConstraint(_, inner, _) | TypeWhere(inner, _) -> typeOfValues inner

/// The error for each `typeof` in the type definition `name`: a type definition is not in the scope of any value.
let internal typeOfInTypeDef (name: Identifier) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    body :: (parameters |> List.choose snd)
    |> List.collect typeOfValues
    |> List.map (fun value ->
        Diagnostics.at DiagnosticCodes.MisplacedTypeof value.Span $"'typeof' cannot be used in the definition of the type '{name}', only in the type of a value")

/// The type names written in `typeExpr` that name no type here: `Strnig`, or `$Email` where only `m.$Email` is in
/// scope. `parameters` are the names of the parameters of the type definition `typeExpr` is the body of, if it is
/// one. Contexts are checked apart.
let rec private typeNamesIn (typeExpr: TypeExpr) : (Identifier * SourceSpan) list =
    let ofType = typeNamesIn
    match typeExpr.Kind with
    | TypeName name -> [ name, typeExpr.Span ]
    | TypeApply(name, args) -> (name, typeExpr.Span) :: (args |> List.collect ofType)
    | TypeTuple items
    | TypeUnion items
    | TypeIntersection items -> items |> List.collect ofType
    | TypeRecord fields ->
        fields
        |> List.collect (function
            | TypeField(_, _, Some t, _)
            | TypeMember(_, Some t) -> ofType t
            | _ -> [])
    | TypeFunc(arg, ret) -> ofType arg @ ofType ret
    | TypeTag(_, Some payload)
    | TypeWithContext(_, payload)
    | TypeConstraint(_, payload, _)
    | TypeWhere(payload, _) -> ofType payload
    | _ -> []

/// The error for each type name written in `typeExprs` that names no type, once per name, and for each that names a
/// type the `module name =` block it is written in cannot see.
let private unknownTypeErrors (state: InferState) (parameters: Set<Identifier>) (typeExprs: TypeExpr list) : Diagnostic list =
    let named = typeExprs |> List.collect typeNamesIn |> List.filter (fun (name, _) -> not (isBuiltInTypeName name || parameters.Contains name))
    let unknown =
        named
        |> List.filter (fun (name, _) -> (tryFindTypeDef name state).IsNone)
        |> List.distinctBy fst
        |> List.map (fun (typeName, span) -> Diagnostics.at DiagnosticCodes.UnknownType span $"Unknown type '{typeName}'")
    let outside =
        named
        |> List.choose (fun (name, span) -> tryFindTypeDef name state |> Option.map (fun info -> name, info, span))
        |> List.collect (fun (name, info, span) -> blockIsolationAt state name info.Symbol span)
        |> List.distinctBy _.Message
    unknown @ outside

/// Whether every type an annotation, such as a def's or a lambda parameter's, names exists.
let internal ensureTypesExist (state: InferState) (typeExprs: TypeExpr list) : Result<unit, Diagnostic list> =
    match unknownTypeErrors state Set.empty typeExprs with
    | [] -> Ok ()
    | errors -> Error errors

/// The error for each type name the definition of a type names that names no type: its body's, and its parameters'
/// constraints'. Its parameters are in scope in both.
let internal unknownTypesInTypeDef (state: InferState) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    unknownTypeErrors state (parameters |> List.map fst |> Set.ofList) (body :: (parameters |> List.choose snd))

/// Whether the definition of the type `name` can name the type itself: an alias can, unless it is a context, or a
/// type of that name is already in scope, which the name then means.
let private canReferToItself (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) =
    not (isNominalName name) && not (isContextDef modifiers) && (tryFindTypeDef name state).IsNone

/// The type the definition of `name`, with the parameters `paramVars`, stands for. An alias that names itself inside
/// the payload of one of its tags is recursive (see `RecursiveAliases`); one that names itself anywhere else reads the
/// name as it did before the alias was defined, and `unguardedSelfReferences` reports it.
let private readTypeDefBody (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (paramVars: TyVar list) (body: TypeExpr) =
    let plain () = typeExprToTy state body
    if not (canReferToItself state name modifiers) then
        plain ()
    else
        let self =
            { Name = name
              Home = state.Home
              Parameters = paramVars
              Underlying = RecursiveAliases.selfMarker paramVars
              IsPrivate = false
              IsContext = false
              Symbol = None
              Members = Map.empty }
        let ty, next = typeExprToTy { state with TypeDefs = state.TypeDefs |> Map.add name self } body
        match RecursiveAliases.selfReferences ty with
        | true, false -> RecursiveAliases.close paramVars ty, { next with TypeDefs = next.TypeDefs |> Map.remove name }
        | true, true -> plain ()
        | false, _ -> ty, { next with TypeDefs = next.TypeDefs |> Map.remove name }

/// The error for each place the alias `name` names itself other than inside the payload of one of its tags (see
/// `RecursiveAliases.unguardedReferences`).
let internal unguardedSelfReferences (name: Identifier) (modifiers: TypeDefModifier list) (body: TypeExpr) : Diagnostic list =
    if isNominalName name || isContextDef modifiers then [] else RecursiveAliases.unguardedReferences name body

let internal registerTypeDef (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
    let originalTypeVars = state.TypeVars
    let mutable current = state
    let paramEntries =
        parameters
        |> List.map (fun (paramName, _) ->
            let tyVar, next = freshVar current
            current <- next
            match tyVar with
            // The variable keeps the parameter's name, so the type is shown as it is written.
            | TyVar var -> paramName, { var with Name = Some paramName }
            | _ -> failwith "Expected TyVar")
    let updatedTypeVars =
        paramEntries
        |> List.fold (fun acc (paramName, var) -> acc |> Map.add paramName var) current.TypeVars
    let stateWithParams = { current with TypeVars = updatedTypeVars }
    let isPrivate = isPrivateType modifiers
    let paramVars = paramEntries |> List.map snd
    let underlyingTy, next = readTypeDefBody stateWithParams name modifiers paramVars body
    let selfReferences = if canReferToItself stateWithParams name modifiers then RecursiveAliases.namesOfItself name body |> List.map fst else []
    let nextNominals =
        if isNominalName name then next.LocalNominals |> Set.add name else next.LocalNominals
    let nextPrivate =
        if isNominalName name && isPrivate then next.LocalPrivateNominals |> Set.add name else next.LocalPrivateNominals
    let nextContexts =
        if isContextDef modifiers then next.ContextDefs |> Map.add name body else next.ContextDefs
    let isExport = modifiers |> List.contains Export
    // A type is named in types, and a context's member is in scope only where the context is required, so neither
    // has a scope of its own.
    let declareUnscoped declarer holder symbolName kind state =
        let symbol, declared = declareSymbol declarer holder symbolName kind isExport None state
        symbol, { declared with Scopes = symbol |> Option.map (fun id -> declared.Scopes |> Map.remove id) |> Option.defaultValue declared.Scopes }
    let symbol, next = declareUnscoped TypeDefBody body.Id name TypeDefinition next
    let mutable next = next
    let members =
        match body.Kind with
        | TypeRecord fields when isContextDef modifiers ->
            fields
            |> List.choose (function
                | TypeField(memberName, _, _, _) ->
                    let memberSymbol, declared = declareUnscoped RecordTypeFields body.Id memberName ContextMember next
                    next <- declared
                    memberSymbol |> Option.map (fun id -> memberName, id)
                | TypeMember _ -> None)
            |> Map.ofList
        | _ -> Map.empty
    let info =
        { Name = name
          Home = next.Home
          Parameters = paramVars
          Underlying = underlyingTy
          IsPrivate = isPrivate
          IsContext = isContextDef modifiers
          Symbol = symbol
          Members = members }
    // A name of the type in its own definition refers to it, as any other does.
    let next = selfReferences |> List.fold (fun state typeExpr -> recordTypeReference typeExpr info state) next
    { next with
        TypeVars = originalTypeVars
        TypeDefs = next.TypeDefs |> Map.add name info
        HiddenTypeNames = next.HiddenTypeNames |> Set.remove name
        ContextDefs = nextContexts
        LocalNominals = nextNominals
        LocalPrivateNominals = nextPrivate }
