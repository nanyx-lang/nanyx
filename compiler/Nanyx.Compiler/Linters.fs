namespace NyxCompiler

open System
open Parser.Program

/// One thing a linter checks for. Its code is `<linter>/<rule>`: `default/unusedBindings`.
type Rule =
    { /// Unique within its linter, in camelCase: `unusedBindings`.
      Id: string
      /// Short, and the title of every diagnostic the rule reports.
      Title: string
      /// How serious the rule's diagnostics are unless configured otherwise.
      DefaultSeverity: Severity
      Description: string }

/// What linters know about the module they check: the module as written, and what type checking found out about
/// it. Desugaring does not yet record which surface node each desugared node came from (#99), so the types of nodes
/// only desugaring made (a workflow bind's call, say) cannot be looked up from the surface tree.
type SemanticModel =
    { /// The module as written: before desugaring, attributes included.
      Syntax: Module
      /// The module's text, with its line endings made `\n` as for the offsets of spans: what a fix's edits apply to.
      Source: string
      /// The type of each expression of `Syntax`, by node.
      Types: Map<NodeId, Ty>
      /// The type of each declaration the module makes, by its symbol.
      SymbolTypes: Map<SymbolId, Ty>
      /// What the module declares, and the imported definitions it refers to.
      Symbols: Map<SymbolId, Symbol>
      /// The symbol each identifier, call, pipe target and qualified name resolved to, by its node.
      References: Map<NodeId, SymbolId>
      /// The import each name brought in by an import came through, by the node that names it.
      ImportReferences: Map<NodeId, SymbolId> }

module SemanticModel =
    /// The model of a type-checked module, from its text, the module as parsed, and what checking it found.
    let ofTyped (source: string) (syntax: Module) (typed: TypedModule) =
        { Syntax = syntax
          Source = source.Replace("\r\n", "\n").Replace("\r", "\n")
          Types = typed.ExprTypes
          SymbolTypes = typed.SymbolTypes
          Symbols = typed.Symbols
          References = typed.References
          ImportReferences = typed.ImportReferences }

/// Something one of a linter's rules found. The host turns it into a diagnostic with the rule's code and
/// configured severity.
type Report =
    { /// The `Id` of the rule that found it.
      Rule: string
      Span: SourceSpan
      Message: string
      Related: (SourceSpan * string) list
      Fixes: Fix list }

/// A set of rules, run over each module the compiler checks. Linters are compiled into the compiler for now: there
/// is no way yet to write one in Nanyx (#107).
type Linter =
    { /// The first part of its rules' codes: `default`.
      Name: string
      Rules: Rule list
      Lint: SemanticModel -> Report list }

/// Which linters run, and how serious their diagnostics are. `Severities` is keyed by code: `Some` severity
/// overrides the rule's default, and `None` turns the rule off. Until the package manifest exists, this is how a
/// project configures its linters.
type LinterOptions =
    { Linters: Linter list
      Severities: Map<string, Severity option> }

/// Walking the module as written, for the rules of the official linter.
module private SyntaxTree =
    /// The expressions directly within `expr`, and the statements of it if it is a block: a guard's predicate among
    /// them. What types hold is left out: they hold no blocks.
    let childrenOf (expr: Expression) : Expression list * Statement list =
        match expr.Kind with
        | LiteralExpr _
        | UnitExpr
        | IdentifierExpr _
        | ContextMemberAccess _ -> [], []
        | FunctionCall(_, _, args)
        | ContextMemberCall(_, _, args)
        | TupleExpr args
        | ListExpr args -> args, []
        | Lambda(_, body) -> [ body ], []
        | BinaryOp(_, left, right) -> [ left; right ], []
        | Pipe(value, _, _, args) -> value :: args, []
        | Block statements -> [], statements
        | Match(values, arms) -> values @ Pattern.armExpressions arms @ (arms |> List.map snd), []
        | Except(value, arms) -> value :: Pattern.armExpressions arms @ (arms |> List.map snd), []
        | RecordExpr fields -> fields |> List.map (function NamedField(_, value) | PositionalField value -> value), []
        | TagExpr(_, payload) -> Option.toList payload, []
        | IfExpr(condition, ifTrue, ifFalse) -> [ condition; ifTrue; ifFalse ], []
        | MemberAccess(target, _, _) -> [ target ], []
        | UseIn(value, body) -> [ value; body ], []
        | WorkflowBindExpr(_, value)
        | WorkflowReturnExpr value -> [ value ], []
        | InterpolatedString parts -> parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None), []

    /// The expressions a statement holds.
    let ofStatement (statement: Statement) : Expression list =
        match statement.Kind with
        | DefStatement(_, _, _, value)
        | ExprStatement value
        | UseStatement value -> [ value ]
        | TypeDefStatement _
        | ImportStatement _ -> []

    /// `expr` and every expression within it.
    let rec expressionsWithin (expr: Expression) : Expression list =
        let children, statements = childrenOf expr
        expr :: ((children @ (statements |> List.collect ofStatement)) |> List.collect expressionsWithin)

    /// The expressions a top-level item holds, and the items of a module block.
    let rec ofItem (item: TopLevelItem) : Expression list =
        match item.Kind with
        | Def (ValueDef(_, _, _, value))
        | Expr value -> [ value ]
        | ModuleBlock(_, items) -> items |> List.collect ofItem
        | Def (TypeDef _)
        | ModuleDecl _
        | Import _
        | Attribute _ -> []

/// The edits the official linter's fixes make.
module private SourceEdits =
    /// An edit that removes what `span` covers with its lines: from the start of its first line to the end of its
    /// last, with a comment after it, when nothing else is written on them. Otherwise it removes the span alone.
    let removal (source: string) (span: SourceSpan) : TextEdit =
        let lineStart = span.Start - span.StartColumn
        let lineEnd =
            match source.IndexOf('\n', span.End) with
            | -1 -> source.Length
            | newline -> newline + 1
        let after = source.Substring(span.End, lineEnd - span.End).Trim()
        if String.IsNullOrWhiteSpace(source.Substring(lineStart, span.StartColumn)) && (after = "" || after.StartsWith "--") then
            { Span = { span with Start = lineStart; End = lineEnd; StartColumn = 0 }; NewText = "" }
        else
            { Span = span; NewText = "" }

/// The official linter's `unusedBindings` rule: a local def, a lambda's argument or a variable a pattern binds that
/// nothing refers to. Top-level defs are left alone, since another module may use them, and so are names starting
/// with `_`, which say they are unused on purpose.
module UnusedBindings =
    let rule =
        { Id = "unusedBindings"
          Title = "Unused binding"
          DefaultSeverity = Severity.Warning
          Description =
            "A local def, a lambda's argument or a variable a pattern binds that is never used. Names starting with `_` are not reported." }

    /// The blocks of `expr` and of everything within it, each as its statements.
    let rec private blocksWithin (expr: Expression) : Statement list list =
        let children, statements = SyntaxTree.childrenOf expr
        (if statements.IsEmpty then [] else [ statements ])
        @ (children |> List.collect blocksWithin)
        @ (statements |> List.collect SyntaxTree.ofStatement |> List.collect blocksWithin)

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
        | ContextMember -> rename

    let private messageFor (symbol: Symbol) =
        match symbol.Kind with
        | Parameter -> $"Argument `{symbol.Name}` is never used"
        | PatternVariable -> $"`{symbol.Name}` is introduced by a pattern but never used"
        | LocalValue
        | TopLevelValue
        | ImportedModule
        | TypeDefinition
        | ContextMember -> $"`{symbol.Name}` is never used"

    let lint (model: SemanticModel) : Report list =
        let referenced = model.References |> Map.values |> Set.ofSeq
        // The local defs, by span: several share one when a destructuring def stands for them. None ends its block,
        // since a block that ends with a def is an error (NYX0220) and linters only run on a module without errors.
        let removableDefs =
            model.Syntax
            |> List.collect SyntaxTree.ofItem
            |> List.collect blocksWithin
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
            | ContextMember -> false)
        |> Seq.filter (fun symbol -> not (symbol.Name.StartsWith "_") && not (referenced.Contains symbol.Id))
        |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
        |> Seq.map (fun symbol ->
            { Rule = rule.Id
              Span = symbol.Declaration
              Message = messageFor symbol
              Related = []
              Fixes = [ fixFor model removableDefs symbol ] })
        |> List.ofSeq

/// The official linter's `nonTailRecursion` rule: a function that calls itself other than in tail position, where
/// each call keeps a stack frame until it returns. A tail call uses no stack (`TailPosition`), so a function that
/// only calls itself in tail position can recurse to any depth. Calls within a lambda written in the function (a
/// workflow block, a `for` loop's body) are left alone: they are calls from that lambda, not the function.
module NonTailRecursion =
    let rule =
        { Id = "nonTailRecursion"
          Title = "Recursive call not in tail position"
          DefaultSeverity = Severity.Info
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
        | BinaryOp("++", Kind (ListExpr _), _) -> "its result goes into a list"
        | BinaryOp(op, _, _) -> $"its result is an operand of `{op}`"
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
        | Lambda _ -> "it is not the last thing the function does"

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
                | DefStatement(_, _, _, Kind (FunctionCall("__rec", _, [ Kind (Lambda _) as lambda ]))) -> Some (statement.Span, lambda)
                | _ -> None)
        (items |> List.collect topLevel) @ (expressions |> List.collect local)

    let lint (model: SemanticModel) : Report list =
        let expressions = model.Syntax |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
        functionsIn model.Syntax expressions
        |> List.collect (fun (declaration, lambda) ->
            match lambda.Kind with
            | Lambda(_, body) ->
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
                              Span = call.Span
                              Message =
                                $"`{symbol.Name}` calls itself here, but not in tail position: {why}. Each call keeps a stack frame until it returns, so a deep enough input runs out of stack."
                              Related = []
                              Fixes = [] }
                    | _ -> None)
            | _ -> [])
        |> List.sortBy (fun report -> report.Span.Start)

/// The official linter's `unreachableArms` rule: a match arm that can never be chosen, because the arms before it
/// already match every value it matches (`PatternSpace.coveringArms`). An arm with a guard never counts as covering
/// another, since whether it matches depends on more than the value's shape.
module UnreachableArms =
    let rule =
        { Id = "unreachableArms"
          Title = "Unreachable match arm"
          DefaultSeverity = Severity.Warning
          Description =
            "A match arm that can never be chosen, because the arms before it match every value it matches: it comes after a catch-all, after an arm that matches everything it does, or after arms that between them match every tag, boolean, int, length of list or combination of them it does." }

    /// An edit that removes the arm whose patterns are `patterns` and whose value is `body`: from its `|` to the end of
    /// its value, and its lines with them when nothing else is written on them (a comment after it goes too). `None`
    /// when no `|` comes before the arm, as for the `else` of an `if ... is`, which is no arm to remove.
    let private removal (source: string) (patterns: Pattern list) (body: Expression) : TextEdit option =
        let first = (List.head patterns).Span
        let mutable bar = first.Start - 1
        while bar >= 0 && (source.[bar] = ' ' || source.[bar] = '\t') do
            bar <- bar - 1
        if bar < 0 || source.[bar] <> '|' || SourceSpan.isNone body.Span then
            None
        else
            let lineStart = source.LastIndexOf('\n', bar) + 1
            let lineEnd =
                match source.IndexOf('\n', body.Span.End) with
                | -1 -> source.Length
                | newline -> newline + 1
            let after = source.Substring(body.Span.End, lineEnd - body.Span.End).Trim()
            if String.IsNullOrWhiteSpace(source.Substring(lineStart, bar - lineStart)) && (after = "" || after.StartsWith "--") then
                Some { Span = TextEdit.spanBetween source first lineStart lineEnd; NewText = "" }
            else
                // On a line with other arms: the arm and the space before its `|`.
                let start = if bar > 0 && source.[bar - 1] = ' ' then bar - 1 else bar
                Some { Span = TextEdit.spanBetween source first start body.Span.End; NewText = "" }

    /// Whether the arm at `index` is one the parser made up: the `()` an `if ... is` without an `else` stands for,
    /// which is reported at the `then` branch, where nothing can be done about it.
    let private isMadeUp (arms: MatchArm list) (index: int) =
        match arms with
        | [ _, thenBranch; _, (Kind UnitExpr as madeUp) ] -> index = 1 && madeUp.Span = thenBranch.Span
        | _ -> false

    let lint (model: SemanticModel) : Report list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.collect (fun expr ->
            match expr.Kind with
            | Match(values, arms) ->
                let types = values |> List.map (fun value -> model.Types |> Map.tryFind value.Id)
                // A lone `else` matches whatever every value is, as `desugar` has it for the compiler.
                let indexed = arms |> List.mapi (fun index (patterns, body) -> index, PatternSpace.ofArm values.Length patterns, body)
                let spanOf (patterns: Pattern list) = SourceSpan.between (List.head patterns).Span (List.last patterns).Span
                indexed
                |> List.choose (fun (index, patterns, body) ->
                    if index = 0 || patterns.IsEmpty || isMadeUp arms index then
                        None
                    else
                        let earlier = indexed |> List.take index |> List.map (fun (i, others, _) -> i, others)
                        PatternSpace.coveringArms earlier types patterns
                        |> Option.map (fun covering ->
                            let message =
                                match covering with
                                | [ _ ] -> "This arm can never match: an arm before it matches every value it does."
                                | _ -> "This arm can never match: the arms before it match every value it does between them."
                            { Rule = rule.Id
                              Span = spanOf patterns
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

/// The official linter's `unusedImports` rule: an import that nothing in the module refers to. A name it brings
/// in, unqualified or through its alias, and a type it brings in named in a type, are uses (`ImportReferences`).
/// Only the imports the module writes are symbols, so the prelude and the import `@entry` adds are never reported.
module UnusedImports =
    let rule =
        { Id = "unusedImports"
          Title = "Unused import"
          DefaultSeverity = Severity.Warning
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
                  Span = item.Span
                  Message = $"`{symbol.Name}` is imported but never used"
                  Related = []
                  Fixes = [ { Title = $"Remove the import of `{symbol.Name}`"; Edits = [ removal model.Source statement items item ] } ] }))
        |> List.ofSeq

/// The official linter's `noTagInCommon` rule: `==` or `!=` on two values of closed tag types that share no tag, such
/// as `#red == #some(1)`. The operands are joined, not made equal (see "Equality and well-known contexts" in the docs'
/// work in progress), so this type-checks, and is always false (or always true).
module NoTagInCommon =
    let rule =
        { Id = "noTagInCommon"
          Title = "Comparison of types with no tag in common"
          DefaultSeverity = Severity.Warning
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
            | BinaryOp(op, left, right) when EqualityOperators.isOperator op ->
                match model.Types |> Map.tryFind left.Id, model.Types |> Map.tryFind right.Id with
                | Some leftTy, Some rightTy ->
                    match closedTags leftTy, closedTags rightTy with
                    | Some leftTags, Some rightTags when (Set.intersect leftTags rightTags).IsEmpty ->
                        let tys = Unifier.tysToDisplayStrings Ascii [ leftTy; rightTy ]
                        let always = if op = EqualityOperators.equals then "false" else "true"
                        Some
                            { Rule = rule.Id
                              Span = expr.Span
                              Message = $"'{tys.[0]}' and '{tys.[1]}' have no tag in common, so this is always {always}"
                              Related = []
                              Fixes = [] }
                    | _ -> None
                | _ -> None
            | _ -> None)

module Linters =
    /// The official linter, which the docs call `default`. Its rules are added one by one.
    let official =
        { Name = "default"
          Rules = [ UnusedBindings.rule; NonTailRecursion.rule; UnreachableArms.rule; UnusedImports.rule; NoTagInCommon.rule ]
          Lint =
            fun model ->
                UnusedBindings.lint model @ NonTailRecursion.lint model @ UnreachableArms.lint model @ UnusedImports.lint model
                @ NoTagInCommon.lint model }

    /// The linters the compiler has, which a manifest's `[linters]` table or a script's `::linters` directives
    /// can turn on.
    let all = [ official ]

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
              Message = message
              Span = span |> Option.filter (SourceSpan.isNone >> not)
              Related = related |> List.filter (fst >> SourceSpan.isNone >> not)
              Suggestions = []
              Fixes = fixes })

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
                    diagnostic options linter rules.[report.Rule] (Some report.Span) report.Message report.Related report.Fixes)

    /// The diagnostics of every linter `options` names over `model`, at their configured severities, in the order
    /// the linters are given and then the order each reported them.
    let run (options: LinterOptions) (model: SemanticModel) : Diagnostic list =
        options.Linters |> List.collect (runOne options model)
