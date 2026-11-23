module TranspilerWasmTests.Features.Examples.Run

open System.IO
open Xunit
open TranspilerWasmTestHelpers

let private examplePath (example: string) =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", example))

let private assertExamplePrints (expected: string) (example: string) =
    let outcome = runFile (examplePath example)
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(expected.ReplaceLineEndings "\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal("", outcome.StandardError)

[<Fact>]
let ``Running - source-generator.nyx prints the generated C#`` () =
    "source-generator.nyx"
    |> assertExamplePrints
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

[<Fact>]
let ``Running - vendingMachine.nyx dispenses two items and prints the coins left`` () =
    "vendingMachine.nyx" |> assertExamplePrints "Dispensed; 3 coins left\n"

[<Fact>]
let ``Running - fizzbuzzbaz.nyx prints the numbers with their words`` () =
    "fizzbuzzbaz.nyx"
    |> assertExamplePrints
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

[<Fact>]
let ``Running - Contexts-as-effects.nyx cracks a Caesar cipher and runs its contexts`` () =
    "Contexts-as-effects.nyx"
    |> assertExamplePrints
        """contexts are the parameters that nobody has to pass by hand
42
55
"""

[<Fact>]
let ``Running - editor.nyx finds lines by content and indentation`` () =
    "editor.nyx"
    |> assertExamplePrints
        """Nearest significant line to 3: line 4: if x > 0 then
Indented more, after line 1: line 5: println(x)
Indented less, after line 5: line 6: main()
Indented less, before line 1: line 0: def main =
Line 1 to line 5: #moreIndentation
Line 5 to line 2: #noIndentation
"""

[<Fact>]
let ``Running - eightQueens.nyx prints a solution for eight queens`` () =
    "eightQueens.nyx"
    |> assertExamplePrints
        """Q . . . . . . .
. . . . Q . . .
. . . . . . . Q
. . . . . Q . .
. . Q . . . . .
. . . . . . Q .
. Q . . . . . .
. . . Q . . . .
"""

[<Fact>]
let ``Running - EventSubscription.nyx posts an event, then reports a refused one`` () =
    "EventSubscription.nyx"
    |> assertExamplePrints
        """POST https://highstorm.app/api/v1/events/deploys {"event":"deploy.finished","content":"v1.2.0"}
Ingested event evt_1
HTTP error 401
"""

[<Fact>]
let ``Running - oo-class.nyx refreshes the attestation only when it has expired`` () =
    "oo-class.nyx"
    |> assertExamplePrints
        """chain [leaf for challenge, root], generated at 1000
chain [leaf for challenge, root], generated at 1000
chain [leaf for challenge, root], generated at 90000
attestation failed
"""

[<Fact>]
let ``Running - pairing.nyx extracts both kinds of payload and builds a reply`` () =
    "pairing.nyx"
    |> assertExamplePrints
        """companion pk-1, identity #none, key index list from #some(1700000000)
trust circle app 42, device ZmFt
extraction failed
pair_success: device, device_identity
"""

[<Fact>]
let ``Running - pixel-sorting.nyx sorts the rows without black pixels`` () =
    "pixel-sorting.nyx"
    |> assertExamplePrints
        """red grey green blue
green black red blue
red grey green blue
8 pixels sorted
"""

[<Fact>]
let ``Running - stringTransformation.nyx converts each input to every casing`` () =
    "stringTransformation.nyx"
    |> assertExamplePrints
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

[<Fact>]
let ``Running - todo-web-app.nyx serves a client from an in-memory store`` () =
    "todo-web-app.nyx"
    |> assertExamplePrints
        """/api/TodoProtocol/toggleCompleted
Toggled [x] 2: Write a web app
#deleted
#deleteError(#todoDoesNotExist)
Added [ ] 4: Celebrate
[ ] 1: Learn Nanyx
[x] 2: Write a web app
[ ] 4: Celebrate
"""

[<Fact>]
let ``Running - wallet.nyx classifies the wallet's addresses`` () =
    "wallet.nyx"
    |> assertExamplePrints
        """primary: #p2wpkh on mainnet
funding: #p2sh on mainnet
cold: #p2trTestnet on testnet
1BvBMSEYstWetqTFn5Au4m4GFg7xJaNVN2: #p2pkh on mainnet
xyz: #unknown on mainnet
"""

[<Fact>]
let ``Running - whisper_decoding.nyx decodes the toy model's sentence`` () =
    "whisper_decoding.nyx"
    |> assertExamplePrints
        """transcribed: hello from nanyx!
transcribed: hello from
transcribed: hello from nanyx!
transcribed: from nanyx
error: best-of with greedy sampling
"""
