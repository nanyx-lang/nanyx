namespace NyxCompiler

open System
open Parser.Program

/// One thing an analyzer checks for. Its code is `<analyzer>/<rule>`: `default/unusedBindings`.
type Rule =
    { /// Unique within its analyzer, in camelCase: `unusedBindings`.
      Id: string
      /// Short, and the title of every diagnostic the rule reports.
      Title: string
      /// How serious the rule's diagnostics are unless configured otherwise.
      DefaultSeverity: Severity
      Description: string }

/// What analyzers know about the module they check: the module as written, and what type checking found out about
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

/// Something one of an analyzer's rules found. The host turns it into a diagnostic with the rule's code and
/// configured severity.
type Report =
    { /// The `Id` of the rule that found it.
      Rule: string
      Span: SourceSpan
      Message: string
      Related: (SourceSpan * string) list
      Fixes: Fix list }

/// A set of rules, run over each module the compiler checks. Analyzers are compiled into the compiler for now: there
/// is no way yet to write one in Nanyx (#107).
type Analyzer =
    { /// The first part of its rules' codes: `default`.
      Name: string
      Rules: Rule list
      Analyze: SemanticModel -> Report list }

/// Which analyzers run, and how serious their diagnostics are. `Severities` is keyed by code: `Some` severity
/// overrides the rule's default, and `None` turns the rule off. Until the package manifest exists, this is how a
/// project configures its analyzers.
type AnalyzerOptions =
    { Analyzers: Analyzer list
      Severities: Map<string, Severity option> }

/// The official analyzer's `unusedBindings` rule: a local def, a lambda's argument or a variable a pattern binds that
/// nothing refers to. Top-level defs are left alone, since another module may use them, and so are names starting
/// with `_`, which say they are unused on purpose.
module UnusedBindings =
    let rule =
        { Id = "unusedBindings"
          Title = "Unused binding"
          DefaultSeverity = Severity.Warning
          Description =
            "A local def, a lambda's argument or a variable a pattern binds that is never used. Names starting with `_` are not reported." }

    /// The expressions directly within `expr`, and the statements of it if it is a block. What patterns and types
    /// hold is left out: they hold no blocks.
    let rec private childrenOf (expr: Expression) : Expression list * Statement list =
        let ofUse binding =
            match binding with
            | UseValue value
            | UseContextInstance(_, value) -> value
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
        | Match(values, arms) -> values @ (arms |> List.map snd), []
        | RecordExpr fields -> fields |> List.map (function NamedField(_, value) | PositionalField value -> value), []
        | TagExpr(_, payload) -> Option.toList payload, []
        | IfExpr(condition, ifTrue, ifFalse) -> [ condition; ifTrue; ifFalse ], []
        | MemberAccess(target, _, _) -> [ target ], []
        | UseIn(binding, body) -> [ ofUse binding; body ], []
        | WorkflowBindExpr(_, value)
        | WorkflowReturnExpr value -> [ value ], []
        | InterpolatedString parts -> parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None), []

    /// The blocks of `expr` and of everything within it, each as its statements.
    let rec private blocksWithin (expr: Expression) : Statement list list =
        let children, statements = childrenOf expr
        let ofStatement (statement: Statement) =
            match statement.Kind with
            | DefStatement(_, _, _, value)
            | ExprStatement value
            | UseStatement (UseValue value)
            | UseStatement (UseContextInstance(_, value)) -> blocksWithin value
            | TypeDefStatement _
            | ImportStatement _ -> []
        (if statements.IsEmpty then [] else [ statements ])
        @ (children |> List.collect blocksWithin)
        @ (statements |> List.collect ofStatement)

    let rec private blocksOfItem (item: TopLevelItem) =
        match item.Kind with
        | Def (ValueDef(_, _, _, value))
        | Expr value -> blocksWithin value
        | ModuleBlock(_, items) -> items |> List.collect blocksOfItem
        | Def (TypeDef _)
        | ModuleDecl _
        | Import _
        | Attribute _ -> []

    /// An edit that removes `statement`'s lines: from the start of its first line to the end of its last, with a
    /// comment after it, when nothing else is written on them. Otherwise it removes the statement alone.
    let private removal (source: string) (statement: Statement) : TextEdit =
        let span = statement.Span
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

    /// The fix for an unused `symbol`. A def is removed, unless removing it would change more than the def: when it is
    /// one of the defs a destructuring def stands for (they share its span), or the last statement of its block (whose
    /// value it is). Those, and any other name, become `_`.
    let private fixFor (model: SemanticModel) (removableDefs: Map<SourceSpan, Statement list>) (symbol: Symbol) : Fix =
        let declaration = symbol.Declaration
        let rename = { Title = $"Rename `{symbol.Name}` to `_`"; Edits = [ { Span = declaration; NewText = "_" } ] }
        match symbol.Kind with
        | LocalValue ->
            let defs = removableDefs |> Map.toSeq |> Seq.tryFind (fun (span, _) -> SourceSpan.contains span declaration)
            match defs with
            | Some (_, [ def ]) -> { Title = $"Remove `{symbol.Name}`"; Edits = [ removal model.Source def ] }
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

    let analyze (model: SemanticModel) : Report list =
        let referenced = model.References |> Map.values |> Set.ofSeq
        // The local defs that are not the last statement of their block, by span: several share one when a
        // destructuring def stands for them.
        let removableDefs =
            model.Syntax
            |> List.collect blocksOfItem
            |> List.collect (fun statements -> statements |> List.truncate (statements.Length - 1))
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

module Analyzers =
    /// The official analyzer, which the docs call `default`. Its rules are added one by one.
    let official = { Name = "default"; Rules = [ UnusedBindings.rule ]; Analyze = UnusedBindings.analyze }

    /// The analyzers the compiler runs unless told otherwise.
    let all = [ official ]

    /// Every analyzer the compiler has, each rule at its default severity.
    let defaultOptions = { Analyzers = all; Severities = Map.empty }

    /// The code of a rule of an analyzer.
    let code (analyzer: Analyzer) (ruleId: string) = $"{analyzer.Name}/{ruleId}"

    /// The rule the host reports under when an analyzer fails: it threw, or reported a rule it does not declare.
    /// Every analyzer has it, so a broken one can be turned off by its code (`default/analyzerFailed`) like any rule.
    let failedRule =
        { Id = "analyzerFailed"
          Title = "Analyzer failed"
          DefaultSeverity = Severity.Warning
          Description = "The analyzer could not check the module. Its other reports for the module are left out." }

    /// The severity of a rule of an analyzer under `options`, or `None` if it is turned off.
    let private severityOf (options: AnalyzerOptions) (analyzer: Analyzer) (rule: Rule) =
        match options.Severities |> Map.tryFind (code analyzer rule.Id) with
        | Some configured -> configured
        | None -> Some rule.DefaultSeverity

    let private diagnostic (options: AnalyzerOptions) (analyzer: Analyzer) (rule: Rule) (span: SourceSpan option) message related fixes =
        severityOf options analyzer rule
        |> Option.map (fun severity ->
            { Code = code analyzer rule.Id
              Severity = severity
              Title = rule.Title
              Message = message
              Span = span |> Option.filter (SourceSpan.isNone >> not)
              Related = related |> List.filter (fst >> SourceSpan.isNone >> not)
              Suggestions = []
              Fixes = fixes })

    let private failed (options: AnalyzerOptions) (analyzer: Analyzer) (reason: string) =
        diagnostic options analyzer failedRule None $"The analyzer '{analyzer.Name}' failed: {reason}" [] []
        |> Option.toList

    /// Runs one analyzer over `model`. If it throws, or reports a rule it does not declare, its reports are left out
    /// and the failure is reported instead, so that a broken analyzer never stops a compile.
    let private runOne (options: AnalyzerOptions) (model: SemanticModel) (analyzer: Analyzer) : Diagnostic list =
        let rules = analyzer.Rules |> List.map (fun rule -> rule.Id, rule) |> Map.ofList
        match (try Ok (analyzer.Analyze model) with ex -> Error ex.Message) with
        | Error reason -> failed options analyzer reason
        | Ok reports ->
            match reports |> List.tryFind (fun report -> not (rules.ContainsKey report.Rule)) with
            | Some unknown -> failed options analyzer $"it reported the rule '{unknown.Rule}', which it does not declare"
            | None ->
                reports
                |> List.choose (fun report ->
                    diagnostic options analyzer rules.[report.Rule] (Some report.Span) report.Message report.Related report.Fixes)

    /// The diagnostics of every analyzer `options` names over `model`, at their configured severities, in the order
    /// the analyzers are given and then the order each reported them.
    let run (options: AnalyzerOptions) (model: SemanticModel) : Diagnostic list =
        options.Analyzers |> List.collect (runOne options model)
