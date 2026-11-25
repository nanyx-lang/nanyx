module Nanyx.Compiler.Tests.Features.Division

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// #2716: `/` divides with a fraction, giving the operands' `fraction`, `Num`'s associated type; `//` divides to a whole
// number, and `%` takes what is left, each of the operands' own type.

[<Fact>]
let ``/ gives each number type's fraction`` () =
    let source =
        normalized """def i = 7 / 2
def n = { x: int, y: int -> x / y }
def s = { x: I8, y: I8 -> x / y }
def u = { x: U64, y: U64 -> x / y }
def f = { x: float, y: float -> x / y }
def g = { x: F32, y: F32 -> x / y }
def d = -7.5 / 2.0
"""
    Assert.Equal<string list>(
        [ "float"; "int, int -> float"; "I8, I8 -> float"; "U64, U64 -> float"; "float, float -> float"; "F32, F32 -> F32"; "float" ],
        typesOf source [ "i"; "n"; "s"; "u"; "f"; "g"; "d" ])

[<Fact>]
let ``// and % give the operands' own type`` () =
    let source =
        normalized """def i = -7 // 2
def r = -7 % 2
def s = { x: I8, y: I8 -> (x // y, x % y) }
def f = { x: float, y: float -> (x // y, x % y) }
def g = { x: F32, y: F32 -> (x // y, x % y) }
"""
    Assert.Equal<string list>(
        [ "int"; "int"; "I8, I8 -> (I8, I8)"; "float, float -> (float, float)"; "F32, F32 -> (F32, F32)" ],
        typesOf source [ "i"; "r"; "s"; "f"; "g" ])

[<Fact>]
let ``A decimal literal divides to its own type, which the code around it settles`` () =
    let source = normalized "def third: F32 = 1.0 / 3.0\ndef half: float = 1 / 2\n"
    Assert.Equal<string list>([ "F32"; "float" ], typesOf source [ "third"; "half" ])

[<Fact>]
let ``An int divided with / is a float where an int is expected`` () =
    let source = normalized "def half: int -> int = { n -> n / 2 }\n"
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'float' vs 'int'", message)
    Assert.Equal("n / 2", text)

[<Fact>]
let ``A function that writes its type divides values of a type variable to its fraction`` () =
    let source =
        normalized """def mean: [Num(a)] list(a) -> fraction = { xs -> list.sum(xs) / fromInt(xs.length) }
def ints = mean([1, 2, 4])
def floats = mean([1.0, 2.0])
def f32s = { xs: list(F32) -> mean(xs) }
def halves: [Num(a)] a -> (fraction, a, a) = { x -> (x / fromInt(2), x // fromInt(2), x % fromInt(2)) }
"""
    Assert.Equal<string list>([ "float"; "float"; "list(F32) -> F32" ], typesOf source [ "ints"; "floats"; "f32s" ])

[<Fact>]
let ``A fraction is not the type it divides`` () =
    let source = normalized "def wrong: [Num(a)] a -> a = { x -> x / x }\n"
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.AnnotationTooGeneral.Code, code)
    Assert.Equal("The annotation of 'wrong' is more general than its inferred type, where 'fraction' would have to be 'a'", message)

[<Fact>]
let ``A function that does not write its type and divides ints with / returns a float`` () =
    let source = normalized "def half = { n -> n / 2 }\ndef ratio = { a: int, b: int -> a / b }\n"
    Assert.Equal<string list>([ "int -> float"; "int, int -> float" ], typesOf source [ "half"; "ratio" ])

[<Fact>]
let ``A function that does not write its type and divides values of a type variable returns its fraction`` () =
    let source =
        normalized """def ratio = { x, y -> x / y }
def a = ratio(7, 2)
def b = ratio(7.0, 2.0)
def c = { x: F32 -> ratio(x, x) }
"""
    let typed = compileClean source
    Assert.Equal<string list>([ "float"; "float"; "F32 -> F32" ], [ "a"; "b"; "c" ] |> List.map (fun name -> Unifier.tyToString typed.Types[name]))
    // `ratio` requires `Num` of its operands' variable, and its `/` gives the `fraction` it requires it at.
    match Unifier.unalias typed.Types["ratio"], typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | TyFunc(TyRecord(parameters, _), result, _), [ [ TyApply("Num", [ TyVar a; TyVar fraction ]) ] ] ->
        Assert.Equal(TyVar a, parameters["0"])
        Assert.Equal(TyVar fraction, result)
    | ty, contexts -> failwith $"Unexpected type: %A{ty}, requiring %A{contexts}"

[<Fact>]
let ``The divisions of a function that does not write its type on one variable share its fraction`` () =
    let source =
        normalized """def both = { x, y -> (x / y, y / x, x // y, x * y) }
def d = both(7, 2)
"""
    let typed = compileClean source
    Assert.Equal("(float, float, int, int)", Unifier.tyToString typed.Types["d"])
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply("Num", [ TyVar _; TyVar _ ]) ] ] -> ()
    | contexts -> failwith $"Expected one Num, got %A{contexts}"

[<Fact>]
let ``A type variable divided to a whole number needs Num`` () =
    let code, message, text = onlyDiagnostic (normalized "def f: a, a -> a = { x, y -> x // y }\n")
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Equal("'f' floors the quotient of values of type 'a' with //, so its type must require Num(a): add [Num(a)] to the type of 'f'", message)
    Assert.Equal("x // y", text)

/// A `Num` type of its own whose `/` gives a `float`.
let private count =
    """type Count = protected (n: int)

def Count.zero: Count = Count(n = 0)
def Count.`+`: Count, Count -> Count = { a, b -> Count(n = a.n + b.n) }
def Count.`-`: Count, Count -> Count = { a, b -> Count(n = a.n - b.n) }
def Count.negate: Count -> Count = { a -> Count(n = -a.n) }
def Count.`*`: Count, Count -> Count = { a, b -> Count(n = a.n * b.n) }
def Count.`/`: Count, Count -> float = { a, b -> a.n / b.n }
def Count.`//`: Count, Count -> Count = { a, b -> Count(n = a.n // b.n) }
def Count.`%`: Count, Count -> Count = { a, b -> Count(n = a.n % b.n) }
def Count.fromInt: int -> Count = { n -> Count(n = n) }
def Count.isInteger: bool = false
"""

[<Fact>]
let ``A negative power of a type whose fraction is another is an error at the operator`` () =
    let source = normalized (count + "def half = Count(n = 2) ** -1\n")
    Assert.Equal(
        (DiagnosticCodes.NegativePowerOfFraction.Code,
         "A negative power of a 'Count' is '1 / x ** n', a 'float', which `/` on 'Count' gives, and not a 'Count': write `Count.fromInt(1) / x ** 1` for it",
         "**"),
        onlyDiagnostic source)
    // A power that is not negative, and the fraction written as a division, are fine.
    compileClean (normalized (count + "def cube = Count(n = 2) ** 3\ndef half = Count.fromInt(1) / Count(n = 2) ** 1\n")) |> ignore
