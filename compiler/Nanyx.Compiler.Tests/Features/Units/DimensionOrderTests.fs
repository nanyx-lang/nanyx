/// A dimension's parts are written in order of their names, however its program is compiled: from a file, whose module
/// sorted before the prelude's, or from memory, whose module sorted after it (#3240).
module Nanyx.Compiler.Tests.Features.DimensionOrder

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private source =
    withFinalNewline "dimension Weight\n@baseUnit unit kg: Weight\n\ndef x: Weight(int) = 2kg * 3s\ndef y: Weight(int) = 2kg / 3s"

let private messages (result: CompileResult) = result.Diagnostics |> List.map _.Message

[<Fact>]
let ``A product and a quotient of a program's dimension and the prelude's are written in order of their names`` () =
    Assert.Equal<string list>(
        [ "'Time(int) * Weight(int)' vs 'Weight(int)'"; "'Weight(float) / Time(float)' vs 'Weight(int)'" ],
        messages (Compiler.compile source))

[<Fact>]
let ``A program writes its dimensions the same compiled from a file as from memory`` () =
    let fromFile =
        withTempDir (fun dir ->
            let path = Path.Combine(dir, "main.nyx")
            File.WriteAllText(path, source)
            Compiler.compileFile path)
    Assert.Equal<string list>(messages (Compiler.compile source), messages fromFile)
