namespace NyxCompiler

open Parser.Program

/// How serious a diagnostic is. The compiler reports `Critical` and `Error`, and `Warning` for an unlisted version a
/// build uses (`NYX0051`) and a type parameter written the old way (`NYX0282`); `Warning`, `Info` and `Hint` are
/// otherwise for linters.
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
      /// The one name the headline shows after the title, in single quotes: the `Console` of
      /// "Missing context 'Console'". None for a headline that is the title alone.
      Subject: string option
      /// The body: the details, with the actual names and types, and the advice after a colon. It doesn't repeat the
      /// headline.
      Message: string
      /// Where the node the diagnostic is about is written; none only for a problem with the whole compilation.
      Span: SourceSpan option
      /// Other places that explain it: "declared here".
      Related: (SourceSpan * string) list
      Suggestions: string list
      Fixes: Fix list
      /// For a diagnostic in a module the compiled one imports, directly or through others: the import in the
      /// compiled module that brings that module in. The editor shows it there as well as in the imported file.
      Import: SourceSpan option
      /// Whether the code it is about does nothing and can be removed, such as a context nothing uses: the editor draws
      /// it faded.
      Unnecessary: bool }

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
    let CodeInModuleBlock = code 12 "Code in a module block" Severity.Error
    let UnexpectedAttributeArgument = code 13 "Unexpected attribute argument" Severity.Error
    let PathImportWithoutAlias = code 14 "File-path import without an alias" Severity.Error
    let NamedModuleImportedByPath = code 15 "Named module imported by file path" Severity.Error
    let EntrypointImported = code 16 "Entrypoint imported" Severity.Error
    let ScriptInPackage = code 17 "Script in a package" Severity.Error
    let DuplicateModule = code 18 "Module declared by more than one file" Severity.Error
    let UnknownKeywordKind = code 19 "Unknown keyword kind" Severity.Error
    let CodeInManifest = code 20 "Code in a manifest" Severity.Error
    let UnknownManifestField = code 21 "Unknown manifest field" Severity.Error
    let MissingManifestField = code 22 "Missing manifest field" Severity.Error
    let InvalidManifestValue = code 23 "Invalid manifest value" Severity.Error
    let UnsupportedManifestField = code 24 "Manifest field not supported yet" Severity.Error
    let ModuleNotPublic = code 25 "Module not public" Severity.Error
    let IndirectDependency = code 26 "Package is not a direct dependency" Severity.Error
    let PackageCycle = code 27 "Packages depend on each other in a cycle" Severity.Error
    let PackageReachedTwice = code 28 "Package reached by two paths" Severity.Error
    let VersionNotShipped = code 29 "Version not shipped" Severity.Error
    let DefinitionWithoutValue = code 30 "Definition without a value" Severity.Error
    let EntryInLibrary = code 31 "Entry point in a library" Severity.Error
    let ContextsJoined = code 32 "Contexts joined with `&` or `+`" Severity.Error
    let LibraryExportsNothing = code 33 "Library that exports no module" Severity.Error
    let TomlManifest = code 34 "Manifest written as TOML" Severity.Error
    let OldFormDirective = code 35 "Directive written in the old form" Severity.Error
    let LintWithoutLinter = code 36 "Lint setting with no linter above it" Severity.Error
    let LookAlikeCharacter = code 37 "Character that looks like another" Severity.Error
    let GitHubReferenceNotPinned = code 38 "GitHub reference not pinned to a commit" Severity.Error
    let GitHubReferenceNotPublishable = code 39 "GitHub reference in a package to publish" Severity.Error
    let InvalidReferencedFile = code 40 "File referenced from GitHub that breaks a file's rules" Severity.Error
    let PackageNotDownloaded = code 41 "Locked package not in the package cache" Severity.Error
    let NoSourceFiles = code 42 "Sources that match no file" Severity.Error
    let SeveralEntryPoints = code 43 "More than one entry point in a package" Severity.Error
    let InvalidLockfile = code 44 "Lockfile that can't be read" Severity.Error
    let NothingToRun = code 45 "Package with nothing to run" Severity.Error
    let KeywordMemberType = code 46 "Keyword member whose type doesn't fit its kind" Severity.Error
    let VersionConflict = code 47 "Version conflict" Severity.Error
    let DependencyNotLocked = code 48 "Dependency not locked" Severity.Error
    let PackageFileNotUtf8 = code 49 "Package file that isn't UTF-8" Severity.Error
    let KeywordsOutOfOrder = code 50 "Keywords in the wrong order" Severity.Error
    /// A warning, not an error: the build goes on.
    let UnlistedVersion = code 51 "Unlisted version" Severity.Warning
    /// A package the compiler ships, at a version the package index records with another SHA-256.
    let ShippedPackageDiffers = code 303 "Shipped package that differs from the index's" Severity.Warning
    let NeedsOtherCompiler = code 52 "Package that needs another compiler" Severity.Error
    let HostNameConvention = code 53 "Host package not named for a host" Severity.Error
    let RenamedPackage = code 54 "Shipped package named by its old name" Severity.Error

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
    let DuplicateField = code 117 "Duplicate field" Severity.Error
    let UnknownPattern = code 118 "Capitalised tag that names no pattern" Severity.Error
    let AmbiguousPattern = code 119 "Tag that more than one pattern returns" Severity.Error
    let ReExportClash = code 120 "Name a whole-module re-export gives twice" Severity.Error

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
    let ScopedContextNotProtected = code 232 "Scoped context is neither protected nor private" Severity.Error
    let ScopedContextEscapes = code 233 "Scoped context escapes the call that supplies it" Severity.Error
    let NotACellArgument = code 234 "mut argument is not a cell" Severity.Error
    let MisplacedCellType = code 235 "Cell type inside another type" Severity.Error
    let UndeterminedContextType = code 236 "Context on a type no parameter or result has" Severity.Error
    let EntryDoesNotFitHost = code 237 "Entry point does not fit the host" Severity.Error
    let HostRuntimeOutsideHost = code 238 "HostRuntime required outside a host" Severity.Error
    let ScriptRunValue = code 239 "Value the script computes as it runs, named outside it" Severity.Error
    let ContextsWithoutType = code 240 "Contexts written without a type on a value" Severity.Error
    let InvalidExtern = code 241 "Invalid foreign function" Severity.Error
    let ExternWithoutHostRuntime = code 242 "Foreign function without HostRuntime" Severity.Error
    let ConflictingExterns = code 243 "Import declared with different types" Severity.Error
    let PrivateContextMember = code 244 "Private context's member called outside its module" Severity.Error
    let ContextNameAsValue = code 245 "Context instance used as a value" Severity.Error
    let UnknownContextName = code 246 "Context name that names no context the lambda is given" Severity.Error
    let ContextAsType = code 247 "Context written as a type" Severity.Error
    let InstanceOutsideUse = code 248 "Context instance written outside a use" Severity.Error
    let UseOfNonInstance = code 249 "use of something other than a context instance" Severity.Error
    let OwnInstanceReplaced = code 250 "Use of an instance for a type that has its own" Severity.Error
    let OperatorRequiredByMember = code 251 "Operator required by its member" Severity.Error
    let InvalidTypeScope = code 252 "Type in the scope of something other than one scoped context" Severity.Error
    let ContextsOnValueType = code 253 "Contexts written on a type that is not a function" Severity.Error
    let AttachedDefinitionInScope = code 254 "Attached definition already in scope for its type" Severity.Error
    let OperatorArity = code 255 "Operator that does not take two arguments" Severity.Error
    let UndeclaredTypeScope = code 256 "Type holding a scoped value without declaring its context" Severity.Error
    let NoFromArray = code 257 "Square-bracket literal of a type with no fromArray" Severity.Error
    let InvalidCollectionSpread = code 258 "Spread of something that can't be iterated" Severity.Error
    let UnionWithTwoRests = code 259 "Union with more than one rest variable" Severity.Error
    let ProtectedShape = code 260 "Value of a nominal type's shape outside its module" Severity.Error
    let PatternInsidePattern = code 261 "Custom pattern inside another pattern" Severity.Error
    let PatternRequiresContexts = code 262 "Custom pattern that requires a context" Severity.Error
    let NoMatchOutsidePattern = code 263 "`_` as a value outside a pattern's body" Severity.Error
    let PatternArguments = code 264 "Custom pattern given arguments wrongly" Severity.Error
    let InvalidAssociatedType = code 265 "Associated type declared wrongly" Severity.Error
    let AmbiguousAssociatedType = code 266 "Associated type named by more than one context" Severity.Error
    let UnknownAssociatedType = code 267 "Associated type the context does not have" Severity.Error
    let UnfixedAssociatedType = code 268 "Instance that leaves an associated type unfixed" Severity.Error
    let AssociatedTypeFixedTwice = code 269 "Instance that fixes an associated type two ways" Severity.Error
    let InvalidTypeParameter = code 270 "Type parameter declared wrongly" Severity.Error
    let MissingTypeArgument = code 271 "Type argument left out" Severity.Error
    let TypeChosenAtRunTime = code 272 "Type value not known at compile time" Severity.Error
    let TypeDescriptionPattern = code 273 "Match on a type's description with a pattern it can't have" Severity.Error
    let PackagedTypeEscapes = code 274 "A packaged type leaves the function that opened it" Severity.Error
    let ComponentRootContextsDiffer = code 275 "Component root provides other contexts than its core root" Severity.Error
    let ReservedExternModule = code 276 "Foreign function of the compiler's own module" Severity.Error
    let ComponentBoundaryType = code 277 "Type that can't cross a component's boundary" Severity.Error
    let ComponentImportOutsideComponent = code 278 "Component import called outside a component" Severity.Error
    let TaskHoldsMemory = code 279 "Task that holds what belongs to a memory block" Severity.Error
    let ParameterNamesNoField = code 280 "Lambda parameter that names no field" Severity.Error
    let TopLevelCell = code 281 "Cell declared at the top level" Severity.Error
    let OldFormTypeParameter = code 282 "Type parameter written the old way" Severity.Error
    let ValueContextTypeOpen = code 283 "Value of a type only a later use fixes" Severity.Error
    let NotOneChar = code 284 "Char literal that is not one character" Severity.Error
    let UnitsNotFromOne = code 286 "Dimension whose units aren't defined from one" Severity.Error
    let MisplacedUnitAttribute = code 287 "Unit attribute that can't apply" Severity.Error
    let UnitCycle = code 288 "Units defined in a circle" Severity.Error
    let UnitOfAnotherDimension = code 289 "Unit defined in another dimension's units" Severity.Error
    let DimensionStorageNotNumber = code 290 "Dimension stored in a type that isn't a number" Severity.Error
    let UnknownUnit = code 291 "Unknown unit" Severity.Error
    let NegativePowerOfFraction = code 292 "Negative power of a type whose fraction is another" Severity.Error
    let StorageOnlyInResult = code 293 "Dimension's storage that no parameter fixes" Severity.Error
    let ConstrainedDimensionStorage = code 294 "Storage written for a constrained dimension" Severity.Error
    let DerivedDimensionStorages = code 295 "Parts of a derived dimension stored differently" Severity.Error
    let NotADimension = code 296 "Part of a derived dimension that is no dimension" Severity.Error
    let UnitOfDerivedDimension = code 297 "Unit of a derived dimension that its parts' units don't define" Severity.Error
    let UnitWorthNothing = code 298 "Unit worth nothing" Severity.Error
    let NoStorageUnit = code 299 "Dimension with no unit to store it in" Severity.Error
    let UnitNotWholeStorageUnits = code 300 "Unit that is no whole number of the storage unit" Severity.Error

    // Calls.
    let ArgumentCount = code 304 "Call with the wrong number of arguments" Severity.Error
    let NotAFunction = code 305 "Call of a value that isn't a function" Severity.Error

    // Tests.
    let NotATest = code 310 "Test that isn't a test's type" Severity.Error
    let ReferenceToTest = code 311 "Reference to a test" Severity.Error
    let TestContextNotProvided = code 312 "Context the test host doesn't provide" Severity.Error

    // Generating code.
    let UnsupportedByBackend = code 301 "Not supported by the WebAssembly backend" Severity.Error
    /// A broken invariant of the backend: a bug in the compiler, not a feature it does not compile yet.
    let BackendInternalError = code 302 "Internal error in the WebAssembly backend" Severity.Error

    /// Every compiler code, in order.
    let all =
        [ SyntaxError; FileNotFound; ImportNotFound; ImportCycle; UnknownStandardLibraryModule
          InvalidStandardLibraryModule; UnknownAttribute; MisplacedAttribute; InvalidDirective; DirectiveInPackage
          CodeInModuleBlock; UnexpectedAttributeArgument; PathImportWithoutAlias
          NamedModuleImportedByPath; EntrypointImported; ScriptInPackage; DuplicateModule; UnknownKeywordKind
          CodeInManifest; UnknownManifestField; MissingManifestField; InvalidManifestValue; UnsupportedManifestField
          ModuleNotPublic; IndirectDependency; PackageCycle; PackageReachedTwice; VersionNotShipped; DefinitionWithoutValue; EntryInLibrary; ContextsJoined
          LibraryExportsNothing; TomlManifest; OldFormDirective; LintWithoutLinter; LookAlikeCharacter; GitHubReferenceNotPinned; GitHubReferenceNotPublishable; InvalidReferencedFile; PackageNotDownloaded; NoSourceFiles; SeveralEntryPoints; InvalidLockfile; NothingToRun; KeywordMemberType; VersionConflict; DependencyNotLocked; PackageFileNotUtf8; KeywordsOutOfOrder; UnlistedVersion; ShippedPackageDiffers; NeedsOtherCompiler; HostNameConvention; RenamedPackage; UnknownIdentifier; UnknownFunction; UnknownMember; NotExported; AmbiguousName; AmbiguousAttachedFunction
          UnknownAttachedType; UnknownContext; UnknownType; OutsideModuleBlock; NotImplemented
          DuplicateDefinition; NotABuiltInType; InitialisationCycle; InvalidTypeName; DuplicateField; UnknownPattern; AmbiguousPattern; ReExportClash
          TypeMismatch; AnnotationTooGeneral; NonExhaustiveMatch; MissingCatchAllArm; MissingContextMembers
          InvalidWorkflow; WorkflowKeywordOutsideWorkflow; InvalidBuiltInCall; InvalidRecordUpdate
          MisplacedTypeof; UndeclaredContext; NominalConstructedOutsideItsModule; WrongPatternCount
          GuardNotAPredicate; NameBoundTwiceInPattern; MisplacedTypeHole; EndlessForLoop; PositionalForNamedArgument
          UnattachableDefinition; BlockWithoutResult; MutOutsideMemory; NotACell; CellAssignmentMismatch
          UnboundContextVariable; ContextsNotAllowed; UnguardedRecursiveAlias
          LiteralOutOfRange; OptionLikeFallback; InvalidRecordSpread; ScopedContextNotProtected; ScopedContextEscapes
          NotACellArgument; MisplacedCellType; UndeterminedContextType; EntryDoesNotFitHost; HostRuntimeOutsideHost; ScriptRunValue; ContextsWithoutType
          InvalidExtern; ExternWithoutHostRuntime; ConflictingExterns; PrivateContextMember; ContextNameAsValue; UnknownContextName
          ContextAsType; InstanceOutsideUse; UseOfNonInstance; OwnInstanceReplaced; OperatorRequiredByMember; InvalidTypeScope; ContextsOnValueType; AttachedDefinitionInScope; OperatorArity
          UndeclaredTypeScope; NoFromArray; InvalidCollectionSpread; UnionWithTwoRests; ProtectedShape; PatternInsidePattern; PatternRequiresContexts; NoMatchOutsidePattern; PatternArguments
          InvalidAssociatedType; AmbiguousAssociatedType; UnknownAssociatedType; UnfixedAssociatedType; AssociatedTypeFixedTwice
          InvalidTypeParameter; MissingTypeArgument; TypeChosenAtRunTime; TypeDescriptionPattern; PackagedTypeEscapes
          ComponentRootContextsDiffer; ReservedExternModule; ComponentBoundaryType; ComponentImportOutsideComponent; TaskHoldsMemory
          ParameterNamesNoField; TopLevelCell; OldFormTypeParameter; ValueContextTypeOpen; NotOneChar
          UnitsNotFromOne; MisplacedUnitAttribute; UnitCycle; UnitOfAnotherDimension; DimensionStorageNotNumber; UnknownUnit
          NegativePowerOfFraction; StorageOnlyInResult; ConstrainedDimensionStorage; DerivedDimensionStorages; NotADimension; UnitOfDerivedDimension; UnitWorthNothing; NoStorageUnit; UnitNotWholeStorageUnits
          ArgumentCount; NotAFunction
          NotATest; ReferenceToTest; TestContextNotProvided
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
          Subject = None
          Message = message
          Span = span |> Option.filter (SourceSpan.isNone >> not)
          Related = []
          Suggestions = []
          Fixes = []
          Import = None
          Unnecessary = false }

    /// A diagnostic about the node written at `span`. A node the compiler made up has no span (`SourceSpan.none`),
    /// and gives a diagnostic without one.
    let at (code: DiagnosticCode) (span: SourceSpan) (message: string) = create code (Some span) message

    /// A diagnostic about the whole compilation rather than any node in it.
    let general (code: DiagnosticCode) (message: string) = create code None message

    /// `diagnostic` with `subject` as the name its headline shows: "Missing context 'Console'".
    let about (subject: string) (diagnostic: Diagnostic) = { diagnostic with Subject = Some subject }

    /// The syntax error `error` is (NYX0001), whose headline names the token the parser found, if it found one.
    let ofParseError (error: ParseError) =
        let diagnostic = at DiagnosticCodes.SyntaxError error.Span error.Message
        match error.Found with
        | Some found -> about found diagnostic
        | None -> diagnostic

    /// What the editor and the terminal show first: the title, then the subject, if there is one, in single quotes.
    let headline (diagnostic: Diagnostic) =
        match diagnostic.Subject with
        | Some subject -> $"{diagnostic.Title} '{subject}'"
        | None -> diagnostic.Title

    /// The diagnostic on one line, where there is room for no more: its headline, then its body after a colon.
    let oneLine (diagnostic: Diagnostic) = $"{headline diagnostic}: {diagnostic.Message}"

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

    /// `items` as a message offers them: "a", "a or b", "a, b or c".
    let listedOr (items: string list) =
        match items with
        | [] -> ""
        | [ only ] -> only
        | _ -> String.concat ", " (List.truncate (items.Length - 1) items) + " or " + List.last items

    let isError (diagnostic: Diagnostic) =
        match diagnostic.Severity with
        | Severity.Critical
        | Severity.Error -> true
        | Severity.Warning
        | Severity.Info
        | Severity.Hint -> false
