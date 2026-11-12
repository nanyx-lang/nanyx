---
title: "Contexts"
description: "Implicit parameters for effects and typeclasses"
order: 2
---

Contexts are implicit parameters in Nanyx. A function can declare the contexts it needs, and the members of those contexts become available inside its body. This is how Nanyx models effects, state, and typeclass-style constraints.

# Declaring a context

A context definition looks like a record type, but uses the `context` keyword:

```nanyx
context Console = (
  write: string -> ()
  writeError: string -> ()
)
```

Contexts are structural by default: any record whose members match the declared shape can be loaded as an instance. Prefixing the name with `$` (for example `$Memory`) makes the context nominal, so that only its home module can create instances; see [Nominal and protected types](./nominal-and-protected-types).

# Requiring a context

A function can specify required contexts in its type signature. Here, `sayHello` can call `println` because it runs in `Console`. The real `Console`, in `nanyx/console`, has a few more members, and `println` is a function written on top of its `write`:

```nanyx
def sayHello: [Console] () -> () = {
  println("Hello, world!")
}
```

# Loading a context

Use the `use` keyword to load an instance of a context into scope. Once loaded, any functions that require it can be called:

```nanyx
def main = {
  use Console(
    write = { text -> hostPrint(text) }
  )

  sayHello()
}
```

A member that uses another context has to say so in the context's definition, as any function type does. To give `Log` a member that prints, write `context Log = (log: [Console] string -> ())`: then `use Log(log = { s -> println(s) })` checks, and calling `log` requires `Console` as well as `Log`. With `log: string -> ()`, that lambda is an error (`NYX0227`), since it requires `Console` and `log`'s type says it requires nothing.

You can also load a context for a single expression with `use ... in`. The context is loaded for that expression only, which can start on the next line:

```nanyx
def result = use Console(write = hostPrint) in
  println("Hello from a scoped context")
```

After `in` there can also be a block of statements, on the lines after it and indented more than the line `use` is on. The block's value is the value of `use ... in`, and the context is loaded for the block only:

```nanyx
def main = {
  def total = use Counter(next = { n -> n - 1 }) in
    def a = counted(3)
    a + 10

  println("{total}")
}
```

Don't put the block in braces: `{ ... }` is a function of no arguments, so `use Counter(...) in { ... }` makes a function instead of running the statements. If the function uses the context, that is an error, as the next section says; if it doesn't, the official linter's [`useInBraces`](../standard-tools/official-linter#use--in-with-a-body-in-braces) rule warns about it.

`use` loads the value of any expression. An instance of a parameterised context is a value whose type is the context applied to a type, so define it with `def` and load it by name:

```nanyx
context Show(a) = (show: a -> string)

def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }

def numbered: Show(int) = (show = { n -> "#{n}" })

def main = {
  use numbered
  describe(42)   // "#42"
}
```

A parameter of the context's type, or a call that returns an instance, can be loaded the same way: `use instance`, `use numberedFrom(100)`.

# Contexts are part of the type

A value's context requirement is part of its type. You can annotate just the contexts and let the rest infer:

```nanyx
def greet: [Console] = {
  println("Hi")
}
```

A function that writes its type runs only in the contexts the type declares, so calling `println` from a `def f: int -> int` is an error. A function that does not write its type needs whichever of the runtime's contexts its body uses, and so does any function that calls it. The top level of a module runs in the runtime's contexts.

Passing a function as a value needs its contexts where it runs, not where it is named. Given `def useFoo: [Foo] string -> string` and an `apply` that calls the function it is given, `apply(useFoo, "hi")` is an error unless `Foo` is available at that call, from the type of the function around it or from a `use`. But `def later = useFoo` is fine anywhere: calling `later` needs `Foo`.

A function's type says which contexts it requires, and a lambda's type says which ones it uses: `{ s -> bar(s) }` is a `[Foo] string -> string`. Calling it requires `Foo`, as calling a function that declares `[Foo]` does, and it can't be given where a pure function is expected (`NYX0227`). A higher-order function takes such a lambda with a context variable, as `list.map` does (see [Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts)). A function named as a value, such as `useFoo` above, has its contexts in its type too.

The contexts a function value requires are the ones in scope where it is called: calling a `[Foo] () -> string` inside a `use Foo(...)` uses that `Foo`, whichever `Foo` was in scope where the function was made or named. Each call passes the function the instances in scope there:

```nanyx
context Foo = (bar: string -> string)

def run: [c] ([c] () -> string) -> string = { f ->
  use Foo(bar = { s -> "inner " ++ s }) in f()
}

def main: [Foo] () -> string = { run({ bar("x") }) }   // "inner x"
```

A function value given where one requiring other contexts is expected, such as a lambda that prints given for a context's member whose type doesn't require `Console`, keeps the instances of those other contexts that are in scope where it is given.

So a lambda that uses a context can leave the scope that supplies it. A lambda uses `Foo` when it calls one of `Foo`'s members, calls a function that requires `Foo`, or calls a local function that does either. Its type still requires `Foo`, so the function that returns it says so, and calling it needs `Foo` where it is called:

```nanyx
context Foo = (bar: string -> string)

def fooEscapes: [Foo] string -> ([Foo] () -> string) = { s ->
  { bar(s) }
}

def main: [Console] () -> () = {
  def escaped = use Foo(bar = { s -> s }) in fooEscapes("hi")
  use Foo(bar = { s -> s ++ "!" }) in println(escaped())   // hi!
  println(escaped())                                       // error: requires 'Foo'
}
```

A result type that leaves the context out, `[Foo] string -> () -> string`, is an error (`NYX0227`). A lambda that uses only a value computed with the context requires nothing, so `def t = bar(s)` followed by `{ t }` is a `() -> string`.

The same goes for a parameterised context. A function that does not write its type, and calls a function that requires `Show(a)` with a value of one of its own type variables, requires `Show` at that variable itself:

```nanyx
def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }
def twice = { v -> describe(v) ++ describe(v) }   // [Show(α)] α -> string
```

Each call to `twice` then finds a `Show` for the type it is called with, as a call to `describe` does. A generic local `def` cannot have one inferred yet: write its type.

This is also how scoped mutation is enforced: values that still require `memory` cannot escape into code that does not provide that context. See [Memory: Values cannot escape memory scope](../advanced/memory#values-cannot-escape-memory-scope).

For a formal proposal of type-level context requirements, see [Work in progress: Context-qualified types](../work-in-progress/context-qualified-types).

# Combining contexts

You can require multiple contexts by joining them with `&`:

```nanyx
def readAndPrintFile
  : [Console & FileSystem] string -> ()
  = { path ->
    def contents = openFile(path).contents
    println(contents)
  }
```

# Console by default

The entry function (the one marked `@entry`, see [Attributes](../tour/attributes)) runs in `Console`, which is why `println` is available in `main` without extra setup. `println`, `print`, `eprintln` and `eprint` take any value with a [`ToString`](#tostringa): `println(42)` prints `42`, and `println([1, 2])` prints `[1, 2]`.

# Contexts as typeclasses

Contexts can also express typeclass-style constraints. Define a context with the required operations and use it as a constraint:

```nanyx
context Combine(a) = (
  empty: a
  combine: (a, a) -> a
)

export def combineAll: [Combine(a)] list(a) -> a
  = { items -> items \fold(empty, combine) }
```

Inside `combineAll`, `empty` and `combine` are the members of the instance it is given, so it combines whatever type it is called with, as long as that type has a `Combine`.

The operators read through contexts of the prelude in the same way: `x + y` on values of a type variable calls the `` `+` `` of [`Add`](#adda), `x ++ y` the `` `++` `` of [`Append`](#appenda), and `x == y` the `` `==` `` of [`Eq`](#eqa). On an `int`, a `float`, a `string` or a `bool`, an operator is always the built-in one, as `++` is on a list, so a counter's `n + 1` in a generic function still adds ints.

## Operators find their context

An operator reads the context member of its name (prefix `-` reads `negate`), and its context is the prelude's context with the fewest members that has that member. `Add` and `Num` both have a `` `+` ``, and `Add` is the smaller, so `+` is read through `Add`; only `Num` has a `` `*` ``.

A function that does not write its type requires the contexts its operators need, of each of its own type variables they are used on:

```nanyx
def add = { x, y -> x + y }                 -- [Add(a)] a, a -> a
def lerp = { a, b, t -> a + (b - a) * t }   -- [Num(a)] a, a, a -> a
def inc = { x -> x + 1 }                    -- int -> int: `1` is an int
```

Adding and multiplying values of one type needs `Num` alone, which has every member of `Add`. A function inside another function requires no contexts, so its arithmetic is on ints or floats. For now the same goes for a function with operators on two different type variables, and for one the module passes as a value, as `add` in `xs \list.fold(0, add)`: see [Operator contexts](../work-in-progress/operator-contexts#functions-that-do-not-write-their-type).

A function's type can also require an operator by its member instead of by the context's name. It means the same as naming the prelude's context that has the member:

```nanyx
def add: [`+`: a, a -> a] = { + }                           -- [Add(a)] a, a -> a
def scale: [(`+`: a, a -> a) & (`*`: a, a -> a)] a, a -> a  -- [Num(a)] a, a -> a
  = { x, k -> x * k + x }
```

A member's type runs to the end of the brackets, a `&` in it being an intersection of types, so a member among other contexts goes in brackets of its own: `` [(`+`: a, a -> a) & Eq(a)] ``.

Only an operator that is a member of one of the prelude's contexts can be required this way; any other member is required by naming its context, as `[ToString(a)]`.

Contexts with associated types, members whose value is a type, are a work in progress; see [Type members and reflection](../work-in-progress/type-members-and-reflection#associated-types).

# Common contexts

In practice, a small set of capability contexts appears frequently in generic APIs.

## Eq(a)

`Eq(a)` is the standard library's context for equality. `x == y` is read through it, and `x != y` is `not (x == y)`.

```nanyx
context Eq(a) = (
  `==`: (a, a) -> bool
)
```

Every type that holds no function has an `Eq`: the compiler compares records field by field, lists element by element, and tags by name and then payload. Functions have no equality, so comparing two functions, or two values that hold one, is a type error.

A function that compares values of one of its type variables requires `Eq` of it:

```nanyx
def contains: [Eq(a)] (list(a), a) -> bool = { xs, target ->
  xs \any { == target }
}
```

A function that does not write its type has the requirement inferred: `def same = { x, y -> x == y }` is `[Eq(a)] a, a -> bool`. Comparing a type built from a variable requires `Eq` of the variable, so `==` on two `list(a)` needs `Eq(a)`.

A nominal type can attach its own `==`, which `a == b` on it, and a function that requires `Eq`, then use for it:

```nanyx
def $Email.`==`: $Email, $Email -> bool = { a, b -> lower(a) == lower(b) }
```

Only a nominal or protected type can: a structural type, such as `type Point = (x: int, y: int)`, and a built-in type always compare by their structure, so attaching `==` to one is an error. `!=` cannot be attached to any type, because it is always `not (a == b)`.

An `==` passes through the structures around it. A tag with a payload, a tuple, a record and a list are compared part by part, each part with its own `==`:

```nanyx
#some($Email("A@x.io")) == #some($Email("a@x.io"))   // true
[$Email("A@x.io")] == [$Email("a@x.io")]             // true

def bothSome: [Eq(a)] (a, a) -> bool = { x, y -> #some(x) == #some(y) }   // compares x and y with Eq(a)
```

## Ord(a)

`Ord(a)` is the standard library's context for ordering. `x < y`, `x <= y`, `x > y` and `x >= y` are all read through its one member, so they cannot disagree with each other.

```nanyx
context Ord(a) = (
  compare: (a, a) -> #less | #equal | #greater
)
```

The compiler has an `Ord` for numbers, `bool` (`false` first), strings (by their bytes, which is the order of their code points), and tuples and lists of them, compared position by position, with a list that is a prefix of another first. On floats the operators follow IEEE 754, so every comparison with `NaN` is false, while `float.compare` is a total order with `NaN` after every other float; that is what `list.sort` uses.

Records and tags have no order: a record's fields and a union's tags have no order of their own in its type, so `<` on them is a type error. Sort a list of them by a key with `list.sortBy`, or make the type nominal and attach a `compare`:

```nanyx
type $Version = (major: int, minor: int)

def $Version.compare: $Version, $Version -> #less | #equal | #greater = { a, b ->
  if a.major != b.major then int.compare(a.major, b.major) else int.compare(a.minor, b.minor)
}
```

As with `==`, only a nominal or protected type can attach `compare`. The built-in types have theirs attached as functions, `int.compare` and `float.compare` among them, but their order is always the compiler's own.

A function that orders values of one of its type variables requires `Ord` of it, `def largest: [Ord(a)] list(a) -> #some(a) | #emptyList`, and one that does not write its type has it inferred: `def larger = { x, y -> if x > y then x else y }` is `[Ord(a)] a, a -> a`. `Ord` does not require `Eq`; a type that attaches both should keep them consistent.

## Hash(a)

`Hash(a)` is the standard library's context for hashing: an `int` for each value, the same for any two values `==` finds equal, so that a map or a set can find a key by its hash. It has no operator; a function that needs it requires it and calls `hash`.

```nanyx
context Hash(a) = (
  hash: a -> int
)
```

The compiler has a `Hash` for every type it has an `Eq` for, which hashes by structure: a value's hash is made from its parts' hashes. Functions have no hash. A nominal type that attaches its own `==` must attach a `hash` that agrees with it, since the compiler's own would not; without one, asking for `Hash` of it is a type error that says so:

```nanyx
type $Email = string

def $Email.`==`: $Email, $Email -> bool = { a, b -> lower(a) == lower(b) }

def $Email.hash: $Email -> int = { e -> string.hash(lower(e)) }
```

As with `==`, only a nominal or protected type can attach `hash`. The built-in types have theirs attached as functions, `int.hash` and `string.hash` among them, for building a type's own `hash`. A type that holds a part with its own `hash`, such as a list of `$Email`s, has no `Hash` yet ([#641](https://github.com/nanyx-lang/nanyx/issues/641)).

## Default(a)

Use `Default(a)` when a function needs a fallback value for a type.

```nanyx
context Default(a) = (
  default: () -> a
)

def valueOrDefault: [Default(a)] #some(a) | #none -> a = {
  | #some(v) -> v
  | #none -> default()
}
```

## Add(a)

`Add(a)` is the standard library's context for values that add and subtract. `x + y` and `x - y` are read through it, and so is `-x`, which is its `negate`.

```nanyx
context Add(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
)
```

The compiler has its own for `int` and `float`. A function that adds values of one of its type variables requires `Add` of it, and in such a function a `0` added to one of those values is `zero`:

```nanyx
def total: [Add(a)] list(a) -> a = { items -> items \fold(zero) { + } }

def orZero: [Add(a)] a -> a = { x -> x + 0 }
```

`list.sum` is `total`. A function that writes its type and adds without `[Add(a)]` is an error that says to add it. One that does not write its type has the requirement [inferred](#operators-find-their-context): `def add = { x, y -> x + y }` is `[Add(a)] a, a -> a`, while `def inc = { x -> x + 1 }` is still `int -> int`. Any other type gets an `Add` by attaching the four members (see [Context synthesis](#context-synthesis)).

## Num(a)

`Num(a)` is the context for numbers: it has every member of `Add(a)`, and `x * y`, `x / y` and `x % y` are read through it too.

```nanyx
context Num(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
  `*`: (a, a) -> a
  `/`: (a, a) -> a
  `%`: (a, a) -> a
  fromInt: int -> a
)
```

In a function that requires `Num(a)`, an integer literal beside a value of type `a` is `fromInt` of it, so `2` in `x * 2` is an `a`. A decimal literal there is an error, since `a` may be an integer type.

```nanyx
def square: [Num(a)] a -> a = { x -> x * x }

def mean: [Num(a)] list(a) -> a = { xs -> list.sum(xs) / fromInt(list.length(xs)) }
```

`mean` calls `list.sum`, which requires `Add(a)`: contexts are structural, and a `Num` has every member an `Add` has, so the `Num` it is given serves. On `int`, `/` truncates and `%` takes the sign of the left operand, so `mean([1, 2, 4])` is `2`; on `float` they follow IEEE 754. A measured type, such as a length, is to have an `Add` but no `Num`, because multiplying two lengths gives an area ([units and dimensions](../work-in-progress/units-and-dimensions)).

## Append(a)

`Append(a)` is the context for values that join end to end. `x ++ y` on values of a type variable is read through it.

```nanyx
context Append(a) = (
  `++`: (a, a) -> a
)
```

The compiler has its own for `string` and `list(a)`, where `++` is the built-in operator. Any other type gets one by attaching `` `++` ``:

```nanyx
def surround: [Append(a)] a, a -> a = { edge, x -> edge ++ x ++ edge }

surround("*", "hi")      -- "*hi*"
surround([0], [1, 2])    -- [0, 1, 2, 0]
```

A function that writes its type and joins values of a type variable without `[Append(a)]` is an error that says to add it, and `def join = { x, y -> x ++ y }` is `[Append(a)] a, a -> a`. So `join(1, 2)` is an error at the call: an `int` has no `++`.

These contexts compose naturally. For example, a generic algorithm can require both `Eq(a)` and `Default(a)` by declaring `[Eq(a) & Default(a)]`.

## ToString(a)

`ToString(a)` says how a value of type `a` is written as text. Unlike the contexts above, you don't declare it: the prelude does, and the compiler knows it, because every [interpolation hole](../getting-started/basics#strings) goes through it. `"Total: {total}"` writes `total` with the `ToString` of its type.

```nanyx
context ToString(a) = (
  toString: a -> string
)
```

The compiler has its own `ToString` for `int`, `float`, `bool`, `string` and tags without a payload. A type of your own gets one by attaching a `toString`, which is [synthesized](#context-synthesis) into the context:

```nanyx
type Point = (x: int, y: int)

def Point.toString = { p -> "({p.x}, {p.y})" }

def message = "Moved to {Point(x = 1, y = 2)}"   // "Moved to (1, 2)"
```

A function that interpolates a value of one of its type variables requires `ToString` of it, as it would by calling any function that does. A function that writes its type says so; one that does not has it inferred:

```nanyx
def label: [ToString(a)] (string, a) -> string = { name, value -> "{name}: {value}" }
def angled = { value -> "<{value}>" }   // [ToString(α)] α -> string
```

Such a function can be called with any type that has a `ToString`, the compiler's own included: `angled(3)` is `"<3>"`, and `angled(#red)` is `"<#red>"`. A tag with a payload has one the compiler derives from its parts, so `angled(#some(1))` is `"<#some(1)>"`.

A `use` changes how its block writes a type, generic code called from the block included:

```nanyx
def receipt = {
  def pounds: ToString(int) = (toString = { pence -> "£{pence / 100}.{pence % 100}" })
  use pounds
  "Total: {250}"   // "Total: £2.50"
}
```

The holes in the instance itself are checked before it is loaded, so they write their ints as usual.

A tag with a payload, a tuple, a record and a list have a `ToString` when their parts do. The compiler derives it, writing each part with the `ToString` of its type:

```nanyx
"{#some(Point(x = 1, y = 2))}"   // "#some((1, 2))"
"{[1, 2, 3]}"                    // "[1, 2, 3]"
"{(name = "Ada", age = 36)}"     // "(age = 36, name = Ada)"

def wrapped: [ToString(a)] a -> string = { value -> "{#some(value)}" }
def passed: [ToString(a)] a -> string = { value -> angled([value]) }
```

Each part is written with the `ToString` found where the value is written: in generic code, the one the function was given for its type variable, and in a block with a `use`, the one it loads, so in `receipt` above `"{[250]}"` would be `"[£2.50]"`.

A record's fields are written in alphabetical order. A string is written without quotes wherever it is; [`dbg`](../stdlib/io#dbg) prints a value as you would write it in code.

`println` and the other printing functions require `ToString` of what they print, so `println(Point(x = 1, y = 2))` prints `(1, 2)` without a hole.

A value whose type has no `ToString`, such as a function, or a structure that holds one, is an error at its hole, or at the call to a function that requires `ToString` of it, such as `println`.

# Context synthesis

When a function requires a context, you don't always have to load or create one manually. If the type in question has [attached definitions](./attached-definitions) that match the context's members, the compiler can **synthesize** the context automatically.

For example, given the prelude's `Add` context:

```nanyx
context Add(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
)
```

And a type with matching attached definitions:

```nanyx
type Point = (x: int, y: int)

def Point.zero = Point(x = 0, y = 0)

def Point.`+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }

def Point.`-` = { a, b -> Point(x = a.x - b.x, y = a.y - b.y) }

def Point.negate = { a -> Point(x = -a.x, y = -a.y) }
```

You can call `list.sum` on a `list(Point)` without explicitly loading an `Add(Point)` context -- the compiler sees that `Point.zero`, ``Point.`+` ``, ``Point.`-` `` and `Point.negate` satisfy the context and synthesizes it. The attached `+` is also what `p + q` on two points calls:

```nanyx
def points = [Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)]

def total = points \list.sum
-- total = Point(x = 9, y = 12)
```

This is analogous to how Haskell derives typeclass instances or how Rust resolves trait implementations. The key difference is that in Nanyx the "instance" is just a collection of attached definitions -- there is no separate declaration required.

This is also where the compiler's own `Add(int)` and `Add(float)` come from: the prelude attaches `zero`, `` `+` ``, `` `-` `` and `negate` to `int` and `float`, and a generic function called at `int` is passed those. A concrete expression like `1 + 2` is the built-in operator, not a call.

## When synthesis applies

Context synthesis occurs when:

- A function requires a context parameterized by a concrete type
- That type has attached definitions matching every member of the context
- The types of those definitions are compatible with the context's signatures

If any member is missing or has an incompatible type, synthesis fails and the compiler requires you to load the context explicitly. The error names the definition that is missing, such as `Point.+`, or the one whose type does not fit.

A context with more than one parameter is synthesized from the definitions attached to its first parameter's type. Its other parameters need not be known at the call: the definitions' types settle them. The prelude's [`FromString(a, e)`](../stdlib/standard-library#reading-from-text) is built for `int` from `int.fromString`, whose type makes `e` the tag `#invalidInt`.

Each call looks for its instance in this order, and takes the first it finds:

1. An instance for that type loaded with `use` around the call, the nearest first.
2. An instance the type of the function around the call declares: a generic `def total: [Add(a)] list(a) -> a` passes its own `Add(a)` on to the functions it calls.
3. An instance synthesized from the type's attached definitions.

An instance is for one type: an `Add(int)` loaded with `use` does not stand in for an `Add(Point)`.

Since contexts are structural, a `use` of a record value loads it as an instance of each context it has the shape of, at the types its members settle. With `def addInt: Add(int) = (...)`, `use addInt` loads an `Add(int)`, as `use Add(int)(...)` would.

## Explicit loading takes priority

If you manually load a context with `use`, that instance is always used, even if attached definitions exist. This lets you override the default behavior when needed:

```nanyx
def reverseSum = {
  use Add(Point)(
    zero = Point(x = 0, y = 0)
    `+` = { a, b -> Point(x = a.x - b.x, y = a.y - b.y) }
    `-` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }
    negate = { a -> a }
  )

  points \list.sum
}
```

A `use` of an `Add` reaches generic code called from its block, such as `list.sum`; it does not change a concrete `p + q` there yet ([numeric contexts](../work-in-progress/numeric-contexts#what-is-implemented)).

# Why contexts matter

- Effects are explicit: if a function touches IO or state, it must declare a context.
- You can swap implementations by loading different context instances.
- They enable typeclass-style generic programming without extra syntax.
