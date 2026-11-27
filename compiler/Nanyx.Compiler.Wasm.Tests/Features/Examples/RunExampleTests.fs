/// Runs the examples in examples/, and checks what each prints. Every example is run here, or listed in `notRun` with
/// why it isn't. Each is run as `nanyx run` runs it, compiled as `nanyx wasm` and the playground compile it, both without
/// debug probes, which leaves the core IR with temporaries where a probe would read a local (#2906), and compiled with
/// probes, as the debugger will compile it (#2225).
module TranspilerWasmTests.Features.Examples.Run

open System.IO
open Xunit
open TranspilerWasmTestHelpers

let private examplesDirectory = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples"))

/// Each example a test runs, with what it prints.
let private prints: Map<string, string> =
    Map.ofList
        [ "constraints.nyx", ""
          "Contexts-as-effects.nyx",
          """contexts are the parameters that nobody has to pass by hand
42
55
"""
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
          "editor.nyx",
          """Nearest significant line to 3: line 4: if x > 0 then
Indented more, after line 1: line 5: println(x)
Indented less, after line 5: line 6: main()
Indented less, before line 1: line 0: def main =
Line 1 to line 5: #moreIndentation
Line 5 to line 2: #noIndentation
"""
          "eightQueens.nyx",
          """Q . . . . . . .
. . . . Q . . .
. . . . . . . Q
. . . . . Q . .
. . Q . . . . .
. . . . . . Q .
. Q . . . . . .
. . . Q . . . .
"""
          "EventSubscription.nyx",
          """POST https://highstorm.app/api/v1/events/deploys {"event":"deploy.finished","content":"v1.2.0"}
Ingested event evt_1
HTTP error 401
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
          "oo-class.nyx",
          """chain [leaf for challenge, root], generated at 1970-01-01T00:00:01Z
chain [leaf for challenge, root], generated at 1970-01-01T00:00:01Z
chain [leaf for challenge, root], generated at 1970-01-01T00:01:30Z
attestation failed
"""
          "pairing.nyx",
          """companion pk-1, identity #none, key index list from #some(1700000000)
trust circle app 42, device ZmFt
extraction failed
pair_success: device, device_identity
"""
          "pixel-sorting.nyx",
          """red grey green blue
green black red blue
red grey green blue
8 pixels sorted
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
          "teamColours.nyx", "#teamsMustBeUnique\nGame 1 finished at 10000: 3-1, 0-3, 3-0\n"
          "stringTransformation.nyx",
          """oneTwoThree -> oneTwoThree
oneTwoThree -> OneTwoThree
oneTwoThree -> one-two-three
oneTwoThree -> one_two_three
oneTwoThree -> One Two Three
oneTwoThree -> One two three
one_two_three -> oneTwoThree
one_two_three -> OneTwoThree
one_two_three -> one-two-three
one_two_three -> one_two_three
one_two_three -> One Two Three
one_two_three -> One two three
one-two-three -> oneTwoThree
one-two-three -> OneTwoThree
one-two-three -> one-two-three
one-two-three -> one_two_three
one-two-three -> One Two Three
one-two-three -> One two three
one two three -> oneTwoThree
one two three -> OneTwoThree
one two three -> one-two-three
one two three -> one_two_three
one two three -> One Two Three
one two three -> One two three
"""
          "todo-web-app.nyx",
          """/api/TodoProtocol/toggleCompleted
Toggled [x] 2: Write a web app
#deleted
#deleteError(#todoDoesNotExist)
Added [ ] 4: Celebrate
[ ] 1: Learn Nanyx
[x] 2: Write a web app
[ ] 4: Celebrate
"""
          "traitsAndTypeclasses.nyx",
          """24
10
(x = 9, y = 12)
(w = 3)
"""
          "vendingMachine.nyx", "Dispensed; 3 coins left\n"
          "wallet.nyx",
          """primary: #p2wpkh on mainnet
funding: #p2sh on mainnet
cold: #p2trTestnet on testnet
1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2: #p2pkh on mainnet
xyz: #unknown on mainnet
"""
          "whisper_decoding.nyx",
          """transcribed: hello from nanyx!
transcribed: hello from
transcribed: hello from nanyx!
transcribed: from nanyx
error: best-of with greedy sampling
"""
          "zipping.nyx",
          """Alex
Sam
Jo
0: Alex
1: Sam
2: Jo
""" ]

/// The examples no test runs, each with why.
let private notRun: Map<string, string> =
    Map.ofList
        []

let examplesRun () : obj array seq = prints |> Map.keys |> Seq.map (fun name -> [| box name |])

[<Theory>]
[<MemberData(nameof examplesRun)>]
let ``Running an example prints what it should`` (example: string) =
    let outcome = runFile (Path.Combine(examplesDirectory, example))
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(prints[example].ReplaceLineEndings "\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal("", outcome.StandardError)

[<Theory>]
[<MemberData(nameof examplesRun)>]
let ``Compiled by nanyx wasm, without debug probes, an example prints what it should`` (example: string) =
    use directory = new TempDirectory()
    let wat = Path.Combine(directory.Path, "example.wat")
    let compiled = nanyxIn examplesDirectory [ "wasm"; example; "--wat"; "-o"; wat ]
    Assert.True((compiled.ExitCode = 0), $"nanyx wasm {example} failed:\n{compiled.StandardError}")
    match runHost [ example ] (File.ReadAllText wat) with
    | Ok printed -> Assert.Equal(prints[example].ReplaceLineEndings "\n", printed.ReplaceLineEndings "\n")
    | Error error -> failwith $"{example} didn't run: %A{error}"

[<Theory>]
[<MemberData(nameof examplesRun)>]
let ``Compiled with debug probes, as the debugger will compile it, an example prints what it should`` (example: string) =
    let wat = transpileScriptWith probedOptions (Path.Combine(examplesDirectory, example))
    match runHost [ example ] wat with
    | Ok printed -> Assert.Equal(prints[example].ReplaceLineEndings "\n", printed.ReplaceLineEndings "\n")
    | Error error -> failwith $"{example} didn't run: %A{error}"

[<Fact>]
let ``Every example is run, or listed as not run with the reason`` () =
    let examples = Directory.EnumerateFiles(examplesDirectory, "*.nyx") |> Seq.map Path.GetFileName |> Set.ofSeq
    let run = prints.Keys |> Set.ofSeq
    let listed = notRun.Keys |> Set.ofSeq
    Assert.Equal<Set<string>>(Set.empty, examples - run - listed)
    Assert.Equal<Set<string>>(Set.empty, Set.intersect run listed)
    Assert.Equal<Set<string>>(Set.empty, (run + listed) - examples)
