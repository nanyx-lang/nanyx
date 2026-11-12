namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites

/// The names of the built-in types. `String`, `List`, `Unit`, `Seq`, `Map` and `Set` are the types, and `string`,
/// `list` and the rest are keyword aliases that mean exactly them, before the dot of an attached definition
/// (`def String.trim` is `def string.trim`) as in a type (`List(int)` is `list(int)`). The compiler works with the
/// alias, so it prints `list(string)`: this pass writes each type's name as its alias before inference (#692).
/// `Int`, `Float` and `Bool` are not types, and are an error with a fix that writes `int`, `float` and `bool`.
module internal BuiltinTypeNames =
    /// Each built-in type's name and the alias the compiler works with.
    let private aliases =
        Map.ofList
            [ "String", "string"
              "Unit", "unit"
              "List", "list"
              "Seq", "seq"
              "Map", Maps.typeName
              "Set", Sets.typeName ]

    /// Each spelling that is not a type and the alias to write instead.
    let private notTypes = Map.ofList [ "Int", "int"; "Float", "float"; "Bool", "bool" ]

    /// The error for `written`, which is not a type, at `span`, with the fix that writes `alias`.
    let private error (span: SourceSpan) (written: string) (alias: string) : Diagnostic =
        { Diagnostics.at DiagnosticCodes.NotABuiltInType span $"'{written}' is not a type: write '{alias}'" with
            Fixes = [ { Title = $"Write `{alias}`"; Edits = [ { Span = span; NewText = alias } ] } ] }

    /// The part of `span`, where `name` is written, that its `length` characters from `offset` are written at.
    let private partOf (span: SourceSpan) (offset: int) (length: int) : SourceSpan =
        if SourceSpan.isNone span then span
        else { span with Start = span.Start + offset; End = span.Start + offset + length; StartColumn = span.StartColumn + offset }

    /// `module'` with each built-in type's name written as its alias, and an error for each `Int`, `Float` and `Bool`,
    /// which is written as its alias too, so that it is reported once: the type a definition is attached to or a
    /// qualified name reaches through (`String.trim`, `t.List.map`), a type written in an annotation (`List(int)`),
    /// and `Int`, `Float` or `Bool` named on its own as a value. A name the module defines as a type of its own, as
    /// `type Set(a) = ...`, is that type and is left as it is. `declared` gives where each definition's name is written.
    let replace (declared: DeclaredNames) (module': Module) : Module * Diagnostic list =
        let errors = ResizeArray<Diagnostic>()
        let rec ownTypeNames (items: TopLevelItem list) =
            items
            |> List.collect (function
                | Kind (Def (TypeDef(name, _, _, _))) -> [ name ]
                | Kind (ModuleBlock(_, inner)) -> ownTypeNames inner
                | _ -> [])
        let own = Set.ofList (ownTypeNames module')
        // The alias `written` at `span` stands for, reporting it if it is not a type.
        let tryAlias (span: SourceSpan) (written: string) =
            if own.Contains written then
                None
            else
                match aliases |> Map.tryFind written, notTypes |> Map.tryFind written with
                | Some alias, _ -> Some alias
                | None, Some alias ->
                    errors.Add(error span written alias)
                    Some alias
                | None, None -> None

        // `name` with the type its last part is attached to written as its alias; `span` is where `name` is written.
        let qualified (span: SourceSpan) (name: Identifier) : Identifier =
            let parts = name.Split('.')
            if parts.Length < 2 then
                name
            else
                let typeIndex = parts.Length - 2
                let offset = parts |> Array.take typeIndex |> Array.sumBy (fun part -> part.Length + 1)
                match tryAlias (partOf span offset parts.[typeIndex].Length) parts.[typeIndex] with
                | None -> name
                | Some alias ->
                    parts.[typeIndex] <- alias
                    String.concat "." parts

        // `name`, undotted, reported if it is `Int`, `Float` or `Bool`; it is left as written, since no value has the alias's name.
        let unqualified (span: SourceSpan) (name: Identifier) =
            if not (name.Contains '.') && not (own.Contains name) then
                notTypes |> Map.tryFind name |> Option.iter (fun alias -> errors.Add(error (partOf span 0 name.Length) name alias))

        let rec typeExpr (t: TypeExpr) : TypeExpr =
            let renamed (written: string) = tryAlias (partOf t.Span 0 written.Length) written |> Option.defaultValue written
            let kind =
                match t.Kind with
                | TypeName name -> TypeName(renamed name)
                | TypeVar _
                | TypeHole
                | TypeUnit
                | TypeOf _ -> t.Kind
                | TypeTuple items -> TypeTuple(items |> List.map typeExpr)
                | TypeRecord fields ->
                    TypeRecord(
                        fields
                        |> List.map (function
                            | TypeField(name, isMutable, isOptional, typeOpt, defaultOpt) -> TypeField(name, isMutable, isOptional, typeOpt |> Option.map typeExpr, defaultOpt)
                            | TypeMember(name, typeOpt) -> TypeMember(name, typeOpt |> Option.map typeExpr)))
                | TypeApply(name, args) -> TypeApply(renamed name, args |> List.map typeExpr)
                | TypeFunc(input, output) -> TypeFunc(typeExpr input, typeExpr output)
                | TypeTag(name, payload) -> TypeTag(name, payload |> Option.map typeExpr)
                | TypeUnion items -> TypeUnion(items |> List.map typeExpr)
                | TypeIntersection items -> TypeIntersection(items |> List.map typeExpr)
                | TypeOptional inner -> TypeOptional(typeExpr inner)
                | TypeContext items -> TypeContext(items |> List.map typeExpr)
                | TypeWithContext(items, inner) -> TypeWithContext(items |> List.map typeExpr, typeExpr inner)
                | TypeConstraint(name, baseTy, predicate) -> TypeConstraint(name, typeExpr baseTy, predicate)
                | TypeWhere(baseTy, predicate) -> TypeWhere(typeExpr baseTy, predicate)
            { t with Kind = kind }

        let typeParameters = List.map (fun (name, constraintOpt) -> name, constraintOpt |> Option.map typeExpr)

        // A def's name is written where the parser declared it, by the node of its value.
        let defName (value: Expression) (name: Identifier) =
            let span = declared |> Map.tryFind (value.Id, DefValue) |> Option.bind List.tryHead |> Option.map snd |> Option.defaultValue SourceSpan.none
            qualified span name

        let expression =
            mapExpressionKinds (fun current kind ->
                match kind with
                | IdentifierExpr name ->
                    unqualified current.Span name
                    IdentifierExpr(qualified current.Span name)
                | FunctionCall(name, rangeOpt, args) ->
                    let span = rangeOpt |> Option.defaultValue current.Span
                    unqualified span name
                    FunctionCall(qualified span name, rangeOpt, args)
                | Pipe(value, name, rangeOpt, args) -> Pipe(value, qualified (rangeOpt |> Option.defaultValue SourceSpan.none) name, rangeOpt, args)
                // An attached definition used as a value, `List.map`, is read as a member access.
                | MemberAccess({ Kind = IdentifierExpr written } as typeName, memberName, rangeOpt) ->
                    let renamed = qualified typeName.Span $"{written}.{memberName}"
                    MemberAccess({ typeName with Kind = IdentifierExpr(renamed.Substring(0, renamed.LastIndexOf '.')) }, memberName, rangeOpt)
                | Lambda(parameters, body, takesUnit) -> Lambda(parameters |> List.map (fun (name, typeOpt) -> name, typeOpt |> Option.map typeExpr), body, takesUnit)
                | ContextMemberAccess(ctxType, memberName) -> ContextMemberAccess(typeExpr ctxType, memberName)
                | ContextMemberCall(ctxType, memberName, args) -> ContextMemberCall(typeExpr ctxType, memberName, args)
                | Block statements ->
                    Block(
                        statements
                        |> List.map (fun statement ->
                            match statement.Kind with
                            | DefStatement(isExport, name, typeOpt, value) ->
                                { statement with Kind = DefStatement(isExport, defName value name, typeOpt |> Option.map typeExpr, value) }
                            | TypeDefStatement(name, modifiers, parameters, body) ->
                                { statement with Kind = TypeDefStatement(name, modifiers, typeParameters parameters, typeExpr body) }
                            | ImportStatement _
                            | UseStatement _
                            | ExprStatement _ -> statement))
                | other -> other)

        let rec item (topLevel: TopLevelItem) : TopLevelItem =
            match topLevel.Kind with
            | Def (ValueDef(isExport, name, typeOpt, value)) ->
                { topLevel with Kind = Def (ValueDef(isExport, defName value name, typeOpt |> Option.map typeExpr, expression value)) }
            | Def (TypeDef(name, modifiers, parameters, body)) ->
                { topLevel with Kind = Def (TypeDef(name, modifiers, typeParameters parameters, typeExpr body)) }
            | Expr value -> { topLevel with Kind = Expr (expression value) }
            | ModuleBlock(name, items) -> { topLevel with Kind = ModuleBlock(name, items |> List.map item) }
            | ModuleDecl _
            | Import _
            | Attribute _ -> topLevel

        let replaced = module' |> List.map item
        // `Int.toString` is reported both as the member access and as the identifier it reads.
        replaced, errors |> Seq.distinctBy _.Span |> Seq.sortBy (fun error -> error.Span |> Option.map _.Start |> Option.defaultValue -1) |> List.ofSeq
