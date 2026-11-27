---
title: "Custom patterns"
description: "Reusable pattern-matching logic with the pattern keyword"
order: 13
---

The `pattern` keyword allows for the encapsulation of matching logic in reusable components.

A custom pattern is a function with a signature but no name: it is identified by the tags it returns. Its tags are
**capitalised** (`#Even`, `#Hsl`), which is what tells them apart from ordinary tags: a capitalised tag is always a
custom pattern, and one that names no pattern in scope is an error ([`NYX0118`](../standard-tools/errors#nyx0118)), with a
fix that writes it in lower case.

The function body contains the logic for matching and extracting values, and returns one of the tags in the pattern's
signature. As with tags elsewhere in the language, they can carry payloads, which are then bound in the match arm.

# Where a pattern is declared

A pattern is declared wherever a `def` can be: at the top level of a file, in a module block, or in a function's
block. One in a function's block is local to it, as a local def is: it is in scope from its declaration to the end of
the block, so the block's matches, and the lambdas defined after it, can use its tags. It can use what a local def can,
such as the function's parameters and a type the block declares.

```nanyx
def classify = { input ->
  pattern string -> #Empty | #Short | #Long = { s ->
    if s == "" then #Empty else if string.length(s) < 5 then #Short else #Long
  }

  match input
    | #Empty -> "empty"
    | #Short -> "short"
    | #Long -> "long"
}

println(classify(""))
println(classify("custom patterns"))
```

A local pattern hides another that returns the same tags, as a local def hides another of its name.

# Sharing patterns

`export pattern ...` shares a pattern from its module, or from its module block, and importing the module brings it
into scope. A module block's pattern that isn't exported is in scope only within the block: its tag outside the block
is an error that names the block ([`NYX0118`](../standard-tools/errors#nyx0118)). A pattern in a function's block is
local to it, so it can't be exported. Two patterns in scope that return
the same tag are an error, as two imports of the same name are, since a match couldn't tell which one to run
([`NYX0119`](../standard-tools/errors#nyx0119)).

# Patterns are pure

A pattern's type requires no contexts: it can't print, read input or change a cell. A pattern that would is an error
([`NYX0262`](../standard-tools/errors#nyx0262)).

So it never matters how often a pattern runs, and the compiler is free to share the work. A pattern without arguments
runs at most once per match, however many arms use it. Arms are tried top to bottom, as in any match.

A match runs a pattern on one of the values it matches, so a pattern's tag goes at the top of an arm's pattern for
that value, alone or as one part of an and-pattern there, as `#Even & n` is. A pattern's tag inside another pattern,
such as `#some(#Even)`, is an error ([`NYX0261`](../standard-tools/errors#nyx0261)).

# Total patterns

A pattern is _total_ if it matches all possible inputs.

A total pattern can have one choice:

```nanyx
type Rgb = (r: float, g: float, b: float)

pattern Rgb -> #Hsl(h: float, s: float, l: float) = { rgb ->
  -- the conversion, cut short for the example: a grey's hue and saturation are 0
  def h = 0.0
  def s = 0.0
  def l = (rgb.r + rgb.g + rgb.b) / 3.0

  #Hsl(h = h, s = s, l = l)
}

def getRgbColor = { () -> Rgb(r = 0.5, g = 0.5, b = 0.5) }

match getRgbColor()
  | #Hsl(h, s, l) -> println("Turned color into HSL: {h}, {s}, {l}")

-- since the pattern is single-choice and total, we can even use it directly in a value binding:

def #Hsl(h, s, l) = getRgbColor()

println("Lightness: {l}")
-- Turned color into HSL: 0.0, 0.0, 0.5
-- Lightness: 0.5
```

or many:

```nanyx
pattern int -> #Even | #Odd = { n ->
  if n % 2 == 0 then #Even else #Odd
}
```

A total pattern's tags cover its input type, so a match whose arms use all of them is exhaustive, with no `_` arm
needed, on its own or mixed with ordinary patterns of the same type. One that leaves a tag out is not exhaustive
([`NYX0203`](../standard-tools/errors#nyx0203)), and the error names the tag.

```nanyx
pattern int -> #Even | #Odd = { n ->
  if n % 2 == 0 then #Even else #Odd
}

def parity = { (n: int) ->
  match n
    | 0 -> "zero"
    | #Even -> "even"
    | #Odd -> "odd"
}

println(parity(0))
println(parity(7))
-- zero
-- odd
```

# Partial patterns

Custom patterns can be partial (meaning they might not match all possible inputs). For a pattern to be partial, one of
its return values must be a wildcard (`_`). A match using a partial pattern needs a `_` arm to be exhaustive: without
one, it is an error ([`NYX0203`](../standard-tools/errors#nyx0203)) that says so.

As a convenience, a partial pattern with exactly one choice and no payload may return a `bool` instead of an explicit
tag:

```nanyx
pattern int -> #Zero | _ = { n -> n == 0 }

def num = 0

def describe = match num
  | #Zero -> "zero"
  | _ -> "non-zero"

println(describe)

-- Alternatively, we can use if-matching style:
if num is #Zero then
  println("It's zero!")

-- zero
-- It's zero!
```

Otherwise, the body says "no match" by returning `_`, which is valid only in a pattern's body: `_` as a value anywhere
else is an error ([`NYX0263`](../standard-tools/errors#nyx0263)).

```nanyx
pattern string -> #ParseInt(int) | #ParseBool(bool) | _ = { s ->
  if
    | int.fromString(s) is #some(i) -> #ParseInt(i)
    | bool.fromString(s) is #some(b) -> #ParseBool(b)
    | else -> _
}

def describe = { (input: string) ->
  match input
    | #ParseInt(i) -> println("Parsed an integer: {i}")
    | #ParseBool(b) -> println("Parsed a boolean: {b}")
    | _ -> println("Input is neither an integer nor a boolean.")
}

describe("42")
describe("yes")
-- Parsed an integer: 42
-- Input is neither an integer nor a boolean.
```

# Patterns with arguments

Patterns can also take arguments: values passed in where the pattern is used. A pattern with arguments has the form
`<args> -> <input> -> <output>`, and its body is curried: given the arguments, it returns a function of the input.
Work that depends only on the arguments can then be done once, outside the part that runs for each input.

```nanyx
pattern string -> string -> #StartsWith | _ = { prefix ->
  { s -> s \string.startsWith(prefix) }
}

def security = { (url: string) ->
  match url
    | #StartsWith("https://") -> "secure"
    | _ -> "not secure"
}

println(security("https://nanyx.dev"))
-- secure
```

At the use site, a pattern with arguments takes two parenthesised groups: the arguments first, then the payload to
destructure. When the pattern's result tag carries no payload, the second group is left out, as in
`#StartsWith(...)` above. It may be left out of a tag that carries one too, to match the tag without taking its
payload apart, as `#SplitOn(":")` does below; the same goes for a pattern without arguments, so `#Hsl` alone matches what
`#Hsl(h, s, l)` does. The compiler knows which patterns take arguments from their declarations, so there is no
ambiguity with an ordinary tag such as `#circle(r)`, whose single group is always a payload. A pattern used with
arguments it doesn't take, or without those it does, is an error ([`NYX0264`](../standard-tools/errors#nyx0264)), and
arguments of the wrong type are a type mismatch ([`NYX0201`](../standard-tools/errors#nyx0201)).

```nanyx
-- A pattern that splits a string in two on a separator
pattern string -> string -> #SplitOn(string, string) | _ = { separator ->
  { s ->
    match s \split(on = separator)
      | [a, b] -> #SplitOn(a, b)
      | _ -> _
  }
}

match "hello,world"
  | #SplitOn(",")(first, second) -> println("First: {first}, Second: {second}")
  | _ -> println("Input string does not contain exactly one comma")

match "key:value"
  | #SplitOn(":") -> println("A pair, split by a colon")
  | _ -> println("No colon")

-- First: hello, Second: world
-- A pair, split by a colon
```

Several arguments are a tuple. An argument is a literal, a name or a qualified name, or a tuple, list or tag of
those. Anything else, as a call or arithmetic, is bound with a `def` before the `match` and passed by its name, so that
a pattern's arguments never read as a payload to take apart, and a value is worked out once, not each time the arm is
tried (NYX0264):

```nanyx
pattern int, int -> int -> #Between | _ = { low, high ->
  { n -> n >= low and n <= high }
}

def size = { (n: int), (limit: int) ->
  def half = limit // 2
  def bigFrom = half + 1

  match n
    | #Between(0, half) -> "small"
    | #Between(bigFrom, limit) -> "big"
    | _ -> "out of range"
}

println(size(8, 10))
-- big
```

A pattern with arguments runs once per argument list the compiler can see is the same (literals, or the same names),
and otherwise once per arm that uses it; since patterns are pure, the difference can't be observed.

Patterns compose with `&`, which matches only when both sides do:

```nanyx
pattern int -> int -> #DivisibleBy | _ = { divisor ->
  { n -> n % divisor == 0 }
}

def fizzbuzz = { (n: int) ->
  match n
    | #DivisibleBy(3) & #DivisibleBy(5) -> "fizzbuzz"
    | #DivisibleBy(3) -> "fizz"
    | #DivisibleBy(5) -> "buzz"
    | _ -> "{n}"
}

for 1..5 | n -> println(fizzbuzz(n))

-- 1
-- 2
-- fizz
-- 4
-- buzz
```