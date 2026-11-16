module TranspilerWasmTests.Features.StdLib.RunTrie

open Xunit
open TranspilerWasmTestHelpers

// #1642: `Trie(k, v)` and `StringTrie(v)` in `nanyx/trie`.

let private run (source: string) = runSource (source.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - a trie maps list keys to values, and answers prefix queries`` () =
    run
        """
import nanyx/trie

def t: Trie(int, string) = [[1, 2, 3] => "c", [1] => "a", [1, 2] => "b"]

println("{Trie.get(t, [1, 2])} {Trie.get(t, [2])} {Trie.get(t, [])} {Trie.containsKey(t, [1, 2, 3])}")
println("{Trie.count(t)} {Trie.keys(t)}")
println("{Trie.withPrefix(t, [1, 2])} {Trie.withPrefix(t, [3])}")
println("{Trie.hasPrefix(t, [1, 2])} {Trie.hasPrefix(t, [2])} {Trie.hasPrefix(t, [])}")
println("{Trie.longestPrefixOf(t, [1, 2, 5])} {Trie.longestPrefixOf(t, [1, 2, 3, 4])}")
println("{Trie.longestPrefixOf(t, [1])} {Trie.longestPrefixOf(t, [5, 1])}")
println(Trie.toString(t))
"""
    |> assertRan
        "#some(b) #none #none true\n3 [[1], [1, 2], [1, 2, 3]]\n[([1, 2], b), ([1, 2, 3], c)] []\ntrue false true\n#some([1, 2], b) #some([1, 2, 3], c)\n#some([1], a) #none\nTrie[[1] => a, [1, 2] => b, [1, 2, 3] => c]\n"

[<Fact>]
let ``Run - the empty trie has no keys, and no prefix of any`` () =
    run
        """
import nanyx/trie

def t: Trie(int, int) = Trie.empty

println("{Trie.get(t, [])} {Trie.count(t)} {Trie.isEmpty(t)} {Trie.keys(t)} {Trie.hasPrefix(t, [])}")
println("{Trie.longestPrefixOf(t, [1])} {Trie.withPrefix(t, [])}")
println(Trie.toString(t))
"""
    |> assertRan "#none 0 true [] false\n#none []\nTrie[=>]\n"

[<Fact>]
let ``Run - inserting a key again replaces its value, and removing one prunes what it leaves empty`` () =
    run
        """
import nanyx/trie

def t = Trie.fromList([([1, 2], "x"), ([1, 2], "y"), ([1], "a")])
def without = Trie.remove(t, [1, 2])

println("{Trie.get(t, [1, 2])} {Trie.count(t)}")
println("{Trie.keys(without)} {Trie.hasPrefix(without, [1, 2])} {Trie.count(without)}")
println("{Trie.remove(t, [7]) == t} {Trie.remove(without, [1]) == Trie.empty}")
"""
    |> assertRan "#some(y) 2\n[[1]] false 1\ntrue true\n"

[<Fact>]
let ``Run - an old trie reads the same after a new one is made from it`` () =
    run
        """
import nanyx/trie

def t: Trie(int, string) = [[1] => "a"]
def more = Trie.insert(t, [1, 2], "b")
def less = Trie.remove(more, [1])

println("{Trie.toList(t)} {Trie.toList(more)} {Trie.toList(less)}")
"""
    |> assertRan "[([1], a)] [([1], a), ([1, 2], b)] [([1, 2], b)]\n"

[<Fact>]
let ``Run - tries holding the same keys are equal, whatever order they were added in`` () =
    run
        """
import nanyx/trie

def a = Trie.fromList([([1, 2], "b"), ([3], "c"), ([1], "a")])
def b = Trie.fromList([([1], "a"), ([3], "c"), ([1, 2], "b"), ([4, 5], "d")]) \remove([4, 5])

println("{a == b} {a == Trie.insert(b, [1], "z")}")
"""
    |> assertRan "true false\n"

[<Fact>]
let ``Run - a trie's elements are compared and hashed with their own == and hash`` () =
    run
        """
import nanyx/text
import nanyx/trie

type Code = protected string

def Code.`==`: Code, Code -> bool = { Code(a), Code(b) -> string.lowercase(a) == string.lowercase(b) }
def Code.hash: Code -> int = { Code(a) -> string.hash(string.lowercase(a)) }

def t = Trie.fromList([([Code("GB")], "UK"), ([Code("GB"), Code("LON")], "London")])

def region =
  match Trie.longestPrefixOf(t, [Code("gb"), Code("Man")])
    | #some(key, name) -> "{list.length(key)} {name}"
    | #none -> "none"

println("{Trie.get(t, [Code("gb"), Code("lon")])} {region}")
"""
    |> assertRan "#some(London) 1 UK\n"

[<Fact>]
let ``Run - a string trie takes and gives strings, its keys sorted`` () =
    run
        """
import nanyx/trie

def t = StringTrie["abc" => 3, "b" => 9, "a" => 1, "ab" => 2]

println("{StringTrie.get(t, "ab")} {StringTrie.get(t, "abd")} {StringTrie.containsKey(t, "b")} {StringTrie.count(t)}")
println("{StringTrie.keys(t)} {StringTrie.withPrefix(t, "ab")} {StringTrie.withPrefix(t, "c")}")
println("{StringTrie.hasPrefix(t, "ab")} {StringTrie.hasPrefix(t, "abcd")}")
println("{StringTrie.longestPrefixOf(t, "abd")} {StringTrie.longestPrefixOf(t, "abc")} {StringTrie.longestPrefixOf(t, "x")}")
println(StringTrie.toString(t))
"""
    |> assertRan
        "#some(2) #none true 4\n[a, ab, abc, b] [(ab, 2), (abc, 3)] []\ntrue false\n#some(ab, 2) #some(abc, 3) #none\nStringTrie[a => 1, ab => 2, abc => 3, b => 9]\n"

[<Fact>]
let ``Run - removing from a string trie leaves the old one as it was`` () =
    run
        """
import nanyx/trie

def t: StringTrie(int) = ["a" => 1, "ab" => 2, "abc" => 3]
def without = StringTrie.remove(t, "ab")

def empty: StringTrie(int) = StringTrie.fromList([])

println("{StringTrie.keys(t)} {StringTrie.keys(without)} {StringTrie.longestPrefixOf(without, "abz")}")
println("{StringTrie.isEmpty(empty)} {StringTrie.toString(empty)}")
"""
    |> assertRan "[a, ab, abc] [a, abc] #some(a, 1)\ntrue StringTrie[=>]\n"
