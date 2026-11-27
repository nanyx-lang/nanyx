module TranspilerWasmTests.Features.StdLib.RunTrie

open Xunit
open TranspilerWasmTestHelpers

// #1642: `Trie(k, v)` and `StringTrie(v)` in `nanyx/trie`.

[<Fact>]
let ``Run - a trie maps list keys to values, and answers prefix queries`` () =
    runSource
        """
import nanyx/trie

def t: Trie(int, string) = [[1, 2, 3] => "c", [1] => "a", [1, 2] => "b"]

println("{Trie.get(t, [1, 2])} {Trie.get(t, [2])} {Trie.get(t, [])} {Trie.containsKey(t, [1, 2, 3])}")
println("{Trie.length(t)} {Trie.keys(t)}")
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
    runSource
        """
import nanyx/trie

def t: Trie(int, int) = Trie.empty

println("{Trie.get(t, [])} {Trie.length(t)} {Trie.isEmpty(t)} {Trie.keys(t)} {Trie.hasPrefix(t, [])}")
println("{Trie.longestPrefixOf(t, [1])} {Trie.withPrefix(t, [])}")
println(Trie.toString(t))
"""
    |> assertRan "#none 0 true [] false\n#none []\nTrie[=>]\n"

[<Fact>]
let ``Run - inserting a key again replaces its value, and removing one prunes what it leaves empty`` () =
    runSource
        """
import nanyx/trie

def t = Trie.fromList([([1, 2], "x"), ([1, 2], "y"), ([1], "a")])
def without = Trie.remove(t, [1, 2])

println("{Trie.get(t, [1, 2])} {Trie.length(t)}")
println("{Trie.keys(without)} {Trie.hasPrefix(without, [1, 2])} {Trie.length(without)}")
println("{Trie.remove(t, [7]) == t} {Trie.remove(without, [1]) == Trie.empty}")
"""
    |> assertRan "#some(y) 2\n[[1]] false 1\ntrue true\n"

[<Fact>]
let ``Run - an old trie reads the same after a new one is made from it`` () =
    runSource
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
    runSource
        """
import nanyx/trie

def a = Trie.fromList([([1, 2], "b"), ([3], "c"), ([1], "a")])
def b = Trie.fromList([([1], "a"), ([3], "c"), ([1, 2], "b"), ([4, 5], "d")]) \remove([4, 5])

println("{a == b} {a == Trie.insert(b, [1], "z")}")
"""
    |> assertRan "true false\n"

[<Fact>]
let ``Run - a trie's elements are compared and hashed with their own == and hash`` () =
    runSource
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
    runSource
        """
import nanyx/trie

def t = StringTrie["abc" => 3, "b" => 9, "a" => 1, "ab" => 2]

println("{StringTrie.get(t, "ab")} {StringTrie.get(t, "abd")} {StringTrie.containsKey(t, "b")} {StringTrie.length(t)}")
println("{StringTrie.keys(t)} {StringTrie.withPrefix(t, "ab")} {StringTrie.withPrefix(t, "c")}")
println("{StringTrie.hasPrefix(t, "ab")} {StringTrie.hasPrefix(t, "abcd")}")
println("{StringTrie.longestPrefixOf(t, "abd")} {StringTrie.longestPrefixOf(t, "abc")} {StringTrie.longestPrefixOf(t, "x")}")
println(StringTrie.toString(t))
"""
    |> assertRan
        "#some(2) #none true 4\n[a, ab, abc, b] [(ab, 2), (abc, 3)] []\ntrue false\n#some(ab, 2) #some(abc, 3) #none\nStringTrie[a => 1, ab => 2, abc => 3, b => 9]\n"

[<Fact>]
let ``Run - removing from a string trie leaves the old one as it was`` () =
    runSource
        """
import nanyx/trie

def t: StringTrie(int) = ["a" => 1, "ab" => 2, "abc" => 3]
def without = StringTrie.remove(t, "ab")

def empty: StringTrie(int) = StringTrie.fromList([])

println("{StringTrie.keys(t)} {StringTrie.keys(without)} {StringTrie.longestPrefixOf(without, "abz")}")
println("{StringTrie.isEmpty(empty)} {StringTrie.toString(empty)}")
"""
    |> assertRan "[a, ab, abc] [a, abc] #some(a, 1)\ntrue StringTrie[=>]\n"

[<Fact>]
let ``Run - tries with the same entries are ==, whatever order their keys were added in`` () =
    // A trie is a node with a map below it, which its own `==` compares by its entries, as a map is (#2522).
    runSource
        """
import nanyx/trie

println(Trie[[1] => "a", [2] => "b"] == Trie[[2] => "b", [1] => "a"])
println(Trie[[1, 2] => "a", [1, 3] => "b", [1] => "c"] == Trie[[1] => "c", [1, 3] => "b", [1, 2] => "a"])
println(Trie[[1] => "a", [2] => "b"] == Trie[[1] => "a", [2] => "c"])
println(Trie[[1] => "a", [2] => "b"] == Trie[[1] => "a", [3] => "b"])
println(Trie[[1] => "a", [2] => "b"] \Trie.remove([2]) == Trie[[1] => "a"])
println(StringTrie["a" => 1, "b" => 2, "ab" => 3] == StringTrie["ab" => 3, "b" => 2, "a" => 1])
println(StringTrie["a" => 1, "b" => 2] == StringTrie["a" => 1, "b" => 3])
println([StringTrie["a" => 1, "b" => 2]] == [StringTrie["b" => 2, "a" => 1]])
"""
    |> assertRan "true\ntrue\nfalse\nfalse\ntrue\ntrue\nfalse\ntrue\n"

[<Fact>]
let ``Run - a string trie reads keys of several bytes a character`` () =
    runSource
        """
import nanyx/trie

def t = StringTrie["héllo" => 1, "hé" => 2, "日本" => 3, "h" => 4]

println("{StringTrie.get(t, "héllo")} {StringTrie.get(t, "hé")} {StringTrie.get(t, "he")} {StringTrie.get(t, "日本")}")
println("{StringTrie.hasPrefix(t, "hél")} {StringTrie.hasPrefix(t, "日")} {StringTrie.hasPrefix(t, "本")}")
println("{StringTrie.longestPrefixOf(t, "hélp")} {StringTrie.longestPrefixOf(t, "日本語")} {StringTrie.longestPrefixOf(t, "日")}")
"""
    |> assertRan "#some(1) #some(2) #none #some(3)\ntrue true false\n#some(hé, 2) #some(日本, 3) #none\n"

let private lookingUp =
    normalized """import nanyx/text
import nanyx/trie

def key: int -> string = { n -> string.repeat("aé", n) }

def made: int -> StringTrie(int) = { n -> StringTrie[key(n) => n, "a" => 1, "b" => 2] }

def gets: int, StringTrie(int), string, int -> int = { times, t, k, acc ->
  if times == 0 then acc else gets(times - 1, t, k, acc + someOr(StringTrie.get(t, k), 0))
}

def prefixes: int, StringTrie(int), string, int -> int = { times, t, k, acc ->
  if times == 0
    then acc
    else prefixes(times - 1, t, k, if StringTrie.hasPrefix(t, k) then acc + 1 else acc)
}

def longest: int, StringTrie(int), string, int -> int = { times, t, k, acc ->
  if times == 0
    then acc
    else match StringTrie.longestPrefixOf(t, k)
      | #some(p, _) -> longest(times - 1, t, k, acc + string.length(p))
      | #none -> longest(times - 1, t, k, acc)
}

def built: int -> int = { n -> StringTrie.length(made(n)) + string.length(key(n)) }

def got: int -> int = { n -> gets(100, made(n), key(n), 0) }

def checked: int -> int = { n -> prefixes(100, made(n), key(n), 0) }

def matched: int -> int = { n -> longest(100, made(n), key(n) ++ "z", 0) }
"""

[<Fact>]
let ``A string trie's lookups allocate nothing for the key's characters`` () =
    // #2211: a lookup made a list of the key's characters first, with `string.chars`, before it walked the trie. It now
    // reads the key by position, so all it allocates is each level's `map.get` result, one a character, and its own
    // result: a `get` of a key of 2 characters made 4 blocks, and makes 2.
    use program = instantiateWasm lookingUp []
    let perLookup (name: string) (n: int) =
        let made, _ = allocationsOf program "built" n
        let looked, result = allocationsOf program name n
        (looked - made) / 100, result
    for n in [ 1; 50 ] do
        let characters = 2 * n
        let keyLength = 3 * n
        for name, expected, own in [ "got", 100 * n, 1; "checked", 100, 1; "matched", 100 * keyLength, 6 ] do
            let blocks, result = perLookup name n
            Assert.Equal(expected, result)
            Assert.InRange(blocks, 0, characters + own)
