/// `nanyx/host.memory` gives a host typed access to linear memory, to pass buffers to and from the foreign functions it
/// calls (#1425, see docs/src/content/compiler-internals/foreign-functions.md).
module TranspilerWasmTests.Features.Hosts.HostMemory

open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// A host whose start calls `main`, with `nanyx/host.memory` imported, `writeStdout` (see `hostWriteStdout`), and
/// `definitions` before it.
let private host (definitions: string) =
    $"""module console.host

import (
  nanyx/host.memory
  nanyx/host.runtime
)

{hostWriteStdout}
{definitions}
main()
"""

/// What `source` writes and how many blocks it leaves allocated, run with `arguments` and memory checks, compiled with
/// inlining and without: an address must stay valid either way.
let private runBothWays (arguments: string list) (source: string) =
    [ checkedOptions; { checkedOptions with Inline = false } ] |> List.map (fun options -> runCheckedHost arguments (hostWatWith options source))

[<Fact>]
let ``A host writes and reads bytes, I32s and I64s in a block it allocates, and frees it`` () =
    let source =
        host
            """def main: [HostRuntime] () -> () = { ->
  def block = allocate(16)
  writeI64(block, 1234567890123)
  writeI32(block + 8, -5)
  writeByte(block + 12, 300)
  writeStdout("{readI64(block)} {readI32(block + 8)} {readByte(block + 12)} {readByte(block + 8)}")
  free(block)
}
"""
    Assert.Equal<Result<string * int, Nanyx.Runtime.RunError> list>(List.replicate 2 (Ok ("1234567890123 -5 44 251", 0)), runBothWays [ "test" ] source)

[<Fact>]
let ``A string goes through memory and back as a copy`` () =
    let source =
        host
            """def copied: [HostRuntime] string -> string = { text ->
  stringAt(addressOf(text), byteLength(text))
}

def main: [HostRuntime] () -> () = { ->
  def copy = copied("héllo" ++ " wörld")
  writeStdout("{copy} {byteLength(copy)}")
}
"""
    Assert.Equal<Result<string * int, Nanyx.Runtime.RunError> list>(List.replicate 2 (Ok ("héllo wörld 13", 0)), runBothWays [ "test" ] source)

[<Fact>]
let ``A host reads its arguments with WASI's args_sizes_get and args_get`` () =
    let source =
        host
            """@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] I32, I32 -> I32

@extern("wasi_snapshot_preview1", "args_get")
def argsGet: [HostRuntime] I32, I32 -> I32

def lengthAt: [HostRuntime] I32 -> I32 = { address ->
  if readByte(address) == 0 then 0 else 1 + lengthAt(address + 1)
}

def argumentsBefore: [HostRuntime] I32, I32, list(string) -> list(string) = { pointers, count, acc ->
  if count == 0
    then acc
    else
      def address = readI32(pointers + (count - 1) * 4)
      argumentsBefore(pointers, count - 1, [stringAt(address, lengthAt(address)), ...acc])
}

def arguments: [HostRuntime] () -> list(string) = { ->
  def sizes = allocate(8)
  ignore(argsSizesGet(sizes, sizes + 4))
  def count = readI32(sizes)
  def bufferSize = readI32(sizes + 4)
  free(sizes)
  def pointers = allocate(count * 4)
  def buffer = allocate(bufferSize)
  ignore(argsGet(pointers, buffer))
  def read = argumentsBefore(pointers, count, [])
  free(pointers)
  free(buffer)
  read
}

def writeEach: [HostRuntime] list(string) -> () = {
  | [] -> ()
  | [first, ...rest] ->
    writeStdout(first ++ ";")
    writeEach(rest)
}

def main: [HostRuntime] () -> () = { ->
  writeEach(arguments())
}
"""
    Assert.Equal<Result<string * int, Nanyx.Runtime.RunError> list>(List.replicate 2 (Ok ("tool;a;b c;", 0)), runBothWays [ "tool"; "a"; "b c" ] source)

[<Fact>]
let ``The memory functions require HostRuntime, so a module that isn't a host can't use them`` () =
    let source =
        """import nanyx/host.memory

def peek = { address -> readByte(address) }
"""
    let result = Compiler.compile source
    Assert.Equal<string list>([ DiagnosticCodes.HostRuntimeOutsideHost.Code ], result.Diagnostics |> List.map _.Code)
