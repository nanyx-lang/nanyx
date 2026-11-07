module Nanyx.Formatter.Tests.FormatCommandTests

open System
open System.IO
open System.Text
open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.FormatCommand

/// A new empty directory for one test, with the given files (path relative to it, and text) in it.
let private directoryWith (files: (string * string) list) =
    let directory = Path.Combine(Path.GetTempPath(), "nanyx-format-tests", Guid.NewGuid().ToString("N"))
    for path, text in files do
        let full = Path.Combine(directory, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text)
    directory

let private found directory paths =
    match findFiles directory paths with
    | Result.Ok files -> files
    | Result.Error message -> failwith message

let private names directory (files: string list) =
    files |> List.map (fun file -> Path.GetRelativePath(directory, file).Replace('\\', '/'))

let private formatted = "def add = { a, b -> a + b }\n"
let private unformatted = "def add={a,b->a+b}"

let private run mode files = FormatCommand.run Formatter.Options.defaults mode files

[<Fact>]
let ``No paths means every .nyx file under the current directory, skipping build output and hidden directories`` () =
    let directory =
        directoryWith [
            "a.nyx", formatted
            "src/b.nyx", formatted
            "src/notes.txt", "not Nanyx"
            "bin/c.nyx", formatted
            "obj/d.nyx", formatted
            "node_modules/e.nyx", formatted
            ".git/f.nyx", formatted
        ]
    Assert.Equal<string list>([ "a.nyx"; "src/b.nyx" ], names directory (found directory []))

[<Fact>]
let ``A directory means the .nyx files under it, and a file means that file`` () =
    let directory = directoryWith [ "a.nyx", formatted; "src/b.nyx", formatted; "src/c.nyx", formatted; "script.txt", formatted ]
    Assert.Equal<string list>([ "src/b.nyx"; "src/c.nyx"; "script.txt" ], names directory (found directory [ "src"; "script.txt"; "src/b.nyx" ]))

[<Fact>]
let ``A path that does not exist is an error naming it`` () =
    let directory = directoryWith [ "a.nyx", formatted ]
    match findFiles directory [ "a.nyx"; "missing.nyx" ] with
    | Result.Error message -> Assert.Contains("missing.nyx", message)
    | Result.Ok files -> failwith $"Expected an error, found %A{files}"

[<Fact>]
let ``Check mode writes nothing and exits with 1 when a file is not formatted`` () =
    let directory = directoryWith [ "a.nyx", unformatted; "b.nyx", formatted ]
    let results = run Check (found directory [])
    Assert.Equal<FileResult list>([ Changed; Unchanged ], List.map snd results)
    Assert.Equal(unformatted, File.ReadAllText(Path.Combine(directory, "a.nyx")))
    Assert.Equal(1, exitCode Check results)

[<Fact>]
let ``Every mode exits with 0 when everything is formatted`` () =
    let directory = directoryWith [ "a.nyx", formatted ]
    Assert.Equal(0, exitCode Check (run Check (found directory [])))
    Assert.Equal(0, exitCode Write (run Write (found directory [])))

[<Fact>]
let ``Write mode rewrites the files that are not formatted and exits with 0`` () =
    let directory = directoryWith [ "a.nyx", unformatted ]
    let results = run Write (found directory [])
    Assert.Equal(0, exitCode Write results)
    Assert.Equal(formatted, File.ReadAllText(Path.Combine(directory, "a.nyx")))
    Assert.Equal(0, exitCode Check (run Check (found directory [])))

[<Fact>]
let ``A file that does not parse is left alone, and the exit code is 2`` () =
    let directory = directoryWith [ "bad.nyx", "def = = ="; "a.nyx", unformatted ]
    let results = run Write (found directory [])
    match results |> List.map snd with
    | [ Changed; Failed(NotParsed error) ] -> Assert.Equal(0, error.Span.StartLine)
    | other -> failwith $"Unexpected results: %A{other}"
    Assert.Equal("def = = =", File.ReadAllText(Path.Combine(directory, "bad.nyx")))
    Assert.Equal(2, exitCode Write results)
    Assert.Equal(2, exitCode Check results)

[<Fact>]
let ``CRLF line endings are kept`` () =
    let directory = directoryWith [ "a.nyx", "def a={1}\r\ndef b = 2\r\n"; "b.nyx", "def x = 1\r\n" ]
    let results = run Write (found directory [])
    Assert.Equal<FileResult list>([ Changed; Unchanged ], List.map snd results)
    Assert.Equal("def a = { 1 }\r\ndef b = 2\r\n", File.ReadAllText(Path.Combine(directory, "a.nyx")))

[<Fact>]
let ``A byte order mark is kept`` () =
    let directory = directoryWith []
    let file = Path.Combine(directory, "a.nyx")
    Directory.CreateDirectory directory |> ignore
    File.WriteAllText(file, unformatted, UTF8Encoding(true))
    run Write [ file ] |> ignore
    let bytes = File.ReadAllBytes file
    Assert.Equal<byte array>([| 0xEFuy; 0xBBuy; 0xBFuy |], bytes[0..2])
    Assert.Equal(formatted, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3))

[<Fact>]
let ``Formatted text is checked by parsing it again`` () =
    match formatText Formatter.Options.defaults "" "-- a comment\ndef a = [1, ...xs] -- and another\n" with
    | Formatted text -> Assert.Equal("-- a comment\ndef a = [1, ...xs] -- and another\n", text)
    | other -> failwith $"Unexpected: %A{other}"
