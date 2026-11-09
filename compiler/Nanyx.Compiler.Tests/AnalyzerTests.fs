module Nanyx.Compiler.AnalyzerTests

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program

let private rule id severity =
    { Id = id; Title = $"Title of {id}"; DefaultSeverity = severity; Description = "" }

/// Reports every top-level def under `everyDef`, at a warning unless configured otherwise.
let private everyDef =
    { Name = "test"
      Rules = [ rule "everyDef" Severity.Warning; rule "neverReported" Severity.Hint ]
      Analyze =
        fun model ->
            model.Syntax
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, _))) as item ->
                    Some { Rule = "everyDef"; Span = item.Span; Message = $"def {name}"; Related = []; Fixes = [] }
                | _ -> None) }

let private throwing =
    { Name = "broken"; Rules = [ rule "anything" Severity.Warning ]; Analyze = fun _ -> failwith "boom" }

let private options analyzers severities =
    { Analyzers = analyzers; Severities = Map.ofList severities }

let private source = "def one = 1\ndef two = one + 1"

/// Compiles `source` with every analyzer the compiler has turned on, at its defaults.
let private compileWithOfficial (source: string) = Compiler.compileWith Analyzers.everyAnalyzer source

/// The text `span` covers in `source`.
let private textAt (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

[<Fact>]
let ``The official analyzer is registered, and runs only when turned on`` () =
    let official = Assert.Single(Analyzers.all)
    Assert.Equal("default", official.Name)
    Assert.Equal<string list>([ "unusedBindings"; "nonTailRecursion" ], official.Rules |> List.map (fun rule -> rule.Id))
    // Analyzers are opt-in: compiling without saying which to run runs none.
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
let ``The analyzers do not look at a module that does not type-check`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) "def one = missing\ndef two = 2"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal("Unknown identifier 'missing'", diagnostic.Message)

[<Fact>]
let ``Configuring another code leaves a rule at its default`` () =
    let result = Compiler.compileWith (options [ everyDef ] [ "test/neverReported", None; "other/everyDef", Some Severity.Error ]) source
    Assert.True(result.Diagnostics |> List.forall (fun diagnostic -> diagnostic.Severity = Severity.Warning))
    Assert.Equal(2, result.Diagnostics.Length)

[<Fact>]
let ``An analyzer that throws is reported once and does not stop the others`` () =
    let result = Compiler.compileWith (options [ throwing; everyDef ] []) source
    Assert.True(result.Typed.IsSome)
    match result.Diagnostics with
    | failure :: rest ->
        Assert.Equal("broken/analyzerFailed", failure.Code)
        Assert.Equal(Severity.Warning, failure.Severity)
        Assert.Contains("'broken'", failure.Message)
        Assert.Contains("boom", failure.Message)
        Assert.Equal(None, failure.Span)
        Assert.Equal<string list>([ "test/everyDef"; "test/everyDef" ], rest |> List.map (fun diagnostic -> diagnostic.Code))
    | [] -> failwith "Expected the failure"
    // A broken analyzer can be turned off like any rule.
    let silenced = Compiler.compileWith (options [ throwing ] [ "broken/analyzerFailed", None ]) source
    Assert.Empty(silenced.Diagnostics)

[<Fact>]
let ``An analyzer that reports a rule it does not declare fails, and its reports are left out`` () =
    let undeclared =
        { everyDef with
            Name = "sloppy"
            Analyze = fun model -> everyDef.Analyze model |> List.map (fun report -> { report with Rule = "madeUp" }) }
    let diagnostic = Assert.Single(Compiler.compileWith (options [ undeclared ] []) source |> fun result -> result.Diagnostics)
    Assert.Equal("sloppy/analyzerFailed", diagnostic.Code)
    Assert.Contains("'madeUp'", diagnostic.Message)

[<Fact>]
let ``Analyzers do not run on a module that does not type-check`` () =
    let result = Compiler.compileWith (options [ everyDef ] []) "def one = missing"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)

[<Fact>]
let ``The model is the module as written, with what type checking found`` () =
    let mutable seen = None
    let recorder =
        { Name = "recorder"
          Rules = []
          Analyze = fun model -> seen <- Some model; [] }
    let source = "@entry\ndef main = {\n  def x = 1\n  x + 1\n}"
    Assert.Empty((Compiler.compileWith (options [ recorder ] []) source).Diagnostics)
    let model = seen.Value
    // Attributes are kept: the model is the syntax before `@entry` is applied.
    Assert.True(model.Syntax |> List.exists (function Kind (Attribute "entry") -> true | _ -> false))
    // The `x` in `x + 1` resolves to the local `x`, which has a type.
    let local = model.Symbols |> Map.values |> Seq.find (fun symbol -> symbol.Name = "x")
    Assert.Equal(LocalValue, local.Kind)
    Assert.Contains(local.Id, model.References |> Map.values)
    Assert.True(model.SymbolTypes.ContainsKey local.Id)
    Assert.NotEmpty(model.Types)

[<Fact>]
let ``Only the module being compiled is analyzed, not the modules it imports`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"nyx-analyzers-{Guid.NewGuid():N}")
    Directory.CreateDirectory dir |> ignore
    try
        File.WriteAllText(Path.Combine(dir, "lib.nyx"), "module lib\nexport def helper = 1\n")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import \"./lib.nyx\"\nimport nanyx/console\ndef answer = helper + 1\n")
        let result = Compiler.compileFileWith (options [ everyDef ] []) mainPath
        let diagnostic = Assert.Single(result.Diagnostics)
        Assert.Equal("def answer", diagnostic.Message)
        Assert.Equal(Path.GetFullPath mainPath, diagnostic.Span.Value.File)
    finally
        Directory.Delete(dir, true)

// The official analyzer's `unusedBindings` rule.

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
    fix.Edits
    |> List.sortByDescending (fun edit -> edit.Span.Start)
    |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Span.Start) + edit.NewText + text.Substring(edit.Span.End)) source,
    fix.Title

[<Fact>]
let ``An unused local def, argument or pattern variable is reported once, at its name`` () =
    // The examples from the official analyzer's page in the docs.
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
        "  def doubled = [a, b] \\ List.map({ * 2 })\n" +
        "  for doubled\n    | n -> n\n" +
        "}\n" +
        "def quarter = { n -> proc {\n  def h = try half(n)\n  return try half(h)\n} }\n"
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

[<Fact>]
let ``The fix for an unused def that ends its block renames it, since the block's value is the def`` () =
    let text, title = fixed' "def f = { x ->\n  def y = x\n}"
    Assert.Equal("Rename `y` to `_`", title)
    Assert.Equal("def f = { x ->\n  def _ = x\n}", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)

// The official analyzer's `nonTailRecursion` rule.

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
        "def again: list(int) -> list(int) = { xs -> xs \\ List.map({ x -> List.length(again([x])) }) }\n"
    Assert.Empty(nonTailIn source)
