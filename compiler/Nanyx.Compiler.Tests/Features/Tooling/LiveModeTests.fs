/// What live mode knows of a file (`LiveMode`): its entry point, and the contexts that entry point requires, which say
/// whether live mode is offered on its own (#2055).
module Nanyx.Compiler.Tests.Features.LiveMode

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The entry point of the script `source`, saved as `main.nyx`, and whether live mode is offered for it.
let private scriptEntry (source: string) =
    use package = new TempPackage([ "main.nyx", source ])
    let result = Script.compileSourceIn (SeededPackages.user ()) (package.Path "main.nyx") source
    result.Typed.Value |> LiveMode.entryOf |> Option.map (fun entry -> entry.Kind, entry.Contexts, LiveMode.offered entry)

[<Fact>]
let ``A script that prints is an entry point that requires Console, and live mode is offered`` () =
    Assert.Equal(Some (LiveMode.Statements, [ "Console" ], true), scriptEntry "println(\"hi\")\n")

[<Fact>]
let ``A script that reads files requires Async and FileSystem too, and live mode is still offered`` () =
    let source =
        "::install nanyx.fs\n\nimport (\n  nanyx.fs/file as file\n  nanyx.fs/path as path\n)\n\nprintln(file.read(path.of(\"a.txt\")))\n"
    match scriptEntry source with
    | Some (LiveMode.Statements, contexts, offered) ->
        Assert.Equal<string list>([ "Async"; "Console"; "FileSystem" ], List.sort contexts)
        Assert.True offered
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A script that reads its arguments requires Env, and live mode isn't offered on its own`` () =
    match scriptEntry "println(\"{args()}\")\n" with
    | Some (LiveMode.Statements, contexts, offered) ->
        Assert.Contains("Env", contexts)
        Assert.False offered
    | other -> Assert.Fail $"%A{other}"

[<Fact>]
let ``A script with nothing to run has no entry point`` () =
    Assert.Equal(None, scriptEntry "def answer = 42\n")

[<Fact>]
let ``A package's @entry is an entry point, with the contexts it requires`` () =
    let result = compileWithConsoleCore "module main\n\n@entry\ndef main = {\n  println(\"hi\")\n}\n"
    let entry = result.Typed.Value |> LiveMode.entryOf
    Assert.Equal(Some (LiveMode.EntryDefinition, [ "Console" ]), entry |> Option.map (fun entry -> entry.Kind, entry.Contexts))

[<Fact>]
let ``A module with no @entry has no entry point`` () =
    let result = compileWithConsoleCore "module main\n\nexport def answer = 42\n"
    Assert.Equal(None, result.Typed.Value |> LiveMode.entryOf)
