module TranspilerWasmTests.Run

open System
open System.Diagnostics
open System.IO
open Xunit

/// What `nanyx run <file>` produced.
type private RunOutcome =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

/// Runs the real CLI (built next to the tests through the project reference) in a child process, so the
/// program's stdout, the diagnostics on stderr and the exit code are all observed exactly as a user would.
/// `standardInput` is written to the program's stdin, which is then closed.
let private nanyxRunWithInput (standardInput: string) (inputPath: string) : RunOutcome =
    let cliPath = Path.Combine(AppContext.BaseDirectory, "nanyx.dll")
    if not (File.Exists cliPath) then
        failwith $"The nanyx CLI was not found at {cliPath}"
    let startInfo =
        ProcessStartInfo("dotnet", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    for argument in [ cliPath; "run"; inputPath ] do
        startInfo.ArgumentList.Add argument
    use proc = Process.Start startInfo
    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    proc.StandardInput.Write standardInput
    proc.StandardInput.Close()
    proc.WaitForExit()
    { ExitCode = proc.ExitCode
      StandardOutput = stdout.Result
      StandardError = stderr.Result }

let private nanyxRun (inputPath: string) : RunOutcome = nanyxRunWithInput "" inputPath

let private runE2E testName =
    nanyxRun (Path.Combine(__SOURCE_DIRECTORY__, testName, "input.nyx"))

let private runSourceWithInput (standardInput: string) (source: string) : RunOutcome =
    let inputPath = Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(inputPath, source)
    try nanyxRunWithInput standardInput inputPath
    finally File.Delete inputPath

let private runSource (source: string) : RunOutcome = runSourceWithInput "" source

let private assertRan (expectedOutput: string) (outcome: RunOutcome) =
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(expectedOutput, outcome.StandardOutput)
    Assert.Equal("", outcome.StandardError)

[<Fact>]
let ``Run - script hello world prints the greeting`` () =
    runE2E "twentythree" |> assertRan "Hello, Nanyx!\n"

[<Fact>]
let ``Run - project hello world runs the entry point`` () =
    runE2E "twentyfour" |> assertRan "Hello, World!\n"

[<Fact>]
let ``Run - dbg prints integers, strings and tags in order`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def main = {\n" +
        "  dbg(42)\n" +
        "  dbg(-7)\n" +
        "  dbg(\"text\")\n" +
        "  dbg(#ok)\n" +
        "  dbg(#some(5))\n" +
        "  println(\"done\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "dbg: 42\ndbg: -7\ndbg: text\ndbg: #ok\ndbg: #some(5)\ndone\n"

[<Fact>]
let ``Run - console print and println write to stdout`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "print(\"a\")\n" +
        "print(\"b\")\n" +
        "println(\"\")\n" +
        "println(\"c\")\n"
    runSource source |> assertRan "ab\nc\n"

[<Fact>]
let ``Run - a function requiring Console gets the runtime's instance`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def greet: [Console] string -> () = { name -> println(\"Hello, \" ++ name) }\n" +
        "\n" +
        "greet(\"Nanyx\")\n"
    runSource source |> assertRan "Hello, Nanyx\n"

[<Fact>]
let ``Run - a use binding overrides the runtime's Console`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def greet: [Console] string -> () = { name -> println(name) }\n" +
        "\n" +
        "def main = {\n" +
        "  use (println = { s -> print(\"[\" ++ s ++ \"]\") }, print = { s -> print(s) })\n" +
        "  greet(\"quiet\")\n" +
        "}\n" +
        "\n" +
        "main()\n" +
        "greet(\"loud\")\n"
    runSource source |> assertRan "[quiet]loud\n"

/// A context with one member, and a function that requires it, for the tests of what a `use` can bind (#88).
let private myConsolePrelude =
    "import nanyx/console\n" +
    "\n" +
    "context MyConsole = (p: string -> ())\n" +
    "\n" +
    "def greet: [MyConsole] string -> () = { name -> p(name) }\n" +
    "\n"

[<Fact>]
let ``Run - a top-level function as a use binding`` () =
    let source =
        myConsolePrelude +
        "@entry\n" +
        "def main = {\n" +
        "  use MyConsole(\n" +
        "    p = println\n" +
        "  )\n" +
        "  greet(\"Nanyx\")\n" +
        "}\n"
    runSource source |> assertRan "Nanyx\n"

[<Fact>]
let ``Run - a use binding lambda that captures a local`` () =
    let source =
        myConsolePrelude +
        "@entry\n" +
        "def main = {\n" +
        "  def prefix = \">> \"\n" +
        "  use MyConsole(\n" +
        "    p = { s -> println(\"{prefix}{s}\") }\n" +
        "  )\n" +
        "  greet(\"captured\")\n" +
        "}\n"
    runSource source |> assertRan ">> captured\n"

[<Fact>]
let ``Run - a parameter or local holding a function as a use binding`` () =
    let source =
        myConsolePrelude +
        "def viaParameter: (string -> ()) -> () = { f ->\n" +
        "  use MyConsole(p = f)\n" +
        "  greet(\"parameter\")\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def shout = { s -> println(s ++ \"!\") }\n" +
        "  use MyConsole(p = shout)\n" +
        "  greet(\"local\")\n" +
        "  viaParameter({ s -> println(\"[\" ++ s ++ \"]\") })\n" +
        "}\n"
    runSource source |> assertRan "local!\n[parameter]\n"

[<Fact>]
let ``Run - use bindings of parameterised contexts and records`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "context Show(a) = (show: a -> string)\n" +
        "context Ask = (ask: () -> int)\n" +
        "\n" +
        "def describe: [Show(a) + Show(b)] (a, b) -> string = { x, y -> Show(a).show(x) ++ \" & \" ++ Show(b).show(y) }\n" +
        "def twice: [Ask] () -> int = { ask() + ask() }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def unit = \"cm\"\n" +
        "  use Show(int) = ( show = { n -> \"{n}{unit}\" } )\n" +
        "  use Show(string) = ( show = { s -> s } )\n" +
        "  println(describe(3, \"three\"))\n" +
        "  def answers = (ask = { 21 })\n" +
        "  use answers\n" +
        "  println(\"{twice()}\")\n" +
        "}\n"
    runSource source |> assertRan "3cm & three\n42\n"

[<Fact>]
let ``Run - string concatenation and equality`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def name = \"Nanyx\"\n" +
        "println(\"Hello, \" ++ name ++ \"!\")\n" +
        "println(if name == \"Nanyx\" then \"same\" else \"different\")\n" +
        "println(if name != \"Nanyx\" then \"different\" else \"same\")\n" +
        "println(if \"a\" ++ \"b\" == \"ab\" then \"concat works\" else \"concat broken\")\n"
    runSource source |> assertRan "Hello, Nanyx!\nsame\nsame\nconcat works\n"

[<Fact>]
let ``Run - text upper-cases and prints`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.uppercase(\"hello, nanyx!\"))\n" +
        "\"shout\" \\uppercase \\println\n"
    runSource source |> assertRan "HELLO, NANYX!\nSHOUT\n"

[<Fact>]
let ``Run - a pipe passes its value in front of several arguments`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def digits: int, int, int -> int = { a, b, c -> a * 100 + b * 10 + c }\n" +
        "def digitsThen: int, int, int, (int -> int) -> int = { a, b, c, h -> h(digits(a, b, c)) }\n" +
        "\n" +
        "println(\"{1 \\digits(2, 3)}\")\n" +
        "println(\"{1 \\digitsThen(2, 3) { + 1000 }}\")\n"
    runSource source |> assertRan "123\n1123\n"

[<Fact>]
let ``Run - a function takes positional and named arguments together, called or piped into`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def stepped: (int, step: int) -> int = { a, step -> a * 10 + step }\n" +
        "def digits: (int, int, last: int) -> int = { a, b, last -> a * 100 + b * 10 + last }\n" +
        "\n" +
        "println(\"{stepped(1, step = 2)}\")\n" +
        "println(\"{1 \\stepped(step = 2)}\")\n" +
        "println(\"{digits(1, 2, last = 3)}\")\n" +
        "println(\"{1 \\digits(2, last = 3)}\")\n"
    runSource source |> assertRan "12\n12\n123\n123\n"

[<Fact>]
let ``Run - text length, lowercase, trim, repeat and substring`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "dbg(String.length(\"\"))\n" +
        "dbg(String.length(\"hello\"))\n" +
        "println(String.lowercase(\"Hello, NANYX!\"))\n" +
        "println(\"[\" ++ String.trim(\"  padded \") ++ \"]\")\n" +
        "println(\"[\" ++ String.trim(\"   \") ++ \"]\")\n" +
        "println(String.repeat(\"ab\", 3))\n" +
        "println(\"[\" ++ String.repeat(\"ab\", 0) ++ \"]\")\n" +
        "println(String.substring(\"nanyx\", 1, 3))\n" +
        "println(String.substring(\"nanyx\", 3, 10))\n"
    runSource source |> assertRan "dbg: 0\ndbg: 5\nhello, nanyx!\n[padded]\n[]\nababab\n[]\nany\nyx\n"

[<Fact>]
let ``Run - text startsWith, endsWith and contains`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "show(String.startsWith(\"nanyx\", \"nan\"))\n" +
        "show(String.startsWith(\"nanyx\", \"nyx\"))\n" +
        "show(String.startsWith(\"nan\", \"nanyx\"))\n" +
        "show(String.endsWith(\"nanyx\", \"nyx\"))\n" +
        "show(String.endsWith(\"nanyx\", \"nan\"))\n" +
        "show(String.contains(\"hello world\", \"o w\"))\n" +
        "show(String.contains(\"hello world\", \"xyz\"))\n" +
        "show(String.contains(\"hello\", \"\"))\n"
    runSource source |> assertRan "yes\nno\nno\nyes\nno\nyes\nno\nyes\n"

[<Fact>]
let ``Run - text indexOf, lastIndexOf and count`` () =
    // `show` matches on the result: `dbg` of a tag returned from a call prints its raw encoding.
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: (#some(int) | #notFound) -> () = {\n" +
        "  | #some(i) -> println(\"#some({i})\")\n" +
        "  | #notFound -> println(\"#notFound\")\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  show(String.indexOf(\"hello\", \"ll\"))\n" +
        "  show(String.indexOf(\"abcabc\", \"c\"))\n" +
        "  show(String.indexOf(\"hello\", \"xyz\"))\n" +
        "  show(String.indexOf(\"lo\", \"hello\"))\n" +
        "  show(String.indexOf(\"hello\", \"\"))\n" +
        "  show(String.lastIndexOf(\"abcabc\", \"bc\"))\n" +
        "  show(String.lastIndexOf(\"abc\", \"\"))\n" +
        "  show(String.lastIndexOf(\"ab\", \"abc\"))\n" +
        "  dbg(String.count(\"banana\", \"an\"))\n" +
        "  dbg(String.count(\"aaaa\", \"aa\"))\n" +
        "  dbg(String.count(\"abc\", \"\"))\n" +
        "}\n"
    runSource source
    |> assertRan (
        "#some(2)\n#some(2)\n#notFound\n#notFound\n#some(0)\n" +
        "#some(4)\n#some(3)\n#notFound\n" +
        "dbg: 2\ndbg: 2\ndbg: 0\n")

[<Fact>]
let ``Run - text replace, removePrefix, removeSuffix, padStart and padEnd`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.replace(\"hello world\", \"o\", \"0\"))\n" +
        "println(String.replace(\"a-b-c\", \"-\", \" - \"))\n" +
        "println(String.replace(\"abc\", \"\", \"x\"))\n" +
        "println(String.removePrefix(\"nanyx\", \"nan\"))\n" +
        "println(String.removePrefix(\"nanyx\", \"nyx\"))\n" +
        "println(String.removeSuffix(\"nanyx\", \"nyx\"))\n" +
        "println(String.padStart(\"7\", 3, \"0\"))\n" +
        "println(String.padEnd(\"ab\", 5, \"xy\"))\n" +
        "println(String.padStart(\"long\", 2, \"0\"))\n"
    runSource source |> assertRan "hell0 w0rld\na - b - c\nabc\nyx\nnanyx\nna\n007\nabxyx\nlong\n"

[<Fact>]
let ``Run - text trimStart, trimEnd, isEmpty and isBlank`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def show: bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "println(\"[\" ++ String.trimStart(\" \t x \") ++ \"]\")\n" +
        "println(\"[\" ++ String.trimEnd(\" x \r\n\") ++ \"]\")\n" +
        "show(String.isEmpty(\"\"))\n" +
        "show(String.isEmpty(\" \"))\n" +
        "show(String.isBlank(\" \t\n\"))\n" +
        "show(String.isBlank(\" x \"))\n"
    runSource source |> assertRan "[x ]\n[ x]\nyes\nno\nyes\nno\n"

[<Fact>]
let ``Run - text lines and words`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "println(String.join(String.lines(\"a\nb\r\nc\n\"), \"|\"))\n" +
        "println(String.join(String.lines(\"a\n\nb\"), \"|\"))\n" +
        "dbg(String.length(String.concat(String.lines(\"\"))))\n" +
        "println(String.join(String.words(\"  one two\tthree\n\"), \"|\"))\n" +
        "dbg(String.length(String.concat(String.words(\"   \"))))\n"
    runSource source |> assertRan "a|b|c\na||b\ndbg: 0\none|two|three\ndbg: 0\n"

[<Fact>]
let ``Run - text split, join and concat`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def parts = String.split(\"a,b,,c\", \",\")\n" +
        "println(String.join(parts, \" | \"))\n" +
        "println(String.join(String.split(\"no separators\", \"-\"), \"+\"))\n" +
        "println(String.join(String.split(\"a--b\", \"--\"), \"/\"))\n" +
        "println(\"[\" ++ String.join([], \", \") ++ \"]\")\n" +
        "println(String.concat([\"Hello\", \", \", \"World\"]))\n" +
        "println(String.join(String.split(\"x\", \"\"), \"?\"))\n"
    runSource source |> assertRan "a | b |  | c\nno separators\na/b\n[]\nHello, World\nx\n"

[<Fact>]
let ``Run - list literals and patterns`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: list(int) -> string = { xs ->\n" +
        "  match xs\n" +
        "  | [] -> \"empty\"\n" +
        "  | [x] -> \"one\"\n" +
        "  | [1, y] -> \"one then another\"\n" +
        "  | [x, ...rest] -> \"many\"\n" +
        "}\n" +
        "def sum: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "  | [] -> 0\n" +
        "  | [x, ...rest] -> x + sum(rest)\n" +
        "}\n" +
        "println(describe([]))\n" +
        "println(describe([7]))\n" +
        "println(describe([1, 2]))\n" +
        "println(describe([2, 2, 2]))\n" +
        "dbg(sum([1, 2, 3, 4]))\n"
    runSource source |> assertRan "empty\none\none then another\nmany\ndbg: 10\n"

[<Fact>]
let ``Run - list spread in expressions`` () =
    // Regression: `[1, ...rest]` worked as a pattern but not as an expression (#44).
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def sum: list(int) -> int = {\n" +
        "  | [] -> 0\n" +
        "  | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def xs = [1, ...[2, 3]]\n" +
        "  println(\"{sum(xs)}\")\n" +
        "  def names = [\"b\"]\n" +
        "  println(String.join([\"a\", ...names, \"c\", ...[], ...[\"d\"]], \"-\"))\n" +
        "}\n"
    runSource source |> assertRan "6\na-b-c-d\n"

[<Fact>]
let ``Run - multi-argument pattern lambdas`` () =
    // The two-parameter `{ | [], _ -> ... | [head, ...tail], f -> ... }` form from the pattern-matching page (#45).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def map: (list(int), (int -> int)) -> list(int) = {\n" +
        "  | [], _ -> []\n" +
        "  | [head, ...tail], f -> [f(head), ...map(tail, f)]\n" +
        "}\n" +
        "def sum: list(int) -> int = {\n" +
        "  | [] -> 0\n" +
        "  | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{sum(map([1, 2, 3], { * 10 }))}\")\n" +
        "}\n"
    runSource source |> assertRan "60\n"

[<Fact>]
let ``Run - ++ joins lists`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def front = [\"a\", \"b\"]\n" +
        "println(String.join(front ++ [\"c\"] ++ [], \"-\"))\n" +
        "println(String.join([] ++ front, \"-\"))\n" +
        "dbg(String.length(\"ab\" ++ \"cd\" ++ \"e\"))\n"
    runSource source |> assertRan "a-b-c\na-b\ndbg: 5\n"

[<Fact>]
let ``Run - an annotated zero-parameter function returning a tag union`` () =
    // The `() -> ...` annotation names a unit input for a lambda with no parameters; the backend used to
    // crash with a negative padding count instead of compiling it.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def getMessage: () -> #some(string) | #noMessage = {\n" +
        "  def message = \"Hello, from Nanyx!\"\n" +
        "  #some(message)\n" +
        "}\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match getMessage()\n" +
        "    | #some(_) -> println(\"There was a message\")\n" +
        "    | #noMessage -> println(\"No message available\")\n" +
        "}\n"
    runSource source |> assertRan "There was a message\n"

[<Fact>]
let ``Run - a function annotated as returning a function returns a closure`` () =
    // `int -> (int -> int)` is one parameter returning a function; the backend used to crash with a
    // negative padding count on the annotation, then reported closures as unsupported (#29).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def makeAdder: int -> (int -> int) = { x -> { y -> x + y } }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def add5 = makeAdder(5)\n" +
        "  dbg(add5(10))\n" +
        "}\n"
    runSource source |> assertRan "dbg: 15\n"

[<Fact>]
let ``Run - a lambda argument's inferred parameter types`` () =
    // A lambda passed to a call is lifted to a function of its own; its parameters' types come from the typed
    // call it was an argument of, so `p.x` has a record layout and `"{n}"` an interpolation type.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type P = (x: int)\n" +
        "def apply: P, (P -> int) -> int = { p, f -> f(p) }\n" +
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def n = P(x = 3) \\apply { p -> p.x }\n" +
        "  def x: #some(int) | #notFound = #some(n)\n" +
        "  def m = x \\mapOption { v -> \"got {v}\" }\n" +
        "  match m\n" +
        "    | #some(s) -> println(s)\n" +
        "    | #notFound -> println(\"none\")\n" +
        "}\n"
    runSource source |> assertRan "got 3\n"

[<Fact>]
let ``Run - calling a unit function whose lambda takes a parameter`` () =
    // `{ _ -> ... }` has one parameter, the unit, which `getData()` passes.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def getData: () -> #some(int) | #notFound = { _ -> #some(42) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match getData()\n" +
        "    | #some(n) -> println(\"got {n}\")\n" +
        "    | #notFound -> println(\"none\")\n" +
        "}\n"
    runSource source |> assertRan "got 42\n"

[<Fact>]
let ``Run - interpolating variables bound by match patterns`` () =
    // Regression: the backend did not know the type of a pattern-bound name, so `"{n}"` inside a
    // match arm failed with "Unable to determine interpolated value type" (#35).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: #some(int) | #none -> string = { v ->\n" +
        "  match v\n" +
        "    | #some(n) -> \"some {n}\"\n" +
        "    | #none -> \"none\"\n" +
        "}\n" +
        "def greet: #named(string) | #anon -> string = { v ->\n" +
        "  match v\n" +
        "    | #named(name) -> \"hello {name}\"\n" +
        "    | #anon -> \"hello stranger\"\n" +
        "}\n" +
        "def first: list(int) -> string = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> \"empty\"\n" +
        "    | [x, ...rest] -> \"first {x}\"\n" +
        "}\n" +
        "println(describe(#some(3)) ++ \", \" ++ describe(#none))\n" +
        "println(greet(#named(\"Ada\")) ++ \", \" ++ greet(#anon))\n" +
        "println(first([7, 8]) ++ \", \" ++ first([]))\n"
    runSource source |> assertRan "some 3, none\nhello Ada, hello stranger\nfirst 7, empty\n"
[<Fact>]
let ``Run - interpolation at the top level of a script`` () =
    // Regression: the script start function used to omit the interpolation scratch locals (#48).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def x = 1 + 1\n" +
        "println(\"script {x}\")\n" +
        "println(\"{x * 10} again\")\n"
    runSource source |> assertRan "script 2\n20 again\n"

[<Fact>]
let ``Run - string literals inside interpolation holes`` () =
    // Regression: literals nested in `{...}` were not collected into the data section (#49).
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def main = {\n" +
        "  println(\"len {String.length(\"abc\")} and {String.uppercase(\"x\")}\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "len 3 and X\n"

[<Fact>]
let ``Run - interpolation holes that allocate or have effects run once`` () =
    // Regression: holes used to be evaluated twice, and an allocating hole in the second pass
    // overwrote the output buffer, so `"a {s ++ "!"} c"` printed just `b!`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/text\n" +
        "\n" +
        "def s = \"b\"\n" +
        "def shout: string -> string = { v ->\n" +
        "  println(\"shouting \" ++ v)\n" +
        "  String.uppercase(v)\n" +
        "}\n" +
        "println(\"a {s ++ \"!\"} c {String.uppercase(s)} d\")\n" +
        "println(\"[{shout(\"x\")}]\")\n"
    runSource source |> assertRan "a b! c B d\nshouting x\n[X]\n"

[<Fact>]
let ``Run - an unannotated function whose branches return different tags`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def getMessage = { flag ->\n" +
        "  def message = \"Hello, from Nanyx!\"\n" +
        "  if flag then #some(message)\n" +
        "  else #noMessage\n" +
        "}\n" +
        "\n" +
        "def describe = { flag ->\n" +
        "  match getMessage(flag)\n" +
        "    | #some(_) -> println(\"There was a message\")\n" +
        "    | #noMessage -> println(\"No message available\")\n" +
        "}\n" +
        "\n" +
        "describe(true)\n" +
        "describe(false)\n"
    runSource source |> assertRan "There was a message\nNo message available\n"

[<Fact>]
let ``Run - importing an unknown standard library module is a diagnostic`` () =
    let outcome = runSource "import nanyx/sockets\n\ndbg(1)\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("Unknown standard library module 'nanyx/sockets'", outcome.StandardError)

[<Fact>]
let ``Run - a compile error exits 1 with the diagnostic on stderr`` () =
    let outcome = runSource "import nanyx/console\n\nprintln(42)\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    // `file:line:col: error CODE: message`, at the argument.
    Assert.Contains(":3:9: error NYX0201: Type mismatch", outcome.StandardError)

[<Fact>]
let ``Run - matching on string literals`` () =
    // Regression: `| "Sat" -> ...` failed with "Unsupported match pattern" (#32).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def kind: string -> string = { day ->\n" +
        "  match day\n" +
        "    | \"Sat\" -> \"weekend\"\n" +
        "    | \"Sun\" -> \"weekend\"\n" +
        "    | _ -> \"weekday\"\n" +
        "}\n" +
        "def greet: #name(string) | #anon -> string = { who ->\n" +
        "  match who\n" +
        "    | #name(\"Ada\") -> \"hello, Ada!\"\n" +
        "    | #name(_) -> \"hello, someone\"\n" +
        "    | #anon -> \"hello, stranger\"\n" +
        "}\n" +
        "def suffix = \"t\"\n" +
        "println(kind(\"Sat\") ++ \" \" ++ kind(\"Sun\") ++ \" \" ++ kind(\"Mon\"))\n" +
        "println(kind(\"Sa\" ++ suffix))\n" +
        "println(greet(#name(\"Ada\")) ++ \" / \" ++ greet(#name(\"Bob\")) ++ \" / \" ++ greet(#anon))\n"
    runSource source |> assertRan "weekend weekend weekday\nweekend\nhello, Ada! / hello, someone / hello, stranger\n"
[<Fact>]
let ``Run - tuple patterns in match arms`` () =
    // Regression: `| (0, _) -> ...` failed with "Unsupported match pattern" (#33).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: (int, int) -> string = { p ->\n" +
        "  match p\n" +
        "    | (0, _) -> \"x zero\"\n" +
        "    | (_, 0) -> \"y zero\"\n" +
        "    | _ -> \"other\"\n" +
        "}\n" +
        "def swapped: (int, int) -> string = { p ->\n" +
        "  match p\n" +
        "    | (a, b) -> \"{b} {a}\"\n" +
        "}\n" +
        "def firstName: list((string, int)) -> string = { people ->\n" +
        "  match people\n" +
        "    | [(name, _), ...rest] -> name\n" +
        "    | [] -> \"nobody\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(describe((0, 1)) ++ \" \" ++ describe((1, 0)) ++ \" \" ++ describe((1, 1)))\n" +
        "  println(swapped((1, 2)))\n" +
        "  println(firstName([(\"Ada\", 36), (\"Grace\", 45)]) ++ \" \" ++ firstName([]))\n" +
        "}\n"
    runSource source |> assertRan "x zero y zero other\n2 1\nAda nobody\n"

[<Fact>]
let ``Run - identifier patterns bind the scrutinee`` () =
    // Regression: `| n -> ...` and `| 0, y -> ...` failed with "Unsupported identifier" (#34).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def echo: int -> int = { v ->\n" +
        "  match v\n" +
        "    | 0 -> 100\n" +
        "    | n -> n + 1\n" +
        "}\n" +
        "def pick: (int, int) -> int = { a, b ->\n" +
        "  match a, b\n" +
        "    | 0, y -> y\n" +
        "    | x, _ -> x\n" +
        "}\n" +
        "def label: string -> string = { s ->\n" +
        "  match s\n" +
        "    | text -> \"<\" ++ text ++ \">\"\n" +
        "}\n" +
        "dbg(echo(0))\n" +
        "dbg(echo(41))\n" +
        "dbg(pick(0, 5))\n" +
        "dbg(pick(7, 5))\n" +
        "println(label(\"x\"))\n"
    runSource source |> assertRan "dbg: 100\ndbg: 42\ndbg: 5\ndbg: 7\n<x>\n"
[<Fact>]
let ``Run - mutually recursive top-level functions`` () =
    // Regression: a def could not call one declared later in the file (#47).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }\n" +
        "def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }\n" +
        "def show: bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "show(isEven(10))\n" +
        "show(isOdd(7))\n" +
        "show(isEven(7))\n" +
        "dbg(later(2))\n" +
        "def later = { x -> x * 21 }\n"
    runSource source |> assertRan "yes\nyes\nno\ndbg: 42\n"
[<Fact>]
let ``Run - tuple destructuring with def`` () =
    // Regression: `def (x, y) = point` was a parse error (#41).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def (a, b) = (1, 2)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def point = (10, 20)\n" +
        "  def (x, y) = point\n" +
        "  def (_, (name, age)) = (0, (\"Ada\", 36))\n" +
        "  println(\"{x},{y}\")\n" +
        "  println(\"{a}+{b}={a + b}\")\n" +
        "  println(name ++ \" {age}\")\n" +
        "}\n"
    runSource source |> assertRan "10,20\n1+2=3\nAda 36\n"

[<Fact>]
let ``Run - with record update`` () =
    // Regression: `alice with age = 31` was a parse error (#40).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def shiftedX: Point -> int = { p -> (p with x = p.x + 1).x }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def alice = (name = \"Alice\", age = 30)\n" +
        "  def older = alice with age = 31\n" +
        "  def renamed = older with name = \"Al\" ++ \"ice\", age = older.age + 1\n" +
        "  println(\"{older.age} {alice.age} {renamed.name} {renamed.age}\")\n" +
        "  println(\"{shiftedX(Point(x = 3, y = 4))}\")\n" +
        "}\n"
    runSource source |> assertRan "31 30 Alice 32\n4\n"

[<Fact>]
let ``Run - member access on the result of a call`` () =
    // Regression: `shift(...)` returned a Point but `q.x` on it was "Record 'q' has no member 'x'" (#60).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def shift: Point -> Point = { q -> Point(x = q.x + 1, y = q.y + 1) }\n" +
        "def pair: (string, string) -> (first: string, second: string) = { a, b -> (first = a, second = b) }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def q = shift(Point(x = 3, y = 4))\n" +
        "  println(\"{q.x} {q.y} {shift(q).x}\")\n" +
        "  def r = shift(shift(q))\n" +
        "  println(\"{r.x} {r.y}\")\n" +
        "  println(pair(\"a\", \"b\").second ++ pair(\"c\", \"d\").first)\n" +
        "}\n"
    runSource source |> assertRan "4 5 5\n6 7\nbc\n"

[<Fact>]
let ``Run - nested record field access`` () =
    // Regression: `line.start.x` failed with "cannot determine layout for .x" (#30).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "def origin = (x = 0, y = 0)\n" +
        "def unit = Point(x = 1, y = 1)\n" +
        "def main = {\n" +
        "  def line = (start = (x = 1, y = 2), finish = (x = 4, y = 6))\n" +
        "  def deep = (outer = (inner = (label = \"deep\")))\n" +
        "  dbg(line.finish.x - line.start.x)\n" +
        "  dbg(line.finish.y - line.start.y)\n" +
        "  println(deep.outer.inner.label)\n" +
        "  dbg(origin.x + origin.y)\n" +
        "  dbg(unit.x + unit.y)\n" +
        "}\n" +
        "main()\n"
    // The last line covers #31: a record built through a type constructor at the top level.
    runSource source |> assertRan "dbg: 3\ndbg: 4\ndeep\ndbg: 0\ndbg: 2\n"
[<Fact>]
let ``Run - float arithmetic and printing`` () =
    // Regression: float literals parsed but `pi * 2.0` was "Type mismatch: float vs int" and the backend had
    // no f64 support at all (#36). Floats are boxed f64 values.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def half: float -> float = { x -> x / 2.0 }\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def pi = 3.14\n" +
        "  def d = pi * 2.0\n" +
        "  println(\"{d}\")\n" +
        "  println(\"{2.0 * 2.0} {0.0 - 1.5} {1.0 / 3.0} {half(5.0)} {1234567.25 + 0.0}\")\n" +
        "  println(if 2.5 > 1.0 and 1.5 <= 1.5 and 0.1 + 0.2 != 0.3 then \"cmp ok\" else \"cmp wrong\")\n" +
        "  dbg(1.5 + 2.25)\n" +
        "}\n"
    runSource source |> assertRan "6.28\n4.0 -1.5 0.333333 2.5 1234567.25\ncmp ok\ndbg: 3.75\n"
[<Fact>]
let ``Run - for loops over ranges and lists`` () =
    // Regression: `for 1..3 | i -> println("{i}")` was a parse error (#37).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def shout: list(string) -> () = { names ->\n" +
        "  for names | name -> println(name ++ \"!\")\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  for 1..3 | i -> println(\"{i}\")\n" +
        "  for [\"a\", \"b\"] | item -> println(item)\n" +
        "  for 1..5 | 5 -> println(\"Go!\")\n" +
        "           | _ -> println(\"Ready...\")\n" +
        "  for 1..2 -> println(\"tick\")\n" +
        "  def n = 2\n" +
        "  for 1..n | i ->\n" +
        "    for 1..n | j -> println(\"{i},{j}\")\n" +
        "  shout([\"hey\"])\n" +
        "}\n"
    runSource source |> assertRan "1\n2\n3\na\nb\nReady...\nReady...\nReady...\nReady...\nGo!\ntick\ntick\n1,1\n1,2\n2,1\n2,2\nhey!\n"

[<Fact>]
let ``Run - rec local loops`` () =
    // Regression: `rec loop = { ... }` was a parse error (#38).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def countdown: int -> int = { n ->\n" +
        "  rec loop = { i, acc -> if i > 0 then loop(i - 1, acc + i) else acc }\n" +
        "  loop(n, 0)\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"{countdown(10)}\")\n" +
        "  rec say = { i ->\n" +
        "    if i > 0 then\n" +
        "      println(\"{i}\")\n" +
        "      say(i - 1)\n" +
        "  }\n" +
        "  say(2)\n" +
        "}\n"
    runSource source |> assertRan "55\n2\n1\n"

[<Fact>]
let ``Run - boolean keywords not, and, or`` () =
    // #39: the docs make boolean operations keywords; `not` had no spelling at all.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: bool -> () = { b -> println(if b then \"yes\" else \"no\") }\n" +
        "def between: (int, int, int) -> bool = { x, lo, hi -> x >= lo and x <= hi }\n" +
        "show(not true)\n" +
        "show(not false)\n" +
        "show(true and false)\n" +
        "show(true or false)\n" +
        "show(not (1 > 2) and between(5, 1, 10))\n" +
        "show(not between(50, 1, 10) or false)\n"
    runSource source |> assertRan "no\nyes\nno\nyes\nyes\nyes\n"
[<Fact>]
let ``Run - if-matching`` () =
    // Regression: `if get(3) is #some(v) then ...` was a parse error (#42).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def get: int -> #some(int) | #none = { n -> #some(n) }\n" +
        "def nothing: int -> #some(int) | #none = { n -> #none }\n" +
        "def describe: #some(int) | #none -> string = { r ->\n" +
        "  if r is #some(v) then \"got {v}\" else \"nothing\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  if get(3) is #some(v) then println(\"got {v}\")\n" +
        "  if nothing(0) is #some(v) then println(\"unexpected {v}\")\n" +
        "  println(describe(get(5)) ++ \" \" ++ describe(nothing(5)))\n" +
        "}\n"
    runSource source |> assertRan "got 3\ngot 5 nothing\n"
[<Fact>]
let ``Run - multi-branch if`` () =
    // #76: `if | cond -> ... | value is pattern -> ... | else -> ...` was a parse error at `|`.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def lookup: int -> #some(int) | #none = { n -> if n > 100 then #none else #some(n * 2) }\n" +
        "def classify: int -> string = { n ->\n" +
        "  if | n < 0 -> \"negative\"\n" +
        "     | n == 0 ->\n" +
        "       \"zero\"\n" +
        "     | lookup(n) is #some(d) -> \"doubled {d}\"\n" +
        "     | else -> \"too big\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(classify(0 - 4))\n" +
        "  println(classify(0))\n" +
        "  println(classify(21))\n" +
        "  println(classify(500))\n" +
        "  if | classify(1) == \"zero\" -> println(\"unexpected\")\n" +
        "     | classify(0) == \"zero\" -> println(\"no else\")\n" +
        "}\n"
    runSource source |> assertRan "negative\nzero\ndoubled 42\ntoo big\nno else\n"
[<Fact>]
let ``Run - guard patterns in the documented form`` () =
    // Regression: the docs write guards as `| { < 0 } -> ...`; the parser only knew `| < 0 ->` (#46).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def describe: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { < 0 } -> \"negative\"\n" +
        "    | 0 -> \"zero\"\n" +
        "    | _ -> \"positive\"\n" +
        "}\n" +
        "def limit = 10\n" +
        "def size: int -> string = { n ->\n" +
        "  match n\n" +
        "    | { >= limit * 2 } -> \"huge\"\n" +
        "    | { >= limit } -> \"big\"\n" +
        "    | else -> \"small\"\n" +
        "}\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(describe(-1) ++ \" \" ++ describe(0) ++ \" \" ++ describe(5))\n" +
        "  println(size(25) ++ \" \" ++ size(10) ++ \" \" ++ size(3))\n" +
        "}\n"
    runSource source |> assertRan "negative zero positive\nhuge big small\n"

[<Fact>]
let ``Run - or-patterns in match arms`` () =
    // #43: `| a | b -> ...` is one arm matching either alternative.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "type Day = #mon | #tue | #wed | #thu | #fri | #sat | #sun\n" +
        "def kind: Day -> string = { day ->\n" +
        "  match day\n" +
        "    | #sat | #sun -> \"weekend\"\n" +
        "    | _ -> \"weekday\"\n" +
        "}\n" +
        "def size: int -> string = { n ->\n" +
        "  match n\n" +
        "    | 0\n" +
        "    | 1\n" +
        "    | 2 -> \"small\"\n" +
        "    | _ -> \"big\"\n" +
        "}\n" +
        "def axis: (int, int) -> string = {\n" +
        "  | 0, _ | _, 0 -> \"axis\"\n" +
        "  | _, _ -> \"plane\"\n" +
        "}\n" +
        "println(kind(#sat) ++ \" \" ++ kind(#sun) ++ \" \" ++ kind(#wed))\n" +
        "println(size(1) ++ \" \" ++ size(2) ++ \" \" ++ size(9))\n" +
        "println(axis(0, 5) ++ \" \" ++ axis(5, 0) ++ \" \" ++ axis(3, 3))\n"
    runSource source |> assertRan "weekend weekend weekday\nsmall small big\naxis axis plane\n"
[<Fact>]
let ``Run - functions as values and higher-order calls`` () =
    // #28: a parameter holding a function can be called; a named function or a lambda can be passed.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def apply: ((int -> int), int) -> int = { f, x -> f(x) }\n" +
        "def twice: ((int -> int), int) -> int = { f, x -> f(f(x)) }\n" +
        "def double: int -> int = { x -> x * 2 }\n" +
        "def map: (list(int), (int -> int)) -> list(int) = { xs, f ->\n" +
        "  match xs\n" +
        "    | [] -> []\n" +
        "    | [head, ...tail] -> [f(head)] ++ map(tail, f)\n" +
        "}\n" +
        "def sum: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> 0\n" +
        "    | [head, ...tail] -> head + sum(tail)\n" +
        "}\n" +
        "def main = {\n" +
        "  def square = { x -> x * x }\n" +
        "  dbg(apply(double, 21))\n" +
        "  dbg(apply({ x -> x + 1 }, 41))\n" +
        "  dbg(apply({ * 3 }, 14))\n" +
        "  dbg(twice(double, 10))\n" +
        "  dbg(square(6))\n" +
        "  dbg(sum(map([1, 2, 3], double)))\n" +
        "  dbg(sum([1, 2, 3] \\map { * 10 }))\n" +
        "}\n" +
        "main()\n"
    runSource source |> assertRan "dbg: 42\ndbg: 42\ndbg: 42\ndbg: 40\ndbg: 36\ndbg: 12\ndbg: 60\n"

[<Fact>]
let ``Run - a capturing lambda passed to a function`` () =
    let source =
        "def apply = { f, x -> f(x) }\n" +
        "def main = { n ->\n" +
        "  apply({ x -> x + n }, 1)\n" +
        "}\n" +
        "dbg(main(2))\n"
    runSource source |> assertRan "dbg: 3\n"

[<Fact>]
let ``Run - closures`` () =
    // Closures capture locals, parameters and other closures; captured strings and records keep their types.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def makeAdder: int -> (int -> int) = { x -> { y -> x + y } }\n" +
        "def twice: ((int -> int), int) -> int = { f, v -> f(f(v)) }\n" +
        "def greeter: string -> (string -> string) = { greeting -> { name -> greeting ++ \", \" ++ name } }\n" +
        "type Point = (x: int, y: int)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def add5 = makeAdder(5)\n" +
        "  println(\"{add5(10)} {twice(add5, 1)} {twice(makeAdder(1), 0)}\")\n" +
        "  def hello = greeter(\"Hello\")\n" +
        "  println(hello(\"Ada\"))\n" +
        "  def origin = Point(x = 1, y = 2)\n" +
        "  def shiftX = { d -> origin.x + d }\n" +
        "  def compose = { g -> { v -> add5(g(v)) } }\n" +
        "  def shiftedThenAdd5 = compose(shiftX)\n" +
        "  def doubledThenAdd5 = compose({ v -> v * 2 })\n" +
        "  println(\"{shiftX(10)}\")\n" +
        "  dbg(shiftedThenAdd5(1))\n" +
        "  dbg(doubledThenAdd5(3))\n" +
        "  def limit = 3\n" +
        "  rec countTo = { i -> if i < limit then countTo(i + 1) else i }\n" +
        "  println(\"{countTo(0)}\")\n" +
        "}\n"
    runSource source |> assertRan "15 11 2\nHello, Ada\n11\ndbg: 7\ndbg: 11\n3\n"

[<Fact>]
let ``Run - a match with no matching arm exits 1 with a runtime error`` () =
    let source =
        "def pick = { x ->\n" +
        "    match x\n" +
        "    | 1 -> 10\n" +
        "}\n" +
        "\n" +
        "dbg(pick(2))\n"
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("Runtime error", outcome.StandardError)
    Assert.Contains("unreachable", outcome.StandardError)

[<Fact>]
let ``Run - a tag union rest passes the tags a match does not name through`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "\n" +
        "def show: (#some(string) | #notFound | #error) -> string = { | #some(s) -> s\n" +
        "                                                       | #notFound -> \"notFound\"\n" +
        "                                                       | #error -> \"error\" }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def x: (#some(int) | #notFound | #error) = #some(20)\n" +
        "  def y: (#some(int) | #notFound | #error) = #error\n" +
        "  println(show(mapOption(x, { n -> \"{n + 1}\" })))\n" +
        "  println(show(mapOption(y, { n -> \"{n + 1}\" })))\n" +
        "}\n"
    runSource source |> assertRan "21\nerror\n"

[<Fact>]
let ``Run - a function that only calls the closure it is given compiles without making one`` () =
    let source =
        "def apply: (int -> int), int -> int = { f, x -> f(x) }\n" +
        "\n" +
        "@entry\n" +
        "def main = { }\n"
    runSource source |> assertRan ""

[<Fact>]
let ``Run - interpolating tags`` () =
    let source =
        "def mapOption\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  def a = #some(5) \\mapOption { n -> n * 2 }\n" +
        "  def b = #some(\"hi\")\n" +
        "  def c = if 1 > 2 then #ok(true) else #none\n" +
        "  println(\"{a} {b} {c} {#done}\")\n" +
        "}\n"
    runSource source |> assertRan "#some(10) #some(\"hi\") #none #done\n"

[<Fact>]
let ``Run - a proc block continues past each #some and stops at the first #error`` () =
    // `run(7)` fails at its first `try`, so nothing after it runs; `run(3)` fails at its second. `sum` binds two
    // `try`s nested in its `return`.
    let source =
        "import nanyx/console\n" +
        "import nanyx/result\n" +
        "\n" +
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def g: int -> #some(string) | #error(#tooBig) = { n -> if n == 3 then #error(#tooBig) else #some(\"n = {n}\") }\n" +
        "\n" +
        "def run: int -> #some(string) | #error(#tooBig) = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  dbg(a)\n" +
        "  def s = try g(a)\n" +
        "  return s\n" +
        "} }\n" +
        "\n" +
        "def add: (int, int) -> int = { a, b -> a + b }\n" +
        "def sum: int -> #some(int) | #error(#tooBig) = { x -> proc {\n" +
        "  return add(try f(x), try f(x + 1))\n" +
        "} }\n" +
        "\n" +
        "def show: (#some(string) | #error(#tooBig)) -> () = {\n" +
        "  | #some(s) -> println(s)\n" +
        "  | #error(_) -> println(\"error\")\n" +
        "}\n" +
        "\n" +
        "def showInt: (#some(int) | #error(#tooBig)) -> () = {\n" +
        "  | #some(n) -> println(\"sum = {n}\")\n" +
        "  | #error(_) -> println(\"error\")\n" +
        "}\n" +
        "\n" +
        "show(run(1))\n" +
        "show(run(3))\n" +
        "show(run(7))\n" +
        "showInt(sum(1))\n" +
        "showInt(sum(5))\n"
    runSource source |> assertRan "dbg: 1\nn = 1\ndbg: 3\nerror\nerror\nsum = 3\nerror\n"

[<Fact>]
let ``Run - a proc block gives back whichever failure stopped it`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/result\n" +
        "\n" +
        "def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some(\"n{n}\") }\n" +
        "def find: string -> #some(int) | #notFound = { s -> if s == \"n3\" then #notFound else #some(7) }\n" +
        "\n" +
        "def run = { x -> proc {\n" +
        "  def s = try name(x)\n" +
        "  def i = try find(s)\n" +
        "  return i\n" +
        "} }\n" +
        "\n" +
        "def show: (#some(int) | #nil | #notFound) -> () = {\n" +
        "  | #some(_) -> println(\"found\")\n" +
        "  | #nil -> println(\"nil\")\n" +
        "  | #notFound -> println(\"not found\")\n" +
        "}\n" +
        "\n" +
        "show(run(1))\n" +
        "show(run(3))\n" +
        "show(run(7))\n"
    runSource source |> assertRan "found\nnot found\nnil\n"

[<Fact>]
let ``Run - Int.parse reads decimal integers and rejects anything else`` () =
    // Negative and large results are left out: a tag's int payload is kept in 16 bits by the backend (#181).
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: (#some(int) | #invalidInt) -> () = {\n" +
        "  | #some(n) -> println(\"#some({n})\")\n" +
        "  | #invalidInt -> println(\"#invalidInt\")\n" +
        "}\n" +
        "\n" +
        "show(Int.parse(\"42\"))\n" +
        "show(Int.parse(\"+5\"))\n" +
        "show(Int.parse(\"007\"))\n" +
        "show(Int.parse(\"0\"))\n" +
        "show(Int.parse(\"\"))\n" +
        "show(Int.parse(\"-\"))\n" +
        "show(Int.parse(\"4x2\"))\n" +
        "show(Int.parse(\" 1\"))\n" +
        "show(\"12\" \\ Int.parse)\n"
    runSource source |> assertRan "#some(42)\n#some(5)\n#some(7)\n#some(0)\n#invalidInt\n#invalidInt\n#invalidInt\n#invalidInt\n#some(12)\n"

[<Fact>]
let ``Run - eprint and eprintln write to stderr`` () =
    let source =
        "def warn: [Console] string -> () = { message -> eprintln(\"warning: \" ++ message) }\n" +
        "\n" +
        "@entry\n" +
        "def main = {\n" +
        "  print(\"out \")\n" +
        "  eprint(\"err \")\n" +
        "  flush()\n" +
        "  warn(\"careful\")\n" +
        "  println(\"done\")\n" +
        "}\n"
    let outcome = runSource source
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal("out done\n", outcome.StandardOutput)
    Assert.Equal("err warning: careful\n", outcome.StandardError)

[<Fact>]
let ``Run - readLine and readToEnd read stdin`` () =
    let source =
        "@entry\n" +
        "def main = {\n" +
        "  match readLine()\n" +
        "    | #some(line) -> println(\"first [\" ++ line ++ \"]\")\n" +
        "    | #error(_) -> println(\"no first\")\n" +
        "  match readLine()\n" +
        "    | #some(line) -> println(\"second [\" ++ line ++ \"]\")\n" +
        "    | #error(_) -> println(\"no second\")\n" +
        "  match readToEnd()\n" +
        "    | #some(rest) -> println(\"rest [\" ++ rest ++ \"]\")\n" +
        "    | #error(_) -> println(\"failed\")\n" +
        "  match readLine()\n" +
        "    | #some(line) -> println(\"more [\" ++ line ++ \"]\")\n" +
        "    | #error(_) -> println(\"end of input\")\n" +
        "}\n"
    runSourceWithInput "hello\r\nworld\nthe\nrest" source
    |> assertRan "first [hello]\nsecond [world]\nrest [the\nrest]\nend of input\n"

[<Fact>]
let ``Run - the prelude's List functions`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def show: list(int) -> string = {\n" +
        "  | [] -> \"[]\"\n" +
        "  | [y, ...rest] -> \"{y} \" ++ show(rest)\n" +
        "}\n" +
        "def xs = [3, 1, 2]\n" +
        "println(show(xs \\ map({ x -> x * 2 })))\n" +
        "println(show(xs \\ filter({ x -> x > 1 })))\n" +
        "println(show(List.reverse(xs)))\n" +
        "println(show(List.flatten([[1, 2], [3], []])))\n" +
        "println(show(List.take(xs, 2)) ++ \" | \" ++ show(List.skip(xs, 2)) ++ \" | \" ++ show(List.append(xs, 9)))\n" +
        "def total: int = List.fold(xs, 0, { acc, x -> acc + x })\n" +
        "def back: int = List.foldBack([1, 2, 3], 0, { acc, x -> acc - x })\n" +
        "def count: int = List.length(xs)\n" +
        "def pairs: int = List.length(List.zip(xs, [\"a\", \"b\"]))\n" +
        "println(\"{total} {back} {count} {pairs}\")\n" +
        "println(if List.all(xs, { x -> x > 0 }) then \"all\" else \"not all\")\n" +
        "println(if List.any(xs, { x -> x > 5 }) then \"any\" else \"none\")\n" +
        "def found = xs \\ find({ x -> x < 3 })\n" +
        "def empty: list(int) = []\n" +
        "def first = List.head(empty)\n" +
        "@entry\n" +
        "def main = {\n" +
        "  match found\n" +
        "    | #some(n) -> println(\"found {n}\")\n" +
        "    | #notFound -> println(\"not found\")\n" +
        "  match first\n" +
        "    | #some(n) -> println(\"head {n}\")\n" +
        "    | #emptyList -> println(\"empty\")\n" +
        "}\n"
    runSource source
    |> assertRan (
        "6 2 4 []\n3 2 []\n2 1 3 []\n1 2 3 []\n3 1 [] | 2 [] | 3 1 2 9 []\n" +
        "6 -6 3 2\nall\nnone\nfound 1\nempty\n")

[<Fact>]
let ``Run - nanyx/math abs, min, max, clamp and pow`` () =
    let source =
        "import nanyx/console\n" +
        "import nanyx/math as math\n" +
        "\n" +
        "def a = math.abs(-42)\n" +
        "def b = math.min(3, 7)\n" +
        "def c = math.max(3, 7)\n" +
        "def d = math.clamp(120, 0, 100)\n" +
        "def e = math.clamp(-5, 0, 100)\n" +
        "def f = math.pow(2, 8)\n" +
        "def g = math.pow(5, 0)\n" +
        "println(\"{a} {b} {c} {d} {e} {f} {g}\")\n"
    runSource source |> assertRan "42 3 7 100 0 256 1\n"

[<Fact>]
let ``Run - the prelude's proc, Result and Option helpers need no import`` () =
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error(\"odd\") }\n" +
        "def quarter = { n -> proc {\n" +
        "  def h = try half(n)\n" +
        "  return try half(h)\n" +
        "} }\n" +
        "\n" +
        "def show: Result(int, string) -> () = {\n" +
        "  | #some(n) -> println(\"some {n}\")\n" +
        "  | #error(e) -> println(\"error {e}\")\n" +
        "}\n" +
        "\n" +
        "show(quarter(8))\n" +
        "show(quarter(6) \\ Result.mapError({ e -> \"ERR: \" ++ e }))\n" +
        "show(half(4) \\ map({ n -> n * 10 }))\n" +
        "def fallback = Result.defaultValue(half(3), 0)\n" +
        "println(\"{fallback}\")\n" +
        "def name: #some(string) | #empty = #some(\"abc\")\n" +
        "def shouted = name \\ Option.map({ s -> s ++ \"!\" })\n" +
        "match shouted\n" +
        "  | #some(s) -> println(s)\n" +
        "  | #empty -> println(\"empty\")\n" +
        "def seven = Option.defaultValue(#none, 7)\n" +
        "def three = Option.mapOr(name, 0, { s -> 3 })\n" +
        "println(\"{seven} {three}\")\n"
    runSource source |> assertRan "some 2\nerror ERR: odd\nsome 20\n0\nabc!\n7 3\n"

[<Fact>]
let ``Run - fields of a record bound by a match pattern`` () =
    // The pattern's `a` shadows the top-level `a`, whose type is a tag rather than a record.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def a = #some((x = \"ax\", y = \"ay\"))\n" +
        "def b: typeof(a) = #some((x = \"bx\", y = \"by\"))\n" +
        "\n" +
        "match b\n" +
        "| #some(a) ->\n" +
        "    def x = a.x\n" +
        "    println(\"x = {x}, y = {a.y}\")\n" +
        "| _ -> println(\"none\")\n" +
        "\n" +
        "def main = {\n" +
        "  def person = #some((name = \"pat\", age = 3))\n" +
        "  match person, 1\n" +
        "  | #some(p), n -> println(\"{p.name} {n}\")\n" +
        "  | _, _ -> println(\"none\")\n" +
        "}\n" +
        "\n" +
        "main()\n"
    runSource source |> assertRan "x = bx, y = by\npat 1\n"

[<Fact>]
let ``Run - a function reading fields takes any record that has them`` () =
    // The records' fields are in different places, so each read finds its field from the record's layout.
    let source =
        "import nanyx/console\n" +
        "\n" +
        "def label = { p -> p.name ++ \" from \" ++ p.city }\n" +
        "def show = {\n" +
        "  | #some(p), n -> println(p.name ++ \" \" ++ n)\n" +
        "  | _, _ -> println(\"none\")\n" +
        "}\n" +
        "def twice = { p -> label(p) ++ \"; \" ++ label(p) }\n" +
        "\n" +
        "println(label((age = 3, name = \"a\", city = \"Leeds\")))\n" +
        "println(label((city = \"York\", name = \"b\")))\n" +
        "show(#some((name = \"c\", age = 3)), \"one\")\n" +
        "show(#some((zip = 1, name = \"d\")), \"two\")\n" +
        "def home = (name = \"e\", city = \"Bath\")\n" +
        "def moved = home with city = \"Ely\"\n" +
        "println(label(moved))\n" +
        "println(twice((name = \"f\", city = \"Hull\", extra = 0)))\n" +
        "def people = [(name = \"g\", city = \"X\"), (name = \"h\", city = \"Y\")]\n" +
        "for people \\ List.map(label) | s -> println(s)\n"
    runSource source |> assertRan "a from Leeds\nb from York\nc one\nd two\ne from Ely\nf from Hull; f from Hull\ng from X\nh from Y\n"
