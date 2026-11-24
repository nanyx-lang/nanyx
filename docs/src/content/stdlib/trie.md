---
title: trie
description: Maps whose keys are sequences, with prefix queries
order: 10.83
---

Import it as `nanyx/trie`. A `Trie` maps keys that are lists to values, and also finds the keys that start with a prefix, and the longest key that is a prefix of another: what autocompletion and routing tables need. A `StringTrie` does the same with string keys.

```nanyx
import nanyx/trie

def routes = StringTrie["/" => "home", "/users" => "users", "/users/admin" => "admin"]

println("{StringTrie.longestPrefixOf(routes, "/users/42")}") -- #some(/users, users)
println("{StringTrie.withPrefix(routes, "/users")}") -- [(/users, users), (/users/admin, admin)]
```
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Tries: maps whose keys are sequences, which also answer questions about prefixes.

A `Trie(k, v)` maps keys that are lists, `list(k)`, to values, as a map does, and also finds every
key that starts with a prefix ([`Trie.withPrefix`](#triewithprefix)), whether any does ([`Trie.hasPrefix`](#triehasprefix)), and the
longest key that is a prefix of a given one ([`Trie.longestPrefixOf`](#trielongestprefixof)), as autocompletion and
routing tables need. Each node is an optional value and a map from the next element of a key to
the node below it, so finding a key takes time in proportion to its length, however many keys
there are. The elements need an `==` and a `hash`, as a map's keys do.

A [`StringTrie(v)`](#stringtrie) is the common case: its keys are strings, each read as its
characters.

Nothing about a trie changes once it is made: [`Trie.insert`](#trieinsert) and [`Trie.remove`](#trieremove) give back a new
trie, which shares the nodes it didn't change with the one it was made from. Removing a key
removes the nodes left with no value and nothing below them, so two tries holding the same keys
and values are `==`, whatever order the keys were added in. A trie has no `hash`, as a map has
none. [`Trie.toString`](#trietostring) writes one as its pairs, `Trie[[1, 2] => a]`.

A `Trie`'s keys come out in the order they were added in, those below one element before those
below the next, and each key before the keys it is a prefix of: its elements need no order. A
`StringTrie`'s come out sorted.

Each function is attached to `Trie` or `StringTrie`, so with `import nanyx/trie` it is called as
`Trie.insert(t, [1, 2], "a")`, or as `t \insert([1, 2], "a")`. A literal of `key => value`
entries makes one: `Trie[[1] => "a", [1, 2] => "b"]`, or `StringTrie["a" => 1, "ab" => 2]`.

## `Trie`

```nanyx fragment
type Trie(k, v) = protected Node(k, v)
```

## `Trie.empty`

```nanyx fragment
def Trie.empty: Trie(k, v)
```

The trie with no keys.

## `Trie.insert`

```nanyx fragment
def Trie.insert: [Eq(k), Hash(k)] Trie(k, v), list(k), v -> Trie(k, v)
```

`trie` with `key` mapped to `value`, in place of any value it had.

## `Trie.remove`

```nanyx fragment
def Trie.remove: [Eq(k), Hash(k)] Trie(k, v), list(k) -> Trie(k, v)
```

`trie` without `key`, and without the nodes that leaves with no value and nothing below them.
The same trie when it has no `key`.

## `Trie.get`

```nanyx fragment
def Trie.get: [Eq(k), Hash(k)] Trie(k, v), list(k) -> #some(v) | #none
```

The value `trie` maps `key` to, or `#none` when it has no `key`.

## `Trie.containsKey`

```nanyx fragment
def Trie.containsKey: [Eq(k), Hash(k)] Trie(k, v), list(k) -> bool
```

Whether `trie` maps `key` to a value.

## `Trie.length`

```nanyx fragment
def Trie.length: Trie(k, v) -> int
```

How many keys `trie` holds.

## `Trie.isEmpty`

```nanyx fragment
def Trie.isEmpty: Trie(k, v) -> bool
```

Whether `trie` holds no keys.

## `Trie.toList`

```nanyx fragment
def Trie.toList: Trie(k, v) -> list((list(k), v))
```

Each key in `trie` and its value, in the order the keys were added in, as the module's
description says.

## `Trie.keys`

```nanyx fragment
def Trie.keys: Trie(k, v) -> list(list(k))
```

The keys in `trie`, in the order of [`Trie.toList`](#trietolist).

## `Trie.fromList`

```nanyx fragment
def Trie.fromList: [Eq(k), Hash(k)] list((list(k), v)) -> Trie(k, v)
```

A trie of the keys and values in `pairs`; a key that comes twice maps to its last value.

## `Trie.fromArray`

```nanyx fragment
def Trie.fromArray: [Eq(k), Hash(k)] array((list(k), v)) -> Trie(k, v)
```

A trie of the keys and values in `pairs`. What a literal builds a trie with,
`Trie[[1] => "a", [1, 2] => "b"]`.

## `Trie.withPrefix`

```nanyx fragment
def Trie.withPrefix: [Eq(k), Hash(k)] Trie(k, v), list(k) -> list((list(k), v))
```

Each key in `trie` that starts with `prefix`, `prefix` itself included, and its value, in the
order of [`Trie.toList`](#trietolist).

## `Trie.hasPrefix`

```nanyx fragment
def Trie.hasPrefix: [Eq(k), Hash(k)] Trie(k, v), list(k) -> bool
```

Whether any key in `trie` starts with `prefix`, or is `prefix`.

## `Trie.longestPrefixOf`

```nanyx fragment
def Trie.longestPrefixOf: [Eq(k), Hash(k)] Trie(k, v), list(k) -> #some(list(k), v) | #none
```

The longest key in `trie` that `key` starts with, `key` itself included, and its value, or
`#none` when no key in `trie` is a prefix of `key`. A router finds the most specific route for a
path with it.

## `Trie.toString`

```nanyx fragment
def Trie.toString: [ToString(k), ToString(v)] Trie(k, v) -> string
```

`trie` as its literal is written, its keys in the order of [`Trie.toList`](#trietolist):
`Trie[[1] => a, [1, 2] => b]`, and `Trie[=>]` when it is empty.

## `` Trie.`==` ``

```nanyx fragment
def Trie.`==`: [Eq(k), Eq(v)] Trie(k, v), Trie(k, v) -> bool
```

Whether `a` and `b` hold the same keys with the same values, whatever order the keys were added
in.

## `StringTrie`

```nanyx fragment
type StringTrie(v) = protected Trie(char, v)
```

A trie whose keys are strings, each read as its characters, as [`string.chars`](text#stringchars) gives them. Its
functions take and give strings, and give its keys sorted.

## `StringTrie.empty`

```nanyx fragment
def StringTrie.empty: StringTrie(v)
```

The string trie with no keys.

## `StringTrie.insert`

```nanyx fragment
def StringTrie.insert: StringTrie(v), string, v -> StringTrie(v)
```

`trie` with `key` mapped to `value`, in place of any value it had.

## `StringTrie.remove`

```nanyx fragment
def StringTrie.remove: StringTrie(v), string -> StringTrie(v)
```

`trie` without `key`, and without the nodes that leaves with no value and nothing below them.

## `StringTrie.get`

```nanyx fragment
def StringTrie.get: StringTrie(v), string -> #some(v) | #none
```

The value `trie` maps `key` to, or `#none` when it has no `key`.

## `StringTrie.containsKey`

```nanyx fragment
def StringTrie.containsKey: StringTrie(v), string -> bool
```

Whether `trie` maps `key` to a value.

## `StringTrie.length`

```nanyx fragment
def StringTrie.length: StringTrie(v) -> int
```

How many keys `trie` holds.

## `StringTrie.isEmpty`

```nanyx fragment
def StringTrie.isEmpty: StringTrie(v) -> bool
```

Whether `trie` holds no keys.

## `StringTrie.toList`

```nanyx fragment
def StringTrie.toList: StringTrie(v) -> list((string, v))
```

Each key in `trie` and its value, sorted by key.

## `StringTrie.keys`

```nanyx fragment
def StringTrie.keys: StringTrie(v) -> list(string)
```

The keys in `trie`, sorted.

## `StringTrie.fromList`

```nanyx fragment
def StringTrie.fromList: list((string, v)) -> StringTrie(v)
```

A string trie of the keys and values in `pairs`; a key that comes twice maps to its last value.

## `StringTrie.fromArray`

```nanyx fragment
def StringTrie.fromArray: array((string, v)) -> StringTrie(v)
```

A string trie of the keys and values in `pairs`. What a literal builds one with,
`StringTrie["a" => 1, "ab" => 2]`.

## `StringTrie.withPrefix`

```nanyx fragment
def StringTrie.withPrefix: StringTrie(v), string -> list((string, v))
```

Each key in `trie` that starts with `prefix`, `prefix` itself included, and its value, sorted
by key: `withPrefix(t, "ab")` gives `"ab"`, `"abc"` and `"abd"`, but not `"a"`.

## `StringTrie.hasPrefix`

```nanyx fragment
def StringTrie.hasPrefix: StringTrie(v), string -> bool
```

Whether any key in `trie` starts with `prefix`, or is `prefix`.

## `StringTrie.longestPrefixOf`

```nanyx fragment
def StringTrie.longestPrefixOf: StringTrie(???), string -> #some(string, ???) | #none
```

The longest key in `trie` that `key` starts with, `key` itself included, and its value, or
`#none` when no key in `trie` is a prefix of `key`.

## `StringTrie.toString`

```nanyx fragment
def StringTrie.toString: [ToString(v)] StringTrie(v) -> string
```

`trie` as its literal is written, its keys sorted: `StringTrie[a => 1, ab => 2]`, and
`StringTrie[=>]` when it is empty.

## `` StringTrie.`==` ``

```nanyx fragment
def StringTrie.`==`: [Eq(v)] StringTrie(v), StringTrie(v) -> bool
```

Whether `a` and `b` hold the same keys with the same values, whatever order the keys were added
in.

<!-- End of the generated reference. -->