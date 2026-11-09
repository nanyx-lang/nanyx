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

let private run mode files = FormatCommand.run (Settings.cached ()) mode files

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

let private block = "def x =\n  def a = 1\n  a\n"

let private settingsOf file =
    match Settings.forFile file with
    | Result.Ok options -> options
    | Result.Error message -> failwith message

let private formattedWith options text =
    match formatText options "" text with
    | Formatted formatted -> formatted
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``With no manifest the defaults apply`` () =
    let directory = directoryWith [ "a.nyx", block ]
    Assert.Equal(Formatter.Options.defaults, settingsOf (Path.Combine(directory, "a.nyx")))

[<Fact>]
let ``With a manifest that has no formatter table the defaults apply`` () =
    let directory = directoryWith [ "app.nyxpack", "name = \"app\"\n\n[dependencies]\n"; "a.nyx", block ]
    Assert.Equal(Formatter.Options.defaults, settingsOf (Path.Combine(directory, "a.nyx")))

[<Fact>]
let ``indentSize sets the indent`` () =
    let directory = directoryWith [ "app.nyxpack", "[formatter]\nindentSize = 4\n"; "a.nyx", block ]
    let file = Path.Combine(directory, "a.nyx")
    Assert.Equal({ Formatter.Options.defaults with IndentSize = 4 }, settingsOf file)
    run Write [ file ] |> ignore
    Assert.Equal("def x =\n    def a = 1\n    a\n", File.ReadAllText file)

[<Fact>]
let ``lineWidth sets the line width`` () =
    let directory = directoryWith [ "app.nyxpack", "[formatter]\nlineWidth = 20\n"; "a.nyx", unformatted ]
    let file = Path.Combine(directory, "a.nyx")
    let options = { Formatter.Options.defaults with LineWidth = 20 }
    Assert.Equal(options, settingsOf file)
    run Write [ file ] |> ignore
    Assert.Equal(formattedWith options unformatted, File.ReadAllText file)

[<Fact>]
let ``The nearest manifest in a parent directory applies`` () =
    let directory =
        directoryWith [ "app.nyxpack", "[formatter]\nindentSize = 4\n"; "src/deep/a.nyx", block; "src/b.nyx", block ]
    Assert.Equal(4, (settingsOf (Path.Combine(directory, "src/deep/a.nyx"))).IndentSize)
    Assert.Equal(4, (settingsOf (Path.Combine(directory, "src/b.nyx"))).IndentSize)

[<Fact>]
let ``Two packages with different settings are formatted in one run, each with its own`` () =
    let directory =
        directoryWith [
            "one/one.nyxpack", "[formatter]\nindentSize = 4\n"
            "one/a.nyx", block
            "two/two.nyxpack", "[formatter]\nindentSize = 3\n"
            "two/b.nyx", block
        ]
    let results = run Write (found directory [])
    Assert.Equal(0, exitCode Write results)
    Assert.Equal("def x =\n    def a = 1\n    a\n", File.ReadAllText(Path.Combine(directory, "one/a.nyx")))
    Assert.Equal("def x =\n   def a = 1\n   a\n", File.ReadAllText(Path.Combine(directory, "two/b.nyx")))

/// Formats a package whose manifest has `formatter` as its `[formatter]` table, next to a package with valid settings,
/// and returns the error for the first package's files, checking that none of them was touched and the other was.
let private badSettings (formatter: string) =
    let directory =
        directoryWith [
            "bad/bad.nyxpack", formatter
            "bad/a.nyx", unformatted
            "bad/src/b.nyx", unformatted
            "good/good.nyxpack", "[formatter]\nlineWidth = 80\n"
            "good/c.nyx", unformatted
        ]
    let results = run Write (found directory [])
    Assert.Equal(2, exitCode Write results)
    Assert.Equal(unformatted, File.ReadAllText(Path.Combine(directory, "bad/a.nyx")))
    Assert.Equal(unformatted, File.ReadAllText(Path.Combine(directory, "bad/src/b.nyx")))
    Assert.Equal(formatted, File.ReadAllText(Path.Combine(directory, "good/c.nyx")))
    match results |> List.map snd with
    | [ Failed(BadSettings message); Failed(BadSettings message'); Changed ] ->
        Assert.Equal(message, message')
        Assert.Contains(Path.Combine(directory, "bad", "bad.nyxpack"), message)
        message
    | other -> failwith $"Unexpected results: %A{other}"

[<Fact>]
let ``An unknown setting is an error naming the manifest and the key, and nothing under the manifest is formatted`` () =
    let message = badSettings "[formatter]\nlineWidth = 80\ntabs = true\n"
    Assert.Contains("[formatter] tabs", message)
    Assert.Contains("lineWidth and indentSize", message)

[<Theory>]
[<InlineData("lineWidth = 0", "lineWidth", "not 0")>]
[<InlineData("indentSize = -2", "indentSize", "not -2")>]
[<InlineData("lineWidth = 80.5", "lineWidth", "a float")>]
[<InlineData("indentSize = \"4\"", "indentSize", "the string \"4\"")>]
[<InlineData("lineWidth = 99999999999", "lineWidth", "not 99999999999")>]
let ``A setting that is not a positive integer is an error naming the manifest and the key`` (setting: string, key: string, found: string) =
    let message = badSettings $"[formatter]\n{setting}\n"
    Assert.Contains($"[formatter] {key} must be a positive integer", message)
    Assert.Contains(found, message)

[<Fact>]
let ``A manifest that is not valid TOML is an error naming it`` () =
    Assert.Contains("not valid TOML", badSettings "[formatter\nlineWidth = 80\n")

[<Fact>]
let ``A formatter key that is not a table is an error naming it`` () =
    Assert.Contains("formatter must be a table", badSettings "formatter = 3\n")

[<Fact>]
let ``A script's #! line and directives are kept as written, with the comments among them`` () =
    let header = "#!/usr/bin/env -S nanyx run\n-- Says hello.\n::linters.default.unusedBindings   =  \"error\"\n\n::formatter.lineWidth = 100"
    Assert.Equal(header + "\n\ndef add = { a, b -> a + b }\n", formattedWith Formatter.Options.defaults (header + "\n\n\ndef add={a,b->a+b}"))
    Assert.Equal(header + "\ndef add = { a, b -> a + b }\n", formattedWith Formatter.Options.defaults (header + "\ndef add={a,b->a+b}"))
    Assert.Equal(header + "\n", formattedWith Formatter.Options.defaults (header + "\n\n"))

[<Fact>]
let ``A script's formatter directives set its options`` () =
    let directory = directoryWith [ "a.nyx", "::formatter.indentSize = 4\n::formatter.lineWidth = 20\n" + block ]
    let file = Path.Combine(directory, "a.nyx")
    Assert.Equal(Result.Ok { Formatter.Options.LineWidth = 20; Formatter.Options.IndentSize = 4 }, Settings.withDirectives file (File.ReadAllText file) Formatter.Options.defaults)
    run Write [ file ] |> ignore
    Assert.Equal("::formatter.indentSize = 4\n::formatter.lineWidth = 20\ndef x =\n    def a = 1\n    a\n", File.ReadAllText file)

[<Theory>]
[<InlineData("::formatter.tabs = 2", "unknown setting ::formatter.tabs; the settings are lineWidth and indentSize")>]
[<InlineData("::formatter.lineWidth = 0", "::formatter.lineWidth must be a positive integer, not 0")>]
[<InlineData("::formatter.lineWidth = \"80\"", "::formatter.lineWidth must be a positive integer, not the string \"80\"")>]
[<InlineData("::formatter.lineWidth", "::formatter takes a setting and its value")>]
let ``A formatter directive that is not valid is an error naming the file and line, and the file is left alone`` (directive: string, message: string) =
    let directory = directoryWith [ "a.nyx", "-- A script.\n" + directive + "\n" + unformatted ]
    let file = Path.Combine(directory, "a.nyx")
    match run Write [ file ] with
    | [ _, Failed(BadSettings error) ] -> Assert.StartsWith($"{file}:2:1: {message}", error)
    | other -> failwith $"Unexpected results: %A{other}"
    Assert.Equal("-- A script.\n" + directive + "\n" + unformatted, File.ReadAllText file)

[<Fact>]
let ``A formatter directive in a file of a package is an error saying to use the manifest`` () =
    let directory = directoryWith [ "app.nyxpack", "[formatter]\nindentSize = 4\n"; "a.nyx", "::formatter.indentSize = 2\n" + block ]
    let file = Path.Combine(directory, "a.nyx")
    match run Write [ file ] with
    | [ _, Failed(BadSettings error) ] -> Assert.Contains("[formatter] table of " + Path.Combine(directory, "app.nyxpack"), error)
    | other -> failwith $"Unexpected results: %A{other}"
