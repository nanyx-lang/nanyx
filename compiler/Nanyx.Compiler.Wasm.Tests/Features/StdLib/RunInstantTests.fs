/// `Instant`, a point in time (#2626): its arithmetic with times, and ISO 8601, written and read back.
module TranspilerWasmTests.Features.StdLib.Instant

open Xunit
open TranspilerWasmTestHelpers

/// A program that reads each of `texts` as an instant and writes it back, with its date, or the failure.
let private readsBack (texts: string list) =
    let lines = texts |> List.map (fun text -> $"  \"{text}\"") |> String.concat "\n"
    $"""def show = {{ text: string ->
  match Instant.fromIsoString(text)
    | #some(i) -> "{{i}} {{Instant.toIsoDate(i)}}"
    | #notIso(failure) -> "not ISO at {{failure.at}}: {{failure.expected}}"
    | #outOfRange -> "out of range"
}}

for [
{lines}
] | text -> println(show(text))
"""

[<Fact>]
let ``Run - an instant is written and read back the same, at the epoch, before it, at leap days and at a year's end`` () =
    let texts =
        [ "1970-01-01T00:00:00Z"
          "1969-12-31T23:59:59Z"
          "1969-12-31T23:59:59.75Z"
          "2000-02-29T12:00:00Z"
          "2024-02-29T23:59:59.999999999Z"
          "1900-03-01T00:00:00Z"
          "2026-12-31T23:59:59Z"
          "2027-01-01T00:00:00Z" ]
    runSource (readsBack texts)
    |> assertRan (texts |> List.map (fun text -> $"{text} {text.Substring(0, 10)}\n") |> String.concat "")

[<Fact>]
let ``Run - an offset is taken off, a fraction is kept, and a date alone is midnight`` () =
    runSource (
        readsBack
            [ "2026-11-25T19:20:57+01:00"
              "2026-11-25T00:20:57-05:30"
              "2026-11-25T18:20:57.250Z"
              "2026-11-25T18:20:57.000000001Z"
              "2026-11-25"
              "+002026-11-25T18:20:57Z"
              "2026-12-31T23:30:00-01:00" ])
    |> assertRan (
        String.concat
            ""
            [ "2026-11-25T18:20:57Z 2026-11-25\n"
              "2026-11-25T05:50:57Z 2026-11-25\n"
              "2026-11-25T18:20:57.25Z 2026-11-25\n"
              "2026-11-25T18:20:57.000000001Z 2026-11-25\n"
              "2026-11-25T00:00:00Z 2026-11-25\n"
              "2026-11-25T18:20:57Z 2026-11-25\n"
              "2027-01-01T00:30:00Z 2027-01-01\n" ])

[<Fact>]
let ``Run - text that isn't ISO 8601 says where it stops being it, and a time no instant reaches is out of range`` () =
    runSource (
        readsBack
            [ "1900-02-29"
              "2026-13-01"
              "2026-11-25T18:20Z"
              "2026-11-25T18:20:57"
              "2026-11-25T18:20:57Zjunk"
              "2026-11-25T24:00:00Z"
              "26-11-25"
              "2026-11-25T18:20:57.Z"
              "3000-01-01"
              "1677-09-21T00:12:43.145224192Z"
              "1677-09-21T00:12:43.145224191Z"
              "2262-04-11T23:47:16.854775807Z"
              "2262-04-11T23:47:16.854775808Z" ])
    |> assertRan (
        String.concat
            ""
            [ "not ISO at 8: a day of the month, from 01 to 28\n"
              "not ISO at 5: a month from 01 to 12\n"
              "not ISO at 16: ':'\n"
              "not ISO at 19: 'Z', or an offset such as '+01:00'\n"
              "not ISO at 20: the end of the text\n"
              "not ISO at 11: an hour from 00 to 23\n"
              "not ISO at 0: a year of four digits\n"
              "not ISO at 20: one to nine digits of a second\n"
              "out of range\n"
              "1677-09-21T00:12:43.145224192Z 1677-09-21\n"
              "out of range\n"
              "2262-04-11T23:47:16.854775807Z 2262-04-11\n"
              "out of range\n" ])

[<Fact>]
let ``Run - an instant is moved and measured by times, compared, and written by a hole`` () =
    let source =
        """def at = { text: string ->
  match Instant.fromIsoString(text)
    | #some(i) -> i
    | _ -> crash("not ISO 8601: {text}")
}

def start = at("2026-11-25T18:20:57Z")
def later = at("2026-11-25T18:22:27.5Z")
def gap: Time(int) = later \since(start)
println(gap)
println(start \since(later))
println(start \plus(90s))
println(later \minus(500ms))
println(start \plus(1d) \Instant.toIsoDate)
println(Instant.epoch)
println(start < later)
println(start == at("2026-11-25T19:20:57+01:00"))
println("{[start, Instant.epoch]}")
"""
    runSource source
    |> assertRan (
        String.concat
            ""
            [ "90500ms\n"
              "-90500ms\n"
              "2026-11-25T18:22:27Z\n"
              "2026-11-25T18:22:27Z\n"
              "2026-11-26\n"
              "1970-01-01T00:00:00Z\n"
              "true\n"
              "true\n"
              "[2026-11-25T18:20:57Z, 1970-01-01T00:00:00Z]\n" ])

[<Fact>]
let ``Run - the time since an earlier now is a time`` () =
    let source =
        """def start = now()
def took: Time(int) = now() \since(start)
println(took >= 0ns)
"""
    runSource source |> assertRan "true\n"
