/// How `nanyx.http/wire` reads the requests and responses the runtime's HTTP imports hand back (see
/// docs/src/content/deep-dives/hosts.md).
module TranspilerWasmTests.Features.Hosts.HttpWire

open System.IO
open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

/// A script that reads a response of `headers` headers and a body of `bodyBytes` bytes with `wire.readResponse`.
let private readingResponse (headers: int) (bodyBytes: int) =
    $"""::dependency nanyx.http

import nanyx.http/wire as wire

def headerText: int, string -> string = {{ n, acc ->
  if n == 0 then acc else headerText(n - 1, "x-header-{{n}}\nvalue {{n}}\n{{acc}}")
}}

@entry
def main = {{
  def body = string.repeat("0123456789abcdef", {bodyBytes / 16})
  def response = "200\n{headers}\n{{headerText({headers}, "")}}{{body}}"

  match wire.readResponse(response)
    | #malformed -> println("malformed")
    | #some(read) -> println("{{read.status}} {{read.headers \length}} {{string.length(read.body)}}")
}}
"""

/// The output of `nanyx run --memory` on `source`, and the bytes it reports allocated.
let private runCountingBytes (source: string) =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source)
    let outcome = nanyxWithInput "" [ "run"; "--memory"; path ]
    Assert.Equal(0, outcome.ExitCode)
    let allocated = Regex.Match(outcome.StandardError, @"bytes allocated +([\d,]+)")
    Assert.True(allocated.Success, outcome.StandardError)
    outcome.StandardOutput.ReplaceLineEndings "\n", int64 (allocated.Groups.[1].Value.Replace(",", ""))

[<Fact>]
let ``Reading a response's headers does not copy its body`` () =
    // Each line is read where it is, so 40 headers cost what their own text does, not a copy of the 1 MB body each
    // (#1887).
    let output, withNone = runCountingBytes (readingResponse 0 1048576)
    Assert.Equal("200 0 1048576\n", output)
    let output, withForty = runCountingBytes (readingResponse 40 1048576)
    Assert.Equal("200 40 1048576\n", output)
    Assert.True(withForty - withNone < 100000L, $"40 headers allocated {withForty - withNone} bytes more than none")
