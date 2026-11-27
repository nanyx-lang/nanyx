---
title: "Functions"
description: "Defining and calling functions"
order: 4
---

Functions are first-class values in Nanyx, meaning they can be passed as arguments, returned from other functions, and stored in data structures.

Functions are written with braces, `{ ... }`, and optionally define parameters: `{ x -> ... }`. The parameter list is a comma-separated list of names, and the body is an expression that computes the result.

# Function basics

A function that takes no parameters (or, more specifically, take `()` as input) is simply `{ ... }`:

```nanyx
def main = {
  println("Hello, world!")
}
```

You could also write it as 

```nanyx
def main = { () ->
  println("Hello, world!")
}
```

But this is almost never preferred, since the unit parameter list is implied when no parameters are specified.

The two differ in one way. A lambda written without parameters, `{ ... }`, that is checked against a function type
taking something other than unit, takes that argument and ignores it:

```nanyx
def apply: (int -> string) -> string = { f ->
  f(1)
}

apply { "ignored" } -- OK: the lambda is an int -> string that ignores its argument
apply { () -> "ignored" } -- error NYX0201: 'int' vs '()'
```

Written `{ () -> ... }`, the lambda says it takes unit, and it is an error to pass it where an `int` is given. With no
type to check it against, `def f = { 0 }` takes unit, as `def f = { () -> 0 }` does: both are `() -> int`. When the
input is a type variable that nothing has settled yet, `{ ... }` takes unit too.

All functions in Nanyx take exactly one argument and return exactly one result. The function type signature is `a -> b`, where `a` is the input type and `b` is the output type.

```nanyx
-- Simple function
def double: int -> int = { x ->
  x * 2
}

-- Calling the function
def result = double(21) -- 42
```

# Multi-parameter functions

Since all functions take one argument, multi-parameter functions actually take a record (or tuple, see [Records and tuples](./records)) as their argument:

```nanyx
def add: int, int -> int = { x, y ->
  x + y
}

-- The type (int, int) -> int means: takes a record of two ints, returns an int

-- Calling with multiple arguments
def sum = add(5, 10) -- 15
```

# Unit type

The `()` type (unit) represents the absence of a meaningful value. It's used for functions that don't take input or don't return output:

```nanyx
-- No meaningful return value
def printHello: [Console] () -> () = {
  println("Hello")
  -- Don't need to write `return ()`
}

-- No parameters
def getMessage: () -> string = {
  "Hello, World!"
}
```

# Lambda expressions

Lambda expressions (also called anonymous functions) are created with braces:

```nanyx fragment
-- Explicit lambda
def print1 = { print("1") }

-- Lambda with parameter
def increment = { x -> x + 1 }

-- Lambda in higher-order function
def names = data \map { item -> item.name }
```

## Shorthand lambdas

Nanyx provides convenient shorthand syntax for common lambda patterns, including member access and binary operators:

```nanyx fragment
-- Property access
def names = users \map { .name }
-- Equivalent to: users \map { user -> user.name }

-- Binary operators
def doubled = numbers \map { * 2 }
-- Equivalent to: numbers \map { x -> x * 2 }

def incremented = numbers \fold(0) { + }
-- Equivalent to: numbers \fold(0) { x, y -> x + y }

-- Comparison operators
def adults = users \filter { .age > 18 }
-- Equivalent to: users \filter { user -> user.age > 18 }

-- The body goes on after the first operation
def evens = numbers \filter { % 2 == 0 }
-- Equivalent to: numbers \filter { x -> x % 2 == 0 }

-- Pipes
def sizes = numbers \map { \math.abs }
-- Equivalent to: numbers \map { x -> x \math.abs }
```

A body that starts with an operator (`+`, `-`, `**`, `*`, `/`, `//`, `%`, `++`, `<`, `<=`, `>`, `>=`, `==` or `!=`) or with a `.` and a field name, or with a pipe, `\f`, has left out its first operand, and that operand is the lambda's argument. The rest of the body follows as it would after any operand, with the usual precedence: `{ * 2 + 1 }` is `{ x -> x * 2 + 1 }`. Only the first operand can be left out. An operator alone, `{ + }`, takes two arguments, one for each side.

A leading `-` is always subtraction from the argument, so `{ - 1 }` is `{ x -> x - 1 }`. A lambda that takes no argument and returns a negative number writes the arrow: `{ -> -1 }`.

The argument has no name, so a name written in the body always means what it means outside the lambda: in `{ * x }`, `x` is the `x` around it.

# Parameters

## Named parameters

If you name parameters in the signature, callers _must_ pass those fields by name, and the function body must destructure them by name too. When the local name is the same as the field name, `field = field` can be shortened to just `field`:

```nanyx fragment
def replace: (string, pattern: string, replacement: string) -> string
  = { s, pattern, replacement -> ??? }

replace("Hello world", pattern = "world", replacement = "Nanyx")
```

Passing a named parameter by position is an error that says which names to write:

```nanyx fragment
replace("Hello world", "world", "Nanyx")
-- error NYX0218: 'replace' takes 'pattern' and 'replacement' by name, not by position: write 'pattern = ...' and 'replacement = ...'
```

This is one rule for every call and every record construction: a positional field is passed positionally, a named field is passed by name. See [Positional vs named inference](./type-inference-and-annotations#positional-vs-named-inference) for how this interacts with unannotated functions.

## Annotating lambda parameters

A lambda's parameters and result can be annotated inline when inference needs a nudge or when the types document intent:

```nanyx
def double = { (x: int): int -> x * 2 }
```

Without brackets, a parameter's annotation ends at the `->`, so `{ q: Point -> q }` takes a `Point` and returns it. A parameter of a function type has brackets around the type, `{ g: (int -> int) -> g(1) }`, or around the whole parameter, `{ (g: int -> int) -> g(1) }`.

A lambda for a function whose parameters are named takes them by name: `{ x, y -> ... }` for `(x: float, y: float) -> float`, in either order, and a parameter that is not one of the names is an error ([NYX0280](../standard-tools/errors#nyx0280)). To give a field another name in the body, take the input with a record pattern, in brackets, `(field = localName)`:

```nanyx
import nanyx/math as math

def magnitude: (x: float, y: float) -> float = { (x = innerX, y = innerY) ->
  math.sqrt(innerX * innerX + innerY * innerY)
}

println(magnitude(x = 3.0, y = 4.0))

-- 5.0
```

## Type arguments

A function can take a type as an argument. This is how a caller says which type it wants where nothing else says it, as for a function that only gives back a value of the type, such as a decoder.

A type argument is a value. The expression `type int` is a value that stands for the type `int`, and its type is `Type(int)`: `Type(a)` is the built-in type of a value that stands for the type `a`. So a parameter that takes a type is written with that type, `Type(a)`, as any other parameter is written with its own, and the caller passes the type with `type` in front of it:

```nanyx
context Decode(a) = (decode: string -> a)

def int.decode: string -> int = { text ->
  text.length
}

def bool.decode: string -> bool = { text ->
  text == "yes"
}

def deserialise: [Decode(a)] Type(a), string -> a = { t, text ->
  decode(text)
}

println(deserialise(type int, "abc")) -- 3
println(deserialise(type bool, "yes")) -- true
```

`a` is a type variable like any other: passing `type int`, a `Type(int)`, where a `Type(a)` is expected fixes `a` as `int`, which picks the [context](../advanced/contexts) instance `Decode(int)`. Inside generic code, `type a` passes on a type variable of its own.

A named parameter can take a type too, as `into: Type(a)`. Like any [named parameter](#named-parameters), it is passed by name, `into = type int`, and the lambda names it as the field:

```nanyx
context Decode(a) = (decode: string -> a)

def int.decode: string -> int = { text ->
  text.length
}

def parse: [Decode(a)] (string, into: Type(a)) -> a = { text, into ->
  decode(text)
}

println(parse("abcd", into = type int)) -- 4
```

A positional and a named type argument differ only in whether the parameter has a name. The field's name and the type variable are separate, but a signature can make them one, `into: Type(into)`, and then names the type `into` in its contexts and its result, `[Decode(into)] (string, into: Type(into)) -> into`. That name can't be a built-in type's, as `list: Type(list)` would be (NYX0270).

A type argument is not an [associated type](../advanced/contexts#associated-types). An associated type, written `elem: type` in a context, is a member whose value is a type, which an instance fixes and a signature names; a type argument is a value that stands for a type, which a caller passes. The two meet where a generic function passes its associated type on as a value, `type elem`.

Earlier releases wrote a type argument's parameter `type a`, or `into: type` for a named one. Those forms still compile, with a warning whose fix rewrites them (NYX0282), and will be errors in a later release.

`type` is always written at the call, and a type argument is never left out, even where the type the call gives back would say what it is: `def n: int = deserialise("abc")` is an error that says to pass `type int` (NYX0271). A function that takes a type argument is otherwise a function like any other, so it can be piped into, wrapped in a lambda and passed as a value.

A type argument is known at compile time: it is never chosen with `if` or `match`, and a list never holds two of different types (NYX0272). It costs nothing when the program runs.

A function that is given a value of type `a` needn't take a `Type(a)` as well: it can require the prelude's context `Typed(a)`, whose one member is the value `typeOf: Type(a)`. The compiler gives an instance of it for every type, so the caller passes nothing more:

```nanyx
context Show(a) = (show: a -> string)

def int.show: int -> string = { n ->
  "int {n}"
}

def showAt: [Show(a)] a, Type(a) -> string = { value, t ->
  show(value)
}

def describe: [Typed(a), Show(a)] a -> string = { value ->
  showAt(value, typeOf)
}

println(describe(5)) -- int 5
```

`Typed` doesn't replace type arguments: in `[Typed(a)] string -> a`, nothing at the call says what `a` is. A `use` can load an instance of `Typed` as of any context, but the only `Type(int)` value is `type int`, so it stands for the same type.

## Inspecting a type

Generic code inspects a type argument by matching on it. Each arm is a tag of the type's description, and an arm that says what the type is tells the checker too: in the `#int` arm below, `a` is `int`, so `value + 1` checks, and in the `#string` arm it is `string`. The function is checked once, as all generic code is, not once for each type it is called at:

```nanyx
def describe: a, Type(a) -> string = { value, t ->
  match t
    | #int -> "the int {value + 1}"
    | #string -> "the string " ++ value
    | #list(elem) -> value \list.map { x -> describe(x, elem) } \string.join(", ")
    | #record(r) ->
      r.fields
        \list.map { f -> f.name ++ " = " ++ describe(f.get(value), f.type) }
        \string.join(", ")
    | else -> "something else"
}

println(describe(41, type int)) -- the int 42
println(describe((x = 1, y = 2), type (x: int, y: int))) -- x = the int 2, y = the int 3
```

The tags are:

- `#int`, `#float`, `#bool`, `#string`, `#unit`, `#char` for a [character](types#characters), and `#i8` to `#u64` and `#f32` for the [sized numbers](types#basic-types): the type is that one.
- `#list(elem)`, `#array(elem)` and `#set(elem)`, and `#map(key, value)`: the type is that collection, of an element type the checker never learns, which `elem` is the `Type` of. So the `#list` arm above calls `describe` again for each element, with `elem`.
- `#function`: the type is a function's. Its input and output aren't described.
- `#record(r)`: the type is a record, a tuple, or a nominal type made from one, seen from its home module. `r` is a `RecordInfo(a)`, with the type's `name`, its `module` (a file's name, or a standard-library module's import path) and its `doc` comment, each empty for a record written out, the `parameters` it is applied to, and its `fields` in the order the type writes them.
- `#union(u)`: the type is a union of tags. `u` is a `UnionInfo(a)` with the same `name`, `module`, `parameters` and `doc`, and its `cases`.
- `#nominal(n)`: a nominal type seen from outside its home module, which shows only its `name`, `module`, `parameters` and `doc`, so its own module decides how it is written and read. Where a `Type` value is seen from is where its `type X` is written, so the `typeOf` of a [`Typed`](#type-arguments) instance, which the prelude writes, sees every nominal type of yours as `#nominal`.

Each field of `r.fields` has a type of its own, which the checker never learns: a field `f` has its `name`, its `type`, a `Type` of it, `get`, which takes a value of `a` and gives the field's value, and the [`attributes`](attributes#attributes-on-fields) written before it, each with its `name` and its `arguments` as pairs of text. Since `f.get(value)` and `f.type` are of the same type, the call `describe(f.get(value), f.type)` checks, whatever the field's type is. A case of `u.cases` has its `name`, its payload's `type` (`()` for a tag without one), and `get`, which gives `#some(payload)` for a value of that case and `#none` otherwise. A value of a field's or an element's type can only be used where it is opened: giving it back from the lambda or the arm is an error (NYX0274), as it could then be given where another field's type is expected.

A `match` on a type that leaves out a tag and has no `else` is a non-exhaustive match (NYX0203), and a pattern that isn't a description's tag is NYX0273. A function that calls itself at the type of a part, as `describe` does, must write its type, so that it can be called at other types.

## Building a value

A description builds a value as well as taking one apart, which is what a decoder needs. A record's `r.build` is given a function that gives each field's value, as `#some(value)`, or fails; it calls it with each field in turn and gives `#some` of the record, or the first failure, with no record made. A case's `c.build` makes a value of its union from a payload of the case's type:

```nanyx
def zeroOf: Type(a) -> #some(a) | #noCases | #noZero = { t ->
  match t
    | #int -> #some(0)
    | #string -> #some("none")
    | #record(r) -> r.build { f -> zeroOf(f.type) }
    | #union(u) ->
      match u.cases
        | [c, ..._] ->
          match zeroOf(c.type)
            | #some(payload) -> #some(c.build(payload))
            | failure -> failure
        | [] -> #noCases
    | else -> #noZero
}

type Size = #small(int) | #large

println(zeroOf(type (name: string, size: Size))) -- #some(name = none, size = #small(0))
println(zeroOf(type (ratio: float))) -- #noZero
```

The function given to `r.build` must give a value of whichever field it is given, so the checker gives it a field whose type is one of its own, which it never learns, and checks that it gives a value of that type: `f.type` is how to make one, as `zeroOf(f.type)` does. A function that gives a value of one particular type, as `{ f -> #some(1) }` would, is a type mismatch. A nominal type seen from outside its home module has no `build`, since its description shows no structure.

# Callable values

Any value with a function type can be called like a function. A [map](../stdlib/prelude-maps) can be called too, and calling it is a lookup, which gives `#some(v) | #notFound`:

```nanyx
def dict: map(string, string) =
  ["Hello" => "Bonjour", "Goodbye" => "Au revoir"]

println(dict("Hello")) -- #some(Bonjour)
println(dict("Hola")) -- #notFound
```

A map is not a function value: where a function is wanted, pass `{ word -> dict(word) }`. Whether a map should be accepted there is [#2763](https://github.com/nanyx-lang/nanyx/issues/2763).

# Higher-order functions

Functions that take other functions as arguments or return functions are called higher-order functions:

```nanyx
-- Takes a function as an argument
def apply: (a -> b), a -> b = { f, x ->
  f(x)
}

-- Returns a function
def makeAdder: int -> int -> int = { x ->
  { y -> x + y }
}

def add5 = makeAdder(5)
def result = add5(10) -- 15
```

# Pattern-matching functions

Since a function that consists only of a pattern match on its arguments is so common, a shorthand syntax is available where functions can pattern match directly on their arguments:

```nanyx
-- Simple pattern matching function
def sumList: list(int) -> int = {
  | [] -> 0
  | [head, ...tail] -> head + sumList(tail)
}

-- Multiple arguments with patterns
def divide: int, int -> #some(int) | #divideByZero = {
  | _, 0 -> #divideByZero
  | x, y -> #some(x // y)
}

-- Each arm has a pattern for each argument, except a lone `else`, which matches whatever they all are
def quadrant: int, int -> string = {
  | 0, 0 -> "origin"
  | 0, _ -> "on the y axis"
  | else -> "elsewhere"
}

-- Pattern matching with guards
def classify: int -> string = {
  | 0 -> "zero"
  | 1 -> "one"
  | n & { < 0 } -> "negative: {n}"
  | _ -> "other"
}
```

# Recursive functions

Top-level definitions in a module all share one scope (see [Declaration order](./modules#declaration-order)), so a top-level function can call itself without any extra syntax:

```nanyx
def factorial: int -> int = { n ->
  if n <= 1 then 1 else n * factorial(n - 1)
}

def length: list(a) -> int = {
  | [] -> 0
  | [_, ...tail] -> 1 + length(tail)
}
```

A definition inside a function body is different: it is only in scope _after_ it has been defined, so its own body cannot see it. Use the `rec` keyword to make a local definition visible to itself:

```nanyx
def countdown = { n ->
  rec loop = { i ->
    if i > 0 then
      println("{i}")
      loop(i - 1)
  }

  loop(n)
}
```

## Tail recursion

A call is a _tail call_ when it is the **last** thing its function does: the function returns whatever the call returns, with nothing left to do afterwards. Nanyx guarantees that a tail call does not use up stack, so a function that recurses through tail calls runs in constant stack space, however many times it calls itself. It is a loop written as a function.

The guarantee covers every kind of call: a function calling itself, two functions calling each other, and calls through a closure or a context member. It is part of the language, not an optimisation, and holds whether or not a given compiler version optimises anything else.

Recursion that is _not_ in tail position gets no such promise. `factorial` and `length` above multiply or add after the recursive call returns, so each call keeps a stack frame, and a deep enough input runs out of stack: the program stops with an error that names the function. The [official linter](../standard-tools/official-linter#recursion-not-in-tail-position) points such calls out.

A common pattern is to nest a helper function that carries an accumulator:

```nanyx
def sum: list(int) -> int = { xs ->
  rec loop = {
    | [], acc -> acc
    | [head, ...tail], acc -> loop(tail, acc + head)
  }

  loop(xs, 0)
}
```

Here `loop` is internal to `sum`, and `acc` stores the running result. Each recursive step passes the updated accumulator forward, and the base case returns it directly.

## Tail position

A call is a tail call when it is in _tail position_:

- The body of a function is in tail position.
- When an `if` is in tail position, so are its branches.
- When a `match` is in tail position, so is the body of each arm, and the same goes for the replacement of each arm of an [`except`](./error-handling#the-except-keyword).
- When a block is in tail position, so is its last expression.
- When `use C(...) in body` is in tail position, so is `body`.

A pipe `x \f(y)` is the call `f(x, y)`, so the same rules apply to it. Nothing else is in tail position: not the arguments of a call, the operands of an operator (`1 + f(n)`), an interpolated value (`"{f(n)}"`), the scrutinee of a `match`, an element of a list or field of a record (`[x, ...f(xs)]`), or the body of a `for` loop.

A [workflow](../advanced/workflows) block is a call to its builder, with the rest of the block passed to each keyword as a closure, and the same rules apply to that code.

A def that starts a task waits for its tasks before it returns ([Async](../advanced/async#each-def-is-a-scope)), so a call last in it is not a tail call, unless what it calls is another def that starts tasks: then that def's tasks join the caller's, and the call stays a tail call. Both defs must be in the same module for that.

# Function composition

Functions can be composed to create new functions:

```nanyx
def compose: (b -> c), (a -> b) -> a -> c = { f, g ->
  { x -> f(g(x)) }
}

def addOne = { + 1 }
def double = { * 2 }

def addOneThenDouble = compose(double, addOne)
def result = addOneThenDouble(5) -- 12
```

# Currying

While Nanyx functions naturally take one argument, you can create curried-style functions:

```nanyx
def add: int -> int -> int = { x ->
  { y -> x + y }
}

def add5 = add(5)
def result = add5(10) -- 15
```

The function a call gives back can be called straight away, without naming it first:

```nanyx
def add: int -> int -> int = { x ->
  { y -> x + y }
}

def result = add(5)(10) -- 15
```

# Partial application

With records, you can simulate partial application:

```nanyx
def process: (config: string, data: int) -> string = { config, data ->
  "{config}: {data}"
}

-- Create a partially applied version
def processWithConfig = { data -> process(config = "verbose", data = data) }

println(processWithConfig(3))

-- verbose: 3
```

# Type Annotations for clarity

While type inference works well, annotating function signatures is recommended for exported functions:

```nanyx fragment
-- Without annotation (inferred)
def add = { x, y -> x + y }

-- With annotation (clearer, better errors)
export def add: (int, int) -> int = { x, y -> x + y }
```

Type annotations serve as documentation and help catch errors early.

# Pure functions

Functions without effects (no contexts) are pure - they always return the same output for the same input:

```nanyx
-- Pure function
def add: int, int -> int = { x, y ->
  x + y
}

-- Effectful function (requires context)
def greet: [Console] string -> () = { name ->
  println("Hello, {name}!")
}
```

# Function examples

## Map Implementation

```nanyx
def map: list(a), (a -> b) -> list(b) = { xs, f ->
  match xs
    | [] -> []
    | [head, ...tail] -> [f(head), ...map(tail, f)]
}
```

## Filter implementation

```nanyx
def filter: list(a), (a -> bool) -> list(a) = { xs, predicate ->
  match xs
    | [] -> []
    | [head, ...tail] ->
      if predicate(head) then [head, ...filter(tail, predicate)] else filter(tail, predicate)
}
```

## Fold implementation

```nanyx
def fold: list(a), b, (b, a -> b) -> b = { xs, acc, f ->
  match xs
    | [] -> acc
    | [head, ...tail] -> fold(tail, f(acc, head), f)
}
```

# Polymorphic functions

In Nanyx a polymorphic function is simply one whose return type depends on 
the types of its argument(s)

The simplest polymorphic function is probably the identity function, i.e.
a function that does nothing other than return its input

```nanyx
def id = { x -> x }
```

Thanks to type inference we don't have to write type annotations for this
function or give it a specification, but if we wanted to it would look like
this:

```nanyx
def id: a -> a = { x -> x }
```

The lowercase `a`, when used in a type position, is a type variable (equivalent
to the concept of 'generics' in other languages). 

```nanyx
def head: list(a) -> #some(a) | #emptyList = {
  | [] -> #emptyList
  | [x, ...] -> #some(x)
}
```