namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites

/// Associated types: a context's members of kind `type`, as `elem` is in
/// `context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)`. The compiler reads each as a last parameter of
/// the context that a signature doesn't write. This pass writes the module that way before inference:
///
/// - the context's declaration takes its associated types as parameters after its own, `Iter(c, elem)`, and its members
///   name each as that parameter;
/// - a context a signature requires is written with an argument for each, `[Iter(c)]` as `[Iter(c, elem)]`, a type
///   variable of the signature named as the associated type, or `Iter(c).elem` where another context in scope has one of
///   that name too. `Iter(c, elem = string)` fixes it: `Iter(c, string)`;
/// - `elem` or `Iter(c).elem` written in the signature is that argument, in the contexts' own arguments too;
/// - a context written as a type, as a `use Iter(Bag)(...)` writes it, is given a `_` for each one its members settle.
///
/// Inference then needs nothing new: an instance's associated type is an argument of the context that the members'
/// types settle, as `int.fromString` settles the failure of a `FromString(int)` (see `ContextInstances`).
module internal AssociatedTypes =
    /// A context with associated types: how many parameters a signature writes for it, its associated types' names in
    /// order, and the names of its other members.
    type private Shape =
        { Written: int
          Associated: string list
          Members: Set<string> }

    /// A context a signature requires, where its associated types can be named: its name, the arguments it is written
    /// with, and what each of its associated types stands for.
    type private InScope =
        { Context: Identifier
          Arguments: TypeExpr list
          Types: (string * TypeExpr) list }

    /// The context `ctx` names and the arguments it is written with.
    let private contextOf (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName name -> Some (name, [])
        | TypeApply(name, args) -> Some (name, args)
        | _ -> None

    let private isNamed (arg: TypeExpr) =
        match arg.Kind with
        | TypeNamedArgument _ -> true
        | _ -> false

    /// `t` as a message writes it, for a type variable's name: a name and the arguments it is applied to.
    let rec private text (t: TypeExpr) : string =
        match t.Kind with
        | TypeName name
        | TypeVar name -> name
        | TypeApply(name, args) -> $"""{name}({args |> List.map text |> String.concat ", "})"""
        | TypeNamedArgument(name, value) -> $"{name} = {text value}"
        | TypeAssociated(context, name) -> $"{text context}.{name}"
        | TypeHole -> "_"
        | TypeUnit -> "()"
        | _ -> "…"

    /// `context` as a message writes it, with its positional arguments: `Iter(c)`.
    let private written (context: Identifier) (args: TypeExpr list) =
        if args.IsEmpty then context else text (TypeExpr.synthetic (TypeApply(context, args)))

    /// The associated types of the context declared with `fields`, each with its default if it is written with one.
    let private declaredIn (fields: TypeRecordField list) =
        fields
        |> List.choose (function
            | TypeField(name, _, Some { Kind = TypeKeyword }, defaultOpt) -> Some (name, defaultOpt)
            | _ -> None)

    /// The names of the members of the context declared with `fields` that are not associated types.
    let private valueMembersIn (fields: TypeRecordField list) =
        fields
        |> List.choose (function
            | TypeField(_, _, Some { Kind = TypeKeyword }, _) -> None
            | TypeField(name, _, _, _) -> Some name
            | _ -> None)
        |> Set.ofList

    /// The contexts with associated types the module declares, at the top level and in its blocks, each by its name.
    let rec private declaredShapes (items: TopLevelItem list) : (Identifier * Shape) list =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (TypeDef(name, modifiers, parameters, { Kind = TypeRecord fields })) when List.contains Context modifiers ->
                match declaredIn fields with
                | [] -> []
                | associated -> [ name, { Written = parameters.Length; Associated = List.map fst associated; Members = valueMembersIn fields } ]
            | ModuleBlock(_, inner) -> declaredShapes inner
            | _ -> [])

    /// The imported contexts with associated types, each by the name the module writes it with.
    let private importedShapes (typeDefs: Map<string, TypeDefInfo>) : (Identifier * Shape) list =
        typeDefs
        |> Map.toList
        |> List.choose (fun (name, info) ->
            if info.IsContext && not info.AssociatedTypes.IsEmpty then
                let members =
                    match Unifier.unalias info.Underlying with
                    | TyRecord(fields, _) -> fields.Keys |> Set.ofSeq
                    | _ -> Set.empty
                Some (name, { Written = info.Parameters.Length - info.AssociatedTypes.Length; Associated = info.AssociatedTypes; Members = members })
            else None)

    /// `module'` with its associated types written as the contexts' parameters (see the module's comment), and the
    /// errors for those written wrongly. `typeDefs` are the types and contexts the module's imports bring in.
    let rewrite (typeDefs: Map<string, TypeDefInfo>) (module': Module) : Module * Diagnostic list =
        let errors = ResizeArray<Diagnostic>()
        let report code (span: SourceSpan) message = errors.Add(Diagnostics.at code span message)
        // The error `code` about `subject`, which its headline names.
        let reportAbout code (span: SourceSpan) (subject: string) message = errors.Add(Diagnostics.at code span message |> Diagnostics.about subject)
        let shapes = Map.ofList (importedShapes typeDefs @ declaredShapes module')

        // The error for the named argument `arg` of the context `context`, which has no associated type of its name.
        let unknownNamed (context: Identifier) (shape: Shape option) (arg: TypeExpr) =
            match arg.Kind, shape with
            | TypeNamedArgument(name, _), Some shape when shape.Members.Contains name ->
                reportAbout DiagnosticCodes.UnknownAssociatedType arg.Span name
                    $"It is a member of '{context}', not one of its associated types: only an associated type is fixed by name in a context's arguments"
            | TypeNamedArgument(name, _), _ ->
                reportAbout DiagnosticCodes.UnknownAssociatedType arg.Span name $"'{context}' has none of this name"
            | _ -> ()

        // What the bare name `name`, written at `t`, stands for among `scope`: the associated type of that name of the one
        // context in scope that has one. Two that have one is an error, and the name a type variable of its own.
        let bare (scope: InScope list) (t: TypeExpr) (name: Identifier) : TypeExpr =
            match scope |> List.filter (fun entry -> entry.Types |> List.exists (fst >> (=) name)) |> List.distinctBy (fun entry -> entry.Context, entry.Arguments) with
            | [] -> t
            | [ entry ] -> { t with Kind = (entry.Types |> List.find (fst >> (=) name) |> snd).Kind }
            | several ->
                let qualified = several |> List.map (fun entry -> $"`{written entry.Context entry.Arguments}.{name}`")
                let contexts = several |> List.map (fun entry -> $"'{written entry.Context entry.Arguments}'")
                reportAbout DiagnosticCodes.AmbiguousAssociatedType t.Span name
                    $"""'{name}' is an associated type of {Diagnostics.listed contexts}: write which one, as {String.concat " or " qualified}"""
                { t with Kind = TypeVar name }

        // What `ctx.name`, written at `t`, stands for: the associated type `name` of the context `ctx` in `scope`.
        let qualified (scope: InScope list) (t: TypeExpr) (ctx: TypeExpr) (name: Identifier) : TypeExpr =
            let ownName = { t with Kind = TypeVar (text t) }
            match contextOf ctx with
            | Some (context, args) ->
                match scope |> List.tryFind (fun entry -> entry.Context = context && entry.Arguments = args), shapes |> Map.tryFind context with
                | Some entry, _ when entry.Types |> List.exists (fst >> (=) name) -> { t with Kind = (entry.Types |> List.find (fst >> (=) name) |> snd).Kind }
                | _, Some shape when shape.Associated |> List.contains name ->
                    report DiagnosticCodes.UnknownAssociatedType t.Span
                        $"'{written context args}' is not one of the contexts required here, so its '{name}' can't be named: add it to the contexts, as in `[{written context args}]`"
                    ownName
                | _, Some shape when shape.Members.Contains name ->
                    reportAbout DiagnosticCodes.UnknownAssociatedType t.Span name $"It is a member of '{context}', not one of its associated types"
                    ownName
                | _ ->
                    reportAbout DiagnosticCodes.UnknownAssociatedType t.Span name $"'{context}' has none of this name"
                    ownName
            | None ->
                report DiagnosticCodes.UnknownAssociatedType t.Span $"Only a context has associated types, as in `Iter(c).{name}`"
                ownName

        let rec typeExpr (scope: InScope list) (t: TypeExpr) : TypeExpr =
            match t.Kind with
            | TypeWithContext(contexts, inner) ->
                let contexts, entries = contextList scope contexts
                { t with Kind = TypeWithContext(contexts, typeExpr (scope @ entries) inner) }
            | TypeContext contexts -> { t with Kind = TypeContext(fst (contextList scope contexts)) }
            | TypeName name when not (name.Contains '.') -> bare scope t name
            | TypeAssociated(ctx, name) -> qualified scope t ctx name
            // A context written as a type, as `use Iter(Bag)(...)` writes it: its members settle what it leaves out.
            | TypeApply(name, args) when shapes.ContainsKey name -> contextAsType scope t name args
            | TypeKeyword ->
                report DiagnosticCodes.InvalidAssociatedType t.Span
                    "`type` is only the type of a context's associated type, as in `context Iter(c) = (elem: type, ...)`"
                { t with Kind = TypeUnit }
            | TypeNamedArgument(name, value) ->
                report DiagnosticCodes.UnknownAssociatedType t.Span $"Only a context's associated type is given by name, and '{name}' isn't one"
                typeExpr scope value
            | _ -> TypeExpr.mapChildren (typeExpr scope) t

        // The context `ctx` written as a type, `name` applied to `args`: each associated type it doesn't fix by name is
        // the one of that context in `scope`, if it requires it, else `_`.
        and contextAsType (scope: InScope list) (ctx: TypeExpr) (name: Identifier) (args: TypeExpr list) : TypeExpr =
            let shape = shapes.[name]
            let named, positional = args |> List.partition isNamed
            let positional = positional |> List.map (typeExpr scope)
            if named.IsEmpty && positional.Length = shape.Written + shape.Associated.Length then
                { ctx with Kind = TypeApply(name, positional) }
            else
                let given = named |> List.choose (fun arg -> match arg.Kind with TypeNamedArgument(n, value) -> Some (n, value) | _ -> None)
                named |> List.filter (fun arg -> match arg.Kind with TypeNamedArgument(n, _) -> not (shape.Associated |> List.contains n) | _ -> false) |> List.iter (unknownNamed name (Some shape))
                let inScope = scope |> List.tryFind (fun entry -> entry.Context = name && entry.Arguments = (args |> List.filter (isNamed >> not)))
                let associated =
                    shape.Associated
                    |> List.map (fun associated ->
                        match given |> List.tryFind (fst >> (=) associated), inScope |> Option.bind (fun entry -> entry.Types |> List.tryFind (fst >> (=) associated)) with
                        | Some (_, value), _ -> typeExpr scope value
                        | None, Some (_, ty) -> ty
                        | None, None -> TypeExpr.at ctx.Span TypeHole)
                { ctx with Kind = TypeApply(name, positional @ associated) }

        // The contexts a signature's list writes, each with an argument for each of its associated types, and where
        // each can be named. An associated type no context of `scope` or of the list shares a name with is a variable
        // of its own name; another, of the qualified name, `Iter(c).elem`. The contexts' own arguments can name them.
        and contextList (scope: InScope list) (contexts: TypeExpr list) : TypeExpr list * InScope list =
            let read =
                contexts
                |> List.map (fun ctx ->
                    match contextOf ctx with
                    | Some (name, args) ->
                        let named, positional = args |> List.partition isNamed
                        ctx, Some (name, positional, named, shapes |> Map.tryFind name)
                    | None -> ctx, None)
            let declaring =
                (scope |> List.collect (fun entry -> entry.Types |> List.map fst))
                @ (read |> List.collect (function
                    | _, Some (_, positional, named, Some shape) when named.IsEmpty && positional.Length = shape.Written + shape.Associated.Length -> []
                    | _, Some (_, _, _, Some shape) -> shape.Associated
                    | _ -> []))
            let shared name = (declaring |> List.filter ((=) name) |> List.length) > 1
            // Each context with associated types, where they can be named; a context written with an argument for each,
            // as a type is written for a reader, `Iter(c, elem)`, names them by those.
            let entryOf reading =
                match reading with
                | Some (name, positional: TypeExpr list, named: TypeExpr list, Some shape) ->
                    let full = named.IsEmpty && positional.Length = shape.Written + shape.Associated.Length
                    let types =
                        if full then List.zip shape.Associated (List.skip shape.Written positional)
                        else
                            shape.Associated
                            |> List.map (fun associated ->
                                match named |> List.tryPick (fun arg -> match arg.Kind with TypeNamedArgument(n, value) when n = associated -> Some value | _ -> None) with
                                | Some value -> associated, typeExpr scope value
                                | None ->
                                    let variable = if shared associated then $"{written name positional}.{associated}" else associated
                                    associated, TypeExpr.synthetic (TypeVar variable))
                    Some ({ Context = name; Arguments = List.truncate shape.Written positional; Types = types }, full)
                | _ -> None
            let withEntries = read |> List.map (fun (ctx, reading) -> ctx, reading, entryOf reading)
            let entries = withEntries |> List.choose (fun (_, _, entry) -> entry |> Option.map fst)
            let inList = scope @ entries
            let contexts =
                withEntries
                |> List.map (fun (ctx, reading, entry) ->
                    match reading with
                    | Some (name, positional, named, shape) ->
                        named
                        |> List.filter (fun arg ->
                            match arg.Kind, shape with
                            | TypeNamedArgument(n, _), Some shape -> not (shape.Associated |> List.contains n)
                            | _ -> true)
                        |> List.iter (unknownNamed name shape)
                        let positional = positional |> List.map (typeExpr inList)
                        match entry, ctx.Kind with
                        | Some (_, true), _ -> { ctx with Kind = TypeApply(name, positional) }
                        | Some (entry, false), _ -> { ctx with Kind = TypeApply(name, positional @ (entry.Types |> List.map snd)) }
                        | None, TypeName _ -> ctx
                        | None, _ -> { ctx with Kind = TypeApply(name, positional) }
                    | None -> typeExpr inList ctx)
            contexts, entries

        // `t`, a def's annotation, rewritten, and the contexts it requires where its value can name their associated
        // types: in the annotations written in it.
        let annotation (t: TypeExpr) : TypeExpr * InScope list =
            match t.Kind with
            | TypeWithContext(contexts, inner) ->
                let contexts, entries = contextList [] contexts
                { t with Kind = TypeWithContext(contexts, typeExpr entries inner) }, entries
            | TypeContext contexts ->
                let contexts, entries = contextList [] contexts
                { t with Kind = TypeContext contexts }, entries
            | _ -> typeExpr [] t, []

        // The declaration of the type `name`: a context with associated types takes them as its last parameters.
        let typeDef (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
            match body.Kind with
            | TypeRecord fields when List.contains Context modifiers && not (declaredIn fields).IsEmpty ->
                let associated = declaredIn fields
                for field in fields do
                    match field with
                    | TypeField(member', _, Some keyword, defaultOpt) when keyword.Kind = TypeKeyword ->
                        if defaultOpt.IsSome then
                            reportAbout DiagnosticCodes.InvalidAssociatedType keyword.Span member'
                                $"An associated type can't have a default: each instance fixes it, by its members' types or with `type T.{member'} = ...`"
                        if TypeResolution.isBuiltInTypeName member' then
                            reportAbout DiagnosticCodes.InvalidAssociatedType keyword.Span member' $"'{member'}' is a built-in type, so it can't be the name of an associated type"
                        elif member'.Length < 2 || not (System.Char.IsLower member'[0]) then
                            reportAbout DiagnosticCodes.InvalidAssociatedType keyword.Span member'
                                $"An associated type's name is lowercase and longer than one letter"
                    | _ -> ()
                let own =
                    { Context = name
                      Arguments = parameters |> List.map (fun (parameter, _) -> TypeExpr.synthetic (TypeVar parameter))
                      Types = associated |> List.map (fun (member', _) -> member', TypeExpr.synthetic (TypeVar member')) }
                let members =
                    fields |> List.filter (function TypeField(_, _, Some { Kind = TypeKeyword }, _) -> false | _ -> true)
                let body = typeExpr [ own ] { body with Kind = TypeRecord members }
                let parameters = parameters @ (associated |> List.map (fun (member', _) -> member', None))
                modifiers @ [ AssociatedTypes (List.map fst associated) ], parameters, body
            | _ -> modifiers, parameters, typeExpr [] body

        let typeParameters = List.map (fun (name, constraintOpt) -> name, constraintOpt |> Option.map (typeExpr []))

        // `value`, the value of a def whose annotation requires the contexts `scope`.
        let expression (scope: InScope list) (value: Expression) =
            value
            |> mapExpressionKinds (fun _ kind ->
                match kind with
                | Lambda(parameters, body, takesUnit) -> Lambda(parameters |> List.map (fun (name, typeOpt) -> name, typeOpt |> Option.map (typeExpr scope)), body, takesUnit)
                | ContextMemberAccess(ctxType, memberName) -> ContextMemberAccess(typeExpr scope ctxType, memberName)
                | ContextMemberCall(ctxType, memberName, args) -> ContextMemberCall(typeExpr scope ctxType, memberName, args)
                // An associated type is also a type argument: `type Iter(c).elem`, or `type elem`.
                | TypeValue written -> TypeValue(typeExpr scope written)
                | ContextNames(names, body) -> ContextNames(names |> List.map (fun (name, typeOpt, span) -> name, typeOpt |> Option.map (typeExpr scope), span), body)
                | Block statements ->
                    Block(
                        statements
                        |> List.map (fun statement ->
                            match statement.Kind with
                            | DefStatement(isExport, name, typeOpt, value) ->
                                let typeOpt = typeOpt |> Option.map (fun t -> match t.Kind with TypeWithContext _ | TypeContext _ -> fst (annotation t) | _ -> typeExpr scope t)
                                { statement with Kind = DefStatement(isExport, name, typeOpt, value) }
                            | TypeDefStatement(name, modifiers, parameters, body) ->
                                let modifiers, parameters, body = typeDef name modifiers (typeParameters parameters) body
                                { statement with Kind = TypeDefStatement(name, modifiers, parameters, body) }
                            | ImportStatement _
                            | UseStatement _
                            | ExprStatement _
                            | AttributeStatement _ -> statement))
                | other -> other)

        let rec item (topLevel: TopLevelItem) : TopLevelItem =
            match topLevel.Kind with
            | Def (ValueDef(isExport, name, typeOpt, value)) ->
                let typeOpt, scope =
                    match typeOpt with
                    | Some t -> let t, scope = annotation t in Some t, scope
                    | None -> None, []
                { topLevel with Kind = Def (ValueDef(isExport, name, typeOpt, expression scope value)) }
            | Def (TypeDef(name, modifiers, parameters, body)) ->
                let modifiers, parameters, body = typeDef name modifiers (typeParameters parameters) body
                { topLevel with Kind = Def (TypeDef(name, modifiers, parameters, body)) }
            | Expr value -> { topLevel with Kind = Expr (expression [] value) }
            | ModuleBlock(name, items) -> { topLevel with Kind = ModuleBlock(name, items |> List.map item) }
            | ModuleDecl _
            | Import _
            | ReExport _
            | Attribute _ -> topLevel

        let rewritten = module' |> List.map item
        rewritten, errors |> Seq.distinctBy (fun error -> error.Span, error.Message) |> List.ofSeq
