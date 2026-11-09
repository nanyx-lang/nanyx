module TranspilerWasmTests.E2E

open System.IO
open Xunit
open TranspilerWasmTestHelpers

let private e2eRootDir = Path.Combine(__SOURCE_DIRECTORY__)

let private loadE2EFile testName fileName =
    let filePath = Path.Combine(e2eRootDir, testName, fileName)
    if not (File.Exists(filePath)) then
        failwith $"E2E test file not found: {filePath}"
    File.ReadAllText(filePath)

let private runE2ETest testName =
    let source = loadE2EFile testName "input.nyx"
    let expected = loadE2EFile testName "expected.wat" |> normalizeOutput
    let actual = transpileWat source |> normalizeOutput
    Assert.Equal(expected, actual)

let private runE2EFileTest testName =
    let inputPath = Path.Combine(e2eRootDir, testName, "input.nyx")
    let expected = loadE2EFile testName "expected.wat" |> normalizeOutput
    let actual = transpileWatFile inputPath |> normalizeOutput
    Assert.Equal(expected, actual)

[<Fact>]
let ``E2E - one - hello world`` () =
    runE2ETest "one"

[<Fact>]
let ``E2E - two - pipe expression`` () =
    runE2ETest "two"

[<Fact>]
let ``E2E - three - booleans and conditionals`` () =
    runE2ETest "three"

[<Fact>]
let ``E2E - four - match literals`` () =
    runE2ETest "four"

[<Fact>]
let ``E2E - five - match tags with payload`` () =
    runE2ETest "five"

[<Fact>]
let ``E2E - six - modulo and multi-value match`` () =
    runE2ETest "six"

[<Fact>]
let ``E2E - seven - logical operators`` () =
    runE2ETest "seven"

[<Fact>]
let ``E2E - eight - recursion`` () =
    runE2ETest "eight"

[<Fact>]
let ``E2E - nine - comparison operators`` () =
    runE2ETest "nine"

[<Fact>]
let ``E2E - ten - tuple allocation and access`` () =
    runE2ETest "ten"

[<Fact>]
let ``E2E - eleven - record with named fields`` () =
    runE2ETest "eleven"

[<Fact>]
let ``E2E - twelve - mixed positional and named fields`` () =
    runE2ETest "twelve"

[<Fact>]
let ``E2E - thirteen - type definition and type constructor`` () =
    runE2ETest "thirteen"

[<Fact>]
let ``E2E - fourteen - context use statement`` () =
    runE2ETest "fourteen"

[<Fact>]
let ``E2E - fifteen - context with multiple arities`` () =
    runE2ETest "fifteen"

[<Fact>]
let ``E2E - sixteen - use identifier reference`` () =
    runE2ETest "sixteen"

[<Fact>]
let ``E2E - seventeen - tuple to multi-param function`` () =
    runE2ETest "seventeen"

[<Fact>]
let ``E2E - eighteen - context passing across functions`` () =
    runE2ETest "eighteen"

[<Fact>]
let ``E2E - nineteen - combined context types`` () =
    runE2ETest "nineteen"

[<Fact>]
let ``E2E - twenty - parameterized context instances`` () =
    runE2ETest "twenty"

[<Fact>]
let ``E2E - twentyone - import from other file`` () =
    runE2EFileTest "twentyone"

[<Fact>]
let ``E2E - twentytwo - top-level main call emits start`` () =
    runE2ETest "twentytwo"

[<Fact>]
let ``E2E - twentythree - println from nanyx/console in a script`` () =
    runE2EFileTest "twentythree"

[<Fact>]
let ``E2E - twentyfour - entry attribute runs main at start`` () =
    runE2EFileTest "twentyfour"

[<Fact>]
let ``E2E - twentyfive - list literals and patterns`` () =
    runE2ETest "twentyfive"

[<Fact>]
let ``E2E - twentysix - filter with a shorthand lambda`` () =
    runE2EFileTest "twentysix"
