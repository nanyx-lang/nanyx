module Nanyx.Compiler.LinterTests

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

let private rule id severity =
    { Id = id; Title = $"Title of {id}"; DefaultSeverity = severity; Description = "" }

/// Reports every top-level def under `everyDef`, at a warning unless configured otherwise.
let private everyDef =
    { Name = "test"
      Rules = [ rule "everyDef" Severity.Warning; rule "neverReported" Severity.Hint ]
      Lint =
        fun model ->
            model.Syntax
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, _))) as item ->
                    Some { Rule = "everyDef"; Span = item.Span; Message = $"def {name}"; Related = []; Fixes = [] }
                | _ -> None) }

let private throwing =
    { Name = "broken"; Rules = [ rule "anything" Severity.Warning ]; Lint = fun _ -> failwith "boom" }

let private options linters severities =
    { Linters = linters; Severities = Map.ofList severities }

let private source = "def one = 1\ndef two = one + 1"

/// Compiles `source` with every linter the compiler has turned on, at its defaults, but for `unusedPrivateDefinitions`:
/// these tests write top-level defs nothing uses, and that rule has tests of its own.
let private compileWithOfficial (source: string) =
    Compiler.compileWith
        { Linters.everyLinter with Severities = Map.ofList [ "default/unusedPrivateDefinitions", None ] }
        source

/// The text `span` covers in `source`.
let private textAt (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

[<Fact>]
let ``The official linter is registered, and runs only when turned on`` () =
    let official = Assert.Single(Linters.all)
    Assert.Equal("default", official.Name)
    Assert.Equal<string list>(
        [ "unusedBindings"; "nonTailRecursion"; "unreachableArms"; "unusedImports"; "noTagInCommon"; "unusedPrivateDefinitions"; "unreachableCode"; "useInBraces"; "strayDocComments" ],
        official.Rules |> List.map (fun rule -> rule.Id))
    // Linters are opt-in: compiling without saying which to run runs none.
    Assert.Empty((Compiler.compile "def f = { x -> 1 }").Diagnostics)
    Assert.Empty((compileWithOfficial source).Diagnostics)
    let unused = Assert.Single((compileWithOfficial "def f = { x -> 1 }").Diagnostics)
    Assert.Equal("default/unusedBindings", unused.Code)

[<Fact>]
let ``A report becomes a diagnostic with the rule's code, title and default severity`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) source
    Assert.True(result.Typed.IsSome)
    Assert.Equal(2, result.Diagnostics.Length)
    let first = result.Diagnostics.Head
    Assert.Equal("test/everyDef", first.Code)
    Assert.Equal("Title of everyDef", first.Title)
    Assert.Equal(Severity.Warning, first.Severity)
    Assert.Equal("def one", first.Message)
    Assert.Equal("def one = 1", textAt source first.Span.Value)
    Assert.False(Diagnostics.isError first)

[<Fact>]
let ``A rule can be given another severity, turned off, or promoted to an error`` () =
    let severityWith configured =
        (Compiler.compileWith (options [ everyDef ] [ "test/everyDef", configured ]) source).Diagnostics
        |> List.map (fun diagnostic -> diagnostic.Severity)
    Assert.Equal<Severity list>([ Severity.Info; Severity.Info ], severityWith (Some Severity.Info))
    Assert.Empty(severityWith None)
    let promoted = Compiler.compileWith (options [ everyDef ] [ "test/everyDef", Some Severity.Error ]) source
    Assert.True(promoted.Diagnostics |> List.forall Diagnostics.isError)
    // The module still type-checked, so the editor keeps its types; the errors are what fail the compile.
    Assert.True(promoted.Typed.IsSome)
    Assert.True(promoted.TypeChecked)

[<Fact>]
let ``The linters do not look at a module that does not type-check`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) "def one = missing\ndef two = 2"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal("Unknown identifier 'missing'", diagnostic.Message)

[<Fact>]
let ``Configuring another code leaves a rule at its default`` () =
    let result = Compiler.compileWith (options [ everyDef ] [ "test/neverReported", None; "other/everyDef", Some Severity.Error ]) source
    Assert.True(result.Diagnostics |> List.forall (fun diagnostic -> diagnostic.Severity = Severity.Warning))
    Assert.Equal(2, result.Diagnostics.Length)

[<Fact>]
let ``A linter that throws is reported once and does not stop the others`` () =
    let result = Compiler.compileWith (options [ throwing; everyDef ] []) source
    Assert.True(result.Typed.IsSome)
    match result.Diagnostics with
    | failure :: rest ->
        Assert.Equal("broken/linterFailed", failure.Code)
        Assert.Equal(Severity.Warning, failure.Severity)
        Assert.Contains("'broken'", failure.Message)
        Assert.Contains("boom", failure.Message)
        Assert.Equal(None, failure.Span)
        Assert.Equal<string list>([ "test/everyDef"; "test/everyDef" ], rest |> List.map (fun diagnostic -> diagnostic.Code))
    | [] -> failwith "Expected the failure"
    // A broken linter can be turned off like any rule.
    let silenced = Compiler.compileWith (options [ throwing ] [ "broken/linterFailed", None ]) source
    Assert.Empty(silenced.Diagnostics)

[<Fact>]
let ``A linter that reports a rule it does not declare fails, and its reports are left out`` () =
    let undeclared =
        { everyDef with
            Name = "sloppy"
            Lint = fun model -> everyDef.Lint model |> List.map (fun report -> { report with Rule = "madeUp" }) }
    let diagnostic = Assert.Single(Compiler.compileWith (options [ undeclared ] []) source |> fun result -> result.Diagnostics)
    Assert.Equal("sloppy/linterFailed", diagnostic.Code)
    Assert.Contains("'madeUp'", diagnostic.Message)

[<Fact>]
let ``Linters do not run on a module that does not type-check`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) "def one = missing"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)

[<Fact>]
let ``The model is the module as written, with what type checking found`` () =
    let mutable seen = None
    let recorder =
        { Name = "recorder"
          Rules = []
          Lint = fun model -> seen <- Some model; [] }
    let source = "@entry\ndef main = {\n  def x = 1\n  x + 1\n}"
    Assert.Empty((Compiler.compileWith (options [ recorder ] []) source).Diagnostics)
    let model = seen.Value
    // Attributes are kept: the model is the syntax before `@entry` is applied.
    Assert.True(model.Syntax |> List.exists (function Kind (Attribute("entry", None)) -> true | _ -> false))
    // The `x` in `x + 1` resolves to the local `x`, which has a type.
    let local = model.Symbols |> Map.values |> Seq.find (fun symbol -> symbol.Name = "x")
    Assert.Equal(LocalValue, local.Kind)
    Assert.Contains(local.Id, model.References |> Map.values)
    Assert.True(model.SymbolTypes.ContainsKey local.Id)
    Assert.NotEmpty(model.Types)

[<Fact>]
let ``Only the module being compiled is analyzed, not the modules it imports`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"nyx-linters-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    try
        File.WriteAllText(Path.Combine(dir, "lib.nyx"), "module lib\nexport def helper = 1\n")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import lib\nimport nanyx/console\ndef answer = helper + 1\n")
        let result = Compiler.compileFileWith (options [ everyDef ] []) mainPath
        let diagnostic = Assert.Single(result.Diagnostics)
        Assert.Equal("def answer", diagnostic.Message)
        Assert.Equal(Path.GetFullPath mainPath, diagnostic.Span.Value.File)
    finally
        Directory.Delete(dir, true)

// The official linter's `unusedBindings` rule.

/// What `default/unusedBindings` reports for `source`: each message with the text it is reported at.
let private unusedIn (source: string) =
    (compileWithOfficial source).Diagnostics
    |> List.map (fun diagnostic ->
        Assert.Equal("default/unusedBindings", diagnostic.Code)
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        diagnostic.Message, textAt source diagnostic.Span.Value)

/// `source` with the edits of the only fix of the only diagnostic for it applied.
let private fixed' (source: string) =
    let diagnostic = Assert.Single((compileWithOfficial source).Diagnostics)
    let fix = Assert.Single(diagnostic.Fixes)
    applyFix source fix, fix.Title

[<Fact>]
let ``An unused local def, argument or pattern variable is reported once, at its name`` () =
    // The examples from the official linter's page in the docs.
    let source =
        "def total =\n  def taxRate = 0.2\n  100\n" +
        "def greet: (string, string) -> string = { name, title ->\n  \"Hello, {name}!\"\n}\n" +
        "def describe: #some(int) | #error(string) -> string = { response ->\n" +
        "  match response\n    | #some(value) -> \"Success\"\n    | #error(msg) -> \"Error\"\n}\n"
    Assert.Equal<(string * string) list>(
        [ "`taxRate` is never used", "taxRate"
          "Argument `title` is never used", "title"
          "`value` is introduced by a pattern but never used", "value"
          "`msg` is introduced by a pattern but never used", "msg" ],
        unusedIn source)

[<Fact>]
let ``Used names, top-level defs and names starting with an underscore are not reported`` () =
    let source =
        "def unusedTopLevel = 1\n" +
        "def f = { x, _ignored ->\n" +
        "  def doubled = x * 2\n" +
        "  def _spare = 3\n" +
        "  def g = { y -> \"{y} {doubled}\" }\n" +
        "  match [doubled]\n" +
        "    | [_first, ..._rest] -> g(1)\n" +
        "    | _ -> g(2)\n" +
        "}\n"
    Assert.Empty(unusedIn source)

[<Fact>]
let ``Names the compiler makes up are not reported`` () =
    // A destructuring def binds the whole value under a made-up name, `for` its element, a shorthand lambda its
    // argument, and a workflow bind a lambda's.
    let source =
        "def half: int -> Result(int, string) = { n -> #some(n / 2) }\n" +
        "def f = { pair ->\n" +
        "  def (a, b) = pair\n" +
        "  def doubled = [a, b] \\ list.map({ * 2 })\n" +
        "  for doubled\n    | n -> n\n" +
        "}\n" +
        "def quarter = { n -> proc {\n  def h = try half(n)\n  return try half(h)\n} }\n"
    Assert.Empty(unusedIn source)

[<Fact>]
let ``A name used only in a guard pattern's predicate is used`` () =
    let source =
        "def size: int, int -> string = { n, limit ->\n" +
        "  match n\n" +
        "    | { x -> x > limit } -> \"big\"\n" +
        "    | _ -> \"small\"\n" +
        "}\n"
    Assert.Empty(unusedIn source)

[<Fact>]
let ``The fix for an unused local def removes its line, with a comment after it`` () =
    let source = "def total = { x ->\n  def y = 1\n  def taxRate = 0.2 -- the rate\n  x + y\n}\n"
    let text, title = fixed' source
    Assert.Equal("Remove `taxRate`", title)
    Assert.Equal("def total = { x ->\n  def y = 1\n  x + y\n}\n", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)

[<Fact>]
let ``The fix for an unused argument or pattern variable renames it to an underscore`` () =
    let argument, title = fixed' "def f = { x, y -> x }"
    Assert.Equal("Rename `y` to `_`", title)
    Assert.Equal("def f = { x, _ -> x }", argument)
    let pattern, _ = fixed' "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(n) -> 1\n    | #none -> 0\n}"
    Assert.Contains("| #some(_) -> 1", pattern)
    Assert.Empty((compileWithOfficial pattern).Diagnostics)
    // After a list pattern's `...` too: without a name, `[x, ...]` would be a list of one.
    let rest, _ = fixed' "def f: list(int) -> int = { xs ->\n  match xs\n    | [x, ...rest] -> x\n    | [] -> 0\n}"
    Assert.Contains("| [x, ..._] -> x", rest)
    Assert.Empty((compileWithOfficial rest).Diagnostics)

[<Fact>]
let ``The fix for one unused name of a destructuring def renames it rather than removing the def`` () =
    let text, title = fixed' "def f = { pair ->\n  def (a, b) = pair\n  b\n}"
    Assert.Equal("Rename `a` to `_`", title)
    Assert.Equal("def f = { pair ->\n  def (_, b) = pair\n  b\n}", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)

// The official linter's `nonTailRecursion` rule.

/// What `default/nonTailRecursion` reports for `source`: each message with the text it is reported at.
let private nonTailIn (source: string) =
    (compileWithOfficial source).Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/nonTailRecursion")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Info, diagnostic.Severity)
        diagnostic.Message, textAt source diagnostic.Span.Value)

let private nonTailMessage name why =
    $"`{name}` calls itself here, but not in tail position: {why}. Each call keeps a stack frame until it returns, so a deep enough input runs out of stack."

[<Fact>]
let ``A function that calls itself other than in tail position is reported at the call, saying why`` () =
    let source =
        "def factorial: int -> int = { n -> if n <= 1 then 1 else n * factorial(n - 1) }\n" +
        "def length: list(int) -> int = {\n  | [] -> 0\n  | [_, ...tail] -> 1 + length(tail)\n}\n" +
        "def copy: list(int) -> list(int) = {\n  | [] -> []\n  | [x, ...rest] -> [x, ...copy(rest)]\n}\n" +
        "def show: int -> string = { n -> if n == 0 then \"\" else \"{show(n - 1)}.\" }\n" +
        "def steps: int -> int = { n ->\n  def before = if n == 0 then 0 else steps(n - 1)\n  before + 1\n}\n"
    Assert.Equal<(string * string) list>(
        [ nonTailMessage "factorial" "its result is an operand of `*`", "factorial(n - 1)"
          nonTailMessage "length" "its result is an operand of `+`", "length(tail)"
          nonTailMessage "copy" "its result goes into a list", "copy(rest)"
          nonTailMessage "show" "its result is interpolated into a string", "show(n - 1)"
          nonTailMessage "steps" "its result is bound to `before`", "steps(n - 1)" ],
        nonTailIn source)

[<Fact>]
let ``A local rec function that calls itself other than in tail position is reported`` () =
    let source =
        "def f: int -> int = { n ->\n" +
        "  rec down = { i -> if i == 0 then 0 else 1 + down(i - 1) }\n" +
        "  down(n)\n" +
        "}\n"
    Assert.Equal<(string * string) list>([ nonTailMessage "down" "its result is an operand of `+`", "down(i - 1)" ], nonTailIn source)

[<Fact>]
let ``Recursion in tail position is not reported`` () =
    // In the branches of an `if`, the arms of a `match`, the end of a block, and as a pipe.
    let source =
        "def countdown: (int, int) -> int = { n, acc -> if n == 0 then acc else countdown(n - 1, acc + 1) }\n" +
        "def sum: (list(int), int) -> int = {\n  | [], acc -> acc\n  | [x, ...rest], acc -> sum(rest, acc + x)\n}\n" +
        "def steps: (int, int) -> int = { n, acc ->\n  def next = n - 1\n  match n\n    | 0 -> acc\n    | _ -> next \\steps(acc + 1)\n}\n" +
        "def count: int -> int = { n ->\n  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + 1) }\n  loop(n, 0)\n}\n"
    Assert.Empty(nonTailIn source)

[<Fact>]
let ``Calls to other functions, and calls from a lambda within the function, are not reported`` () =
    // `twice` calls `inc` other than in tail position, and `again` is called from a lambda it builds, not by itself.
    let source =
        "def inc: int -> int = { n -> n + 1 }\n" +
        "def twice: int -> int = { n -> inc(inc(n)) }\n" +
        "def again: list(int) -> list(int) = { xs -> xs \\ list.map({ x -> list.length(again([x])) }) }\n"
    Assert.Empty(nonTailIn source)

// The official linter's `unreachableArms` rule.

/// What `default/unreachableArms` reports for `source`: the text of each arm it reports, with the texts of the arms
/// it says cover it.
let private unreachableIn (source: string) =
    (compileWithOfficial source).Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/unreachableArms")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        textAt source diagnostic.Span.Value, diagnostic.Related |> List.map (fst >> textAt source))

[<Fact>]
let ``An arm after a catch-all or a variable is unreachable`` () =
    // The example from the official linter's page in the docs.
    let afterWildcard = "def f: int -> string = { n ->\n  match n\n    | _ -> \"always\"\n    | 0 -> \"never reached\"\n}"
    Assert.Equal<(string * string list) list>([ "0", [ "_" ] ], unreachableIn afterWildcard)
    let diagnostic = (compileWithOfficial afterWildcard).Diagnostics |> List.find (fun d -> d.Code = "default/unreachableArms")
    Assert.Equal("This arm can never match: an arm before it matches every value it does.", diagnostic.Message)
    let afterVariable = "def f: int -> int = { n ->\n  match n\n    | _other -> 1\n    | 2 -> 2\n}"
    Assert.Equal<(string * string list) list>([ "2", [ "_other" ] ], unreachableIn afterVariable)

[<Fact>]
let ``An arm after arms that handle every tag, boolean or length of list is unreachable`` () =
    let tags = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(_n) -> 1\n    | #none -> 0\n    | _ -> 2\n}"
    Assert.Equal<(string * string list) list>([ "_", [ "#some(_n)"; "#none" ] ], unreachableIn tags)
    let booleans = "def f: bool -> int = { b ->\n  match b\n    | true -> 1\n    | false -> 0\n    | _ -> 2\n}"
    Assert.Equal<(string * string list) list>([ "_", [ "true"; "false" ] ], unreachableIn booleans)
    let lists = "def f: list(int) -> int = { xs ->\n  match xs\n    | [] -> 0\n    | [_x, ..._rest] -> 1\n    | _ -> 2\n}"
    Assert.Equal<(string * string list) list>([ "_", [ "[]"; "[_x, ..._rest]" ] ], unreachableIn lists)

[<Fact>]
let ``An arm that an earlier one matches everything of is unreachable, at any position`` () =
    let subsumed = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(_) -> 1\n    | #some(5) -> 2\n    | #none -> 0\n}"
    Assert.Equal<(string * string list) list>([ "#some(5)", [ "#some(_)" ] ], unreachableIn subsumed)
    let range = "def f: int -> int = { n ->\n  match n\n    | 1..10 -> 1\n    | 5 -> 2\n    | _ -> 0\n}"
    Assert.Equal<(string * string list) list>([ "5", [ "1..10" ] ], unreachableIn range)
    // With two values matched, arms that handle each tag of the first whatever the second is cover any later arm.
    let twoValues =
        "def f: (#some(int) | #none), bool -> int = { o, b ->\n  match o, b\n    | #some(_x), _ -> 1\n    | #none, _ -> 2\n    | _, true -> 3\n}"
    Assert.Equal<(string * string list) list>([ "_, true", [ "#some(_x), _"; "#none, _" ] ], unreachableIn twoValues)
    // Arms that cover a combination of values between them, none of them with catch-alls elsewhere.
    let combinations =
        "def f: bool, bool -> int = { a, b ->\n  match a, b\n    | true, true -> 1\n    | false, true -> 2\n    | _, false -> 3\n    | _, true -> 4\n}"
    Assert.Equal<(string * string list) list>([ "_, true", [ "true, true"; "false, true" ] ], unreachableIn combinations)
    let ranges = "def f: int -> int = { n ->\n  match n\n    | 1..5 -> 1\n    | 6..10 -> 2\n    | 3..8 -> 3\n    | _ -> 0\n}"
    Assert.Equal<(string * string list) list>([ "3..8", [ "1..5"; "6..10" ] ], unreachableIn ranges)

[<Fact>]
let ``An arm is not unreachable after guards, partial patterns or other values`` () =
    let afterGuard = "def f: int -> int = { n ->\n  match n\n    | { > 0 } -> 1\n    | 5 -> 2\n    | _ -> 3\n}"
    Assert.Empty(unreachableIn afterGuard)
    let afterPartialTag = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(5) -> 1\n    | #some(_n) -> 2\n    | #none -> 0\n}"
    Assert.Empty(unreachableIn afterPartialTag)
    // `#some(5)` leaves the other `#some`s: the catch-all after it and `#none` can match `#some(6)`.
    let catchAllAfterPartialTag = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(5) -> 1\n    | #none -> 0\n    | _ -> 2\n}"
    Assert.Empty(unreachableIn catchAllAfterPartialTag)
    let otherLiterals = "def f: int -> int = { n ->\n  match n\n    | 1 -> 1\n    | 2 -> 2\n    | _ -> 3\n}"
    Assert.Empty(unreachableIn otherLiterals)
    // `[_x, _y, ..._rest]` leaves lists of one: the catch-all after it can match.
    let listsLeft = "def f: list(int) -> int = { xs ->\n  match xs\n    | [] -> 0\n    | [_x, _y, ..._rest] -> 1\n    | _ -> 2\n}"
    Assert.Empty(unreachableIn listsLeft)

[<Fact>]
let ``An and-pattern matches what all its parts do`` () =
    // `n & 5` matches only 5, so the arm for 5 after it can never match, but the catch-all can (#427).
    let literal = "def f: int -> int = { n ->\n  match n\n    | m & 5 -> m\n    | 5 -> 2\n    | _ -> 3\n}"
    Assert.Equal<(string * string list) list>([ "5", [ "m & 5" ] ], unreachableIn literal)
    let ranges = "def f: int -> int = { n ->\n  match n\n    | 1..10 & 5..20 -> 1\n    | 6 -> 2\n    | 11 -> 3\n    | _ -> 0\n}"
    Assert.Equal<(string * string list) list>([ "6", [ "1..10 & 5..20" ] ], unreachableIn ranges)
    // A guard part covers nothing for certain.
    let guarded = "def f: int -> int = { n ->\n  match n\n    | m & { > 0 } -> m\n    | 5 -> 2\n    | _ -> 3\n}"
    Assert.Empty(unreachableIn guarded)
    // An and-pattern asked about that cannot be told apart from nothing is not reported.
    let disjoint = "def f: int -> int = { n ->\n  match n\n    | 1 & 2 -> 1\n    | _ -> 0\n}"
    Assert.Empty(unreachableIn disjoint)
    let afterCatchAll = "def f: int -> int = { n ->\n  match n\n    | _ -> 1\n    | m & { > 0 } -> m\n}"
    Assert.Equal<(string * string list) list>([ "m & { > 0 }", [ "_" ] ], unreachableIn afterCatchAll)

[<Fact>]
let ``The else of an if-is after a pattern that always matches is unreachable, but the unit an if-is makes up is not reported`` () =
    let withElse = "def f: (int, int) -> int = { p -> if p is (_a, _b) then 1 else 2 }"
    Assert.Equal<(string * string list) list>([ "2", [ "(_a, _b)" ] ], unreachableIn withElse)
    // There is no `|` arm to remove: the fix would be to drop the `else`, which is the user's to write.
    let diagnostic = (compileWithOfficial withElse).Diagnostics |> List.find (fun d -> d.Code = "default/unreachableArms")
    Assert.Empty(diagnostic.Fixes)
    Assert.Empty(unreachableIn "def f: (int, int) -> () = { p -> if p is (_a, _b) then () }")

[<Fact>]
let ``The fix for an unreachable arm removes it, with its line when it has one to itself`` () =
    let ownLine = "def f: int -> string = { n ->\n  match n\n    | _ -> \"always\"\n    | 0 -> \"never\" -- dead\n}"
    let text, title = fixed' ownLine
    Assert.Equal("Remove the unreachable arm", title)
    Assert.Equal("def f: int -> string = { n ->\n  match n\n    | _ -> \"always\"\n}", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)
    let sameLine, _ = fixed' "def f: int -> int = { | _ -> 1 | 0 -> 2 }"
    Assert.Equal("def f: int -> int = { | _ -> 1 }", sameLine)

[<Fact>]
let ``A lone else in a match on several values is unreachable after arms that match everything`` () =
    let source = "def f: int, int -> int = { x, y ->\n  match x, y\n    | 0, _ -> 0\n    | _, _ -> 1\n    | else -> 2\n}"
    Assert.Equal<(string * string list) list>([ "else", [ "_, _" ] ], unreachableIn source)
