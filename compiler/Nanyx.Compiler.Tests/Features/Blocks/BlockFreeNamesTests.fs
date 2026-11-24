/// The names a block refers to that nothing within it binds (`SyntaxTree.freeNames`), which lambda lifting asks of each
/// lambda it lifts, and checking of each def.
module Nanyx.Compiler.Tests.Features.BlockFreeNames

open Xunit
open Parser.Program
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The value of the def `run` in `source`.
let private runOf (source: string) =
    match parseModuleFile "main.nyx" source with
    | Ok items -> items |> List.pick (function Kind (Def (ValueDef(_, "run", _, value))) -> Some value | _ -> None)
    | Error error -> failwith error

/// A memory block that stores into its local `count` times.
let private storing (count: int) =
    String.concat "\n" [ "def run = { start ->"; "  memory {"; "    mut x = start"; yield! List.replicate count "    x := x + 1"; "    x"; "  }"; "}"; "" ]

[<Fact>]
let ``A block's free names are the names it uses and does not bind, in the order they are written`` () =
    let run = runOf "def run = { start ->\n  first(start)\n  def b = second(start)\n  third(b)\n}\n"
    Assert.Equal<string list>([ "first"; "second"; "third" ], SyntaxTree.freeNames run)

// Each statement's names were added to all the names before them, so a block of n statements took n² time: lambda
// lifting spent 5 of the 5.4 seconds it took on a memory block of 16,000 stores there (#2408).
[<Fact>]
let ``A block's free names are found in time that grows with its length`` () =
    takesLinearTime 16_000 (fun count ->
        let run = runOf (storing count)
        let clock = System.Diagnostics.Stopwatch.StartNew()
        SyntaxTree.freeNames run |> ignore
        clock.Elapsed.TotalMilliseconds)
