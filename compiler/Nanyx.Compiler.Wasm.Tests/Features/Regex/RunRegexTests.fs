module TranspilerWasmTests.Features.Regex.RunRegex

open Xunit
open TranspilerWasmTestHelpers

// The shipped package `nanyx.regex` (#2032): `Regex.parse`, RE2's syntax, a Pike VM that takes linear time, the
// functions that find, replace and split, and the patterns `#Matches` and `#MatchesRegex` (#2034), run as a script that
// names it in a `::dependency` line.

/// What running the script `body` printed, after the lines that make `nanyx.regex` its dependency and import it, and
/// three helpers: `re`, the regex of a pattern that is right; `show`, which prints the first match of a pattern in a
/// text, with where it starts and ends; and `bad`, which prints the column and the message of a pattern's error.
let private runRegex (body: string) =
    let source =
        """::dependency nanyx.regex

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
let ``Run - characters, escapes, dot and the classes \d \w \s and their negations each match what they stand for`` () =
    runRegex
        """show('a.c', "xabc")
show('a.c', "a\nc")
show('\.\*\+', "a.*+")
println("x\t\n" \Regex.find(re('\t\n')) \mapSome { m -> "{m.start}-{m.end}" })
show('\x41\x{263A}', "A☺")
show('\d+', "ab 123")
show('\D+', "12ab3")
show('\w+', "  foo_1 ")
show('\W+', "ab, cd")
println("a \t b" \Regex.find(re('\s+')) \mapSome { m -> "{m.start}-{m.end}" })
show('\S+', "  ab ")
"""
    |> expect
        """abc 1-4
none
.*+ 1-4
#some(1-3)
A☺ 0-2
123 3-6
ab 2-4
foo_1 2-7
,  2-4
#some(1-4)
ab 2-4
"""

[<Fact>]
let ``Run - a class matches its characters and ranges, negated with ^, with ] first and - last as characters`` () =
    runRegex
        """show('[a-c]+', "xxbcaz")
show('[^a-c]+', "abxyzc")
show('[]a]+', "x]a]")
show('[a-]+', "x-a-")
show('[\d.]+', "v1.25!")
show('[[:alpha:]]+', "12abC3")
show('[[:digit:][:space:]]+', "ab1 2c")
"""
    |> expect
        """bca 2-5
xyz 2-5
]a] 1-4
-a- 1-4
1.25 1-5
abC 2-5
1 2 2-5
"""

[<Fact>]
let ``Run - the anchors ^ $ \A \z, and \b and \B at a word's edge`` () =
    runRegex
        """show('^abc', "abc")
show('^abc', "xabc")
show('abc$', "xabc")
show('abc$', "abc\n")
show('\Aab', "ab")
show('ab\z', "ab")
show('\bfoo\b', "a foo b")
show('\bfoo\b', "afoob")
show('\Boo\B', "afoob")
"""
    |> expect
        """abc 0-3
none
abc 1-4
none
ab 0-2
ab 0-2
foo 2-5
none
oo 2-4
"""

[<Fact>]
let ``Run - alternatives take the first that matches, and non-capturing groups repeat`` () =
    runRegex
        """show('cat|dog', "hotdog")
show('a|ab', "xab")
show('ab|a', "xab")
show('(?:ab)+', "xababab")
show('x(?:a|bc)*y', "xabcay")
"""
    |> expect
        """dog 3-6
a 1-2
ab 1-3
ababab 1-7
xabcay 0-6
"""

[<Fact>]
let ``Run - quantifiers are greedy, or lazy with a ? after them`` () =
    runRegex
        """show('a*', "aaa")
show('a*?', "aaa")
show('a+', "baaa")
show('a+?', "baaa")
show('a?b', "ab")
show('a??b', "ab")
show('<.*>', "<a><b>")
show('<.*?>', "<a><b>")
"""
    |> expect
        """aaa 0-3
 0-0
aaa 1-4
a 1-2
ab 0-2
ab 0-2
<a><b> 0-6
<a> 0-3
"""

[<Fact>]
let ``Run - counted quantifiers repeat exactly, at least, or between, and a { that starts none is a character`` () =
    runRegex
        """show('a{2}', "aaaa")
show('a{2,}', "aaaaa")
show('a{2,3}', "aaaaa")
show('a{2,3}?', "aaaaa")
show('(ab){0,2}c', "abababc")
show('a{,3}', 'a{,3}')
show('x{', 'x{')
"""
    |> expect
        """aa 0-2
aaaaa 0-5
aaa 0-3
aa 0-2
ababc 2-7
a{,3} 0-5
x{ 0-2
"""

[<Fact>]
let ``Run - the flags i, m and s, for the rest of a group, turned off, or for a group of their own`` () =
    runRegex
        """show('(?i)hello', "HeLLo")
show('(?i)[a-c]+', "xABCa")
show('(?i)é', "É")
show('(?i)σ', "Σ")
show('(?i:a)b', "Ab")
show('(?i:a)b', "AB")
show('(?i)a(?-i)b', "AB")
show('(?m)^b', "a\nb")
show('(?m)a$', "a\nb")
show('(?s)a.c', "a\nc")
show('(?is)A.C', "a\nc")
"""
    |> expect
        """HeLLo 0-5
ABCa 1-5
É 0-1
Σ 0-1
Ab 0-2
none
none
b 2-3
a 0-1
a
c 0-3
a
c 0-3
"""

[<Fact>]
let ``Run - groups capture by number and by name, and a group that took no part is none`` () =
    runRegex
        """match "x 10-20 y" \Regex.find(re('(\d+)-(?<high>\d+)'))
  | #some(m) -> println("{m \group(0)} {m \group(1)} {m \group(2)} {m \named("high")} {m \group(3)} {m \named("low")}")
  | #notFound -> println("none")

match "b" \Regex.find(re('(a)|(b)'))
  | #some(m) -> println("{m \group(1)} {m \group(2)}")
  | #notFound -> println("none")

match "abcd" \Regex.find(re('(a|ab)(c|bcd)(d*)'))
  | #some(m) -> println("{m \group(1)} {m \group(2)} {m \group(3)}")
  | #notFound -> println("none")

match "2024" \Regex.find(re('(?P<year>\d{4})'))
  | #some(m) -> println("{m \named("year")}")
  | #notFound -> println("none")
"""
    |> expect
        """#some(10-20) #some(10) #some(20) #some(20) #none #none
#none #some(b)
#some(a) #some(bcd) #some()
#some(2024)
"""

[<Fact>]
let ``Run - text is matched by code point, and start and end count code points`` () =
    runRegex
        """show('é.', "café!")
show('😀+', "x😀😀y")
show('.', "😀")
show('[à-ÿ]+', "voilà")
show('\x{1F600}', "a😀")
"""
    |> expectLines
        [ "é! 3-5"
          "😀😀 1-3"
          "😀 0-1"
          "à 4-5"
          "😀 1-2" ]

[<Fact>]
let ``Run - a pattern that is wrong is an error that says where and why`` () =
    runRegex
        """bad('(a')
bad('a)')
bad('[a')
bad('[z-a]')
bad('[\d-z]')
bad('*a')
bad('a**')
bad('a{1001}')
bad('a{3,2}')
bad("a\\")
bad('\q')
bad('\x4')
bad('[\b]')
bad('(?x)')
bad('[[:nope:]]')
bad('(?<>a)')
bad('(?<year>\d+)(?<year>x)')
"""
    |> expectLines
        [ "1: There is no ')' to close this '('"
          "2: There is no '(' for this ')' to close"
          "1: There is no ']' to close this '['"
          "3: The range 'z-a' goes backwards"
          "4: A range in a class goes from one character to another"
          "1: There is nothing before '*' to repeat"
          "3: A quantifier can't follow another quantifier"
          "2: A count can be at most 1000"
          "2: The least count, 3, is more than the most, 2"
          "2: The pattern ends with a '\\' that escapes nothing"
          "1: '\\q' is not an escape"
          "1: '\\x' needs two hexadecimal digits, or some in braces"
          "2: '\\b' matches a place, not a character, so it can't be in a class"
          "3: 'x' is not a flag: the flags are 'i', 'm' and 's'"
          "2: 'nope' is not a POSIX class"
          "1: A group's name is letters, digits and '_', between '<' and '>'"
          "13: Two groups are named 'year'" ]

[<Fact>]
let ``Run - backreferences, lookaround and Unicode classes are errors that say they are not supported`` () =
    runRegex
        """bad('(a)\1')
bad('(?<n>a)\k<n>')
bad('(?P<n>a)(?P=n)')
bad('a(?=b)')
bad('a(?!b)')
bad('(?<=a)b')
bad('(?<!a)b')
bad('\pL')
"""
    |> expectLines
        [ "4: Backreferences such as '\\1' aren't supported: matching takes linear time"
          "8: Backreferences such as '\\k' aren't supported: matching takes linear time"
          "9: Backreferences such as '(?P=' aren't supported: matching takes linear time"
          "2: Lookaround such as '(?=' isn't supported: matching takes linear time"
          "2: Lookaround such as '(?!' isn't supported: matching takes linear time"
          "1: Lookaround such as '(?<=' isn't supported: matching takes linear time"
          "1: Lookaround such as '(?<!' isn't supported: matching takes linear time"
          "1: Unicode classes such as '\\p' aren't supported" ]

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
let ``Run - a match on #Matches is exhaustive with an arm for everything else, and pattern arms mix with literals`` () =
    runRegex
        """def kind: string -> string = {
  | "" -> "empty"
  | #Matches('^\d+$') -> "number"
  | #Matches('^\w+$')(m) -> "word of {string.length(m.text)}"
  | _ -> "other"
}

for ["", "42", "abc", "a b"] | text -> println(kind(text))
"""
    |> expect
        """empty
number
word of 3
other
"""

[<Fact>]
let ``Run - #Matches given a pattern that is wrong crashes with what Regex.parse says of it`` () =
    let outcome =
        runRegex
            """match "x"
  | #Matches('(\d+') -> println("matched")
  | _ -> println("no match")
"""
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.StartsWith("crash: The regex '(\\d+' is wrong at column 1: There is no ')' to close this '('\n", outcome.StandardError)

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
