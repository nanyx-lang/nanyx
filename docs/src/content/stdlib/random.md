---
title: random
description: Random numbers, from the Random context a host provides or from a seed
order: 12.6
---

A function that draws random numbers requires `Random`. The console and HTTP hosts provide one that draws from the
operating system, and their preludes bring `Random` into every module of a program, so the entry point can roll a
die with no setup:

```nanyx
import nanyx/random as random

def roll = random.between(1, 6)
println(roll >= 1 and roll <= 6)
-- true
```

`random.seeded` runs a function under a `Random` that a seed decides instead, so the same seed gives the same numbers
on every run. That is how a test, or a game that replays, gets numbers it can predict:

```nanyx
import nanyx/random as random

def deal = { _ -> random.shuffle(["ace", "king", "queen", "jack"]) }

println(random.seeded(42, deal) == random.seeded(42, deal))
-- true
```

`Random` has one member, `bits`, and every function here is written on it, so `use Random(bits = ...)` makes a source
that gives whatever numbers a test needs. A library that names `Random` in a type imports it from `nanyx/random`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Random numbers: a function that needs them requires [`Random`](#random), and draws them with the functions
here.

A host provides `Random` to the program's entry point, as it provides `Console`: the console and
HTTP hosts draw its bits from the operating system's random source. [`seeded`](#seeded) runs a function
under a `Random` that a seed decides, so a program, or a test, can give the same numbers every
time it runs.

Import it with `import nanyx/random as random` to call `random.between(1, 6)` as the docs do.

## `Random`

```nanyx fragment
context Random = (
  bits: () -> int
)
```

A source of random bits. A host supplies one, and [`seeded`](#seeded) makes one from a seed; a `use` of
`Random(bits = ...)` makes any other, such as one that gives the numbers a test needs.

- `bits`: 64 random bits, as an `int`: each of its values as likely as any other.

## `below`

```nanyx fragment
def below: [Random] int -> int
```

A random `int` from 0 up to `count`, not including it: `random.below(6)` is one of 0 to 5, each as
likely as the others. A `count` below 1 stops the program, as [`crash`](prelude#crash) does.

## `between`

```nanyx fragment
def between: [Random] int, int -> int
```

A random `int` from `low` to `high`, both included: `random.between(1, 6)` rolls a die. A `high`
below `low` stops the program, as [`crash`](prelude#crash) does.

## `fraction`

```nanyx fragment
def fraction: [Random] () -> float
```

A random `float` from 0.0 up to 1.0, not including 1.0, every one of the 2⁵³ evenly spaced
values in between as likely as any other.

## `coinToss`

```nanyx fragment
def coinToss: [Random] () -> bool
```

`true` or `false`, each as likely as the other.

## `choose`

```nanyx fragment
def choose: [Random] list(a) -> #some(a) | #empty
```

An element of `xs` chosen at random, each as likely as the others, or `#empty` when it has none.

## `shuffle`

```nanyx fragment
def shuffle: [Random] list(a) -> list(a)
```

The elements of `xs` in a random order, each order as likely as any other.

## `seeded`

```nanyx fragment
def seeded: [c] int, ([c, Random] () -> a) -> a
```

What `body` gives, run with a `Random` whose bits `seed` decides: the same seed gives the same
bits, in the same order, on every run. The bits are SplitMix64's, which are fast and pass the
usual statistical tests, but can be predicted from a few of them: they are no use for keys or
passwords.

<!-- End of the generated reference. -->
