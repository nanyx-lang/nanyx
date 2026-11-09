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
    report.FromCoreIr
    |> List.filter (fun name -> name.Contains "__")
    |> List.fold (fun (text: string) specialisation ->
        let start = text.IndexOf($"\n\n  (func ${specialisation} ")
        let text =
            if start < 0 then text
            else text.Remove(start, text.IndexOf("\n  )", start) + "\n  )".Length - start)
        let definition = specialisation.Substring(0, specialisation.IndexOf "__")
        Regex.Replace(text, $@"\${Regex.Escape specialisation}\b", "$" + definition)) wat

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
        "def shout: int -> int = { n -> dbg(n) }\n"
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "add"; "classify"; "size" ], report.FromCoreIr)
    let reason = report.Fallbacks |> List.find (fst >> (=) "shout") |> snd
    Assert.Equal("a call to 'dbg'", reason)

[<Fact>]
let ``A function that calls one the core IR cannot specialise falls back too`` () =
    // `show` is generic and its body is out of the core IR's reach, so `main` cannot call a specialisation of it.
    let source =
        "def show = { x -> dbg(x) }\n" +
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
let ``Every intermediate value in the core IR is named`` () =
    let typed = typedModule "def f = { x, y -> (x + 1) * (y - 2) }\n"
    let input: CoreIrLower.LowerInput =
        { Definitions =
            typed.Module |> List.choose (function Kind (Def (ValueDef(_, name, _, expr))) -> Some (name, expr) | _ -> None)
          ExprTypes = typed.ExprTypes
          CalleeTypes = typed.CalleeTypes
          DefinitionTypes = typed.Types
          RequiresContexts = Set.empty }
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
