---
title: "Piping"
description: "Readable left-to-right composition"
order: 9
---

Nanyx supports a pipe operator that sends the value on the left into the first argument of a function: `x \f(...)`.

# Why piping?

In any programming language, if the user wishes to call a series of functions on a value (passing the result of one function to the next), they can nest the function calls. The problem with nested function calls is that the functions execute from the inside out and must therefore be read backwards (from right to left), which can be hard to follow:

```nanyx fragment
def getStreamFromInput: Request -> Stream(Byte) = ...
def getBytesFromStream: Stream(Byte) -> string = ...
def deserialisePerson: string -> Person = ...

def personObj = deserialisePerson(getBytesFromStream(getStreamFromInput(request)))
```

Piping makes sequences of function calls easy to read by allowing the functions to be written in the order they are executed—and is the preferred style for expressions containing nested function calls:

```nanyx fragment
def personObj = request
  \getStreamFromInput
  \getBytesFromStream
  \deserialisePerson
```

# Piping with extra arguments

The piped value will be combined with any additional written arguments into a record to form the argument of the function. For example, `x \f(y)` is equivalent to `f(x, y)` and `x \f(y, z)` is equivalent to `f(x, y, z)` etc. This allows you to pipe into functions that take more than one argument and, unlike curried functions, the argument order remains the same as the non-piped version. 

```nanyx
def add = { x, y -> x + y }

def result = 1
  \add(2)
  \add(3)
```

## Pipes on the lines after

A pipe chain can go on over several lines, one pipe to a line. A line that starts with `\` continues the expression above it when it is indented more than the line that expression starts on. This works anywhere an expression is written, including the last line of a lambda body and the body of a match arm:

```nanyx
def total = { items: list(price: int) ->
  items
    \map { .price }
    \sum
}
```

A `\` at the same indent as the line the expression starts on, or less, does not continue it.

## Piping with trailing lambdas

If the last argument of a function is a lambda, you can write it after the piped value without parentheses:

```nanyx
def result = [1, 2, 3]
  \map { + 1 }
```

The piping and trailing lambda syntax can be combined with extra arguments as well:

```nanyx
def result = [1, 2, 3] \fold(0) { + }
```

This is a relatively simply syntax transformation, which becomes

```nanyx
def result = list.fold([1, 2, 3], 0, { + })
```

# Piping into a match

`\match`, followed by arms, matches on the piped value: `x \match | ...` is `match x | ...`. It lets a pipe chain end in, or go on after, a match:

```nanyx
def describe = { words ->
  words
    \string.join(" ")
    \match
      | "" -> "nothing"
      | s -> s
    \println
}
```

A pipe after the arms, indented as the `\match` is, applies to the result of the whole match.

# Qualified piping

If the function is [attached](../advanced/attached-definitions) to the type on the left-hand side of the `\`, the function does not need to be qualified with the type name: `x \f` resolves `f` by the type of `x`. This is the only way to call an attached definition without its type name. Dot access, `x.f`, only reads a field or a built-in property of `x` (see [Dot access](../advanced/attached-definitions#dot-access)).

```nanyx
-- In regular function syntax we must qualify the function with the type name
def parts = string.split("hello world", on = " ")

-- With piping, we can omit the type name because we know the type of the first argument before we need to identify the function
def sameParts = "hello world" \split(on = " ")
```

Note that the function can still be qualified in a pipe expression if desired; it might also be required if the type of the first argument has not yet been determined by the time the function is identified:

```nanyx fragment
def f = { x ->
  x\foo -- Error: the type of the value piped to 'foo' is not known here, so the pipe cannot find 'SomeType.foo'
  x\SomeType.foo -- OK: the function is qualified, so it can be resolved 
}
```