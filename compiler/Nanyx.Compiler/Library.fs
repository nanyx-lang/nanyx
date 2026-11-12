namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open NyxCompiler.ExpressionRewrites

type CompilationPhase =
    | Parsed
    | Desugared
    | Typed
    | Lowered
    | Emitted

type CompileResult =
    { Phase: CompilationPhase
      Diagnostics: Diagnostic list
      /// The module typed with what did check: present when it parsed and its imports checked, even if it has type
      /// or name errors of its own (see `TypeChecked`).
      Typed: TypedModule option
      /// Whether the module type-checked: it has no type or name errors. A linter rule configured as an error does
      /// not stop it type-checking.
      TypeChecked: bool
      /// Whether each type or name error in the module is compiled to a trap in `Typed` where it was found, so that a
      /// debug build can run it: the trap writes the error and stops the program if it is reached. True when there
      /// are none.
      ErrorsDeferred: bool
      /// The root module as parsed, before desugaring: the syntax the user wrote, whose nodes the typed module's
      /// `ExprTypes` and `CalleeTypes` are keyed by. Present when `Typed` is.
      Syntax: Module option }

/// A type-checkedModule module together with what an importer needs from it.
type private CheckedModule =
    { Typed: TypedModule
      /// The errors found checking the module itself. A module with errors is still typed, with what did check,
      /// for the editor's features; it cannot be run or imported.
      Errors: Diagnostic list
      /// The warnings found checking the module itself, which do not stop it being run or imported: each deprecated
      /// spelling of a built-in type (see `DeprecatedTypeNames`).
      Warnings: Diagnostic list
      /// Those of `Errors` that are not compiled to a trap in `Own`, so a debug build cannot run the module.
      Undeferred: Diagnostic list
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

/// Where in a builder's block a workflow keyword is when the rest of the block cannot be its continuation.
type Unreachable =
    /// Inside a lambda, which runs when it is called, not where it is written.
    | InLambda
    /// In the body of a `for` loop, which runs once per element.
    | InLoopBody
    /// In a part of an expression that may not run: a branch of an `if` or an arm of a `match` that is part of a
    /// larger expression, or the right of `and` or `or`.
    | InConditionalPart

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

    /// Renames every reference to a name in `renames` throughout an expression. Used when a module is
    /// inlined into its importer, so that its definitions keep pointing at each other under their new names.
    /// A name that inference resolved to a parameter, a local definition or a pattern variable of the module
    /// (`typed`) shadows the top-level definition, so it keeps its name.
    let private renameReferencesInExpr (typed: TypedModule) (renames: Map<string, string>) (expr: Expression) : Expression =
        let isShadowed (node: Expression) =
            typed.References |> Map.tryFind node.Id |> Option.bind (fun id -> typed.Symbols |> Map.tryFind id)
            |> Option.exists (fun symbol -> symbol.Kind = Parameter || symbol.Kind = LocalValue || symbol.Kind = PatternVariable)
        let rename (node: Expression) (name: string) =
            if isShadowed node then name else renames |> Map.tryFind name |> Option.defaultValue name
        expr
        |> mapExpressionKinds (fun current kind ->
            match kind with
            | IdentifierExpr name -> IdentifierExpr(rename current name)
            | FunctionCall(name, rangeOpt, args) -> FunctionCall(rename current name, rangeOpt, args)
            | Pipe(value, name, rangeOpt, args) -> Pipe(value, rename current name, rangeOpt, args)
            | other -> other)

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
        | Except(value, arms) -> referencedNames { expr with Kind = Match([ value ], arms) }
        | TupleExpr items
        | ListExpr items -> items |> List.collect referencedNames
        | RecordExpr fields ->
            fields |> List.collect (function NamedField(_, value) | PositionalField value -> referencedNames value)
        | TagExpr(_, payload) -> payload |> Option.map referencedNames |> Option.defaultValue []
        | MemberAccess(inner, memberName, _) ->
            $".{memberName}" :: (dottedName expr |> Option.toList) @ referencedNames inner
        | UseIn(value, body) -> referencedNames value @ referencedNames body
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
        | UseStatement value -> referencedNames value
        | ImportStatement _
        | TypeDefStatement _ -> []

    and private referencedNamesInPattern (pattern: Pattern) : string list =
        match pattern.Kind with
        | GuardPattern predicate -> referencedNames predicate
        | RangePattern(startValue, endValue) -> referencedNames startValue @ referencedNames endValue
        | TagPattern(_, Some payload) -> referencedNamesInPattern payload
        | TuplePattern patterns
        | AndPattern patterns
        | ListPattern(patterns, _) -> patterns |> List.collect referencedNamesInPattern
        | ListSplatMiddle(before, after) -> (before @ after) |> List.collect referencedNamesInPattern
        | RecordPattern(_, patterns) -> patterns |> List.collect referencedNamesInPattern
        | RecordMemberPattern(members, _) -> members |> List.collect (snd >> referencedNamesInPattern)
        | TagPattern(_, None)
        | LiteralPattern _
        | IdentifierPattern _
        | WildcardPattern
        | UnitPattern
        | ElsePattern -> []

    /// The inlined items `own` uses: the value definitions it reaches, directly or through other inlined
    /// definitions, and every type definition, in their original order. A definition named like a member of a
    /// context is always kept, because the backend supplies it as the ambient instance of that context (the
    /// runtime's `Console`, say) without anything naming it. So is each attached definition an instance the type
    /// checker built for a call in `own`, or in a definition kept, is made of (`instances`, by the call's node), as
    /// `int.toString` for a `ToString(int)`: the backend passes it where the instance is required, unnamed.
    let private reachedItems (instances: Map<NodeId, ContextInstance option list>) (own: Module) (inlined: TopLevelItem list) : TopLevelItem list =
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
        // The members of the contexts without parameters, which an import under an alias defines under it (`c.write`).
        let plainContextMembers =
            inlined @ own
            |> List.collect (function
                | Kind (Def (TypeDef(_, modifiers, [], Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
                    fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
                | _ -> [])
            |> Set.ofList
        let rootNames =
            own
            |> List.collect (function
                | Kind (Def (ValueDef(_, _, _, value))) | Kind (Expr value) -> referencedNames value @ synthesizedMembersIn instances value
                | _ -> [])
        let isReferenced (referenced: Set<string>) (name: string) =
            referenced.Contains name
            || contextMembers.Contains name
            || (name.Contains "." && referenced.Contains (name.Substring(name.LastIndexOf '.')))
            || (name.Contains "." && plainContextMembers.Contains (name.Substring(name.LastIndexOf '.' + 1)))
        let rec reach (referenced: Set<string>) (reached: Set<string>) =
            let newlyReached =
                bodies |> List.filter (fun (name, _) -> not (reached.Contains name) && isReferenced referenced name)
            if newlyReached.IsEmpty then
                reached
            else
                let referenced = newlyReached |> List.collect (fun (_, value) -> referencedNames value @ synthesizedMembersIn instances value) |> List.fold (fun acc name -> Set.add name acc) referenced
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
                    Some { item with Kind = Def (ValueDef(false, rename name, typeOpt, renameReferencesInExpr checkedModule.Typed renames expr)) }
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

    /// Desugars `module'`, with the errors in what it desugars: a workflow keyword the rest of its builder's block
    /// cannot follow (see `Unreachable`).
    /// `module'` desugared, with the errors desugaring found, and where each builder block falls off its end without
    /// `return`: by the node of the made-up read of the builder's `zero` that is the block's value there, the place
    /// in the block to report a missing `zero` at.
    let desugarWithDiagnostics (module': Module) : Module * Diagnostic list * Map<NodeId, SourceSpan> =
        let isReturnKeyword keyword = keyword = "return"
        let diagnostics = ResizeArray<Diagnostic>()
        let mutable zeros: Map<NodeId, SourceSpan> = Map.empty

        // The builder whose block is being desugared (`proc` in `proc { ... }`), and whether that block used a
        // workflow keyword. A keyword `k` in the block calls the builder's member `k`, `return` its member `return`.
        let mutable builder: string option = None
        let mutable builderUsed = false

        // Where the expression being desugared is, when the rest of the builder's block cannot follow a keyword
        // there: inside a lambda, which runs when it is called; in the body of a `for` loop, which runs once per
        // element; or in a part of an expression that may not run, as a branch of an `if` that is not a statement
        // of its own. A builder block nested there starts afresh. The outermost place is the one that says why: a `try`
        // in an arm of a `for` loop's patterns is in the loop's body, whatever else it is in.
        let mutable unreachable: Unreachable option = None
        let within (place: Unreachable) (desugarIt: unit -> 'a) : 'a =
            let outer = unreachable
            unreachable <- outer |> Option.orElse (Some place)
            let result = desugarIt ()
            unreachable <- outer
            result

        // The error for the keyword `keyword` at `expr`, which the rest of the block of the builder `builderName`
        // cannot follow from `place`.
        let reportUnreachable (expr: Expression) (keyword: string) (builderName: string) (place: Unreachable) =
            let where, advice =
                match place with
                | InLambda -> "inside a lambda", "move it out of the lambda, or handle the value with a `match` there"
                | InLoopBody -> "in the body of a `for` loop", "handle the value with a `match` in the loop's body"
                | InConditionalPart -> "in a part of an expression that may not run", "make the `if` or `match` it is in a statement of its own"
            diagnostics.Add(
                Diagnostics.at DiagnosticCodes.InvalidWorkflow expr.Span
                    $"`{keyword}` is {where}, so the rest of the `{builderName}` block cannot follow it: {advice}")
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
            | Except(value, arms) ->
                let prefix, value' = liftBinds value
                prefix, rebuild (Except(value', arms))
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

        // Whether `expr` uses a workflow keyword of the block it is in: one not inside a lambda, or inside a builder
        // block of its own.
        let rec usesKeyword (expr: Expression) : bool =
            let inValue (statement: Statement) =
                match statement.Kind with
                | DefStatement(_, _, _, value)
                | ExprStatement value
                | UseStatement value -> usesKeyword value
                | ImportStatement _
                | TypeDefStatement _ -> false
            match expr.Kind with
            | WorkflowBindExpr _
            | WorkflowReturnExpr _ -> true
            | Lambda _ -> false
            | FunctionCall(name, _, [ Kind (Lambda([], _)) ]) when not (name.StartsWith "__") -> false
            | FunctionCall(_, _, args)
            | ContextMemberCall(_, _, args) -> List.exists usesKeyword args
            | Pipe(value, _, _, args) -> List.exists usesKeyword (value :: args)
            | BinaryOp(_, left, right) -> usesKeyword left || usesKeyword right
            | IfExpr(cond, thenExpr, elseExpr) -> usesKeyword cond || usesKeyword thenExpr || usesKeyword elseExpr
            | Match(values, arms) -> List.exists usesKeyword values || arms |> List.exists (snd >> usesKeyword)
            | Except(value, arms) -> usesKeyword value || arms |> List.exists (snd >> usesKeyword)
            | Block statements -> List.exists inValue statements
            | TupleExpr items
            | ListExpr items -> List.exists usesKeyword items
            | RecordExpr fields -> fields |> List.exists (function NamedField(_, value) | PositionalField value -> usesKeyword value)
            | TagExpr(_, Some payload) -> usesKeyword payload
            | MemberAccess(baseExpr, _, _) -> usesKeyword baseExpr
            | UseIn(_, body) -> usesKeyword body
            | InterpolatedString parts -> parts |> List.exists (function StringExpr value -> usesKeyword value | StringText _ -> false)
            | TagExpr(_, None)
            | ContextMemberAccess _
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> false

        // Whether `expr` is an `if` or a `match` with a workflow keyword in a branch: as a statement of its own, each
        // branch goes on to the rest of the block (see `desugarBranching`).
        let branchesUseKeywords (expr: Expression) =
            match expr.Kind with
            | IfExpr(_, thenExpr, elseExpr) -> usesKeyword thenExpr || usesKeyword elseExpr
            | Match(_, arms)
            | Except(_, arms) -> arms |> List.exists (snd >> usesKeyword)
            | _ -> false

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
                let outerBuilder, outerUsed, outerUnreachable = builder, builderUsed, unreachable
                builder <- Some name
                builderUsed <- false
                unreachable <- None
                let body' = desugarExpr body
                let used = builderUsed
                builder <- outerBuilder
                builderUsed <- outerUsed
                unreachable <- outerUnreachable
                if used then body'.Kind
                else FunctionCall(name, rangeOpt, [ { lambda with Kind = Lambda([], body') } ])
            // A `for` loop's body is a lambda of the elements, which the loop runs once per step.
            | ForSyntax.Call(iterables, loop, parameters, body) ->
                let body' = within InLoopBody (fun () -> desugarExpr body)
                FunctionCall(ForSyntax.builtIn, None, List.map desugarExpr iterables @ [ { loop with Kind = Lambda(parameters, body') } ])
            | FunctionCall(name, rangeOpt, args) ->
                FunctionCall(name, rangeOpt, args |> List.map desugarExpr)
            | Lambda(args, body) ->
                Lambda(args, within InLambda (fun () -> desugarExpr body))
            // The right of `and` and `or` only runs when the left does not settle the result.
            | BinaryOp(("and" | "or") as op, left, right) ->
                BinaryOp(op, desugarExpr left, within InConditionalPart (fun () -> desugarExpr right))
            | BinaryOp(op, left, right) ->
                BinaryOp(op, desugarExpr left, desugarExpr right)
            | Pipe(value, name, rangeOpt, args) ->
                Pipe(desugarExpr value, name, rangeOpt, args |> List.map desugarExpr)
            | Block statements ->
                Block(desugarBlock statements)
            // An arm, like a branch of an `if`, only runs when it is chosen. One that is a statement of its own is
            // desugared by `desugarBranching` instead when it uses a keyword.
            // A lone `else` in a match on several values becomes one per value (`PatternSpace.ofArm`).
            | Match(exprs, arms) ->
                let mappedExprs = exprs |> List.map desugarExpr
                let mappedArms =
                    arms
                    |> List.map (fun (patterns, expr) ->
                        PatternSpace.ofArm exprs.Length patterns |> List.map (Pattern.mapExpressions desugarExpr),
                        within InConditionalPart (fun () -> desugarExpr expr))
                Match(mappedExprs, mappedArms)
            | Except(value, arms) -> desugarKind { expr with Kind = Except.asMatch expr value arms }
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
                IfExpr(
                    desugarExpr cond,
                    within InConditionalPart (fun () -> desugarExpr thenExpr),
                    within InConditionalPart (fun () -> desugarExpr elseExpr))
            | MemberAccess(baseExpr, field, rangeOpt) -> MemberAccess(desugarExpr baseExpr, field, rangeOpt)
            | ContextMemberAccess(ctxType, field) -> ContextMemberAccess(ctxType, field)
            | ContextMemberCall(ctxType, field, args) -> ContextMemberCall(ctxType, field, args |> List.map desugarExpr)
            | UseIn(value, body) -> UseIn(desugarExpr value, desugarExpr body)
            // A keyword the rest of its builder's block cannot follow is an error; the value stands in for it, as
            // nothing is checked once desugaring fails.
            | WorkflowBindExpr(keyword, value) ->
                match builder, unreachable with
                | Some builderName, Some place ->
                    reportUnreachable expr (if isReturnKeyword keyword then "return" else keyword) builderName place
                    (desugarExpr value).Kind
                | _ -> (workflowCall expr keyword [desugarExpr value]).Kind
            | WorkflowReturnExpr value ->
                match builder, unreachable with
                | Some builderName, Some place ->
                    reportUnreachable expr "return" builderName place
                    (desugarExpr value).Kind
                | _ -> (workflowCall expr "return" [desugarExpr value]).Kind
            // Each hole calls the prelude's hole function, which converts the value through `ToString`.
            | InterpolatedString parts ->
                let mappedParts =
                    parts
                    |> List.map (function
                        | StringText text -> StringText text
                        | StringExpr hole -> StringExpr (Expression.from hole (FunctionCall(InterpolationHoles.holeFunction, None, [ desugarExpr hole ]))))
                InterpolatedString mappedParts
            | UnitExpr
            | LiteralExpr _
            | IdentifierExpr _ -> expr.Kind
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
            | UseStatement value -> { stmt with Kind = UseStatement (desugarExpr value) }
            | ImportStatement _
            | TypeDefStatement _ -> stmt
        // The statements of a block from its first workflow keyword on, as that keyword's call with the rest of the
        // block as its continuation, and so on. A block that falls off its end without `return` has the builder's
        // `zero` as its value there (`#some()` for `proc`), its last value only run; outside a builder's block, `()`.
        and desugarWorkflowChain (statements: Statement list) =
            let ending =
                match builder with
                | Some builderName ->
                    let zero = Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr builderName), "zero", None))
                    let place = statements |> List.tryLast |> Option.map _.Span |> Option.defaultValue SourceSpan.none
                    zeros <- zeros |> Map.add zero.Id place
                    builderUsed <- true
                    zero
                | None -> Expression.synthetic UnitExpr
            desugarChain statements (fun last ->
                match last with
                | Some value -> Expression.from value (Block [ Statement.from value (ExprStatement value); Statement.synthetic (ExprStatement ending) ])
                | None -> ending)
        // `statements` as a workflow chain that goes on to `next` at its end, given the value of its last statement
        // if that is an expression. A keyword takes the rest of the chain as its continuation; `return` ends the
        // whole block, so what follows it, `next` included, never runs.
        and desugarChain (statements: Statement list) (next: Expression option -> Expression) : Expression =
            match statements with
            | [] -> next None
            | statement :: rest ->
                let restOfChain () = desugarChain rest next
                match statement.Kind with
                | DefStatement(isExport, name, typeOpt, expr) ->
                    match expr.Kind with
                    | WorkflowBindExpr(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr "return" [desugarExpr bindExpr]
                    | WorkflowBindExpr(keyword, bindExpr) ->
                        workflowCall expr keyword [desugarExpr bindExpr; Expression.from expr (Lambda([name, None], restOfChain ()))]
                    | _ when branchesUseKeywords expr -> desugarBranching expr (Some name) rest next
                    | _ ->
                        chainBlock statement (restOfChain ()) { statement with Kind = DefStatement(isExport, name, typeOpt, desugarExpr expr) }
                | ExprStatement expr ->
                    match expr.Kind with
                    | WorkflowReturnExpr value -> workflowCall expr "return" [desugarExpr value]
                    | WorkflowBindExpr(keyword, bindExpr) when isReturnKeyword keyword ->
                        workflowCall expr "return" [desugarExpr bindExpr]
                    // The last statement's bound value is the chain's value.
                    | WorkflowBindExpr(keyword, bindExpr) when rest.IsEmpty ->
                        let value = freshName "value"
                        let continuation = next (Some (Expression.from expr (IdentifierExpr value)))
                        workflowCall expr keyword [desugarExpr bindExpr; Expression.from expr (Lambda([value, None], continuation))]
                    | WorkflowBindExpr(keyword, bindExpr) ->
                        workflowCall expr keyword [desugarExpr bindExpr; Expression.from expr (Lambda([("_", None)], restOfChain ()))]
                    | _ when branchesUseKeywords expr -> desugarBranching expr None rest next
                    | _ when rest.IsEmpty -> next (Some (desugarExpr expr))
                    | _ ->
                        chainBlock statement (restOfChain ()) { statement with Kind = ExprStatement (desugarExpr expr) }
                | UseStatement _
                | ImportStatement _
                | TypeDefStatement _ ->
                    chainBlock statement (restOfChain ()) (desugarStatement statement)
        // An `if` or a `match` statement with a workflow keyword in a branch, `expr`, followed by `rest`. The rest of
        // the block, up to `next`, is bound once to a local function, and each branch is a chain of its own that goes
        // on to it: a keyword in a branch has the rest of the block as its continuation, as one outside does, and a
        // `return` in a branch ends the whole block. `name` is what a `def` binds the statement's value to.
        //
        //   def __rest_1 = { name -> <rest of the block> }
        //   if cond then <then branch, then __rest_1(its value)> else <else branch, then __rest_1(its value)>
        and desugarBranching (expr: Expression) (name: Identifier option) (rest: Statement list) (next: Expression option -> Expression) : Expression =
            freshNames <- freshNames + 1
            let restName = $"{NyxCompiler.Infer.workflowRestPrefix}{freshNames}"
            let parameter = name |> Option.defaultValue "_"
            let restFunction = Expression.from expr (Lambda([ parameter, None ], desugarChain rest next))
            // A branch goes on to the rest with its value when a `def` binds it, and with `()` otherwise, so that
            // branches of different types can still be a statement.
            let goOn (last: Expression option) =
                let argument =
                    match last, name with
                    | Some value, Some _ -> value
                    | _ -> Expression.from expr UnitExpr
                let call = Expression.from expr (FunctionCall(restName, None, [ argument ]))
                match last, name with
                | Some value, None -> Expression.from value (Block [ Statement.from value (ExprStatement value); Statement.from call (ExprStatement call) ])
                | _ -> call
            let branch (body: Expression) =
                let statements =
                    match body.Kind with
                    | Block statements -> statements
                    | _ -> [ Statement.from body (ExprStatement body) ]
                desugarChain (statements |> List.collect liftStatementBinds) goOn
            let branched =
                let kind =
                    match expr.Kind with
                    | Except(value, arms) -> Except.asMatch expr value arms
                    | kind -> kind
                match kind with
                | IfExpr(cond, thenExpr, elseExpr) -> IfExpr(desugarExpr cond, branch thenExpr, branch elseExpr)
                | Match(values, arms) ->
                    let desugarArm (patterns, body) =
                        PatternSpace.ofArm values.Length patterns |> List.map (Pattern.mapExpressions desugarExpr), branch body
                    Match(values |> List.map desugarExpr, arms |> List.map desugarArm)
                | other -> other
            Expression.from expr (
                Block [
                    Statement.from expr (DefStatement(false, restName, None, restFunction))
                    Statement.from expr (ExprStatement { expr with Kind = branched })
                ])
        // A statement followed by the rest of the chain, as a block made up for them that spans from the statement
        // to the end of the rest. The rest is a made-up `()` with no span when the statement is the last one.
        and chainBlock (statement: Statement) (nextExpr: Expression) (desugared: Statement) =
            let span =
                if SourceSpan.isNone nextExpr.Span then statement.Span
                else SourceSpan.between statement.Span nextExpr.Span
            Expression.at span (Block [ desugared; Statement.from nextExpr (ExprStatement nextExpr) ])
        // A block, from its first workflow keyword on, as a workflow chain (see `desugarWorkflowChain`): a statement
        // that is a keyword, or an `if` or `match` with one in a branch. Where the rest of a builder's block cannot
        // follow (see `unreachable`), its keywords are left to be reported.
        and desugarBlock statements =
            let startsChain (statement: Statement) =
                (unreachable.IsNone || builder.IsNone)
                && (match statement.Kind with
                    | DefStatement(_, _, _, Kind (WorkflowBindExpr _))
                    | ExprStatement (Kind (WorkflowBindExpr _))
                    | ExprStatement (Kind (WorkflowReturnExpr _)) -> true
                    | DefStatement(_, _, _, value)
                    | ExprStatement value -> branchesUseKeywords value
                    | _ -> false)
            let rec split prefix remaining =
                match remaining with
                | [] -> List.rev prefix
                | stmt :: _ when startsChain stmt ->
                    let chainExpr = desugarWorkflowChain remaining
                    List.rev prefix @ [ Statement.from chainExpr (ExprStatement chainExpr) ]
                | stmt :: rest ->
                    split (desugarStatement stmt :: prefix) rest
            split [] (statements |> List.collect liftStatementBinds)

        let desugared =
            module'
            |> List.map (function
                | Kind (Def (ValueDef(isExport, name, typeOpt, expr))) as item ->
                    { item with Kind = Def (ValueDef(isExport, name, typeOpt, desugarExpr expr)) }
                | Kind (Expr expr) as item -> { item with Kind = Expr (desugarExpr expr) }
                | other -> other)
        desugared, List.ofSeq diagnostics, zeros

    let desugar (module': Module) : Module =
        let desugared, _, _ = desugarWithDiagnostics module'
        desugared

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
    /// not precede a value definition, is an error. `@entry` takes no argument.
    let applyAttributes (module': Module) : Result<Module, Diagnostic list> =
        let rec loop (remaining: TopLevelItem list) (acc: TopLevelItem list) (entryCalls: TopLevelItem list) (errors: Diagnostic list) =
            match remaining with
            | [] -> List.rev acc, List.rev entryCalls, List.rev errors
            | Kind (Attribute(name, _)) as attribute :: rest when name <> entryAttribute ->
                loop rest acc entryCalls (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"Unknown attribute '@{name}'" :: errors)
            | Kind (Attribute(name, Some argument)) :: rest ->
                let error = Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span $"Attribute '@{name}' takes no argument"
                loop rest acc entryCalls (error :: errors)
            | Kind (Attribute _) :: (Kind (Def (ValueDef(_, name, _, value))) as def) :: rest ->
                // The call is made up for the def, and reported there.
                let call = TopLevelItem.at def.Span (Expr (Expression.from value (FunctionCall(name, None, []))))
                loop rest (def :: acc) (call :: entryCalls) errors
            | Kind (Attribute(name, _)) as attribute :: rest ->
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
    /// open for more tags, but no more can come once the definition is done. A rest a context the definition requires
    /// is applied to is left open (`contexts` has each one's contexts): an instance can add tags to it, as
    /// `FromString(int, #invalidInt)` does to the `e` of `[FromString(a, e)] string -> #some(a) | e`.
    let private resolveTypes (subst: Subst) (types: Map<string, Ty>) (contexts: Map<string, TypeExpr list>) (typeDefs: Map<string, TypeDefInfo>) (items: TypedTopLevelItem list) =
        let closeOutputRests name ty =
            let contextVars = contexts |> Map.tryFind name |> Option.defaultValue [] |> List.collect NyxCompiler.Infer.annotationVarNames |> Set.ofList
            let closed =
                Unifier.outputOnlyRestVars ty
                |> List.filter (fun v -> not (v.Name |> Option.exists contextVars.Contains))
                |> List.map (fun v -> v.Id, TyUnion [])
                |> Map.ofList
            if closed.IsEmpty then ty else Unifier.apply closed ty
        let resolvedTypes = types |> Map.map (fun name ty -> Unifier.apply subst ty |> closeOutputRests name)
        let resolvedTypeDefs =
            typeDefs
            |> Map.map (fun _ info ->
                // A definition's parameters stand for the types it is applied to, whatever the module found for a
                // variable of the same id: an imported definition's are numbered by the module it came from.
                let ownSubst = info.Parameters |> List.fold (fun acc parameter -> acc |> Map.remove parameter.Id) subst
                { info with Underlying = Unifier.apply ownSubst info.Underlying })
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
            | TypedUseStatement expr -> TypedUseStatement (applyTypedExpr expr)
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

    /// `expr` with each builder block that uses no workflow keyword (`blocks`, by the node of the call it is written
    /// as) replaced by its block, which runs where it is written.
    let private inlineWorkflowBlocks (blocks: Set<NodeId>) (expr: Expression) : Expression =
        if blocks.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind with
                | FunctionCall(_, _, [ Kind (Lambda([], body)) ]) when blocks.Contains current.Id -> body.Kind
                | other -> other)

    /// Where `span` is, as `nanyx` prints a diagnostic's place: `file:line:column: `.
    let private deferredLocation (span: SourceSpan) =
        if span.File = "" then $"{span.StartLine + 1}:{span.StartColumn + 1}: "
        else $"{span.File}:{span.StartLine + 1}:{span.StartColumn + 1}: "

    /// What a deferred error writes when it is reached: the diagnostic as `nanyx` prints it, marked as deferred.
    let private deferredMessage (diagnostic: Diagnostic) =
        let location = diagnostic.Span |> Option.map deferredLocation |> Option.defaultValue ""
        $"{location}error {diagnostic.Code}: {diagnostic.Message} (deferred from compile time)\n"

    /// `expr` with each expression that failed to check replaced by a call to `__panic`, which writes the errors
    /// and traps when it is reached, so a debug build runs the code around it. An expression in `failed` is found
    /// by its node. A conflict among the constraints is only known by where it was found, the expression whose
    /// constraints conflicted, so one in `mismatches` replaces the outermost expression written there. Each
    /// diagnostic placed is added to `placed`, by `deferredMessage`. A match in `unhandled`, which does not handle
    /// every case, is given a last arm that catches the rest and traps.
    ///
    /// An expression in `unknownTypeUses` needs the type of a value that has none, because of an error (piping it,
    /// or reading a member of it): the backend could not compile it, so it traps too. It is not an error of its own,
    /// so it writes why it stopped without a code.
    let private deferErrors (failed: Map<NodeId, Diagnostic list>) (mismatches: Map<SourceSpan, Diagnostic>) (unhandled: Map<NodeId, Diagnostic list>) (unknownTypeUses: Set<NodeId>) (placed: System.Collections.Generic.HashSet<string>) (expr: Expression) =
        let panicWith (node: Expression) (message: string) =
            FunctionCall("__panic", None, [ Expression.from node (LiteralExpr (StringLit message)) ])
        let panic (node: Expression) (diagnostics: Diagnostic list) =
            let messages = diagnostics |> List.map deferredMessage
            messages |> List.iter (placed.Add >> ignore)
            panicWith node (String.concat "" messages)
        expr
        |> mapExpressionKinds (fun node kind ->
            let diagnostics =
                match failed |> Map.tryFind node.Id with
                | Some diagnostics -> diagnostics
                | None -> mismatches |> Map.tryFind node.Span |> Option.toList
            match diagnostics, kind, unhandled |> Map.tryFind node.Id with
            | _ :: _, _, _ -> panic node diagnostics
            | [], Match(values, arms), Some missing ->
                let catchAll = values |> List.map (fun _ -> Pattern.at node.Span WildcardPattern)
                Match(values, arms @ [ catchAll, Expression.from node (panic node missing) ])
            | [], _, _ when unknownTypeUses.Contains node.Id ->
                panicWith node $"{deferredLocation node.Span}error: the value used here has no known type, because of an error deferred from compile time\n"
            | _ -> kind)

    /// The module as the backend will see it: the typed items turned back into syntax, with each pipe to an
    /// attached function, and each hole that calls a derived definition, written with the name it resolved to (`pipeTargets`), each operator that calls a definition as
    /// that call (`operatorCalls`, through the instance `operatorInstances` gives where it has one), each expression inference read as another as that one (`readAs`),
    /// each read of a built-in property as a call (`properties`), each builder block that uses no keyword as its block
    /// (`workflowBlocks`), and without import statements. `defer` compiles the module's errors to traps first, for a
    /// debug build.
    let private moduleFromTypedItems (defer: Expression -> Expression) (pipeTargets: Map<NodeId, Identifier>) (operatorCalls: Map<NodeId, Identifier>) (operatorInstances: Map<NodeId, TypeExpr>) (readAs: Map<NodeId, Expression>) (properties: Map<NodeId, BuiltInProperty>) (workflowBlocks: Set<NodeId>) (items: TypedTopLevelItem list) : Module =
        let lower (expr: Expression) =
            expr |> defer |> retargetPipes pipeTargets |> callOperators operatorCalls operatorInstances |> writeAsRead readAs |> callBuiltInProperties properties |> inlineWorkflowBlocks workflowBlocks
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

    /// The standard-library modules this process has checked, by `originKey` and source, each with the modules it
    /// imports, which a compilation that imports it needs too (`compileFrom` resolves inlined definitions' types by
    /// them). A user's own modules are checked again in each compilation. A checked module is never changed, and
    /// sharing one is safe: its type variables are numbered by the module itself, as they would be if it were checked
    /// again, and its syntax nodes and symbols keep ids no other module has.
    let private checkedStandardLibrary =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list> * (string * Result<CheckedModule, CompilationPhase * Diagnostic list>) list>>()

    /// Parses, desugars and type-checks one module, resolving and checking the modules it imports first. An
    /// error says which phase it came from.
    let rec private typecheckModule (checkedModules: CheckedModules) (origin: ModuleOrigin) (source: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        let resolved =
            parseWithDiagnostics (sourceFileName origin) source
            |> Result.mapError (fun diagnostics -> Parsed, diagnostics)
            |> Result.bind (fun (ast, declared, moduleBlocks) ->
                match desugarWithDiagnostics ast with
                | _, (_ :: _ as errors), _ -> Error (Desugared, errors)
                | desugaredAst, [], zeros ->
                    applyAttributes desugaredAst
                    |> Result.map (fun desugared ->
                        let desugared, warnings = DeprecatedTypeNames.replace declared desugared
                        ast, declared, moduleBlocks, desugared, zeros, warnings)
                    |> Result.mapError (fun diagnostics -> Desugared, diagnostics))
        match resolved with
        | Result.Error failure -> Error failure
        | Result.Ok (ast, declared, moduleBlocks, desugared, zeros, warnings) ->
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
            let mutable state = { NyxCompiler.Infer.emptyState with Declared = declared; ModuleBlocks = moduleBlocks; WorkflowZeros = zeros; Home = origin }
            let mutable errors: Diagnostic list = []
            let mutable inlined: TopLevelItem list = []
            // Kept apart and inlined last, so that where an explicit import defines the same name, its definition is kept.
            let mutable preludeInlined: TopLevelItem list = []
            let mutable preludeTypeNames: Set<string> = Set.empty
            let mutable inlinedTypes: Map<string, Ty> = Map.empty
            // Where the calls in the imported modules' definitions, which are inlined, find their instances.
            let mutable inlinedInstances: Map<NodeId, ContextInstance option list> = Map.empty
            // The instances the `use`s in the imported modules' definitions load.
            let mutable inlinedUseContexts: Map<NodeId, TypeExpr> = Map.empty
            // The contexts the defs the imports bring in require, with their types (see `ImportedContextTypes`).
            let mutable importedContextTypes: Map<SymbolId, Ty * Ty list> = Map.empty
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
                        Seq.append exportedSymbols exportedTypeSymbols
                        |> Seq.fold (fun acc (KeyValue(id, _)) ->
                            acc |> Map.add id ((acc |> Map.tryFind id |> Option.defaultValue []) @ [ import.Id ])) state.ImportedThrough
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
                // The contexts the imported functions require that this module cannot name are carried in with their
                // definitions, hidden, so a call to one is checked as a call needing the context is here: an ambient
                // one, and a parameterised one, whose instance each call finds (an attached `Point.show` for a
                // `Show(Point)`, say).
                let carried, carriedParameterised =
                    let rec contextNames (ctx: TypeExpr) =
                        match ctx.Kind with
                        | TypeContext contexts -> contexts |> List.collect contextNames
                        | TypeName name
                        | TypeApply(name, _) -> [ name ]
                        | _ -> []
                    let unnamed =
                        imported.ContextRequirements
                        |> Map.values
                        |> Seq.collect (List.collect contextNames)
                        |> Seq.distinct
                        |> Seq.filter (fun name -> not (typeDefs.ContainsKey name) && not (state.ContextDefs.ContainsKey name))
                        |> List.ofSeq
                    unnamed |> List.choose (fun name -> imported.AmbientContexts |> Map.tryFind name |> Option.map (fun info -> name, info)),
                    unnamed
                    |> List.choose (fun name ->
                        imported.Typed.TypeDefs
                        |> Map.tryFind name
                        |> Option.filter (fun info -> info.IsContext && not info.Parameters.IsEmpty && not (imported.AmbientContexts.ContainsKey name))
                        |> Option.map (fun info -> name, info))
                state <-
                    { state with
                        TypeDefs = carried @ carriedParameterised |> List.fold (fun acc (name, info) -> acc |> Map.add name info) typeDefs
                        HiddenTypeNames = carried @ carriedParameterised |> List.fold (fun acc (name, _) -> acc |> Set.add name) hiddenTypeNames
                        CarriedAmbientContexts = carried |> List.fold (fun acc (name, _) -> acc |> Set.add name) state.CarriedAmbientContexts
                        Symbols = Seq.fold (fun acc (KeyValue(id, symbol)) -> acc |> Map.add id symbol) state.Symbols (Seq.concat [ exportedSymbols; exportedTypeSymbols; importSymbols ])
                        ImportedThrough = importedThrough
                        // The prelude's contexts are the ones operators are read through (see `OperatorContexts`),
                        // but for one whose name an import after it gives to a type of its own.
                        PreludeContexts =
                            if importedKey = preludeKey then
                                exportedTypeDefs |> Map.filter (fun _ info -> info.IsContext) |> Map.keys |> Set.ofSeq
                            elif qualifier.IsNone then state.PreludeContexts - Set.ofSeq exportedTypeDefs.Keys
                            else state.PreludeContexts
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
                inlinedUseContexts <- imported.Typed.UseContexts |> Map.fold (fun acc id context -> acc |> Map.add id context) inlinedUseContexts
                importedContextTypes <-
                    RequiredContexts.ofImport imported.Subst imported.Typed.ContextTypes imported.RawSymbolTypes exportedSymbols.Keys
                    |> Map.fold (fun acc id found -> acc |> Map.add id found) importedContextTypes
            for item in imports do
                // An import of a module that is still being checked, because it imports this one, is a cycle.
                let checkImport (importOrigin: ModuleOrigin) (readSource: unit -> string) =
                    let importKey = originKey importOrigin
                    if importKey = key || visited.Contains importKey then
                        Error (Typed, [ Diagnostics.at DiagnosticCodes.ImportCycle item.Span $"Import cycle detected for {importKey}" ])
                    else
                        match typecheckImport checkedModules importOrigin readSource (visited.Add key) with
                        // A module with errors fails each importer with them.
                        | Ok imported when not imported.Errors.IsEmpty -> Error (Typed, imported.Errors)
                        | checkedImport -> checkedImport
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
                        match FileImports.missingAlias item, resolveImportPath (Path.GetDirectoryName fullPath) item with
                        | Some diag, _
                        | None, Error diag -> errors <- errors @ [ diag ]
                        | None, Ok importPath ->
                            match checkImport (SourceFile importPath) (fun () -> File.ReadAllText importPath) with
                            | Error (_, diags) -> errors <- errors @ diags
                            | Ok imported ->
                                match FileImports.importedFile item imported.Name imported.Syntax with
                                | [] -> mergeImport (originKey (SourceFile importPath)) item imported
                                | diags -> errors <- errors @ diags
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
            state <-
                { state with
                    AmbiguousImports = ambiguousImports
                    PreludeContexts = OperatorContexts.inModule (key = preludeKey) desugared state.PreludeContexts
                    NamedAsValues = SyntaxTree.namedAsValues desugared }
            if not errors.IsEmpty then
                // A module imported along several paths fails each importer with the same diagnostics.
                Error (Typed, List.distinct errors)
            else
                let types, items, (state: NyxCompiler.Infer.InferState), inferErrors = NyxCompiler.Infer.inferModuleWithEnv env state desugared
                let subst, mismatches = Unifier.solve state.Constraints
                // Every error in the module is reported: the items that failed to check, and each conflict among the
                // constraints of those that did, in the order they are in the source rather than the order solving
                // found them. The module is typed all the same, with what did check.
                let mismatchErrors =
                    mismatches
                    |> List.sortBy (fun (_, span) -> span.Start)
                    |> List.map (fun (message, span) -> Diagnostics.at (MemoryCells.mismatchCode state span) span message)
                let moduleErrors = inferErrors @ mismatchErrors @ (if key = preludeKey then OperatorContexts.ambiguityErrors state else [])
                // Each error that can be is compiled to a trap where it was found, for a debug build to run.
                let placed = System.Collections.Generic.HashSet<string>()
                let defer =
                    if moduleErrors.IsEmpty then id
                    else
                        let failed = state.FailedExprs |> List.fold (fun acc (id, diagnostics) -> acc |> Map.add id diagnostics) Map.empty
                        let bySpan = mismatchErrors |> List.choose (fun diagnostic -> diagnostic.Span |> Option.map (fun span -> span, diagnostic)) |> Map.ofList
                        let unhandled = state.UnhandledMatches |> List.fold (fun acc (id, diagnostics) -> acc |> Map.add id diagnostics) Map.empty
                        deferErrors failed bySpan unhandled (Set.ofList state.UnknownTypeUses) placed
                let resolvedTypes, resolvedTypeDefs, resolvedItems = resolveTypes subst types state.ContextRequirements state.TypeDefs items
                let own = moduleFromTypedItems defer (StructuralInstances.callTargets state) state.OperatorCalls state.OperatorInstances state.ReadAs state.BuiltInProperties state.WorkflowBlocks resolvedItems |> writeBlockAliases state |> writeCells state
                let undeferred = moduleErrors |> List.filter (fun diagnostic -> not (placed.Contains (deferredMessage diagnostic)))
                // The module's own definitions win over imported ones of the same name.
                let ownNames = exportedValueNames own |> Set.union (own |> List.choose (function Kind (Def (ValueDef(false, name, _, _))) -> Some name | _ -> None) |> Set.ofList)
                let instances = inlinedInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) state.ContextInstances
                let inlined =
                    inlined @ preludeInlined
                    |> List.filter (function
                        | Kind (Def (ValueDef(_, name, _, _))) -> not (ownNames.Contains name)
                        | _ -> true)
                    |> distinctDefinitions
                    |> reachedItems instances own
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
                          ContextInstances = state.ContextInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) inlinedInstances
                          UseContexts = state.UseContexts |> Map.fold (fun acc id context -> acc |> Map.add id context) inlinedUseContexts
                          // Resolved for the root module only, by `compileWith`.
                          InferredContextTypes = state.InferredContextTypes
                          ContextTypes = RequiredContexts.ofDefs state subst ownTopLevel
                          ImportedContextTypes = importedContextTypes
                          // Filled in for the root module only, by `compileWith`.
                          InlinedExprTypes = Map.empty
                          InlinedCalleeTypes = Map.empty }
                     Errors = moduleErrors
                     Warnings = warnings
                     Undeferred = undeferred
                     Own = own
                     Inlined = inlined
                     Name = FileImports.declaredName moduleBlocks desugared
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

    /// An imported module, checked the first time this compilation imports it; its source is only read then. A
    /// standard-library module is checked once per process (see `checkedStandardLibrary`), and the modules it imports
    /// come into this compilation with it.
    and private typecheckImport (checkedModules: CheckedModules) (origin: ModuleOrigin) (readSource: unit -> string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        match checkedModules.TryGetValue key with
        | true, checkedModule -> checkedModule
        | false, _ ->
            let checkedModule =
                match origin with
                | StandardLibraryModule _ ->
                    let source = readSource ()
                    let check _ =
                        lazy
                            (let own = CheckedModules()
                             let checkedModule = typecheckModule own origin source visited
                             checkedModule, [ for KeyValue(importKey, imported) in own -> importKey, imported ])
                    let checkedModule, imports = checkedStandardLibrary.GetOrAdd((key, source), check).Value
                    for importKey, imported in imports do
                        checkedModules.TryAdd(importKey, imported) |> ignore
                    checkedModule
                | SourceFile _
                | InMemory -> typecheckModule checkedModules origin (readSource ()) visited
            checkedModules.[key] <- checkedModule
            checkedModule

    let parse (source: string) : Result<Module, Diagnostic list> =
        match parseModuleFileWithNames "" source with
        | Result.Ok (ast, _, _) -> Ok ast
        | Result.Error err -> Error [ syntaxError err ]

    /// Checks the module from `origin`, then runs the linters `options` names over it if it type-checked. A module
    /// with type or name errors is typed all the same, with what did check, beside its errors. Only
    /// the module being compiled is analyzed: an imported module is analyzed when it is compiled itself, so its
    /// diagnostics are not repeated in every module that imports it, and the standard library never is (but by tests).
    let compileFrom (options: LinterOptions) (origin: ModuleOrigin) (source: string) : CompileResult =
        let checkedModules = CheckedModules()
        match typecheckModule checkedModules origin source Set.empty with
        | Result.Error (phase, diagnostics) ->
            { Phase = phase
              Diagnostics = diagnostics
              Typed = None
              TypeChecked = false
              ErrorsDeferred = false
              Syntax = None }
        | Result.Ok checkedModule ->
            // Types by node are only read for the root module, so only its are resolved: not those of every
            // imported module (the standard library included) on every compile.
            let resolve = Map.map (fun _ ty -> Unifier.apply checkedModule.Subst ty)
            // Nor are an imported module's resolved but for the nodes of its definitions that are inlined here, each by the
            // substitution of the module it came from.
            let inlinedNodes = System.Collections.Generic.HashSet<NodeId>()
            for item in checkedModule.Inlined do
                match item.Kind with
                | Def (ValueDef(_, _, _, expr)) ->
                    expr |> mapExpressionKinds (fun node kind -> inlinedNodes.Add node.Id |> ignore; kind) |> ignore
                | _ -> ()
            let imported = checkedModules.Values |> Seq.choose (function Result.Ok m -> Some m | Result.Error _ -> None) |> List.ofSeq
            let resolveInlined (raw: CheckedModule -> Map<NodeId, Ty>) =
                inlinedNodes
                |> Seq.choose (fun id ->
                    imported |> List.tryPick (fun m -> raw m |> Map.tryFind id |> Option.map (fun ty -> id, Unifier.apply m.Subst ty)))
                |> Map.ofSeq
            let typed =
                { checkedModule.Typed with
                    ExprTypes = resolve checkedModule.RawExprTypes
                    CalleeTypes = resolve checkedModule.RawCalleeTypes
                    SymbolTypes = resolve checkedModule.RawSymbolTypes
                    InferredContextTypes =
                        checkedModule.Typed.InferredContextTypes |> Map.map (fun _ tys -> tys |> List.map (Unifier.apply checkedModule.Subst))
                    ContextTypes = checkedModule.Typed.ContextTypes |> Map.map (fun _ tys -> tys |> List.map (Unifier.apply checkedModule.Subst))
                    InlinedExprTypes = resolveInlined _.RawExprTypes
                    InlinedCalleeTypes = resolveInlined _.RawCalleeTypes }
            // Callers that stop at errors (`nanyx run`, say) look at the diagnostics, and the typed module is still
            // there for the editor's features. The linters only look at a module that type-checked; a rule
            // configured as an error fails the compile as a compiler error does. Like the linters' findings, warnings
            // are only reported for the module being compiled.
            { Phase = Typed
              Diagnostics =
                checkedModule.Warnings
                @ if checkedModule.Errors.IsEmpty then Linters.run options (SemanticModel.ofTyped (sourceFileName origin) source checkedModule.Syntax typed)
                  else ImportFixes.add (normalizeSource source) checkedModule.Syntax checkedModule.Errors
              Typed = Some typed
              TypeChecked = checkedModule.Errors.IsEmpty
              ErrorsDeferred = checkedModule.Undeferred.IsEmpty
              Syntax = Some checkedModule.Syntax }

    /// `compileFile` with the linters and severities `options` gives.
    let compileFileWith (options: LinterOptions) (filePath: string) : CompileResult =
        if not (File.Exists filePath) then
            { Phase = Parsed
              Diagnostics = [ Diagnostics.general DiagnosticCodes.FileNotFound $"File not found: {filePath}" ]
              Typed = None
              TypeChecked = false
              ErrorsDeferred = false
              Syntax = None }
        else
            compileFrom options (SourceFile (Path.GetFullPath filePath)) (File.ReadAllText filePath)

    /// Compiles the file at `filePath` up to the typed module, resolving its imports (standard-library modules
    /// from the embedded library, everything else relative to the file). It runs no linters: `compileFileWith` runs those it is given.
    let compileFile (filePath: string) : CompileResult =
        compileFileWith Linters.none filePath

    /// `compileSource` with the linters and severities `options` gives.
    let compileSourceWith (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileFrom options (SourceFile (Path.GetFullPath filePath)) source

    /// Compiles `source` as if it were the contents of the file at `filePath` (which need not exist, or may hold
    /// older text: an editor's unsaved buffer, say). Imports are resolved exactly as `compileFile` resolves them.
    let compileSource (filePath: string) (source: string) : CompileResult =
        compileSourceWith Linters.none filePath source

    /// `compile` with the linters and severities `options` gives.
    let compileWith (options: LinterOptions) (source: string) : CompileResult =
        compileFrom options InMemory source

    /// Compiles source given directly, up to the typed module, without linters. Standard-library
    /// imports are resolved; imports by path cannot be, since there is no file for them to be relative to.
    let compile (source: string) : CompileResult =
        compileWith Linters.none source
