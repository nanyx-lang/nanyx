---
title: "Custom patterns"
description: "Reusable pattern-matching logic with the pattern keyword"
order: 13
---

> **Being built.** The design below is decided ([#1719](https://github.com/nanyx-lang/nanyx/issues/1719)). Total
> patterns work, and the examples of them on this page run as tests. Partial patterns are still to come
> ([#1722](https://github.com/nanyx-lang/nanyx/issues/1722)), as are patterns with arguments
> ([#1723](https://github.com/nanyx-lang/nanyx/issues/1723)).
The `pattern` keyword allows for the encapsulation of matching logic in reusable components.

A custom pattern is a function with a signature but no name: it is identified by the tags it returns. Its tags are
**capitalised** (`#Even`, `#Hsl`), which is what tells them apart from ordinary tags: a capitalised tag is always a
custom pattern, and one that names no pattern in scope is an error.

The function body contains the logic for matching and extracting values, and returns one of the tags in the pattern's
signature. As with tags elsewhere in the language, they can carry payloads, which are then bound in the match arm.

# Sharing patterns

A pattern is a top-level declaration, like a `def`. `export pattern ...` shares it, and importing its module brings
it into scope. Two patterns in scope that return the same tag are an error, as two imports of the same name are,
since a match couldn't tell which one to run.

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
its return values must be a wildcard (`_`). A match using a partial pattern needs a `_` arm to be exhaustive.

As a convenience, a partial pattern with exactly one choice and no payload may return a `bool` instead of an explicit
tag:

```nanyx skip=#1722
pattern int -> #Zero | _ = { n -> n == 0 }

match num
  | #Zero -> "zero"
  | _ -> "non-zero"

-- Alternatively, we can use if-matching style:
if num is #Zero then
  println("It's zero!")
```

Otherwise, the body says "no match" by returning `_`, which is valid only in a pattern's body:

```nanyx skip=#1722
pattern string -> #ParseInt(int) | #ParseBool(bool) | _ = { s ->
  if | int.fromString(s) is #some(i) -> #ParseInt(i)
     | bool.fromString(s) is #some(b) -> #ParseBool(b)
     | else -> _
}

match getUserInput()
  | #ParseInt(i) -> println("Parsed an integer: {i}")
  | #ParseBool(b) -> println("Parsed a boolean: {b}")
  | _ -> println("Input is neither an integer nor a boolean.")
```

# Patterns with arguments

Patterns can also take arguments: values passed in where the pattern is used. A pattern with arguments has the form
`<args> -> <input> -> <output>`, and its body is curried: given the arguments, it returns a function of the input.
Work that depends only on the arguments can then be done once, outside the part that runs for each input.

```nanyx fragment
pattern string -> string -> #StartsWith | _ = { prefix ->
  { s -> s \string.startsWith(prefix) }
}

match url
  | #StartsWith("https://") -> "secure"
  | _ -> "not secure"
```

At the use site, a pattern with arguments takes two parenthesised groups: the arguments first, then the payload to
destructure. When the pattern's result tag carries no payload, the second group is left out, as in
`#StartsWith(...)` above. The compiler knows which patterns take arguments from their declarations, so there is no
ambiguity with an ordinary tag such as `#circle(r)`, whose single group is always a payload.

```nanyx fragment
-- A pattern that splits a string in two on a separator
pattern string -> string -> #SplitOn(string, string) | _ = { separator ->
  { s ->
    match s \split(on = separator)
      | [a, b] -> #SplitOn(a, b)
      | _ -> _
  }
}

match "hello,world"
  | #SplitOn(",")(first, second) -> "First: {first}, Second: {second}"
  | _ -> "Input string does not contain exactly one comma"
```

Several arguments are a tuple: `pattern (int, int) -> int -> #Between | _`. A pattern with arguments runs once per
argument list the compiler can see is the same (literals, or the same names), and otherwise once per arm that uses
it; since patterns are pure, the difference can't be observed.

Patterns compose with `&`, which matches only when both sides do:

```nanyx fragment
pattern int -> int -> #DivisibleBy | _ = { divisor ->
  { n -> n % divisor == 0 }
}

match n
  | #DivisibleBy(3) & #DivisibleBy(5) -> "fizzbuzz"
  | #DivisibleBy(3) -> "fizz"
  | #DivisibleBy(5) -> "buzz"
  | _ -> n \toString
```
