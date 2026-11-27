/// Test packages (#3095): a package whose manifest gives `tests` and no `sources` holds only the tests of the packages it
/// depends on, an application among them, and only `nanyx test` builds it.
module TranspilerWasmTests.Features.Tests.TestPackage

open Xunit
open TranspilerWasmTestHelpers

/// An application under the console host, with an `@entry` that reads its arguments, and modules it exports.
let private cartFiles =
    [ "cart/cart.nyxp",
      $"""(
  package = (name = "cart", version = "0.1.0", sources = ["src/**/*.nyx"])
  {consoleHostEntry}
)
"""
      "cart/src/pricing.nyx",
      """export module pricing

export type Line = (name: string, price: int, quantity: int)

export def subtotal: list(Line) -> int = { lines ->
  lines \list.fold(0, { total, line -> total + line.price * line.quantity })
}
"""
      "cart/src/main.nyx",
      """export module main

import pricing

export def greeting: string -> string = { name -> "hello {name}" }

@entry
def main = {
  println(greeting("{args()}"))
  println("{subtotal([Line(name = "Milk", price = 145, quantity = 2)])}")
}
""" ]

/// The package beside `cart` that holds its tests, whose manifest's `package` is `package`.
let private cartTests (package: string) =
    [ "cart-tests/cart-tests.nyxp",
      $"""(
  package = {package}
  dependencies = [(package = "cart", path = "../cart")]
)
"""
      "cart-tests/tests/pricing.tests.nyx",
      """module pricing.tests

import cart/pricing

@test
def `the subtotal adds up every line` = {
  assert(subtotal([Line(name = "Milk", price = 145, quantity = 2)]) == 290)
}
"""
      "cart-tests/tests/main.tests.nyx",
      """module main.tests

import cart/main

@test
def greets = {
  assert(greeting("x") == "hello x")
}
""" ]

/// What `nanyx` printed for `arguments` in a directory that holds `cart` and `cart-tests`, whose manifest's `package`
/// is `package`.
let private nanyxBeside (package: string) (arguments: string list) =
    use directory = new TempDirectory()
    for path, text in cartFiles @ cartTests package do
        directory.Write path text
    nanyxIn directory.Path arguments

let private testPackage = """(name = "cart.tests", version = "0.1.0", tests = ["tests/**/*.nyx"])"""

[<Fact>]
let ``nanyx test runs a test package's tests, which import an application's modules`` () =
    let outcome = nanyxBeside testPackage [ "test"; "cart-tests" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        """Running 2 tests in cart.tests

PASS main.tests.greets
PASS pricing.tests.`the subtotal adds up every line`

2 passed
""",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )

[<Fact>]
let ``A test package that writes sources = [] is one too`` () =
    let outcome = nanyxBeside """(name = "cart.tests", version = "0.1.0", sources = [], tests = ["tests/**/*.nyx"])""" [ "test"; "cart-tests" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("2 passed", outcome.StandardOutput)

[<Fact>]
let ``A test package's test file is checked as its test build compiles it: the application's @entry is no error`` () =
    let outcome = nanyxBeside testPackage [ "check"; "cart-tests/tests/main.tests.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        """Phase: Typed
No diagnostics.
Types:
greets : () -> ()
""",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )

[<Fact>]
let ``nanyx test of a test package's test file runs its tests in the package's test build`` () =
    let outcome = nanyxBeside testPackage [ "test"; "cart-tests/tests/pricing.tests.nyx" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(
        """Running 1 test in cart.tests

PASS pricing.tests.`the subtotal adds up every line`

1 passed
""",
        outcome.StandardOutput.ReplaceLineEndings "\n"
    )

[<Fact>]
let ``nanyx run of a test package's test file says it is a test file, and to use nanyx test`` () =
    let outcome = nanyxBeside testPackage [ "run"; "cart-tests/tests/pricing.tests.nyx" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("error NYX0045", outcome.StandardError)
    Assert.Contains("It is a test file of cart.tests", outcome.StandardError)

[<Fact>]
let ``nanyx check of a test package checks its test files`` () =
    let outcome = nanyxBeside testPackage [ "check"; "cart-tests" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Contains("Checked cart.tests (2 files).", outcome.StandardOutput)

[<Fact>]
let ``The application a test package depends on still runs its @entry`` () =
    let outcome = nanyxBeside testPackage [ "run"; "cart"; "a" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("hello [a]\n290\n", outcome.StandardOutput.ReplaceLineEndings "\n")

[<Theory>]
[<InlineData("run")>]
[<InlineData("build")>]
let ``A test package has no program to run or build, and says to run nanyx test`` (command: string) =
    let outcome = nanyxBeside testPackage [ command; "cart-tests" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("error NYX0045", outcome.StandardError)
    Assert.Contains("It is a test package, which holds the tests of the packages it depends on and no program: run them with `nanyx test`", outcome.StandardError)

[<Fact>]
let ``sources whose patterns match no file is still NYX0042 beside tests`` () =
    let outcome = nanyxBeside """(name = "cart.tests", version = "0.1.0", sources = ["lib/**/*.nyx"], tests = ["tests/**/*.nyx"])""" [ "test"; "cart-tests" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Contains("NYX0042", outcome.StandardError)
    Assert.Contains("selects its sources with 'lib/**/*.nyx', which match no file", outcome.StandardError)
