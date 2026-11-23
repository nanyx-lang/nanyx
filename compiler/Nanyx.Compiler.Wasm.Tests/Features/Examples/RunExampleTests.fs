/// Runs the examples in examples/, and checks what each prints. Every example is run here, or listed in `notRun` with
/// why it isn't.
module TranspilerWasmTests.Features.Examples.Run

open System.IO
open Xunit
open TranspilerWasmTestHelpers

let private examplesDirectory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples"))

/// Each example a test runs, with what it prints.
let private prints: Map<string, string> =
    Map.ofList
        [ "constraints.nyx", ""
          "csv.nyx",
          """GOOG,"Google Inc.",505.24,0.47,0.09
YHOO,"Yahoo! Inc.",27.38,0.33,1.22
CNET,"CNET Networks",8.62,-0.13,-1.4901
Google Inc. is up 0.09%
Yahoo! Inc. is up 1.22%
CNET Networks is down -1.4901%
"""
          "directives.nyx",
          """Hello, Ada!
Hello, Grace!
Hello, Barbara!
"""
          "fizzbuzzbaz.nyx",
          """1
2
fizz
4
buzz
fizz
baz
8
fizz
buzz
11
fizz
13
baz
fizzbuzz
16
17
fizz
19
buzz
fizzbaz
22
23
fizz
buzz
26
fizz
baz
29
fizzbuzz
"""
          "functional-argument-parsing.nyx", "Processing input 'foo.txt' verbose=true\n"
          "money.nyx", "Tea and cake: £5.55\n"
          "Nim.nyx",
          """alice
bob
alice
  1 -> bob
    1 -> alice
      1 -> alice wins
    2 -> bob wins
  2 -> bob
    1 -> bob wins
  3 -> alice wins
#won(#alice)
#cheated(#bob)
[#cheated(#bob), #won(#alice)]
alice -> 10
bob -> 0
"""
          "regex-matching.nyx",
          """Hi, we have saved your phone number 123-456-7890.
Hi JohnSmith, we have saved your email address.
Invalid contact information: neither an email address nor a phone number.
"""
          "semanticVersionParsing.nyx",
          """1.2.3: major 1, minor 2, patch 3
1.2.3-beta+build42: major 1, minor 2, patch 3
1.2: not a version number
x.y.z: not a version number
"""
          "shop.nyx",
          """
== ORDERS ==

Receipt for Ada
-------------------------------------------
Breakfast tea          2 x  £2.89   £5.78
Semi-skimmed milk      1 x  £1.25   £1.25
Sourdough loaf         1 x  £3.50   £3.50
-------------------------------------------
Subtotal                            £10.53
Total (4 items)                     £10.53

Receipt for Grace
-------------------------------------------
Free-range eggs        6 x  £3.10  £18.60
Raspberry jam          2 x  £2.15   £4.30
Semi-skimmed milk      4 x  £1.25   £5.00
-------------------------------------------
Subtotal                            £27.90
Bulk discount                       -£2.79
Total (12 items)                    £25.11

Order for Alan refused: there is no product COF
Order for Edsger refused: not enough Mature cheddar in stock
Order for Barbara refused: could not read 'BRD two' (write lines as SKU x QTY)

== STOCK ==
TEA   14 ##############
MLK   30 ##############################
BRD    6 ######  <- reorder
JAM    9 #########
EGG   12 ############
CHS    4 ####  <- reorder
Stock value: £172.51
Running low: Sourdough loaf, Mature cheddar

== TILL CALCULATOR ==
3 4 + 2 *             = 14
289 2 * 125 +         = 703
100 7 %               = 2
12 30 max 5 min       = 5
5 0 /                 cannot divide by zero
1 +                   not enough numbers for +
2 3 4 +               too many numbers left over
2 two *               do not know 'two'

== TAKINGS ==
  8:00 £12 ####
  9:00 £30 ##########
 10:00 £41 #############
 11:00 £25 ########
 12:00 £18 ######
 13:00 £52 #################
 14:00 £47 ###############
 15:00 £22 #######
 16:00 £15 #####
 17:00 £33 ###########
Total:   £295
Best:    £52 at 13:00
Median:  £30
Sorted:  12, 15, 18, 22, 25, 30, 33, 41, 47, 52
Quiet hours: 3 (12, 18, 15)
"""
          "source-generator.nyx",
          """using System;

namespace Demo
{
partial record Person
{
public object this[string name]
{
    get => name switch
    {
        "Name" => Name,
        "Age" => Age,
        _ => throw new IndexOutOfRangeException(name)
    };
    set
    {
        switch (name)
        {
            case "Name": Name = (string) value; break;
            default: throw new IndexOutOfRangeException(name);
        }
    }
}
}
}
"""
          "sql-utils.nyx", ""
          "teamColours.nyx",
          """Game 1 started at 100 and finished at 400
[[Herons, Herons, Crows, Herons], [Crows, Crows, Crows], [Herons, Herons, Herons]]
"""
          "traitsAndTypeclasses.nyx",
          """24
10
(x = 9, y = 12)
(w = 3)
"""
          "vendingMachine.nyx", "Dispensed; 3 coins left\n"
          "zipping.nyx",
          """Alex
Sam
Jo
0: Alex
1: Sam
2: Jo
""" ]

/// The examples no test runs, each with why. They are out of date with the library (#2459).
let private notRun: Map<string, string> =
    Map.ofList
        [ "Contexts-as-effects.nyx", "`'a'..'z'` needs an `Enumerable(string)`, which isn't loaded (#2459)"
          "EventSubscription.nyx", "it imports `nanyx.web/http`, which is not a package (#2459)"
          "editor.nyx", "it imports `nanyx/text.regex`, which is now the package `nanyx.regex` (#2459)"
          "eightQueens.nyx", "it passes a `seq(string)` to `string.join`, which takes a list (#2459)"
          "malicious-shas.nyx", "it imports `nanyx/encoding`, which doesn't exist (#2459)"
          "oo-class.nyx", "it imports `nanyx/time`, which doesn't exist (#2459)"
          "pairing.nyx", "it imports `nanyx/encoding`, which doesn't exist (#2459)"
          "pixel-sorting.nyx", "it calls `Math.sqrt` and `Math.floor`, which `nanyx/math` doesn't have (#2459)"
          "stringTransformation.nyx", "it imports `nanyx/text.regex`, which is now the package `nanyx.regex` (#2459)"
          "todo-web-app.nyx", "it imports `nanyx/time`, which doesn't exist (#2459)"
          "wallet.nyx", "it imports `nanyx/encoding`, which doesn't exist (#2459)"
          "web-server-shared.nyx", "it imports `nanyx/time`, which doesn't exist (#2459)"
          "whisper_decoding.nyx", "it calls `Random.float`, which isn't in scope (#2459)" ]

let examplesRun () : obj array seq = prints |> Map.keys |> Seq.map (fun name -> [| box name |])

[<Theory>]
[<MemberData(nameof examplesRun)>]
let ``Running an example prints what it should`` (example: string) =
    let outcome = runFile (Path.Combine(examplesDirectory, example))
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(prints[example].ReplaceLineEndings "\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal("", outcome.StandardError)

[<Fact>]
let ``Every example is run, or listed as not run with the reason`` () =
    let examples = Directory.EnumerateFiles(examplesDirectory, "*.nyx") |> Seq.map Path.GetFileName |> Set.ofSeq
    let run = prints.Keys |> Set.ofSeq
    let listed = notRun.Keys |> Set.ofSeq
    Assert.Equal<Set<string>>(Set.empty, examples - run - listed)
    Assert.Equal<Set<string>>(Set.empty, Set.intersect run listed)
    Assert.Equal<Set<string>>(Set.empty, (run + listed) - examples)
