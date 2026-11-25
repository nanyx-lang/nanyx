module TranspilerWasmTests.Features.Regex.RunRegex

open Xunit
open TranspilerWasmTestHelpers

// `nanyx.regex` as a large real program for the backend: a pattern reader that goes 1000 groups deep in one stack
// (#2584), a Pike VM that marks its threads in an `MArray` per code point and takes linear time, programs of up to
// 100000 instructions from nested repeats (#2593), a lazy sequence of matches, and the custom patterns `#Matches` and
// `#MatchesRegex` (#2034) from a dependency. These tests have caught backend bugs; the package's own behaviour is
// tested in its repository.
//
// The package is not shipped with the compiler: it is published to the index from nanyx-lang/regex (#2631). Its
// sources here, under `nanyx.regex/`, are a copy, test data only, of packages/nanyx.regex as it was on this
// repository's main when it moved (nanyx.regex 0.1.0), so that these tests download nothing, with its halvings written
// `//` since `/` divides with a fraction (#2716). They are not kept in step with the package's releases.

/// The directory of the copy of `nanyx.regex` the scripts depend on by its path.
let private regexPackage = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "nanyx.regex")

/// What running the script `body` printed, after the lines that make the copy of `nanyx.regex` its dependency and
/// import it, and three helpers: `re`, the regex of a pattern that is right; `show`, which prints the first match of a
/// pattern in a text, with where it starts and ends; and `bad`, which prints the column and the message of a pattern's
/// error.
let private runRegex (body: string) =
    let source =
        $"::dependency nanyx.regex {regexPackage}\n" + """
import nanyx.regex/regex

def re = { pattern ->
  match Regex.parse(pattern)
    | #some(r) -> r
    | #invalidPattern(e) -> crash("{pattern}: {e.message}")
}

def show = { pattern, text ->
  match text \Regex.find(re(pattern))
    | #some(m) -> println("{m.text} {m.start}-{m.end}")
    | #notFound -> println("none")
}

def bad = { pattern ->
  match Regex.parse(pattern)
    | #some(_) -> println("ok")
    | #invalidPattern(e) -> println("{e.column}: {e.message}")
}

"""
    runSource ((source + body).ReplaceLineEndings "\n")

let private expect (expected: string) = assertRan (expected.ReplaceLineEndings "\n")

/// The lines a script printed, for output that isn't Nanyx, which a triple-quoted string would put in the parser's
/// corpus.
let private expectLines (lines: string list) = assertRan (lines |> List.map (fun line -> line + "\n") |> String.concat "")

[<Fact>]
let ``Run - isMatch, findAll, replace, replaceWith and split, each taking the text first`` () =
    runRegex
        """def digits = re('[0-9]+')

println("abc123" \Regex.isMatch(digits))
println("abc" \Regex.isMatch(digits))
println("a1b22" \Regex.findAll(digits) \seq.map { m -> m.text } \seq.toList)
println("baaac" \Regex.findAll(re('a*')) \seq.map { m -> "{m.start}-{m.end}" } \seq.toList)
println("a1b22" \Regex.replace(digits, with = "#"))
println("2024-11-17" \Regex.replace(re('(\d+)-(\d+)-(?<day>\d+)'), with = '${day}/$2/$1 $$ $0 [$9]'))
println("a1b22" \Regex.replaceWith(re('\d'), { m -> "<{m.text}>" }))
println("a,b;c" \Regex.split(re('[,;]')))
println("abc" \Regex.split(re('')))
println(",a," \Regex.split(re(',')) \list.map { piece -> "'{piece}'" })
println(digits)
"""
    |> expect
        """true
false
[1, 22]
[0-0, 1-4, 5-5]
a#b#
17/11/2024 $ 2024-11-17 []
a<1>b<2><2>
[a, b, c]
[a, b, c]
['', 'a', '']
[0-9]+
"""

[<Fact>]
let ``Run - findAll finds each match only when it is pulled, so a long text cut with seq.take is quick`` () =
    runRegex
        """def text = string.repeat("ab ", 200000)
println(text \Regex.findAll(re('a(b)')) \seq.take(3) \seq.map { m -> m.start } \seq.toList)
"""
    |> expect "[0, 3, 6]\n"

[<Fact>]
let ``Run - a pattern that takes a backtracking engine exponential time takes linear time`` () =
    runRegex
        """def text = string.repeat("a", 10000)
println(text \Regex.isMatch(re('(a*)*b')))
println(text \Regex.isMatch(re('(a|aa)+$')))
"""
    |> expect "false\ntrue\n"

[<Fact>]
let ``Run - #Matches matches a string against a literal pattern in a match arm, with and without the match`` () =
    runRegex
        """def classify = { (line: string) ->
  match line
    | #Matches('^\s*$') -> "blank"
    | #Matches('^(\d+)-(\d+)$')(m) -> "range {m \group(1) \someOr("")} to {m \group(2) \someOr("")}"
    | #Matches('(?i)^todo:\s*(?<task>.*)$')(m) -> "task {m \named("task") \someOr("")}"
    | _ -> "other"
}

println(classify("   "))
println(classify("10-20"))
println(classify("TODO: write tests"))
println(classify("10-"))
println(if "abc" is #Matches('b') then "has a b" else "no b")
"""
    |> expect
        """blank
range 10 to 20
task write tests
other
has a b
"""

[<Fact>]
let ``Run - #MatchesRegex matches a string against a regex made at run time, with and without the match`` () =
    runRegex
        """def words = ["cat", "dog"]
def animal = re(string.join(words, "|"))
def digit = re('\d')

def describe = { (text: string) ->
  match text
    | #MatchesRegex(animal)(m) -> "an animal at {m.start}: {m.text}"
    | #MatchesRegex(digit) -> "a digit"
    | _ -> "nothing"
}

println(describe("hotdog"))
println(describe("r2d2"))
println(describe("x"))
"""
    |> expect
        """an animal at 3: dog
a digit
nothing
"""

[<Fact>]
let ``Run - groups nest 1000 deep, and a group past that is an error at its '(', however deep the pattern goes`` () =
    // A pattern taken from input can't run the program out of stack (#2584): reading a group takes no stack, and
    // compiling and matching the deepest pattern allowed fit in it.
    runRegex
        """def nested = { n, open, inner, close -> string.repeat(open, n) ++ inner ++ string.repeat(close, n) }

for [nested(1000, "(", "a", ")"), nested(1000, "(?:", "a*", ")*"), nested(1000, "(a|", "b", ")")] | pattern ->
  println("xaab" \Regex.find(re(pattern)) \mapSome { m -> "{m.start}-{m.end}" })
bad(nested(1001, "(", "a", ")"))
bad("a|(?i:b" ++ nested(1000, "(", "c", ")") ++ ")")
bad(nested(20000, "(", "a", ")"))
bad(nested(999, "(", "a", ")") ++ ")")
bad(nested(1000, "(", "a", "") ++ string.repeat(")", 999))
"""
    |> expectLines
        [ "#some(1-2)"
          "#some(0-0)"
          "#some(1-2)"
          "1001: Groups can be nested at most 1000 deep"
          "1007: Groups can be nested at most 1000 deep"
          "1001: Groups can be nested at most 1000 deep"
          "2000: There is no '(' for this ')' to close"
          "1: There is no ')' to close this '('" ]

[<Fact>]
let ``Run - nested + and * are loops, so their program grows by one copy at each level and they compile and match`` () =
    // A `+` is its copy and a split back, and a `*` a split, its copy and a jump: a program no larger however they
    // nest (#2593). The size a pattern compiles to shows past the cap, whose error says it.
    runRegex
        """def nested = { n, open, inner, close -> string.repeat(open, n) ++ inner ++ string.repeat(close, n) }

def body = string.repeat("a", 100000)

for [25, 50] | n ->
  bad(nested(n, "(?:", body, ")+"))
  bad(nested(n, "(?:", body, ")*"))
for [nested(25, "(?:", "a", ")+"), nested(70, "(?:", "a+", ")*"), nested(1000, "(?:a|", "b", ")+")] | pattern ->
  println("xaab" \Regex.find(re(pattern)) \mapSome { m -> "{m.start}-{m.end}" })
show('a+?', "aaa")
show('(a+)+b', "aaab")
show('x(a|ab)+y', "xababay")
"""
    |> expectLines
        [ "1: The pattern compiles to 100025 instructions, more than the 100000 a pattern may"
          "1: The pattern compiles to 100050 instructions, more than the 100000 a pattern may"
          "1: The pattern compiles to 100050 instructions, more than the 100000 a pattern may"
          "1: The pattern compiles to 100100 instructions, more than the 100000 a pattern may"
          "#some(1-3)"
          "#some(0-0)"
          "#some(1-4)"
          "a 0-1"
          "aaab 0-4"
          "xababay 0-7" ]

[<Fact>]
let ``Run - a pattern compiles to at most 100000 instructions, and one past is an error`` () =
    runRegex
        """println(re(string.repeat("a", 100000)) \Regex.toString \string.length)
bad(string.repeat("a", 100001))
println(re("(?:" ++ string.repeat("a", 100) ++ '){1000}') \Regex.toString \string.length)
bad("(?:" ++ string.repeat("a", 101) ++ '){1000}')
"""
    |> expectLines
        [ "100000"
          "1: The pattern compiles to 100001 instructions, more than the 100000 a pattern may"
          "110"
          "1: The pattern compiles to 101000 instructions, more than the 100000 a pattern may" ]
