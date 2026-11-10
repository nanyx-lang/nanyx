module NyxCompiler.Infer

open System
open Parser.Program
open NyxCompiler

type TypedExpr = NyxCompiler.TypedExpr
type TypedStatement = NyxCompiler.TypedStatement

/// What the names of the locals the workflow rewrite binds the rest of a block to start with, after an `if` or `match`
/// with workflow keywords in its branches: each branch goes on to it. No name written in the source can start with it.
let workflowRestPrefix = "__rest_"

open NyxCompiler.InferState

type InferState = NyxCompiler.InferState.InferState

let emptyState = NyxCompiler.InferState.emptyState

open NyxCompiler.Visibility
open NyxCompiler.BranchJoin

let blockImports = NyxCompiler.Visibility.blockImports

let private primitiveType name =
    TyPrimitive name

let private literalType literal =
    match literal with
    | StringLit _ -> primitiveType "string"
    | IntLit _ -> primitiveType "int"
    | FloatLit _ -> primitiveType "float"
    | BoolLit _ -> primitiveType "bool"

let private listType elementTy =
    TyApply("list", [ elementTy ])

let private numericType =
    TyPrimitive "int"

let private tupleFieldName index =
    string index

let private lookupPrimitive (name: string) =
    match name with
    | "int" | "Int" -> Some (primitiveType "int")
    | "float" | "Float" -> Some (primitiveType "float")
    | "bool" | "Bool" -> Some (primitiveType "bool")
    | "string" | "String" -> Some (primitiveType "string")
    | "unit" | "Unit" -> Some (primitiveType "unit")
    | _ -> None

/// Whether `name` is a type the compiler provides, such as `Int` or `List`, rather than one the program defines.
/// `Option` is one too, though only as a name to attach the helpers for option-like unions to: there is no type.
let private isBuiltInTypeName (name: string) =
    (lookupPrimitive name).IsSome
    || name.Equals("list", StringComparison.OrdinalIgnoreCase)
    || name.Equals("seq", StringComparison.OrdinalIgnoreCase)
    || name = "Option"

/// Whether the type `name` is attached to exists; `span` is where the definition's name is written.
let private ensureAttachedTypeExists (state: InferState) (currentModuleOpt: string option) (name: Identifier) (span: SourceSpan) : Result<unit, Diagnostic list> =
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

/// Where the name of a call, pipe or member access is written (`spanOpt`), or the whole expression if not known.
let private nameSpan (spanOpt: SourceSpan option) (expr: Expression) : SourceSpan =
    spanOpt |> Option.defaultValue expr.Span

/// The names definitions can be attached to a value of type `ty` under, nearest first: each alias it is written
/// with, then the nominal type or the language's name for a built-in type (`String`, `List`). Empty when the type
/// has no name, such as a union, a record written out in full, or a type that is not known yet.
let rec attachedTypeNames (ty: Ty) : string list =
    match ty with
    | TyAlias(name, _, underlying) -> name :: attachedTypeNames underlying
    | TyNominal(name, _, _) -> [ name ]
    | TyPrimitive name -> [ string (Char.ToUpperInvariant name.[0]) + name.Substring 1; name ]
    | TyApply("list", _) -> [ "List" ]
    | TyApply("seq", _) -> [ "Seq" ]
    | _ -> []

/// The attached definition `x \funcName` calls: `Name.funcName` for the first name of `x`'s type that has one in
/// scope. None when `funcName` is already qualified, or no name of the type attaches it, so the pipe calls a plain
/// `funcName` instead. Only names in `env` count, so a module imported with `as` is reached as `\t.String.trim`.
let private tryResolveAttachedFunction (env: TypeEnv) (lhsType: Ty) (funcName: string) : string option =
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
let private unknownMember (env: TypeEnv) (span: SourceSpan) (receiver: string option) (ty: Ty) (memberName: string) : Diagnostic =
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

let private isNominal (info: TypeDefInfo) =
    isNominalName info.Name

/// The type the name `name`, defined by `info`, stands for written without arguments: the nominal type, or the alias.
let private namedType (name: string) (info: TypeDefInfo) : Ty =
    if isNominal info then TyNominal(info.Name, info.Underlying, info.IsPrivate) else aliasType name info

/// The type definition a type name written in the module means: none for a name only an aliased import brings in,
/// which has to be written with the alias.
let private tryFindTypeDef (name: string) (state: InferState) : TypeDefInfo option =
    if state.HiddenTypeNames.Contains name then None else state.TypeDefs |> Map.tryFind name

let private tryInferAttachedLambdaInput (state: InferState) (name: Identifier) (expr: Expression) : Ty option =
    match expr.Kind with
    | Lambda(args, _) when name.Contains(".") && args.Length = 1 ->
        let dotIndex = name.LastIndexOf(".")
        let typeName = name.Substring(0, dotIndex)
        match tryFindTypeDef typeName state with
        | Some info -> Some (namedType typeName info)
        | None -> lookupPrimitive typeName
    | _ -> None

let private isContextDef (modifiers: TypeDefModifier list) =
    modifiers |> List.contains Context

let private tupleRecordType (items: Ty list) =
    items
    |> List.mapi (fun index ty -> tupleFieldName index, ty)
    |> Map.ofList
    |> TyRecord

let private inputTypeFromArgs argTys =
    match argTys with
    | [] -> TyPrimitive "unit"
    | [ single ] -> single
    | _ -> tupleRecordType argTys

let private isPrivateType modifiers =
    modifiers |> List.exists (fun modifier -> modifier = TypeDefModifier.Private)


let rec private typeExprToTy (state: InferState) (typeExpr: TypeExpr) : Ty * InferState =
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
                | TypeField(name, _, _, typeOpt, _) -> Some (name, typeOpt)
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
        if name.Equals("list", StringComparison.OrdinalIgnoreCase) then
            let elementTy =
                match argTys with
                | [] -> TyPrimitive "unit"
                | [single] -> single
                | multiple -> tupleRecordType multiple
            TyApply("list", [elementTy]), current
        else
            match tryFindTypeDef name current with
            | Some info when not (isNominal info) ->
                let current = recordTypeReference typeExpr info current
                if info.Parameters.Length = argTys.Length then
                    let substitutions =
                        (info.Parameters, argTys)
                        ||> List.zip
                        |> List.fold (fun acc (paramVar, argTy) -> acc |> Map.add paramVar.Id argTy) Map.empty
                    TyAlias(name, argTys, Unifier.apply substitutions info.Underlying), current
                else
                    TyApply(name, argTys), current
            | Some info -> TyApply(name, argTys), recordTypeReference typeExpr info current
            | None -> TyApply(name, argTys), current
    | TypeFunc(arg, ret) ->
        let argTy, next = typeExprToTy state arg
        let retTy, nextState = typeExprToTy next ret
        TyFunc(argTy, retTy), nextState
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
    | TypeOptional inner ->
        let innerTy, next = typeExprToTy state inner
        TyUnion [ innerTy; TyTag("none", None) ], next
    | TypeContext _ ->
        let ty, next = freshVar state
        ty, next
    | TypeWithContext(_, inner) ->
        typeExprToTy state inner
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
let rec private typeOfValues (typeExpr: TypeExpr) : Expression list =
    let ofList items = items |> List.collect typeOfValues
    match typeExpr.Kind with
    | TypeOf value -> [ value ]
    | TypeName _ | TypeVar _ | TypeHole | TypeUnit -> []
    | TypeTuple items | TypeUnion items | TypeIntersection items | TypeContext items -> ofList items
    | TypeRecord fields ->
        fields
        |> List.collect (function
            | TypeField(_, _, _, Some t, _) | TypeMember(_, Some t) -> typeOfValues t
            | _ -> [])
    | TypeApply(_, args) -> ofList args
    | TypeFunc(arg, ret) -> typeOfValues arg @ typeOfValues ret
    | TypeTag(_, payload) -> payload |> Option.map typeOfValues |> Option.defaultValue []
    | TypeWithContext(contexts, inner) -> ofList contexts @ typeOfValues inner
    | TypeOptional inner | TypeConstraint(_, inner, _) | TypeWhere(inner, _) -> typeOfValues inner

/// The error for each `typeof` in the type definition `name`: a type definition is not in the scope of any value.
let private typeOfInTypeDef (name: Identifier) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    body :: (parameters |> List.choose snd)
    |> List.collect typeOfValues
    |> List.map (fun value ->
        Diagnostics.at DiagnosticCodes.MisplacedTypeof value.Span $"'typeof' cannot be used in the definition of the type '{name}', only in the type of a value")

let private extractContextFromTypeExpr (typeExpr: TypeExpr option) : TypeExpr list option * TypeExpr option =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, inner))) -> Some contexts, Some inner
    | Some (Kind (TypeContext contexts)) -> Some contexts, None
    | Some other -> None, Some other
    | None -> None, None

let private extendEnvWithRecordFields (env: TypeEnv) (ty: Ty) =
    let addFields fields =
        fields
        |> Map.fold (fun acc name fieldTy -> TypeEnv.extend name (TypeEnv.mono fieldTy) acc) env
    match Unifier.unalias ty with
    | TyRecord fields -> addFields fields
    | TyNominal(_, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord fields -> addFields fields
        | _ -> env
    | _ -> env

/// The members of the context `ctxExpr` names, each with its type and its symbol, if the context declares one.
let rec private collectContextMembers (state: InferState) (ctxExpr: TypeExpr) : (Identifier * Ty * SymbolId option) list * InferState =
    match ctxExpr.Kind with
    | TypeRecord fields ->
        let mutable current = state
        let members =
            fields
            |> List.choose (function
                | TypeField(name, _, _, typeOpt, _) -> Some (name, typeOpt)
                | TypeMember(name, typeOpt) -> Some (name, typeOpt))
            |> List.map (fun (name, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some t -> typeExprToTy current t
                    | None -> freshVar current
                current <- next
                name, ty, None)
        members, current
    | TypeContext contexts ->
        let mutable current = state
        let members =
            contexts
            |> List.collect (fun item ->
                let itemMembers, next = collectContextMembers current item
                current <- next
                itemMembers)
        members, current
    | TypeName name
    | TypeApply(name, _) ->
        let infoOpt = state.TypeDefs |> Map.tryFind name
        let state =
            match infoOpt with
            | Some info -> recordTypeReference ctxExpr info state
            | None -> state
        // Each member's symbol is the one the context's definition declared, in whichever module that is.
        let withSymbols (members: (Identifier * Ty * SymbolId option) list) =
            match infoOpt with
            | Some info -> members |> List.map (fun (memberName, ty, _) -> memberName, ty, info.Members |> Map.tryFind memberName)
            | None -> members
        match state.ContextDefs |> Map.tryFind name with
        | Some ctxDef ->
            let members, next = collectContextMembers state ctxDef
            withSymbols members, next
        | None ->
            // Also check TypeDefs for non-context types used as contexts
            match infoOpt with
            | Some info ->
                match Unifier.unalias info.Underlying with
                | TyRecord fields ->
                    fields |> Map.toList |> List.map (fun (memberName, ty) -> memberName, ty, None) |> withSymbols, state
                | _ -> [], state
            | None -> [], state
    | _ -> [], state

let private extendEnvWithContexts (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) =
    let mutable current = state
    let mutable env' = env
    for ctx in contexts do
        let members, next = collectContextMembers current ctx
        current <- next
        // A context named through an import's alias (`c.Console`) also supplies its members under the alias
        // (`c.println`), which would otherwise still be the ambient instance's.
        let qualifier =
            match ctx.Kind with
            | TypeName name
            | TypeApply(name, _) when name.Contains '.' -> Some (name.Substring(0, name.LastIndexOf '.'))
            | _ -> None
        env' <-
            members
            |> List.fold (fun acc (name, ty, symbol) ->
                let acc = TypeEnv.declare name symbol (TypeEnv.mono ty) acc
                match qualifier with
                | Some qualifier -> TypeEnv.declare $"{qualifier}.{name}" symbol (TypeEnv.mono ty) acc
                | None -> acc) env'
    env', current

let rec private unknownContextNames (state: InferState) (ctxExpr: TypeExpr) : Identifier list =
    match ctxExpr.Kind with
    | TypeContext contexts -> contexts |> List.collect (unknownContextNames state)
    | TypeName name
    | TypeApply(name, _) ->
        if state.ContextDefs.ContainsKey name || (tryFindTypeDef name state).IsSome then [] else [ name ]
    | _ -> []

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
            | TypeField(_, _, _, Some t, _)
            | TypeMember(_, Some t) -> ofType t
            | _ -> [])
    | TypeFunc(arg, ret) -> ofType arg @ ofType ret
    | TypeTag(_, Some payload)
    | TypeOptional payload
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
let private ensureTypesExist (state: InferState) (typeExprs: TypeExpr list) : Result<unit, Diagnostic list> =
    match unknownTypeErrors state Set.empty typeExprs with
    | [] -> Ok ()
    | errors -> Error errors

/// The error for each type name the definition of a type names that names no type: its body's, and its parameters'
/// constraints'. Its parameters are in scope in both.
let private unknownTypesInTypeDef (state: InferState) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    unknownTypeErrors state (parameters |> List.map fst |> Set.ofList) (body :: (parameters |> List.choose snd))

/// Whether every context in the signature of `name` exists; `span` is where the definition's name is written.
let private ensureContextsExist (state: InferState) (name: Identifier) (span: SourceSpan) (contexts: TypeExpr list) : Result<unit, Diagnostic list> =
    match contexts |> List.collect (unknownContextNames state) |> List.distinct with
    | [] -> Ok ()
    | unknown ->
        unknown
        |> List.map (fun ctxName -> Diagnostics.at DiagnosticCodes.UnknownContext span $"Unknown context '{ctxName}' in the signature of '{name}'")
        |> Error

/// Whether `name` is bound in `env` to the ambient instance's member, not to something nearer that shadows it: a
/// member of a context the def declares, or of an instance a `use` loads.
let private isAmbientMember (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.AmbientMembers |> Map.tryFind name, TypeEnv.tryFind name env with
    | Some (symbol, _), Some scheme -> scheme.Symbol = Some symbol
    | _ -> false

/// The ambient contexts that code in `env` runs in: every one at the top level, else those something nearer
/// supplies every member of.
let private availableAmbientContexts (env: TypeEnv) (state: InferState) : Set<Identifier> =
    let membersByContext =
        state.AmbientMembers
        |> Map.toList
        |> List.groupBy (fun (_, (_, ctxName)) -> ctxName)
    let fromMembers =
        membersByContext
        |> List.filter (fun (_, members) ->
            match state.ContextScope with
            | AmbientScope -> true
            | DefScope _ ->
                members |> List.forall (fun (memberName, _) -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName)))
        |> List.map fst
        |> Set.ofList
    match state.ContextScope with
    | AmbientScope -> Set.union fromMembers state.CarriedAmbientContexts
    | DefScope _ -> fromMembers

/// `what` (which ends "requires", say) the context `ctxName` at `span`, in the def `defName`, whose type does not
/// declare it.
let private undeclaredContext (defName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.UndeclaredContext span $"{what} the context '{ctxName}', which '{defName}' does not declare: add [{ctxName}] to the type of '{defName}', or load an instance with `use`"

/// `what` needs the ambient context `ctxName` where nothing nearer supplies it: fine at the top level, inferred for
/// an unannotated def, and an error in a def whose type does not declare it.
let private needAmbientContext (state: InferState) (ctxName: Identifier) (what: string) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match state.ContextScope with
    | AmbientScope -> Ok state
    | DefScope(defName, true) ->
        let contexts = state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty
        Ok { state with InferredContexts = state.InferredContexts |> Map.add defName (contexts.Add ctxName) }
    | DefScope(defName, false) -> Error [ undeclaredContext defName ctxName what span ]

/// The name the definition `name` names is declared under: a block import's name (`zero`) stands for the qualified
/// one (`money.zero`), unless something nearer binds it.
let private canonicalName (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.BlockAliases |> Map.tryFind name with
    | Some qualified when (TypeEnv.tryFind name env |> Option.bind _.Symbol) = (TypeEnv.tryFind qualified env |> Option.bind _.Symbol) -> qualified
    | _ -> name

/// Checks the context use of naming `name` at `span`: a member of the ambient instance needs its context, and a use
/// of an unannotated top-level def is kept to check once its contexts are known.
let private checkContextUse (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    if isAmbientMember env state name then
        let _, ctxName = state.AmbientMembers.[name]
        needAmbientContext state ctxName $"'{name}' is a member of" span
    else
        match state.UnannotatedDefs |> Map.tryFind name, TypeEnv.tryFind name env with
        | Some symbol, Some scheme when scheme.Symbol = Some symbol && not (state.AmbientMembers.IsEmpty && state.CarriedAmbientContexts.IsEmpty) ->
            let deferred = { Caller = state.ContextScope; Callee = name; Span = span; Available = availableAmbientContexts env state }
            Ok { state with DeferredContextUses = deferred :: state.DeferredContextUses }
        | _ -> Ok state

/// Checks that `contexts`, which `name` requires, are available at `span`. A context with a member not in scope at all
/// is an error only when `reportMissing`.
let private ensureContextAvailable (reportMissing: bool) (env: TypeEnv) (state: InferState) (name: Identifier) (contexts: TypeExpr list) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    let requiredMembers =
        contexts
        |> List.map (fun ctx ->
            let members, _ = collectContextMembers state ctx
            ctx, members |> List.map (fun (memberName, _, _) -> memberName))
    let isCarried (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName ctxName
        | TypeApply(ctxName, _) -> state.CarriedAmbientContexts.Contains ctxName
        | _ -> false
    // A carried ambient context is supplied by something nearer only if its members are in scope and not the
    // instance's; otherwise it is needed from the ambient instance, as a context this module can name is.
    let suppliedNearer members =
        members |> List.forall (fun memberName -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName))
    // Each context with a member not in scope at all, with those members.
    let missing =
        requiredMembers
        |> List.filter (fst >> isCarried >> not)
        |> List.choose (fun (ctx, members) ->
            match members |> List.filter (fun memberName -> TypeEnv.tryFind memberName env |> Option.isNone) with
            | [] -> None
            | absent -> Some (ctx, absent))
    if missing.IsEmpty || not reportMissing then
        // Every member is in scope, but one the ambient instance supplies is only available where its context is.
        requiredMembers
        |> List.fold (fun result (ctx, members) ->
            result
            |> Result.bind (fun current ->
                match ctx.Kind, members |> List.exists (isAmbientMember env current) with
                | (TypeName ctxName | TypeApply(ctxName, _)), _ when isCarried ctx && not (suppliedNearer members) ->
                    needAmbientContext current ctxName $"'{name}' requires" span
                | (TypeName ctxName | TypeApply(ctxName, _)), true -> needAmbientContext current ctxName $"'{name}' requires" span
                | _ -> Ok current)) (Ok state)
    else
        // A context is named as it is written, as a type alias is; only one written out as a record has nothing to
        // name it by but its members.
        let named, unnamed =
            missing
            |> List.partition (fun (ctx, _) -> match ctx.Kind with TypeName _ | TypeApply _ -> true | _ -> false)
        let quoted names = names |> List.distinct |> List.map (fun name -> $"'{name}'")
        let contextNames =
            named |> List.choose (fun (ctx, _) -> match ctx.Kind with TypeName ctxName | TypeApply(ctxName, _) -> Some ctxName | _ -> None) |> quoted
        let memberNames = unnamed |> List.collect snd |> quoted
        let listed (items: string list) =
            match items with
            | [] -> ""
            | [ only ] -> only
            | _ -> String.concat ", " (List.truncate (items.Length - 1) items) + " and " + List.last items
        let message =
            match contextNames, memberNames with
            | [ single ], [] -> $"'{name}' requires the context {single}, which is not available here"
            | _, [] -> $"'{name}' requires the contexts {listed contextNames}, which are not available here"
            | [], _ -> $"'{name}' requires the context members {listed memberNames}, which are not available here"
            | _ ->
                let contextsText = if contextNames.Length = 1 then "the context" else "the contexts"
                $"'{name}' requires {contextsText} {listed contextNames} and the context members {listed memberNames}, which are not available here"
        Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]

let private registerTypeDef (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
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
    let underlyingTy, next = typeExprToTy stateWithParams body
    let isPrivate = isPrivateType modifiers
    let paramVars = paramEntries |> List.map snd
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
                | TypeField(memberName, _, _, _, _) ->
                    let memberSymbol, declared = declareUnscoped RecordTypeFields body.Id memberName ContextMember next
                    next <- declared
                    memberSymbol |> Option.map (fun id -> memberName, id)
                | TypeMember _ -> None)
            |> Map.ofList
        | _ -> Map.empty
    { next with
        TypeVars = originalTypeVars
        TypeDefs =
            next.TypeDefs
            |> Map.add name
                { Name = name
                  Parameters = paramVars
                  Underlying = underlyingTy
                  IsPrivate = isPrivate
                  IsContext = isContextDef modifiers
                  Symbol = symbol
                  Members = members }
        HiddenTypeNames = next.HiddenTypeNames |> Set.remove name
        ContextDefs = nextContexts
        LocalNominals = nextNominals
        LocalPrivateNominals = nextPrivate }


// Instances of parameterised contexts.
//
// A function that requires `[Sum(a)]` is passed an instance of `Sum` for whatever `a` is at each call. The call finds
// it, nearest first, among the instances in scope (each `use` of a `Sum(T)`, or of a value of `Sum`'s shape, around
// the call, and each context the type of the function around the call declares), or else builds it from the
// definitions attached to the type, as
// `Point.0` and `Point.+` make a `Sum(Point)`. Where it was found is kept by the call's node, for the backend.

/// What the names of the instances in scope start with, in the environment. No name written in the source can, so
/// an instance never shadows a name, or is shadowed by one.
let private instancePrefix = "@instance "

/// Whether `name` is the name of a context.
let private isContextName (state: InferState) (name: Identifier) =
    state.ContextDefs.ContainsKey name || (state.TypeDefs |> Map.tryFind name |> Option.exists _.IsContext)

/// Whether `ctx` names a parameterised context: `Sum(a)`, `Show(int)`.
let private isParameterizedContext (state: InferState) (ctx: TypeExpr) =
    match ctx.Kind with
    | TypeApply(name, _ :: _) -> isContextName state name
    | _ -> false

/// `env` with the instance of `ctxName` applied to `argTys` in scope, as `written`: bound under a name of its own, with
/// the context applied to its arguments as its type (`Sum(Point)` is `TyApply("Sum", [Point])`). Of two instances of
/// one context, the one put in scope later is nearer.
let private bindInstanceOf (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) (written: TypeExpr) : TypeEnv * InferState =
    let bindingName = $"{instancePrefix}{ctxName} {state.NextId}"
    let state = { state with NextId = state.NextId + 1; InstanceContexts = state.InstanceContexts |> Map.add bindingName written }
    TypeEnv.extend bindingName (TypeEnv.mono (TyApply(ctxName, argTys))) env, state

/// `env` with the instance `ctx` in scope, if it is one of a parameterised context (see `bindInstanceOf`).
let private bindInstance (env: TypeEnv) (state: InferState) (ctx: TypeExpr) : TypeEnv * InferState =
    match ctx.Kind with
    | TypeApply(ctxName, args) when isParameterizedContext state ctx ->
        let argTys, state = args |> List.mapFold typeExprToTy state
        bindInstanceOf env state ctxName argTys ctx
    | _ -> env, state

/// `env` with each instance of a parameterised context among `contexts` in scope (see `bindInstance`).
let private bindInstances (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) : TypeEnv * InferState =
    contexts |> List.fold (fun (env, state) ctx -> bindInstance env state ctx) (env, state)

/// `ty` with each type variable named in `names` replaced by the type it maps to.
let rec private replaceNamedVars (names: Map<string, Ty>) (ty: Ty) : Ty =
    let recur = replaceNamedVars names
    match ty with
    | TyVar { Name = Some name } when names.ContainsKey name -> names.[name]
    | TyVar _
    | TyPrimitive _ -> ty
    | TyNominal(name, underlying, isPrivate) -> TyNominal(name, recur underlying, isPrivate)
    | TyAlias(name, args, underlying) -> TyAlias(name, List.map recur args, recur underlying)
    | TyFunc(arg, ret) -> TyFunc(recur arg, recur ret)
    | TyRecord fields -> TyRecord(fields |> Map.map (fun _ fieldTy -> recur fieldTy))
    | TyOpenRecord(fields, row) -> TyOpenRecord(fields |> Map.map (fun _ fieldTy -> recur fieldTy), row)
    | TyTag(name, payload) -> TyTag(name, Option.map recur payload)
    | TyApply(name, args) -> TyApply(name, List.map recur args)
    | TyUnion items -> TyUnion(List.map recur items)

/// The type each named type variable of `general` stands for in `specific`, a type of the same shape: a function's
/// type as it is declared, against the same type instantiated at a call.
let rec private namedVarsIn (general: Ty) (specific: Ty) : Map<string, Ty> =
    let all (pairs: (Ty * Ty) list) =
        pairs |> List.fold (fun acc (g, s) -> namedVarsIn g s |> Map.fold (fun acc name ty -> acc |> Map.add name ty) acc) Map.empty
    let fieldPairs (generalFields: Map<string, Ty>) (specificFields: Map<string, Ty>) =
        [ for KeyValue(name, g) in generalFields do
            match specificFields |> Map.tryFind name with
            | Some s -> yield g, s
            | None -> () ]
    match general, specific with
    | TyVar { Name = Some name }, _ -> Map.ofList [ name, specific ]
    | TyFunc(ga, gr), TyFunc(sa, sr) -> all [ ga, sa; gr, sr ]
    | TyApply(_, gs), TyApply(_, ss) when gs.Length = ss.Length -> all (List.zip gs ss)
    | TyAlias(_, gs, gu), TyAlias(_, ss, su) when gs.Length = ss.Length -> all ((gu, su) :: List.zip gs ss)
    | TyNominal(_, gu, _), TyNominal(_, su, _) -> namedVarsIn gu su
    | TyRecord gf, TyRecord sf -> all (fieldPairs gf sf)
    | TyOpenRecord(gf, _), TyOpenRecord(sf, _) -> all (fieldPairs gf sf)
    | TyTag(_, Some g), TyTag(_, Some s) -> namedVarsIn g s
    | TyUnion gs, TyUnion ss when gs.Length = ss.Length -> all (List.zip gs ss)
    | _ -> Map.empty

/// Whether the types `a` and `b`, resolved as far as they can be, are the same type.
let private sameType (a: Ty) (b: Ty) =
    a = b
    || ((TypeEnv.freeTyVars a).IsEmpty && (TypeEnv.freeTyVars b).IsEmpty
        && Unifier.unify [ a, b, Equal, SourceSpan.none ] |> Result.isOk)

/// The instance of `ctxName` applied to `argTys` nearest in scope in `env`, as written where it is put in scope.
let private instanceInScope (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) : TypeExpr option =
    let prefix = $"{instancePrefix}{ctxName} "
    env
    |> Map.toSeq
    |> Seq.filter (fun (name, _) -> name.StartsWith prefix)
    |> Seq.choose (fun (name, scheme) ->
        match resolveSoFar state scheme.Type with
        | TyApply(_, instanceArgs) when instanceArgs.Length = argTys.Length && List.forall2 sameType instanceArgs argTys ->
            Some (int (name.Substring prefix.Length), name)
        | _ -> None)
    |> Seq.sortByDescending fst
    |> Seq.tryHead
    |> Option.bind (fun (_, name) -> state.InstanceContexts |> Map.tryFind name)

/// The members of the context `ctxName` with its parameters bound to `argTys`, each with its type.
let private contextMemberTypes (state: InferState) (ctxName: Identifier) (argTys: Ty list) : (Identifier * Ty) list option =
    match state.TypeDefs |> Map.tryFind ctxName with
    | Some info when info.Parameters.Length = argTys.Length ->
        let substitution = List.zip info.Parameters argTys |> List.fold (fun acc (param, argTy) -> acc |> Map.add param.Id argTy) Map.empty
        match Unifier.unalias info.Underlying with
        | TyRecord fields -> Some (fields |> Map.toList |> List.map (fun (name, ty) -> name, Unifier.apply substitution ty))
        | _ -> None
    | _ -> None

/// The instance of `ctxName` applied to `argTys` built from the definitions attached to the type it is for, as
/// `Point.0` and `Point.+` make a `Sum(Point)`: each member's name and the definition it is. The definitions' types
/// must be the members' types. `calleeName` requires the context at `span`.
let private synthesizeInstance (env: TypeEnv) (state: InferState) (calleeName: Identifier) (ctxName: Identifier) (argTys: Ty list) (span: SourceSpan) : Result<(string * string) list * InferState, Diagnostic list> =
    let ctxText = $"""{ctxName}({argTys |> List.map Unifier.tyToString |> String.concat ", "})"""
    let requirer = InterpolationHoles.requirer calleeName argTys
    let fail message = Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]
    match argTys, contextMemberTypes state ctxName argTys with
    | [ forType ], Some members ->
        // The name a definition for the type is attached under: `Point`, `$Money`, `Int`.
        let attachTo = attachedTypeNames forType |> List.tryHead |> Option.defaultValue (Unifier.tyToString forType)
        let found = members |> List.map (fun (memberName, memberTy) -> memberName, memberTy, tryResolveAttachedFunction env forType memberName)
        match found |> List.filter (fun (_, _, definition) -> definition.IsNone) |> List.map (fun (memberName, _, _) -> $"'{attachTo}.{memberName}'") with
        | [] ->
            found
            |> List.fold (fun result (memberName, memberTy, definition) ->
                result
                |> Result.bind (fun (bound, current: InferState) ->
                    let definition = definition.Value
                    let definitionTy, next = instantiate current (TypeEnv.tryFind definition env).Value
                    match Unifier.unify ((memberTy, definitionTy, Equal, span) :: next.Constraints) with
                    | Ok _ -> Ok (bound @ [ memberName, definition ], addConstraintAt span memberTy definitionTy next)
                    | Error _ ->
                        fail $"{requirer} requires the context '{ctxText}', but '{definition}' has the type '{Unifier.tyToString (resolveSoFar next definitionTy)}', where '{memberName}' must be '{Unifier.tyToString (resolveSoFar next memberTy)}'")) (Ok ([], state))
        | _ when InterpolationHoles.isHole calleeName -> fail (InterpolationHoles.noInstance forType)
        | missing ->
            let listed =
                match missing with
                | [ only ] -> only
                | _ -> String.concat ", " (List.truncate (missing.Length - 1) missing) + " and " + List.last missing
            let notDefined = if missing.Length = 1 then "is not defined: define it" else "are not defined: define them"
            fail $"{requirer} requires the context '{ctxText}', which is not loaded here, and {listed} {notDefined}, or load an instance with `use`"
    | _ -> fail $"{requirer} requires the context '{ctxText}', which is not loaded here: load an instance with `use`"

/// The type of each of `args`, the arguments of a parameterised context a function requires, at a call where the
/// function has the type `calleeTy`. `signature` is the type the function declares, which `args` name its type
/// variables from; it is read afresh, and is the callee's type at the call. Without one, the type variables are
/// matched by name against `generalTy`, the callee's type before it was instantiated.
let private contextArgTysAtCall (state: InferState) (signature: TypeExpr option) (generalTy: Ty) (calleeTy: Ty) (args: TypeExpr list) : Ty list * InferState =
    // Read with no variables in scope, so each variable is a fresh one, shared by the signature and the arguments.
    let mutable current = { state with TypeVars = Map.empty }
    let read (typeExpr: TypeExpr) =
        let ty, next = typeExprToTy current typeExpr
        current <- next
        ty
    let argTys =
        match signature with
        | Some signature ->
            let signatureTy = read signature
            let argTys = args |> List.map read
            current <- addConstraint signatureTy calleeTy current
            argTys
        | None ->
            let names = namedVarsIn generalTy calleeTy
            args |> List.map (read >> replaceNamedVars names)
    let current = { current with TypeVars = state.TypeVars }
    argTys |> List.map (resolveSoFar current), current

/// Each parameterised context a value of the type `valueTy` is an instance of, applied to the types it is one for: a
/// record with exactly the context's members, at types that settle each of its parameters. `use (`+` = ...)`, of a
/// record whose type does not name a context, finds that it is an `Add(int)` by the record's shape.
let private contextsOfValue (state: InferState) (valueTy: Ty) : TypeExpr list =
    match Unifier.unalias (resolveSoFar state valueTy) with
    | TyRecord fields ->
        [ for KeyValue(ctxName, info) in state.TypeDefs do
            if info.IsContext && not info.Parameters.IsEmpty then
                let paramTys, scratch = info.Parameters |> List.mapFold (fun current _ -> freshVar current) state
                match contextMemberTypes scratch ctxName paramTys with
                | Some members when Set.ofList (List.map fst members) = Set.ofSeq fields.Keys ->
                    let scratch = members |> List.fold (fun current (name, memberTy) -> addConstraint memberTy fields.[name] current) scratch
                    if Unifier.unify scratch.Constraints |> Result.isOk then
                        let argTys = paramTys |> List.map (resolveSoFar scratch)
                        if argTys |> List.forall (TypeEnv.freeTyVars >> Set.isEmpty) then
                            match argTys |> List.map (ContextInference.typeExprOf Map.empty) with
                            | args when args |> List.forall Option.isSome ->
                                yield TypeExpr.synthetic (TypeApply(ctxName, args |> List.map Option.get))
                            | _ -> ()
                | _ -> () ]
    | _ -> []

/// `env` with the instance a `use` of the value of type `valueTy` loads in scope, when it is one of a parameterised
/// context: a value whose type is the context applied to types, as `def addInt: Sum(int) = (...)` makes `addInt`
/// (recorded by the node of `value`, see `TypedModule.UseContexts`), or else a record of a context's shape (see
/// `contextsOfValue`).
let private useInstance (env: TypeEnv) (state: InferState) (value: Expression) (valueTy: Ty) : TypeEnv * InferState =
    match resolveSoFar state valueTy with
    | TyAlias(ctxName, (_ :: _ as argTys), _)
    | TyApply(ctxName, (_ :: _ as argTys)) when isContextName state ctxName ->
        let context = TyApply(ctxName, argTys)
        match ContextInference.typeExprOf (ContextInference.varNames context) context with
        | Some written ->
            let env, state = bindInstanceOf env state ctxName argTys written
            env, { state with UseContexts = state.UseContexts |> Map.add value.Id written }
        | None -> env, state
    | _ -> bindInstances env state (contextsOfValue state valueTy)

/// Finds where `call`, a call to `name` that inference has just typed, finds each parameterised context the callee
/// requires (see `TypedModule.ContextInstances`), once its arguments have settled what the context is for. A context
/// for a type inference has not worked out yet is left to be found by its members' names, as one that is not
/// parameterised is.
let private resolveCallInstances (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeSpan: SourceSpan) : Result<InferState, Diagnostic list> =
    let contexts = state.ContextRequirements |> Map.tryFind (canonicalName env state name) |> Option.defaultValue []
    match TypeEnv.tryFind name env, state.CalleeTypes |> Map.tryFind call.Id with
    | Some scheme, Some calleeTy when contexts |> List.exists (isParameterizedContext state) ->
        contexts
        |> List.fold (fun result ctx ->
            result
            |> Result.bind (fun (found, current: InferState) ->
                match ctx.Kind with
                | TypeApply(ctxName, args) when isParameterizedContext current ctx ->
                    let signature = current.ContextSignatures |> Map.tryFind (canonicalName env current name)
                    let argTys, current = contextArgTysAtCall current signature scheme.Type calleeTy args
                    match instanceInScope env current ctxName argTys with
                    | Some written -> Ok (found @ [ Some (InstanceInScope written) ], current)
                    // A hole of a built-in type is written by the backend, not by the prelude's `Int.toString` and the
                    // others, which are written with one.
                    | None when InterpolationHoles.isHole name && argTys |> List.forall InterpolationHoles.hasBuiltInInstance ->
                        Ok (found @ [ Some InstanceBuiltIn ], current)
                    // A type with a name definitions can be attached to: not a variable, or a record written out.
                    | None when argTys |> List.forall (attachedTypeNames >> List.isEmpty >> not) ->
                        synthesizeInstance env current name ctxName argTys calleeSpan
                        |> Result.map (fun (members, next) -> found @ [ Some (InstanceSynthesized members) ], next)
                    | None when InterpolationHoles.isHole name ->
                        InterpolationHoles.resolveLater env current call ctxName argTys calleeSpan found contexts.Length
                    | None ->
                        match ensureContextAvailable true env current name [ ctx ] calleeSpan, current.ContextScope with
                        | Ok next, _ -> Ok (found @ [ None ], next)
                        // In an unannotated def, a type the call leaves open may be settled by the rest of the body,
                        // or be one of the def's own variables, which the def then requires the context at.
                        | Error errors, DefScope(defName, true) when argTys |> List.exists (TypeEnv.freeTyVars >> Set.isEmpty >> not) ->
                            let pending =
                                { Def = defName; Call = call.Id; Callee = name; Index = found.Length; Count = contexts.Length
                                  Context = ctxName; ArgTys = argTys; Span = calleeSpan; Errors = errors }
                            Ok (found @ [ None ], { current with PendingContextInstances = pending :: current.PendingContextInstances })
                        | Error errors, _ -> Error errors
                | _ -> Ok (found @ [ None ], current))) (Ok ([], state))
        |> Result.map (fun (found, current) ->
            if found |> List.forall Option.isNone then current
            else { current with ContextInstances = current.ContextInstances |> Map.add call.Id found })
    | _ -> Ok state

/// `ContextInference.resolvePending` for the def `name`, building an instance from the definitions attached to the
/// type it is for, as a call does. A hole finds its instance as `InterpolationHoles.resolvePending` says.
let private resolvePendingInstances (env: TypeEnv) (state: InferState) (name: Identifier) (symbol: SymbolId option) (scheme: TypeScheme) : Result<TypeExpr option * InferState, Diagnostic list> =
    ContextInference.resolvePending state name symbol scheme (fun pending argTys current ->
        let synthesize env =
            if argTys |> List.forall (attachedTypeNames >> List.isEmpty >> not) then
                Some (synthesizeInstance env current pending.Callee pending.Context argTys pending.Span)
            else None
        if InterpolationHoles.isHole pending.Callee then
            Some (InterpolationHoles.resolvePending (fun env -> instanceInScope env current) synthesize (fun def -> TypeEnv.tryFind def env) current pending argTys)
        else
            synthesize env |> Option.map (Result.map (fun (members, next) -> Some (InstanceSynthesized members), next)))

/// The type of the field `fieldName` of a value of type `ty`, as far as the constraints gathered so far tell. A
/// value of no known type yet is taken to be a record with at least that field, and an open record without it
/// to have it among its others: reading `p.name` makes `p` a `(name: a, ...r)`. `None` when `ty` is known not to
/// have the field.
let private fieldTySoFar (state: InferState) (ty: Ty) (fieldName: string) : (Ty * InferState) option =
    let openWith (row: Ty) (next: InferState) =
        let fieldTy, next = freshVar next
        let record, next = openRecordWith (Map.ofList [ fieldName, fieldTy ]) next
        Some (fieldTy, addConstraint row record next)
    let resolved = resolveSoFar state ty
    match recordFieldsOf resolved with
    | Some fields -> fields |> Map.tryFind fieldName |> Option.map (fun fieldTy -> fieldTy, state)
    | None ->
        match Unifier.unalias resolved with
        | TyVar _ -> openWith resolved state
        | TyOpenRecord(fields, row) ->
            match fields |> Map.tryFind fieldName with
            | Some fieldTy -> Some (fieldTy, state)
            | None -> openWith (TyVar row) state
        | _ -> None

/// A def's type `ty` generalised against `env`, both resolved against the constraints gathered so far. Unresolved,
/// a local bound to a call's result is a type variable, and generalising that would make every later use a fresh,
/// unrelated variable (and let a match on it skip the exhaustiveness check, #61); a variable `env` only has once
/// resolved, as a parameter's `#some(a) | r` does, would be generalised as if nothing else had it.
///
/// Every union rest variable that nothing can add tags to is solved to the empty union first:
/// `#some(5) \mapOption f` is a `#some(string)`, not a `#some(string) | r`. Such a variable is in no function's
/// input (see `Unifier.outputOnlyRestVars`), was not named in an annotation, and is not shared with `env`, where a
/// later constraint could still give it tags. The constraint solving it is added to the state, so the types
/// recorded for the def's expressions lose it too. A rest the unifier made up, as it does when a closed union is
/// given where an open one is expected, may be numbered differently the next time the constraints are solved, so
/// rather than it, each of inference's own variables solved to a type with it is constrained to that type without it.
let private generalizeDef (env: TypeEnv) (state: InferState) (ty: Ty) : TypeScheme * InferState =
    match Unifier.unify state.Constraints with
    | Error _ -> TypeEnv.generalize env ty, state
    | Ok subst ->
        let resolved = Unifier.apply subst ty
        let envVars =
            TypeEnv.freeEnvVars env
            |> Seq.map (fun id -> TypeEnv.freeTyVars (Unifier.apply subst (TyVar { Id = id; Name = None })))
            |> Set.unionMany
        let closed =
            Unifier.outputOnlyRestVars resolved
            |> List.filter (fun v -> v.Name.IsNone && not (envVars.Contains v.Id))
        let bottom = TyUnion []
        let closing = closed |> List.map (fun v -> v.Id, bottom) |> Map.ofList
        let closedTy = Unifier.apply closing resolved
        let madeUp, own = closed |> List.partition Unifier.isMadeUpRest
        let solvedWithMadeUp =
            if madeUp.IsEmpty then []
            else
                let madeUpIds = madeUp |> List.map (fun v -> v.Id) |> Set.ofList
                subst
                |> Map.toList
                |> List.choose (fun (id, _) ->
                    let var = { Id = id; Name = None }
                    let solved = Unifier.apply subst (TyVar var)
                    if Unifier.isMadeUpRest var || Set.isEmpty (Set.intersect madeUpIds (TypeEnv.freeTyVars solved)) then None
                    else Some (TyVar var, Unifier.apply closing solved))
        { Quantified = Set.difference (TypeEnv.freeTyVars closedTy) envVars
          Type = closedTy
          Symbol = None },
        (own |> List.map (fun v -> TyVar v, bottom)) @ solvedWithMadeUp
        |> List.fold (fun st (var, closedTy) -> addConstraint var closedTy st) state

/// Arithmetic and ordering work on `int` and `float`. Without numeric type classes yet, an operation is on
/// floats when either operand is already known to be one, and defaults to `int` otherwise.
let private numericOperandType (state: InferState) (leftTy: Ty) (rightTy: Ty) =
    let isFloat ty =
        match Unifier.unalias (resolveSoFar state ty) with
        | TyPrimitive "float" -> true
        | _ -> false
    if isFloat leftTy || isFloat rightTy then TyPrimitive "float" else numericType

/// The types of a lambda's parameters when its input is expected to be `expectedInput`. Several parameters take a
/// record's fields, by name or by position. Against an input known not to be a record they are a tuple of fresh
/// variables that must be that input, so `{ x, y -> ... }` where a tag or `string` is expected is a type error
/// reported at `span`, not two parameters of no known type.
let private argTypesFromExpected (state: InferState) (span: SourceSpan) (args: (Identifier * TypeExpr option) list) (expectedInput: Ty) =
    let mutable current = state
    let argTys =
        match Unifier.unalias (resolveSoFar state expectedInput) with
        | TyRecord _ when args.Length = 1 -> [ expectedInput ]
        | TyRecord fields ->
            args
            |> List.mapi (fun index (name, _) ->
                match fields |> Map.tryFind name with
                | Some ty -> ty
                | None ->
                    let tupleName = tupleFieldName index
                    match fields |> Map.tryFind tupleName with
                    | Some ty -> ty
                    | None ->
                        let ty, next = freshVar current
                        current <- next
                        ty)
        | _ when args.Length = 1 -> [ expectedInput ]
        | resolved ->
            let tys =
                args
                |> List.map (fun _ ->
                    let ty, next = freshVar current
                    current <- next
                    ty)
            match resolved with
            | _ when args.IsEmpty -> ()
            | TyVar _
            | TyOpenRecord _ -> ()
            | _ -> current <- addConstraintAt span expectedInput (tupleRecordType tys) current
            tys
    argTys, current


/// Records the type inference gave an expression, so it can be looked up by node once unification has resolved it.
let private recordExprType (result: Result<TypedExpr * InferState, Diagnostic list>) =
    result
    |> Result.map (fun (typedExpr, state) ->
        typedExpr, { state with ExprTypes = state.ExprTypes |> Map.add typedExpr.Expr.Id typedExpr.Type })

/// The constraints inferring `expr` adds are reported at `expr` (or, for a node the compiler made up, wherever the
/// enclosing ones are); those its parent adds afterwards are reported at the parent again.
///
/// An expression that fails to check is recorded in `FailedExprs` and typed as `Unifier.errorType`, which agrees
/// with whatever its surroundings make of it, so checking carries on around it and this never gives `Error`. What
/// inferring it did before it failed is dropped. The smallest expression that fails is the one recorded: one whose
/// parts check but that does not itself (a call with the wrong number of arguments) is recorded whole.
let rec private inferExpr (env: TypeEnv) (state: InferState) (expr: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    let outer = state.Origin
    let origin = if SourceSpan.isNone expr.Span then outer else expr.Span
    let inferred =
        inferExprKind env { state with Origin = origin } expr
        |> Result.bind (fun (typedExpr, next) ->
            match expr.Kind with
            // A call to a function that requires a parameterised context finds its instance once its arguments are typed.
            | FunctionCall(name, spanOpt, _) ->
                resolveCallInstances env next expr name (nameSpan spanOpt expr) |> Result.map (fun next -> typedExpr, next)
            | _ -> Ok (typedExpr, next))
    let recovered =
        match inferred with
        | Ok _ -> inferred
        | Error diagnostics ->
            Ok (mkTypedExpr expr Unifier.errorType None None None, { state with FailedExprs = (expr.Id, diagnostics) :: state.FailedExprs })
    recovered
    |> recordExprType
    |> Result.map (fun (typedExpr, next) -> typedExpr, { next with Origin = outer })

and private inferExprKind (env: TypeEnv) (state: InferState) (expr: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    match expr.Kind with
    | LiteralExpr literal ->
        Ok (mkTypedExpr expr (literalType literal) None None None, state)
    | InterpolatedString parts ->
        let mutable current = state
        let mutable errors: Diagnostic list = []
        for part in parts do
            match part with
            | StringText _ -> ()
            | StringExpr inner ->
                match inferExpr env current inner with
                | Ok (_, next) -> current <- next
                | Error err -> errors <- errors @ err
        if errors.IsEmpty then
            Ok (mkTypedExpr expr (TyPrimitive "string") None None None, current)
        else
            Error errors
    | UnitExpr ->
        Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, state)
    // The placeholder for code not written yet is an error like any other, so a release build refuses it and a debug
    // build defers it: it has the error type, and stops the program if reached.
    | IdentifierExpr name when name = Parser.Program.Api.notImplemented ->
        Error [ Diagnostics.at DiagnosticCodes.NotImplemented expr.Span "Not implemented: `???` stands for code not written yet" ]
    | IdentifierExpr name ->
        match TypeEnv.tryFind name env with
        | Some scheme when not (referenceErrors name expr scheme state).IsEmpty -> Error (referenceErrors name expr scheme state)
        | Some scheme ->
            // A function that needs an ambient context can only be passed on where it runs in that context. One that
            // needs a context nothing here supplies can be passed to code that loads it.
            let contextCheck =
                match state.ContextRequirements |> Map.tryFind (canonicalName env state name) with
                | Some contexts -> ensureContextAvailable false env state name contexts expr.Span
                | None -> Ok state
                |> Result.bind (fun current -> checkContextUse env current (canonicalName env state name) expr.Span)
            match contextCheck with
            | Error err -> Error err
            | Ok state ->
                let ty, next = instantiate (recordReference expr scheme state) scheme
                Ok (mkTypedExpr expr ty None None None, next)
        | None -> Error [ Diagnostics.at DiagnosticCodes.UnknownIdentifier expr.Span $"Unknown identifier '{name}'" ]
    | FunctionCall(name, spanOpt, args) ->
        let calleeSpan = nameSpan spanOpt expr
        if name = "dbg" then
            match args with
            | [single] ->
                match inferExpr env state single with
                | Ok (typedArg, next) -> Ok (mkTypedExpr expr typedArg.Type None None None, next)
                | Error err -> Error err
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall calleeSpan "dbg expects a single argument" ]
        elif name = "not" then
            // The `not` keyword: the parser represents `not x` as a call to "not" with one argument.
            match args with
            | [single] ->
                match inferExpr env state single with
                | Ok (typedArg, next) ->
                    let constrained = addConstraint typedArg.Type (TyPrimitive "bool") next
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, constrained)
                | Error err -> Error err
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "not expects a single operand" ]
        elif name = "__with" then
            // `record with f = v, ...`: the parser represents a record update as a call to "__with" with the
            // record and a record of the updated fields. The result has the record's type; every updated
            // field must exist on it, at the field's type.
            match args with
            | [ baseExpr; Kind (RecordExpr updates) ] ->
                match inferExpr env state baseExpr with
                | Error err -> Error err
                | Ok (typedBase, afterBase) ->
                    match recordFieldsOf typedBase.Type with
                    | None -> Error [ Diagnostics.at DiagnosticCodes.InvalidRecordUpdate baseExpr.Span "`with` needs a record whose fields are known here" ]
                    | Some fields ->
                        let mutable current = afterBase
                        let mutable errors: Diagnostic list = []
                        // Field names have no span of their own (#96): a field is reported at its new value.
                        for update in updates do
                            match update with
                            | NamedField(fieldName, valueExpr) ->
                                match fields |> Map.tryFind fieldName with
                                | None -> errors <- errors @ [ Diagnostics.at DiagnosticCodes.UnknownMember valueExpr.Span $"Record has no member '{fieldName}' to update" ]
                                | Some fieldTy ->
                                    match inferExpr env current valueExpr with
                                    | Ok (typedValue, next) -> current <- addConstraintAt valueExpr.Span typedValue.Type fieldTy next
                                    | Error err -> errors <- errors @ err
                            | PositionalField valueExpr -> errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordUpdate valueExpr.Span "`with` updates fields by name" ]
                        if errors.IsEmpty then Ok (mkTypedExpr expr typedBase.Type None None None, current)
                        else Error errors
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`with` expects a record and the fields to update" ]

        elif name = RangeSyntax.builtIn then
            // `a..b`: a bounded range of ints, a `seq(int)`; `a..` has no end, so it is a `gen(int)` (see the ranges page).
            let mutable current = state
            let mutable errors: Diagnostic list = []
            for arg in args do
                match inferExpr env current arg with
                | Ok (typedArg, next) -> current <- addConstraint typedArg.Type (TyPrimitive "int") next
                | Error err -> errors <- errors @ err
            let sequence = if args.Length = 1 then "gen" else "seq"
            if errors.IsEmpty then Ok (mkTypedExpr expr (TyApply(sequence, [ TyPrimitive "int" ])) None None None, current)
            else Error errors
        elif name = "__rec" then
            // `rec name = value`: the value of a self-referencing local def; the block already binds the name.
            match args with
            | [ inner ] -> inferExpr env state inner
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "rec expects a single definition" ]
        elif name = ForSyntax.builtIn then
            // `for xs, ys | arms`: the parser passes the iterables and a lambda of one element of each. Each iterable
            // is a `list(a)`, a `seq(a)` or a `gen(a)`, and the loop is unit. It stops when the shortest iterable
            // does, so a `gen`, which may never end, is only walked in step with one that does.
            match expr.Kind with
            | ForSyntax.Call(iterables, lambdaExpr, lambdaArgs, lambdaBody) ->
                let elementOf (current: InferState) (iterable: Expression) =
                    inferExpr env current iterable
                    |> Result.map (fun (typedIterable, afterIterable) ->
                        match Unifier.unalias (resolveSoFar afterIterable typedIterable.Type) with
                        | TyApply(("seq" | "list" | "gen") as sequence, [ elementTy ]) -> (elementTy, sequence = "gen"), afterIterable
                        | _ ->
                            let elementTy, next = freshVar afterIterable
                            (elementTy, false), addConstraint typedIterable.Type (listType elementTy) next)
                let elements =
                    iterables |> List.fold (fun result iterable ->
                        result |> Result.bind (fun (found, current) ->
                            elementOf current iterable |> Result.map (fun (element, next) -> found @ [ element ], next))) (Ok ([], state))
                match elements with
                | Error err -> Error err
                | Ok (elements, _) when elements |> List.forall snd ->
                    let message = "A `for` loop over a `gen` alone may never end: walk it in step with a list or a bounded range, as `for xs, 0.. | x, i` does"
                    Error [ Diagnostics.at DiagnosticCodes.EndlessForLoop (List.head iterables).Span message ]
                | Ok (elements, constrained) ->
                    let expectedInput = inputTypeFromArgs (List.map fst elements)
                    match inferLambdaWithExpected env constrained lambdaExpr lambdaArgs lambdaBody (Some expectedInput) None with
                    | Ok (_, next) -> Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, next)
                    | Error err -> Error err
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "for expects an iterable and a loop body" ]
        else
        let contextCheck =
            match state.ContextRequirements |> Map.tryFind (canonicalName env state name) with
            // A parameterised context is found by its type once the arguments are typed (see `resolveCallInstances`).
            | Some contexts -> ensureContextAvailable true env state name (contexts |> List.filter (isParameterizedContext state >> not)) calleeSpan
            | None -> Ok state
            |> Result.bind (fun current -> checkContextUse env current (canonicalName env state name) calleeSpan)
        match contextCheck with
        | Error err -> Error err
        | Ok state ->
            match TypeEnv.tryFind name env with
            | Some scheme when not (referenceErrors name expr scheme state).IsEmpty -> Error (referenceErrors name expr scheme state)
            | Some scheme ->
                let mutable current = state
                let funcTy, nextState = instantiate (recordReference expr scheme current) scheme
                current <- { nextState with CalleeTypes = nextState.CalleeTypes |> Map.add expr.Id funcTy }

                let recordFieldsOpt = recordFieldsOf funcTy

                let isWorkflowCall =
                    match args with
                    | [Kind (Lambda(lambdaArgs, _))] when lambdaArgs.IsEmpty -> recordFieldsOpt
                    | _ -> None

                match isWorkflowCall with
                | Some _ ->
                    // A builder's block with no workflow keyword, `proc { ... }` with no `try` or `return`: the
                    // workflow rewrite leaves it as a call, since it cannot tell a builder from a function given a
                    // trailing lambda, but a builder is a record. The block runs where it is written, as a block
                    // with keywords does, and its value is the block's.
                    match args with
                    | [Kind (Lambda(lambdaArgs, lambdaBody)) as lambdaExpr] ->
                        match inferLambdaWithExpected env current lambdaExpr lambdaArgs lambdaBody (Some (TyPrimitive "unit")) None with
                        | Ok (typedLambda, next) ->
                            let returnTy =
                                match Unifier.unalias typedLambda.Type with
                                | TyFunc(_, ret) -> ret
                                | other -> other
                            Ok (mkTypedExpr expr returnTy None None None, { next with WorkflowBlocks = next.WorkflowBlocks.Add expr.Id })
                        | Error err -> Error err
                    | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidWorkflow calleeSpan $"Workflow '{name}' must be called with a trailing lambda" ]
                | None ->
                    // With a known signature, each argument is checked against its parameter as it is inferred,
                    // so a lambda argument's parameters can take their types from the signature and from the
                    // arguments before it: in `customer \map { c -> c.name }`, `c` is already a record when
                    // `c.name` is checked.
                    let paramTys =
                        match Unifier.unalias funcTy, args with
                        | TyFunc(declaredInput, _), [ _ ] -> Some [ declaredInput ]
                        | TyFunc(declaredInput, _), _ ->
                            match Unifier.unalias declaredInput with
                            | TyRecord fields when fields.Count = args.Length ->
                                let tys = args |> List.mapi (fun index _ -> fields |> Map.tryFind (tupleFieldName index))
                                if tys |> List.forall Option.isSome then Some (tys |> List.choose id) else None
                            | _ -> None
                        | _ -> None
                    let expectedLambdaInput index lambdaArgs =
                        let unannotated = lambdaArgs |> List.forall (fun (_, typeOpt: TypeExpr option) -> typeOpt.IsNone)
                        match paramTys |> Option.bind (List.tryItem index) |> Option.map Unifier.unalias with
                        | Some (TyFunc(lambdaInput, _)) when unannotated && not (List.isEmpty lambdaArgs) ->
                            match Unifier.unify current.Constraints with
                            | Ok subst ->
                                match Unifier.apply subst lambdaInput with
                                // A rest that widening made up is only room for tags no argument has given yet,
                                // not tags the lambda must handle: the lambda takes its input from its own
                                // patterns, as it does when the arguments come as one tuple (#452).
                                | resolved when
                                    (match Unifier.unalias resolved with
                                     | TyUnion items -> items |> List.exists (function TyVar v -> Unifier.isMadeUpRest v | _ -> false)
                                     | _ -> false) -> None
                                // A lambda of several parameters needs the input's fields to split between them.
                                | resolved when (match Unifier.unalias resolved with TyRecord _ -> true | _ -> false) -> Some resolved
                                | resolved when lambdaArgs.Length = 1 -> Some resolved
                                | _ -> None
                            | Error _ -> None
                        | _ -> None
                    let argResults =
                        args
                        |> List.mapi (fun index arg ->
                            let inferred =
                                match arg with
                                | Kind (Lambda(lambdaArgs, lambdaBody)) ->
                                    match expectedLambdaInput index lambdaArgs with
                                    | Some expectedInput -> inferLambdaWithExpected env current arg lambdaArgs lambdaBody (Some expectedInput) None
                                    | None -> inferExpr env current arg
                                | _ -> inferExpr env current arg
                            match inferred with
                            | Ok (ty, next) ->
                                current <-
                                    match paramTys |> Option.bind (List.tryItem index) with
                                    | Some paramTy -> addAssignableConstraintAt arg.Span ty.Type paramTy next
                                    | None -> next
                                Ok ty
                            | Error err -> Error err)
                    let errors = argResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                    if errors.IsEmpty then
                        let typedArgs = argResults |> List.choose (function Ok typedExpr -> Some typedExpr | _ -> None)
                        let positionalForNamed =
                            match Unifier.unalias (resolveSoFar current funcTy) with
                            | TyFunc(input, _) -> NamedArguments.check current expr name input args typedArgs
                            | _ -> None
                        match positionalForNamed with
                        | Some diagnostic -> Error [ diagnostic ]
                        | None ->
                        let argTys = typedArgs |> List.map (fun typedExpr -> typedExpr.Type)
                        // When the callee's type is already a function (an annotated or already-inferred
                        // def), its return type is the call's type directly, so a member access or match on
                        // the result can see it without waiting for the constraint below to be solved.
                        match Unifier.unalias funcTy, paramTys with
                        // Each argument was checked against its parameter above, which is all a known signature
                        // asks; constraining several arguments together as well, as one record, would make each
                        // equal to its parameter, and undo an argument union's widening into its parameter's.
                        | TyFunc(_, declaredRet), Some _ when args.Length > 1 ->
                            Ok ({ mkTypedExpr expr declaredRet None None None with Arguments = Some typedArgs }, current)
                        | _ ->
                        let retTy, nextState =
                            match Unifier.unalias funcTy with
                            | TyFunc(_, declaredRet) -> declaredRet, current
                            | _ -> freshVar current
                        // A known input is what the argument must be assignable to. A fresh variable in its place
                        // would be bound to the argument's own type first (constraints are solved newest first),
                        // and a nominal argument would then have to equal its parameter's shape.
                        let expectedInput, nextState2 =
                            match Unifier.unalias funcTy with
                            | TyFunc(declaredInput, _) -> declaredInput, nextState
                            | _ -> freshVar nextState
                        let inputTy = inputTypeFromArgs argTys
                        // A single argument is the call's input: a mismatch is that argument's. So is one with the
                        // callee's type when that is known to be a function, whose result is the call's already.
                        let inputSpan = match args with [ single ] -> single.Span | _ -> SourceSpan.none
                        let funcSpan = if paramTys.IsSome then inputSpan else SourceSpan.none
                        let withFuncConstraint = addConstraintAt funcSpan funcTy (TyFunc(expectedInput, retTy)) nextState2
                        let withAssignable = addAssignableConstraintAt inputSpan inputTy expectedInput withFuncConstraint
                        Ok ({ mkTypedExpr expr retTy None None None with Arguments = Some typedArgs }, withAssignable)
                    else
                        Error errors
            | None ->
                match tryFindTypeDef name state with
                // A `module name =` block can only make the types it sees.
                | Some info when not (blockIsolationAt state name info.Symbol calleeSpan).IsEmpty ->
                    Error (blockIsolationAt state name info.Symbol calleeSpan)
                // Only a nominal type's home module can make one: anywhere else it comes from what that module exports.
                | Some info when isNominal info && not (inNominalHome state info.Name calleeSpan) ->
                    Error [ Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule calleeSpan $"The nominal type '{name}' can only be constructed in the module that defines it; use a function that module exports instead" ]
                | Some info ->
                    let mutable current = state
                    let argResults =
                        args
                        |> List.map (fun arg ->
                            match inferExpr env current arg with
                            | Ok (ty, next) ->
                                current <- next
                                Ok ty
                            | Error err -> Error err)
                    let errors = argResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                    if errors.IsEmpty then
                        let typedArgs = argResults |> List.choose (function Ok typedExpr -> Some typedExpr | _ -> None)
                        match NamedArguments.check current expr name info.Underlying args typedArgs with
                        | Some diagnostic -> Error [ diagnostic ]
                        | None ->
                        let argTys = typedArgs |> List.map (fun typedExpr -> typedExpr.Type)
                        let inputTy = inputTypeFromArgs argTys
                        let constrained = addAssignableConstraint inputTy info.Underlying current
                        Ok (mkTypedExpr expr (namedType name info) None None None, constrained)
                    else
                        Error errors
                | None ->
                    // `s.contains("x")` is read as a call to a function named `s.contains`. When `s` is a value, the
                    // dot was meant to reach a definition attached to its type, which is a pipe's to do.
                    let dot = name.LastIndexOf '.'
                    let receiver = if dot > 0 then name.Substring(0, dot) else ""
                    match TypeEnv.tryFind receiver env with
                    | Some scheme when dot > 0 && not (receiver.Contains '.') ->
                        let memberName = name.Substring(dot + 1)
                        let receiverTy = resolveSoFar state scheme.Type
                        let message =
                            match tryResolveAttachedFunction env receiverTy memberName with
                            | Some attached ->
                                $"'{receiver}' is a value, so '{name}' is not a function's name. '{attached}' is attached to its type: call it with a pipe, as in `{receiver} \\{memberName}(...)`"
                            | None -> $"'{receiver}' is a value, so '{name}' is not a function's name, and '{Unifier.tyToString receiverTy}' has no '{memberName}' attached"
                        Error [ Diagnostics.at DiagnosticCodes.UnknownFunction calleeSpan message ]
                    | _ -> Error [ Diagnostics.at DiagnosticCodes.UnknownFunction calleeSpan $"Unknown function '{name}'" ]
    | Lambda(args, body) ->
        inferLambdaWithExpected env state expr args body None None
    | BinaryOp(_, left, right) ->
        match inferExpr env state left with
        | Error err -> Error err
        | Ok (leftExpr, next) ->
            match inferExpr env next right with
            | Error err -> Error err
            | Ok (rightExpr, nextState) ->
                let constrained = addConstraint leftExpr.Type rightExpr.Type nextState
                match OperatorCalls.definition env nextState expr leftExpr.Type with
                | Some scheme ->
                    let resultTy, called = OperatorCalls.infer nextState expr scheme (inputTypeFromArgs [ leftExpr.Type; rightExpr.Type ])
                    Ok (mkTypedExpr expr resultTy None None None, called)
                | None ->
                match expr.Kind with
                | BinaryOp("++", _, _) ->
                    // `++` concatenates two strings or two lists; both sides have the same type, which is the result.
                    // When neither side's type is known yet the result is simply that shared type.
                    let concatenable =
                        [ leftExpr.Type; rightExpr.Type ]
                        |> List.tryPick (fun ty ->
                            match Unifier.unalias ty with
                            | TyPrimitive "string" -> Some ty
                            | TyApply("list", _) -> Some ty
                            | _ -> None)
                    let resultTy = concatenable |> Option.defaultValue leftExpr.Type
                    Ok (mkTypedExpr expr resultTy None None None, constrained)
                | BinaryOp(op, _, _) when ["+"; "-"; "*"; "/"] |> List.contains op ->
                    let numericConstrained =
                        constrained
                        |> addConstraint leftExpr.Type (numericOperandType constrained leftExpr.Type rightExpr.Type)
                    Ok (mkTypedExpr expr leftExpr.Type None None None, numericConstrained)
                | BinaryOp(op, _, _) when ["<"; ">"; "<="; ">="] |> List.contains op ->
                    let numericConstrained =
                        constrained
                        |> addConstraint leftExpr.Type (numericOperandType constrained leftExpr.Type rightExpr.Type)
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, numericConstrained)
                | BinaryOp(op, _, _) when ["=="; "!="] |> List.contains op ->
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, constrained)
                | BinaryOp(op, _, _) when ["and"; "or"] |> List.contains op ->
                    // Both operands are already unified with each other; pin them to bool.
                    let boolConstrained = constrained |> addConstraint leftExpr.Type (TyPrimitive "bool")
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, boolConstrained)
                | _ -> Ok (mkTypedExpr expr leftExpr.Type None None None, constrained)
    | Pipe(value, name, spanOpt, args) ->
        match inferExpr env state value with
        | Error err -> Error err
        | Ok (typedValue, nextState) ->
            // A pipe resolves left to right: the value's type as the code before the pipe settles it picks the
            // attached function, even where that type is still a variable waiting on the module's constraints.
            let valueTy = resolveSoFar nextState typedValue.Type
            let resolvedName =
                tryResolveAttachedFunction env valueTy name
                |> Option.defaultValue name
            let nextState =
                if resolvedName = name then nextState
                else { nextState with PipeTargets = nextState.PipeTargets |> Map.add expr.Id resolvedName }
            let callArgs = pipeCallArguments value args
            // Piping what failed to check to a name attached to nothing is not another error: the name may be
            // attached to the type it was meant to have. Its arguments are still checked.
            if resolvedName = name && Unifier.isError valueTy && (TypeEnv.tryFind name env).IsNone then
                let mutable current = nextState
                for arg in args do
                    match inferExpr env current arg with
                    | Ok (_, next) -> current <- next
                    | Error _ -> ()
                Ok (mkTypedExpr expr Unifier.errorType None None None, { current with UnknownTypeUses = expr.Id :: current.UnknownTypeUses })
            else
            match inferExpr env nextState { expr with Kind = FunctionCall(resolvedName, spanOpt, callArgs) } with
            | Ok (typedCall, finalState) ->
                Ok ({ typedCall with Expr = { expr with Kind = Pipe(value, resolvedName, spanOpt, args) } }, finalState)
            | Error err -> Error err
    | UseIn(value, body) ->
        match inferExpr env state value with
        | Error err -> Error err
        | Ok (typedValue, nextState) ->
            let envWithUse, nextState = useInstance (extendEnvWithRecordFields env typedValue.Type) nextState value typedValue.Type
            match inferExpr envWithUse nextState body with
            | Ok (typedBody, finalState) -> Ok (mkTypedExpr expr typedBody.Type (Some typedBody) None None, finalState)
            | Error err -> Error err
    | WorkflowBindExpr _ ->
        Error [ Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow expr.Span "Workflow bind used outside of a workflow block" ]
    | WorkflowReturnExpr _ ->
        Error [ Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow expr.Span "Workflow return used outside of a workflow block" ]
    | Block statements ->
        inferBlock env state expr statements
    | TupleExpr items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                match inferExpr env current item with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let errors = tys |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
        if errors.IsEmpty then
            let itemExprs = tys |> List.choose (function Ok ty -> Some ty | _ -> None)
            let itemTys = itemExprs |> List.map (fun typedExpr -> typedExpr.Type)
            Ok (mkTypedExpr expr (tupleRecordType itemTys) None None None, current)
        else
            Error errors
    | RecordExpr fields ->
        let positional, named =
            fields
            |> List.fold (fun (pos, named) field ->
                match field with
                | PositionalField expr -> (expr :: pos, named)
                | NamedField(name, expr) -> (pos, (name, expr) :: named)) ([], [])
        let mutable current = state
        let positionalResults =
            positional
            |> List.rev
            |> List.map (fun expr ->
                match inferExpr env current expr with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let namedResults =
            named
            |> List.rev
            |> List.map (fun (name, expr) ->
                match inferExpr env current expr with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok (name, typedExpr)
                | Error err -> Error err)
        let errors =
            positionalResults
            |> List.choose (function Error err -> Some err | _ -> None)
            |> List.concat
            |> fun errs ->
                let namedErrors = namedResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                errs @ namedErrors
        if errors.IsEmpty then
            let positionalExprs = positionalResults |> List.choose (function Ok ty -> Some ty | _ -> None)
            let positionalFieldsMap =
                positionalExprs
                |> List.mapi (fun index typedExpr -> tupleFieldName index, typedExpr.Type)
            let namedFieldsMap =
                namedResults
                |> List.choose (function Ok (name, typedExpr) -> Some (name, typedExpr.Type) | _ -> None)
            let fieldMap =
                positionalFieldsMap @ namedFieldsMap
                |> Map.ofList
            Ok (mkTypedExpr expr (TyRecord fieldMap) None None None, current)
        else
            Error errors
    | ListExpr items ->
        let mutable current = state
        let itemResults =
            items
            |> List.map (fun item ->
                match inferExpr env current item with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let errors = itemResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
        if errors.IsEmpty then
            match itemResults |> List.choose (function Ok ty -> Some ty | _ -> None) with
            | [] ->
                let elementTy, nextState = freshVar current
                Ok (mkTypedExpr expr (listType elementTy) None None None, nextState)
            | head :: tail ->
                let constrained = tail |> List.fold (fun st ty -> addConstraint head.Type ty.Type st) current
                Ok (mkTypedExpr expr (listType head.Type) None None None, constrained)
        else
            Error errors
    | TagExpr(name, payloadOpt) ->
        match payloadOpt with
        | None -> Ok (mkTypedExpr expr (TyTag(name, None)) None None None, state)
        | Some payload ->
            match inferExpr env state payload with
            | Ok (payloadExpr, next) -> Ok (mkTypedExpr expr (TyTag(name, Some payloadExpr.Type)) None None None, next)
            | Error err -> Error err
    | IfExpr(condition, thenExpr, elseExpr) ->
        match inferExpr env state condition with
        | Error err -> Error err
        | Ok (condExpr, next) ->
            let withConstraint = addConstraint condExpr.Type (primitiveType "bool") next
            match inferExpr env withConstraint thenExpr with
            | Error err -> Error err
            | Ok (thenExprTyped, nextState) ->
                match inferExpr env nextState elseExpr with
                | Error err -> Error err
                | Ok (elseExprTyped, finalState) ->
                    let resultTy, constrained = joinBranchTypes Set.empty finalState [ thenExprTyped.Type; elseExprTyped.Type ]
                    // The condition and branches are kept as statements so that walks over the typed tree (the
                    // backend's collection of local and pattern-bound types) reach what they bind, such as a
                    // match in the else branch of a multi-branch if.
                    let children = [ condExpr; thenExprTyped; elseExprTyped ] |> List.map TypedExprStatement
                    Ok (mkTypedExpr expr resultTy None (Some children) None, constrained)
    // Desugaring has made each `except` a `match`; one checked before then is checked as that match.
    | Except(value, arms) -> inferExprKind env state { expr with Kind = Except.asMatch expr value arms }
    | Match(scrutinees, arms) ->
        let mutable current = state
        let scrutineeResults =
            scrutinees
            |> List.map (fun expr ->
                match inferExpr env current expr with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let scrutineeErrors = scrutineeResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
        // Each arm has a pattern for each value matched (`desugar` has made a lone `else` one per value).
        let wrongPatternCounts =
            let count n noun = if n = 1 then $"1 {noun}" else $"{n} {noun}s"
            arms
            |> List.choose (fun (patterns, _) ->
                if patterns.Length = scrutinees.Length then
                    None
                else
                    let span =
                        match patterns with
                        | [] -> expr.Span
                        | _ -> SourceSpan.between (List.head patterns).Span (List.last patterns).Span
                    let written = count patterns.Length "pattern"
                    let values = count scrutinees.Length "value"
                    Some (
                        Diagnostics.at
                            DiagnosticCodes.WrongPatternCount
                            span
                            $"This arm has {written}, but the match is on {values}: an arm needs a pattern for each value, or a lone `else` for anything else"))
        if not scrutineeErrors.IsEmpty then
            Error scrutineeErrors
        elif not wrongPatternCounts.IsEmpty then
            Error wrongPatternCounts
        else
            let scrutineeExprs = scrutineeResults |> List.choose (function Ok ty -> Some ty | _ -> None)
            let scrutineeTypes = scrutineeExprs |> List.map (fun typedExpr -> typedExpr.Type)
            let mkTypedPattern pattern ty = { Pattern = pattern; Type = ty }
            let rec tryRecordFields ty =
                match ty with
                | TyRecord fields -> Some fields
                | TyNominal(_, underlying, _)
                | TyAlias(_, _, underlying) -> tryRecordFields underlying
                | _ -> None
            // The variables a pattern binds are declared by the body of its arm, `armBody`, and are in scope until
            // its end. As with `inferExpr`, the constraints inferring `pattern` adds are reported at `pattern`.
            let rec inferPattern (armBody: Expression) (env': TypeEnv) (state': InferState) (scrutineeTy: Ty) (pattern: Pattern) =
                let outer = state'.Origin
                let origin = if SourceSpan.isNone pattern.Span then outer else pattern.Span
                inferPatternKind armBody env' { state' with Origin = origin } scrutineeTy pattern
                |> Result.map (fun (nextEnv, next, typedPattern) -> nextEnv, { next with Origin = outer }, typedPattern)

            and inferPatternKind (armBody: Expression) (env': TypeEnv) (state': InferState) (scrutineeTy: Ty) (pattern: Pattern) =
                match pattern.Kind with
                | WildcardPattern
                | ElsePattern -> Ok (env', state', mkTypedPattern pattern scrutineeTy)
                // `()` matches the unit value, so what it is matched against is unit.
                | UnitPattern -> Ok (env', addConstraint scrutineeTy (TyPrimitive "unit") state', mkTypedPattern pattern scrutineeTy)
                | IdentifierPattern name ->
                    let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) state'
                    Ok (TypeEnv.declare name symbol (TypeEnv.mono scrutineeTy) env', recordSymbolType symbol scrutineeTy declared, mkTypedPattern pattern scrutineeTy)
                | LiteralPattern literal ->
                    let constrained = addConstraint scrutineeTy (literalType literal) state'
                    Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                // A predicate on the value matched. It sees what is in scope at the match, not what its own arm binds:
                // an arm's names are bound only once all its patterns have matched.
                | GuardPattern predicate ->
                    match predicate.Kind with
                    | Lambda([ argument ], body) ->
                        inferLambdaWithExpected env state' predicate [ argument ] body (Some scrutineeTy) (Some (primitiveType "bool"))
                        |> recordExprType
                        |> Result.map (fun (_, nextState) -> env', nextState, mkTypedPattern pattern scrutineeTy)
                    | _ ->
                        Error [
                            Diagnostics.at
                                DiagnosticCodes.GuardNotAPredicate
                                predicate.Span
                                "A guard pattern is a predicate on the value matched, a lambda of one parameter such as `{ < 0 }` or `{ x -> x % 2 == 0 }`" ]
                // Every part matches the same value, and the names each binds are in scope after it.
                | AndPattern parts ->
                    let repeated =
                        parts
                        |> List.collect (Pattern.boundNames >> List.distinct)
                        |> List.countBy id
                        |> List.filter (fun (_, count) -> count > 1)
                        |> List.map fst
                    match repeated with
                    | name :: _ ->
                        Error [
                            Diagnostics.at
                                DiagnosticCodes.NameBoundTwiceInPattern
                                pattern.Span
                                $"The name '{name}' is bound by more than one part of this pattern; each part of an and-pattern must bind different names" ]
                    | [] ->
                        let step result part =
                            result
                            |> Result.bind (fun (partEnv, partState) ->
                                inferPattern armBody partEnv partState scrutineeTy part
                                |> Result.map (fun (nextEnv, nextState, _) -> nextEnv, nextState))
                        List.fold step (Ok (env', state')) parts
                        |> Result.map (fun (nextEnv, nextState) -> nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                | RangePattern(startExpr, endExpr) ->
                    match inferExpr env' state' startExpr with
                    | Error err -> Error err
                    | Ok (startTyped, nextState) ->
                        match inferExpr env' nextState endExpr with
                        | Error err -> Error err
                        | Ok (endTyped, finalState) ->
                            let constrained =
                                finalState
                                |> addConstraint scrutineeTy startTyped.Type
                                |> addConstraint startTyped.Type endTyped.Type
                            Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                | TuplePattern patterns ->
                    let mutable currentState = state'
                    let itemTypes, nextState =
                        patterns
                        |> List.map (fun _ ->
                            let ty, next = freshVar currentState
                            currentState <- next
                            ty)
                        |> fun tys -> tys, currentState
                    // A name alone matches the field it names, where the value is already known to be a record with
                    // one; the other elements match fields by position.
                    let knownFields =
                        if patterns |> List.exists (function Kind (IdentifierPattern _) -> true | _ -> false) then
                            recordFieldsSoFar state' scrutineeTy |> Option.map (Map.keys >> Set.ofSeq)
                        else None
                    let tupleRecord =
                        match knownFields with
                        | Some fields ->
                            List.zip patterns itemTypes
                            |> List.mapi (fun index (pat, ty) -> Pattern.tupleElementField fields index pat, ty)
                            |> Map.ofList
                            |> TyRecord
                        | None -> tupleRecordType itemTypes
                    let constrained = addConstraint scrutineeTy tupleRecord nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    let mutable innerPatterns: TypedPattern list = []
                    for (pat, ty) in List.zip patterns itemTypes do
                        match inferPattern armBody innerEnv innerState ty pat with
                        | Ok (nextEnv, nextState, typedPattern) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                            innerPatterns <- innerPatterns @ [typedPattern]
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerState, mkTypedPattern pattern tupleRecord)
                    else
                        Error innerErrors
                | TagPattern(tagName, payloadOpt) ->
                    match payloadOpt with
                    | None ->
                        let constrained = addAssignableConstraint (TyTag(tagName, None)) scrutineeTy state'
                        Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                    | Some payload ->
                        let payloadTy, nextState = freshVar state'
                        let constrained = addAssignableConstraint (TyTag(tagName, Some payloadTy)) scrutineeTy nextState
                        match inferPattern armBody env' constrained payloadTy payload with
                        | Ok (nextEnv, nextState, _) ->
                            Ok (nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                        | Error err -> Error err
                | ListPattern(patterns, splatOpt) ->
                    let elementTy, nextState = freshVar state'
                    let constrained = addConstraint scrutineeTy (listType elementTy) nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    for pat in patterns do
                        match inferPattern armBody innerEnv innerState elementTy pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                        | Error err -> innerErrors <- err
                    let innerStateWithSplat =
                        match splatOpt with
                        | Some (Kind (IdentifierPattern name)) ->
                            let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) innerState
                            innerEnv <- TypeEnv.declare name symbol (TypeEnv.mono (listType elementTy)) innerEnv
                            recordSymbolType symbol (listType elementTy) declared
                        | _ -> innerState
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerStateWithSplat, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors
                | ListSplatMiddle(prefix, suffix) ->
                    let elementTy, nextState = freshVar state'
                    let constrained = addConstraint scrutineeTy (listType elementTy) nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    for pat in prefix @ suffix do
                        match inferPattern armBody innerEnv innerState elementTy pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors
                // `$Email(raw)` takes a nominal value apart: the payload pattern matches the value's shape. A protected
                // type's shape is hidden outside its home module, so only that module can look inside one.
                | RecordPattern(typeName, [ payload ]) when (tryFindTypeDef typeName state' |> Option.exists isNominal) ->
                    let info = (tryFindTypeDef typeName state').Value
                    let isolation = blockIsolationAt state' typeName info.Symbol pattern.Span
                    if not isolation.IsEmpty then
                        Error isolation
                    elif info.IsPrivate && not (inNominalHome state' info.Name pattern.Span) then
                        Error [ Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule pattern.Span $"The protected type '{typeName}' can only be taken apart in the module that defines it; use a function that module exports instead" ]
                    else
                        let constrained = addConstraint scrutineeTy (namedType typeName info) state'
                        match inferPattern armBody env' constrained info.Underlying payload with
                        | Ok (nextEnv, nextState, _) -> Ok (nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                        | Error err -> Error err
                | RecordPattern(typeName, patterns) ->
                    let mutable currentState = state'
                    let itemTypes, nextState =
                        patterns
                        |> List.map (fun _ ->
                            let ty, next = freshVar currentState
                            currentState <- next
                            ty)
                        |> fun tys -> tys, currentState
                    let anyRecord, nextState = openRecordWith Map.empty nextState
                    let constrained = addConstraint scrutineeTy anyRecord nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    for (pat, ty) in List.zip patterns itemTypes do
                        match inferPattern armBody innerEnv innerState ty pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors
                | RecordMemberPattern members ->
                    let mutable innerEnv = env'
                    let mutable innerState = state'
                    let mutable innerErrors: Diagnostic list = []
                    let fieldHints = tryRecordFields scrutineeTy |> Option.defaultValue Map.empty
                    let mutable fieldMap = Map.empty
                    for (name, pat) in members do
                        let hintTy = fieldHints |> Map.tryFind name
                        let memberTy, nextState =
                            match hintTy with
                            | Some ty -> ty, innerState
                            | None -> freshVar innerState
                        innerState <- nextState
                        match inferPattern armBody innerEnv innerState memberTy pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                            if hintTy.IsNone then
                                fieldMap <- fieldMap.Add(name, memberTy)
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        // The pattern names some of the record's fields, not all of them.
                        let constrained =
                            if fieldMap.IsEmpty then innerState
                            else
                                let record, next = openRecordWith fieldMap innerState
                                addConstraint scrutineeTy record next
                        Ok (innerEnv, constrained, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors

            let mutable armExprs: TypedExpr list = []
            let mutable typedArms: TypedMatchArm list = []
            let mutable errors: Diagnostic list = []
            let patternsForIndex index =
                arms
                |> List.choose (fun (patterns, _) ->
                    if index < patterns.Length then Some patterns.[index] else None)
            // A scrutinee's type may still be a variable here (a call's result, a local bound to one). For the
            // exhaustiveness check it is resolved against the constraints gathered so far, and before the arms
            // add theirs: a lone `#some(_)` arm would otherwise narrow the variable to that tag first and the
            // check would pass silently (#61).
            let resolvedScrutineeTypes = scrutineeTypes |> List.map (resolveSoFar current >> Unifier.unalias)
            let mutable scrutineeOverrides: Map<int, Ty> = Map.empty
            for (index, scrutineeTy) in scrutineeTypes |> List.indexed do
                // The arms describe the scrutinee's union, payloads included (`ArmUnion`). With a catch-all arm they
                // need not name every tag, so the union is left open with a rest variable for the tags the arms do not
                // name (`if #some(v) = get(n) then ...` must accept a `#some(int) | #none`).
                match ArmUnion.describe false (patternsForIndex index) current with
                | Some (unionTy, stateForUnion) ->
                    scrutineeOverrides <- scrutineeOverrides |> Map.add index unionTy
                    current <- addConstraint scrutineeTy unionTy stateForUnion
                | None -> ()
            // What is wrong with the match as a whole is reported at its head (`match a`), not over its arms.
            let headSpan = MatchHead.span expr scrutinees arms
            // A value no arm matches (`PatternSpace.missingCase`) is described as the one value that makes it so, or
            // as the combination of values.
            let exhaustivenessDiagnostics () =
                match PatternSpace.missingCase (arms |> List.map fst) (resolvedScrutineeTypes |> List.map Some) with
                | None -> []
                | Some witnesses ->
                    match witnesses |> List.indexed |> List.filter (fun (_, witness) -> witness <> PatternSpace.Anything) with
                    | [ index, witness ] ->
                        let suffix = if scrutineeTypes.Length > 1 then $" (scrutinee {index + 1})" else ""
                        [ Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan $"Non-exhaustive match{suffix}, {PatternSpace.whatIsMissing witness}" ]
                    | _ ->
                        let missing = witnesses |> List.map PatternSpace.show |> String.concat ", "
                        [ Diagnostics.at DiagnosticCodes.NonExhaustiveMatch headSpan $"Non-exhaustive match, missing case: {missing}" ]
            // A tag that an earlier arm handles in full (its payload and every other position are catch-alls)
            // cannot reach a later arm, so a name bound by a catch-all in a later arm has the scrutinee's union
            // without those tags; with only the rest left, it has the rest's type. That is what lets
            // `| #some(a), f -> #some(f(a)) | other, _ -> other` be `(#some(a) | r), (a -> b) -> (#some(b) | r)`.
            let tagsHandledBefore armIndex index =
                arms
                |> List.take armIndex
                |> List.choose (fun (patterns, _) -> PatternSpace.tagHandled patterns index)
                |> Set.ofList
            let restRowTy index =
                match scrutineeOverrides |> Map.tryFind index with
                | Some (TyUnion items) -> items |> List.tryPick (function TyVar _ as rest -> Some rest | _ -> None)
                | _ -> None
            let catchAllBindingTy armIndex index =
                match scrutineeOverrides |> Map.tryFind index, restRowTy index with
                | Some (TyUnion items), Some _ ->
                    let handled = tagsHandledBefore armIndex index
                    if handled.IsEmpty then
                        None
                    else
                        match items |> List.filter (function TyTag(name, _) -> not (handled.Contains name) | _ -> true) with
                        | [ single ] -> Some single
                        | remaining -> Some (TyUnion remaining)
                | _ -> None
            let rowVars =
                scrutineeTypes
                |> List.indexed
                |> List.choose (fun (index, _) -> restRowTy index)
                |> List.choose (function TyVar v -> Some v.Id | _ -> None)
                |> Set.ofList
            for (armIndex, (patterns, expr)) in List.indexed arms do
                if errors.IsEmpty then
                    let mutable env' = env
                    let mutable state' = current
                    let effectiveScrutineeTypes =
                        scrutineeTypes
                        |> List.mapi (fun idx ty ->
                            match List.tryItem idx patterns with
                            | Some (Kind (IdentifierPattern _)) when (catchAllBindingTy armIndex idx).IsSome ->
                                (catchAllBindingTy armIndex idx).Value
                            | _ -> scrutineeOverrides |> Map.tryFind idx |> Option.defaultValue ty)
                    let patternPairs = List.zip patterns effectiveScrutineeTypes
                    let mutable typedPatterns: TypedPattern list = []
                    for (pattern, scrutineeTy) in patternPairs do
                        match inferPattern expr env' state' scrutineeTy pattern with
                        | Ok (nextEnv, nextState, typedPattern) ->
                            env' <- nextEnv
                            state' <- nextState
                            typedPatterns <- typedPatterns @ [typedPattern]
                        | Error err -> errors <- err
                    if errors.IsEmpty then
                        match inferExpr env' state' expr with
                        | Ok (typedExpr, next) ->
                            current <- next
                            armExprs <- typedExpr :: armExprs
                            typedArms <- typedArms @ [ (typedPatterns, typedExpr) ]
                        | Error err -> errors <- err
            if errors.IsEmpty then
                let hasRestUnion =
                    resolvedScrutineeTypes
                    |> List.exists (function
                        | TyUnion items -> items |> List.exists (function TyVar _ -> true | _ -> false)
                        | _ -> false)
                let restError =
                    if hasRestUnion then
                        let hasCatchAll =
                            arms
                            |> List.exists (fun (patterns, _) ->
                                patterns
                                |> List.exists (function
                                    | Kind (WildcardPattern | ElsePattern | IdentifierPattern _) -> true
                                    | _ -> false))
                        if hasCatchAll then
                            None
                        else
                            Some (Diagnostics.at DiagnosticCodes.MissingCatchAllArm headSpan "Tag union rest parameters require a catch-all match arm")
                    else
                        None
                let exhaustivenessErrors =
                    if restError.IsSome then
                        []
                    else
                        exhaustivenessDiagnostics ()
                let armTypes = armExprs |> List.rev |> List.map (fun typedExpr -> typedExpr.Type)
                let resultTy, constrained = joinBranchTypes rowVars current armTypes
                // A match that does not handle every case is reported, and typed as if it did: a debug build gives it
                // an arm that traps on the cases it leaves out.
                let unhandled = Option.toList restError @ exhaustivenessErrors
                let constrained =
                    if unhandled.IsEmpty then constrained
                    else { constrained with UnhandledMatches = (expr.Id, unhandled) :: constrained.UnhandledMatches }
                Ok (mkTypedExpr expr resultTy None None (Some typedArms), constrained)
            else
                Error errors
    | MemberAccess(baseExpr, memberName, spanOpt) ->
        let memberSpan = nameSpan spanOpt expr
        match baseExpr.Kind with
        | IdentifierExpr baseName ->
            let qualifiedName = $"{baseName}.{memberName}"
            match TypeEnv.tryFind qualifiedName env with
            | Some scheme when not (referenceErrors qualifiedName expr scheme state).IsEmpty -> Error (referenceErrors qualifiedName expr scheme state)
            | Some scheme ->
                let ty, next = instantiate (recordReference expr scheme state) scheme
                Ok (mkTypedExpr expr ty None None None, next)
            | None -> readMember env state expr baseExpr (Some baseName) memberName memberSpan
        | _ -> readMember env state expr baseExpr None memberName memberSpan
    | ContextMemberAccess(ctxType, memberName) ->
        // TODO: Look up the context type and find the member's type
        // For now, return a fresh type variable
        let ty, next = freshVar (recordContextMemberReference expr ctxType memberName state)
        Ok (mkTypedExpr expr ty None None None, next)
    | ContextMemberCall(ctxType, memberName, args) ->
        // TODO: Look up the context, find the member function type, check args
        // For now, infer args and return a fresh type variable
        let mutable current = recordContextMemberReference expr ctxType memberName state
        let mutable errors: Diagnostic list = []
        for arg in args do
            match inferExpr env current arg with
            | Ok (_, next) -> current <- next
            | Error err -> errors <- errors @ err
        if errors.IsEmpty then
            let ty, next = freshVar current
            Ok (mkTypedExpr expr ty None None None, next)
        else
            Error errors

/// `baseExpr.memberName`, where `expr` is that member access and `receiver` the base as written if it is a name: a
/// record's field, or else a built-in property of the base's type as far as the code before it settles that type,
/// as for a pipe. A value of no known type yet is taken to be a record with that field (see `fieldTySoFar`), so
/// `{ s -> s.length }` reads a field, not a string's length.
and private readMember (env: TypeEnv) (state: InferState) (expr: Expression) (baseExpr: Expression) (receiver: string option) (memberName: string) (memberSpan: SourceSpan) =
    match inferExpr env state baseExpr with
    | Error err -> Error err
    | Ok (typedBase, next) ->
        match fieldTySoFar next typedBase.Type memberName with
        | Some (fieldTy, next) -> Ok (mkTypedExpr expr fieldTy None None None, next)
        | None ->
            let baseTy = resolveSoFar next typedBase.Type
            match builtInProperty baseTy memberName with
            | Some (property, propertyTy) ->
                Ok (mkTypedExpr expr propertyTy None None None, { next with BuiltInProperties = next.BuiltInProperties |> Map.add expr.Id property })
            // What failed to check was reported where it failed; reading from it is not another error.
            | None when Unifier.isError baseTy ->
                Ok (mkTypedExpr expr Unifier.errorType None None None, { next with UnknownTypeUses = expr.Id :: next.UnknownTypeUses })
            // A builder block that can end without `return` needs the builder's `zero` to be its value there.
            | None when next.WorkflowZeros.ContainsKey expr.Id ->
                let builderName = receiver |> Option.defaultValue "builder"
                let place = next.WorkflowZeros.[expr.Id]
                [ Diagnostics.at DiagnosticCodes.InvalidWorkflow place $"This `{builderName}` block can end here without `return`, and `{builderName}` has no `zero` to be its value then: end the block with `return`, or give `{builderName}` a `zero`" ]
                |> Error
            | None -> Error [ unknownMember env memberSpan receiver baseTy memberName ]

/// Infers the value named by each `typeof` in `typeExpr`, so that reading the annotation finds their types.
and private inferTypeOfValues (env: TypeEnv) (state: InferState) (typeExpr: TypeExpr) : Result<InferState, Diagnostic list> =
    typeOfValues typeExpr
    |> List.fold (fun result value -> result |> Result.bind (fun current -> inferExpr env current value |> Result.map snd)) (Ok state)

and inferLambdaWithExpected (env: TypeEnv) (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (body: Expression) (expectedInputOpt: Ty option) (expectedReturnOpt: Ty option) : Result<TypedExpr * InferState, Diagnostic list> =
    // The parameters' annotations are read only when nothing else gives their types, but each must name types.
    let annotated =
        match ensureTypesExist state (args |> List.choose snd), expectedInputOpt with
        | Error errors, _ -> Error errors
        | Ok (), Some _ -> Ok state
        | Ok (), None ->
            args
            |> List.choose snd
            |> List.fold (fun result typeExpr -> result |> Result.bind (fun current -> inferTypeOfValues env current typeExpr)) (Ok state)
    annotated |> Result.bind (fun state -> inferLambdaAfterTypeOfs env state lambda args body expectedInputOpt expectedReturnOpt)

and private inferLambdaAfterTypeOfs (env: TypeEnv) (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (body: Expression) (expectedInputOpt: Ty option) (expectedReturnOpt: Ty option) : Result<TypedExpr * InferState, Diagnostic list> =
    let mutable current = state
    let mutable env' = env
    let argTys =
        match expectedInputOpt with
        | Some expectedInput ->
            let tys, next = argTypesFromExpected current lambda.Span args expectedInput
            current <- next
            tys
        | None ->
            args
            |> List.map (fun (_, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some typeExpr -> typeExprToTy current typeExpr
                    | None -> freshVar current
                current <- next
                ty)
    for ((name, typeOpt), ty) in List.zip args argTys do
        let symbol, next = declareSymbol LambdaParameters lambda.Id name Parameter false (Some lambda.Span.End) current
        current <- next |> recordSymbolType symbol ty |> recordAnnotated symbol typeOpt.IsSome
        env' <- TypeEnv.declare name symbol (TypeEnv.mono ty) env'
    match inferExpr env' current body with
    | Ok (bodyExpr, next) ->
        let withReturnConstraint =
            match expectedReturnOpt with
            | Some expectedReturn ->
                match Unifier.unalias expectedReturn with
                | TyNominal(name, underlying, _) when inNominalHome state name body.Span ->
                    match Unifier.unalias bodyExpr.Type with
                    | TyNominal _ -> addAssignableConstraintAt body.Span bodyExpr.Type expectedReturn next
                    | _ -> addAssignableConstraintAt body.Span bodyExpr.Type underlying next
                | _ -> addAssignableConstraintAt body.Span bodyExpr.Type expectedReturn next
            | None -> next
        let inputTy =
            match expectedInputOpt with
            | Some expectedInput -> expectedInput
            | None -> inputTypeFromArgs argTys
        Ok (mkTypedExpr lambda (TyFunc(inputTy, bodyExpr.Type)) (Some bodyExpr) None None, withReturnConstraint)
        // Called directly for a lambda whose input is known, not only through `inferExpr`.
        |> recordExprType
    | Error err -> Error err

and inferBlock (env: TypeEnv) (state: InferState) (block: Expression) (statements: Statement list) : Result<TypedExpr * InferState, Diagnostic list> =
    match statements with
    | [] -> Ok (mkTypedExpr block (TyPrimitive "unit") None (Some []) None, state)
    | _ ->
        let mutable current = state
        let mutable env' = env
        let mutable lastExpr = mkTypedExpr (Expression.synthetic (LiteralExpr (IntLit 0))) (TyPrimitive "unit") None None None
        let mutable typedStatements: TypedStatement list = []
        let mutable errors: Diagnostic list = []
        for statement in statements do
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, expr) ->
                if errors.IsEmpty then
                    let contextsExist =
                        match fst (extractContextFromTypeExpr typeOpt) with
                        | Some contexts -> ensureContextsExist current name (declarationSpan current DefValue expr) contexts
                        | None -> Ok ()
                    let typeOfsInferred =
                        match typeOpt with
                        | Some typeExpr -> inferTypeOfValues env' current typeExpr
                        | None -> Ok current
                    match ensureAttachedTypeExists current None name (declarationSpan current DefValue expr) |> Result.bind (fun () -> ensureTypesExist current (Option.toList typeOpt)) |> Result.bind (fun () -> contextsExist) |> Result.bind (fun () -> typeOfsInferred) with
                    | Error err -> errors <- err
                    | Ok inferredState ->
                        current <- inferredState
                        let contextOpt, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
                        let envWithCtx, nextState =
                            match contextOpt with
                            | Some contexts -> extendEnvWithContexts env' current contexts ||> fun env state -> bindInstances env state contexts
                            | None -> env', current
                        let expectedInputOpt, expectedReturnOpt, expectedDeclaredOpt, nextState2 =
                            match declaredTypeExprOpt with
                            | Some declaredExpr ->
                                let ty, st = typeExprToTy nextState declaredExpr
                                match Unifier.unalias ty with
                                | TyFunc(inputTy, returnTy) -> TypeHoles.expectedLambdaInput declaredExpr (Some inputTy), Some returnTy, Some ty, st
                                | _ -> None, None, Some ty, st
                            | None -> None, None, None, nextState
                        let effectiveExpectedInputOpt =
                            match expectedInputOpt with
                            | Some _ -> expectedInputOpt
                            | None when expectedDeclaredOpt.IsNone -> tryInferAttachedLambdaInput nextState2 name expr
                            | None -> None
                        let symbol, declaredState = declareSymbol DefValue expr.Id name LocalValue false (Some block.Span.End) nextState2
                        let preboundTy, preboundState, envWithRec, preboundFresh =
                            match expectedDeclaredOpt with
                            | Some declaredTy -> declaredTy, declaredState, TypeEnv.declare name symbol (TypeEnv.mono declaredTy) envWithCtx, false
                            | None ->
                                let ty, st = freshVar declaredState
                                ty, st, TypeEnv.declare name symbol (TypeEnv.mono ty) envWithCtx, true
                        let nextStateWithReq =
                            match contextOpt with
                            | Some contexts -> registerContextRequirement preboundState name contexts declaredTypeExprOpt
                            | None -> preboundState
                        // A local function that writes its type runs in the contexts the type declares; one that does
                        // not runs in those of the def around it.
                        let outerScope = nextStateWithReq.ContextScope
                        let bodyState =
                            match typeOpt, expr.Kind with
                            | Some _, Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, false) }
                            | _ -> nextStateWithReq
                        let inferResult =
                            match expr.Kind with
                            | Lambda(args, body) when effectiveExpectedInputOpt.IsSome || expectedReturnOpt.IsSome ->
                                inferLambdaWithExpected envWithRec bodyState expr args body effectiveExpectedInputOpt expectedReturnOpt
                            | _ -> inferExpr envWithRec bodyState expr
                            |> Result.map (fun (typedExpr, next) -> typedExpr, { next with ContextScope = outerScope })
                        match inferResult with
                        | Ok (typedExpr, next) ->
                            let declaredTy, finalState =
                                match expectedDeclaredOpt with
                                | Some declaredTy ->
                                    match expr.Kind with
                                    | Lambda _ -> declaredTy, TypeHoles.constrainLambda expr.Span declaredTypeExprOpt declaredTy typedExpr.Type next
                                    | _ ->
                                        let updatedState =
                                            match Unifier.unalias declaredTy with
                                            | TyNominal(name, underlying, _) when inNominalHome current name expr.Span ->
                                                match Unifier.unalias typedExpr.Type with
                                                | TyNominal _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                                | _ -> addAssignableConstraintAt expr.Span typedExpr.Type underlying next
                                            | _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                        declaredTy, updatedState
                                | None ->
                                    let updatedState =
                                        if preboundFresh then addConstraintAt expr.Span preboundTy typedExpr.Type next else next
                                    typedExpr.Type, updatedState
                            current <-
                                finalState
                                |> recordSymbolType symbol declaredTy
                                |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                            // The rest of a workflow block after an `if` or `match` with keywords in its branches
                            // is bound to a local the branches all go on to. It is one continuation, not a function
                            // to use at different types: generalised, its failures would be closed to those of the
                            // rest alone, and a `try` in a branch could not add its own.
                            let scheme, generalizedState =
                                if name.StartsWith workflowRestPrefix then TypeEnv.mono declaredTy, current
                                else generalizeDef env' current declaredTy
                            current <- generalizedState
                            env' <- TypeEnv.declare name symbol scheme env'
                            typedStatements <- typedStatements @ [ TypedDefStatement(isExport, name, typeOpt, typedExpr) ]
                            lastExpr <- typedExpr
                        | Error err -> errors <- err
            | ImportStatement items ->
                typedStatements <- typedStatements @ [ TypedImportStatement items ]
            | TypeDefStatement(name, modifiers, parameters, body) ->
                if errors.IsEmpty then errors <- typeOfInTypeDef name body parameters @ TypeHoles.inTypeDef name body parameters
                current <- registerTypeDef current name modifiers parameters body
                // Checked once the type is registered, so it can name itself.
                if errors.IsEmpty then errors <- unknownTypesInTypeDef current body parameters
                typedStatements <- typedStatements @ [ TypedTypeDefStatement(name, modifiers, parameters, body) ]
            | UseStatement value ->
                if errors.IsEmpty then
                    match inferExpr env' current value with
                    | Ok (typedExpr, next) ->
                        let withUse, next = useInstance (extendEnvWithRecordFields env' typedExpr.Type) next value typedExpr.Type
                        current <- next
                        env' <- withUse
                        typedStatements <- typedStatements @ [ TypedUseStatement typedExpr ]
                    | Error err -> errors <- err
            | ExprStatement expr ->
                if errors.IsEmpty then
                    match inferExpr env' current expr with
                    | Ok (typedExpr, next) ->
                        current <- next
                        typedStatements <- typedStatements @ [ TypedExprStatement typedExpr ]
                        lastExpr <- typedExpr
                    | Error err -> errors <- err
        if errors.IsEmpty then
            let blockExpr = mkTypedExpr block lastExpr.Type (Some lastExpr) (Some typedStatements) None
            Ok (blockExpr, current)
        else
            Error errors

/// The names of the type variables written in an annotation, in order of first appearance.
let rec private annotationVarNames (typeExpr: TypeExpr) : string list =
    let ofList items = items |> List.collect annotationVarNames
    let names =
        match typeExpr.Kind with
        | TypeVar name -> [ name ]
        | TypeName _ | TypeHole | TypeUnit | TypeContext _ -> []
        | TypeTuple items | TypeUnion items | TypeIntersection items -> ofList items
        | TypeRecord fields ->
            fields
            |> List.collect (function
                | TypeField(_, _, _, Some t, _) | TypeMember(_, Some t) -> annotationVarNames t
                | _ -> [])
        | TypeApply(_, args) -> ofList args
        | TypeFunc(arg, ret) -> annotationVarNames arg @ annotationVarNames ret
        | TypeTag(_, payload) -> payload |> Option.map annotationVarNames |> Option.defaultValue []
        | TypeOptional inner | TypeWithContext(_, inner) | TypeConstraint(_, inner, _) | TypeWhere(inner, _) ->
            annotationVarNames inner
        // The type of a value is not a variable the annotation introduces.
        | TypeOf _ -> []
    names |> List.distinct

/// The type variables of a def's annotation are rigid: the body has to work for every type they stand for.
/// Once the constraints gathered so far are solved, each must still be a variable of its own. One bound to
/// a concrete type, or to the same type as another of the annotation's variables, means the annotation is
/// more general than the body, which would otherwise quietly narrow it.
let private checkAnnotationIsRigid (state: InferState) (name: Identifier) (span: SourceSpan) (typeExpr: TypeExpr) : Result<unit, Diagnostic list> =
    let vars =
        annotationVarNames typeExpr
        |> List.choose (fun varName -> state.TypeVars |> Map.tryFind varName |> Option.map (fun var -> varName, var))
    if vars.IsEmpty then
        Ok ()
    else
        match Unifier.unify state.Constraints with
        // A conflict is reported when the module's constraints are solved.
        | Error _ -> Ok ()
        | Ok subst ->
            let resolved = vars |> List.map (fun (varName, var) -> varName, Unifier.apply subst (TyVar var))
            // Show the variables an annotation variable resolved to by that annotation variable's name.
            let displayNames =
                resolved
                |> List.fold (fun acc (varName, ty) ->
                    match ty with
                    | TyVar image when not (acc |> Map.containsKey image.Id) -> acc |> Map.add image.Id (TyPrimitive varName)
                    | _ -> acc) Map.empty
            let problems =
                resolved
                |> List.mapi (fun index (varName, ty) ->
                    match ty with
                    | TyVar _ ->
                        resolved
                        |> List.take index
                        |> List.tryFind (fun (_, otherTy) -> otherTy = ty)
                        |> Option.map (fun (otherName, _) -> $"`{varName}` would have to be `{otherName}`")
                    | concrete -> Some $"`{varName}` would have to be `{Unifier.tyToString (Unifier.apply displayNames concrete)}`")
                |> List.choose id
            if problems.IsEmpty then
                Ok ()
            else
                let details = String.concat "; " problems
                Error [ Diagnostics.at DiagnosticCodes.AnnotationTooGeneral span $"Annotation of '{name}' is more general than the inferred type: {details}" ]

/// Infers a module in `initialEnv`, carrying on past an item that fails to check: the types and items of those
/// that do, and the diagnostics of those that do not. The constraints are not solved here, so a type mismatch is
/// not among them.
let inferModuleWithEnv (initialEnv: TypeEnv) (initialState: InferState) (module': Module) : Map<string, Ty> * TypedTopLevelItem list * InferState * Diagnostic list =
    let mutable env = initialEnv
    let mutable state = initialState
    // Each def's annotation has type variables of its own: `b` in one def's type is not `b` in another's, so a def
    // whose body fails to fit its annotation cannot solve another def's variables (#453).
    let beginDef () = state <- { state with TypeVars = initialState.TypeVars }
    let mutable currentModule: string option = None
    let mutable types = Map.empty
    let mutable items: TypedTopLevelItem list = []
    let mutable errors: Diagnostic list = []

    // An imported context whose every member is also an imported definition of that name is an ambient instance:
    // the one the runtime supplies, as `nanyx/console`'s definitions are its `Console`. The top level of the module
    // runs in it; a def runs in it only if it declares the context, or does not write its type and so infers it.
    state <-
        { state with
            AmbientMembers =
                state.TypeDefs
                |> Map.toList
                |> List.collect (fun (ctxName, info) ->
                    // An aliased import's context (`c.Console`) has its members under the alias too (`c.println`).
                    let qualify memberName =
                        if ctxName.Contains '.' then $"{ctxName.Substring(0, ctxName.LastIndexOf '.')}.{memberName}" else memberName
                    let bindings =
                        info.Members
                        |> Map.toList
                        |> List.map (fun (memberName, memberSymbol) ->
                            let memberName = qualify memberName
                            memberName,
                            TypeEnv.tryFind memberName env
                            |> Option.bind (fun scheme -> scheme.Symbol)
                            |> Option.filter (fun symbol -> symbol <> memberSymbol))
                    if bindings.IsEmpty || not info.Parameters.IsEmpty || bindings |> List.exists (snd >> Option.isNone) then []
                    else bindings |> List.map (fun (memberName, symbol) -> memberName, (symbol.Value, ctxName)))
                |> Map.ofList }

    // Pre-bind every top-level value before inferring any body, so a definition can refer to one
    // declared later in the module (forward references, mutual recursion). An annotated def is bound
    // at its declared type; an unannotated one at a fresh variable that its inferred type is unified
    // with once its body has been checked. Type definitions are registered in the same pass, so an
    // annotation can name a type declared further down.
    // A block the file imports (`import money`) brings its exports in under other names: each is bound wherever the
    // definition it stands for is, so it has the same type and refers to the same symbol.
    let blockImported = blockImports state.ModuleBlocks module'
    let aliases = blockImported |> List.collect (blockAliases state.ModuleBlocks module') |> Map.ofList
    state <- { state with BlockImports = blockImported; BlockAliases = aliases }
    let aliasesOf =
        aliases |> Map.toList |> List.groupBy snd |> List.map (fun (qualified, pairs) -> qualified, List.map fst pairs) |> Map.ofList
    let declareTopLevel (name: string) symbol scheme (env: TypeEnv) =
        aliasesOf
        |> Map.tryFind name
        |> Option.defaultValue []
        |> List.fold (fun acc alias -> TypeEnv.declare alias symbol scheme acc) (TypeEnv.declare name symbol scheme env)
    let withoutTopLevel (name: string) (env: TypeEnv) =
        aliasesOf |> Map.tryFind name |> Option.defaultValue [] |> List.fold (fun acc alias -> acc |> Map.remove alias) (env |> Map.remove name)

    let mutable preboundFreshVars: Map<string, Ty> = Map.empty
    for item in module' do
        match item with
        | Kind (Def (TypeDef(name, modifiers, parameters, body))) ->
            state <- registerTypeDef state name modifiers parameters body
        | Kind (Def (ValueDef(isExport, name, typeOpt, value))) ->
            beginDef ()
            let symbol, declared = declareSymbol DefValue value.Id name TopLevelValue isExport None state
            // The module's own def wins over an imported one of the same name, and requires only its own contexts.
            state <- { declared with ContextRequirements = declared.ContextRequirements |> Map.remove name }
            match typeOpt, value.Kind, symbol with
            | None, Lambda _, Some symbol -> state <- { state with UnannotatedDefs = state.UnannotatedDefs |> Map.add name symbol }
            | _ -> ()
            let _, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
            match declaredTypeExprOpt with
            // An annotation with a `typeof` can only be read once the values it names are in scope, so the def is
            // pre-bound as an unannotated one is, and its annotation is unified with that once it has been read. So is
            // one with a `_`, which generalised here would stand for any type, as a named variable does.
            | Some declaredExpr when (typeOfValues declaredExpr).IsEmpty && not (TypeHoles.hasHoles declaredExpr) ->
                let ty, st = typeExprToTy state declaredExpr
                state <- st
                env <- declareTopLevel name symbol (TypeEnv.generalize env ty) env
            | _ ->
                let ty, st = freshVar state
                state <- st
                preboundFreshVars <- preboundFreshVars |> Map.add name ty
                env <- declareTopLevel name symbol (TypeEnv.mono ty) env
        | _ -> ()

    // The exported types of an imported block can be written under the import's names too (`$Money`, `m.$Money`).
    state <-
        { state with
            TypeDefs =
                aliases
                |> Map.fold (fun acc alias qualified ->
                    match state.TypeDefs |> Map.tryFind qualified with
                    | Some info -> acc |> Map.add alias info
                    | None -> acc) state.TypeDefs }

    // A def that fails to check is reported, and bound at the error type from then on, so its uses are not errors too:
    // its annotation may be what failed.
    //
    // It stays in the module, failed as a whole, so a debug build can defer it: a function's body traps, so calls to
    // it still compile, and a value traps where it is computed. It keeps no annotation.
    let abandon isExport (name: string) (expr: Expression) (diagnostics: Diagnostic list) =
        let failedNode =
            match expr.Kind with
            | Lambda(_, body) -> body
            | _ -> expr
        state <- { state with FailedExprs = (failedNode.Id, diagnostics) :: state.FailedExprs }
        types <- types |> Map.add name Unifier.errorType
        items <- items @ [ TypedDef (TypedValueDef(isExport, name, None, mkTypedExpr expr Unifier.errorType None None None)) ]
        match TypeEnv.tryFind name env with
        | Some scheme -> env <- declareTopLevel name scheme.Symbol (TypeEnv.mono Unifier.errorType) env
        | None -> ()

    for item in module' do
        match item.Kind with
        | Def (ValueDef(isExport, name, typeOpt, expr)) ->
            beginDef ()
            let contextsExist =
                match fst (extractContextFromTypeExpr typeOpt) with
                | Some contexts -> ensureContextsExist state name (declarationSpan state DefValue expr) contexts
                | None -> Ok ()
            // A def's own name is not in scope in its annotation: `def x: typeof x` says nothing.
            let typeOfsInferred =
                match typeOpt with
                | Some typeExpr -> inferTypeOfValues (withoutTopLevel name env) state typeExpr
                | None -> Ok state
            match ensureAttachedTypeExists state currentModule name (declarationSpan state DefValue expr) |> Result.bind (fun () -> ensureTypesExist state (Option.toList typeOpt)) |> Result.bind (fun () -> contextsExist) |> Result.bind (fun () -> typeOfsInferred) with
            | Error err -> abandon isExport name expr err
            | Ok inferredState ->
                state <- inferredState
                let contextOpt, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
                let envWithCtx, nextState =
                    match contextOpt with
                    | Some contexts -> extendEnvWithContexts env state contexts ||> fun env state -> bindInstances env state contexts
                    | None -> env, state
                let expectedInputOpt, expectedReturnOpt, expectedDeclaredOpt, nextState2 =
                    match declaredTypeExprOpt with
                    | Some declaredExpr ->
                        let ty, st = typeExprToTy nextState declaredExpr
                        match Unifier.unalias ty with
                        | TyFunc(inputTy, returnTy) -> TypeHoles.expectedLambdaInput declaredExpr (Some inputTy), Some returnTy, Some ty, st
                        | _ -> None, None, Some ty, st
                    | None -> None, None, None, nextState
                let effectiveExpectedInputOpt =
                    match expectedInputOpt with
                    | Some _ -> expectedInputOpt
                    | None when expectedDeclaredOpt.IsNone -> tryInferAttachedLambdaInput nextState2 name expr
                    | None -> None
                // The name is already in `env` from the pre-binding pass: at its declared type, or at the
                // fresh variable recorded in `preboundFreshVars`. Self-recursion sees the declared type as a
                // monomorphic binding while the body is checked, as before.
                // The symbol the pre-binding pass made.
                let symbol, _ = declareSymbol DefValue expr.Id name TopLevelValue isExport None state
                let envWithRec =
                    match expectedDeclaredOpt with
                    | Some declaredTy -> TypeEnv.declare name symbol (TypeEnv.mono declaredTy) envWithCtx
                    | None -> envWithCtx
                let nextStateWithReq =
                    match contextOpt with
                    | Some contexts -> registerContextRequirement nextState2 name contexts declaredTypeExprOpt
                    | None -> nextState2
                // A function runs in the contexts its type declares, or infers them. A value is computed at the top
                // level, in the ambient ones.
                let bodyState =
                    match expr.Kind with
                    | Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, typeOpt.IsNone) }
                    | _ -> nextStateWithReq
                let inferResult =
                    match expr.Kind with
                    | Lambda(args, body) when effectiveExpectedInputOpt.IsSome || expectedReturnOpt.IsSome ->
                        inferLambdaWithExpected envWithRec bodyState expr args body effectiveExpectedInputOpt expectedReturnOpt
                    | _ -> inferExpr envWithRec bodyState expr
                    |> Result.map (fun (typedExpr, next) -> typedExpr, { next with ContextScope = AmbientScope })
                match inferResult with
                | Ok (typedExpr, next) ->
                    let declaredTy, finalState =
                        match expectedDeclaredOpt with
                        | Some declaredTy ->
                            match expr.Kind with
                            | Lambda _ -> declaredTy, TypeHoles.constrainLambda expr.Span declaredTypeExprOpt declaredTy typedExpr.Type next
                            | _ ->
                                let updatedState =
                                    match Unifier.unalias declaredTy with
                                    | TyNominal(name, underlying, _) when inNominalHome state name expr.Span ->
                                        match Unifier.unalias typedExpr.Type with
                                        | TyNominal _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                        | _ -> addAssignableConstraintAt expr.Span typedExpr.Type underlying next
                                    | _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                declaredTy, updatedState
                        | None ->
                            let updatedState =
                                match preboundFreshVars |> Map.tryFind name with
                                | Some preboundTy -> addConstraintAt expr.Span preboundTy typedExpr.Type next
                                | None -> next
                            typedExpr.Type, updatedState
                    // An annotated def pre-bound at a fresh variable (one with a `typeof`) is that annotation.
                    let finalState =
                        match expectedDeclaredOpt, preboundFreshVars |> Map.tryFind name with
                        | Some _, Some preboundTy -> addConstraintAt expr.Span preboundTy declaredTy finalState
                        | _ -> finalState
                    state <-
                        finalState
                        |> recordSymbolType symbol declaredTy
                        |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                    let rigidResult =
                        match declaredTypeExprOpt with
                        | Some declaredExpr -> checkAnnotationIsRigid finalState name (declarationSpan finalState DefValue expr) declaredExpr
                        | None -> Ok ()
                    match rigidResult with
                    | Error err -> abandon isExport name expr err
                    | Ok () ->
                        // Generalize against an env without this def's own pre-binding, so the fresh
                        // variable it was pre-bound at does not stop its type being generalized. It is resolved
                        // first, as a local def's is: `{ x -> x + 1 }` is `α -> α` until its constraints are
                        // applied, and generalising that would make every use `α -> α` too.
                        let scheme, generalizedState = generalizeDef (withoutTopLevel name env) state declaredTy
                        // The parameterised contexts an unannotated def requires are part of its type, as if written.
                        match resolvePendingInstances env generalizedState name symbol scheme with
                        | Error err ->
                            state <- generalizedState
                            abandon isExport name expr err
                        | Ok (inferredAnnotation, resolvedState) ->
                            state <- resolvedState
                            env <- declareTopLevel name symbol scheme env
                            types <- types |> Map.add name declaredTy
                            items <- items @ [ TypedDef (TypedValueDef(isExport, name, Option.orElse inferredAnnotation typeOpt, typedExpr)) ]
                | Error err -> abandon isExport name expr err
        // A block is a module, not a program, so it has no top-level code.
        | Expr expr when (blockAround state.ModuleBlocks expr.Span).IsSome ->
            let blockName = fst (blockAround state.ModuleBlocks expr.Span).Value
            errors <- errors @ [ Diagnostics.at DiagnosticCodes.CodeInModuleBlock expr.Span $"Module '{blockName}' is a block, which can only hold definitions, types and imports, not code to run" ]
        | Expr expr ->
            match inferExpr env state expr with
            | Ok (typedExpr, next) ->
                state <- next
                items <- items @ [ TypedExprItem typedExpr ]
            | Error err -> errors <- errors @ err
        | Import itemsList -> items <- items @ [ TypedImport itemsList ]
        | ModuleDecl name ->
            currentModule <- Some name
            items <- items @ [ TypedModuleDecl name ]
        | ModuleBlock _ -> ()
        // Attributes are resolved by the compiler front end before inference (see Compiler.applyAttributes).
        | Attribute _ -> ()
        | Def (TypeDef(name, modifiers, parameters, body)) ->
            // Registered in the pre-binding pass above.
            match typeOfInTypeDef name body parameters @ TypeHoles.inTypeDef name body parameters with
            | [] -> errors <- errors @ unknownTypesInTypeDef state body parameters
            | typeOfErrors -> errors <- errors @ typeOfErrors
            items <- items @ [ TypedDef (TypedTypeDef(name, modifiers, parameters, body)) ]

    // The holes left to the end of the module (see `InterpolationHoles.resolveLater`), now that every type is known.
    match resolvePendingInstances env state InterpolationHoles.moduleEnd None (TypeEnv.mono (TyPrimitive "unit")) with
    | Ok (_, resolved) -> state <- resolved
    | Error err -> errors <- errors @ err
    state <- { state with HoleScopes = Map.empty }

    do
        // The ambient contexts each unannotated function needs: those its body uses, and those the unannotated
        // functions it uses need that nothing nearer supplies, until no more are found.
        let uses = List.rev state.DeferredContextUses
        let mutable required =
            state.UnannotatedDefs |> Map.map (fun name _ -> state.InferredContexts |> Map.tryFind name |> Option.defaultValue Set.empty)
        let mutable changed = true
        while changed do
            changed <- false
            for used in uses do
                match used.Caller with
                | DefScope(caller, true) when required.ContainsKey caller ->
                    let needed = Set.difference required.[used.Callee] used.Available
                    if not (Set.isSubset needed required.[caller]) then
                        required <- required |> Map.add caller (Set.union required.[caller] needed)
                        changed <- true
                | _ -> ()
        for used in uses do
            match used.Caller with
            | DefScope(caller, false) ->
                for ctxName in Set.difference required.[used.Callee] used.Available do
                    errors <- errors @ [ undeclaredContext caller ctxName $"'{used.Callee}' requires" used.Span ]
            | _ -> ()
        // What an unannotated function needs is part of its type, as a context written in its type is. They come after
        // the parameterised contexts it requires, which its calls have already found instances of in that order.
        state <-
            { state with
                ContextRequirements =
                    required
                    |> Map.filter (fun _ contexts -> not contexts.IsEmpty)
                    |> Map.fold (fun acc name contexts ->
                        let parameterized = acc |> Map.tryFind name |> Option.defaultValue []
                        acc |> Map.add name (parameterized @ (contexts |> Set.toList |> List.map (TypeName >> TypeExpr.synthetic)))) state.ContextRequirements }

    // The defs that failed and the expressions that failed inside the others, in the order they are in the source. An
    // expression inferred more than once (a lambda checked again against its expected type) is reported once.
    let failedExprErrors = state.FailedExprs @ state.UnhandledMatches |> List.rev |> List.collect snd
    let allErrors =
        errors @ failedExprErrors
        |> List.distinct
        |> List.sortBy (fun diag -> diag.Span |> Option.map (fun span -> span.Start) |> Option.defaultValue System.Int32.MaxValue)
    types, items, state, allErrors

/// Infers a module, carrying on past an item that fails to check: the types and items of those that do, and the
/// diagnostics of those that do not. The constraints are not solved here, so a type mismatch is not among them.
let inferModule (module': Module) : Map<string, Ty> * TypedTopLevelItem list * InferState * Diagnostic list =
    inferModuleWithEnv TypeEnv.empty emptyState module'
