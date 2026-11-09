module TranspilerWasmTests.Features.Core.CoreIr

open System.IO
open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open Parser.Program
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen

let private typedModule (source: string) =
    let result = Compiler.compile source
    let errors = result.Diagnostics |> List.filter (fun d -> d.Severity = Severity.Error)
    Assert.True(errors.IsEmpty, $"Unexpected errors: %A{errors}")
    result.Typed.Value

let private compileWithReport (source: string) =
    transpileTypedModuleWithReport { UseCoreIr = true } (typedModule source)

let private count (text: string) (wat: string) = Regex.Matches(wat, Regex.Escape text).Count

/// `wat` with the specialisations the core IR made taken out, and calls to them made to the definition they
/// specialise: what the old path compiles a generic definition's callers to.
let private withoutSpecialisations (report: CoreIrReport) (wat: string) =
    report.Specialisations
    |> List.fold (fun (text: string) (specialisation, definition) ->
        let start = text.IndexOf($"\n\n  (func ${specialisation} ")
        let text =
            if start < 0 then text
            else text.Remove(start, text.IndexOf("\n  )", start) + "\n  )".Length - start)
        Regex.Replace(text, $@"\${Regex.Escape specialisation}(?![\w$])", "$" + definition)) wat

[<Fact>]
let ``A function the core IR can lower goes through it, and one it cannot falls back`` () =
    let source =
        "def add = { x, y -> x + y }\n" +
        "\n" +
        "def classify = { n ->\n" +
        "  def doubled = add(n, n)\n" +
        "  if doubled > 10 then #big(doubled) else #small\n" +
        "}\n" +
        "\n" +
        "def size = { n ->\n" +
        "  match classify(n)\n" +
        "    | #big(d) -> d\n" +
        "    | #small -> 0\n" +
        "}\n" +
        "\n" +
        "def shout: int -> float = { n -> 1.5 }\n"
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "add"; "classify"; "size" ], report.FromCoreIr)
    let reason = report.Fallbacks |> List.find (fst >> (=) "shout") |> snd
    Assert.Equal("a value of type float", reason)

[<Fact>]
let ``A function that calls one the core IR cannot specialise falls back too`` () =
    // `show` is generic and its body is out of the core IR's reach, so `main` cannot call a specialisation of it.
    let source =
        "def show = { x ->\n" +
        "  def scale = 1.5\n" +
        "  x\n" +
        "}\n" +
        "\n" +
        "def main = show(1)\n"
    let wat, report = compileWithReport source
    Assert.Empty(report.FromCoreIr)
    Assert.Equal("it calls 'show' at a type the IR could not lower it at", report.Fallbacks |> List.find (fst >> (=) "main") |> snd)
    Assert.Contains("call $show", wat)

[<Fact>]
let ``A generic function is emitted once per instantiation`` () =
    let source =
        "def id = { x -> x }\n" +
        "\n" +
        "def main = {\n" +
        "  def a = id(1)\n" +
        "  def b = id(a + 1)\n" +
        "  def s = id(\"text\")\n" +
        "  def t = id(#ok)\n" +
        "  b\n" +
        "}\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "id__int"; "id__string"; "id__tags_ok"; "main" ], report.FromCoreIr)
    Assert.Equal(1, count "(func $id__int " wat)
    Assert.Equal(1, count "(func $id__string " wat)
    Assert.Equal(1, count "(func $id__tags_ok " wat)
    Assert.Equal(2, count "call $id__int\n" wat)
    // The definition itself is still compiled the old way, unspecialised, for the callers the core IR cannot lower.
    Assert.Equal(1, count "(func $id " wat)
    Assert.Equal("it is generic: each instantiation is lowered where it is called", report.Fallbacks |> List.find (fst >> (=) "id") |> snd)

[<Fact>]
let ``A specialisation specialises the generic functions it calls`` () =
    let source =
        "def wrap = { x -> #some(x) }\n" +
        "\n" +
        "def orElse = { o, fallback ->\n" +
        "  match o\n" +
        "    | #some(v) -> v\n" +
        "    | #none -> fallback\n" +
        "}\n" +
        "\n" +
        "def roundTrip = { x, fallback -> orElse(wrap(x), fallback) }\n" +
        "\n" +
        "def main = {\n" +
        "  def n = roundTrip(1, 2)\n" +
        "  def s = roundTrip(\"a\", \"b\")\n" +
        "  n\n" +
        "}\n"
    let wat, report = compileWithReport source
    for name in [ "wrap__int"; "wrap__string"; "orElse__int"; "orElse__string"; "roundTrip__int"; "roundTrip__string" ] do
        Assert.Contains(name, report.FromCoreIr)
        Assert.Equal(1, count $"(func ${name} " wat)
    Assert.Contains("call $wrap__string\n    local.get $fallback\n    return_call $orElse__string", wat)

[<Fact>]
let ``Records, tuples and nominal values go through the core IR`` () =
    let source =
        "type Person = (name: string, age: int)\n" +
        "\n" +
        "type $Money = private int\n" +
        "\n" +
        "def make = { n -> Person(name = \"Ada\", age = n) }\n" +
        "\n" +
        "def older: Person -> Person = { p -> p with age = p.age + 1 }\n" +
        "\n" +
        "def swapped: (int, int) -> (int, int) = { t ->\n" +
        "  match t\n" +
        "    | (a, b) -> (b, a)\n" +
        "}\n" +
        "\n" +
        "def pence: $Money -> int = { m ->\n" +
        "  def $Money(p) = m\n" +
        "  p\n" +
        "}\n" +
        "\n" +
        "def add: (int, int) -> int = { a, b -> a + b }\n" +
        "\n" +
        "def stepped: (int, step: int) -> int = { a, step -> a * 10 + step }\n" +
        "\n" +
        "def main = {\n" +
        "  def pair = (1, 2)\n" +
        "  def named = stepped(1, step = 2)\n" +
        "  older(make(3)).age + pence($Money(250)) + add(pair) + named + swapped(pair).0\n" +
        "}\n"
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "make"; "older"; "swapped"; "pence"; "add"; "stepped"; "main" ], report.FromCoreIr)

[<Fact>]
let ``Strings, lists and intrinsics go through the core IR`` () =
    let source =
        "def greet: string -> string = { name -> \"Hello, \" ++ name }\n" +
        "\n" +
        "def differ: (string, string) -> bool = { a, b -> a != b }\n" +
        "\n" +
        "def isNone = { o -> o == #none }\n" +
        "\n" +
        "def heads: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> 0\n" +
        "    | [a] -> a\n" +
        "    | [a, b, ...rest] -> a + b\n" +
        "}\n" +
        "\n" +
        "def word = { s ->\n" +
        "  match s\n" +
        "    | \"yes\" -> 1\n" +
        "    | _ -> 0\n" +
        "}\n" +
        "\n" +
        "def size: string -> int = { s -> s.length }\n" +
        "\n" +
        "def main = heads([1, 2] ++ [3]) + word(\"yes\") + size(greet(\"Ada\"))\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "greet"; "differ"; "isNone"; "heads"; "word"; "size"; "main" ], report.FromCoreIr)
    Assert.Contains("call $__nanyx_string_equals\n    i32.eqz", wat)
    Assert.Contains("call $__nanyx_list_concat", wat)
    Assert.Contains("call $__nanyx_tag_equals", wat)

[<Fact>]
let ``Interpolated strings and dbg go through the core IR`` () =
    let source =
        "def pick = { n -> if n > 0 then #some(\"yes\") else #none }\n" +
        "\n" +
        "def describe: int -> string = { n -> \"n is {n}, positive {n > 0}, pick {pick(n)}\" }\n" +
        "\n" +
        "def nested: string -> string = { s -> \"outer {\"inner {s}\"} end\" }\n" +
        "\n" +
        "def debugs: int -> int = { n ->\n" +
        "  dbg(n)\n" +
        "  dbg(\"text\")\n" +
        "  dbg(#ok)\n" +
        "  dbg(#some(n))\n" +
        "  dbg(pick(n))\n" +
        "  n\n" +
        "}\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "pick"; "describe"; "nested"; "debugs" ], report.FromCoreIr)
    // A string inside another's hole keeps to its own hole locals.
    Assert.Contains("local.set $__interp_hole_1_0", wat)
    Assert.Contains("call $dbg_tag_payload_some", wat)

[<Fact>]
let ``Lambdas, closures and function values go through the core IR`` () =
    let source =
        "def apply = { f, x -> f(x) }\n" +
        "\n" +
        "def double: int -> int = { n -> n * 2 }\n" +
        "\n" +
        "def compose = { f, g -> { x -> g(f(x)) } }\n" +
        "\n" +
        "def countdown: int -> int = { n ->\n" +
        "  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + i) }\n" +
        "  loop(n, 0)\n" +
        "}\n" +
        "\n" +
        "def main = {\n" +
        "  def offset = 10\n" +
        "  def both = compose(double, { x -> x + offset })\n" +
        "  apply({ x -> x + offset }, 1) + apply(double, 4) + both(1) + countdown(4)\n" +
        "}\n"
    let wat, report = compileWithReport source
    // A lambda written in a generic function is specialised with it; one written in `main` keeps its own name.
    for name in [ "compose__int__int__int"; "__lambda_0__int__int__int"; "__rec_loop_1"; "__lambda_2"; "__fn_double"; "main" ] do
        Assert.Contains(name, report.FromCoreIr)
    // Calls through function values, and closure blocks holding a table slot and what they capture.
    Assert.Contains("call_indirect (type $ctx_fn_2)", wat)
    Assert.Contains("call $__nanyx_alloc", wat)

[<Fact>]
let ``A function that reads a field of any record is specialised for each layout it is given`` () =
    let source =
        "def nOf = { r -> r.n }\n" +
        "\n" +
        "def main = {\n" +
        "  def small = (n = 1)\n" +
        "  nOf(small) + nOf((a = 2, n = 3)) + nOf((a = 4, b = 5, n = 6)) + nOf((n = 7))\n" +
        "}\n"
    let wat, report = compileWithReport source
    // One specialisation per layout, named by the record's other fields.
    Assert.Equal<string list>([ "nOf__int__rec_"; "nOf__int__rec_a_int"; "nOf__int__rec_a_int_b_int"; "main" ], report.FromCoreIr)
    // Each reads `n` from where its layout puts it, rather than looking the field up at run time.
    for name, offset in [ "nOf__int__rec_", 0; "nOf__int__rec_a_int", 4; "nOf__int__rec_a_int_b_int", 8 ] do
        Assert.Contains($"(func ${name} (param $r i32) (result i32)\n    local.get $r\n    i32.const {offset}\n    i32.add\n    i32.load\n  )", wat)
    let specialisations = wat.Substring(wat.IndexOf "(func $nOf__")
    Assert.DoesNotContain("call $__nanyx_open_field", specialisations)

[<Fact>]
let ``Every intermediate value in the core IR is named`` () =
    let typed = typedModule "def f = { x, y -> (x + 1) * (y - 2) }\n"
    let input: CoreIrLower.LowerInput =
        { Definitions =
            typed.Module |> List.choose (function Kind (Def (ValueDef(_, name, _, expr))) -> Some (name, expr) | _ -> None)
          ExprTypes = typed.ExprTypes
          CalleeTypes = typed.CalleeTypes
          DefinitionTypes = typed.Types
          RequiresContexts = Set.empty
          RecordConstructors = Set.empty
          NominalWrappers = Set.empty
          Lifted = Map.empty }
    let lowered = CoreIrLower.lowerModule input id
    let func, _ = lowered.Roots.["f"]
    let printed = CoreIr.Print.func func
    let expected =
        "fn f(x: int, y: int): int =\n" +
        "  let __t3: int = x + 1\n" +
        "  let __t4: int = y - 2\n" +
        "  __t3 * __t4"
    Assert.Equal(expected, printed)

/// The E2E programs that type-check on their own, compiled with and without the core IR.
let private e2ePrograms () =
    let root = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "E2E")
    Directory.GetDirectories root
    |> Array.sort
    |> Array.choose (fun dir ->
        let result = Compiler.compileFile (Path.Combine(dir, "input.nyx"))
        match result.Typed with
        | Some typed when result.Diagnostics |> List.forall (fun d -> d.Severity <> Severity.Error) -> Some (Path.GetFileName dir, typed)
        | _ -> None)

[<Fact>]
let ``The core IR compiles what it lowers as the old path does`` () =
    let programs = e2ePrograms ()
    let mutable lowered = 0
    for name, typed in programs do
        let withCoreIr, report = transpileTypedModuleWithReport { UseCoreIr = true } typed
        let withoutCoreIr, _ = transpileTypedModuleWithReport { UseCoreIr = false } typed
        lowered <- lowered + report.FromCoreIr.Length
        Assert.True((withoutSpecialisations report withCoreIr = withoutCoreIr), $"{name} compiles differently through the core IR")
    // Not a vacuous comparison: the programs give the core IR plenty to lower.
    Assert.True(lowered >= 10, $"Only {lowered} functions went through the core IR")
