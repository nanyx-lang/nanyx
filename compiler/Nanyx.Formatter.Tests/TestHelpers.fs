/// The helpers the formatter's tests share.
module Nanyx.Formatter.Tests.TestHelpers

open Xunit
open Nanyx.Formatter

let lines (ls: string list) = String.concat "\n" ls + "\n"

let format (source: string) =
    match Formatter.formatSource Formatter.Options.defaults "" source with
    | Result.Ok formatted -> formatted
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

/// Formatting `source` gives it back unchanged.
let unchanged (source: string) = Assert.Equal(source, format source)
