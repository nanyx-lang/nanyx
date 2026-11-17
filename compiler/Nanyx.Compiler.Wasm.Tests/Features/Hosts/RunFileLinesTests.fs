/// `file.lines` reads a file lazily, a chunk at a time, as a `[FileSystem] seq(string)` (#1784, see
/// docs/src/content/work-in-progress/files-http-json.md).
module TranspilerWasmTests.Features.Hosts.FileLines

open System.IO
open System.Text
open Xunit
open TranspilerWasmTestHelpers

/// The directives and imports of a script that uses `nanyx.fs`.
let private header =
    """::dependency nanyx.fs

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

"""

/// What `nanyx run --check-memory`, with the options `options`, gives for `source` after `header`, saved as `main.nyx`
/// in `directory`, which is the current directory.
let private runIn (directory: string) (options: string list) (source: string) =
    File.WriteAllText(Path.Combine(directory, "main.nyx"), header + source)
    let outcome = nanyxIn directory ([ "run"; "--check-memory" ] @ options @ [ "main.nyx" ])
    { outcome with
        StandardOutput = outcome.StandardOutput.ReplaceLineEndings "\n"
        StandardError = outcome.StandardError.ReplaceLineEndings "\n" }

/// Asserts that `outcome` ran cleanly and printed `expected`.
let private assertPrinted (expected: string) (outcome: RunOutcome) =
    Assert.Equal("", outcome.StandardError)
    Assert.Equal(expected.ReplaceLineEndings "\n", outcome.StandardOutput)
    Assert.Equal(0, outcome.ExitCode)

[<Fact>]
let ``A file is read line by line, without its line breaks`` () =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "crlf.txt"), "one\r\ntwo\r\n\r\nfour")
    File.WriteAllText(Path.Combine(directory.Path, "lf.txt"), "é\nü\n")
    File.WriteAllText(Path.Combine(directory.Path, "empty.txt"), "")
    let source =
        """def show = { name ->
  match file.lines(path.of(name))
    | #some(lines) -> println(seq.toList(lines))
    | #error(#notFound) -> println("not found")
    | #error(#isDirectory) -> println("a directory")
    | #error(#notDirectory) -> println("not a directory")
    | #error(#permissionDenied) -> println("permission denied")
    | #error(_) -> println("another error")
}

show("crlf.txt")
show("lf.txt")
show("empty.txt")
show("missing.txt")
show(".")
show("lf.txt/inside.txt")
show("../outside.txt")
"""
    runIn directory.Path [] source
    |> assertPrinted
        """[one, two, , four]
[é, ü]
[]
not found
a directory
not a directory
permission denied
"""

[<Theory>]
[<InlineData("")>]
[<InlineData("--no-inline")>]
let ``The lines of a file much larger than a chunk are counted, a chunk at a time`` (option: string) =
    use directory = new TempDirectory()
    let text = StringBuilder()
    for index in 0 .. 199999 do
        text.Append($"line {index} of a file of six megabytes or so\n") |> ignore
    File.WriteAllText(Path.Combine(directory.Path, "big.txt"), text.ToString())
    let source =
        """match file.lines(path.of("big.txt"))
  | #some(lines) ->
    println(seq.fold(lines, 0, { count, _ -> count + 1 }))
    println(seq.toList(seq.skip(lines, 199998)))
  | #error(_) -> println("error")
"""
    runIn directory.Path (if option = "" then [] else [ option ]) source
    |> assertPrinted
        """200000
[line 199998 of a file of six megabytes or so, line 199999 of a file of six megabytes or so]
"""

/// A file of `count` numbered lines, `line 0` first, in `directory`, which takes many chunks to read.
let private writeLog (directory: string) (count: int) =
    let text = StringBuilder()
    for index in 0 .. count - 1 do
        text.Append($"line {index}\n") |> ignore
    File.WriteAllText(Path.Combine(directory, "log.txt"), text.ToString())

// A file is opened for each chunk and closed before the chunk is given back, so a sequence holds nothing open between
// pulls: the file can be deleted, which Windows refuses for a file that is open. A read that then fails partway stops
// the program, rather than ending the sequence as if the file were shorter.
[<Fact>]
let ``Pulling only the first line leaves the file closed, and a read that fails later crashes, naming the file`` () =
    use directory = new TempDirectory()
    writeLog directory.Path 100000
    let source =
        """match file.lines(path.of("log.txt"))
  | #some(lines) ->
    println(seq.toList(seq.take(lines, 1)))
    println(file.delete(path.of("log.txt")))
    println(seq.fold(lines, 0, { count, _ -> count + 1 }))
  | #error(_) -> println("error")
"""
    let outcome = runIn directory.Path [] source
    Assert.Equal("[line 0]\n#ok\n", outcome.StandardOutput)
    Assert.Contains("crash: Couldn't read the rest of 'log.txt': it is no longer there", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)
    Assert.False(File.Exists(Path.Combine(directory.Path, "log.txt")))

[<Fact>]
let ``A file cut short while it is read crashes, rather than seeming to end`` () =
    use directory = new TempDirectory()
    writeLog directory.Path 100000
    let source =
        """match file.lines(path.of("log.txt"))
  | #some(lines) ->
    println(seq.toList(seq.take(lines, 1)))
    println(file.write(path.of("log.txt"), "line 0\n"))
    println(seq.fold(lines, 0, { count, _ -> count + 1 }))
  | #error(_) -> println("error")
"""
    let outcome = runIn directory.Path [] source
    Assert.Equal("[line 0]\n#ok\n", outcome.StandardOutput)
    Assert.Contains("crash: Couldn't read the rest of 'log.txt': it is shorter than it was", outcome.StandardError)
    Assert.Equal(1, outcome.ExitCode)

[<Fact>]
let ``A file outside the current directory has no lines to read`` () =
    use directory = new TempDirectory()
    use other = new TempDirectory()
    File.WriteAllText(Path.Combine(other.Path, "secret.txt"), "secret\n")
    let source =
        $"""match file.lines(path.of("{other.Path.Replace('\\', '/')}/secret.txt"))
  | #some(_) -> println("read")
  | #error(#permissionDenied) -> println("permission denied")
  | #error(_) -> println("another error")
"""
    runIn directory.Path [] source |> assertPrinted "permission denied\n"
