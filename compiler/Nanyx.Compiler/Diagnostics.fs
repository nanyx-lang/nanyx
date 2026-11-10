namespace NyxCompiler

open Parser.Program

/// How serious a diagnostic is. The compiler reports only `Critical` and `Error` (there are no compiler warnings);
/// `Warning`, `Info` and `Hint` are for linters.
[<RequireQualifiedAccess>]
type Severity =
    /// Compilation cannot go on at all: there is no source to check.
    | Critical
    | Error
    | Warning
    | Info
    | Hint

/// A change to source text: `Span` is replaced by `NewText`.
type TextEdit = { Span: SourceSpan; NewText: string }

module TextEdit =
    /// The span from `start` to `end'` of `source`, in the file of `like`.
    let spanBetween (source: string) (like: SourceSpan) (start: int) (end': int) : SourceSpan =
        let lineStart = source.LastIndexOf('\n', max 0 (start - 1)) + 1
        let line = source.Substring(0, start) |> Seq.filter ((=) '\n') |> Seq.length
        { like with Start = start; End = end'; StartLine = line; StartColumn = start - (if start = 0 then 0 else lineStart) }

/// A change that resolves a diagnostic, shown to the user as `Title`.
type Fix = { Title: string; Edits: TextEdit list }

type Diagnostic =
    { /// `NYX` and four digits for the compiler's own (see `DiagnosticCodes`), `<linter>/<rule>` for a linter's.
      Code: string
      Severity: Severity
      /// Short and the same for every diagnostic with this code: "Type mismatch".
      Title: string
      /// The details, with the actual names and types.
      Message: string
      /// Where the node the diagnostic is about is written; none only for a problem with the whole compilation.
      Span: SourceSpan option
      /// Other places that explain it: "declared here".
      Related: (SourceSpan * string) list
      Suggestions: string list
      Fixes: Fix list }

/// A kind of compiler diagnostic: its code, title and severity.
type DiagnosticCode =
    { Code: string
      Title: string
      Severity: Severity }

/// Every diagnostic the compiler reports, so that codes are stable and can be documented. Codes are allocated by
/// phase: `NYX00xx` for reading a module and its imports, `NYX01xx` for resolving names, `NYX02xx` for types. A code
/// is never reused for something else once it has been released.
module DiagnosticCodes =
    let private code number title severity = { Code = $"NYX%04d{number}"; Title = title; Severity = severity }

    // Reading a module and its imports.
    let SyntaxError = code 1 "Syntax error" Severity.Error
    let FileNotFound = code 2 "File not found" Severity.Critical
    let ImportNotFound = code 3 "Import not found" Severity.Error
    let ImportCycle = code 4 "Import cycle" Severity.Error
    let UnknownStandardLibraryModule = code 5 "Unknown standard library module" Severity.Error
    let InvalidStandardLibraryModule = code 6 "Invalid standard library module" Severity.Error
    let UnknownAttribute = code 7 "Unknown attribute" Severity.Error
    let MisplacedAttribute = code 8 "Misplaced attribute" Severity.Error
    let InvalidDirective = code 9 "Invalid directive" Severity.Error
    let DirectiveInPackage = code 10 "Directive in a package" Severity.Error
    let UnsupportedDirective = code 11 "Unsupported directive" Severity.Error
    let CodeInModuleBlock = code 12 "Code in a module block" Severity.Error
    let UnexpectedAttributeArgument = code 13 "Unexpected attribute argument" Severity.Error

    // Resolving names.
    let UnknownIdentifier = code 101 "Unknown identifier" Severity.Error
    let UnknownFunction = code 102 "Unknown function" Severity.Error
    let UnknownMember = code 103 "Unknown member" Severity.Error
    let NotExported = code 104 "Not exported" Severity.Error
    let AmbiguousName = code 105 "Ambiguous name" Severity.Error
    let AmbiguousAttachedFunction = code 106 "Ambiguous attached function" Severity.Error
    let UnknownAttachedType = code 107 "Unknown attached type" Severity.Error
    let UnknownContext = code 108 "Unknown context" Severity.Error
    let UnknownType = code 109 "Unknown type" Severity.Error
    let OutsideModuleBlock = code 110 "Name from outside a module block" Severity.Error
    let NotImplemented = code 111 "Not implemented" Severity.Error

    // Types.
    let TypeMismatch = code 201 "Type mismatch" Severity.Error
    let AnnotationTooGeneral = code 202 "Annotation too general" Severity.Error
    let NonExhaustiveMatch = code 203 "Non-exhaustive match" Severity.Error
    let MissingCatchAllArm = code 204 "Missing catch-all arm" Severity.Error
    let MissingContextMembers = code 205 "Missing context" Severity.Error
    let InvalidWorkflow = code 206 "Invalid workflow" Severity.Error
    let WorkflowKeywordOutsideWorkflow = code 207 "Workflow keyword outside a workflow" Severity.Error
    let InvalidBuiltInCall = code 208 "Invalid built-in call" Severity.Error
    let InvalidRecordUpdate = code 209 "Invalid record update" Severity.Error
    let MisplacedTypeof = code 210 "Misplaced typeof" Severity.Error
    let UndeclaredContext = code 211 "Undeclared context" Severity.Error
    let NominalConstructedOutsideItsModule = code 212 "Nominal type constructed outside its module" Severity.Error
    let WrongPatternCount = code 213 "Wrong number of patterns" Severity.Error
    let GuardNotAPredicate = code 214 "Guard pattern is not a predicate" Severity.Error
    let NameBoundTwiceInPattern = code 215 "Name bound twice in a pattern" Severity.Error
    let MisplacedTypeHole = code 216 "Misplaced type hole" Severity.Error
    let EndlessForLoop = code 217 "Endless for loop" Severity.Error
    let PositionalForNamedArgument = code 218 "Named parameter passed by position" Severity.Error
    let UnattachableDefinition = code 219 "Definition cannot be attached" Severity.Error
    let BlockWithoutResult = code 220 "Block without a result" Severity.Error

    // Generating code.
    let UnsupportedByBackend = code 301 "Not supported by the WebAssembly backend" Severity.Error

    /// Every compiler code, in order.
    let all =
        [ SyntaxError; FileNotFound; ImportNotFound; ImportCycle; UnknownStandardLibraryModule
          InvalidStandardLibraryModule; UnknownAttribute; MisplacedAttribute; InvalidDirective; DirectiveInPackage
          UnsupportedDirective; CodeInModuleBlock; UnexpectedAttributeArgument
          UnknownIdentifier; UnknownFunction; UnknownMember; NotExported; AmbiguousName; AmbiguousAttachedFunction
          UnknownAttachedType; UnknownContext; UnknownType; OutsideModuleBlock; NotImplemented
          TypeMismatch; AnnotationTooGeneral; NonExhaustiveMatch; MissingCatchAllArm; MissingContextMembers
          InvalidWorkflow; WorkflowKeywordOutsideWorkflow; InvalidBuiltInCall; InvalidRecordUpdate
          MisplacedTypeof; UndeclaredContext; NominalConstructedOutsideItsModule; WrongPatternCount
          GuardNotAPredicate; NameBoundTwiceInPattern; MisplacedTypeHole; EndlessForLoop; PositionalForNamedArgument
          UnattachableDefinition; BlockWithoutResult
          UnsupportedByBackend ]

module Diagnostics =
    let private create (code: DiagnosticCode) (span: SourceSpan option) (message: string) =
        { Code = code.Code
          Severity = code.Severity
          Title = code.Title
          Message = message
          Span = span |> Option.filter (SourceSpan.isNone >> not)
          Related = []
          Suggestions = []
          Fixes = [] }

    /// A diagnostic about the node written at `span`. A node the compiler made up has no span (`SourceSpan.none`),
    /// and gives a diagnostic without one.
    let at (code: DiagnosticCode) (span: SourceSpan) (message: string) = create code (Some span) message

    /// A diagnostic about the whole compilation rather than any node in it.
    let general (code: DiagnosticCode) (message: string) = create code None message

    /// `diagnostic` with `span` as a related location, explained by `message`. A made-up span is left out.
    let withRelated (span: SourceSpan) (message: string) (diagnostic: Diagnostic) =
        if SourceSpan.isNone span then diagnostic
        else { diagnostic with Related = diagnostic.Related @ [ span, message ] }

    /// `items` as a message lists them: "a", "a and b", "a, b and c".
    let listed (items: string list) =
        match items with
        | [] -> ""
        | [ only ] -> only
        | _ -> String.concat ", " (List.truncate (items.Length - 1) items) + " and " + List.last items

    let isError (diagnostic: Diagnostic) =
        match diagnostic.Severity with
        | Severity.Critical
        | Severity.Error -> true
        | Severity.Warning
        | Severity.Info
        | Severity.Hint -> false
