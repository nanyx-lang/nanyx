namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites

/// The capitalised spellings of the built-in types, `Int` for `int` and `List` for `list`, which a definition used to
/// be attached under (`def Int.fromString`) and a type could be written with (`List(int)`). They are not types: the
/// name before the dot must be a real type, spelled as the type is (#653). For one release each is still read as the
/// type it stands for, with a warning and a fix that writes the type's name.
module internal DeprecatedTypeNames =
    /// Each deprecated spelling and the type's name.
    let private aliases =
        Map.ofList
            [ "Int", "int"
              "Float", "float"
              "Bool", "bool"
              "String", "string"
              "Unit", "unit"
              "List", "list"
              "Seq", "seq"
              "Map", Maps.typeName
              "Set", Sets.typeName ]

    /// The warning for `written`, a deprecated spelling of `typeName`, at `span`, with the fix that writes the type's name.
    let private warning (span: SourceSpan) (written: string) (typeName: string) : Diagnostic =
        { Diagnostics.at
            DiagnosticCodes.DeprecatedTypeName
            span
            $"'{written}' is not a type: write '{typeName}', the type's name. '{written}' is read as '{typeName}' for now, but will stop working" with
            Fixes = [ { Title = $"Write `{typeName}`";Edits = [ { Span = span; NewText = typeName } ] } ] }

    /// The part of `span`, where `name` is written, that its `length` characters from `offset` are written at.
    let private partOf (span: SourceSpan) (offset: int) (length: int) : SourceSpan =
        if SourceSpan.isNone span then span
        else { span with Start = span.Start + offset; End = span.Start + offset + length; StartColumn = span.StartColumn + offset }

    /// `module'` with each deprecated spelling of a built-in type replaced by the type's name, and a warning for each:
    /// the type a definition is attached to or a qualified name reaches through (`Int.fromString`, `t.String.trim`),
    /// and a type written in an annotation (`List(int)`). A spelling the module defines as a type of its own, as
    /// `type Set(a) = ...`, is that type and is left as it is. `declared` gives where each definition's name is written.
    let replace (declared: DeclaredNames) (module': Module) : Module * Diagnostic list =
        let warnings = ResizeArray<Diagnostic>()
        let rec ownTypeNames (items: TopLevelItem list) =
            items
            |> List.collect (function
                | Kind (Def (TypeDef(name, _, _, _))) -> [ name ]
                | Kind (ModuleBlock(_, inner)) -> ownTypeNames inner
                | _ -> [])
        let own = Set.ofList (ownTypeNames module')
        let tryAlias (written: string) =
            if own.Contains written then None else aliases |> Map.tryFind written

        // `name` with the type its last part is attached to spelled as the type is; `span` is where `name` is written.
        let qualified (span: SourceSpan) (name: Identifier) : Identifier =
            let parts = name.Split('.')
            if parts.Length < 2 then
                name
            else
                let typeIndex = parts.Length - 2
                match tryAlias parts.[typeIndex] with
                | None -> name
                | Some typeName ->
                    let offset = parts |> Array.take typeIndex |> Array.sumBy (fun part -> part.Length + 1)
                    warnings.Add(warning (partOf span offset parts.[typeIndex].Length) parts.[typeIndex] typeName)
                    parts.[typeIndex] <- typeName
                    String.concat "." parts

        let rec typeExpr (t: TypeExpr) : TypeExpr =
            let renamed (written: string) =
                match tryAlias written with
                | Some typeName ->
                    warnings.Add(warning (partOf t.Span 0 written.Length) written typeName)
                    typeName
                | None -> written
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
                | IdentifierExpr name -> IdentifierExpr(qualified current.Span name)
                | FunctionCall(name, rangeOpt, args) -> FunctionCall(qualified (rangeOpt |> Option.defaultValue current.Span) name, rangeOpt, args)
                | Pipe(value, name, rangeOpt, args) -> Pipe(value, qualified (rangeOpt |> Option.defaultValue SourceSpan.none) name, rangeOpt, args)
                // An attached definition used as a value, `Int.toString`, is read as a member access.
                | MemberAccess({ Kind = IdentifierExpr written } as typeName, memberName, rangeOpt) ->
                    let renamed = qualified typeName.Span $"{written}.{memberName}"
                    MemberAccess({ typeName with Kind = IdentifierExpr(renamed.Substring(0, renamed.LastIndexOf '.')) }, memberName, rangeOpt)
                | Lambda(parameters, body) -> Lambda(parameters |> List.map (fun (name, typeOpt) -> name, typeOpt |> Option.map typeExpr), body)
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
        replaced, warnings |> Seq.sortBy (fun warning -> warning.Span |> Option.map _.Start |> Option.defaultValue -1) |> List.ofSeq
