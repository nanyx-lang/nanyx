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
let ``Run - a year or a fraction of a second of too many digits isn't ISO 8601, however many there are (#3113)`` () =
    // Their digits are counted only so far, and not read, so they overflow no int and use no stack for each.
    let source =
        readsBack
            [ "9223372036854775807-01-01T00:00:00Z"
              "+92233720368547758070-01-01T00:00:00Z"
              "2026-11-25T18:20:57.0000000000000000000000000001Z" ]
        + """println(show(string.repeat("1", 1_000_000) ++ "-01-01T00:00:00Z"))
"""
    runSource source
    |> assertRan (
        String.concat
            ""
            [ "not ISO at 0: a year of four digits\n"
              "not ISO at 1: a year of four to six digits\n"
              "not ISO at 20: one to nine digits of a second\n"
              "not ISO at 0: a year of four digits\n" ])

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

[<Fact>]
let ``Run - an instant's - is the time between two, and + moves one by a time, as since and plus do`` () =
    let source =
        """def start = Instant.epoch
def later = start + 90s
println(later)
println(later - start)
println(start - later)
println(later \since(start) == later - start)
println(later \plus(1s) == later + 1s)
println(later \minus(30s))
"""
    runSource (normalized source)
    |> assertRan "1970-01-01T00:01:30Z\n90s\n-90s\ntrue\ntrue\n1970-01-01T00:01:00Z\n"

[<Fact>]
let ``Run - an instant's Unix time is its time since 1970, and a unit divides it into a number`` () =
    runSource
        """match Instant.fromIsoString("2026-11-26T09:00:00.25Z")
  | #some(at) ->
    def unix = at \toUnixTime
    println(unix // 1ms)
    println(unix // 1s)
    println(Instant.epoch \toUnixTime)
    println((Instant.epoch \minus(1s)) \toUnixTime // 1ms)
  | _ -> println("not ISO")
"""
    |> assertRan "1795683600250\n1795683600\n0s\n-1000\n"

[<Fact>]
let ``Run - shift moves an instant on by a positive time and back by a negative one`` () =
    runSource
        """def start = Instant.epoch + 90s
def back = -30s
println(start \shift(-5s))
println(start \shift(5s))
println(start \shift(back) == start \minus(30s))
println(start \shift(0s) == start)
"""
    |> assertRan "1970-01-01T00:01:25Z\n1970-01-01T00:01:35Z\ntrue\ntrue\n"
