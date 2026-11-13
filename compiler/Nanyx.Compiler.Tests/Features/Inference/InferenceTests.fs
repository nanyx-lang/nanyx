module Nanyx.Compiler.Tests.Features.Inference.Inference

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Compile returns typed module for valid source`` () =
    let source = "def message = \"Hello\""
    let result = Compiler.compile source
    Assert.Equal(CompilationPhase.Typed, result.Phase)
    Assert.True(result.Diagnostics.IsEmpty)
    Assert.True(result.Typed.IsSome)

[<Fact>]
let ``Typecheck infers literal types`` () =
    let source = "def message = \"Hello\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["message"])

[<Fact>]
let ``Typecheck interpolated string`` () =
    let source =
        "def name = \"Ada\"\n" +
        "def msg = \"Hello {name}\""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["msg"])

[<Fact>]
let ``Typecheck list type application`` () =
    let source =
        "def sum: list(int) -> int = { xs -> 0 }\n" +
        "def values: list(string, int) = []"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck dbg returns its argument`` () =
    let source =
        "def value = dbg(1)\n" +
        "def text = dbg(\"hi\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["value"])
    Assert.Equal(TyPrimitive "string", typed.Types.["text"])

[<Fact>]
let ``Typecheck self recursive function`` () =
    let source =
        "def fact = { n -> if n == 0 then 1 else n * fact(n - 1) }\n" +
        "def result = fact(3)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck supports polymorphic let`` () =
    let source =
        "def id = { x -> x }\n" +
        "def a = id(1)\n" +
        "def b = id(\"hi\")"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["a"])
    Assert.Equal(TyPrimitive "string", typed.Types.["b"])

[<Fact>]
let ``Typecheck expands generic type aliases in annotations`` () =
    let source =
        "type Async(a) = #done(a) | #pending(a)\n" +
        "def fromResult: a -> Async(a) = { r -> #done(r) }\n" +
        "def value = fromResult(5)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck uses tuple input for multi-arg functions`` () =
    let source =
        "def f = { x, y -> x }\n" +
        "def result = f(1, 2)"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    match typed.Types.["f"] with
    | TyFunc(TyRecord fields, TyVar _, _) when fields.ContainsKey("0") && fields.ContainsKey("1") ->
        ()
    | other -> Assert.True(false, $"Unexpected function type: %A{other}")
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck uses unit input for zero-arg functions`` () =
    let source =
        "def f = { 1 }\n" +
        "def result = f()"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "unit", TyPrimitive "int", []), typed.Types.["f"])
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck gives a lambda argument's parameter its type from the callee`` () =
    // `p.x` is checked inside the lambda, before the call is solved: `p` is a `P` from `apply`'s signature.
    let source =
        "type P = (x: int)\n" +
        "def apply: P, (P -> int) -> int = { p, f -> f(p) }\n" +
        "def n = P(x = 3) \\apply { p -> p.x }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["n"])

[<Fact>]
let ``Typecheck rejects a lambda of several parameters whose signature's input is not a record`` () =
    let messages source = (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Message)
    Assert.Equal<string list>(
        [ "Type mismatch: #some(x: string, y: string) vs (t0, t1)." ],
        messages "def g: #some(x: string, y: string) -> string = { x, y -> \"{x}{y}\" }\n")
    Assert.Equal<string list>(
        [ "Type mismatch: #some(x: string, y: string) vs (t1, t2)." ],
        messages (
            "def a: #some(x: string, y: string) = #some(x = \"\", y = \"\")\n" +
            "def g: typeof a -> string = { x, y -> \"{x}{y}\" }\n"))
    Assert.Equal<string list>(
        [ "Type mismatch: string vs (string, t1)." ], messages "def g: string -> string = { x, y -> x }\n")

[<Fact>]
let ``Typecheck gives a lambda argument's parameter a type solved from the arguments before it`` () =
    // `a` in `mapSome`'s signature is only known from the first argument, a `#some(Customer)`.
    let source =
        "type Customer = (name: string)\n" +
        "def mapSome\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def c: #some(Customer) | #notFound = #notFound\n" +
        "def name = c \\mapSome { c -> c.name }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    match result.Typed.Value.Types.["name"] with
    | TyUnion [ TyTag("some", Some (TyPrimitive "string")); TyTag("notFound", None) ] -> ()
    | other -> Assert.True(false, $"Unexpected name type: %A{other}")

[<Fact>]
let ``Typecheck match arms agree`` () =
    let source =
        "def result = match 1\n" +
        "  | 1 -> \"one\"\n" +
        "  | _ -> \"other\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["result"])

[<Fact>]
let ``Typecheck infers list element type`` () =
    let source = "def numbers = [1, 2, 3]"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyApply("list", [TyPrimitive "int"]), typed.Types.["numbers"])

[<Fact>]
let ``Typecheck infers operator results`` () =
    let source =
        "def sum = 1 + 2\n" +
        "def lt = 1 < 2\n" +
        "def eq = \"a\" == \"b\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["sum"])
    Assert.Equal(TyPrimitive "bool", typed.Types.["lt"])
    Assert.Equal(TyPrimitive "bool", typed.Types.["eq"])

[<Fact>]
let ``Typecheck ++ concatenates strings and lists`` () =
    let source =
        "def greeting = \"Hello, \" ++ \"World\"\n" +
        "def numbers = [1, 2] ++ [3]\n" +
        "def shout: string -> string = { s -> s ++ \"!\" }\n" +
        "def twice: list(int) -> list(int) = { xs -> xs ++ xs }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["greeting"])
    Assert.Equal(TyApply("list", [ TyPrimitive "int" ]), typed.Types.["numbers"])

[<Fact>]
let ``Typecheck rejects ++ between a string and an int`` () =
    let result = Compiler.compile "def bad = \"a\" ++ 1"
    assertReports DiagnosticCodes.TypeMismatch result

[<Fact>]
let ``Typecheck rejects + on strings`` () =
    let result = Compiler.compile "def bad = \"a\" + \"b\""
    assertReports DiagnosticCodes.TypeMismatch result

[<Fact>]
let ``Typecheck allows mutual recursion between annotated top-level defs`` () =
    let source =
        "def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }\n" +
        "def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }\n" +
        "def result = isEven(10)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "bool", result.Typed.Value.Types.["result"])

[<Fact>]
let ``Typecheck allows a forward reference to an unannotated def`` () =
    let source =
        "def result = addOne(41)\n" +
        "def addOne = { x -> x + 1 }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["result"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int", []), result.Typed.Value.Types.["addOne"])

[<Fact>]
let ``Typecheck still generalizes a def used after its definition`` () =
    let source =
        "def id = { x -> x }\n" +
        "def a = id(1)\n" +
        "def b = id(\"s\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["a"])
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["b"])

[<Fact>]
let ``Typecheck types the boolean keywords as bool`` () =
    let source =
        "def a = true and false\n" +
        "def b = a or not a\n" +
        "def c = not (1 < 2)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let types = result.Typed.Value.Types
    Assert.Equal(TyPrimitive "bool", types.["a"])
    Assert.Equal(TyPrimitive "bool", types.["b"])
    Assert.Equal(TyPrimitive "bool", types.["c"])

[<Fact>]
let ``Typecheck accepts arithmetic and comparison on floats`` () =
    // #36: `+ - * /` and `< > <= >=` used to pin their operands to int.
    let ok =
        Compiler.compile (
            "def pi = 3.14\n" +
            "def d = pi * 2.0\n" +
            "def e: float = 1.5 + 2.5\n" +
            "def half = { x -> x / 2.0 }\n" +
            "def big = 2.5 > 1.0\n" +
            "def sum: (float, float) -> float = { a, b -> a + b }\n")
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)
    let mixed = Compiler.compile "def m = 1 + 2.0"
    Assert.False(mixed.Diagnostics.IsEmpty, "int + float should not type-check")
    let stillInt = Compiler.compile "def n: float = 1 + 2"
    Assert.False(stillInt.Diagnostics.IsEmpty, "int arithmetic is still int")

[<Fact>]
let ``Typecheck types for loops and ranges`` () =
    let ok =
        Compiler.compile (
            "def r = 1..3\n" +
            "def total = { xs -> for xs | x -> x + 1 }\n" +
            "def f = for 1..3 | i -> i * 2\n" +
            "def g: () = for [\"a\"] | s -> s ++ \"!\"\n")
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)
    let wrong = Compiler.compile "def h = for 1..3 | i -> i ++ \"x\""
    Assert.False(wrong.Diagnostics.IsEmpty, "a range element is an int")
    let badRange = Compiler.compile "def h = \"a\"..\"z\""
    Assert.False(badRange.Diagnostics.IsEmpty, "ranges are over ints")

[<Fact>]
let ``Typecheck rejects boolean keywords on non-booleans`` () =
    let andResult = Compiler.compile "def a = 1 and 2"
    Assert.False(andResult.Diagnostics.IsEmpty, "`1 and 2` should not type-check")
    let notResult = Compiler.compile "def b = not \"text\""
    Assert.False(notResult.Diagnostics.IsEmpty, "`not \"text\"` should not type-check")
