namespace NyxCompiler

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
    /// The model of a type-checked module, from the module as parsed and what checking it found.
    let ofTyped (syntax: Module) (typed: TypedModule) =
        { Syntax = syntax
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

module Analyzers =
    /// The official analyzer, which the docs call `default`. Its rules are added one by one.
    let official = { Name = "default"; Rules = []; Analyze = fun _ -> [] }

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
