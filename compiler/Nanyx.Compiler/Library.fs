namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program

type CompilationPhase =
    | Parsed
    | Desugared
    | Typed
    | Lowered
    | Emitted

type CompileResult =
    { Phase: CompilationPhase
      Diagnostics: Diagnostic list
      Typed: TypedModule option
      /// The root module as parsed, before desugaring: the syntax the user wrote, whose nodes the typed module's
      /// `ExprTypes` and `CalleeTypes` are keyed by. Only present when the module type-checked.
      Syntax: Module option }

/// Where a module's source came from, which decides how its imports are resolved and what is in scope.
type private ModuleOrigin =
    /// A file on disk: imports by path are relative to its directory.
    | SourceFile of fullPath: string
    /// A module of the embedded standard library, checkedModule with the compiler intrinsics in scope.
    | StandardLibraryModule of name: string
    /// Source given directly (e.g. by tests): only standard-library imports can be resolved.
    | InMemory

/// A type-checkedModule module together with what an importer needs from it.
type private CheckedModule =
    { Typed: TypedModule
      /// The module's own items (imports removed), as the backend will see them.
      Own: Module
      /// Items inlined from the modules it imports, already renamed and ready for the backend.
      Inlined: TopLevelItem list
      /// The declared module name, if any.
      Name: string option
      /// The module as parsed, before desugaring.
      Syntax: Module
      /// The substitution unification found, which resolves the types inference recorded by node.
      Subst: Subst
      /// The types inference recorded by node, unresolved: only the root module's are resolved (see `compileWith`).
      RawExprTypes: Map<NodeId, Ty>
      RawCalleeTypes: Map<NodeId, Ty>
      RawSymbolTypes: Map<SymbolId, Ty>
      /// The symbols of the module's own exported definitions, by name: not those of what it imports.
      Exports: Map<string, Symbol>
      /// The contexts each of the module's own exported definitions requires, written or inferred, by name.
      ContextRequirements: Map<string, TypeExpr list>
      /// The type each of those definitions declares, without its contexts, where it writes one.
      ContextSignatures: Map<string, TypeExpr>
      /// The ambient contexts (the runtime's, as `Console`) by the names the module knows them by, with their
      /// definitions: an importer that cannot name one still needs it where it calls a function that requires it.
      AmbientContexts: Map<string, TypeDefInfo> }

module Compiler =
    let private normalizeSource (source: string) =
        source.Replace("\r\n", "\n").Replace("\r", "\n")

    let private sanitizeIdentifier (name: string) =
        let sanitized =
            name
            |> Seq.map (fun c -> if Char.IsLetterOrDigit c || c = '_' then c else '_')
            |> Seq.toArray
            |> String
        if String.IsNullOrWhiteSpace sanitized then
            "_"
        elif Char.IsDigit sanitized.[0] then
            "_" + sanitized
        else
            sanitized

    let private importQualifier (item: ImportItem) =
        item.Alias |> Option.map sanitizeIdentifier

    let private resolveImportPath (baseDir: string) (item: ImportItem) : Result<string, Diagnostic> =
        let rawPath =
            match item.Source with
            | PathImport path ->
                if Path.IsPathRooted path then path else Path.Combine(baseDir, path)
            | ModuleImport name ->
                let relative =
                    name.Replace(".", string Path.DirectorySeparatorChar)
                        .Replace("/", string Path.DirectorySeparatorChar)
                Path.Combine(baseDir, relative + ".nyx")
        let fullPath =
            if Path.GetExtension rawPath = "" then rawPath + ".nyx" else rawPath
        if File.Exists fullPath then
            Ok (Path.GetFullPath fullPath)
        else
            Error (Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"Import not found: {rawPath}")

    let private collectImports (module': Module) =
        module'
        |> List.choose (function
            | Kind (Import items) -> Some items
            | _ -> None)
        |> List.collect id

    let private removeImportStatements (module': Module) =
        module'
        |> List.filter (function
            | Kind (Import _) -> false
            | _ -> true)

    let private declaredModuleName (module': Module) =
        module'
        |> List.tryPick (function
            | Kind (ModuleDecl name) -> Some name
            | _ -> None)

    /// An expression with `visit` applied to every node, inner nodes first: `visit` is given the node as it was
    /// (for its id) and its kind with the children already visited, and returns the node's new kind.
    let private mapExpressionKinds (visit: Expression -> ExpressionKind -> ExpressionKind) (expr: Expression) : Expression =
        let rec mapExpr (current: Expression) : Expression =
            { current with Kind = visit current (mapKind current) }
        and mapKind current =
            match current.Kind with
            | IdentifierExpr _ -> current.Kind
            | FunctionCall(name, rangeOpt, args) ->
                FunctionCall(name, rangeOpt, args |> List.map mapExpr)
            | Lambda(parameters, body) ->
                Lambda(parameters, mapExpr body)
            | BinaryOp(op, left, right) ->
                BinaryOp(op, mapExpr left, mapExpr right)
            | Pipe(value, name, rangeOpt, args) ->
                Pipe(mapExpr value, name, rangeOpt, args |> List.map mapExpr)
            | Block statements ->
                Block(statements |> List.map mapStatement)
            | Match(values, arms) ->
                let mappedValues = values |> List.map mapExpr
                let mappedArms = arms |> List.map (fun (patterns, armExpr) -> patterns, mapExpr armExpr)
                Match(mappedValues, mappedArms)
            | TupleExpr items -> TupleExpr(items |> List.map mapExpr)
            | RecordExpr fields ->
                RecordExpr(
                    fields
                    |> List.map (function
                        | NamedField(name, value) -> NamedField(name, mapExpr value)
                        | PositionalField value -> PositionalField(mapExpr value)))
            | ListExpr items -> ListExpr(items |> List.map mapExpr)
            | TagExpr(tagName, payloadOpt) -> TagExpr(tagName, payloadOpt |> Option.map mapExpr)
            | IfExpr(cond, thenExpr, elseExpr) ->
                IfExpr(mapExpr cond, mapExpr thenExpr, mapExpr elseExpr)
            | MemberAccess(baseExpr, fieldName, rangeOpt) -> MemberAccess(mapExpr baseExpr, fieldName, rangeOpt)
            | ContextMemberCall(ctxType, memberName, args) ->
                ContextMemberCall(ctxType, memberName, args |> List.map mapExpr)
            | UseIn(binding, body) ->
                UseIn(mapBinding binding, mapExpr body)
            | InterpolatedString parts ->
                InterpolatedString(
                    parts
                    |> List.map (function
                        | StringText text -> StringText text
                        | StringExpr value -> StringExpr(mapExpr value)))
            | WorkflowBindExpr(keyword, value) -> WorkflowBindExpr(keyword, mapExpr value)
            | WorkflowReturnExpr value -> WorkflowReturnExpr(mapExpr value)
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _ -> current.Kind
        and mapBinding binding =
            match binding with
            | UseValue valueExpr -> UseValue(mapExpr valueExpr)
            | UseContextInstance(ctxType, valueExpr) -> UseContextInstance(ctxType, mapExpr valueExpr)
        and mapStatement (statement: Statement) =
            { statement with Kind = mapStatementKind statement.Kind }
        and mapStatementKind kind =
            match kind with
            | DefStatement(isExport, name, typeOpt, value) -> DefStatement(isExport, name, typeOpt, mapExpr value)
            | ExprStatement value -> ExprStatement(mapExpr value)
            | UseStatement binding -> UseStatement(mapBinding binding)
            | ImportStatement _
            | TypeDefStatement _ -> kind
        mapExpr expr

    /// Renames every reference to a name in `renames` throughout an expression. Used when a module is
    /// inlined into its importer, so that its definitions keep pointing at each other under their new names.
    /// (Lambda parameters and local definitions that shadow a renamed name are not accounted for.)
    let private renameReferencesInExpr (renames: Map<string, string>) (expr: Expression) : Expression =
        let rename (name: string) = renames |> Map.tryFind name |> Option.defaultValue name
        expr
        |> mapExpressionKinds (fun _ kind ->
            match kind with
            | IdentifierExpr name -> IdentifierExpr(rename name)
            | FunctionCall(name, rangeOpt, args) -> FunctionCall(rename name, rangeOpt, args)
            | Pipe(value, name, rangeOpt, args) -> Pipe(value, rename name, rangeOpt, args)
            | other -> other)

    /// Every pipe in an expression that inference resolved to an attached definition (`targets`, by the pipe's
    /// node) written with that definition's name, so the backend calls it: `x \trim` becomes `x \String.trim`.
    let private retargetPipes (targets: Map<NodeId, Identifier>) (expr: Expression) : Expression =
        if targets.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, targets |> Map.tryFind current.Id with
                | Pipe(value, _, rangeOpt, args), Some target -> Pipe(value, target, rangeOpt, args)
                | other, _ -> other)

    /// The function that reads a built-in property: the string-length intrinsic, which needs no import, and the
    /// prelude's `List.length`, which every module imports.
    let private builtInPropertyFunction (property: BuiltInProperty) : Identifier =
        match property with
        | StringLength -> Intrinsics.stringLength
        | ListLength -> "List.length"

    /// `expr` with each read of a built-in property (`properties`, by the member access's node) written as a call to
    /// the function that reads it, for the backend, which knows only records' fields: `s.length` is
    /// `__nanyx_string_length(s)`.
    let private callBuiltInProperties (properties: Map<NodeId, BuiltInProperty>) (expr: Expression) : Expression =
        if properties.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind, properties |> Map.tryFind current.Id with
                | MemberAccess(target, _, rangeOpt), Some property -> FunctionCall(builtInPropertyFunction property, rangeOpt, [ target ])
                | other, _ -> other)

    /// `module'` with each name a block import brought in (`zero`, `$Money.new`) written as the definition it stands
    /// for (`money.zero`), which is the only name the backend knows. Only a name inference resolved to that definition
    /// is rewritten, not a local that shadows it.
    let private writeBlockAliases (state: NyxCompiler.Infer.InferState) (module': Module) : Module =
        if state.BlockAliases.IsEmpty then
            module'
        else
            let resolved (node: Expression) (name: string) =
                match state.BlockAliases |> Map.tryFind name, state.References |> Map.tryFind node.Id |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
                | Some qualified, Some symbol when symbol.Name = qualified -> qualified
                | _ -> name
            let rewrite =
                mapExpressionKinds (fun current kind ->
                    match kind with
                    | IdentifierExpr name -> IdentifierExpr(resolved current name)
                    | FunctionCall(name, rangeOpt, args) -> FunctionCall(resolved current name, rangeOpt, args)
                    | Pipe(value, name, rangeOpt, args) -> Pipe(value, resolved current name, rangeOpt, args)
                    | other -> other)
            module'
            |> List.map (fun item ->
                match item.Kind with
                | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, typeOpt, rewrite value)) }
                | Expr value -> { item with Kind = Expr (rewrite value) }
                | _ -> item)

    /// Every name an expression may refer to: identifiers, callees and pipe targets, and for a member access
    /// `a.b.c` both the dotted name and the member as `.c`, since the backend may call a definition named either
    /// way. Shadowing is ignored, so this can only over-count.
    let rec private referencedNames (expr: Expression) : string list =
        let rec dottedName (current: Expression) =
            match current.Kind with
            | IdentifierExpr name -> Some name
            | MemberAccess(inner, memberName, _) -> dottedName inner |> Option.map (fun prefix -> $"{prefix}.{memberName}")
            | _ -> None
        match expr.Kind with
        | IdentifierExpr name -> [ name ]
        | FunctionCall(name, _, args) -> name :: (args |> List.collect referencedNames)
        | Pipe(value, name, _, args) -> name :: referencedNames value @ (args |> List.collect referencedNames)
        | Lambda(_, body) -> referencedNames body
        | BinaryOp(_, left, right) -> referencedNames left @ referencedNames right
        | IfExpr(cond, thenExpr, elseExpr) -> referencedNames cond @ referencedNames thenExpr @ referencedNames elseExpr
        | Block statements -> statements |> List.collect referencedNamesInStatement
        | Match(values, arms) ->
            (values |> List.collect referencedNames)
            @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect referencedNamesInPattern) @ referencedNames body))
        | TupleExpr items
        | ListExpr items -> items |> List.collect referencedNames
        | RecordExpr fields ->
            fields |> List.collect (function NamedField(_, value) | PositionalField value -> referencedNames value)
        | TagExpr(_, payload) -> payload |> Option.map referencedNames |> Option.defaultValue []
        | MemberAccess(inner, memberName, _) ->
            $".{memberName}" :: (dottedName expr |> Option.toList) @ referencedNames inner
        | UseIn(binding, body) -> referencedNamesInBinding binding @ referencedNames body
        | WorkflowBindExpr(_, value)
        | WorkflowReturnExpr value -> referencedNames value
        | InterpolatedString parts ->
            parts |> List.collect (function StringExpr value -> referencedNames value | StringText _ -> [])
        | ContextMemberCall(_, memberName, args) -> memberName :: (args |> List.collect referencedNames)
        | ContextMemberAccess _
        | UnitExpr
        | LiteralExpr _ -> []

    and private referencedNamesInStatement (statement: Statement) : string list =
        match statement.Kind with
        | DefStatement(_, _, _, value) -> referencedNames value
        | ExprStatement value -> referencedNames value
        | UseStatement binding -> referencedNamesInBinding binding
        | ImportStatement _
        | TypeDefStatement _ -> []

    and private referencedNamesInBinding (binding: UseBinding) : string list =
        match binding with
        | UseValue value
        | UseContextInstance(_, value) -> referencedNames value

    and private referencedNamesInPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | GuardPattern(_, value) -> referencedNames value
        | RangePattern(startValue, endValue) -> referencedNames startValue @ referencedNames endValue
        | TagPattern(_, Some payload) -> referencedNamesInPattern payload
        | TuplePattern patterns
        | ListPattern(patterns, _) -> patterns |> List.collect referencedNamesInPattern
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect referencedNamesInPattern
        | RecordPattern(_, patterns) -> patterns |> List.collect referencedNamesInPattern
        | RecordMemberPattern members -> members |> List.collect (snd >> referencedNamesInPattern)
        | TagPattern(_, None)
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | ElsePattern -> []

    /// The inlined items `own` uses: the value definitions it reaches, directly or through other inlined
    /// definitions, and every type definition, in their original order. A definition named like a member of a
    /// context is always kept, because the backend supplies it as the ambient instance of that context (the
    /// runtime's `Console`, say) without anything naming it.
    let private reachedItems (own: Module) (inlined: TopLevelItem list) : TopLevelItem list =
        let bodies =
            inlined
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, value))) -> Some (name, value)
                | _ -> None)
        let contextMembers =
            inlined @ own
            |> List.collect (function
                | Kind (Def (TypeDef(_, modifiers, _, Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
                    fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
                | _ -> [])
            |> Set.ofList
        let rootNames =
            own
            |> List.collect (function
                | Kind (Def (ValueDef(_, _, _, value))) -> referencedNames value
                | Kind (Expr value) -> referencedNames value
                | _ -> [])
        let isReferenced (referenced: Set<string>) (name: string) =
            referenced.Contains name
            || contextMembers.Contains name
            || (name.Contains "." && referenced.Contains (name.Substring(name.LastIndexOf '.')))
        let rec reach (referenced: Set<string>) (reached: Set<string>) =
            let newlyReached =
                bodies |> List.filter (fun (name, _) -> not (reached.Contains name) && isReferenced referenced name)
            if newlyReached.IsEmpty then
                reached
            else
                let referenced = newlyReached |> List.collect (snd >> referencedNames) |> List.fold (fun acc name -> Set.add name acc) referenced
                reach referenced (newlyReached |> List.fold (fun acc (name, _) -> Set.add name acc) reached)
        let reached = reach (Set.ofList rootNames) Set.empty
        inlined
        |> List.filter (function
            | Kind (Def (ValueDef(_, name, _, _))) -> reached.Contains name
            | _ -> true)

    /// The items an importer inlines for the backend from a module it imports: the module's own value
    /// definitions and type definitions (plus whatever that module inlined itself). Exported definitions keep
    /// their names, or take the import's alias as a qualifier; private ones are qualified with the module's own
    /// name so they cannot collide with anything in the importer.
    let private inlinedRenames (item: ImportItem) (checkedModule: CheckedModule) : Map<string, string> =
        let qualifier = importQualifier item
        let moduleName =
            checkedModule.Name
            |> Option.map sanitizeIdentifier
            |> Option.defaultValue "import"
        checkedModule.Own
        |> List.choose (function
            | Kind (Def (ValueDef(true, name, _, _))) ->
                qualifier |> Option.map (fun q -> name, $"{q}.{name}")
            | Kind (Def (ValueDef(false, name, _, _))) -> Some (name, $"{moduleName}.{name}")
            | _ -> None)
        |> Map.ofList

    let private inlinedItemsForImport (item: ImportItem) (checkedModule: CheckedModule) : TopLevelItem list =
        let renames = inlinedRenames item checkedModule
        let rename (name: string) = renames |> Map.tryFind name |> Option.defaultValue name
        let ownItems =
            checkedModule.Own
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, typeOpt, expr))) as item ->
                    Some { item with Kind = Def (ValueDef(false, rename name, typeOpt, renameReferencesInExpr renames expr)) }
                | Kind (Def (TypeDef _)) as typeDef -> Some typeDef
                | _ -> None)
        checkedModule.Inlined @ ownItems

    /// The types of the definitions `inlinedItemsForImport` inlines, by the names they are inlined under, so that
    /// the backend knows them as it knows the importer's own (the layout of an imported record, say).
    let private inlinedTypesForImport (item: ImportItem) (checkedModule: CheckedModule) : Map<string, Ty> =
        let renames = inlinedRenames item checkedModule
        checkedModule.Typed.Types
        |> Seq.map (fun (KeyValue(name, ty)) -> (renames |> Map.tryFind name |> Option.defaultValue name), ty)
        |> Map.ofSeq

    /// Keeps the first definition of each name, so a module inlined along two import paths appears once.
    let private distinctDefinitions (items: TopLevelItem list) : TopLevelItem list =
        let seen = System.Collections.Generic.HashSet<string>()
        items
        |> List.filter (function
            | Kind (Def (ValueDef(_, name, _, _))) -> seen.Add("value:" + name)
            | Kind (Def (TypeDef(name, _, _, _))) -> seen.Add("type:" + name)
            | _ -> true)

    let private exportedValueNames (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(true, name, _, _))) -> Some name
            | _ -> None)
        |> Set.ofList

    let private exportedTypeNames (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, modifiers, _, _))) when modifiers |> List.contains Export -> Some name
            | _ -> None)
        |> Set.ofList

    let private mergeTypeDefs (target: Map<string, TypeDefInfo>) (incoming: Map<string, TypeDefInfo>) =
        incoming
        |> Map.fold (fun acc key value ->
            if Map.containsKey key acc then acc else acc |> Map.add key value) target

    /// Brings an imported module's exported values into scope, as references to their symbols (`symbols`, by name).
    /// Every type variable left in an exported type is quantified, so each use takes fresh ones. The imported
    /// module numbered its variables separately from the importer, so they are first renumbered below zero, where
    /// the importer never numbers one: a quantified variable that shared an id with one of its fresh replacements
    /// would be replaced twice.
    let private extendEnvWithImport (env: TypeEnv) (qualifierOpt: string option) (types: Map<string, Ty>) (symbols: Map<string, SymbolId>) =
        let quantify ty =
            let renumbered =
                TypeEnv.freeTyVars ty
                |> Seq.mapi (fun index id -> id, TyVar { Id = -(index + 1); Name = None })
                |> Map.ofSeq
            TypeEnv.generalize TypeEnv.empty (Unifier.apply renumbered ty)
        types
        |> Map.fold (fun acc name ty ->
            let importedName =
                match qualifierOpt with
                | Some qualifier -> $"{qualifier}.{name}"
                | None -> name
            acc |> TypeEnv.declare importedName (symbols |> Map.tryFind name) (quantify ty)) env

    /// The diagnostic for a failed parse. FParsec's report starts by giving the file and position ("Error in path:
    /// Ln: 1 Col: 5"), which the diagnostic's span already gives. FParsec wraps that line when the path is long, so
    /// it is cut off through the position, not at the first line break.
    let syntaxError (error: ParseError) =
        let column = error.Message.IndexOf(" Col: ", System.StringComparison.Ordinal)
        let lineEnd = if column < 0 then -1 else error.Message.IndexOf('\n', column)
        let message =
            if error.Message.StartsWith "Error in " && lineEnd >= 0 then error.Message.Substring(lineEnd + 1)
            else error.Message
        Diagnostics.at DiagnosticCodes.SyntaxError error.Span message
    let private parseWithDiagnostics (filePath: string) (source: string) : Result<Module * DeclaredNames * (ModuleName * SourceSpan) list, Diagnostic list> =
        match parseModuleFileWithNames filePath source with
        | Result.Ok parsed -> Ok parsed
        | Result.Error err -> Error [ syntaxError err ]

    let desugar (module': Module) =
        let isReturnKeyword keyword = keyword = "return"

        // The builder whose block is being desugared (`proc` in `proc { ... }`), and whether that block used a
        // workflow keyword. A keyword `k` in the block calls the builder's member `k`, `return` its member `return`.
        let mutable builder: string option = None
        let mutable builderUsed = false
        let mutable freshNames = 0
        let freshName prefix =
            freshNames <- freshNames + 1
            $"__{prefix}_{freshNames}"

        // `k(args)` for the workflow keyword `k`: a call to the member `k` of the builder the block was written
        // after, through a local of its own (`def m = proc.try` then `m(args)`), so that each use instantiates the
        // builder's type afresh. Outside a builder's block it is a call to a function named `k`.
        let workflowCall (source: Expression) (keyword: string) (args: Expression list) : Expression =
            match builder with
            | Some builderName ->
                builderUsed <- true
                let memberLocal = freshName $"{sanitizeIdentifier builderName}_{keyword}"
                let memberAccess = Expression.from source (MemberAccess(Expression.from source (IdentifierExpr builderName), keyword, None))
                Expression.from source (
                    Block [
                        Statement.from source (DefStatement(false, memberLocal, None, memberAccess))
                        Statement.from source (ExprStatement (Expression.from source (FunctionCall(memberLocal, None, args))))
                    ])
            | None -> Expression.from source (FunctionCall(keyword, None, args))

        // A workflow bind nested in an expression, as in `return R(a = try f())`, becomes a bind of a fresh name
        // just before its statement, and the expression refers to that name. Binds are lifted from the places an
        // expression always evaluates, inner ones before outer ones and left to right; not from lambdas, nested
        // blocks, the branches of an `if` or a `match`, or the right of `and`/`or`.
        let rec liftBinds (expr: Expression) : Statement list * Expression =
            let liftAll exprs =
                let lifted = exprs |> List.map liftBinds
                List.collect fst lifted, List.map snd lifted
            let rebuild kind = { expr with Kind = kind }
            match expr.Kind with
            | WorkflowBindExpr(keyword, value) when not (isReturnKeyword keyword) ->
                let prefix, value' = liftBinds value
                let name = freshName keyword
                prefix @ [ Statement.from expr (DefStatement(false, name, None, { expr with Kind = WorkflowBindExpr(keyword, value') })) ],
                Expression.from expr (IdentifierExpr name)
            | WorkflowBindExpr(keyword, value) ->
                let prefix, value' = liftBinds value
                prefix, rebuild (WorkflowBindExpr(keyword, value'))
            | WorkflowReturnExpr value ->
                let prefix, value' = liftBinds value
                prefix, rebuild (WorkflowReturnExpr value')
            | FunctionCall(name, rangeOpt, args) ->
                let prefix, args' = liftAll args
                prefix, rebuild (FunctionCall(name, rangeOpt, args'))
            | ContextMemberCall(ctxType, field, args) ->
                let prefix, args' = liftAll args
                prefix, rebuild (ContextMemberCall(ctxType, field, args'))
            | BinaryOp(("and" | "or") as op, left, right) ->
                let prefix, left' = liftBinds left
                prefix, rebuild (BinaryOp(op, left', right))
            | BinaryOp(op, left, right) ->
                match liftAll [ left; right ] with
                | prefix, [ left'; right' ] -> prefix, rebuild (BinaryOp(op, left', right'))
                | _ -> [], expr
            | Pipe(value, name, rangeOpt, args) ->
                match liftAll (value :: args) with
                | prefix, value' :: args' -> prefix, rebuild (Pipe(value', name, rangeOpt, args'))
                | _ -> [], expr
            | TupleExpr items ->
                let prefix, items' = liftAll items
                prefix, rebuild (TupleExpr items')
            | ListExpr items ->
                let prefix, items' = liftAll items
                prefix, rebuild (ListExpr items')
            | RecordExpr fields ->
                let lifted =
                    fields
                    |> List.map (function
                        | NamedField(name, value) ->
                            let prefix, value' = liftBinds value
                            prefix, NamedField(name, value')
                        | PositionalField value ->
                            let prefix, value' = liftBinds value
                            prefix, PositionalField value')
                List.collect fst lifted, rebuild (RecordExpr(List.map snd lifted))
            | TagExpr(name, Some payload) ->
                let prefix, payload' = liftBinds payload
                prefix, rebuild (TagExpr(name, Some payload'))
            | MemberAccess(baseExpr, field, rangeOpt) ->
                let prefix, baseExpr' = liftBinds baseExpr
                prefix, rebuild (MemberAccess(baseExpr', field, rangeOpt))
            | IfExpr(cond, thenExpr, elseExpr) ->
                let prefix, cond' = liftBinds cond
                prefix, rebuild (IfExpr(cond', thenExpr, elseExpr))
            | Match(values, arms) ->
                let prefix, values' = liftAll values
                prefix, rebuild (Match(values', arms))
            | InterpolatedString parts ->
                let lifted =
                    parts
                    |> List.map (function
                        | StringText text -> [], StringText text
                        | StringExpr value ->
                            let prefix, value' = liftBinds value
                            prefix, StringExpr value')
                List.collect fst lifted, rebuild (InterpolatedString(List.map snd lifted))
            | TagExpr(_, None)
            | Lambda _
            | Block _
            | UseIn _
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> [], expr

        // The statement with the binds nested in it lifted out before it. A bind that is a whole statement
        // (`def a = try f()`, or `try f()` on its own) stays where it is; only binds inside its value are lifted.
        let liftStatementBinds (statement: Statement) : Statement list =
            let liftValue (value: Expression) =
                match value.Kind with
                | WorkflowBindExpr(keyword, bound) when not (isReturnKeyword keyword) ->
                    let prefix, bound' = liftBinds bound
                    prefix, { value with Kind = WorkflowBindExpr(keyword, bound') }
                | _ -> liftBinds value
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, value) ->
                let prefix, value' = liftValue value
                prefix @ [ { statement with Kind = DefStatement(isExport, name, typeOpt, value') } ]
            | ExprStatement value ->
                let prefix, value' = liftValue value
                prefix @ [ { statement with Kind = ExprStatement value' } ]
            | UseStatement _
            | ImportStatement _
            | TypeDefStatement _ -> [ statement ]

        // A workflow bind or return becomes a call to the workflow's members, whose type is not that of the bind as
        // written (a bind's call is the whole rest of the workflow), so that call is a node of its own, with a new id.
        // Everything else keeps its node's id.
        let rec desugarExpr (expr: Expression) : Expression =
            match expr.Kind with
            | WorkflowBindExpr _
            | WorkflowReturnExpr _ -> Expression.from expr (desugarKind expr)
            | _ -> { expr with Kind = desugarKind expr }
        and desugarKind expr =
            match expr.Kind with
            // A block written after a builder's name, `proc { ... }`: when the block uses a workflow keyword, it
            // is the block itself, with its keywords calling the builder's members (see `workflowCall`). The
            // documented `proc.apply({ ... })` around it is not made yet: the block runs where it is written.
            | FunctionCall(name, rangeOpt, [ Kind (Lambda([], body)) as lambda ]) when not (name.StartsWith "__") ->
                let outerBuilder, outerUsed = builder, builderUsed
                builder <- Some name
                builderUsed <- false
                let body' = desugarExpr body
                let used = builderUsed
                builder <- outerBuilder
                builderUsed <- outerUsed
                if used then body'.Kind
                else FunctionCall(name, rangeOpt, [ { lambda with Kind = Lambda([], body') } ])
            | FunctionCall(name, rangeOpt, args) ->
                FunctionCall(name, rangeOpt, args |> List.map desugarExpr)
            | Lambda(args, body) ->
                Lambda(args, desugarExpr body)
            | BinaryOp(op, left, right) ->
                BinaryOp(op, desugarExpr left, desugarExpr right)
            | Pipe(value, name, rangeOpt, args) ->
                Pipe(desugarExpr value, name, rangeOpt, args |> List.map desugarExpr)
            | Block statements ->
                Block(desugarBlock statements)
            | Match(exprs, arms) ->
                let mappedExprs = exprs |> List.map desugarExpr
                let mappedArms = arms |> List.map (fun (patterns, expr) -> patterns, desugarExpr expr)
                Match(mappedExprs, mappedArms)
            | TupleExpr items -> TupleExpr(items |> List.map desugarExpr)
            | RecordExpr fields ->
                let mappedFields =
                    fields
                    |> List.map (function
                        | NamedField(name, value) -> NamedField(name, desugarExpr value)
                        | PositionalField value -> PositionalField(desugarExpr value))
                RecordExpr mappedFields
            | ListExpr items -> ListExpr(items |> List.map desugarExpr)
            | TagExpr(name, payload) -> TagExpr(name, payload |> Option.map desugarExpr)
            | IfExpr(cond, thenExpr, elseExpr) ->
                IfExpr(desugarExpr cond, desugarExpr thenExpr, desugarExpr elseExpr)
            | MemberAccess(baseExpr, field, rangeOpt) -> MemberAccess(desugarExpr baseExpr, field, rangeOpt)
            | ContextMemberAccess(ctxType, field) -> ContextMemberAccess(ctxType, field)
            | ContextMemberCall(ctxType, field, args) -> ContextMemberCall(ctxType, field, args |> List.map desugarExpr)
            | UseIn(binding, body) ->
                let binding' = desugarUseBinding binding
                UseIn(binding', desugarExpr body)
            | WorkflowBindExpr(keyword, value) -> (workflowCall expr keyword [desugarExpr value]).Kind
            | WorkflowReturnExpr value -> (workflowCall expr "return" [desugarExpr value]).Kind
            | InterpolatedString parts ->
                let mappedParts =
                    parts
                    |> List.map (function
                        | StringText text -> StringText text
                        | StringExpr expr -> StringExpr (desugarExpr expr))
                InterpolatedString mappedParts
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> expr.Kind
        and desugarUseBinding binding =
            match binding with
            | UseValue expr -> UseValue (desugarExpr expr)
            | UseContextInstance(ctxType, expr) -> UseContextInstance(ctxType, desugarExpr expr)
        and desugarStatement (stmt: Statement) =
            match stmt.Kind with
            | DefStatement(isExport, name, typeOpt, expr) ->
                let expr' =
                    match expr.Kind with
                    | WorkflowBindExpr(keyword, value) when isReturnKeyword keyword ->
                        workflowCall expr "return" [desugarExpr value]
                    | _ -> desugarExpr expr
                { stmt with Kind = DefStatement(isExport, name, typeOpt, expr') }
            | ExprStatement expr -> { stmt with Kind = ExprStatement (desugarExpr expr) }
            | UseStatement binding -> { stmt with Kind = UseStatement (desugarUseBinding binding) }
            | ImportStatement _
            | TypeDefStatement _ -> stmt
        and desugarWorkflowChain (statements: Statement list) =
            match statements with
            | [] -> Expression.synthetic UnitExpr
            | statement :: rest ->
                match statement.Kind with
                | DefStatement(isExport, name, typeOpt, expr) ->
                    match expr.Kind with
                    | WorkflowBindExpr(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr "return" [desugarExpr bindExpr]
                    | WorkflowBindExpr(keyword, bindExpr) ->
                        let nextExpr = desugarWorkflowChain rest
                        workflowCall expr keyword [desugarExpr bindExpr; Expression.from expr (Lambda([name, None], nextExpr))]
                    | _ ->
                        let nextExpr = desugarWorkflowChain rest
                        chainBlock statement nextExpr { statement with Kind = DefStatement(isExport, name, typeOpt, desugarExpr expr) }
                | ExprStatement expr ->
                    match expr.Kind with
                    | WorkflowReturnExpr value -> workflowCall expr "return" [desugarExpr value]
                    | WorkflowBindExpr(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr "return" [desugarExpr bindExpr]
                    | WorkflowBindExpr(keyword, bindExpr) ->
                        let nextExpr = desugarWorkflowChain rest
                        workflowCall expr keyword [desugarExpr bindExpr; Expression.from expr (Lambda([("_", None)], nextExpr))]
                    | _ ->
                        let nextExpr = desugarWorkflowChain rest
                        chainBlock statement nextExpr { statement with Kind = ExprStatement (desugarExpr expr) }
                | UseStatement _
                | ImportStatement _
                | TypeDefStatement _ ->
                    let nextExpr = desugarWorkflowChain rest
                    chainBlock statement nextExpr (desugarStatement statement)
        // A statement followed by the rest of the chain, as a block made up for them that spans from the statement
        // to the end of the rest. The rest is a made-up `()` with no span when the statement is the last one.
        and chainBlock (statement: Statement) (nextExpr: Expression) (desugared: Statement) =
            let span =
                if SourceSpan.isNone nextExpr.Span then statement.Span
                else SourceSpan.between statement.Span nextExpr.Span
            Expression.at span (Block [ desugared; Statement.from nextExpr (ExprStatement nextExpr) ])
        and desugarBlock statements =
            let rec split prefix remaining =
                match remaining with
                | [] -> List.rev prefix
                | Kind (DefStatement(_, _, _, Kind (WorkflowBindExpr _))) :: _
                | Kind (ExprStatement (Kind (WorkflowBindExpr _))) :: _
                | Kind (ExprStatement (Kind (WorkflowReturnExpr _))) :: _ ->
                    let chainExpr = desugarWorkflowChain remaining
                    List.rev prefix @ [ Statement.from chainExpr (ExprStatement chainExpr) ]
                | stmt :: rest ->
                    split (desugarStatement stmt :: prefix) rest
            split [] (statements |> List.collect liftStatementBinds)

        module'
        |> List.map (function
            | Kind (Def (ValueDef(isExport, name, typeOpt, expr))) as item ->
                { item with Kind = Def (ValueDef(isExport, name, typeOpt, desugarExpr expr)) }
            | Kind (Expr expr) as item -> { item with Kind = Expr (desugarExpr expr) }
            | other -> other)

    /// The attribute that marks a definition as the program's entry point: `@entry def main = { ... }`.
    let entryAttribute = "entry"

    /// The standard-library module the entry point runs inside: `@entry` is called by the runtime with its
    /// `Console` instance, so the module that declares the entry point sees `nanyx/console` without importing it.
    let entryContextModule = StandardLibrary.importPrefix + "console"

    let private importsModule (moduleName: string) (module': Module) =
        collectImports module'
        |> List.exists (fun item ->
            match item.Source with
            | ModuleImport name -> name = moduleName
            | PathImport _ -> false)

    /// Resolves `@attribute` lines. `@entry` on a definition appends a call to that definition at the end
    /// of the module, so it runs as the program's start function, and imports `nanyx/console` for the module
    /// (the context the runtime supplies to the entry point); any other attribute, or an attribute that does
    /// not precede a value definition, is an error.
    let applyAttributes (module': Module) : Result<Module, Diagnostic list> =
        let rec loop (remaining: TopLevelItem list) (acc: TopLevelItem list) (entryCalls: TopLevelItem list) (errors: Diagnostic list) =
            match remaining with
            | [] -> List.rev acc, List.rev entryCalls, List.rev errors
            | Kind (Attribute name) as attribute :: rest when name <> entryAttribute ->
                loop rest acc entryCalls (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"Unknown attribute '@{name}'" :: errors)
            | Kind (Attribute _) :: (Kind (Def (ValueDef(_, name, _, value))) as def) :: rest ->
                // The call is made up for the def, and reported there.
                let call = TopLevelItem.at def.Span (Expr (Expression.from value (FunctionCall(name, None, []))))
                loop rest (def :: acc) (call :: entryCalls) errors
            | Kind (Attribute name) as attribute :: rest ->
                loop rest acc entryCalls (Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span $"Attribute '@{name}' must be followed by a definition" :: errors)
            | item :: rest -> loop rest (item :: acc) entryCalls errors
        match loop module' [] [] [] with
        | items, entryCalls, [] ->
            let entryImport =
                if not entryCalls.IsEmpty && not (importsModule entryContextModule items) then
                    [ TopLevelItem.synthetic (Import [ { Source = ModuleImport entryContextModule; Alias = None; Span = SourceSpan.none } ]) ]
                else
                    []
            Ok (entryImport @ items @ entryCalls)
        | _, _, errors -> Error errors

    /// Applies the substitution found by unification to everything inference produced.
    /// A definition's type is reported with the rest variables that only stand for tags it gives out closed (see
    /// `Unifier.outputOnlyRestVars`): a union widened by a call, as a `proc` block's is by each `try`, is left
    /// open for more tags, but no more can come once the definition is done.
    let private resolveTypes (subst: Subst) (types: Map<string, Ty>) (typeDefs: Map<string, TypeDefInfo>) (items: TypedTopLevelItem list) =
        let closeOutputRests ty =
            let closed = Unifier.outputOnlyRestVars ty |> List.map (fun v -> v.Id, TyUnion []) |> Map.ofList
            if closed.IsEmpty then ty else Unifier.apply closed ty
        let resolvedTypes = types |> Map.map (fun _ ty -> Unifier.apply subst ty |> closeOutputRests)
        let resolvedTypeDefs =
            typeDefs
            |> Map.map (fun _ info -> { info with Underlying = Unifier.apply subst info.Underlying })
        let rec applyTypedExpr (typedExpr: TypedExpr) =
            { typedExpr with
                Type = Unifier.apply subst typedExpr.Type
                Body = typedExpr.Body |> Option.map applyTypedExpr
                Statements = typedExpr.Statements |> Option.map (List.map applyTypedStatement)
                MatchArms = typedExpr.MatchArms |> Option.map (List.map applyTypedMatchArm)
                Arguments = typedExpr.Arguments |> Option.map (List.map applyTypedExpr) }
        and applyTypedStatement statement =
            match statement with
            | TypedDefStatement(isExport, name, typeOpt, expr) ->
                TypedDefStatement(isExport, name, typeOpt, applyTypedExpr expr)
            | TypedExprStatement expr -> TypedExprStatement (applyTypedExpr expr)
            | TypedUseStatement(binding, exprOpt) ->
                TypedUseStatement(binding, exprOpt |> Option.map applyTypedExpr)
            | TypedImportStatement _
            | TypedTypeDefStatement _ -> statement
        and applyTypedMatchArm (patterns, expr) =
            let mappedPatterns =
                patterns |> List.map (fun pattern -> { pattern with Type = Unifier.apply subst pattern.Type })
            (mappedPatterns, applyTypedExpr expr)
        let resolvedItems =
            items
            |> List.map (fun item ->
                match item with
                | TypedExprItem expr -> TypedExprItem (applyTypedExpr expr)
                | TypedDef (TypedValueDef(isExport, name, typeOpt, expr)) ->
                    TypedDef (TypedValueDef(isExport, name, typeOpt, applyTypedExpr expr))
                | TypedDef (TypedTypeDef _)
                | TypedImport _
                | TypedModuleDecl _ -> item)
        resolvedTypes, resolvedTypeDefs, resolvedItems

    /// The module as the backend will see it: the typed items turned back into syntax, with each pipe to an
    /// attached function written with the name it resolved to (`pipeTargets`), each read of a built-in property as a
    /// call (`properties`), and without import statements.
    let private moduleFromTypedItems (pipeTargets: Map<NodeId, Identifier>) (properties: Map<NodeId, BuiltInProperty>) (items: TypedTopLevelItem list) : Module =
        let lower (expr: Expression) = expr |> retargetPipes pipeTargets |> callBuiltInProperties properties
        items
        |> List.choose (function
            | TypedModuleDecl name -> Some (ModuleDecl name)
            | TypedImport _ -> None
            | TypedDef (TypedValueDef(isExport, name, typeOpt, expr)) ->
                Some (Def (ValueDef(isExport, name, typeOpt, lower expr.Expr)))
            | TypedDef (TypedTypeDef(name, modifiers, parameters, body)) ->
                Some (Def (TypeDef(name, modifiers, parameters, body)))
            | TypedExprItem expr -> Some (Expr (lower expr.Expr)))
        |> List.map TopLevelItem.synthetic

    let private originKey (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> fullPath
        | StandardLibraryModule name -> StandardLibrary.importPrefix + name
        | InMemory -> "<source>"

    /// The file a module's syntax nodes are reported in.
    let private sourceFileName (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> fullPath
        | StandardLibraryModule name -> StandardLibrary.importPrefix + name + ".nyx"
        | InMemory -> ""

    /// The modules one compilation has checked, by `originKey`, so that a module imported along several paths is
    /// checked once and every importer sees the same symbols.
    type private CheckedModules = System.Collections.Generic.Dictionary<string, Result<CheckedModule, CompilationPhase * Diagnostic list>>

    /// Parses, desugars and type-checks one module, resolving and checking the modules it imports first. An
    /// error says which phase it came from.
    let rec private typecheckModule (checkedModules: CheckedModules) (origin: ModuleOrigin) (source: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        let resolved =
            parseWithDiagnostics (sourceFileName origin) source
            |> Result.mapError (fun diagnostics -> Parsed, diagnostics)
            |> Result.bind (fun (ast, declared, moduleBlocks) ->
                applyAttributes (desugar ast)
                |> Result.map (fun desugared -> ast, declared, moduleBlocks, desugared)
                |> Result.mapError (fun diagnostics -> Desugared, diagnostics))
        match resolved with
        | Result.Error failure -> Error failure
        | Result.Ok (ast, declared, moduleBlocks, desugared) ->
            let preludeKey = originKey (StandardLibraryModule StandardLibrary.preludeModuleName)
            // Every module but the prelude imports it first, so that anything it imports or defines itself wins.
            // An import of a `module name =` block of this file is not of another file: inference resolves it.
            let blockImported = NyxCompiler.Infer.blockImports moduleBlocks desugared |> List.map _.Import
            let fileImports = collectImports desugared |> List.filter (fun item -> not (List.contains item blockImported))
            let imports =
                if key = preludeKey then
                    fileImports
                else
                    { Source = ModuleImport (StandardLibrary.importPrefix + StandardLibrary.preludeModuleName); Alias = None; Span = SourceSpan.none }
                    :: fileImports
            let mutable env =
                match origin with
                | StandardLibraryModule _ -> Intrinsics.typeEnv
                | SourceFile _
                | InMemory -> TypeEnv.empty
            let mutable state = { NyxCompiler.Infer.emptyState with Declared = declared; ModuleBlocks = moduleBlocks }
            let mutable errors: Diagnostic list = []
            let mutable inlined: TopLevelItem list = []
            // Kept apart and inlined last, so that where an explicit import defines the same name, its definition is kept.
            let mutable preludeInlined: TopLevelItem list = []
            let mutable preludeTypeNames: Set<string> = Set.empty
            let mutable inlinedTypes: Map<string, Ty> = Map.empty
            // Where the calls in the imported modules' definitions, which are inlined, find their instances.
            let mutable inlinedInstances: Map<NodeId, ContextInstance option list> = Map.empty
            // The modules that bring each name in unqualified, by the module's key, with the name of the module.
            // The prelude is not one of them: a name it shares with an import is the import's, not ambiguous.
            let mutable unqualifiedSources: Map<string, (string * string) list> = Map.empty
            let mergeImport (importedKey: string) (item: ImportItem) (imported: CheckedModule) =
                let qualifier = importQualifier item
                let exportedValues = exportedValueNames imported.Own
                if qualifier.IsNone && importedKey <> preludeKey then
                    let moduleName =
                        match imported.Name, item.Source with
                        | Some name, _ -> name
                        | None, ModuleImport name -> name
                        | None, PathImport path -> path
                    for name in exportedValues do
                        let sources = unqualifiedSources |> Map.tryFind name |> Option.defaultValue []
                        if not (sources |> List.exists (fun (key, _) -> key = importedKey)) then
                            unqualifiedSources <- unqualifiedSources |> Map.add name (sources @ [ importedKey, moduleName ])
                let exportedTypes = exportedTypeNames imported.Own
                let exportedValueTypes =
                    imported.Typed.Types |> Map.filter (fun name _ -> exportedValues.Contains name)
                let exportedTypeDefs =
                    imported.Typed.TypeDefs |> Map.filter (fun name _ -> exportedTypes.Contains name)
                // The exported definitions, declared in the imported module's file.
                let exportedSymbols =
                    imported.Exports
                    |> Map.filter (fun name _ -> exportedValues.Contains name)
                    |> Map.values
                    |> Seq.map (fun symbol -> symbol.Id, symbol)
                    |> Map.ofSeq
                // The exported types and their contexts' members, declared in the imported module's file too.
                let exportedTypeSymbols =
                    exportedTypeDefs
                    |> Map.values
                    |> Seq.collect (fun info -> Option.toList info.Symbol @ (info.Members |> Map.values |> List.ofSeq))
                    |> Seq.choose (fun id -> imported.Typed.Symbols |> Map.tryFind id)
                    |> Seq.map (fun symbol -> symbol.Id, symbol)
                    |> Map.ofSeq
                env <- extendEnvWithImport env qualifier exportedValueTypes (imported.Exports |> Map.map (fun _ symbol -> symbol.Id))
                // The import is a symbol of its own, unless the compiler made it up (for `@entry`).
                let importSymbols, importedThrough =
                    if SourceSpan.isNone item.Span then
                        Map.empty, state.ImportedThrough
                    else
                        let name =
                            match item.Alias, item.Source with
                            | Some alias, _ -> alias
                            | None, ModuleImport moduleName -> moduleName
                            | None, PathImport path -> path
                        let import = { Id = SymbolId.next (); Name = name; Kind = ImportedModule; Declaration = item.Span; IsExported = false }
                        // Only an alias is written as a name: an import without one has no name to write.
                        if item.Alias.IsSome then
                            state <- { state with Scopes = state.Scopes |> Map.add import.Id (Scope.wholeModule item.Span.File) }
                        Map.ofList [ import.Id, import ],
                        exportedSymbols |> Map.fold (fun acc id _ -> acc |> Map.add id import.Id) state.ImportedThrough
                // A type an explicit import exports replaces the prelude's type of the same name.
                let typeDefs =
                    if importedKey = preludeKey then
                        preludeTypeNames <- Set.ofSeq exportedTypeDefs.Keys
                        state.TypeDefs
                    else
                        state.TypeDefs |> Map.filter (fun name _ -> not (preludeTypeNames.Contains name && exportedTypeDefs.ContainsKey name))
                // An aliased import's types are written with the alias (`m.$Email`). Their own names stay known for
                // the signatures that name them, but cannot be written unless something else brings them in. An
                // unaliased import's types can, and replace a type only an aliased import brought in.
                let typeDefs, hiddenTypeNames =
                    match qualifier with
                    | Some qualifier ->
                        let qualified = exportedTypeDefs |> Map.fold (fun acc name info -> acc |> Map.add $"{qualifier}.{name}" info) typeDefs
                        let hidden = exportedTypeDefs.Keys |> Seq.filter (fun name -> not (typeDefs.ContainsKey name)) |> Set.ofSeq
                        qualified, Set.union state.HiddenTypeNames hidden
                    | None ->
                        typeDefs |> Map.filter (fun name _ -> not (state.HiddenTypeNames.Contains name && exportedTypeDefs.ContainsKey name)),
                        state.HiddenTypeNames - Set.ofSeq exportedTypeDefs.Keys
                // A function that requires a context requires it wherever it is called, under the name it is imported as.
                let contextRequirements =
                    imported.ContextRequirements
                    |> Map.fold (fun acc name contexts ->
                        let importedName =
                            match qualifier with
                            | Some qualifier -> $"{qualifier}.{name}"
                            | None -> name
                        acc |> Map.add importedName contexts) state.ContextRequirements
                let contextSignatures =
                    imported.ContextSignatures
                    |> Map.fold (fun acc name signature ->
                        let importedName =
                            match qualifier with
                            | Some qualifier -> $"{qualifier}.{name}"
                            | None -> name
                        acc |> Map.add importedName signature) state.ContextSignatures
                let typeDefs = mergeTypeDefs typeDefs exportedTypeDefs
                // The ambient contexts the imported functions require that this module cannot name are carried in
                // with their definitions, hidden, so a call to one is checked as a call needing the context is here.
                let carried =
                    let rec contextNames (ctx: TypeExpr) =
                        match ctx.Kind with
                        | TypeContext contexts -> contexts |> List.collect contextNames
                        | TypeName name
                        | TypeApply(name, _) -> [ name ]
                        | _ -> []
                    imported.ContextRequirements
                    |> Map.values
                    |> Seq.collect (List.collect contextNames)
                    |> Seq.distinct
                    |> Seq.filter (fun name -> not (typeDefs.ContainsKey name) && not (state.ContextDefs.ContainsKey name))
                    |> Seq.choose (fun name -> imported.AmbientContexts |> Map.tryFind name |> Option.map (fun info -> name, info))
                    |> List.ofSeq
                state <-
                    { state with
                        TypeDefs = carried |> List.fold (fun acc (name, info) -> acc |> Map.add name info) typeDefs
                        HiddenTypeNames = carried |> List.fold (fun acc (name, _) -> acc |> Set.add name) hiddenTypeNames
                        CarriedAmbientContexts = carried |> List.fold (fun acc (name, _) -> acc |> Set.add name) state.CarriedAmbientContexts
                        Symbols = Seq.fold (fun acc (KeyValue(id, symbol)) -> acc |> Map.add id symbol) state.Symbols (Seq.concat [ exportedSymbols; exportedTypeSymbols; importSymbols ])
                        ImportedThrough = importedThrough
                        // Where each definition is imported, since a `module name =` block's imports are its own.
                        ImportSites =
                            Seq.append exportedSymbols exportedTypeSymbols
                            |> Seq.fold (fun acc (KeyValue(id, _)) ->
                                acc |> Map.add id (item.Span :: (acc |> Map.tryFind id |> Option.defaultValue []))) state.ImportSites
                        ContextRequirements = contextRequirements
                        ContextSignatures = contextSignatures }
                if importedKey = preludeKey then
                    preludeInlined <- inlinedItemsForImport item imported
                else
                    inlined <- inlined @ inlinedItemsForImport item imported
                inlinedTypes <- inlinedTypesForImport item imported |> Map.fold (fun acc name ty -> acc |> Map.add name ty) inlinedTypes
                inlinedInstances <- imported.Typed.ContextInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) inlinedInstances
            for item in imports do
                // An import of a module that is still being checked, because it imports this one, is a cycle.
                let checkImport (importOrigin: ModuleOrigin) (readSource: unit -> string) =
                    let importKey = originKey importOrigin
                    if importKey = key || visited.Contains importKey then
                        Error (Typed, [ Diagnostics.at DiagnosticCodes.ImportCycle item.Span $"Import cycle detected for {importKey}" ])
                    else
                        typecheckImport checkedModules importOrigin readSource (visited.Add key)
                match StandardLibrary.tryModuleName item with
                | Some moduleName ->
                    match StandardLibrary.tryLoadSource moduleName with
                    | None ->
                        let available = String.concat ", " (StandardLibrary.moduleNames |> List.map (fun name -> StandardLibrary.importPrefix + name))
                        errors <- errors @ [ Diagnostics.at DiagnosticCodes.UnknownStandardLibraryModule item.Span $"Unknown standard library module '{StandardLibrary.importPrefix}{moduleName}' (available: {available})" ]
                    | Some moduleSource ->
                        match checkImport (StandardLibraryModule moduleName) (fun () -> moduleSource) with
                        | Error (_, diags) -> errors <- errors @ diags
                        | Ok imported when imported.Name <> Some moduleName ->
                            let declared = imported.Name |> Option.defaultValue "(none)"
                            errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule item.Span $"Standard library module '{StandardLibrary.importPrefix}{moduleName}' declares the module name '{declared}'" ]
                        | Ok imported -> mergeImport (originKey (StandardLibraryModule moduleName)) item imported
                | None ->
                    match origin with
                    | SourceFile fullPath ->
                        match resolveImportPath (Path.GetDirectoryName fullPath) item with
                        | Error diag -> errors <- errors @ [ diag ]
                        | Ok importPath ->
                            match checkImport (SourceFile importPath) (fun () -> File.ReadAllText importPath) with
                            | Error (_, diags) -> errors <- errors @ diags
                            | Ok imported -> mergeImport (originKey (SourceFile importPath)) item imported
                    | StandardLibraryModule name ->
                        errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule item.Span $"Standard library module '{name}' may only import other standard library modules" ]
                    | InMemory ->
                        let shown =
                            match item.Source with
                            | PathImport path -> path
                            | ModuleImport name -> name
                        errors <- errors @ [ Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"Import not found: {shown} (only standard library modules can be imported here)" ]
            // A name two imports bring in is an error only where it is used, and only if nothing nearer binds it.
            let ambiguousImports =
                unqualifiedSources
                |> Map.toSeq
                |> Seq.choose (fun (name, sources) ->
                    match sources, TypeEnv.tryFind name env |> Option.bind (fun scheme -> scheme.Symbol) with
                    | _ :: _ :: _, Some symbol -> Some (name, (symbol, sources |> List.map (fun (_, moduleName) -> $"{moduleName}.{name}")))
                    | _ -> None)
                |> Map.ofSeq
            state <- { state with AmbiguousImports = ambiguousImports }
            if not errors.IsEmpty then
                // A module imported along several paths fails each importer with the same diagnostics.
                Error (Typed, List.distinct errors)
            else
                match NyxCompiler.Infer.inferModuleWithEnv env state desugared with
                | Error diagnostics -> Error (Typed, diagnostics)
                | Ok (types, items, (state: NyxCompiler.Infer.InferState)) ->
                    match Unifier.unify state.Constraints with
                    | Error (message, span) -> Error (Typed, [ Diagnostics.at DiagnosticCodes.TypeMismatch span message ])
                    | Ok subst ->
                        let resolvedTypes, resolvedTypeDefs, resolvedItems = resolveTypes subst types state.TypeDefs items
                        let own = moduleFromTypedItems state.PipeTargets state.BuiltInProperties resolvedItems |> writeBlockAliases state
                        // The module's own definitions win over imported ones of the same name.
                        let ownNames = exportedValueNames own |> Set.union (own |> List.choose (function Kind (Def (ValueDef(false, name, _, _))) -> Some name | _ -> None) |> Set.ofList)
                        let inlined =
                            inlined @ preludeInlined
                            |> List.filter (function
                                | Kind (Def (ValueDef(_, name, _, _))) -> not (ownNames.Contains name)
                                | _ -> true)
                            |> distinctDefinitions
                            |> reachedItems own
                        // The names the imports and the prelude bring in, as written (`foo`, or `m.foo` under an
                        // alias), each meaning the definition the environment settled on (an explicit import's over
                        // the prelude's), less the names the module declares at the top level itself.
                        let ownTopLevel =
                            state.Scopes
                            |> Map.keys
                            |> Seq.map (fun id -> state.Symbols.[id])
                            |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue)
                            |> List.ofSeq
                        let declaredHere = ownTopLevel |> List.map (fun symbol -> symbol.Name) |> Set.ofList
                        let imported =
                            env
                            |> Map.toList
                            |> List.choose (fun (name, scheme) ->
                                match scheme.Symbol with
                                | Some id when state.Symbols.ContainsKey id && not (declaredHere.Contains name) -> Some (name, id, scheme.Type)
                                | _ -> None)
                        // Only the undotted ones are in scope as names: a dotted one is reached through its prefix.
                        let scopes =
                            imported
                            |> List.filter (fun (name, _, _) -> not (name.Contains '.'))
                            |> List.fold (fun acc (_, id, _) -> acc |> Map.add id (Scope.wholeModule (sourceFileName origin))) state.Scopes
                        // A def a `module name =` block does not export can only be named inside the block.
                        let keptToItsBlock (symbol: Symbol) =
                            not symbol.IsExported
                            && state.ModuleBlocks |> List.exists (fun (_, block) -> SourceSpan.contains block symbol.Declaration)
                        let moduleNames =
                            ownTopLevel
                            |> List.filter (keptToItsBlock >> not)
                            |> List.fold (fun acc symbol -> acc |> Map.add symbol.Name symbol.Id) (imported |> List.map (fun (name, id, _) -> name, id) |> Map.ofList)
                        Ok { Typed =
                                { Module = inlined @ own
                                  // The types of the imported definitions inlined for the backend, then the module's own.
                                  Types = resolvedTypes |> Map.fold (fun acc name ty -> acc |> Map.add name ty) inlinedTypes
                                  TypeDefs = resolvedTypeDefs
                                  Items = resolvedItems
                                  // Filled in for the root module only, by `compileWith`.
                                  ExprTypes = Map.empty
                                  CalleeTypes = Map.empty
                                  SymbolTypes = Map.empty
                                  Symbols = state.Symbols
                                  AnnotatedSymbols = state.AnnotatedSymbols
                                  References = state.References
                                  ImportReferences = state.ImportReferences
                                  Scopes = scopes
                                  ModuleNames = moduleNames
                                  ImportedTypes = imported |> List.map (fun (_, id, ty) -> id, ty) |> Map.ofList
                                  ContextInstances = state.ContextInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) inlinedInstances }
                             Own = own
                             Inlined = inlined
                             Name = declaredModuleName desugared
                             Syntax = ast
                             Subst = subst
                             RawExprTypes = state.ExprTypes
                             RawCalleeTypes = state.CalleeTypes
                             RawSymbolTypes = state.SymbolTypes
                             // Only declarations the module made itself are in `DeclarationSymbols`.
                             Exports =
                                state.DeclarationSymbols
                                |> Map.values
                                |> Seq.map (fun id -> state.Symbols.[id])
                                |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue && symbol.IsExported)
                                |> Seq.map (fun symbol -> symbol.Name, symbol)
                                |> Map.ofSeq
                             ContextRequirements =
                                let exported = exportedValueNames own
                                state.ContextRequirements |> Map.filter (fun name _ -> exported.Contains name)
                             ContextSignatures =
                                let exported = exportedValueNames own
                                state.ContextSignatures |> Map.filter (fun name _ -> exported.Contains name)
                             AmbientContexts =
                                state.AmbientMembers
                                |> Map.values
                                |> Seq.map snd
                                |> Set.ofSeq
                                |> Set.union state.CarriedAmbientContexts
                                |> Seq.choose (fun name -> state.TypeDefs |> Map.tryFind name |> Option.map (fun info -> name, info))
                                |> Map.ofSeq }

    /// An imported module, checked the first time this compilation imports it; its source is only read then.
    and private typecheckImport (checkedModules: CheckedModules) (origin: ModuleOrigin) (readSource: unit -> string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        match checkedModules.TryGetValue key with
        | true, checkedModule -> checkedModule
        | false, _ ->
            let checkedModule = typecheckModule checkedModules origin (readSource ()) visited
            checkedModules.[key] <- checkedModule
            checkedModule

    let parse (source: string) : Result<Module, Diagnostic list> =
        match parseModuleFileWithNames "" source with
        | Result.Ok (ast, _, _) -> Ok ast
        | Result.Error err -> Error [ syntaxError err ]

    /// Checks the module from `origin`, then runs the analyzers `options` names over it if it type-checked. Only
    /// the module being compiled is analyzed: an imported module is analyzed when it is compiled itself, so its
    /// diagnostics are not repeated in every module that imports it, and the standard library never is.
    let private compileFrom (options: AnalyzerOptions) (origin: ModuleOrigin) (source: string) : CompileResult =
        match typecheckModule (CheckedModules()) origin source Set.empty with
        | Result.Error (phase, diagnostics) ->
            { Phase = phase
              Diagnostics = diagnostics
              Typed = None
              Syntax = None }
        | Result.Ok checkedModule ->
            // Types by node are only read for the root module, so only its are resolved: not those of every
            // imported module (the standard library included) on every compile.
            let resolve = Map.map (fun _ ty -> Unifier.apply checkedModule.Subst ty)
            let typed =
                { checkedModule.Typed with
                    ExprTypes = resolve checkedModule.RawExprTypes
                    CalleeTypes = resolve checkedModule.RawCalleeTypes
                    SymbolTypes = resolve checkedModule.RawSymbolTypes }
            // A rule configured as an error fails the compile as a compiler error does: callers that stop at
            // errors (`nanyx run`, say) look at the diagnostics, and the typed module is still there for the
            // editor's features.
            { Phase = Typed
              Diagnostics = Analyzers.run options (SemanticModel.ofTyped source checkedModule.Syntax typed)
              Typed = Some typed
              Syntax = Some checkedModule.Syntax }

    /// `compileFile` with the analyzers and severities `options` gives.
    let compileFileWith (options: AnalyzerOptions) (filePath: string) : CompileResult =
        if not (File.Exists filePath) then
            { Phase = Parsed
              Diagnostics = [ Diagnostics.general DiagnosticCodes.FileNotFound $"File not found: {filePath}" ]
              Typed = None
              Syntax = None }
        else
            compileFrom options (SourceFile (Path.GetFullPath filePath)) (File.ReadAllText filePath)

    /// Compiles the file at `filePath` up to the typed module, resolving its imports (standard-library modules
    /// from the embedded library, everything else relative to the file), and runs every analyzer over it.
    let compileFile (filePath: string) : CompileResult =
        compileFileWith Analyzers.defaultOptions filePath

    /// `compileSource` with the analyzers and severities `options` gives.
    let compileSourceWith (options: AnalyzerOptions) (filePath: string) (source: string) : CompileResult =
        compileFrom options (SourceFile (Path.GetFullPath filePath)) source

    /// Compiles `source` as if it were the contents of the file at `filePath` (which need not exist, or may hold
    /// older text: an editor's unsaved buffer, say). Imports are resolved exactly as `compileFile` resolves them.
    let compileSource (filePath: string) (source: string) : CompileResult =
        compileSourceWith Analyzers.defaultOptions filePath source

    /// `compile` with the analyzers and severities `options` gives.
    let compileWith (options: AnalyzerOptions) (source: string) : CompileResult =
        compileFrom options InMemory source

    /// Compiles source given directly, up to the typed module, and runs every analyzer over it. Standard-library
    /// imports are resolved; imports by path cannot be, since there is no file for them to be relative to.
    let compile (source: string) : CompileResult =
        compileWith Analyzers.defaultOptions source
