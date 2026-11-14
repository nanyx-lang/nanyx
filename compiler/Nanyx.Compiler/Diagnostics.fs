namespace NyxCompiler

open Parser.Program

/// How serious a diagnostic is. The compiler reports `Critical` and `Error`; `Warning`, `Info` and `Hint` are for linters.
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
      Fixes: Fix list
      /// For a diagnostic in a module the compiled one imports, directly or through others: the import in the
      /// compiled module that brings that module in. The editor shows it there as well as in the imported file.
      Import: SourceSpan option }

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
    let PathImportWithoutAlias = code 14 "File-path import without an alias" Severity.Error
    let NamedModuleImportedByPath = code 15 "Named module imported by file path" Severity.Error
    let EntrypointImported = code 16 "Entrypoint imported" Severity.Error
    let ScriptInPackage = code 17 "Script in a package" Severity.Error
    let DuplicateModule = code 18 "Module declared by more than one file" Severity.Error

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
    let DuplicateDefinition = code 113 "Duplicate definition" Severity.Error
    let NotABuiltInType = code 114 "Not a built-in type" Severity.Error
    let InitialisationCycle = code 115 "Initialisation cycle" Severity.Error
    let InvalidTypeName = code 116 "Invalid type name" Severity.Error

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
    let MutOutsideMemory = code 221 "mut outside a memory block" Severity.Error
    let NotACell = code 222 "Assignment to a non-cell" Severity.Error
    let CellAssignmentMismatch = code 223 "Cell assignment type mismatch" Severity.Error
    let UnboundContextVariable = code 226 "Unbound context variable" Severity.Error
    let ContextsNotAllowed = code 227 "Function requires contexts not allowed here" Severity.Error
    let UnguardedRecursiveAlias = code 228 "Type alias refers to itself outside a tag" Severity.Error
    let LiteralOutOfRange = code 229 "Literal out of range" Severity.Error
    let OptionLikeFallback = code 230 "Option-like right operand of ??" Severity.Error
    let InvalidRecordSpread = code 231 "Invalid record spread" Severity.Error
    let ScopedContextNotNominal = code 232 "Scoped context is not nominal" Severity.Error
    let ScopedContextEscapes = code 233 "Scoped context escapes the call that supplies it" Severity.Error
    let NotACellArgument = code 234 "mut argument is not a cell" Severity.Error
    let MisplacedCellType = code 235 "Cell type inside another type" Severity.Error
    let UndeterminedContextType = code 236 "Context on a type no parameter or result has" Severity.Error
    let EntryDoesNotFitHost = code 237 "Entry point does not fit the host" Severity.Error
    let HostRuntimeOutsideHost = code 238 "HostRuntime required outside a host" Severity.Error
    let ScriptRunValue = code 239 "Value the script computes as it runs, named outside it" Severity.Error

    // Generating code.
    let UnsupportedByBackend = code 301 "Not supported by the WebAssembly backend" Severity.Error
    /// A broken invariant of the backend: a bug in the compiler, not a feature it does not compile yet.
    let BackendInternalError = code 302 "Internal error in the WebAssembly backend" Severity.Error

    /// Every compiler code, in order.
    let all =
        [ SyntaxError; FileNotFound; ImportNotFound; ImportCycle; UnknownStandardLibraryModule
          InvalidStandardLibraryModule; UnknownAttribute; MisplacedAttribute; InvalidDirective; DirectiveInPackage
          UnsupportedDirective; CodeInModuleBlock; UnexpectedAttributeArgument; PathImportWithoutAlias
          NamedModuleImportedByPath; EntrypointImported; ScriptInPackage; DuplicateModule
          UnknownIdentifier; UnknownFunction; UnknownMember; NotExported; AmbiguousName; AmbiguousAttachedFunction
          UnknownAttachedType; UnknownContext; UnknownType; OutsideModuleBlock; NotImplemented
          DuplicateDefinition; NotABuiltInType; InitialisationCycle; InvalidTypeName
          TypeMismatch; AnnotationTooGeneral; NonExhaustiveMatch; MissingCatchAllArm; MissingContextMembers
          InvalidWorkflow; WorkflowKeywordOutsideWorkflow; InvalidBuiltInCall; InvalidRecordUpdate
          MisplacedTypeof; UndeclaredContext; NominalConstructedOutsideItsModule; WrongPatternCount
          GuardNotAPredicate; NameBoundTwiceInPattern; MisplacedTypeHole; EndlessForLoop; PositionalForNamedArgument
          UnattachableDefinition; BlockWithoutResult; MutOutsideMemory; NotACell; CellAssignmentMismatch
          UnboundContextVariable; ContextsNotAllowed; UnguardedRecursiveAlias
          LiteralOutOfRange; OptionLikeFallback; InvalidRecordSpread; ScopedContextNotNominal; ScopedContextEscapes
          NotACellArgument; MisplacedCellType; UndeterminedContextType; EntryDoesNotFitHost; HostRuntimeOutsideHost; ScriptRunValue
          UnsupportedByBackend; BackendInternalError ]

    /// The section of the docs' error reference about the compiler code `code`, such as
    /// https://nanyx.dev/docs/standard-tools/errors#nyx0201; none for a linter's code.
    let referenceUrl (code: string) : string option =
        if all |> List.exists (fun known -> known.Code = code) then
            Some $"https://nanyx.dev/docs/standard-tools/errors#{code.ToLowerInvariant()}"
        else
            None

module Diagnostics =
    let private create (code: DiagnosticCode) (span: SourceSpan option) (message: string) =
        { Code = code.Code
          Severity = code.Severity
          Title = code.Title
          Message = message
          Span = span |> Option.filter (SourceSpan.isNone >> not)
          Related = []
          Suggestions = []
          Fixes = []
          Import = None }

    /// A diagnostic about the node written at `span`. A node the compiler made up has no span (`SourceSpan.none`),
    /// and gives a diagnostic without one.
    let at (code: DiagnosticCode) (span: SourceSpan) (message: string) = create code (Some span) message

    /// A diagnostic about the whole compilation rather than any node in it.
    let general (code: DiagnosticCode) (message: string) = create code None message

    /// `diagnostic` with `span` as a related location, explained by `message`. A made-up span is left out.
    let withRelated (span: SourceSpan) (message: string) (diagnostic: Diagnostic) =
        if SourceSpan.isNone span then diagnostic
        else { diagnostic with Related = diagnostic.Related @ [ span, message ] }

    /// `diagnostic` as the module it is in reports it, rather than through an import of that module.
    let inItsModule (diagnostic: Diagnostic) = { diagnostic with Import = None }

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
