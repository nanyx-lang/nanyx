/// The helpers the formatter's tests share.
module Nanyx.Formatter.Tests.TestHelpers

open System
open System.IO
open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.FormatCommand

let lines (ls: string list) = String.concat "\n" ls + "\n"

/// A new empty directory for one test, with the given files (path relative to it, and text) in it.
let directoryWith (files: (string * string) list) =
    let directory = Path.Combine(Path.GetTempPath(), "nanyx-format-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    for path, text in files do
        let full = Path.Combine(directory, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text)
    directory

/// `source` with `\n` line breaks, whatever the line endings of the test file's checkout, without spaces at its end,
/// and with a final line break. A triple-quoted string that ends in `"` is written with a space before its closing
/// quotes, which this drops.
let normalized (source: string) = source.Replace("\r\n", "\n").TrimEnd(' ') + "\n"

/// Formats `source` to lines of at most `width` columns, failing on a parse error.
let formatWith (width: int) (source: string) =
    match Formatter.formatSource { Formatter.Options.defaults with LineWidth = width } "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Formats `source` with the default options, lines of at most 100 columns.
let format (source: string) = formatWith Formatter.Options.defaults.LineWidth source

/// Formatting `source` gives it back unchanged.
let unchanged (source: string) = Assert.Equal(source, format source)

/// Formats `source` as `nanyx format` does, with the check that the result says the same thing with the same comments.
let formatChecked (source: string) =
    match formatText Formatter.Options.defaults "" source with
    | Formatted formatted -> formatted
    | other -> failwith $"Not formatted: %A{other}"

/// Formatting `source` as `nanyx format` does gives `expected`, and formatting that gives it back unchanged. Both are
/// `normalized` first.
let formatsTo (expected: string) (source: string) =
    let expected = normalized expected
    let formatted = formatChecked (normalized source)
    Assert.Equal(expected, formatted)
    Assert.Equal(formatted, formatChecked formatted)
