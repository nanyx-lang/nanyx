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
    | "undefined" -> Some Unifier.bottomType
    | _ when SizedNumber.isName name -> Some (primitiveType name)
    | _ -> None

/// Whether `name` is a type the compiler provides, such as `int` or `list`, rather than one the program defines.
/// A type's capitalised name, `List`, is written as its alias, `list`, by `BuiltinTypeNames` before inference.
let internal isBuiltInTypeName (name: string) =
    (lookupPrimitive name).IsSome
    || Lists.isTypeName name
    || Seqs.isTypeName name
    || name = CellPassingSyntax.typeName
    || Maps.isTypeName name
    || Sets.isTypeName name
    || Arrays.isTypeName name
    || Boxes.isTypeName name
    || MutableArrays.isTypeName name

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
    | TyNominal(id, _, _, _) -> [ id.Name ]
    | TyPrimitive name -> [ name ]
    | Lists.ListType _ -> [ Lists.typeName ]
    | Seqs.SeqType _ -> [ Seqs.typeName ]
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
/// reached with a pipe, not a dot, so the message says so when there is one.
let internal unknownMember (env: TypeEnv) (span: SourceSpan) (receiver: string option) (ty: Ty) (memberName: string) : Diagnostic =
    let rec isRecord (ty: Ty) =
        match Unifier.unalias ty with
        | TyRecord _
        | TyOpenRecord _ -> true
        | TyNominal(_, _, underlying, _) -> isRecord underlying
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

/// Whether `info` defines a nominal type: one declared `protected` or `private`.
let internal isNominal (info: TypeDefInfo) =
    info.IsNominal

/// The type variable an annotation names `name`: the one it already stands for in this annotation, or a new one. The
/// variable keeps the annotation's name, so types are shown with the names the user wrote.
let private namedTypeVar (state: InferState) (name: Identifier) : TyVar * InferState =
    match state.TypeVars |> Map.tryFind name with
    | Some existing -> existing, state
    | None ->
        let var = { Id = state.NextId; Name = Some name }
        var, { state with NextId = state.NextId + 1; TypeVars = state.TypeVars |> Map.add name var }

/// The nominal type `info` defines, applied to `argTys`: made from its definition's type with them for its parameters.
/// A type in a scoped context's scope has the context, of its scope, as a last argument (see `TypeDefInfo.ScopedBy`).
let internal nominalType (info: TypeDefInfo) (argTys: Ty list) : Ty =
    let parameters, arguments =
        match info.Scope, List.tryLast argTys with
        | Some scope, Some (FunctionContexts.ScopedContext(_, scopeTy)) when argTys.Length = info.Parameters.Length + 1 ->
            info.Parameters @ [ scope ], List.truncate info.Parameters.Length argTys @ [ scopeTy ]
        | _ -> info.Parameters, argTys
    TyNominal(TypeDefInfo.nominalId info, argTys, Unifier.applyToParameters parameters arguments info.Underlying, info.IsPrivate)

/// The scope a type in a scoped context's scope is of where it is written: the one the signature's context of that
/// name has (see `WrittenContexts`), as the scoped context itself.
let private writtenScope (state: InferState) (info: TypeDefInfo) : Ty list * InferState =
    match info.ScopedBy with
    | Some context ->
        let scope, next = namedTypeVar state (FunctionContexts.scopeVariableName context)
        [ FunctionContexts.scoped context (TyVar scope) ], next
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
    let underlying = Unifier.applyToParameters (info.Parameters @ Option.toList info.Scope) (argTys @ scopeArgument) info.Underlying
    TyNominal(TypeDefInfo.nominalId info, argTys @ Option.toList scoped, underlying, info.IsPrivate), underlying, scoped, next

/// The type the name `name`, defined by `info`, stands for written without arguments: the nominal type, or the alias.
/// A generic one is applied to its own parameters.
let internal namedType (state: InferState) (name: string) (info: TypeDefInfo) : Ty * InferState =
    if isNominal info then
        let scope, next = writtenScope state info
        nominalType info ((info.Parameters |> List.map TyVar) @ scope), next
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
            | Some info -> namedType (recordTypeReference typeExpr info state) name info
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
        TyRecord fieldMap, next
    // `...r` is the type variable `r`, shared with the rest of the annotation; `...` is a variable of its own.
    | TypeOpenRecord(fields, row) ->
        let fieldMap, next = recordFieldTypes state fields
        let rowVar, next =
            match row with
            | Some name -> namedTypeVar next name
            | None -> { Id = next.NextId; Name = None }, { next with NextId = next.NextId + 1 }
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
        elif Arrays.isTypeName name && argTys.Length = 1 then
            Arrays.ofType argTys.[0], current
        elif Boxes.isTypeName name && argTys.Length = 1 then
            Boxes.ofType argTys.[0], current
        elif MutableArrays.isTypeName name && argTys.Length = 1 then
            MutableArrays.ofType argTys.[0], current
        // `mut T`: a cell of the `memory` block whose `Memory` the signature writes, as `[Memory] mut int -> ()` says
        // (see `WrittenContexts.read`). A `mut cell` parameter's own annotation, which has no source, is of a scope of
        // its own: the cell given for it says which. A signature inference writes out has no source either, but what
        // its cells hold is written by inference too, and its cells share the signature's scope.
        elif name = CellPassingSyntax.typeName && argTys.Length = 1 then
            let scope, current =
                match Some typeExpr with
                | CellPassingSyntax.CellParameter held when held |> Option.forall (fun held -> not (SourceSpan.isNone held.Span)) -> freshVar current
                | _ -> typeExprToTy current (TypeExpr.synthetic (TypeVar (FunctionContexts.scopeVariableName FunctionContexts.cellContext)))
            FunctionContexts.cellType argTys.[0] scope, current
        else
            match tryFindTypeDef name current with
            | Some info when not (isNominal info) ->
                let current = recordTypeReference typeExpr info current
                if info.Parameters.Length = argTys.Length then
                    TyAlias(name, argTys, Unifier.applyToParameters info.Parameters argTys info.Underlying), current
                else
                    TyApply(name, argTys), current
            // A context required with its arguments stays applied to them (see `FunctionContexts`).
            | Some info when not info.IsContext && info.Parameters.Length = argTys.Length ->
                let scope, current = writtenScope (recordTypeReference typeExpr info current) info
                nominalType info (argTys @ scope), current
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

/// The types of the fields of a record type, by name. A field written without a type is a fresh variable.
and private recordFieldTypes (state: InferState) (fields: TypeRecordField list) : Map<string, Ty> * InferState =
    let mutable current = state
    let fieldMap =
        fields
        |> List.choose (function
            | TypeField(name, _, typeOpt, _) -> Some (name, typeOpt)
            | TypeMember(name, typeOpt) -> Some (name, typeOpt)
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
let rec private typeNamesIn (typeExpr: TypeExpr) : (Identifier * SourceSpan) list =
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
        |> List.map (fun (typeName, span) ->
            let own = typeName.Substring(typeName.LastIndexOf '.' + 1)
            if own.Length > 1 && System.Char.IsLower own[0] then
                Diagnostics.at DiagnosticCodes.InvalidTypeName span
                    $"'{typeName}' is not a type: a type's name starts with a capital letter, and a lowercase name longer than one letter is reserved for associated types"
            else
                Diagnostics.at DiagnosticCodes.UnknownType span
                    ($"Unknown type '{typeName}'" + StandardLibraryImports.hint state.StandardLibraryExports.Value typeName))
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
                          $"'{name}' is a context, not a type: it can only be required, as in `[{name}]`, or loaded with `use`")
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
                    | TyFunc _
                    | TyVar _ -> None
                    | ty ->
                        Some (Diagnostics.at DiagnosticCodes.ContextsOnValueType (SourceSpan.between first.Span (List.last written).Span)
                                  $"Only a function type carries contexts, and '{Unifier.tyToString ty}' isn't one: write them on the function that requires them")
                | _ -> None)
    unknown @ outside @ contexts @ onValues

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

/// Whether the definition of the type `name` can name the type itself: an alias can, unless it is a context, or a
/// type of that name is already in scope, which the name then means.
let private canReferToItself (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) =
    not (TypeDefModifier.isNominal modifiers) && not (isContextDef modifiers) && (tryFindTypeDef name state).IsNone

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
              IsNominal = false
              IsPrivate = false
              IsContext = false
              IsScoped = false
              ScopedBy = None
              Scope = None
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
    if TypeDefModifier.isNominal modifiers || isContextDef modifiers then [] else RecursiveAliases.unguardedReferences name body

/// The error for a scoped context that is neither protected nor private: only the context's own module may make its
/// instances, so that each one comes from the function that supplies it.
let internal unnominalScopedContext (name: Identifier) (modifiers: TypeDefModifier list) : Diagnostic list =
    match TypeDefModifier.scopedSpan modifiers with
    | Some span when not (TypeDefModifier.isNominal modifiers) ->
        [ Diagnostics.at DiagnosticCodes.ScopedContextNotProtected span
            $"The scoped context '{name}' must be either protected or private, as in `scoped context {Names.written name} = protected (...)`, so that only its own module can make its instances" ]
    | _ -> []

/// The errors for the contexts the type `name`'s declaration says its values belong to the scope of (see
/// `TypeDefModifier.InScopeOf`): each must be a scoped context, and there can be only one. The parser reads them only
/// before `protected` or `private`, so the type is nominal.
let internal typeScopeErrors (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) : Diagnostic list =
    let contexts = TypeDefModifier.scopeContexts modifiers
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
    notScoped @ several

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
            match TypeDefModifier.kindSpan modifiers with
            | Some kindSpan when declared.IsEmpty ->
                { diagnostic with Fixes = [ { Title = $"Declare `[{context}]`"; Edits = [ { Span = { kindSpan with End = kindSpan.Start }; NewText = $"[{context}] " } ] } ] }
            | _ -> diagnostic)
    | _ -> []

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
    // A type in a scoped context's scope reads that context's scope in its body as its own (see `TypeDefInfo.Scope`).
    let scopedBy = TypeDefModifier.scopeContexts modifiers |> List.tryExactlyOne |> Option.map fst
    let scope, updatedTypeVars =
        match scopedBy with
        | Some context ->
            let name = FunctionContexts.scopeVariableName context
            let var, next = freshVar current
            current <- next
            match var with
            | TyVar var -> Some { var with Name = Some name }, updatedTypeVars |> Map.add name { var with Name = Some name }
            | _ -> failwith "Expected TyVar"
        | None -> None, updatedTypeVars
    let stateWithParams = { current with TypeVars = updatedTypeVars }
    let isPrivate = isPrivateType modifiers
    let paramVars = paramEntries |> List.map snd
    let underlyingTy, next = readTypeDefBody stateWithParams name modifiers paramVars body
    let selfReferences = if canReferToItself stateWithParams name modifiers then RecursiveAliases.namesOfItself name body |> List.map fst else []
    let isNominal = TypeDefModifier.isNominal modifiers
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
                | TypeMember _
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
