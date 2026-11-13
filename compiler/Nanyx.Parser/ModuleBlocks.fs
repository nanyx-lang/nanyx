[<AutoOpen>]
module Parser.Program.ModuleBlocks

open System
open Parser.Program

let private qualifyModuleName (prefix: string) (name: string) =
    if String.IsNullOrWhiteSpace prefix then name else $"{prefix}.{name}"

/// `typeExpr` with the types `typeMap` qualifies renamed, and the value of each `typeof` rewritten by `rewriteValue`.
let private rewriteTypeExprInModule (typeMap: Map<string, string>) (rewriteValue: Expression -> Expression) (typeExpr: TypeExpr) : TypeExpr =
    let rec rewrite (t: TypeExpr) = { t with Kind = rewriteKind t.Kind }
    and rewriteKind kind =
        match kind with
        | TypeName name ->
            match typeMap |> Map.tryFind name with
            | Some qualified -> TypeName qualified
            | None -> TypeName name
        | TypeVar _
        | TypeHole
        | TypeUnit -> kind
        | TypeTuple items -> TypeTuple (items |> List.map rewrite)
        | TypeRecord fields ->
            TypeRecord (
                fields
                |> List.map (function
                    | TypeField(name, isMutable, typeOpt, defaultOpt) -> TypeField(name, isMutable, typeOpt |> Option.map rewrite, defaultOpt)
                    | TypeMember(name, typeOpt) -> TypeMember(name, typeOpt |> Option.map rewrite)))
        | TypeApply(name, args) ->
            let mappedName = typeMap |> Map.tryFind name |> Option.defaultValue name
            TypeApply(mappedName, args |> List.map rewrite)
        | TypeFunc(inputTy, outputTy) -> TypeFunc(rewrite inputTy, rewrite outputTy)
        | TypeTag(name, payload) -> TypeTag(name, payload |> Option.map rewrite)
        | TypeUnion items -> TypeUnion (items |> List.map rewrite)
        | TypeIntersection items -> TypeIntersection (items |> List.map rewrite)
        | TypeContext items -> TypeContext (items |> List.map rewrite)
        | TypeWithContext(items, inner) -> TypeWithContext(items |> List.map rewrite, rewrite inner)
        | TypeConstraint(name, baseTy, predicate) -> TypeConstraint(name, rewrite baseTy, predicate)
        | TypeWhere(baseTy, predicate) -> TypeWhere(rewrite baseTy, predicate)
        | TypeOf value -> TypeOf (rewriteValue value)
    rewrite typeExpr

let private rewriteExpressionInModule (valueMap: Map<string, string>) (typeMap: Map<string, string>) (expr: Expression) : Expression =
    let rewriteQualifiedHead (name: string) =
        if name.Contains(".") then
            let dot = name.IndexOf('.')
            let head = name.Substring(0, dot)
            let tail = name.Substring(dot + 1)
            match typeMap |> Map.tryFind head with
            | Some qualifiedHead -> $"{qualifiedHead}.{tail}"
            | None -> name
        else
            name

    let rewriteName (name: string) =
        match valueMap |> Map.tryFind name with
        | Some qualified -> qualified
        | None ->
            match typeMap |> Map.tryFind name with
            | Some qualifiedType -> qualifiedType
            | None -> rewriteQualifiedHead name

    let rec rewrite (e: Expression) = { e with Kind = rewriteKind e }
    and rewriteKind e =
        match e.Kind with
        | IdentifierExpr name -> IdentifierExpr(rewriteName name)
        | FunctionCall(name, range, args) -> FunctionCall(rewriteName name, range, args |> List.map rewrite)
        | Lambda(args, body, takesUnit) -> Lambda(args, rewrite body, takesUnit)
        | BinaryOp(op, opSpan, left, right) -> BinaryOp(op, opSpan, rewrite left, rewrite right)
        | Pipe(value, funcName, range, args) -> Pipe(rewrite value, rewriteName funcName, range, args |> List.map rewrite)
        | Block statements -> Block (statements |> List.map (fun statement -> { statement with Kind = rewriteStatement statement.Kind }))
        | Match(values, arms) -> Match(values |> List.map rewrite, arms |> List.map (fun (patterns, body) -> patterns |> List.map (Pattern.mapExpressions rewrite), rewrite body))
        | Except(value, arms) -> Except(rewrite value, arms |> List.map (fun (patterns, body) -> patterns |> List.map (Pattern.mapExpressions rewrite), rewrite body))
        | TupleExpr items -> TupleExpr (items |> List.map rewrite)
        | RecordExpr fields ->
            RecordExpr (
                fields
                |> List.map (function
                    | NamedField(name, value) -> NamedField(name, rewrite value)
                    | PositionalField value -> PositionalField (rewrite value)))
        | ListExpr items -> ListExpr (items |> List.map rewrite)
        | TagExpr(name, payload) -> TagExpr(name, payload |> Option.map rewrite)
        | IfExpr(cond, ifTrue, ifFalse) -> IfExpr(rewrite cond, rewrite ifTrue, rewrite ifFalse)
        | MemberAccess(baseExpr, field, range) -> MemberAccess(rewrite baseExpr, field, range)
        | ContextMemberAccess(ctxType, field) -> ContextMemberAccess(rewriteType ctxType, field)
        | ContextMemberCall(ctxType, field, args) -> ContextMemberCall(rewriteType ctxType, field, args |> List.map rewrite)
        | UseIn(value, body) -> UseIn(rewrite value, rewrite body)
        | WorkflowBindExpr(name, value) -> WorkflowBindExpr(name, rewrite value)
        | WorkflowReturnExpr value -> WorkflowReturnExpr (rewrite value)
        | InterpolatedString parts -> InterpolatedString (parts |> List.map (function | StringText text -> StringText text | StringExpr value -> StringExpr (rewrite value)))
        | UnitExpr
        | LiteralExpr _ -> e.Kind
    and rewriteStatement kind =
        match kind with
        | DefStatement(isExport, name, typeOpt, valueExpr) -> DefStatement(isExport, name, typeOpt |> Option.map rewriteType, rewrite valueExpr)
        | TypeDefStatement(name, modifiers, typeParams, typeExpr) ->
            let mappedParams = typeParams |> List.map (fun (paramName, paramTypeOpt) -> paramName, paramTypeOpt |> Option.map rewriteType)
            TypeDefStatement(name, modifiers, mappedParams, rewriteType typeExpr)
        | ImportStatement items -> ImportStatement items
        | UseStatement value -> UseStatement (rewrite value)
        | ExprStatement bodyExpr -> ExprStatement (rewrite bodyExpr)
    and rewriteType typeExpr = rewriteTypeExprInModule typeMap rewrite typeExpr
    rewrite expr

let rec private expandModuleBlock (prefix: string) (items: TopLevelItem list) : TopLevelItem list =
    let localTypeNames =
        items
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, _, _))) when not (name.Contains(".")) -> Some name
            | _ -> None)
        |> Set.ofList

    let typeMap =
        localTypeNames
        |> Seq.map (fun name -> name, qualifyModuleName prefix name)
        |> Map.ofSeq

    let localValueNames =
        items
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, _))) when not (name.Contains(".")) -> Some name
            | _ -> None)
        |> Set.ofList

    let valueMap =
        localValueNames
        |> Seq.map (fun name -> name, qualifyModuleName prefix name)
        |> Map.ofSeq

    let rewriteType = rewriteTypeExprInModule typeMap (rewriteExpressionInModule valueMap typeMap)

    let qualifyDefName (name: string) =
        if name.Contains(".") then
            let dot = name.IndexOf('.')
            let head = name.Substring(0, dot)
            let tail = name.Substring(dot + 1)
            match typeMap |> Map.tryFind head with
            | Some qualifiedHead -> $"{qualifiedHead}.{tail}"
            | None -> qualifyModuleName prefix name
        else
            qualifyModuleName prefix name

    let qualifyTypeName (name: string) =
        if name.Contains(".") then qualifyModuleName prefix name else typeMap |> Map.tryFind name |> Option.defaultValue (qualifyModuleName prefix name)

    items
    |> List.collect (fun item ->
        let keep kind = [ { item with Kind = kind } ]
        match item.Kind with
        | ModuleDecl _ -> []
        | ModuleBlock(name, nested) ->
            // The nested block becomes a module declaration, written where the block was.
            let nestedPrefix = qualifyModuleName prefix name
            TopLevelItem.at item.Span (ModuleDecl nestedPrefix) :: expandModuleBlock nestedPrefix nested
        | Import _
        | ReExport _
        | Attribute _ -> [ item ]
        | Expr expr -> keep (Expr (rewriteExpressionInModule valueMap typeMap expr))
        | Def (ValueDef(isExport, name, typeOpt, expr)) ->
            keep (Def (ValueDef(isExport, qualifyDefName name, typeOpt |> Option.map rewriteType, rewriteExpressionInModule valueMap typeMap expr)))
        | Def (TypeDef(name, modifiers, typeParams, body)) ->
            let mappedTypeParams = typeParams |> List.map (fun (paramName, paramTypeOpt) -> paramName, paramTypeOpt |> Option.map rewriteType)
            keep (Def (TypeDef(qualifyTypeName name, modifiers, mappedTypeParams, rewriteType body))))

let internal expandMidFileModules (items: TopLevelItem list) : TopLevelItem list =
    let rec loop (currentModule: string option) (remaining: TopLevelItem list) (acc: TopLevelItem list) =
        match remaining with
        | [] -> List.rev acc
        | item :: rest ->
            match item.Kind with
            | ModuleDecl name -> loop (Some name) rest (item :: acc)
            | ModuleBlock(name, innerItems) ->
                let prefix =
                    match currentModule with
                    | Some root when not (String.IsNullOrWhiteSpace root) -> qualifyModuleName root name
                    | _ -> name
                let expanded = TopLevelItem.at item.Span (ModuleDecl prefix) :: expandModuleBlock prefix innerItems
                loop currentModule rest ((List.rev expanded) @ acc)
            | _ -> loop currentModule rest (item :: acc)
    loop None items []
