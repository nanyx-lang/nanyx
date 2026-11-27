/// `filesystem.inMemory` (`nanyx.fs/filesystem`) runs a block with a `FileSystem` that holds its files in memory, for a
/// test of code that reads and writes files (#3080). Each of its members gives what the console host's does on disk.
module TranspilerWasmTests.Features.Hosts.MemoryFileSystem

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// The operations, each with what it gave on one line, that run on the disk and in memory alike: reads, writes,
/// appends, chunks, listings, made directories and deletes, and each error they can give, from a start of `a.txt`,
/// holding `alpha`, and `sub/b.txt`, holding `beta`.
let private operations =
    """::install nanyx.fs

import (
  nanyx/async as (Async)
  nanyx.fs/dir as dir
  nanyx.fs/file as file
  nanyx.fs/filesystem as (FileSystem)
  nanyx.fs/filesystem as filesystem
  nanyx.fs/path as path
)

def at = { name -> path.of(name) }

def operations: [Async, FileSystem] () -> list(string) = {
  [
    "read a.txt: {file.read(at("a.txt"))}"
    "read sub/b.txt: {file.read(at("sub/b.txt"))}"
    "read missing.txt: {file.read(at("missing.txt"))}"
    "read sub: {file.read(at("sub"))}"
    "read a.txt/x: {file.read(at("a.txt/x"))}"
    "read missing/x: {file.read(at("missing/x"))}"
    "readBytes a.txt: {file.readBytes(at("a.txt"))}"
    "readBytes sub: {file.readBytes(at("sub"))}"
    "write c.txt: {file.write(at("c.txt"), "héllo")}"
    "read c.txt: {file.read(at("c.txt"))}"
    "readBytes c.txt: {file.readBytes(at("c.txt"))}"
    "write a.txt: {file.write(at("a.txt"), "new")}"
    "append a.txt: {file.append(at("a.txt"), " more")}"
    "read a.txt after: {file.read(at("a.txt"))}"
    "append d.txt: {file.append(at("d.txt"), "made")}"
    "read d.txt: {file.read(at("d.txt"))}"
    "write sub: {file.write(at("sub"), "x")}"
    "write missing/x: {file.write(at("missing/x"), "x")}"
    "write a.txt/x: {file.write(at("a.txt/x"), "x")}"
    "append sub: {file.append(at("sub"), "x")}"
    "append missing/x: {file.append(at("missing/x"), "x")}"
    "chunk c.txt 0 2: {readFileChunk(at("c.txt"), 0, 2)}"
    "chunk c.txt 3 10: {readFileChunk(at("c.txt"), 3, 10)}"
    "chunk c.txt 6 1: {readFileChunk(at("c.txt"), 6, 1)}"
    "chunk c.txt 7 1: {readFileChunk(at("c.txt"), 7, 1)}"
    "chunk missing.txt: {readFileChunk(at("missing.txt"), 0, 1)}"
    "chunk sub: {readFileChunk(at("sub"), 0, 1)}"
    "exists a.txt: {file.exists(at("a.txt"))}"
    "exists sub: {file.exists(at("sub"))}"
    "exists .: {file.exists(at("."))}"
    "exists missing.txt: {file.exists(at("missing.txt"))}"
    "exists a.txt/x: {file.exists(at("a.txt/x"))}"
    "list sub: {dir.list(at("sub"))}"
    "list a.txt: {dir.list(at("a.txt"))}"
    "list missing: {dir.list(at("missing"))}"
    "create sub: {dir.create(at("sub"))}"
    "create a.txt: {dir.create(at("a.txt"))}"
    "create new: {dir.create(at("new"))}"
    "create deep/er: {dir.create(at("deep/er"))}"
    "create a.txt/x: {dir.create(at("a.txt/x"))}"
    "write new/e.txt: {file.write(at("new/e.txt"), "e")}"
    "list new: {dir.list(at("new"))}"
    "delete a.txt: {file.delete(at("a.txt"))}"
    "delete a.txt again: {file.delete(at("a.txt"))}"
    "delete sub: {file.delete(at("sub"))}"
    "delete missing/x: {file.delete(at("missing/x"))}"
    "delete d.txt/x: {file.delete(at("d.txt/x"))}"
    "exists a.txt: {file.exists(at("a.txt"))}"
  ]
}
"""

/// The files the operations start from, on disk.
let private writeStart (directory: TempDirectory) =
    directory.Write "a.txt" "alpha"
    directory.Write "sub/b.txt" "beta"

/// The lines `nanyx run` printed for `main`, run after `operations` in `directory`.
let private linesOf (directory: string) (main: string) =
    File.WriteAllText(Path.Combine(directory, "main.nyx"), operations + main)
    let outcome = nanyxIn directory [ "run"; "--check-memory"; "main.nyx" ]
    Assert.True(outcome.ExitCode = 0, outcome.StandardError)
    outcome.StandardOutput.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n') |> List.ofArray

[<Fact>]
let ``Each member gives in memory what the console host's gives on disk`` () =
    use disk = new TempDirectory()
    writeStart disk
    let onDisk = linesOf disk.Path """println(string.join(operations(), "\n"))"""

    use memory = new TempDirectory()
    let inMemory =
        linesOf memory.Path """println(string.join(filesystem.inMemory([("a.txt", "alpha"), ("sub/b.txt", "beta")], operations), "\n"))"""

    Assert.Equal(String.concat "
" onDisk, String.concat "
" inMemory)

[<Fact>]
let ``Files in memory are read back as they were written, and an error is the one the member gives`` () =
    use directory = new TempDirectory()
    let lines =
        linesOf directory.Path """println(string.join(filesystem.inMemory([("a.txt", "alpha"), ("sub/b.txt", "beta")], operations), "\n"))"""

    let line (start: string) = lines |> List.find (fun line -> line.StartsWith(start + ":"))
    Assert.Equal("read sub/b.txt: #some(beta)", line "read sub/b.txt")
    Assert.Equal("read missing.txt: #notFound", line "read missing.txt")
    Assert.Equal("read sub: #isDirectory", line "read sub")
    Assert.Equal("read a.txt after: #some(new more)", line "read a.txt after")
    Assert.Equal("create sub: #alreadyExists", line "create sub")
    Assert.Equal("list new: #some([new/e.txt])", line "list new")
    Assert.Equal("delete a.txt again: #notFound", line "delete a.txt again")

[<Fact>]
let ``A file system in memory writes nothing to the disk`` () =
    use directory = new TempDirectory()
    linesOf directory.Path """println(string.join(filesystem.inMemory([("a.txt", "alpha"), ("sub/b.txt", "beta")], operations), "\n"))"""
    |> ignore

    Assert.Equal<string list>([ "main.nyx" ], Directory.GetFileSystemEntries directory.Path |> Array.map Path.GetFileName |> List.ofArray)

[<Fact>]
let ``A test reads back in memory what the code it tests wrote`` () =
    use directory = new TempDirectory()
    directory.Write
        "main.nyx"
        """::install nanyx.fs

import (
  nanyx/async as (Async)
  nanyx.fs/file as file
  nanyx.fs/filesystem as (FileSystem, FsError)
  nanyx.fs/filesystem as filesystem
  nanyx.fs/path as path
)

def save: [Async, FileSystem] int -> #ok | FsError = { port ->
  file.write(path.of("app.toml"), "port = {port}")
}

@test
def `saves the port` = {
  filesystem.inMemory([("app.toml", "port = 8080")], {
    assert(save(9090) == #ok)
    assert(file.read(path.of("app.toml")) == #some("port = 9090"))
  })
}
"""

    let outcome = nanyxIn directory.Path [ "test"; "main.nyx" ]
    Assert.True(outcome.ExitCode = 0, outcome.StandardOutput + outcome.StandardError)
    Assert.Contains("1 passed", outcome.StandardOutput)
