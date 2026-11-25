/// How code generation (see `CodeGen`) finds the contexts a definition requires: the members of each context in the
/// order they are stored, the keys of the members each definition requires, and where a call finds each of them.
module internal Transpiler.Wasm.CodeGenContexts

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers

/// The contexts the def whose annotation is `typeExpr` and whose value is `value` requires (see
/// `Contexts.extractDefContexts`).
let extractContextTypes (typeExpr: TypeExpr option) (value: Expression) : TypeExpr list =
    fst (Contexts.extractDefContexts typeExpr value) |> Option.defaultValue []

/// The type the def whose annotation is `typeExpr` and whose value is `value` declares, without the contexts it
/// requires (see `Contexts.extractDefContexts`).
let unwrapContextType (typeExpr: TypeExpr option) (value: Expression) : TypeExpr option =
    snd (Contexts.extractDefContexts typeExpr value)

/// The members of the context `name` in `typeLayouts`, in the order they are stored. A context an import names under its
/// alias (`c.Console`) has the layout of its own name, which the type definition the import brings in keeps.
let layoutOf (typeLayouts: Map<string, string list>) (name: string) : string list option =
    match typeLayouts |> Map.tryFind name with
    | Some layout -> Some layout
    | None when name.Contains '.' -> typeLayouts |> Map.tryFind (name.Substring(name.LastIndexOf '.' + 1))
    | None -> None

/// The keys of the members of the contexts each definition in `defs` requires, by the definition's name, from the
/// members of each context in `typeLayouts`. For a parameterised context the key is the instance's member (`toString`
/// of `ToString(a)`), and for a plain one it is the member's name (`println`).
let contextMemberKeys (defs: (string * TypeExpr option * Expression) list) (typeLayouts: Map<string, string list>) : Map<string, ContextMemberKey list> =
    defs
    |> List.choose (fun (name, typeOpt, value) ->
        let keys =
            extractContextTypes typeOpt value
            |> List.collect (fun ctxType ->
                let baseName =
                    match ctxType.Kind with
                    | TypeName n
                    | TypeApply(n, _) -> n
                    | _ -> "unknown"
                layoutOf typeLayouts baseName |> Option.defaultValue [] |> List.map (contextMemberKey (Some ctxType)))
        if keys.IsEmpty then None else Some (name, keys))
    |> Map.ofList

/// Where a call finds the member `memberName` of a parameterised context that the type checker found as `instance`
/// (see `TypedModule.ContextInstances`), given the keys of the members in scope there (`scopeKeys`), and the key of the
/// member in scope it is, if it is one: none for an instance found any other way.
let foundMember
    (functionWrappers: Map<string, string>)
    (scopeKeys: Set<ContextMemberKey>)
    (memberName: string)
    (instance: ContextInstance)
    : (Choice<ContextMemberKey, CoreIrLowerInput.ContextArgument>) option =
    match instance with
    // An instance in scope, as written where it is: the members of a `use` or of the context a function's type
    // declares. A `use` of a record value, which the type checker found to be the instance by its shape, binds its
    // members by their names alone.
    | InstanceInScope(written, _) ->
        let writtenKey = contextMemberKey (Some written) memberName
        Some (Choice1Of2 (if not (scopeKeys.Contains writtenKey) && scopeKeys.Contains (NamedMember memberName) then NamedMember memberName else writtenKey))
    // An instance built from the attached definitions: each member is one of them, a function passed as a closure over
    // its wrapper, or a value passed as it is.
    | InstanceSynthesized members ->
        let definition = members |> List.find (fun (n, _) -> n = memberName) |> snd
        match functionWrappers |> Map.tryFind definition with
        | Some wrapper -> Some (Choice2Of2 (CoreIrLowerInput.ClosureOverWrapper wrapper))
        | None -> Some (Choice2Of2 (CoreIrLowerInput.DefinitionValue definition))
    // An instance derived from the parts of a type with variables in it: a closure over its definition and the
    // instances the definition requires.
    | InstanceDerived(_, definition, at) ->
        match functionWrappers |> Map.tryFind definition with
        | Some wrapper -> Some (Choice2Of2 (CoreIrLowerInput.DerivedClosure(wrapper, at)))
        | None -> CoreIr.internalError $"the derived definition '{definition}' has no wrapper"
    | InstanceBuiltIn -> None

/// Where a call to `name`, the call at node `callId`, finds each member of the contexts the callee requires, in the
/// order it requires them, given the keys of the members in scope there (`scopeKeys`). Each is found where the type
/// checker found its context: an instance in scope (a `use`, or a context the caller's type declares), or one built
/// from attached definitions. One the type checker left to be found by name is the member of that key in scope, or a
/// compatible one (the same context and member, at other type arguments), or else the ambient instance's member, or else
/// none (`Unfound`). One of the keys in `passed` is not found: the callers of the function's value pass it.
let contextArguments
    (requirements: Map<string, ContextMemberKey list>)
    (contextTypes: Map<string, TypeExpr list>)
    (typeLayouts: Map<string, string list>)
    (contextInstances: Map<NodeId, ContextInstance option list>)
    (functionWrappers: Map<string, string>)
    (ambientMembers: Map<ContextMemberKey, string>)
    (scopeKeys: Set<ContextMemberKey>)
    (passed: Set<ContextMemberKey>)
    (name: string)
    (callId: NodeId)
    : CoreIrLowerInput.ContextArgument list =
    let required = requirements |> Map.tryFind name |> Option.defaultValue []

    // A member of the same context at other type arguments, not already passed: a required `toString` of
    // `ToString(a)` can be an available one of `ToString(int)`. The first by its key's text is taken.
    let findCompatible (used: Set<ContextMemberKey>) (requiredKey: ContextMemberKey) : ContextMemberKey option =
        scopeKeys
        |> Seq.sortBy ContextMemberKey.text
        |> Seq.tryFind (fun availableKey -> not (used.Contains availableKey) && ContextMemberKey.sameMember requiredKey availableKey)

    // The member in scope, else the ambient instance's, else none (see `ContextArgument.Unfound`).
    let memberFor (requiredKey: ContextMemberKey) (resolvedKey: ContextMemberKey) =
        if scopeKeys.Contains resolvedKey then CoreIrLowerInput.MemberInScope resolvedKey
        else
            match ambientMembers |> Map.tryFind resolvedKey with
            | Some wrapper -> CoreIrLowerInput.ClosureOverWrapper wrapper
            | None -> CoreIrLowerInput.Unfound requiredKey

    // Where the type checker found each context the callee requires, as each required member's instance, in the order
    // of `required`: none for a call it found nothing for. A function that does not write its type may also require
    // ambient contexts, after the parameterised ones its type is given (see `ContextInference`): the call has found no
    // instance of those, which are found by their members' names.
    let instances =
        match contextInstances |> Map.tryFind callId, contextTypes |> Map.tryFind name with
        | Some found, Some types ->
            let found = if found.Length >= types.Length then List.truncate types.Length found else found @ List.replicate (types.Length - found.Length) None
            List.zip types found
            |> List.collect (fun (contextType, instance) ->
                let baseName = match contextType.Kind with TypeName n | TypeApply(n, _) -> n | _ -> "unknown"
                layoutOf typeLayouts baseName |> Option.defaultValue [] |> List.map (fun memberName -> memberName, instance))
            |> fun (perMember: (string * ContextInstance option) list) -> if perMember.Length = required.Length then Some perMember else None
        | _ -> None

    required
    |> List.mapi (fun index requiredKey -> requiredKey, instances |> Option.map (fun perMember -> perMember.[index]))
    |> List.fold (fun (acc: CoreIrLowerInput.ContextArgument list, used: Set<ContextMemberKey>) (requiredKey, instance) ->
        // A member its value's callers pass.
        if passed.Contains requiredKey then acc @ [ CoreIrLowerInput.PassedByCaller ], used
        else
        match instance |> Option.bind (fun (memberName, found) -> found |> Option.bind (foundMember functionWrappers scopeKeys memberName)) with
        | Some (Choice1Of2 key) -> acc @ [ memberFor requiredKey key ], used.Add key
        | Some (Choice2Of2 argument) -> acc @ [ argument ], used
        | None ->
            let resolvedKey =
                if scopeKeys.Contains requiredKey then requiredKey
                else findCompatible used requiredKey |> Option.defaultValue requiredKey
            acc @ [ memberFor requiredKey resolvedKey ], used.Add resolvedKey) ([], Set.empty)
    |> fst

let extractTypeLayouts (module': Module) : Map<string, string list> * Map<string, Map<string, string>> =
    let rec typeExprToTypeString (t: TypeExpr) : string =
        match t.Kind with
        | TypeName name -> name
        | TypeFunc(param, ret) -> $"({typeExprToTypeString param}) -> ({typeExprToTypeString ret})"
        | TypeUnit -> "()"
        | TypeTuple items -> items |> List.map typeExprToTypeString |> String.concat ", " |> sprintf "(%s)"
        | _ -> "unknown"
    let results =
        SyntaxTree.typeDefinitions module'
        |> List.choose (function
            | TypeDef(name, _, _, Kind (TypeRecord fields)) ->
                let fieldInfo =
                    fields
                    |> List.choose (function
                        | TypeField(fieldName, _, typeOpt, _) ->
                            let typeName =
                                match typeOpt with
                                | Some t -> typeExprToTypeString t
                                | _ -> "unknown"
                            Some (fieldName, typeName)
                        | TypeFieldAttribute _ -> None)
                let sortedFieldNames = fieldInfo |> List.map fst |> List.sort
                let fieldTypeMap = fieldInfo |> Map.ofList
                Some (name, sortedFieldNames, fieldTypeMap)
            | _ -> None)
    let layouts = results |> List.map (fun (n, fields, _) -> (n, fields)) |> Map.ofList
    let fieldTypes = results |> List.map (fun (n, _, types) -> (n, types)) |> Map.ofList
    layouts, fieldTypes

/// The members of each context in `typeDefs`, each with its declared type, in the order `typeLayouts` stores them (see
/// `ContextLayouts`): of one without parameters by key, its own type variables boxed
/// (`CoreIrLowerTypes.boxOwnVariables`), and of a parameterised one by name, in its parameters.
let contextLayoutsOf (typeDefs: Map<string, TypeDefInfo>) (typeLayouts: Map<string, string list>) : ContextLayouts =
    let declared (parameterised: bool) =
        typeDefs
        |> Map.filter (fun _ info -> info.IsContext && info.Parameters.IsEmpty <> parameterised)
        |> Map.toList
        |> List.choose (fun (name, info) ->
            match Unifier.unalias info.Underlying, layoutOf typeLayouts name with
            | TyRecord(members, _), Some layout -> Some (name, info, layout |> List.choose (fun memberName -> members |> Map.tryFind memberName |> Option.map (fun ty -> memberName, ty)))
            | _ -> None)
    { Plain =
        declared false
        |> List.map (fun (name, _, members) -> name, members |> List.map (fun (memberName, ty) -> contextMemberKey None memberName, CoreIrLowerTypes.boxOwnVariables ty))
        |> Map.ofList
      Parameterised = declared true |> List.map (fun (name, info, members) -> name, (info.Parameters, members)) |> Map.ofList
      Waiting =
        typeDefs
        |> Map.filter (fun _ info -> info.IsContext && info.Name = "Async" && info.Home = StandardLibraryModule ModuleOrigin.asyncName)
        |> Map.keys
        |> Set.ofSeq }

/// `exprTypes` with the type of each function `module'` names as a value (a closure over its wrapper: `wrapped` gives
/// the function each wrapper calls) requiring the contexts without parameters that the function requires
/// (`contextTypes`), as the value of a function whose type writes them already does (see
/// `ContextVariables.ofNamedValue`): its callers pass their members. A function that does not write its type is named
/// at a type without the ambient contexts it was inferred to require, and this is how they reach it from where it is
/// called, as the host's `use` reaches an `@entry`.
let carryPlainContexts
    (typeDefs: Map<string, TypeDefInfo>)
    (contextTypes: Map<string, TypeExpr list>)
    (wrapped: Map<string, string>)
    (module': Module)
    (exprTypes: Map<NodeId, Ty>)
    : Map<NodeId, Ty> =
    let plainContexts (name: string) =
        contextTypes
        |> Map.tryFind name
        |> Option.defaultValue []
        |> List.choose (fun context ->
            match context.Kind with
            | TypeName contextName when typeDefs |> Map.tryFind contextName |> Option.exists (fun info -> info.IsContext && info.Parameters.IsEmpty) ->
                Some (TyApply(contextName, []))
            | _ -> None)
    module'
    |> List.collect (function
        | Kind (Def (ValueDef(_, _, _, value))) | Kind (Expr value) -> SyntaxTree.expressionsWithin value
        | _ -> [])
    |> List.fold (fun (acc: Map<NodeId, Ty>) expr ->
        match expr.Kind with
        | ClosureSyntax.Closure(target, _) ->
            match wrapped |> Map.tryFind target |> Option.map plainContexts, acc |> Map.tryFind expr.Id with
            | Some (_ :: _ as plain), Some (TyFunc(input, output, own)) -> acc |> Map.add expr.Id (TyFunc(input, output, own @ plain |> List.distinct))
            | _ -> acc
        | _ -> acc) exprTypes

/// Each type an annotation writes, matched to the type in the same place in the type it annotates: the type
/// variables it names, as inference may have merged a written variable with another and dropped its name, and
/// the types a context's arguments write that are neither variables nor primitives (`Word`, `list(int)`).
let rec matchAnnotation (annotation: TypeExpr) (ty: Ty) : (TypeExpr * Ty) list =
    let within =
        match annotation.Kind, Unifier.unalias ty with
        | TypeFunc(input, output), TyFunc(input', output', _) -> matchAnnotation input input' @ matchAnnotation output output'
        // A function type written with the contexts it requires, `[c] a -> b`.
        | TypeWithContext(_, inner), _ -> matchAnnotation inner ty
        | TypeTuple items, TyRecord(fields, _) ->
            items |> List.mapi (fun index item -> fields |> Map.tryFind (string index) |> Option.map (matchAnnotation item) |> Option.defaultValue []) |> List.concat
        | TypeApply(applied, items), TyApply(applied', items') when applied = applied' && items.Length = items'.Length ->
            List.map2 matchAnnotation items items' |> List.concat
        | TypeRecord written, TyRecord(fields, _) ->
            written
            |> List.collect (function
                | TypeField(fieldName, _, Some fieldType, _) ->
                    fields |> Map.tryFind fieldName |> Option.map (matchAnnotation fieldType) |> Option.defaultValue []
                | _ -> [])
        // A tag's payload, and each tag of a union by its name, as the union's tags need not be in the same order.
        | TypeTag(tag, Some payload), TyTag(tag', Some payload') when tag = tag' -> matchAnnotation payload payload'
        | TypeUnion written, TyUnion items ->
            written
            |> List.collect (fun item ->
                match item.Kind with
                | TypeTag(tag, Some _) ->
                    items
                    |> List.tryFind (function TyTag(tag', Some _) -> tag = tag' | _ -> false)
                    |> Option.map (matchAnnotation item)
                    |> Option.defaultValue []
                | _ -> [])
        | _ -> []
    (annotation, ty) :: within

/// The type `argument`, written in an annotation as a context's argument, stands for: a primitive, a type variable by
/// its name (in `named`, or where the annotation writes it, in `annotated`, see `matchAnnotation`), or another type
/// where the annotation writes it in place of one (`[ToText(Word)] Word -> int`).
let writtenArgumentType (named: (string * Ty) list) (annotated: (TypeExpr * Ty) list) (argument: TypeExpr) : Ty option =
    let annotatedVars =
        annotated
        |> List.choose (fun (written, ty) ->
            match written.Kind, Unifier.unalias ty with
            | (TypeName varName | TypeVar varName), TyVar _ -> Some (varName, ty)
            | _ -> None)
    let vars = named @ annotatedVars |> Map.ofList
    let writtenTy (argument: TypeExpr) =
        annotated |> List.tryFind (fun (written, _) -> written = argument) |> Option.map snd
    match argument.Kind with
    | TypeName ("int" | "string" | "bool" | "unit" | "float" as primitive) -> Some (TyPrimitive primitive)
    | TypeName varName ->
        vars
        |> Map.tryFind varName
        |> Option.orElse (if SizedNumber.isName varName || varName = Chars.name then Some (TyPrimitive varName) else None)
        |> Option.orElse (writtenTy argument)
    | TypeVar varName -> vars |> Map.tryFind varName
    // A tag, or a union of them, as an associated type is fixed with: `FromString(a, failure = #invalidInt)`.
    | TypeTag(tag, None) -> Some (TyTag(tag, None))
    | TypeUnion items ->
        let tags = items |> List.choose (fun item -> match item.Kind with TypeTag(tag, None) -> Some (TyTag(tag, None)) | _ -> None)
        if tags.Length = items.Length then Some (TyUnion tags) else writtenTy argument
    | _ -> writtenTy argument

/// Whether `context` is a parameterised context, at some types.
let isInstance (context: Ty) =
    match context with
    | TyApply(_, _ :: _) -> true
    | _ -> false
