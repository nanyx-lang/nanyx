namespace NyxCompiler

open System
open Parser.Program

/// The official linter's `unusedBindings` rule: a local def, a lambda's argument or a variable a pattern binds that
/// nothing refers to. Top-level defs are left to `unusedPrivateDefinitions`, and names starting with `_`, which say
/// they are unused on purpose, are left alone.
module UnusedBindings =
    let rule =
        { Id = "unusedBindings"
          Title = "Unused binding"
          DefaultSeverity = Severity.Warning
          Unnecessary = true
          Description =
            "A local def, a lambda's argument or a variable a pattern binds that is never used. Names starting with `_` are not reported." }

    /// The fix for an unused `symbol`. A def is removed, unless it is one of the defs a destructuring def stands for
    /// (they share its span), where removing it would remove the others too. Those, and any other name, become `_`.
    let private fixFor (model: SemanticModel) (removableDefs: Map<SourceSpan, Statement list>) (symbol: Symbol) : Fix =
        let declaration = symbol.Declaration
        let rename = { Title = $"Rename `{symbol.Name}` to `_`"; Edits = [ { Span = declaration; NewText = "_" } ] }
        match symbol.Kind with
        | LocalValue ->
            let defs = removableDefs |> Map.toSeq |> Seq.tryFind (fun (span, _) -> SourceSpan.contains span declaration)
            match defs with
            | Some (_, [ def ]) -> { Title = $"Remove `{symbol.Name}`"; Edits = [ SourceEdits.removal model.Source def.Span ] }
            | _ -> rename
        | Parameter
        | PatternVariable
        | TopLevelValue
        | ImportedModule
        | TypeDefinition
        | ContextMember
        | UnitOfMeasure -> rename

    let private messageFor (symbol: Symbol) =
        match symbol.Kind with
        | Parameter -> "The parameter is never used"
        | PatternVariable -> "A pattern introduces it, but it is never used"
        | LocalValue
        | TopLevelValue
        | ImportedModule
        | TypeDefinition
        | ContextMember
        | UnitOfMeasure -> "It is never used"

    let lint (model: SemanticModel) : Report list =
        let referenced = model.References |> Map.values |> Set.ofSeq
        // The local defs, by span: several share one when a destructuring def stands for them. None ends its block,
        // since a block that ends with a def is an error (NYX0220) and linters only run on a module without errors.
        let removableDefs =
            model.Syntax
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.blocksWithin
            |> List.concat
            |> List.filter (function Kind (DefStatement _) -> true | _ -> false)
            |> List.groupBy (fun statement -> statement.Span)
            |> Map.ofList
        model.Symbols
        |> Map.values
        |> Seq.filter (fun symbol ->
            match symbol.Kind with
            | LocalValue
            | Parameter
            | PatternVariable -> true
            | TopLevelValue
            | ImportedModule
            | TypeDefinition
            | ContextMember
            | UnitOfMeasure -> false)
        |> Seq.filter (fun symbol -> not (symbol.Name.StartsWith "_") && not (referenced.Contains symbol.Id))
        |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
        |> Seq.map (fun symbol ->
            { Rule = rule.Id
              Subject = Some symbol.Name
              Span = symbol.Declaration
              Message = messageFor symbol
              Related = []
              Fixes = [ fixFor model removableDefs symbol ] })
        |> List.ofSeq

/// The official linter's `nonTailRecursion` rule: a function that calls itself other than in tail position, where
/// each call keeps a stack frame until it returns. A tail call uses no stack (`TailPosition`), so a function that
/// only calls itself in tail position can recurse to any depth. Calls within a lambda written in the function (a
/// workflow block, a `for` loop's body) are left alone: they are calls from that lambda, not the function. A `memory`
/// block's are not: the block runs in the function, and frees its memory after its body, so no call in it is in tail
/// position (#2725).
module NonTailRecursion =
    let rule =
        { Id = "nonTailRecursion"
          Title = "Recursive call not in tail position"
          DefaultSeverity = Severity.Info
          Unnecessary = false
          Description =
            "A function calls itself, but not as the last thing it does, so every call keeps a stack frame and a deep enough input runs out of stack. A call in tail position uses no stack." }

    /// What a function does with the result of a call within `expr` that `expr` is not itself, which is why the
    /// call is not in tail position.
    let private whyNotTail (expr: Expression) : string =
        match expr.Kind with
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _) -> $"its result is passed to `{name}`"
        | ContextMemberCall(_, name, _) -> $"its result is passed to `{name}`"
        // `[x, ...rest]` is parsed as `[x] ++ rest`.
        | BinaryOp("++", _, Kind (ListExpr _), _) -> "its result goes into a list"
        | BinaryOp(op, _, _, _) -> $"its result is an operand of `{op}`"
        | IfExpr _ -> "the `if` decides on its result"
        | Match _ -> "the `match` matches on its result"
        | Except _ -> "the `except` matches on its result"
        | Block _ -> "the block goes on after it"
        | ListExpr _ -> "its result goes into a list"
        | TupleExpr _ -> "its result goes into a tuple"
        | RecordExpr _ -> "its result goes into a record"
        | TagExpr(name, _) -> $"its result is the payload of `#{name}`"
        | MemberAccess(_, field, _) -> $"`.{field}` is read from its result"
        | InterpolatedString _ -> "its result is interpolated into a string"
        | UseIn _ -> "its result is loaded as a context"
        | WorkflowBindExpr _
        | WorkflowReturnExpr _ -> "a workflow keyword is applied to its result"
        | LiteralExpr _
        | UnitExpr
        | IdentifierExpr _
        | ContextMemberAccess _
        | TypeValue _
        | Lambda _
        | ContextNames _ -> "it is not the last thing the function does"

    /// The calls within `expr`, a function's body or part of it, other than those within lambdas, each with why it
    /// is not in tail position if it may not be: `why` says that for `expr` itself.
    let rec private callsWithin (why: string option) (expr: Expression) : (Expression * string option) list =
        let call =
            match expr.Kind with
            | FunctionCall _
            | Pipe _
            | ContextMemberCall _ -> [ expr, why ]
            | _ -> []
        let children, statements = SyntaxTree.childrenOf expr
        // The children in tail position when `expr` is (as `TailPosition.callsIn` has it). Any other child loses tail
        // position here, unless it already lost it further out, which is the reason that counts.
        let keepTail =
            match expr.Kind with
            | IfExpr(_, ifTrue, ifFalse) -> set [ ifTrue.Id; ifFalse.Id ]
            | Match(_, arms)
            | Except(_, arms) -> arms |> List.map (fun (_, arm) -> arm.Id) |> Set.ofList
            | UseIn(_, body) -> set [ body.Id ]
            | _ -> Set.empty
        let childWhy = why |> Option.orElse (Some (whyNotTail expr))
        let ofChild (child: Expression) = callsWithin (if keepTail.Contains child.Id then why else childWhy) child
        let ofChildren =
            match expr.Kind with
            | Lambda _ -> []
            | FunctionCall(name, _, [ Kind (Lambda(_, body, _)) ]) when name = MemoryCells.memoryFunction ->
                callsWithin (why |> Option.orElse (Some "the `memory` block frees its memory after it")) body
            | _ -> children |> List.collect ofChild
        let ofStatements =
            statements
            |> List.mapi (fun index statement ->
                match statement.Kind with
                | ExprStatement last when index = statements.Length - 1 -> callsWithin why last
                | DefStatement(_, name, _, value) -> callsWithin (why |> Option.orElse (Some $"its result is bound to `{name}`")) value
                | _ -> SyntaxTree.ofStatement statement |> List.collect (callsWithin childWhy))
            |> List.concat
        call @ ofChildren @ ofStatements

    /// The functions a module declares that can call themselves: a top-level def whose value is a lambda, and a
    /// local `rec` whose value is one. Each is the span of the def (which holds its name) and the lambda.
    /// `expressions` are every expression of the module, which local defs are found in.
    let private functionsIn (items: TopLevelItem list) (expressions: Expression list) : (SourceSpan * Expression) list =
        let rec topLevel (item: TopLevelItem) =
            match item.Kind with
            | Def (ValueDef(_, _, _, (Kind (Lambda _) as lambda))) -> [ item.Span, lambda ]
            | ModuleBlock(_, items) -> items |> List.collect topLevel
            | _ -> []
        let local (expr: Expression) =
            snd (SyntaxTree.childrenOf expr)
            |> List.choose (fun statement ->
                match statement.Kind with
                | DefStatement(_, _, _, Kind (RecSyntax.Call (Kind (Lambda _) as lambda))) -> Some (statement.Span, lambda)
                | _ -> None)
        (items |> List.collect topLevel) @ (expressions |> List.collect local)

    let lint (model: SemanticModel) : Report list =
        let expressions = model.Syntax |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
        functionsIn model.Syntax expressions
        |> List.collect (fun (declaration, lambda) ->
            match lambda.Kind with
            | Lambda(_, body, _) ->
                let tailCalls = TailPosition.callsIn body
                // A call to the function itself: one to the symbol declared by the def, and not by anything within
                // the lambda (a local def that hides its name).
                let isSelf (symbol: Symbol) =
                    SourceSpan.contains declaration symbol.Declaration && not (SourceSpan.contains lambda.Span symbol.Declaration)
                callsWithin None body
                |> List.choose (fun (call, why) ->
                    match model.References |> Map.tryFind call.Id |> Option.bind (fun id -> model.Symbols |> Map.tryFind id) with
                    | Some symbol when isSelf symbol && not (tailCalls.Contains call.Id) ->
                        let why = why |> Option.defaultValue "it is not the last thing the function does"
                        Some
                            { Rule = rule.Id
                              Subject = None
                              Span = call.Span
                              Message =
                                $"`{symbol.Name}` calls itself here, but not in tail position: {why}. Each call keeps a stack frame until it returns, so a deep enough input runs out of stack."
                              Related = []
                              Fixes = [] }
                    | _ -> None)
            | _ -> [])
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `unreachableArms` rule: a match arm that can never be chosen, because the arms before it
/// already match every value it matches (`PatternSpace.coveringArms`). It is reported at the whole arm. An arm with a guard never counts as covering
/// another, since whether it matches depends on more than the value's shape.
module UnreachableArms =
    let rule =
        { Id = "unreachableArms"
          Title = "Unreachable match arm"
          DefaultSeverity = Severity.Warning
          Unnecessary = true
          Description =
            "A match arm that can never be chosen, because the arms before it match every value it matches: it comes after a catch-all, after an arm that matches everything it does, or after arms that between them match every tag, boolean, int, length of list or combination of them it does." }

    /// An edit that removes the arm whose patterns are `patterns` and whose value is `body`: from its `|` to the end of
    /// its value, and its lines with them when nothing else is written on them (a comment after it goes too). `None`
    /// when no `|` comes before the arm, as for the `else` of an `if ... is`, which is no arm to remove.
    let internal removal (source: string) (patterns: Pattern list) (body: Expression) : TextEdit option =
        let first = (List.head patterns).Span
        let mutable bar = first.Start - 1
        while bar >= 0 && (source.[bar] = ' ' || source.[bar] = '\t') do
            bar <- bar - 1
        if bar < 0 || source.[bar] <> '|' || SourceSpan.isNone body.Span then
            None
        else
            let arm = TextEdit.spanBetween source first bar body.Span.End
            let edit = SourceEdits.removal source arm
            if edit.Span <> arm then
                Some edit
            else
                // On a line with other arms: the arm and the space before its `|`.
                let start = if bar > 0 && source.[bar - 1] = ' ' then bar - 1 else bar
                Some { Span = TextEdit.spanBetween source first start body.Span.End; NewText = "" }

    let lint (model: SemanticModel) : Report list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.collect (fun expr ->
            match expr.Kind with
            | Match(values, arms) ->
                let indexed = MatchCoverage.rows values arms
                let spanOf (patterns: Pattern list) = SourceSpan.between (List.head patterns).Span (List.last patterns).Span
                indexed
                |> List.choose (fun (index, patterns, body) ->
                    MatchCoverage.covering model.Types values arms index
                    |> Option.map (fun covering ->
                        let message =
                            match covering with
                            | [ _ ] -> "This arm can never match: an arm before it matches every value it does."
                            | _ -> "This arm can never match: the arms before it match every value it does between them."
                        { Rule = rule.Id
                          Subject = None
                          // The whole arm, which the editor draws faint: none of it can run.
                          Span = if SourceSpan.isNone body.Span then spanOf patterns else SourceSpan.between (List.head patterns).Span body.Span
                          Message = message
                          Related =
                            covering
                            |> List.map (fun i ->
                                let _, others, _ = indexed.[i]
                                spanOf others, (if covering.Length = 1 then "matches every value first" else "matches some of them first"))
                          Fixes =
                            removal model.Source patterns body
                            |> Option.map (fun edit -> { Title = "Remove the unreachable arm"; Edits = [ edit ] })
                            |> Option.toList }))
            | _ -> [])
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `undefinedPayloadArm` rule: an arm for a tag whose payload is `undefined`, which no value has,
/// so the matched value's type leaves the tag out (see `Unifier.apply`): `| #some(x) -> ...` on the head of a
/// `list(undefined)`, which is only ever `#emptyList` (#3129). The arm checks, as code after a `crash` does, since it
/// is no mistake in the types, only code that can't run; this is a hint, drawn faint, whose fix removes the arm. An arm
/// for a tag the type has nowhere is an error the type checker reports.
module UndefinedPayloadArms =
    let rule =
        { Id = "undefinedPayloadArm"
          Title = "Arm for a tag whose payload is undefined"
          DefaultSeverity = Severity.Hint
          Unnecessary = true
          Description =
            "A match arm for a tag whose payload is `undefined`, such as `#some(x)` on the head of a `list(undefined)`: no value has that tag, so the arm can never match." }

    /// The tags `ty` can be, if it is a tag or a closed union of them.
    let private closedTags (ty: Ty) : Set<string> option =
        let tagOf (item: Ty) = match Unifier.unalias item with TyTag(name, _) -> Some name | _ -> None
        match Unifier.unalias ty with
        | TyTag(name, _) -> Some (Set.singleton name)
        | TyUnion items when not items.IsEmpty && items |> List.forall (tagOf >> Option.isSome) -> Some (items |> List.choose tagOf |> Set.ofList)
        | _ -> None

    let lint (model: SemanticModel) : Report list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.collect (fun expr ->
            match expr.Kind with
            | Match(values, arms) ->
                let tags = values |> List.map (fun value -> model.Types |> Map.tryFind value.Id |> Option.bind closedTags)
                arms
                |> List.choose (fun (patterns, body) ->
                    if patterns.Length <> values.Length then None
                    else
                        List.zip patterns tags
                        |> List.tryPick (fun (pattern, tags) ->
                            match pattern.Kind, tags with
                            | TagPattern(name, Some _), Some tags when not (tags.Contains name) -> Some name
                            | _ -> None)
                        |> Option.map (fun tag ->
                            { Rule = rule.Id
                              Subject = Some tag
                              Span = SourceSpan.between (List.head patterns).Span (if SourceSpan.isNone body.Span then (List.last patterns).Span else body.Span)
                              Message = $"This arm can never match: `#{tag}` would hold a value of type `undefined`, of which there is none"
                              Related = []
                              Fixes =
                                UnreachableArms.removal model.Source patterns body
                                |> Option.map (fun edit -> { Title = "Remove the unreachable arm"; Edits = [ edit ] })
                                |> Option.toList }))
            | _ -> [])
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `unusedImports` rule: an import that nothing in the module refers to. A name it brings
/// in, unqualified or through its alias, and a type it brings in named in a type, are uses (`ImportReferences`).
/// Only the imports the module writes are symbols, so the prelude and the import `@entry` adds are never reported.
module UnusedImports =
    let rule =
        { Id = "unusedImports"
          Title = "Unused import"
          DefaultSeverity = Severity.Warning
          Unnecessary = true
          Description =
            "An import that nothing in the module uses: no value, type or context it brings in is referred to, under its alias or unqualified. An alias starting with `_` is not reported." }

    /// Each import statement of the module, a `module name =` block's among them, with its items.
    let rec private importsOf (items: TopLevelItem list) : (TopLevelItem * ImportItem list) list =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Import imports -> [ item, imports ]
            | ModuleBlock(_, items) -> importsOf items
            | _ -> [])

    /// An edit that removes the import `item`: the whole statement when it is the statement's only import, and
    /// otherwise the item, with its line when it has one to itself, or with the space before it.
    let private removal (source: string) (statement: TopLevelItem) (siblings: ImportItem list) (item: ImportItem) : TextEdit =
        match siblings with
        | [ _ ] -> SourceEdits.removal source statement.Span
        | _ ->
            let edit = SourceEdits.removal source item.Span
            if edit.Span <> item.Span then
                edit
            else
                let mutable start = item.Span.Start
                while start > 0 && (source.[start - 1] = ' ' || source.[start - 1] = '\t') do
                    start <- start - 1
                { edit with Span = { item.Span with Start = start; StartColumn = item.Span.StartColumn - (item.Span.Start - start) } }

    let lint (model: SemanticModel) : Report list =
        let used = model.ImportReferences |> Map.values |> Set.ofSeq
        let statements = importsOf model.Syntax
        model.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> symbol.Kind = ImportedModule && not (symbol.Name.StartsWith "_") && not (used.Contains symbol.Id))
        |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
        |> Seq.choose (fun symbol ->
            statements
            |> List.tryPick (fun (statement, items) ->
                items |> List.tryFind (fun item -> item.Span = symbol.Declaration) |> Option.map (fun item -> statement, items, item))
            |> Option.map (fun (statement, items, item) ->
                { Rule = rule.Id
                  Subject = Some symbol.Name
                  Span = item.Span
                  Message = "It is imported but never used"
                  Related = []
                  Fixes = [ { Title = $"Remove the import of `{symbol.Name}`"; Edits = [ removal model.Source statement items item ] } ] }))
        |> List.ofSeq

/// The official linter's `unusedPrivateDefinitions` rule: a top-level def, type or context the module does not export,
/// or a member of such a context, that nothing refers to. Only the module can name what it does not export, so with no
/// references it is dead. A reference from within the definition itself (a function calling itself, a type naming
/// itself) is not a use. Left alone: `@entry`'s def, which the runtime calls; a `@test`, which a test build runs; a def an instance built from attached
/// definitions picks up (`InstanceSynthesized`); and names starting with `_`. A `module name =` block's defs that it
/// does not export are private to the block, and are reported the same way.
module UnusedPrivateDefinitions =
    let rule =
        { Id = "unusedPrivateDefinitions"
          Title = "Unused private definition"
          DefaultSeverity = Severity.Warning
          Unnecessary = true
          Description =
            "A top-level def, type or context that the module does not export, or a member of such a context, that nothing in the module uses. `@entry`'s def, a `@test`, defs an instance picks up from a type's attached definitions, and names starting with `_` are not reported." }

    /// The definitions the module writes, each the item that writes it, a `module name =` block's among them.
    let rec private definitionsOf (items: TopLevelItem list) : TopLevelItem list =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def _ -> [ item ]
            | ModuleBlock(_, items) -> definitionsOf items
            | ModuleDecl _
            | Import _
            | ReExport _
            | Attribute _
            | DimensionDecl _
            | UnitDecl _
            | Expr _ -> [])

    /// The names of the definitions that an instance built from attached definitions picks up somewhere in the module.
    let private synthesizedMembers (model: SemanticModel) : Set<string> =
        model.ContextInstances
        |> Map.values
        |> Seq.collect id
        |> Seq.collect (function
            | Some (InstanceSynthesized members) -> members |> List.map snd
            | Some (InstanceDerived(_, definition, _)) -> [ definition ]
            | Some (InstanceInScope _)
            | Some InstanceBuiltIn
            | None -> [])
        |> Set.ofSeq

    let private messageFor (symbol: Symbol) =
        match symbol.Kind with
        | TypeDefinition -> "The type is never used"
        | ContextMember -> "The context's member is never used"
        | UnitOfMeasure -> "The unit is never used"
        | TopLevelValue
        | LocalValue
        | Parameter
        | PatternVariable
        | ImportedModule -> "It is never used"

    /// The names of the defs marked `@test` or `@entry` in `items`, a `module name =` block's among them: a test build
    /// runs the one, and the host calls the other. A test build makes `@entry`'s def an ordinary one, which nothing
    /// calls (#3094).
    let rec private runIn (items: TopLevelItem list) : Set<string> =
        items
        |> List.pairwise
        |> List.choose (fun (first, second) ->
            match first.Kind, second.Kind with
            | Attribute(name, _), Def (ValueDef(_, defName, _, _)) when name = Attributes.testAttribute || name = Attributes.entryAttribute ->
                Some defName
            | _ -> None)
        |> Set.ofList
        |> Set.union (items |> List.choose (fun item -> match item.Kind with ModuleBlock(_, inner) -> Some (runIn inner) | _ -> None) |> Set.unionMany)

    let lint (model: SemanticModel) : Report list =
        let definitions = definitionsOf model.Syntax
        let picked = synthesizedMembers model
        let run = runIn model.Syntax
        // The span of each expression and type as written, so that a reference from within a definition to itself is
        // told apart: a def that calls itself, a type that names itself. Nodes desugaring made are not among them, and
        // their references count as uses: the call to `@entry`'s def that `applyAttributes` adds is one.
        let expressions = model.Syntax |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
        let types =
            (model.Syntax |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
            |> List.collect TypeExpr.within
        let spans =
            (expressions |> List.map (fun expr -> expr.Id, expr.Span)) @ (types |> List.map (fun typeExpr -> typeExpr.Id, typeExpr.Span))
            |> Map.ofList
        // A def that starts tasks calls, from a node desugaring made, the def the compiler made to run its body in its
        // caller's scope, which is written as itself (see `WrittenReferences`): that call is no use of it (#3035).
        let callsOwnScopeDef (node: NodeId) =
            not (spans.ContainsKey node)
            && model.CalledReferences
               |> Map.tryFind node
               |> Option.bind model.Symbols.TryFind
               |> Option.exists (fun called -> match Names.ownPart called.Name with TaskScopeSyntax.InCallersScope _ -> true | _ -> false)
        let usesBySymbol =
            model.References
            |> Map.filter (fun node _ -> not (callsOwnScopeDef node))
            |> Map.toList
            |> List.groupBy snd
            |> List.map (fun (symbol, references) -> symbol, references |> List.map (fun (node, _) -> spans |> Map.tryFind node))
            |> Map.ofList
        let definitionOf (symbol: Symbol) =
            definitions |> List.tryFind (fun item -> SourceSpan.contains item.Span symbol.Declaration)
        let isUsed (symbol: Symbol) (definition: TopLevelItem) =
            usesBySymbol
            |> Map.tryFind symbol.Id
            |> Option.defaultValue []
            |> List.exists (function
                | Some span -> not (SourceSpan.contains definition.Span span)
                | None -> true)
        model.Symbols
        |> Map.values
        |> Seq.filter (fun symbol ->
            match symbol.Kind with
            | TopLevelValue
            | TypeDefinition
            | ContextMember -> not symbol.IsExported && not (symbol.Name.StartsWith "_")
            | LocalValue
            | Parameter
            | PatternVariable
            | ImportedModule
            // A unit's `unit` line is no definition this rule removes (see `definitionsOf`).
            | UnitOfMeasure -> false)
        |> Seq.choose (fun symbol -> definitionOf symbol |> Option.map (fun definition -> symbol, definition))
        |> Seq.filter (fun (symbol, definition) ->
            not (isUsed symbol definition)
            && not (symbol.Kind = TopLevelValue && (picked.Contains symbol.Name || run.Contains symbol.Name)))
        |> Seq.sortBy (fun (symbol, _) -> symbol.Declaration.Start)
        |> Seq.map (fun (symbol, definition) ->
            { Rule = rule.Id
              Subject = Some symbol.Name
              Span = symbol.Declaration
              Message = messageFor symbol
              Related = []
              // A member is one field of its context, which has no span of its own to remove.
              Fixes =
                match symbol.Kind with
                | ContextMember -> []
                | _ -> [ { Title = $"Remove `{symbol.Name}`"; Edits = [ SourceEdits.removal model.Source definition.Span ] } ] })
        |> List.ofSeq

/// The official linter's `noTagInCommon` rule: `==` or `!=` on two values of closed tag types that share no tag, such
/// as `#red == #some(1)`. The operands are joined, not made equal (see "Eq(a)" in the docs' Contexts page), so this type-checks, and is always false (or always true).
module NoTagInCommon =
    let rule =
        { Id = "noTagInCommon"
          Title = "Comparison of types with no tag in common"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "`==` or `!=` on two values whose types are closed tag unions with no tag in common, so that the comparison is always false (or always true)." }

    /// The names of the tags a value of the type `ty` can be, if it is a tag or a union closed to others.
    let private closedTags (ty: Ty) : Set<string> option =
        let tagName (item: Ty) =
            match Unifier.unalias item with
            | TyTag(name, _) -> Some name
            | _ -> None
        match Unifier.unalias ty with
        | TyTag(name, _) -> Some (Set.singleton name)
        | TyUnion items when items |> List.forall (tagName >> Option.isSome) -> Some (items |> List.choose tagName |> Set.ofList)
        | _ -> None

    let lint (model: SemanticModel) : Report list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.choose (fun expr ->
            match expr.Kind with
            | BinaryOp(op, _, left, right) when EqualityOperators.isOperator op ->
                match model.Types |> Map.tryFind left.Id, model.Types |> Map.tryFind right.Id with
                | Some leftTy, Some rightTy ->
                    match closedTags leftTy, closedTags rightTy with
                    | Some leftTags, Some rightTags when (Set.intersect leftTags rightTags).IsEmpty ->
                        let tys = Unifier.tysToDisplayStrings Ascii [ leftTy; rightTy ]
                        let always = if op = EqualityOperators.equals then "false" else "true"
                        Some
                            { Rule = rule.Id
                              Subject = None
                              Span = expr.Span
                              Message = $"'{tys.[0]}' and '{tys.[1]}' have no tag in common, so this is always {always}"
                              Related = []
                              Fixes = [] }
                    | _ -> None
                | _ -> None
            | _ -> None)

/// The official linter's `unreachableCode` rule: the statements of a block after one that never gives a value, whose
/// type is `undefined` (a call to `crash`, say), or after a workflow's `return`, which ends the block. They can never run. It is reported once per block, at the statements
/// from the first that never runs to the end of the block. The arguments of a call after one whose type is
/// `undefined` are reported the same way, once per call.
module UnreachableCode =
    let rule =
        { Id = "unreachableCode"
          Title = "Unreachable code"
          DefaultSeverity = Severity.Warning
          Unnecessary = true
          Description =
            "Statements of a block after one that never gives a value, such as a call to `crash`, whose type is `undefined`, or after a `return`, and the arguments of a call after one that never gives a value. They can never run." }

    /// Why `expr`, whose type is `undefined` or which is a `return`, means what follows never runs.
    let private whyNever (expr: Expression) =
        match expr.Kind with
        | WorkflowReturnExpr _ -> "`return` ends the block"
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _) -> $"`{name}` never returns"
        | _ -> "the statement before it never gives a value"

    /// The arguments of a call after one that never gives a value: arguments are evaluated left to right, so neither
    /// they nor the call itself ever run. Reported once per call, from the first that never runs to its last. A pipe's
    /// piped value is its first argument, and a call of several arguments is given them as one tuple.
    let private afterArguments (never: Expression -> bool) (expr: Expression) : Report option =
        let spread (arguments: Expression list) =
            match arguments with
            | [ Kind (TupleExpr items) ] -> items
            | _ -> arguments
        let call =
            match expr.Kind with
            | FunctionCall(name, _, arguments) -> Some (name, spread arguments)
            | Pipe(piped, name, _, arguments) -> Some (name, piped :: spread arguments)
            | ContextMemberCall(_, name, arguments) -> Some (name, spread arguments)
            | _ -> None
        call
        |> Option.bind (fun (name, arguments) ->
            arguments
            |> List.tryFindIndex never
            |> Option.filter (fun index -> index < arguments.Length - 1)
            |> Option.map (fun index ->
                let diverging = arguments.[index]
                let unreachable = arguments |> List.skip (index + 1)
                let callee = if MadeUpNames.isMadeUp name then "the call" else $"`{name}`"
                { Rule = rule.Id
                  Subject = None
                  Span = SourceSpan.between (List.head unreachable).Span (List.last unreachable).Span
                  Message = $"This code never runs: {whyNever diverging}, so the arguments after it are never evaluated and {callee} is never called"
                  Related = [ diverging.Span, "never gives a value" ]
                  Fixes = [] }))

    let lint (model: SemanticModel) : Report list =
        let never (expr: Expression) =
            model.Types |> Map.tryFind expr.Id |> Option.exists Unifier.isBottom
        // A `return` ends its whole block, whatever its value's type.
        let ends (expr: Expression) =
            match expr.Kind with
            | WorkflowReturnExpr _ -> true
            | _ -> never expr
        let items = model.Syntax |> List.collect SyntaxTree.ofItem
        let ofArguments = items |> List.collect SyntaxTree.expressionsWithin |> List.choose (afterArguments never)
        items
        |> List.collect SyntaxTree.blocksWithin
        |> List.choose (fun statements ->
            statements
            |> List.indexed
            |> List.tryPick (fun (index, statement) ->
                match SyntaxTree.ofStatement statement with
                | [ value ] when index < statements.Length - 1 && ends value -> Some (index, statement, value)
                | _ -> None)
            |> Option.map (fun (index, diverging, value) ->
                let unreachable = statements |> List.skip (index + 1)
                let span = SourceSpan.between (List.head unreachable).Span (List.last unreachable).Span
                // Removing them would leave the block ending with a def, which is an error (NYX0220).
                let fixes =
                    match diverging.Kind with
                    | ExprStatement _ -> [ { Title = "Remove the unreachable code"; Edits = [ SourceEdits.removal model.Source span ] } ]
                    | _ -> []
                { Rule = rule.Id
                  Subject = None
                  Span = span
                  Message = $"This code never runs: {whyNever value}"
                  Related = [ diverging.Span, (match value.Kind with WorkflowReturnExpr _ -> "ends the block" | _ -> "never gives a value") ]
                  Fixes = fixes }))
        |> List.append ofArguments
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `useInBraces` rule: `use X in { ... }`, where the body is a lambda of no arguments written
/// with braces, as if they made a block. The context is loaded only to make the function, not when it is called, and
/// the statements are not run. A lambda written with its `()` argument, `{ () -> ... }`, says it means a function.
module UseInBraces =
    let rule =
        { Id = "useInBraces"
          Title = "`use ... in` with a body in braces"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "`use X in { ... }`, whose body is a function of no arguments, not a block run with `X` loaded. Put the statements on the lines after `in`, indented." }

    let lint (model: SemanticModel) : Report list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.choose (fun expr ->
            match expr.Kind with
            | UseIn(_, (Kind (Lambda([], _, false)) as body)) ->
                Some
                    { Rule = rule.Id
                      Subject = None
                      Span = body.Span
                      Message =
                        "The braces make this a function of no arguments, not a block run with the context loaded: put the statements on the lines after `in`, indented"
                      Related = []
                      Fixes = [] }
            | _ -> None)

/// The official linter's `strayDocComments` rule: a doc comment (`--|`) that documents nothing, because what follows
/// it is not something a doc comment can document (see `DocTarget`). Its fix makes it an ordinary comment.
module StrayDocComments =
    let rule =
        { Id = "strayDocComments"
          Title = "Doc comment documents nothing"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "A doc comment (`--|`) documents the definition, type, context member, field or tag on the line after it, or the module at the top of the file. One followed by anything else documents nothing." }

    /// The doc comment `written` as ordinary comments: its `--|` made `--`, and each of its continuation lines a `--`
    /// comment at the indent it has.
    let private asOrdinaryComment (written: string) =
        written.Split '\n'
        |> Array.mapi (fun index line ->
            if index = 0 then "--" + line.Substring 3
            elif String.IsNullOrWhiteSpace line then line
            else
                let text = line.TrimStart(' ', '\t')
                line.Substring(0, line.Length - text.Length) + "-- " + text)
        |> String.concat "\n"

    let lint (model: SemanticModel) : Report list =
        model.DocComments.Value
        |> List.filter (fun comment -> comment.Target = DocumentsNothing)
        |> List.map (fun comment ->
            let written = model.Source.Substring(comment.Span.Start, comment.Span.End - comment.Span.Start)
            { Rule = rule.Id
              Subject = None
              Span = comment.Span
              Message =
                "This doc comment documents nothing: put it on the line before a definition, a type, a field or a tag, or make it an ordinary comment with `--`"
              Related = []
              Fixes = [ { Title = "Make it an ordinary comment"; Edits = [ { Span = comment.Span; NewText = asOrdinaryComment written } ] } ] })

/// The official linter's `unannotatedExports` rule: an exported top-level def, a `module name =` block's among them,
/// that does not write its type. Its fix writes the type it was inferred to have after its name, with the contexts it
/// requires, as hover shows it (`Signatures`). A destructuring def (`export def (a, b) = ...`) has no one name to write
/// a type after, so it is left alone.
module UnannotatedExports =
    let rule =
        { Id = "unannotatedExports"
          Title = "Exported definition without a type annotation"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description = "An exported top-level def that does not write its type. Other modules use it by its type, so write it down." }

    /// The value defs the module writes, a `module name =` block's among them, each with whether it writes its
    /// type.
    let rec private valueDefsOf (items: TopLevelItem list) : (TopLevelItem * bool) list =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(_, _, annotation, _)) -> [ item, annotation.IsSome ]
            | ModuleBlock(_, items) -> valueDefsOf items
            | Def (TypeDef _)
            | ModuleDecl _
            | Import _
            | ReExport _
            | Attribute _
            | DimensionDecl _
            | UnitDecl _
            | Expr _ -> [])

    let lint (model: SemanticModel) : Report list =
        let defs = valueDefsOf model.Syntax
        // The defs a destructuring def stands for share its span.
        let destructuring =
            defs |> List.countBy (fun (item, _) -> item.Span) |> List.filter (fun (_, count) -> count > 1) |> List.map fst |> Set.ofList
        let topLevel =
            model.Symbols |> Map.values |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue) |> List.ofSeq
        defs
        |> List.filter (fun (item, annotated) -> not annotated && not (destructuring.Contains item.Span))
        // A def's own name is the only top-level symbol declared within it.
        |> List.choose (fun (item, _) -> topLevel |> List.tryFind (fun symbol -> SourceSpan.contains item.Span symbol.Declaration))
        |> List.filter (fun symbol -> symbol.IsExported)
        |> List.choose (fun symbol ->
            model.SymbolTypes
            |> Map.tryFind symbol.Id
            |> Option.map (fun ty ->
                let contexts = model.ContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
                let named = Signatures.writable model.TypeDefs model.Symbols
                let written = Signatures.typeWithContexts model.TypeDefs Source (named ty) (List.map named contexts)
                let afterName = symbol.Declaration.End
                { Rule = rule.Id
                  Subject = Some symbol.Name
                  Span = symbol.Declaration
                  Message = $"Its type isn't written: write `: {written}` after its name"
                  Related = []
                  Fixes =
                    [ { Title = $"Write the type of `{symbol.Name}`"
                        Edits = [ { Span = TextEdit.spanBetween model.Source symbol.Declaration afterName afterName; NewText = $": {written}" } ] } ] }))
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `resultOnlyTypeVariable` rule: a top-level def of a function whose type has a variable that a
/// context it requires names and its result has, but that no parameter fixes, as `[Decode(a)] string -> a` has. A
/// parameter fixes a variable its type has, and so does a context that names one a parameter fixes, as
/// `[Iterable(c, elem = a)] c -> a` fixes `a` through `c`. Nothing at a call says which type such a variable is but a
/// type written on its result, so the rule suggests a type argument, which says it at the call. A def that requires
/// `Typed` is left alone: it is given the type at the call already.
module ResultOnlyTypeVariables =
    let rule =
        { Id = "resultOnlyTypeVariable"
          Title = "Type variable only in the result"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "A function whose result has a type variable that a context it requires names but no parameter fixes: nothing at a call says which type it is but a type written on the result. Take it as a type argument." }

    /// The variables of `ty` that no parameter of it fixes, which a context of `contexts` names and its result has.
    let private unfixed (ty: Ty) (contexts: Ty list) : int list =
        match Unifier.unalias ty with
        | TyFunc(input, output, _) when not (contexts |> List.exists (FunctionContexts.contextName >> (=) (Some TypeValues.typedContext))) ->
            let named = contexts |> List.map TypeEnv.freeTyVars
            // The variables a parameter fixes, and those a context naming one of them fixes with it.
            let rec closed (fixedVars: Set<int>) =
                let more = named |> List.filter (Set.intersect fixedVars >> Set.isEmpty >> not) |> List.fold Set.union fixedVars
                if more = fixedVars then fixedVars else closed more
            let fixedVars = closed (TypeEnv.freeTyVars input)
            let result = TypeEnv.freeTyVars output
            named |> Set.unionMany |> Set.filter (fun var -> result.Contains var && not (fixedVars.Contains var)) |> Set.toList
        | _ -> []

    let lint (model: SemanticModel) : Report list =
        model.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue)
        |> Seq.choose (fun symbol ->
            match model.SymbolTypes |> Map.tryFind symbol.Id, model.ContextTypes |> Map.tryFind symbol.Id with
            | Some ty, Some contexts ->
                match unfixed ty contexts with
                | [] -> None
                | vars ->
                    // Each variable as the def's type is written, by hover and `unannotatedExports`' fix.
                    let varTys = ContextInference.varsInOrder ty |> List.filter (fun var -> vars |> List.contains var.Id) |> List.map TyVar
                    let written =
                        Unifier.tysToDisplayStringsIn model.TypeDefs Source (ty :: contexts @ varTys)
                        |> List.skip (1 + contexts.Length)
                    let first = List.head written
                    let quoted = Diagnostics.listed (written |> List.map (fun name -> "'" + name + "'"))
                    Some
                        { Rule = rule.Id
                          Subject = Some symbol.Name
                          Span = symbol.Declaration
                          Message =
                            $"Only its result has {quoted}, which a context it requires names, so nothing at a call says which type it is but a type written on the result: "
                            + $"take it as a type argument, `Type({first})`, or a named one, `{first}: Type({first})`, which a call passes as `type T`"
                          Related = []
                          Fixes = [] }
            | _ -> None)
        |> List.ofSeq
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `shadowedPreludeNames` rule: a top-level def, a local def, a lambda's argument or a variable a
/// pattern binds whose name is one the prelude brings into scope, which it hides wherever it is in scope itself. Only
/// the prelude's unqualified definitions are in scope by name: one attached to a type (`list.map`) is reached through
/// the type's name, and a context's member only in a def that requires the context. Left alone: the standard library's
/// own modules, which define the names the prelude brings in. There is no fix, since a fix's edits cannot ask for the
/// new name.
module ShadowedPreludeNames =
    let rule =
        { Id = "shadowedPreludeNames"
          Title = "Definition hides a prelude member"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "A def, a lambda's argument or a variable a pattern binds whose name is one the prelude brings into scope, such as `ignore` or `pow`, so the prelude's can't be named where it is in scope." }

    let lint (model: SemanticModel) : Report list =
        if StandardLibrary.isModuleFile model.File then
            []
        else
            let preludeFiles = StandardLibrary.moduleFiles StandardLibrary.preludeModuleName |> Set.ofList
            let preludeNames =
                model.Symbols
                |> Map.values
                |> Seq.filter (fun symbol ->
                    symbol.Kind = TopLevelValue && symbol.IsExported && preludeFiles.Contains symbol.Declaration.File && not (symbol.Name.Contains '.'))
                |> Seq.map _.Name
                |> Set.ofSeq
            model.Symbols
            |> Map.values
            |> Seq.filter (fun symbol ->
                match symbol.Kind with
                | TopLevelValue
                | LocalValue
                | Parameter
                | PatternVariable -> symbol.Declaration.File = model.File && not (SourceSpan.isNone symbol.Declaration)
                | ImportedModule
                | TypeDefinition
                | ContextMember
                | UnitOfMeasure -> false)
            |> Seq.filter (fun symbol -> preludeNames.Contains symbol.Name)
            |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
            |> Seq.map (fun symbol ->
                { Rule = rule.Id
                  Subject = Some symbol.Name
                  Span = symbol.Declaration
                  Message = $"The prelude's '{symbol.Name}' can't be named where it is in scope"
                  Related = []
                  Fixes = [] })
            |> List.ofSeq

/// The official linter's `semicolons` rule: statements separated by `;` on one line, rather than written one per line.
/// Where the statements are a block's lines, its fix puts the statement after the `;` on a line of its own, at the
/// block's indent; a match arm's or a lambda's one-line body is left to the author to lay out.
module Semicolons =
    let rule =
        { Id = "semicolons"
          Title = "Statements separated by `;`"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "Statements separated by `;` on one line. Put each statement on a line of its own; turn the rule off to write short sequences on one line." }

    /// The `;` between `before` and `after`, two statements of `block`, if they are separated by one, with its fix.
    let private separator (source: string) (block: Statement list) (before: Statement) (after: Statement) =
        if before.Span.End < 0 || before.Span.End >= after.Span.Start || after.Span.Start > source.Length then
            None
        else
            let between = source.Substring(before.Span.End, after.Span.Start - before.Span.End)
            if between.Trim(' ', '\t') <> ";" then
                None
            else
                let offset = before.Span.End + between.IndexOf ';'
                let span =
                    { after.Span with
                        Start = offset
                        End = offset + 1
                        StartColumn = after.Span.StartColumn - (after.Span.Start - offset) }
                let lineStart = offset - span.StartColumn
                let indent = source.Substring(lineStart, span.StartColumn).Length - source.Substring(lineStart, span.StartColumn).TrimStart(' ', '\t').Length
                let startsTheLine = block |> List.exists (fun s -> s.Span.StartLine = after.Span.StartLine && s.Span.StartColumn = indent)
                let fixes =
                    if startsTheLine then
                        [ { Title = "Put the statement on a line of its own"
                            Edits = [ { Span = { span with Start = before.Span.End; End = after.Span.Start; StartColumn = before.Span.End - lineStart }
                                        NewText = "\n" + source.Substring(lineStart, indent) } ] } ]
                    else
                        []
                Some(span, fixes)

    let lint (model: SemanticModel) : Report list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.blocksWithin
        |> List.collect (fun block ->
            block
            |> List.pairwise
            |> List.choose (fun (before, after) -> separator model.Source block before after)
            |> List.map (fun (span, fixes) ->
                { Rule = rule.Id
                  Subject = None
                  Span = span
                  Message = "Put each statement on a line of its own, rather than separating them with `;`"
                  Related = []
                  Fixes = fixes }))
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `unconstructibleType` rule: an exported `protected` or `private` type that nothing its module
/// exports gives out, so that code outside the module can name the type but never get a value of it. An exported def
/// gives one out when the type is in an output position of its type: its result, a value of it, an argument of a
/// callback the caller passes, a field of a protected type it gives out, or an argument of a context instance. A
/// context, a type the module does not export and a structural type are never reported. The rule sees one module at a
/// time, so a type another module of the package gives out is reported too. Its fix adds an exported `of` that wraps the
/// type's representation.
module UnconstructibleTypes =
    let rule =
        { Id = "unconstructibleType"
          Title = "Exported type no one outside its module can get"
          DefaultSeverity = Severity.Error
          Unnecessary = false
          Description =
            "An exported `protected` or `private` type that nothing its module exports gives out, so no code outside the module can get a value of it. Export a function that returns one." }

    /// Whether the nominal type `target` is in an output position of `ty`: one its holder gives out when `positive`, and
    /// one its holder is given when not. A protected type's fields can be read anywhere, so a nominal type that is not
    /// private gives out what its fields hold; `seen` are those already looked into.
    let rec private givesOut (target: NominalId) (seen: Set<NominalId>) (positive: bool) (ty: Ty) : bool =
        match ty with
        | TyNominal(id, _, _, _) when id = target -> positive
        | TyNominal(id, args, underlying, isPrivate) ->
            args |> List.exists (givesOut target seen positive)
            || (not isPrivate.IsPrivate && not (seen.Contains id) && givesOut target (seen.Add id) positive underlying)
        | TyFunc(input, output, contexts) ->
            givesOut target seen (not positive) input
            || givesOut target seen positive output
            || contexts |> List.exists (givesOut target seen positive)
        | _ -> Ty.exists (givesOut target seen positive) ty

    /// The type definitions and value defs of each module the file writes, the file's own and each `module name =`
    /// block's, with the module's name where it has one.
    let rec private modulesOf (name: string option) (items: TopLevelItem list) : (string option * TopLevelItem list) list =
        let blocks = items |> List.collect (function Kind (ModuleBlock(block, blockItems)) -> modulesOf (Some block) blockItems | _ -> [])
        (name, items |> List.filter (function Kind (Def _) -> true | _ -> false)) :: blocks

    /// The definition of `name.of`, which takes the type's `representation`, as written, and wraps it in the type, whose
    /// parameters are `parameters`: exported when `exported`.
    let internal ofDefinition (exported: bool) (name: string) (parameters: Identifier list) (representation: string) =
        let applied =
            match parameters with
            | [] -> name
            | _ -> $"""{name}({parameters |> String.concat ", "})"""
        let export = if exported then "export " else ""
        $"{export}def {name}.of: {representation} -> {applied} = {{ value -> {name}(value) }}"

    /// The fix: an exported `of` after the type's declaration that takes its representation, as written, and wraps it.
    let private ofStub (model: SemanticModel) (item: TopLevelItem) (name: string) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
        let indent = String(' ', item.Span.StartColumn)
        let representation = model.Source.Substring(body.Span.Start, body.Span.End - body.Span.Start)
        let stub = ofDefinition true name (List.map fst parameters) representation
        { Title = $"Export `{name}.of`, which makes a `{name}`"
          Edits = [ { Span = TextEdit.spanBetween model.Source item.Span item.Span.End item.Span.End; NewText = $"\n\n{indent}{stub}" } ] }

    let lint (model: SemanticModel) : Report list =
        let symbolWithin (item: TopLevelItem) (kind: SymbolKind) =
            model.Symbols |> Map.values |> Seq.filter (fun symbol -> symbol.Kind = kind && SourceSpan.contains item.Span symbol.Declaration) |> List.ofSeq
        modulesOf (model.Syntax |> List.tryPick (function Kind (ModuleDecl(_, name)) -> Some name | _ -> None)) model.Syntax
        |> List.collect (fun (moduleName, items) ->
            let exportedTypes =
                items
                |> List.filter (function Kind (Def (TypeDef _)) -> true | _ -> false)
                |> List.collect (fun item -> symbolWithin item TypeDefinition |> List.map (fun symbol -> item, symbol))
            let exportedValues =
                items
                |> List.filter (function Kind (Def (ValueDef _)) -> true | _ -> false)
                |> List.collect (fun item -> symbolWithin item TopLevelValue)
                |> List.filter _.IsExported
                |> List.choose (fun symbol -> model.SymbolTypes |> Map.tryFind symbol.Id)
            exportedTypes
            |> List.choose (fun (item, symbol) ->
                match item.Kind with
                | Def (TypeDef(qualified, modifiers, parameters, body)) when
                    symbol.IsExported && symbol.Name = qualified && TypeDefModifier.isNominal modifiers && not (List.contains Context modifiers)
                    ->
                    // A block's type is declared as `block.Name`, but written by its own name.
                    let name = qualified.Substring(qualified.LastIndexOf '.' + 1)
                    model.TypeDefs
                    |> Map.values
                    |> Seq.tryFind (fun info -> info.Symbol = Some symbol.Id)
                    |> Option.filter (fun info ->
                        let target = TypeDefInfo.nominalId info
                        not (exportedValues |> List.exists (givesOut target Set.empty true)))
                    |> Option.map (fun info ->
                        let kind = if info.IsPrivate then "private" else "protected"
                        let owner = moduleName |> Option.map (fun moduleName -> $"'{moduleName}'") |> Option.defaultValue "its module"
                        { Rule = rule.Id
                          Subject = Some name
                          Span = symbol.Declaration
                          Message = $"'{name}' is {kind}, and nothing {owner} exports returns one, so no code outside {owner} can get a '{name}'"
                          Related = []
                          Fixes = [ ofStub model item name parameters body ] })
                | _ -> None))
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `unusedContexts` rule: a context a def's signature requires that nothing in its body uses, so
/// that every caller must supply it for nothing (#2099). A context is used when the body names one of its members, an
/// operator among them, or calls or names what requires it: a def that requires it (`ContextTypes`), or a callee whose
/// type at the call does (`CalleeTypes`). Only a def whose value is a lambda is looked at, a local one too, and not an
/// exported one: what it requires is part of what its module offers, and may be there to leave room for a later use. A
/// context variable, `[c]`, is not a context.
module UnusedContexts =
    let rule =
        { Id = "unusedContexts"
          Title = "Unused context"
          DefaultSeverity = Severity.Warning
          Unnecessary = true
          Description =
            "A context a def's signature requires that nothing in its body uses: no member of it is read, and nothing the body calls requires it. Every caller must supply it for nothing. Exported defs are not reported." }

    /// The names of the contexts the expressions within `value`, a def's value, use: those each call or pipe in it, or
    /// one desugaring made for it, requires of its callee, those the defs it names require, and those whose members it
    /// names, an operator among them.
    let private usedIn (model: SemanticModel) (value: Expression) : Set<string> =
        let contextOfMember =
            model.TypeDefs
            |> Map.toSeq
            |> Seq.filter (fun (_, info) -> info.IsContext)
            |> Seq.collect (fun (name, info) -> info.Members |> Map.values |> Seq.map (fun symbol -> symbol, name))
            |> Map.ofSeq
        let nodes =
            SyntaxTree.expressionsWithin value
            |> List.collect (fun expr -> expr.Id :: (model.Lowered |> Map.tryFind expr.Id |> Option.defaultValue []))
        let names (contexts: Ty list) = contexts |> List.choose FunctionContexts.contextName
        let ofCallee =
            nodes
            |> List.collect (fun node ->
                match model.CalleeTypes |> Map.tryFind node |> Option.map Unifier.unalias with
                | Some (TyFunc(_, _, contexts)) -> names contexts
                | _ -> [])
        let referenced = nodes |> List.choose (fun node -> model.References |> Map.tryFind node)
        let ofDefs =
            referenced
            |> List.collect (fun symbol ->
                match model.ContextTypes |> Map.tryFind symbol, model.ImportedContextTypes |> Map.tryFind symbol with
                | Some contexts, _
                | None, Some (_, contexts) -> names contexts
                | None, None -> [])
        let members = referenced |> List.choose contextOfMember.TryFind
        Set.ofList (ofCallee @ ofDefs @ members)

    /// The name of the context `context` names, if it names one the module knows: not a context variable.
    let private contextName (model: SemanticModel) (context: TypeExpr) =
        match context.Kind with
        | TypeName name
        | TypeApply(name, _) when model.TypeDefs |> Map.tryFind name |> Option.exists _.IsContext -> Some name
        | _ -> None

    /// Each def that isn't exported, a local one too, whose value is a lambda, with its name, its signature and its value.
    let private defs (model: SemanticModel) : (string * TypeExpr * Expression) list =
        let rec topLevel (items: TopLevelItem list) =
            items
            |> List.collect (fun item ->
                match item.Kind with
                | Def(ValueDef(false, name, Some annotation, ({ Kind = Lambda _ } as value))) -> [ name, annotation, value ]
                | ModuleBlock(_, items) -> topLevel items
                | _ -> [])
        let local =
            model.Syntax
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.expressionsWithin
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements
                    |> List.choose (fun statement ->
                        match statement.Kind with
                        | DefStatement(false, name, Some annotation, ({ Kind = Lambda _ } as value)) -> Some (name, annotation, value)
                        | _ -> None)
                | _ -> [])
        topLevel model.Syntax @ local

    /// The edit that removes `context`, one of `contexts`, the contexts of `annotation`, whose type after them is
    /// `inner`: with the `, ` after it, or before it if it is the last; the whole `[...] ` if it is the only one.
    let private removal (source: string) (annotation: TypeExpr) (contexts: TypeExpr list) (inner: TypeExpr) (context: TypeExpr) : TextEdit =
        let between start end' = TextEdit.spanBetween source annotation.Span start end'
        match contexts |> List.tryFindIndex (fun other -> other.Span = context.Span) with
        | Some _ when contexts.Length = 1 -> { Span = between annotation.Span.Start inner.Span.Start; NewText = "" }
        | Some index when index < contexts.Length - 1 -> { Span = between context.Span.Start contexts.[index + 1].Span.Start; NewText = "" }
        | Some index -> { Span = between contexts.[index - 1].Span.End context.Span.End; NewText = "" }
        | None -> { Span = context.Span; NewText = "" }

    let lint (model: SemanticModel) : Report list =
        defs model
        |> List.collect (fun (name, annotation, value) ->
            match annotation.Kind with
            | TypeWithContext(contexts, inner) ->
                let used = usedIn model value
                contexts
                |> List.choose (fun context ->
                    match contextName model context with
                    | Some contextName when not (SourceSpan.isNone context.Span) && not (used.Contains contextName) ->
                        Some
                            { Rule = rule.Id
                              Subject = Some contextName
                              Span = context.Span
                              Message = $"'{contextName}' is required but not used by '{name}'"
                              Related = []
                              Fixes = [ { Title = $"Remove `{contextName}`"; Edits = [ removal model.Source annotation contexts inner context ] } ] }
                    | _ -> None)
            | _ -> [])
        |> List.sortBy (fun report -> report.Span.Start)

/// Where `@allow` suppresses linter rules: before a def, a local one too, or a top-level type, it suppresses the
/// diagnostics of the rules it names within the def (see `Attributes.allowAttribute`). The host applies it to every linter's diagnostics, and the
/// official linter's `unusedAllows` rule reports a rule an `@allow` names that suppressed nothing.
module Allows =
    let rule =
        { Id = "unusedAllows"
          Title = "`@allow` that suppresses nothing"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description = "An `@allow` names a linter rule that reports nothing in the definition after it. Remove it." }

    /// An `@allow`: the attribute, the rules it names (each as its code and where it is written), and the def it is
    /// before.
    type Allow =
        { Attribute: SourceSpan
          Rules: (Identifier * Identifier * SourceSpan) list
          Def: SourceSpan }

    let private allowOf (span: SourceSpan) (name: Identifier) (argument: Expression option) (def: SourceSpan option) =
        match def, Attributes.allowedRules argument with
        | Some def, Some rules when name = Attributes.allowAttribute -> Some { Attribute = span; Rules = rules; Def = def }
        | _ -> None

    /// The `@allow`s of `model`, at the top level, in module blocks and in blocks. Attributes may stand between one and
    /// its def.
    let collect (model: SemanticModel) : Allow list =
        let rec ofItems (items: TopLevelItem list) =
            match items with
            | [] -> []
            | Kind (Attribute(name, argument)) as attribute :: rest ->
                let def =
                    rest
                    |> List.skipWhile (function Kind (Attribute _) -> true | _ -> false)
                    |> List.tryHead
                    |> Option.bind (function Kind (Def _) as def -> Some def.Span | _ -> None)
                Option.toList (allowOf attribute.Span name argument def) @ ofItems rest
            | Kind (ModuleBlock(_, blockItems)) :: rest -> ofItems blockItems @ ofItems rest
            | _ :: rest -> ofItems rest
        let rec ofBlock (statements: Statement list) =
            match statements with
            | [] -> []
            | ({ Kind = AttributeStatement(name, argument) } as attribute) :: rest ->
                let def =
                    rest
                    |> List.skipWhile (fun statement -> match statement.Kind with AttributeStatement _ -> true | _ -> false)
                    |> List.tryHead
                    |> Option.bind (fun statement -> match statement.Kind with DefStatement _ -> Some statement.Span | _ -> None)
                Option.toList (allowOf attribute.Span name argument def) @ ofBlock rest
            | _ :: rest -> ofBlock rest
        let local = model.Syntax |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.blocksWithin |> List.collect ofBlock
        ofItems model.Syntax @ local

    /// Whether `allow` suppresses `diagnostic` by the rule it names at `ruleSpan`.
    let suppresses (allow: Allow) (linter: Identifier, rule: Identifier, _: SourceSpan) (diagnostic: Diagnostic) =
        diagnostic.Code = $"{linter}/{rule}"
        && diagnostic.Span |> Option.exists (fun span -> SourceSpan.contains allow.Def span)

    /// What `unusedAllows` says of a rule an `@allow` names that suppressed nothing, from the linters there are.
    let message (linters: Linter list) (linter: Identifier, rule: Identifier, _: SourceSpan) =
        match linters |> List.tryFind (fun known -> known.Name = linter) with
        | None -> $"There is no linter '{linter}', so `@allow({linter}/{rule})` suppresses nothing: remove it"
        | Some known when not (known.Rules |> List.exists (fun r -> r.Id = rule)) ->
            $"The linter '{linter}' has no rule '{rule}', so `@allow({linter}/{rule})` suppresses nothing: remove it"
        | Some _ -> $"`{linter}/{rule}` reports nothing in this definition, so `@allow({linter}/{rule})` suppresses nothing: remove it"

    /// The fix for a rule an `@allow` names that suppressed nothing: when it is the only one it names, removing the
    /// attribute.
    let fixes (model: SemanticModel) (allow: Allow) =
        match allow.Rules with
        | [ _ ] -> [ { Title = "Remove the `@allow`"; Edits = [ SourceEdits.removal model.Source allow.Attribute ] } ]
        | _ -> []

module Linters =
    /// The official linter, which the docs call `default`. Its rules are added one by one.
    let official =
        { Name = "default"
          Package = "nanyx.lint"
          Rules =
            [ UnusedBindings.rule; NonTailRecursion.rule; UnreachableArms.rule; UnusedImports.rule; NoTagInCommon.rule
              UnusedPrivateDefinitions.rule; UnreachableCode.rule; UseInBraces.rule; StrayDocComments.rule
              UnannotatedExports.rule; ResultOnlyTypeVariables.rule; UnusedResults.rule; UselessExpressions.rule; UnawaitedTasks.rule; ShadowedPreludeNames.rule; Semicolons.rule
              UnconstructibleTypes.rule; UnusedContexts.rule; RepeatedPulls.rule; RedundantTypeNames.rule; UndefinedPayloadArms.rule; Allows.rule ]
          Lint =
            fun model ->
                UnusedBindings.lint model @ NonTailRecursion.lint model @ UnreachableArms.lint model @ UnusedImports.lint model
                @ NoTagInCommon.lint model @ UnusedPrivateDefinitions.lint model @ UnreachableCode.lint model @ UseInBraces.lint model
                @ StrayDocComments.lint model @ UnannotatedExports.lint model @ ResultOnlyTypeVariables.lint model
                @ UnusedResults.lint model @ UselessExpressions.lint model @ UnawaitedTasks.lint model @ ShadowedPreludeNames.lint model @ Semicolons.lint model
                @ UnconstructibleTypes.lint model @ UnusedContexts.lint model @ RepeatedPulls.lint model @ RedundantTypeNames.lint model
                @ UndefinedPayloadArms.lint model }

    /// The linters the compiler has, which a manifest's `linters` or a script's `::linter` directives can turn on.
    let all = [ official ]

    /// The ids of the official linter's rules that its package, `nanyx.lint`, implements, which the compiler's own rules
    /// leave to it. The rules move to the package a group at a time; the compiler runs the rest beside it.
    let packageRules : Set<string> = Set.empty

    /// The linter `builtIn`, one of `all`, run as its package's linter `made` runs it: the package's reports, and the
    /// compiler's own for each rule the package doesn't implement yet (see `packageRules`).
    let withPackage (made: Linter) (builtIn: Linter) : Linter =
        { builtIn with Lint = fun model -> (builtIn.Lint model |> List.filter (fun report -> not (packageRules.Contains report.Rule))) @ made.Lint model }

    /// No linters: what runs where nothing turns any on. Linters are opt-in, like a package's dependencies.
    let none = { Linters = []; Severities = Map.empty }

    /// Every linter the compiler has, each rule at its default severity.
    let everyLinter = { Linters = all; Severities = Map.empty }

    /// The code of a rule of a linter.
    let code (linter: Linter) (ruleId: string) = $"{linter.Name}/{ruleId}"

    /// The rule the host reports under when a linter fails: it threw, or reported a rule it does not declare.
    /// Every linter has it, so a broken one can be turned off by its code (`default/linterFailed`) like any rule.
    let failedRule =
        { Id = "linterFailed"
          Title = "Linter failed"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description = "The linter could not check the module. Its other reports for the module are left out." }

    /// The severity of a rule of a linter under `options`, or `None` if it is turned off.
    let private severityOf (options: LinterOptions) (linter: Linter) (rule: Rule) =
        match options.Severities |> Map.tryFind (code linter rule.Id) with
        | Some configured -> configured
        | None -> Some rule.DefaultSeverity

    let private diagnostic (options: LinterOptions) (linter: Linter) (rule: Rule) (span: SourceSpan option) message related fixes =
        severityOf options linter rule
        |> Option.map (fun severity ->
            { Code = code linter rule.Id
              Severity = severity
              Title = rule.Title
              Subject = None
              Message = message
              Span = span |> Option.filter (SourceSpan.isNone >> not)
              Related = related |> List.filter (fst >> SourceSpan.isNone >> not)
              Suggestions = []
              Fixes = fixes
              Import = None
              Unnecessary = rule.Unnecessary })

    let private failed (options: LinterOptions) (linter: Linter) (reason: string) =
        diagnostic options linter failedRule None $"The linter '{linter.Name}' failed: {reason}" [] []
        |> Option.toList

    /// Runs one linter over `model`. If it throws, or reports a rule it does not declare, its reports are left out
    /// and the failure is reported instead, so that a broken linter never stops a compile.
    let private runOne (options: LinterOptions) (model: SemanticModel) (linter: Linter) : Diagnostic list =
        let rules = linter.Rules |> List.map (fun rule -> rule.Id, rule) |> Map.ofList
        match (try Ok (linter.Lint model) with ex -> Error ex.Message) with
        | Error reason -> failed options linter reason
        | Ok reports ->
            match reports |> List.tryFind (fun report -> not (rules.ContainsKey report.Rule)) with
            | Some unknown -> failed options linter $"it reported the rule '{unknown.Rule}', which it does not declare"
            | None ->
                reports
                |> List.choose (fun report ->
                    diagnostic options linter rules.[report.Rule] (Some report.Span) report.Message report.Related report.Fixes
                    |> Option.map (fun diagnostic -> { diagnostic with Subject = report.Subject }))

    /// `diagnostics` without those an `@allow` suppresses, and, when the official linter runs, its `unusedAllows`
    /// diagnostics for each rule an `@allow` names that suppressed nothing (see `Allows`).
    let private applyAllows (options: LinterOptions) (model: SemanticModel) (diagnostics: Diagnostic list) =
        let allows = Allows.collect model
        let suppressed (diagnostic: Diagnostic) =
            allows |> List.exists (fun allow -> allow.Rules |> List.exists (fun rule -> Allows.suppresses allow rule diagnostic))
        let unused =
            if not (options.Linters |> List.exists (fun linter -> linter.Name = official.Name)) then
                []
            else
                allows
                |> List.collect (fun allow ->
                    allow.Rules
                    |> List.filter (fun rule -> not (diagnostics |> List.exists (Allows.suppresses allow rule)))
                    |> List.choose (fun ((_, _, span) as rule) ->
                        diagnostic options official Allows.rule (Some span) (Allows.message (options.Linters @ all |> List.distinctBy _.Name) rule) [] (Allows.fixes model allow)))
        (diagnostics |> List.filter (suppressed >> not)) @ unused

    /// The diagnostics of every linter `options` names over `model`, at their configured severities, in the order
    /// the linters are given and then the order each reported them, without those an `@allow` suppresses.
    let run (options: LinterOptions) (model: SemanticModel) : Diagnostic list =
        options.Linters |> List.collect (runOne options model) |> applyAllows options model
