/// Custom patterns (see the custom patterns page): the patterns in scope, found by their tags, and what a match on their tags checks.
module Nanyx.Compiler.Tests.Features.CustomPatterns

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The codes of the diagnostics compiling `result` gave.
let private codesOf (result: CompileResult) = result.Diagnostics |> List.map _.Code

let private evenOdd = "pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }"

[<Fact>]
let ``A capitalised tag that no pattern returns is an error, with a fix that writes it in lower case`` () =
    let source = "def answer = #Some(42)"
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.UnknownPattern.Code, code)
    Assert.Equal("#Some(42)", text)
    Assert.Contains("No pattern in scope returns `#Some`", message)
    let fix = Assert.Single((single (Compiler.compile source)).Fixes)
    Assert.Equal("def answer = #some(42)", applyFix source fix)

[<Fact>]
let ``A capitalised tag in a pattern or a type names a pattern too`` () =
    let source =
        """
def f: #Yes | #no -> int = { x ->
  match x
    | #Yes -> 1
    | #no -> 0
}
"""
            .ReplaceLineEndings "\n"
    let result = Compiler.compile source
    let unknown = result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.UnknownPattern.Code)
    Assert.Equal<string list>([ "#Yes"; "#Yes" ], unknown |> List.map (spanOf >> textAt source))

[<Fact>]
let ``A pattern's own tags name it, in its type and its body`` () =
    let result = Compiler.compile $"{evenOdd}\ndef e = #Even"
    Assert.DoesNotContain(DiagnosticCodes.UnknownPattern.Code, codesOf result)

[<Fact>]
let ``Two patterns a module declares that return one tag are an error at the second`` () =
    let source = $"{evenOdd}\npattern int -> #Odd | #Big = {{ n -> if n > 100 then #Big else #Odd }}"
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.AmbiguousPattern.Code, code)
    Assert.Equal("int -> #Odd | #Big", text)
    Assert.Contains("Another pattern in this module returns `#Odd` too", message)

[<Fact>]
let ``An exported pattern is in scope where its module is imported`` () =
    let result = compileImportingAll [ "parity.nyx", $"module parity\nexport {evenOdd}" ] "module main\nimport parity\ndef e = #Even"
    Assert.DoesNotContain(DiagnosticCodes.UnknownPattern.Code, codesOf result)

[<Fact>]
let ``A pattern that is not exported is not in scope where its module is imported`` () =
    let result = compileImportingAll [ "parity.nyx", $"module parity\n{evenOdd}\nexport def x = 1" ] "module main\nimport parity\ndef e = #Even"
    assertReports DiagnosticCodes.UnknownPattern result

[<Fact>]
let ``Two imported patterns that return one tag are ambiguous where the tag is used`` () =
    let modules =
        [ "parity.nyx", $"module parity\nexport {evenOdd}"
          "oddity.nyx", "module oddity\nexport pattern int -> #Odd | #Normal = { n -> #Normal }" ]
    let result = compileImportingAll modules "module main\nimport parity\nimport oddity\ndef o = #Odd"
    assertReports DiagnosticCodes.AmbiguousPattern result
    Assert.DoesNotContain(DiagnosticCodes.AmbiguousPattern.Code, codesOf (compileImportingAll modules "module main\nimport parity\nimport oddity\ndef e = #Even"))

[<Fact>]
let ``Two imported patterns of the same tags are ambiguous too`` () =
    let modules =
        [ "parity.nyx", $"module parity\nexport {evenOdd}"
          "other.nyx", $"module other\nexport {evenOdd}" ]
    let result = compileImportingAll modules "module main\nimport parity\nimport other\ndef e = #Even"
    assertReports DiagnosticCodes.AmbiguousPattern result
    Assert.Contains("parity", (result.Diagnostics |> List.find (fun d -> d.Code = DiagnosticCodes.AmbiguousPattern.Code)).Message)

[<Fact>]
let ``A module's own pattern wins over an imported one`` () =
    let result = compileImportingAll [ "parity.nyx", $"module parity\nexport {evenOdd}" ] $"module main\nimport parity\n{evenOdd}\ndef e = #Even"
    Assert.DoesNotContain(DiagnosticCodes.AmbiguousPattern.Code, codesOf result)

[<Fact>]
let ``A match that leaves out a total pattern's tag is not exhaustive, and says which`` () =
    let source =
        $"""
{evenOdd}

def parity = {{ n: int ->
  match n
    | #Even -> "even"
}}
"""
            .ReplaceLineEndings "\n"
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, code)
    Assert.Equal("match n", text)
    Assert.Contains("Missing `#Odd`", message)

[<Fact>]
let ``A total pattern's tags make a match exhaustive, alone or with ordinary patterns`` () =
    let source =
        $"""
{evenOdd}

def parity = {{ n: int ->
  match n
    | 0 -> "zero"
    | #Even -> "even"
    | #Odd -> "odd"
}}
"""
            .ReplaceLineEndings "\n"
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A pattern's tag inside another pattern is an error`` () =
    let source =
        $"""
{evenOdd}

def f = {{ x ->
  match x
    | #some(#Even) -> 1
    | _ -> 2
}}
"""
            .ReplaceLineEndings "\n"
    let code, _, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.PatternInsidePattern.Code, code)
    Assert.Equal("#Even", text)

[<Fact>]
let ``A pattern whose body requires a context is an error`` () =
    let source =
        """
pattern int -> #Loud | #Quiet = { n ->
  println("checking")
  #Loud
}
"""
            .ReplaceLineEndings "\n"
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.PatternRequiresContexts.Code, code)
    Assert.Contains("a pattern is pure", message)
    Assert.Equal("println", text)
    Assert.Empty((single (Compiler.compile source)).Fixes)
[<Fact>]
let ``A match on a partial pattern without a _ arm is not exhaustive, and says so`` () =
    let source =
        """
pattern int -> #Zero | _ = { n -> n == 0 }

def describe = { (num: int) ->
  match num
    | #Zero -> "zero"
}
"""
            .ReplaceLineEndings "\n"
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, code)
    Assert.Equal("match num", text)
    Assert.Contains("Missing a `_` arm for the values the pattern doesn't match", message)

[<Fact>]
let ``A partial pattern with several tags names the tags a match leaves out, and the _ arm`` () =
    let source =
        """
pattern string -> #ParseInt(int) | #ParseBool(bool) | _ = { s ->
  if
    | int.fromString(s) is #some(i) -> #ParseInt(i)
    | bool.fromString(s) is #some(b) -> #ParseBool(b)
    | else -> _
}

def parse = { (s: string) ->
  match s
    | #ParseInt(i) -> i
}
"""
            .ReplaceLineEndings "\n"
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, code)
    Assert.Contains("Missing `#ParseBool`, which the pattern returns too, and a `_` arm", message)

[<Fact>]
let ``_ as a value outside a pattern's body is an error of its own`` () =
    let code, message, text = onlyDiagnostic "def nothing = _"
    Assert.Equal(DiagnosticCodes.NoMatchOutsidePattern.Code, code)
    Assert.Equal("_", text)
    Assert.Contains("no match", message)
let private divisibleBy = "pattern int -> int -> #DivisibleBy | _ = { divisor -> { n -> n % divisor == 0 } }"

/// The one diagnostic of `divisibleBy` and a function of `n` that matches it with `arm` and then `_`.
let private armWith (arm: string) =
    let source =
        """
pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }

def f = { (n: int) ->
  match n
    | ARM -> 1
    | _ -> 2
}
"""
    onlyDiagnostic (divisibleBy + source.Replace("ARM", arm).ReplaceLineEndings "\n")

[<Fact>]
let ``A pattern that takes arguments, used without them, is an error`` () =
    let code, message, text = armWith "#DivisibleBy"
    Assert.Equal(DiagnosticCodes.PatternArguments.Code, code)
    Assert.Equal("#DivisibleBy", text)
    Assert.Contains("takes arguments", message)

[<Fact>]
let ``A pattern that takes no arguments, given some, is an error`` () =
    let code, message, text = armWith "#Even(2)(x)"
    Assert.Equal(DiagnosticCodes.PatternArguments.Code, code)
    Assert.Equal("2", text)
    Assert.Contains("takes no arguments", message)

[<Fact>]
let ``A pattern's arguments are values, not patterns`` () =
    let code, _, text = armWith "#DivisibleBy(_)"
    Assert.Equal(DiagnosticCodes.PatternArguments.Code, code)
    Assert.Equal("_", text)

[<Fact>]
let ``A pattern given arguments of the wrong type, or too few, is a type mismatch at them`` () =
    let code, _, text = armWith "#DivisibleBy(\"three\")"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("\"three\"", text)
    let source = "pattern (int, int) -> int -> #Between | _ = { low, high -> { n -> n >= low and n <= high } }\ndef f = { (n: int) ->\n  match n\n    | #Between(1) -> 1\n    | _ -> 2\n}\n"
    let code, _, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("1", text)
/// The texts the unknown-pattern errors of compiling `source` are at.
let private unknownPatternsIn (source: string) =
    let source = source.ReplaceLineEndings "\n"
    (Compiler.compile source).Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.UnknownPattern.Code)
    |> List.map (spanOf >> textAt source)

[<Fact>]
let ``A pattern declared in a block isn't in scope before its declaration`` () =
    let source =
        """
def f = { x: int ->
  def early =
    match x
      | #Big -> "big"
      | _ -> "small"
  pattern int -> #Big | _ = { n -> if n > 10 then #Big else _ }
  def late =
    match x
      | #Big -> "big"
      | _ -> "small"
  early ++ late
}
"""
    Assert.Equal<string list>([ "#Big" ], unknownPatternsIn source)

[<Fact>]
let ``A pattern declared in a block isn't in scope outside it`` () =
    let source =
        """
def f = { x: int ->
  pattern int -> #Big | _ = { n -> if n > 10 then #Big else _ }
  x
}

def g = { x: int ->
  match x
    | #Big -> "big"
    | _ -> "small"
}
"""
    Assert.Equal<string list>([ "#Big" ], unknownPatternsIn source)

[<Fact>]
let ``A pattern declared in a block hides an outer one that returns its tags`` () =
    let source =
        $"""
{evenOdd}

def f = {{ x: int ->
  pattern int -> #Even | #Odd = {{ n -> if n > 0 then #Even else #Odd }}
  match x
    | #Even -> 1
    | #Odd -> 0
}}
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.Empty(result.Diagnostics |> List.filter Diagnostics.isError)

/// A module block that declares a pattern, `export pattern` if `exported`, and a use of its `#Pos` outside the block.
let private blockPatternUsedOutside (exported: bool) =
    $"""
module shapes =
  {(if exported then "export " else "")}pattern int -> #Neg | #Zero | #Pos = {{ n -> if n < 0 then #Neg else if n == 0 then #Zero else #Pos }}

import shapes

def described = match 5
  | #Pos -> "positive"
  | _ -> "other"
"""
        .ReplaceLineEndings "\n"

[<Fact>]
let ``A module block's unexported pattern used outside the block is an error at its tag, naming the block`` () =
    let source = blockPatternUsedOutside false
    let result = Compiler.compile source
    let unknown = Assert.Single(result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.UnknownPattern.Code))
    Assert.Equal("#Pos", textAt source (spanOf unknown))
    Assert.Equal("`#Pos` is a pattern of module block 'shapes', which doesn't export it: write `export pattern` there to use it here", unknown.Message)
    Assert.Equal<string list>([ "int -> #Neg | #Zero | #Pos" ], unknown.Related |> List.map (fst >> textAt source))
    Assert.DoesNotContain(DiagnosticCodes.NotExported.Code, codesOf result)

[<Fact>]
let ``A module block's exported pattern is in scope outside the block`` () =
    let result = Compiler.compile (blockPatternUsedOutside true)
    Assert.Empty(result.Diagnostics |> List.filter Diagnostics.isError)

[<Fact>]
let ``A module block's unexported pattern is in scope within the block`` () =
    let source =
        """
module shapes =
  pattern int -> #Neg | #Pos = { n -> if n < 0 then #Neg else #Pos }

  export def sign = { n: int ->
    match n
      | #Neg -> "negative"
      | #Pos -> "positive"
  }
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.Empty(result.Diagnostics |> List.filter Diagnostics.isError)

/// Patterns that take arguments, `#DivisibleBy` with no payload and `#SplitOn` with one, and a def `f` whose match uses
/// `arm`.
let private withArguments (arm: string) =
    normalized (
        """pattern int -> int -> #DivisibleBy | _ = { divisor -> { n -> n % divisor == 0 } }

pattern string -> string -> #SplitOn(string, string) | _ = { separator -> { s -> #SplitOn(s, s) } }

def comma = { () -> "," }

def f = { (n: int), (s: string) ->
  match n
    | ARM -> 1
    | _ -> 2
}
"""
            .Replace("ARM", arm))

[<Fact>]
let ``A pattern's argument that is a call is NYX0264 at it, with or without a payload group`` () =
    // An argument is a literal, a name, or a tuple, list or tag of those, in both forms (#2064).
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.PatternArguments.Code, "n + 1" ],
        reportedAt (withArguments "#DivisibleBy(n + 1)"))
    let source = withArguments "#DivisibleBy(2)"
    let withPayload = source.Replace("match n\n    | #DivisibleBy(2) -> 1", "match s\n    | #SplitOn(comma())(a, b) -> 1")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.PatternArguments.Code, "comma()" ], reportedAt withPayload)
    compileClean (withArguments "#DivisibleBy(-3)") |> ignore

[<Fact>]
let ``The fix for a pattern's argument that is a call binds it with a def before the match`` () =
    let source = withArguments "#DivisibleBy(n + 1)"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    let fix = Assert.Single(diagnostic.Fixes)
    Assert.Equal("Bind it with `def divisibleByArgument`", fix.Title)
    let fixedSource = applyFix source fix
    Assert.Contains("  def divisibleByArgument = n + 1\n  match n\n    | #DivisibleBy(divisibleByArgument) -> 1", fixedSource)
    compileClean fixedSource |> ignore
