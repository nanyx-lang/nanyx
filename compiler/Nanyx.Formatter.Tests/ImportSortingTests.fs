/// How the formatter sorts imports: within each run of them, standard library first, then packages, then paths.
module Nanyx.Formatter.Tests.ImportSortingTests

open Xunit
open Parser.Program
open Nanyx.Formatter
open Nanyx.Formatter.FormatCommand

/// `source` with `\n` line breaks, whatever the line endings of this file's checkout, and a final line break.
let private normalized (source: string) = source.Replace("\r\n", "\n") + "\n"

/// Formats `source` as `nanyx format` does, with the check that the result says the same thing with the same comments.
let private format (source: string) =
    match formatText Formatter.Options.defaults "" source with
    | Formatted formatted -> formatted
    | other -> failwith $"Not formatted: %A{other}"

/// Formatting `source` gives `expected`, and formatting that gives it back unchanged.
let private formatsTo (expected: string) (source: string) =
    let expected = normalized expected
    let formatted = format (normalized source)
    Assert.Equal(expected, formatted)
    Assert.Equal(formatted, format formatted)

let private parse (source: string) =
    match parseModuleWithTrivia "" (normalized source) with
    | Result.Ok parsed -> parsed.Items
    | Result.Error error -> failwith $"Parse failed: {error.Message}"

[<Fact>]
let ``A run of imports out of order is sorted`` () =
    formatsTo
        """import (
  nanyx/console
  nanyx/math
  nanyx/text
)

def a = 1"""
        """import nanyx/text
import nanyx/console
import nanyx/math

def a = 1"""

[<Fact>]
let ``The names in one bracketed import are sorted`` () =
    formatsTo
        """import (
  nanyx/list
  nanyx/text
)"""
        """import (
  nanyx/text
  nanyx/list
)"""

[<Fact>]
let ``Runs separated by a blank line are each sorted, and not merged`` () =
    formatsTo
        """import (
  nanyx/math
  nanyx/text
)

import (
  nanyx/console
  nanyx/list
)"""
        """import nanyx/text
import nanyx/math

import nanyx/list
import nanyx/console"""

[<Fact>]
let ``A comment on a line of its own ends a run and stays where it is`` () =
    formatsTo
        """import (
  nanyx/math
  nanyx/text
  -- for parsing
  nanyx/list
  nanyx/result
)"""
        """import nanyx/text
import nanyx/math
-- for parsing
import nanyx/result
import nanyx/list"""

[<Fact>]
let ``A block comment ends a run and stays where it is`` () =
    formatsTo
        """import (
  nanyx/math
  nanyx/text
  ---
  for parsing
  ---
  nanyx/list
  nanyx/result
)"""
        """import (
  nanyx/text
  nanyx/math
  ---
  for parsing
  ---
  nanyx/result
  nanyx/list
)"""

[<Fact>]
let ``The standard library comes first, then other packages, then paths`` () =
    formatsTo
        """import (
  nanyx/text
  io/file
  json
  "./local.nyx"
)"""
        """import "./local.nyx"
import json
import nanyx/text
import io/file"""

[<Fact>]
let ``Paths are sorted alphabetically among themselves`` () =
    formatsTo
        """import (
  "../b.nyx"
  "./a.nyx"
)"""
        """import "./a.nyx"
import "../b.nyx" """

[<Fact>]
let ``Case is ignored, except to break a tie`` () =
    formatsTo
        """import (
  Alpha
  beta
  Gamma
  gamma
)"""
        """import gamma
import beta
import Gamma
import Alpha"""

[<Fact>]
let ``The alias does not affect the order`` () =
    formatsTo
        """import (
  nanyx/math as z
  nanyx/text as a
)"""
        """import nanyx/text as a
import nanyx/math as z"""

[<Fact>]
let ``A comment at the end of an import's line moves with it`` () =
    formatsTo
        """import (
  nanyx/math -- for sqrt
  nanyx/text -- for padding
)"""
        """import nanyx/text -- for padding
import nanyx/math -- for sqrt"""
    formatsTo
        """import (
  nanyx/list
  nanyx/math -- for sqrt
  nanyx/text
)"""
        """import (
  nanyx/text
  nanyx/math -- for sqrt
  nanyx/list
)"""

[<Fact>]
let ``Duplicate imports are kept`` () =
    formatsTo
        """import (
  nanyx/math
  nanyx/text
  nanyx/text
)"""
        """import nanyx/text
import nanyx/math
import nanyx/text"""

[<Fact>]
let ``Imports in a module block are sorted too`` () =
    formatsTo
        """module m =
  import (
    nanyx/math
    nanyx/text
  )

  def a = 1"""
        """module m =
  import nanyx/text
  import nanyx/math
  def a = 1"""

[<Fact>]
let ``The safety check compares against the original with its imports sorted`` () =
    let original = parse """import nanyx/text
import nanyx/math"""
    let sorted = parse """import (
  nanyx/math
  nanyx/text
)"""
    let dropped = parse "import nanyx/math"
    let duplicated = parse """import (
  nanyx/math
  nanyx/math
  nanyx/text
)"""
    Assert.True(Syntax.same original sorted)
    Assert.False(Syntax.same original dropped)
    Assert.False(Syntax.same original duplicated)
