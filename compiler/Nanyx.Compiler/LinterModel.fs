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
      /// Whether what the rule reports is code that does nothing, which an editor draws faded (see
      /// `Diagnostic.Unnecessary`).
      Unnecessary: bool
      Description: string }

/// What linters know about the module they check: the module as written, and what type checking found out about
/// it. The types of nodes only desugaring made (a workflow bind's call, say) are found through `Lowered`.
type SemanticModel =
    { /// The file the module was compiled as, as its spans name it: empty for source compiled directly.
      File: string
      /// The module as written: before desugaring, attributes included.
      Syntax: Module
      /// The module's text, with its line endings made `\n` as for the offsets of spans: what a fix's edits apply to.
      Source: string
      /// The type of each expression of `Syntax`, by node, and of each expression desugaring made up.
      Types: Map<NodeId, Ty>
      /// The expressions desugaring and `@entry` made up for each node of `Syntax`, by its node, in the order they
      /// were made: a workflow bind's are the call to its builder's member and the continuation passed to it.
      Lowered: Map<NodeId, NodeId list>
      /// The type of each declaration the module makes, by its symbol.
      SymbolTypes: Map<SymbolId, Ty>
      /// The contexts each of the module's own top-level defs requires, written or inferred, by its symbol, as
      /// `TypedModule.ContextTypes` has them: over the variables of its type in `SymbolTypes`.
      ContextTypes: Map<SymbolId, Ty list>
      /// What the module declares, and the imported definitions it refers to.
      Symbols: Map<SymbolId, Symbol>
      /// The types and contexts the module can name, by the names it writes them by (see `TypedModule.TypeDefs`).
      TypeDefs: Map<string, TypeDefInfo>
      /// The symbol each identifier, call, pipe target and qualified name resolved to, by its node, as the source
      /// writes it (see `WrittenReferences`).
      References: Map<NodeId, SymbolId>
      /// The symbol each identifier, call and pipe target calls, by its node: as `References`, but for a call of a def
      /// the compiler made in place of the one written, as a def that starts tasks calls the def that runs its body in
      /// its caller's scope (see `TaskScopes`), which is to that made def.
      CalledReferences: Map<NodeId, SymbolId>
      /// The import each name brought in by an import came through, by the node that names it.
      ImportReferences: Map<NodeId, SymbolId>
      /// Where each call to a function that requires a parameterised context finds each instance, by the call's node
      /// (as `TypedModule.ContextInstances` has it): an instance built from attached definitions names them.
      ContextInstances: Map<NodeId, ContextInstance option list>
      /// What provides each context a call requires, by the call's node (as `TypedModule.ContextProviders` has it).
      ContextProviders: Map<NodeId, ProvidedContext list>
      /// The callee's type at each call and pipe, by the call's node, with the contexts it requires there.
      CalleeTypes: Map<NodeId, Ty>
      /// The types of the defs the imports bring in, with the contexts they require, by their symbols.
      ImportedContextTypes: Map<SymbolId, Ty * Ty list>
      /// The pipes whose target is written with a type's name that the name without it reaches as well, by the pipe's
      /// node: `xs \list.map` where `xs` is a list (see `InferState.RedundantTypeNames`).
      RedundantTypeNames: Set<NodeId>
      /// The module's doc comments, each with what it documents, found from the parse only when a rule asks.
      DocComments: Lazy<DocComment list> }

module SemanticModel =
    /// The model of a type-checked module, from the file it was compiled as (empty for source compiled directly), its
    /// text, the module as parsed, the expressions made up for its nodes, its doc comments, and what checking it found.
    let ofTyped (file: string) (source: string) (syntax: Module) (lowered: Map<NodeId, NodeId list>) (docComments: Lazy<FileDocComments>) (redundantTypeNames: Set<NodeId>) (typed: TypedModule) =
        let source = source.Replace("\r\n", "\n").Replace("\r", "\n")
        { File = file
          Syntax = syntax
          Source = source
          Types = typed.ExprTypes
          Lowered = lowered
          SymbolTypes = typed.SymbolTypes
          ContextTypes = typed.ContextTypes
          Symbols = typed.Symbols
          TypeDefs = typed.TypeDefs
          References = WrittenReferences.ofModule typed
          CalledReferences = typed.References
          ImportReferences = typed.ImportReferences
          ContextInstances = typed.ContextInstances
          ContextProviders = typed.ContextProviders
          CalleeTypes = typed.CalleeTypes
          ImportedContextTypes = typed.ImportedContextTypes
          RedundantTypeNames = redundantTypeNames
          DocComments = lazy docComments.Value.Comments }

/// Something one of a linter's rules found. The host turns it into a diagnostic with the rule's code and
/// configured severity.
type Report =
    { /// The `Id` of the rule that found it.
      Rule: string
      Span: SourceSpan
      Message: string
      Related: (SourceSpan * string) list
      Fixes: Fix list
      /// The one name the diagnostic's headline shows after the rule's title: the binding an `unusedBindings`
      /// report is about. None for a headline that is the title alone.
      Subject: string option }

/// A set of rules, run over each module the compiler checks: one compiled into the compiler (see `Linters.all`), or a
/// linter package's lint build, which `LintBuilds.make` makes one of.
type Linter =
    { /// The first part of its rules' codes: `default`.
      Name: string
      /// The package a manifest's `linters` names it by: `nanyx.lint`.
      Package: string
      Rules: Rule list
      Lint: SemanticModel -> Report list }

/// Which linters run, and how serious their diagnostics are. `Severities` is keyed by code: `Some` severity
/// overrides the rule's default, and `None` turns the rule off. A manifest's `linters` and a script's `::linter`
/// directives come to these.
type LinterOptions =
    { Linters: Linter list
      Severities: Map<string, Severity option> }

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
