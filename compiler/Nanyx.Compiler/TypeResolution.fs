/// Reading the types written in the source: the primitive and built-in types, the names definitions are attached to a
/// type under, a type annotation as a `Ty`, the errors for type names that name no type, and type definitions.
module NyxCompiler.TypeResolution

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.Visibility

/// The names of the type variables written in an annotation, in order of first appearance.
let rec internal annotationVarNames (typeExpr: TypeExpr) : string list =
    let ofChildren () = TypeExpr.children typeExpr |> List.collect annotationVarNames
    let names =
        match typeExpr.Kind with
        | TypeVar name -> [ name ]
        // Only the type a context set is written on is read, not the contexts.
        | TypeContext _ -> []
        | TypeWithContext(_, inner) -> annotationVarNames inner
        // The name of an open record's other fields is a type variable like any other.
        | TypeOpenRecord(_, row) -> ofChildren () @ Option.toList row
        | _ -> ofChildren ()
    names |> List.distinct

let internal primitiveType name =
    TyPrimitive name

let internal literalType literal =
    match literal with
    | StringLit _ -> primitiveType "string"
    | IntLit _ -> primitiveType "int"
    | FloatLit _ -> primitiveType "float"
    | BoolLit _ -> primitiveType "bool"
    // A number with a unit is a measured value, which `Infer` types with its unit's dimension.
    | UnitLit _ -> Unifier.errorType

let internal listType elementTy = Lists.ofType elementTy

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
    | _ when name = Chars.name -> Some Chars.ty
    | "undefined" -> Some Unifier.bottomType
    | _ when SizedNumber.isName name -> Some (primitiveType name)
    | _ -> None

/// Whether `name` is a type the compiler provides, such as `int` or `list`, rather than one the program defines.
/// A type's capitalised name, `List`, is written as its alias, `list`, by `BuiltinTypeNames` before inference.
let internal isBuiltInTypeName (name: string) =
    (lookupPrimitive name).IsSome
    || Lists.isTypeName name
    || name = CellPassingSyntax.typeName
    || Maps.isTypeName name
    || Sets.isTypeName name
    || name = Maps.storeName
    || Arrays.isTypeName name
    || Boxes.isTypeName name
    || MutableArrays.isTypeName name
    || TypeValues.isTypeName name
    || TypeValues.isDescriptionType name

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
                | _ -> Error [ Diagnostics.at DiagnosticCodes.UnknownAttachedType span $"'{name}' is attached to '{candidateTypeName}', which names no type in scope" |> Diagnostics.about candidateTypeName ]
            else
                Ok ()

/// The names definitions can be attached to a value of type `ty` under, nearest first: each alias it is written
/// with, then the nominal type or the built-in type's name (`string`, `list`). Empty when the type has no name, such
/// as a union, a record written out in full, or a type that is not known yet.
let rec attachedTypeNames (ty: Ty) : string list =
    match ty with
    | TyAlias(name, _, underlying) -> name :: attachedTypeNames underlying
    | TyNominal(id, _, _, _) -> [ id.Name ]
    | TyPrimitive name -> [ name ]
    | Lists.ListType _ -> [ Lists.typeName ]
    | Maps.MapType _ -> [ Maps.typeName ]
    | Sets.SetType _ -> [ Sets.typeName ]
    | Arrays.ArrayType _ -> [ Arrays.typeName ]
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
        |> List.tryFind (fun name -> env.Names.ContainsKey name)

/// The names of the built-in properties, whatever type has them.
let builtInPropertyNames = [ "length" ]

/// The built-in property `name` of a value of type `ty`, and the property's type, if the type has one of that name:
/// a string's or a list's `length`.
let builtInProperty (ty: Ty) (name: string) : (BuiltInProperty * Ty) option =
    match Unifier.unalias ty, name with
    | TyPrimitive "string", "length" -> Some (StringLength, TyPrimitive "int")
    | Lists.ListType _, "length" -> Some (ListLength, TyPrimitive "int")
    | _ -> None

/// The error for reading `memberName` with a dot from a value of type `ty` that has no such field or property.
/// `receiver` is the value as written, if it is a name. A definition attached to the type under that name is
/// reached with a pipe, not a dot, so the message says so when there is one; otherwise a field of the record whose name
/// is close is suggested, with a fix that writes it.
let internal unknownMember (env: TypeEnv) (span: SourceSpan) (receiver: string option) (ty: Ty) (memberName: string) : Diagnostic =
    let rec recordFields (ty: Ty) =
        match Unifier.unalias ty with
        | TyRecord(fields, _)
        | TyOpenRecord(fields, _) -> Some fields
        | RecursiveNominals.Nominal(_, _, underlying, _) -> recordFields underlying
        | _ -> None
    let isRecord ty = (recordFields ty).IsSome
    let missing =
        match receiver with
        | Some name when isRecord ty -> $"The record '{name}' has no member '{memberName}'"
        | None when isRecord ty -> $"The record has no member '{memberName}'"
        | _ -> $"'{Unifier.typeText Map.empty ty}' has no property '{memberName}'"
    match tryResolveAttachedFunction env ty memberName with
    | Some attached ->
        let piped = receiver |> Option.defaultValue "x"
        Diagnostics.at DiagnosticCodes.UnknownMember span $"{missing}, but '{attached}' is attached to its type: call it with a pipe, as in `{piped} \\{memberName}`"
        |> Diagnostics.about memberName
    | None ->
        let fields = recordFields ty |> Option.map (Map.keys >> List.ofSeq) |> Option.defaultValue []
        NameSuggestions.unknown DiagnosticCodes.UnknownMember span missing fields memberName

/// The alias `name`, defined by `info`, written without arguments: a generic one is applied to its own parameters.
let private aliasType (name: string) (info: TypeDefInfo) : Ty =
    TyAlias(name, info.Parameters |> List.map TyVar, info.Underlying)

/// Whether `info` defines a nominal type: one declared `protected` or `private`.
let internal isNominal (info: TypeDefInfo) =
    info.IsNominal

/// The type variable an annotation names `name`: the one it already stands for in this annotation, or a new one. The
/// variable keeps the annotation's name, so types are shown with the names the user wrote.
let private namedTypeVar (state: InferState) (name: Identifier) : TyVar * InferState =
    match state.TypeVars |> Map.tryFind (WrittenVar name) with
    | Some existing -> existing, state
    | None ->
        let var = TyVar.ordinary state.NextId (Some name)
        var, { state with NextId = state.NextId + 1; TypeVars = state.TypeVars |> Map.add (WrittenVar name) var }
/// The nominal type `info` defines, applied to `argTys`: made from its definition's type with them for its parameters.
/// A type in a scoped context's scope has the context, of its scope, as a last argument (see `TypeDefInfo.ScopedBy`).
let internal nominalType (info: TypeDefInfo) (argTys: Ty list) : Ty =
    let hidden = List.skip (min argTys.Length info.Parameters.Length) argTys
    // The scope, of a type in a scoped context's scope, and the contexts, of a type generic in contexts, each for the
    // variable the type's body writes it as.
    let scope =
        match info.Scope, hidden with
        | Some scope, FunctionContexts.ScopedContext(_, scopeTy) :: _ -> [ scope, scopeTy ]
        | _ -> []
    let contexts =
        match info.ContextVariable, List.tryLast hidden with
        | Some variable, Some (FunctionContexts.Carried contexts) -> [ variable, TyUnion contexts ]
        | _ -> []
    let parameters, arguments =
        (List.zip (List.truncate argTys.Length info.Parameters) (List.truncate info.Parameters.Length argTys)) @ scope @ contexts |> List.unzip
    let underlying =
        RecursiveNominals.instantiate info.Underlying argTys
        |> Option.defaultWith (fun () -> Unifier.applyToParameters parameters arguments info.Underlying)
    TyNominal(TypeDefInfo.nominalId info, argTys, underlying, info.IsPrivate)

/// The contexts a type generic in contexts is written with, as its last argument (see `FunctionContexts.carried`):
/// none for a type written without them, the pure one.
let private writtenContexts (info: TypeDefInfo) (contexts: Ty list) : Ty list =
    match info.ContextVariable with
    | Some _ -> [ FunctionContexts.carried contexts ]
    | None -> []

/// The scope a type in a scoped context's scope is of where it is written: the one the signature's context of that
/// name has (see `WrittenContexts`), as the scoped context itself.
let private writtenScope (state: InferState) (info: TypeDefInfo) : Ty list * InferState =
    match info.ScopedBy with
    | Some context ->
        let scope, next = signatureScope state context
        [ FunctionContexts.scoped context scope ], next
    | None -> [], state

/// The nominal type `info` defines, applied to a new variable for each of its parameters, and of a new scope if it
/// has one: the type of a value its constructor makes, or a pattern takes apart. Also gives what that type is made
/// from, and the scoped context of the value's scope, if it has one.
let internal freshNominalType (state: InferState) (info: TypeDefInfo) : Ty * Ty * Ty option * InferState =
    let argTys, next = freshVars info.Parameters state
    let scoped, scopeArgument, next =
        match info.ScopedBy with
        | Some context ->
            let scope, next = freshVar next
            Some (FunctionContexts.scoped context scope), [ scope ], next
        | None -> None, [], next
    // A value's contexts are those of the functions it is made with.
    let contexts, contextArgument, next =
        match info.ContextVariable with
        | Some _ ->
            let variable, next = freshVar next
            [ FunctionContexts.carried [ variable ] ], [ variable ], next
        | None -> [], [], next
    let args = argTys @ Option.toList scoped @ contexts
    let underlying =
        RecursiveNominals.instantiate info.Underlying args
        |> Option.defaultWith (fun () ->
            Unifier.applyToParameters (info.Parameters @ Option.toList info.Scope @ Option.toList info.ContextVariable) (argTys @ scopeArgument @ contextArgument) info.Underlying)
    let nominal = TyNominal(TypeDefInfo.nominalId info, args, underlying, info.IsPrivate)
    nominal, RecursiveNominals.underlying nominal, scoped, next

/// The alias `name`, defined by `info`, applied to a new variable for each of its parameters: the type of a value its
/// constructor makes. Its own parameters would make every value one type, and an imported alias's are numbered by the
/// module it came from, so they can have the ids of this module's variables.
let internal freshAliasType (state: InferState) (name: string) (info: TypeDefInfo) : Ty * InferState =
    let argTys, next = freshVars info.Parameters state
    TyAlias(name, argTys, Unifier.applyToParameters info.Parameters argTys info.Underlying), next

/// The type the name `name`, defined by `info`, stands for written without arguments: the nominal type, or the alias.
/// A generic one is applied to its own parameters.
let internal namedType (state: InferState) (name: string) (info: TypeDefInfo) : Ty * InferState =
    if isNominal info then
        let scope, next = writtenScope state info
        nominalType info ((info.Parameters |> List.map TyVar) @ scope @ writtenContexts info []), next
    else aliasType name info, state

/// The type definition a type name written in the module means: none for a name only an aliased import brings in,
/// which has to be written with the alias.
let internal tryFindTypeDef (name: string) (state: InferState) : TypeDefInfo option =
    if state.HiddenTypeNames.Contains name then None else state.TypeDefs |> Map.tryFind name

/// The type the def `name` is attached to, when its value `expr` is a lambda of one parameter: what the parameter is
/// taken to be if its body allows (see `Infer.inferDefValue`).
let internal tryInferAttachedLambdaInput (state: InferState) (name: Identifier) (expr: Expression) : Ty option =
    match expr.Kind with
    | Lambda(args, _, _) when name.Contains(".") && args.Length = 1 ->
        let dotIndex = name.LastIndexOf(".")
        let typeName = name.Substring(0, dotIndex)
        match tryFindTypeDef typeName state with
        // A type in a scoped context's scope is of the scope its parameter's annotation gives it, if any.
        | Some { ScopedBy = Some _ } -> None
        | Some info -> Some (fst (namedType state typeName info))
        | None -> lookupPrimitive typeName
    | _ -> None

let private isContextDef (modifiers: TypeDefModifier list) =
    modifiers |> List.contains Context

let internal tupleRecordType (items: Ty list) =
    items
    |> List.mapi (fun index ty -> tupleFieldName index, ty)
    |> fun fields -> TyRecord(Map.ofList fields, FieldOrder.none)

let internal inputTypeFromArgs argTys =
    match argTys with
    | [] -> TyPrimitive "unit"
    | [ single ] -> single
    | _ -> tupleRecordType argTys

let private isPrivateType modifiers =
    modifiers |> List.exists (fun modifier -> modifier = TypeDefModifier.Private)

/// The order the fields of a record type are written in (see `FieldOrder`).
let private writtenOrder (fields: TypeRecordField list) =
    FieldOrder(fields |> List.choose (function TypeField(name, _, _, _) -> Some name | TypeFieldAttribute _ -> None))

let rec internal typeExprToTy (state: InferState) (typeExpr: TypeExpr) : Ty * InferState =
    match typeExpr.Kind with
    | TypeName name ->
        match lookupPrimitive name with
        | Some prim -> prim, state
        | None ->
            match tryFindTypeDef name state with
            // A dimension is a measured value: `Bytes` of the storage it is constrained to, and `Time` of a storage of its
            // own, as a `_` is (see `Units`). A derived one, `Speed`, is an alias of its dimensions.
            | Some info when info.IsDimension && info.IsNominal ->
                let state = recordTypeReference typeExpr info state
                match info.Parameters with
                | [] -> TyMeasure(Dimension.ofBase (TypeDefInfo.nominalId info), info.Underlying), state
                | _ ->
                    let storage, next = freshVar state
                    TyMeasure(Dimension.ofBase (TypeDefInfo.nominalId info), storage), next
            // A derived dimension, `Speed`, is what it names, `Length / Time`, of a storage of its own.
            | Some info when info.IsDimension ->
                let storage, next = freshVar (recordTypeReference typeExpr info state)
                Unifier.applyToParameters info.Parameters [ storage ] info.Underlying, next
            | Some info -> namedType (recordTypeReference typeExpr info state) name info
            | None when TypeValues.descriptionArities |> Map.tryFind name = Some 0 -> TyApply(name, []), state
            | None -> TyPrimitive name, state
    | TypeVar name ->
        let var, next = namedTypeVar state name
        TyVar var, next
    // Each `_` is a variable of its own, with no name to share (see `TypeHoles`). It is recorded by node, as an
    // expression's type is, so an editor can show what it was inferred to be, and an annotation read again (the
    // contexts it declares are read for their members, and again for what their instance fixes) reads the same one.
    | TypeHole ->
        match state.ExprTypes |> Map.tryFind typeExpr.Id with
        | Some ty -> ty, state
        | None ->
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
        let fieldMap, next = recordFieldTypes state fields
        TyRecord(fieldMap, writtenOrder fields), next
    // `...r` is the type variable `r`, shared with the rest of the annotation; `...` is a variable of its own.
    | TypeOpenRecord(fields, row) ->
        let fieldMap, next = recordFieldTypes state fields
        let rowVar, next =
            match row with
            | Some name -> namedTypeVar next name
            | None -> (TyVar.ordinary next.NextId None), { next with NextId = next.NextId + 1 }
        TyOpenRecord(fieldMap, rowVar), next
    | TypeApply(name, args) ->
        let mutable current = state
        let argTys =
            args
            |> List.map (fun arg ->
                let ty, next = typeExprToTy current arg
                current <- next
                ty)
        if Lists.isTypeName name then
            let elementTy =
                match argTys with
                | [] -> TyPrimitive "unit"
                | [single] -> single
                | multiple -> tupleRecordType multiple
            Lists.ofType elementTy, current
        elif Maps.isTypeName name && argTys.Length = 2 then
            Maps.ofTypes argTys.[0] argTys.[1], current
        elif Sets.isTypeName name && argTys.Length = 1 then
            Sets.ofType argTys.[0], current
        elif name = Maps.storeName && argTys.Length = 2 then
            Maps.storeOf argTys.[0] argTys.[1], current
        elif Arrays.isTypeName name && argTys.Length = 1 then
            Arrays.ofType argTys.[0], current
        elif Boxes.isTypeName name && argTys.Length = 1 then
            Boxes.ofType argTys.[0], current
        elif MutableArrays.isTypeName name && argTys.Length = 1 then
            MutableArrays.ofType argTys.[0], current
        elif TypeValues.isTypeName name && argTys.Length = 1 && (tryFindTypeDef name current).IsNone then
            TypeValues.ofType argTys.[0], current
        elif TypeValues.descriptionArities |> Map.tryFind name = Some argTys.Length && (tryFindTypeDef name current).IsNone then
            TyApply(name, argTys), current
        // `mut T`: a cell of the `memory` block whose `Memory` the signature writes, as `[Memory] mut int -> ()` says
        // (see `WrittenContexts.read`). A `mut cell` parameter's own annotation, which has no source, is of a scope of
        // its own: the cell given for it says which. A signature inference writes out has no source either, but what
        // its cells hold is written by inference too, and its cells share the signature's scope.
        elif name = CellPassingSyntax.typeName && argTys.Length = 1 then
            let scope, current =
                match Some typeExpr with
                | CellPassingSyntax.CellParameter held when held |> Option.forall (fun held -> not (SourceSpan.isNone held.Span)) -> freshVar current
                | _ -> signatureScope current FunctionContexts.cellContext
            FunctionContexts.cellType argTys.[0] scope, current
        else
            match tryFindTypeDef name current with
            // `Time(a)`: a measured value of the storage written. A constrained dimension's is its own, and writing
            // another is an error (see `DimensionSignatures.withStorages`).
            | Some info when info.IsDimension && info.IsNominal ->
                let storage =
                    match info.Parameters, argTys with
                    | [ _ ], [ storage ] -> storage
                    | _ -> info.Underlying
                TyMeasure(Dimension.ofBase (TypeDefInfo.nominalId info), storage), recordTypeReference typeExpr info current
            // `Speed(float)`: the derived dimension it names, `Length / Time`, stored as written, with no alias around it,
            // so it is one type with `Length(float) / Time(float)` wherever either is written.
            | Some info when info.IsDimension && info.Parameters.Length = argTys.Length ->
                Unifier.applyToParameters info.Parameters argTys info.Underlying, recordTypeReference typeExpr info current
            | Some info when not (isNominal info) ->
                let current = recordTypeReference typeExpr info current
                if info.Parameters.Length = argTys.Length then
                    TyAlias(name, argTys, Unifier.applyToParameters info.Parameters argTys info.Underlying), current
                else
                    TyApply(name, argTys), current
            // A context required with its arguments stays applied to them (see `FunctionContexts`).
            | Some info when not info.IsContext && info.Parameters.Length = argTys.Length ->
                let scope, current = writtenScope (recordTypeReference typeExpr info current) info
                nominalType info (argTys @ scope @ writtenContexts info []), current
            | Some info -> TyApply(name, argTys), recordTypeReference typeExpr info current
            | None -> TyApply(name, argTys), current
    // `Length / Time`: the dimensions of both sides multiplied or divided, of the storage they share, which
    // `DimensionSignatures` checks is one. A side that is no dimension is an error there too.
    | TypeProduct(left, right)
    | TypeQuotient(left, right) ->
        let leftTy, current = typeExprToTy state left
        let rightTy, current = typeExprToTy current right
        let combine = match typeExpr.Kind with TypeProduct _ -> Dimension.multiply | _ -> Dimension.divide
        match Unifier.unalias leftTy, Unifier.unalias rightTy with
        | TyMeasure(leftDimension, storage), TyMeasure(rightDimension, _) -> Dimension.measured (combine leftDimension rightDimension) storage, current
        | _ -> Unifier.errorType, current
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
        match innerTy with
        // A type generic in contexts carries those written in front of it, `[FileSystem] Stream(string)`.
        | FunctionContexts.CarryingNominal(id, ownArgs, carried) ->
            match tryFindNominalDef id next with
            | Some info ->
                match WrittenContexts.withContexts typeExprToTy next contexts (TyFunc(TyPrimitive "unit", TyPrimitive "unit", carried)) with
                | TyFunc(_, _, written), next -> nominalType info (ownArgs @ writtenContexts info written), next
                | _, next -> innerTy, next
            | None -> innerTy, next
        | _ -> WrittenContexts.withContexts typeExprToTy next contexts innerTy
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
    // `AssociatedTypes` writes these away before inference, and reports the ones it leaves.
    | TypeNamedArgument(_, value) -> typeExprToTy state value
    // `type a`, the old form of a parameter of type `Type(a)`.
    | TypeArgument operand ->
        let standsFor, next = typeExprToTy state operand
        TypeValues.ofType standsFor, next
    | TypeKeyword
    | TypeAssociated _ -> freshVar state

/// The types of the fields of a record type, by name. A field written without a type is a fresh variable.
and private recordFieldTypes (state: InferState) (fields: TypeRecordField list) : Map<string, Ty> * InferState =
    let mutable current = state
    let fieldMap =
        fields
        |> List.choose (function
            | TypeField(name, _, typeOpt, _) -> Some (name, typeOpt)
            | TypeFieldAttribute _ -> None)
        |> List.map (fun (name, typeOpt) ->
            let ty, next =
                match typeOpt with
                | Some t -> typeExprToTy current t
                | None -> freshVar current
            current <- next
            name, ty)
        |> Map.ofList
    fieldMap, current

/// The values named by each `typeof` in `typeExpr`, in the order they are written.
let rec internal typeOfValues (typeExpr: TypeExpr) : Expression list =
    match typeExpr.Kind with
    | TypeOf value -> [ value ]
    | _ -> TypeExpr.children typeExpr |> List.collect typeOfValues

/// The error for each `typeof` in the type definition `name`: a type definition is not in the scope of any value.
let internal typeOfInTypeDef (name: Identifier) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    body :: (parameters |> List.choose snd)
    |> List.collect typeOfValues
    |> List.map (fun value ->
        Diagnostics.at DiagnosticCodes.MisplacedTypeof value.Span $"'typeof' cannot be used in the definition of the type '{name}', only in the type of a value")

/// The type names written in `typeExpr` that name no type here: `Strnig`, or `Email` where only `m.Email` is in
/// scope. `parameters` are the names of the parameters of the type definition `typeExpr` is the body of, if it is
/// one. Contexts are checked apart.
let rec internal typeNamesIn (typeExpr: TypeExpr) : (Identifier * SourceSpan) list =
    let ofType = typeNamesIn
    match typeExpr.Kind with
    | TypeName name -> [ name, typeExpr.Span ]
    | TypeApply(name, args) -> (name, typeExpr.Span) :: (args |> List.collect ofType)
    | TypeTuple items
    | TypeUnion items
    | TypeIntersection items -> items |> List.collect ofType
    | TypeRecord fields
    | TypeOpenRecord(fields, _) ->
        fields
        |> List.collect (function
            | TypeField(_, _, Some t, _) -> ofType t
            | _ -> [])
    | TypeFunc(arg, ret)
    | TypeProduct(arg, ret)
    | TypeQuotient(arg, ret) -> ofType arg @ ofType ret
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
        |> List.map (fun (typeName, span) ->
            let own = typeName.Substring(typeName.LastIndexOf '.' + 1)
            if own.Length > 1 && System.Char.IsLower own[0] then
                Diagnostics.at DiagnosticCodes.InvalidTypeName span
                    $"'{typeName}' is not a type: a type's name starts with a capital letter, and a lowercase name longer than one letter is reserved for associated types"
                |> Diagnostics.about typeName
            else
                Diagnostics.at DiagnosticCodes.UnknownType span
                    ($"'{typeName}' is not in scope" + StandardLibraryImports.hint state.StandardLibraryExports.Value typeName)
                |> Diagnostics.about typeName)
    let outside =
        named
        |> List.choose (fun (name, span) -> tryFindTypeDef name state |> Option.map (fun info -> name, info, span))
        |> List.collect (fun (name, info, span) -> blockIsolationAt state name info.Symbol span)
        |> List.distinctBy _.Message
    // A context is not a type: its name is written only in a context list and as the constructor in a `use`.
    let contexts =
        named
        |> List.choose (fun (name, span) ->
            match tryFindTypeDef name state with
            | Some info when info.IsContext ->
                // The name only, not a context's arguments.
                let span = { span with End = min span.End (span.Start + name.Length) }
                Some (Diagnostics.at DiagnosticCodes.ContextAsType span
                          $"'{name}' is a context, not a type: it can only be required, as in `[{name}]`, or loaded with `use`"
                      |> Diagnostics.about name)
            | _ -> None)
    // Only a function type carries contexts (see `WrittenContexts.withContexts`); a type a declaration puts in a scoped
    // context's scope says so on the declaration, not where it is used.
    let onValues =
        if not unknown.IsEmpty then []
        else
            typeExprs
            |> List.collect TypeExpr.within
            |> List.choose (fun typeExpr ->
                match typeExpr.Kind with
                | TypeWithContext((first :: _) as written, inner) ->
                    match Unifier.unalias (fst (typeExprToTy state inner)) with
                    | TyFunc _ -> None
                    | FunctionContexts.CarryingNominal _ -> None
                    | TyVar _ as ty ->
                        Some (Diagnostics.at DiagnosticCodes.ContextsOnValueType (SourceSpan.between first.Span (List.last written).Span)
                                  $"'{Unifier.typeText state.TypeDefs ty}' is a type variable, and a type variable doesn't carry contexts: write them on the function that requires them")
                    | TyNominal _ as ty ->
                        Some (Diagnostics.at DiagnosticCodes.ContextsOnValueType (SourceSpan.between first.Span (List.last written).Span)
                                  $"'{Unifier.typeText state.TypeDefs ty}' doesn't take contexts: only a function type does, or a type declared with a context variable, as in `type Stream(a) = [k] protected (...)`")
                    | ty ->
                        Some (Diagnostics.at DiagnosticCodes.ContextsOnValueType (SourceSpan.between first.Span (List.last written).Span)
                                  $"Only a function type carries contexts, and '{Unifier.typeText state.TypeDefs ty}' isn't one: write them on the function that requires them")
                | _ -> None)
    // A union has one rest: each variable written after the first is one too many, not dropped.
    let extraRests =
        typeExprs
        |> List.collect TypeExpr.within
        |> List.collect (fun typeExpr ->
            match typeExpr.Kind with
            | TypeUnion items ->
                match items |> List.filter (fun item -> match item.Kind with TypeVar _ -> true | _ -> false) with
                | first :: extra ->
                    let written (rest: TypeExpr) = match rest.Kind with TypeVar name -> name | _ -> "_"
                    extra
                    |> List.map (fun rest ->
                        Diagnostics.at DiagnosticCodes.UnionWithTwoRests rest.Span
                            $"A union has at most one rest variable, and this one already has '{written first}': '{written rest}' would stand for the same other tags")
                | [] -> []
            | _ -> [])
    unknown @ outside @ contexts @ onValues @ extraRests

/// Whether every type an annotation, such as a def's or a lambda parameter's, names exists, and is not a context.
let internal ensureTypesExist (state: InferState) (typeExprs: TypeExpr list) : Result<unit, Diagnostic list> =
    match unknownTypeErrors state Set.empty typeExprs with
    | [] -> Ok ()
    | errors -> Error errors

/// Whether every type `context`, the context of a `use C(T)(members)` (see `UseInstanceSyntax`), names exists: the
/// context itself is the one place outside a context list its name is written.
let internal ensureInstanceContextExists (state: InferState) (context: TypeExpr) : Result<unit, Diagnostic list> =
    let isTheContext (diagnostic: Diagnostic) =
        diagnostic.Code = DiagnosticCodes.ContextAsType.Code && diagnostic.Span |> Option.exists (fun span -> span.Start = context.Span.Start)
    match unknownTypeErrors state Set.empty [ context ] |> List.filter (isTheContext >> not) with
    | [] -> Ok ()
    | errors -> Error errors

/// The error for each type name the definition of a type names that names no type: its body's, and its parameters'
/// constraints'. Its parameters are in scope in both.
let internal unknownTypesInTypeDef (state: InferState) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    unknownTypeErrors state (parameters |> List.map fst |> Set.ofList) (body :: (parameters |> List.choose snd))

/// Whether the definition of the type `name` can name the type itself: an alias or a nominal type can, unless it is a
/// context, or a type of that name is already in scope, which the name then means.
let private canReferToItself (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) =
    not (isContextDef modifiers) && (tryFindTypeDef name state).IsNone

/// What the nominal type `self` defines, whose `Underlying` is `RecursiveNominals.selfMarker`, is made from: the type
/// its definition `body` stands for, closed over if it names the type (see `RecursiveNominals`).
let private readNominalTypeDefBody (state: InferState) (self: TypeDefInfo) (body: TypeExpr) =
    let ty, next = typeExprToTy { state with TypeDefs = state.TypeDefs |> Map.add self.Name self } body
    let next = { next with TypeDefs = next.TypeDefs |> Map.remove self.Name }
    let id = TypeDefInfo.nominalId self
    match ty with
    // A type made from just itself, `type T = protected T`, is made from nothing: the name means what it did before.
    | TyNominal(nominal, _, _, _) when nominal = id -> typeExprToTy state body
    | _ when RecursiveNominals.refersToItself id ty ->
        let variables = self.Parameters @ Option.toList self.Scope @ Option.toList self.ContextVariable
        RecursiveNominals.close id variables self.ContextVariable ty, next
    | _ -> ty, next

/// The type the definition of `name`, with the parameters `paramVars`, stands for. An alias that names itself inside
/// the payload of one of its tags is recursive (see `RecursiveAliases`); one that names itself anywhere else reads the
/// name as it did before the alias was defined, and `unguardedSelfReferences` reports it. A nominal type `self` can
/// name itself anywhere (see `RecursiveNominals`).
let private readTypeDefBody (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (paramVars: TyVar list) (self: TypeDefInfo) (body: TypeExpr) =
    let plain () = typeExprToTy state body
    if not (canReferToItself state name modifiers) then
        plain ()
    elif self.IsNominal then
        readNominalTypeDefBody state self body
    else
        let self =
            { Name = name
              Home = state.Home
              Parameters = paramVars
              Underlying = RecursiveAliases.selfMarker paramVars
              IsNominal = false
              IsPrivate = false
              IsContext = false
              IsScoped = false
              ScopedBy = None
              Scope = None
              ContextVariable = None
              AssociatedTypes = []
              Symbol = None
              Members = Map.empty
              Doc = ""
              FieldAttributes = Map.empty
              IsDimension = false }
        let ty, next = typeExprToTy { state with TypeDefs = state.TypeDefs |> Map.add name self } body
        match RecursiveAliases.selfReferences ty with
        | true, false -> RecursiveAliases.close paramVars ty, { next with TypeDefs = next.TypeDefs |> Map.remove name }
        | true, true -> plain ()
        | false, _ -> ty, { next with TypeDefs = next.TypeDefs |> Map.remove name }

/// The error for each place the alias `name` names itself other than inside the payload of one of its tags (see
/// `RecursiveAliases.unguardedReferences`).
let internal unguardedSelfReferences (name: Identifier) (modifiers: TypeDefModifier list) (body: TypeExpr) : Diagnostic list =
    if TypeDefModifier.isNominal modifiers || isContextDef modifiers then [] else RecursiveAliases.unguardedReferences name body

/// The error for a scoped context that is neither protected nor private: only the context's own module may make its
/// instances, so that each one comes from the function that supplies it.
let internal unnominalScopedContext (name: Identifier) (modifiers: TypeDefModifier list) : Diagnostic list =
    match TypeDefModifier.scopedSpan modifiers with
    | Some span when not (TypeDefModifier.isNominal modifiers) ->
        [ Diagnostics.at DiagnosticCodes.ScopedContextNotProtected span
            $"The scoped context '{name}' must be either protected or private, as in `scoped context {Names.written name} = protected (...)`, so that only its own module can make its instances"
          |> Diagnostics.about name ]
    | _ -> []

/// The errors for the contexts the type `name`'s declaration says its values belong to the scope of (see
/// `TypeDefModifier.InScopeOf`): each must be a scoped context, and there can be only one. The parser reads them only
/// before `protected` or `private`, so the type is nominal.
let internal typeScopeErrors (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) : Diagnostic list =
    let variables, contexts = TypeDefModifier.scopeContexts modifiers |> List.partition (fst >> Names.isTypeVariable)
    // A type is generic in one context variable, which stands for any number of contexts, and not in a scoped context's
    // scope as well.
    let variableErrors =
        match variables, contexts with
        | [], _ -> []
        | _ :: (_, span) :: _, _ ->
            [ Diagnostics.at DiagnosticCodes.InvalidTypeScope span $"The type '{name}' can have only one context variable, which stands for any number of contexts" ]
        | [ _ ], (_, span) :: _ ->
            [ Diagnostics.at DiagnosticCodes.InvalidTypeScope span $"The type '{name}' is generic in contexts, so it can't belong to a scoped context's scope too" ]
        | [ _ ], [] -> []
    let notScoped =
        contexts
        |> List.choose (fun (context, span) ->
            match tryFindTypeDef context state with
            | Some info when info.IsContext && info.IsScoped -> None
            | Some info when info.IsContext ->
                Some (Diagnostics.at DiagnosticCodes.InvalidTypeScope span
                          $"'{context}' isn't a scoped context: a type can only belong to a scoped context's scope")
            | _ ->
                Some (Diagnostics.at DiagnosticCodes.InvalidTypeScope span
                          $"'{context}' isn't a context: a type can only belong to a scoped context's scope, as in `type {Names.written name} = [Memory] protected (...)`"))
    let several =
        match contexts with
        | _ :: (_, span) :: _ ->
            [ Diagnostics.at DiagnosticCodes.InvalidTypeScope span $"The type '{name}' can belong to only one scoped context's scope" ]
        | _ -> []
    variableErrors @ notScoped @ several

/// The values of a scoped context's scope `ty` holds as it is written, each as the scoped context and the name of the
/// nominal type that belongs to its scope. A function type holds none for the contexts it requires, and a nominal type
/// none in what it is made of: one that holds such a value declares the context itself.
let rec private heldScopes (ty: Ty) : (string * string) list =
    match ty with
    | TyNominal(id, args, _, _) ->
        let own =
            match List.tryLast args with
            | Some (FunctionContexts.ScopedContext(context, _)) -> [ context, id.Name ]
            | _ -> []
        own @ List.collect heldScopes args
    | TyFunc(input, output, _) -> heldScopes input @ heldScopes output
    | TyContexts _ -> []
    | _ -> Ty.children ty |> List.collect heldScopes

/// The error for each scoped context whose scope the representation of the nominal type `name` holds a value of,
/// and that its declaration does not write before its kind: `type MStack(a) = [Memory] private MRef(list(a))`. Where the
/// declaration writes no context, the fix writes the one it holds.
let internal undeclaredHeldScopes (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (body: TypeExpr) : Diagnostic list =
    match tryFindTypeDef name state with
    | Some info when info.IsNominal && not info.IsContext ->
        let declared = TypeDefModifier.scopeContexts modifiers |> List.map (fst >> WrittenContexts.canonical state)
        heldScopes info.Underlying
        |> List.distinctBy fst
        |> List.filter (fun (context, _) -> not (List.contains context declared))
        |> List.map (fun (context, held) ->
            let kind = if modifiers |> List.contains Private then "private" else "protected"
            let parameters = if info.Parameters.IsEmpty then "" else $"""({info.Parameters |> List.choose _.Name |> String.concat ", "})"""
            let diagnostic =
                Diagnostics.at DiagnosticCodes.UndeclaredTypeScope body.Span
                    $"'{name}' holds a value of type '{held}', which belongs to a '{context}' scope: declare it `type {Names.written name}{parameters} = [{context}] {kind} ...`"
                |> Diagnostics.about name
            match TypeDefModifier.kindSpan modifiers with
            | Some kindSpan when declared.IsEmpty ->
                { diagnostic with Fixes = [ { Title = $"Declare `[{context}]`"; Edits = [ { Span = { kindSpan with End = kindSpan.Start }; NewText = $"[{context}] " } ] } ] }
            | _ -> diagnostic)
    | _ -> []

/// The values of an attribute's argument on a field of a record type's definition, as text, by their fields' names or
/// their positions (see `FieldAttribute`): a string's text, a number or a bool as written, a tag with its `#`, or a
/// record or tuple of them. `None` for an argument with anything else in it.
let internal fieldAttributeArguments (argument: Expression option) : (string * string) list option =
    let literal (value: Expression) =
        match value.Kind with
        | LiteralExpr(StringLit text) -> Some text
        | LiteralExpr(IntLit n) -> Some (string n)
        | LiteralExpr(FloatLit x) -> Some (x.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
        | LiteralExpr(BoolLit b) -> Some (if b then "true" else "false")
        | TagExpr(tag, None) -> Some $"#{tag}"
        | _ -> None
    let all (named: (string * Expression) list) =
        let values = named |> List.map (fun (key, value) -> literal value |> Option.map (fun text -> key, text))
        if values |> List.forall Option.isSome then Some (values |> List.choose id) else None
    match argument with
    | None -> Some []
    | Some { Kind = RecordExpr fields } ->
        fields
        |> List.mapi (fun index field ->
            match field with
            | NamedField(fieldName, value) -> fieldName, value
            | PositionalField value -> string index, value)
        |> all
    | Some { Kind = TupleExpr items } -> all (items |> List.mapi (fun index item -> string index, item))
    | Some value -> all [ "0", value ]

/// The attributes written before the fields of `body`, a record type's definition, by each field's name.
let private fieldAttributesOf (body: TypeExpr) : Map<string, FieldAttribute list> =
    match body.Kind with
    | TypeRecord fields ->
        TypeRecordField.withAttributes fields
        |> List.choose (fun (attributes, field) ->
            match field, attributes with
            | _, [] -> None
            | TypeField(fieldName, _, _, _), _ ->
                let read =
                    attributes
                    |> List.map (fun (attributeName, argument, _) ->
                        { Name = attributeName; Arguments = fieldAttributeArguments argument |> Option.defaultValue [] })
                Some (fieldName, read)
            | TypeFieldAttribute _, _ -> None)
        |> Map.ofList
    | _ -> Map.empty

let internal registerTypeDef (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
    let originalTypeVars = state.TypeVars
    let mutable current = state
    let paramEntries =
        parameters
        |> List.map (fun (paramName, _) ->
            let var, next = freshTyVar current
            current <- next
            // The variable keeps the parameter's name, so the type is shown as it is written.
            paramName, { var with Name = Some paramName })
    let updatedTypeVars =
        paramEntries
        |> List.fold (fun acc (paramName, var) -> acc |> Map.add (WrittenVar paramName) var) current.TypeVars
    // A type in a scoped context's scope reads that context's scope in its body as its own (see `TypeDefInfo.Scope`).
    let scopedBy = TypeDefModifier.scopeContexts modifiers |> List.filter (fst >> Names.isTypeVariable >> not) |> List.tryExactlyOne |> Option.map fst
    let scope, updatedTypeVars =
        match scopedBy with
        | Some context ->
            let var, next = freshTyVar current
            current <- next
            let scope = { var with Name = Some context; Kind = SignatureScope context }
            Some scope, updatedTypeVars |> Map.add (ScopeOf context) scope
        | None -> None, updatedTypeVars
    // A type generic in contexts reads its context variable in its body as its own (see `TypeDefInfo.ContextVariable`).
    let contextVariable, updatedTypeVars =
        match TypeDefModifier.scopeContexts modifiers |> List.filter (fst >> Names.isTypeVariable) with
        | [ name, _ ] ->
            let var, next = freshTyVar current
            current <- next
            Some { var with Name = Some name }, updatedTypeVars |> Map.add (WrittenVar name) { var with Name = Some name }
        | _ -> None, updatedTypeVars
    let stateWithParams = { current with TypeVars = updatedTypeVars }
    let isPrivate = isPrivateType modifiers
    let paramVars = paramEntries |> List.map snd
    let isNominal = TypeDefModifier.isNominal modifiers
    // The type as its own definition names it, before what it is made from is known.
    let self =
        { Name = name
          Home = stateWithParams.Home
          Parameters = paramVars
          Underlying = RecursiveNominals.selfMarker
          IsNominal = isNominal
          IsPrivate = isPrivate
          IsContext = false
          IsScoped = List.exists TypeDefModifier.isScoped modifiers
          ScopedBy = scopedBy
          Scope = scope
          ContextVariable = contextVariable
          AssociatedTypes = []
          Symbol = None
          Members = Map.empty
          Doc = ""
          FieldAttributes = Map.empty
          IsDimension = false }
    let underlyingTy, next = readTypeDefBody stateWithParams name modifiers paramVars self body
    let selfReferences = if canReferToItself stateWithParams name modifiers then RecursiveAliases.namesOfItself name body |> List.map fst else []
    let nextNominals =
        if isNominal then next.LocalNominals |> Set.add name else next.LocalNominals
    let nextPrivate =
        if isPrivate then next.LocalPrivateNominals |> Set.add name else next.LocalPrivateNominals
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
                | TypeFieldAttribute _ -> None)
            |> Map.ofList
        | _ -> Map.empty
    let info =
        { Name = name
          Home = next.Home
          Parameters = paramVars
          Underlying = underlyingTy
          IsNominal = isNominal
          IsPrivate = isPrivate
          IsContext = isContextDef modifiers
          IsScoped = List.exists TypeDefModifier.isScoped modifiers
          ScopedBy = scopedBy
          Scope = scope
          ContextVariable = contextVariable
          AssociatedTypes = TypeDefModifier.associatedTypes modifiers
          Symbol = symbol
          Members = members
          Doc = ""
          FieldAttributes = if isContextDef modifiers then Map.empty else fieldAttributesOf body
          IsDimension = false }
    // A name of the type in its own definition refers to it, as any other does.
    let next = selfReferences |> List.fold (fun state typeExpr -> recordTypeReference typeExpr info state) next
    { next with
        TypeVars = originalTypeVars
        TypeDefs = next.TypeDefs |> Map.add name info
        HiddenTypeNames = next.HiddenTypeNames |> Set.remove name
        ContextDefs = nextContexts
        LocalNominals = nextNominals
        LocalPrivateNominals = nextPrivate }

/// The type definitions `items` declares, ordered so that each comes after the types it names that are declared
/// among them, and otherwise in the order they are written. Each is registered with `registerTypeDef` in this order,
/// so a type's body reads the types it names as declared, wherever they are written: a type written above an alias it
/// names, or in a fragment of a partial module whose file sorts first, sees the alias's union and not an unknown name.
/// Types that name each other keep the order they are written in, as do two definitions of one name.
let internal inDependencyOrder (items: TopLevelItem list) =
    let typeDefs =
        items
        |> List.choose (function
            | Kind (Def (TypeDef(name, modifiers, parameters, body))) -> Some (name, modifiers, parameters, body)
            | _ -> None)
        |> Array.ofList
    let indicesByName =
        typeDefs |> Array.indexed |> Array.groupBy (fun (_, (name, _, _, _)) -> name) |> Array.map (fun (name, defs) -> name, Array.map fst defs) |> Map.ofArray
    let named (_, _, parameters: (Identifier * TypeExpr option) list, body) =
        body :: (parameters |> List.choose snd) |> List.collect typeNamesIn |> List.map fst |> List.distinct
    let visited = Array.create typeDefs.Length false
    let mutable ordered = []
    let rec visit index =
        if not visited.[index] then
            visited.[index] <- true
            named typeDefs.[index] |> List.iter (fun name -> indicesByName |> Map.tryFind name |> Option.iter (Array.iter visit))
            ordered <- typeDefs.[index] :: ordered
    for index in 0 .. typeDefs.Length - 1 do
        visit index
    List.rev ordered
