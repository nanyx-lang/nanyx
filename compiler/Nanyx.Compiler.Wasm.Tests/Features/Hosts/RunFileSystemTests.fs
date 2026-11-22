/// The console host (`nanyx.console/console.host`) provides `FileSystem`, from the shipped package `nanyx.fs`, on WASI's
/// file functions: `nanyx run` gives the program the current directory, and the one `--allow-dir` names (#1783, see
/// docs/src/content/deep-dives/hosts.md).
module TranspilerWasmTests.Features.Hosts.FileSystem

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// The directives and imports of a script that uses `nanyx.fs`.
let private header =
    """::dependency nanyx.fs

import (
  nanyx.fs/dir as dir
  nanyx.fs/file as file
  nanyx.fs/path as path
)

"""

/// What `nanyx run` gives, with its memory checked and the options `options`, for `source` after `header`, saved as
/// `main.nyx` in `directory`, which is the current directory.
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

/// `path` as a program writes it in a string literal: with `/`.
let private written (path: string) = path.Replace('\\', '/')

[<Fact>]
let ``A script reads, writes, appends, lists and deletes files in the current directory`` () =
    use directory = new TempDirectory()
    let source =
        """def work = path.of("work")
def notes = path.join(work, "notes.txt")

println(dir.create(work))
println(file.write(notes, "first line\n"))
println(file.append(notes, "second line\n"))
println(file.write(path.join(work, "todo.txt"), "nothing"))
println(file.read(notes))
println(file.exists(notes))
println(dir.list(work))
println(file.delete(notes))
println(file.exists(notes))
println(dir.list(work))
"""
    runIn directory.Path [] source
    |> assertPrinted
        """#ok
#ok
#ok
#ok
#some(first line
second line
)
true
#some([work/notes.txt, work/todo.txt])
#ok
false
#some([work/todo.txt])
"""
    Assert.Equal("nothing", File.ReadAllText(Path.Combine(directory.Path, "work", "todo.txt")))
    Assert.False(File.Exists(Path.Combine(directory.Path, "work", "notes.txt")))

[<Fact>]
let ``Text is written and read as UTF-8, and readBytes gives the bytes`` () =
    use directory = new TempDirectory()
    File.WriteAllBytes(Path.Combine(directory.Path, "given.txt"), [| 0uy; 255uy; 10uy |])
    let source =
        """def greeting = path.of("greeting.txt")

println(file.write(greeting, "héllo"))
println(file.read(greeting))
println(file.readBytes(greeting))
println(file.readBytes(path.of("given.txt")))
"""
    runIn directory.Path [] source
    |> assertPrinted "#ok\n#some(héllo)\n#some([104, 195, 169, 108, 108, 111])\n#some([0, 255, 10])\n"
    Assert.Equal<byte array>([| 104uy; 195uy; 169uy; 108uy; 108uy; 111uy |], File.ReadAllBytes(Path.Combine(directory.Path, "greeting.txt")))

[<Fact>]
let ``What goes wrong is an FsError`` () =
    use directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "folder")) |> ignore
    File.WriteAllText(Path.Combine(directory.Path, "plain.txt"), "text")
    let source =
        """println(file.read(path.of("missing.txt")))
println(file.exists(path.of("missing.txt")))
println(file.read(path.of("folder")))
println(file.delete(path.of("folder")))
println(file.delete(path.of("missing.txt")))
println(dir.create(path.of("folder")))
println(dir.list(path.of("missing")))
println(file.write(path.of("missing/new.txt"), "x"))
println(file.exists(path.of("folder")))
"""
    runIn directory.Path [] source
    |> assertPrinted
        """#notFound
false
#isDirectory
#isDirectory
#notFound
#alreadyExists
#notFound
#notFound
true
"""
    Assert.True(Directory.Exists(Path.Combine(directory.Path, "folder")))

[<Fact>]
let ``A directory too long to list in one read is listed whole`` () =
    use directory = new TempDirectory()
    let listed = Path.Combine(directory.Path, "many")
    Directory.CreateDirectory listed |> ignore
    for index in 1..300 do
        File.WriteAllText(Path.Combine(listed, $"a-file-with-a-rather-long-name-{index:D3}.txt"), "")
    let source =
        """match dir.list(path.of("many"))
  | #some(paths) -> println(list.length(paths))
  | e -> println(e)
"""
    runIn directory.Path [] source |> assertPrinted "300\n"

[<Fact>]
let ``A path outside the current directory is permissionDenied, unless --allow-dir gives its directory`` () =
    use directory = new TempDirectory()
    use other = new TempDirectory()
    File.WriteAllText(Path.Combine(other.Path, "secret.txt"), "secret")
    let source =
        $"""def outside = path.of("{written other.Path}")
def inside = path.of("{written directory.Path}")

println(file.read(path.join(outside, "secret.txt")))
println(file.write(path.join(outside, "new.txt"), "x"))
println(file.exists(path.join(outside, "secret.txt")))
println(file.read(path.of("../secret.txt")))
println(file.write(path.join(inside, "here.txt"), "here"))
println(file.read(path.of("here.txt")))
"""
    runIn directory.Path [] source
    |> assertPrinted "#permissionDenied\n#permissionDenied\nfalse\n#permissionDenied\n#ok\n#some(here)\n"
    Assert.False(File.Exists(Path.Combine(other.Path, "new.txt")))
    runIn directory.Path [ "--allow-dir"; other.Path ] source
    |> assertPrinted "#some(secret)\n#ok\ntrue\n#permissionDenied\n#ok\n#some(here)\n"
    Assert.Equal("x", File.ReadAllText(Path.Combine(other.Path, "new.txt")))

[<Fact>]
let ``--allow-dir naming no directory is a usage error`` () =
    use directory = new TempDirectory()
    let outcome = runIn directory.Path [ "--allow-dir"; "nowhere" ] "println(1)\n"
    Assert.Equal(2, outcome.ExitCode)
    Assert.Equal("error: --allow-dir names nowhere, which is not a directory.\n", outcome.StandardError)

[<Fact>]
let ``A library that requires FileSystem runs under the console host`` () =
    use directory = new TempDirectory()
    let library = Path.Combine(directory.Path, "config")
    Directory.CreateDirectory library |> ignore
    let manifest =
        $"""(package = (name = "acme.config", version = "0.1.0"), dependencies = [(package = "nanyx.fs", version = "{NyxCompiler.Toolchain.version}")])"""
    File.WriteAllText(Path.Combine(library, "acme.config.nyxp"), manifest)
    let config =
        """export module config

import (
  nanyx.fs/file as file
  nanyx.fs/filesystem as (FileSystem)
  nanyx.fs/path as path
)

export def setting: [FileSystem] string -> string = { name ->
  match file.read(path.join(path.of("settings"), name))
    | #some(text) -> text
    | #notFound -> "default"
    | _ -> "unreadable"
}
"""
    File.WriteAllText(Path.Combine(library, "config.nyx"), config)
    Directory.CreateDirectory(Path.Combine(directory.Path, "settings")) |> ignore
    File.WriteAllText(Path.Combine(directory.Path, "settings", "colour"), "blue")
    let main =
        """::dependency acme.config ./config

import acme.config/config

@entry
def main = {
  println(setting("colour"))
  println(setting("size"))
}
"""
    File.WriteAllText(Path.Combine(directory.Path, "main.nyx"), main)
    nanyxIn directory.Path [ "run"; "--check-memory"; "main.nyx" ] |> assertPrinted "blue\ndefault\n"

[<Fact>]
let ``A program that does not use FileSystem imports nothing for it`` () =
    let wat = transpileWat (withFinalNewline """@entry
def main = { println("hi") }""")
    for name in [ "path_open"; "fd_prestat_get"; "fd_readdir"; "fd_close"; "fd_filestat_get"; "path_unlink_file" ] do
        Assert.DoesNotContain(name, wat)

[<Fact>]
let ``A path is normalised, and joined, taken apart and written`` () =
    use directory = new TempDirectory()
    let source =
        """println(path.of("notes\\./drafts/../todo.txt"))
println(path.of(""))
println(path.of("../../a"))
println(path.of("/../a/"))
println(path.of("C:\\Users\\ada"))
println(path.join(path.of("logs"), "today.txt"))
println(path.join(path.of("logs"), "/var/log"))
println(path.join(path.of("."), "a"))
println(path.parent(path.of("logs/today.txt")))
println(path.parent(path.of("today.txt")))
println(path.parent(path.of(".")))
println(path.parent(path.of("/")))
println(path.parent(path.of("C:/")))
println(path.fileName(path.of("logs/today.txt")))
println(path.fileName(path.of("..")))
println(path.extension(path.of("archive.tar.gz")))
println(path.extension(path.of(".gitignore")))
println(path.extension(path.of("README")))
println(path.of("a/b") == path.of("a\\b"))
"""
    runIn directory.Path [] source
    |> assertPrinted
        """notes/todo.txt
.
../../a
/a
C:/Users/ada
logs/today.txt
/var/log
a
#some(logs)
#some(.)
#none
#none
#none
#some(today.txt)
#none
#some(gz)
#none
#none
true
"""
