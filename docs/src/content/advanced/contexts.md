---
title: "Contexts"
description: "Implicit parameters for effects and typeclasses"
order: 2
---

Contexts are implicit parameters in Nanyx. A function can declare the contexts it needs, and the members of those contexts become available inside its body. This is how Nanyx models effects, state, and typeclass-style constraints.

# Declaring a context

A context definition looks like a record type, but uses the `context` keyword:

```nanyx
context Console = (write: string -> (), writeError: string -> ())
```

A plain context serves for any other whose members it has: a function that requires `Add(a)` is given the `Num(a)` in scope. Declaring it `protected` after its `=`, as the prelude declares `Memory`, makes the context nominal, so that only its home module can create instances; see [Nominal types](./nominal-and-protected-types). A `protected` or `private` context is then supplied only by an instance of itself: a `use` of another context with the same members doesn't serve for it (NYX0205), or any module could forge one. It may still serve for a plain context whose members it has.

```nanyx
context Tx = protected (commit: () -> ())
context Plain = (commit: () -> ())

def needsPlain: [Plain] () -> () = {
  commit()
}

def viaTx: [Tx] () -> () = {
  needsPlain()
} -- a Tx serves for a Plain

def main = {
  use Tx(commit = { println("committed") })
  viaTx()
}
```

A context is not a type. Its name can be written only in a context list, as in `[Log]`, and as the constructor in a `use`, as in `use Log(log = ...)`. So a context can't be a parameter's, a result's or a field's type, or a type argument: `def take = { l: Log -> () }` and `() -> Log` are errors (`NYX0247`). An instance is never a value, so it is written only where a `use` loads it: `def inst = Log(log = ...)` is an error too (`NYX0248`). To reuse one, write a handler, a function that runs a lambda under the `use`:

```nanyx
context Log = (log: string -> ())

def withConsoleLog: [Console] ([Log] () -> a) -> a = { f ->
  use Log(log = { m -> println(m) }) in f()
}

def main = {
  withConsoleLog { log("hello") }
}
```

On a context, `protected` and `private` are about access. Any module can require either in a context list and call the functions that require it, but only the home module can construct an instance in a `use` (NYX0212). Any module can call a protected context's members. Only the home module can call a private context's members (NYX0244), so a private context is a capability: other code can require `[Tx]` and pass it on to the module's functions, but only the module uses what the instance holds.

```nanyx
module db =
  export context Tx = private (commit: () -> ())

  export def query: [Tx] string -> () = { sql ->
    commit()
  }

import db

def report: [Tx] () -> () = {
  query("select 1") -- fine: query is db's
  -- commit() -- NYX0244: only db can call commit
}
```

This differs from leaving out `export`, which stops other modules naming the context at all.

The full form of a context definition is `export scoped context Name(params) = protected ...`, where `export`, `scoped` and the parameters are each optional and the modifiers come in that order. A `scoped` context belongs to the scope that supplies it, as the prelude's `Memory` belongs to a `memory` block, and nothing that requires it may leave that scope:

```nanyx
export scoped context Memory = protected ()
```

A scoped context must be either `protected` or `private`, so that each of its instances comes from the function its module provides to supply it: `scoped context Tx = (...)` is an error. That function loads an instance with `use` to call what it supplies, as `memory` does with `use Memory() in block()`: a parameter whose type requires the context doesn't give the function one, so calling it without the `use` is an error (NYX0211). Any module may declare one. What requires a scoped context leaving its scope is NYX0233.

## Types in a scope

A protected or private type can belong to a scoped context's scope: its declaration writes the context before its kind. Each value of the type belongs to the scope of the context it was made under, so it can't leave the call that supplies that context, and neither can anything holding it:

```nanyx fragment
export scoped context Tx = private ()

export type Cursor = [Tx] protected (query: string, row: int)

export def transaction: [c] ([Tx, c] () -> a) -> a = { block -> use Tx() in block() }
export def open: [Tx] string -> Cursor = { query -> Cursor(query = query, row = 0) }
export def next: [Tx] Cursor -> int = { c -> c.row + 1 }

def row = transaction {
  def c = open("select")
  next(c) -- fine
}

def leaks = transaction {
  def c = open("select")
  c                -- error NYX0233: the result of this 'transaction' call requires its 'Tx' through 'c'
}
```

The type is written and shown as plain `Cursor`: the scope is never written. Making one requires the context, as calling a function that requires it does, and so does using one through functions like `next`; holding or passing one requires nothing. The type can take parameters as usual, as `type MRef(a) = [Memory] protected (...)` does, and the scope isn't one of them. Only a scoped context can be written there, and only one (NYX0252). Contexts written on any other type that isn't a function, or a type generic in contexts (below), as in `x: [Console] int`, are an error (NYX0253).

A protected or private type whose representation, as its declaration writes it, holds a value of a type in a scope must declare that scope's context too, so that a reader can see where its values belong without looking inside: `type MStack(a) = [Memory] private MRef(list(a))`. Leaving it out is an error (NYX0256), and its fix writes the context. Everything a value holds of the scope is then of the value's own scope, so one value belongs to one block. A type argument given where the type is used doesn't count: `list(MRef(int))` and a `Holder(MRef(int))` of `type Holder(a) = protected (item: a)` need nothing, since the scope is visible where they're written, and nor does a function type that requires the context, which holds none of its values.

## Types generic in contexts

A protected or private type can be generic in contexts, as a function type with a context variable is: its declaration writes a context variable before its kind, and the function types in its body write it where they require the contexts. A use writes the contexts in front of the type, or none for the pure one:

```nanyx
type Thunk(a) = [k] protected (get: [k] () -> a)

def pure: Thunk(int) = Thunk(get = { 1 })

def loud: [Console] Thunk(int) = Thunk(
  get = {
    println("got")
    2
  }
)

def force: [c] ([c] Thunk(a)) -> a = { t ->
  t.get()
}

def map: ([c] Thunk(a)), ([d] a -> b) -> [c, d] Thunk(b) = { t, f ->
  Thunk(get = { f(t.get()) })
}

def main: [Console] () -> () = {
  println(force(loud) + force(pure)) -- got, then 3
  println(force(map(pure, { x -> x * 10 }))) -- 10
}
```

A value carries the contexts of the functions it's made with, so `Thunk(get = { 1 })` is a pure `Thunk(int)`. That holds inside a function too: a function that makes a value with a lambda that prints returns a `[Console] Thunk(int)`, and needn't declare `Console` itself, since making the value doesn't run the lambda. Holding or passing a `[Console] Thunk(int)` requires nothing; calling its functions requires `Console` there, as calling any `[Console]` function does, so `force(loud)` in a function that doesn't declare `Console` is an error (NYX0211). A value with fewer contexts fits where one with more is expected, as a pure function does: `pure` can be given for a `[Console] Thunk(int)`, but `loud` can't be given for a `Thunk(int)` (NYX0227). A context variable binds as it does for a function type: `map`'s result carries both `c` and `d`, and since only its result writes them, a call to `map` doesn't require them.

In a list of types without `->`, the contexts in front of the list are its first type's, since a tuple carries none: `() -> ([Console] Thunk(int), int)` returns a `[Console] Thunk(int)` and an `int`. Contexts in front of a bracketed tuple, as in `[Console] (Thunk(int), int)`, are an error (NYX0253).

A type has one context variable, which stands for any number of contexts, and can't belong to a scoped context's scope as well (NYX0252).
# Requiring a context

A function can specify required contexts in its type signature. Here, `sayHello` can call `println` because it runs in `Console`. The real `Console`, which the console host provides to every program, has a few more members, and `println` is a function written on top of its `write`:

```nanyx
def sayHello: [Console] () -> () = {
  println("Hello, world!")
}
```

A context's members are in scope in such a function by their bare names, and so are the definitions
[attached to the context](./attached-definitions#attached-to-a-context), as `start` is wherever `Async` is.

A function whose type lists no contexts requires none, so calling `println` in its body is an error (`NYX0211`). To say on purpose that a function must stay pure, write an empty list, `[]`. It means the same as leaving the list out, and the formatter keeps it:

```nanyx
def double: [] int -> int = { x ->
  x * 2
}
```

# Loading a context

Use the `use` keyword to load an instance of a context into scope. Once loaded, any functions that require it can be called:

```nanyx
context Greeter = (greet: string -> ())

def sayHello: [Greeter] () -> () = {
  greet("world")
}

def main = {
  use Greeter(greet = { name -> println("Hello, {name}!") })

  sayHello()
}
```

A `use` loads a declared context, and its instance is always written there: the context's constructor, as above, or `use Show(int)(...)` for a parameterised one, or a name a lambda gave the instance it is given (see [below](#naming-the-instance-a-lambda-is-given)). Nothing else can be loaded, so loose members are never in scope: `use (write = ...)`, `use someRecord` and `use makeConsole()` are errors (`NYX0249`).

A `use` can implement one context in terms of others, as an effect handler does. A member given at the `use` may need more contexts than its type declares, and those are required where the `use` is written:

```nanyx
context Log = (log: string -> ())

def greet: [Log] string -> () = { name ->
  log("hello {name}")
}

def main = {
  use Log(log = { message -> println(message) }) -- main requires Console here
  greet("world")
}
```

`log` keeps its type, `string -> ()`, so `greet` knows nothing of `Console`, and `message` is a `string`. The member runs with the instances in scope at the `use`, so `use Log(log = { m -> log("[app] {m}") })` calls the `Log` loaded before it.

A function given by its name works the same way, when an instance of each context it needs besides is in scope at the `use`: with `def shout: [Console] string -> ()`, `use Log(log = shout)` runs `shout` with the `Console` there. When one is not, it is an error (`NYX0227`), as it would be anywhere else a function that needs a context is given where one that doesn't is expected.

An instance is always written at its `use`, so that is where what its members need is required. A handler function, as in `withConsoleLog` above, requires it in turn.

You can also load a context for a single expression with `use ... in`. The context is loaded for that expression only, which can start on the next line:

```nanyx
context Prefix = (prefix: string)

def label: [Prefix] string -> string = { text ->
  "{prefix}{text}"
}

def result = use Prefix(prefix = "> ") in label("hello")

println(result)

-- > hello
```

After `in` there can also be a block of statements, on the lines after it and indented more than the line `use` is on. The block's value is the value of `use ... in`, and the context is loaded for the block only:

```nanyx fragment
def main = {
  def total = use Counter(next = { n -> n - 1 }) in
    def a = counted(3)
    a + 10

  println("{total}")
}
```

Don't put the block in braces: `{ ... }` is a function of no arguments, so `use Counter(...) in { ... }` makes a function instead of running the statements. If the function uses the context, that is an error, as the next section says; if it doesn't, the official linter's [`useInBraces`](../standard-tools/official-linter#use--in-with-a-body-in-braces) rule warns about it.

## Naming the instance a lambda is given

A lambda can name the instance of a context it is given by writing `context` and a name before its parameters. The name can only be loaded again, with `use l` or `use l in ...`, which brings back the members as the lambda was given them, for instance after a `use` inside it has shadowed them:

```nanyx
context Log = (log: string -> ())

def inner: [Log] () -> () = {
  log("from inner")
}

def run: [Log] () -> () = { context l ->
  use Log(log = { m -> log("[shadowed] {m}") })
  inner() -- [shadowed] from inner
  use l
  inner() -- from inner, through run's own Log
}
```

The name isn't a parameter: the lambda's type and the number of arguments it takes are the same as without it. When the lambda is given several contexts, the name says which, as in `{ context m: Memory, context l: Log, x -> ... }`, and a name that matches none, or could match more than one, is an error (`NYX0246`). A lambda is given the contexts its def's type declares, or that the type it is checked against requires, as a handler's lambda is given the context the handler supplies. A `memory` block is given its `Memory`, so `memory { context m -> ... }` names it, and `use m` in an inner block makes the cells declared after it belong to `m`'s block ([Memory](../advanced/memory#values-cannot-escape-memory-scope)). An instance is never a value, so using the name any other way, passing it, returning it or storing it, is an error (`NYX0245`). An inner lambda may still capture the name, to `use` it.

An instance of a parameterised context names the type it is for after the context's name, `use C(T)(members)`:

```nanyx
context Show(a) = (show: a -> string)

def describe: [Show(a)] a -> string = { v ->
  Show(a).show(v)
}

def main = {
  use Show(int)(show = { n -> "#{n}" })
  describe(42) -- "#42"
}
```

The type can be left out where the rest of the def settles it, `use C(members)`: the instance is the one the block's
calls need, or the one the members are called at. A handler that pushes each value into a list it builds loads the
instance at the builder's own element type, whichever type that is where the builder is called:

```nanyx
context Pushed(a) = (push: a -> ())

def gather: [c] ([c, Pushed(b)] () -> ()) -> list(b) = { block ->
  memory {
    mut found = []
    use Pushed(push = { x -> found := [x, ...found] }) in block()
    list.reverse(found)
  }
}

println(gather { push(1) }) -- [1]
```

A handler can give its lambda an instance of a parameterised context too. The lambda is given it at the types its parameter's type writes, so a context with several instances for one type, such as a monoid, is chosen by the handler a block is passed to:

```nanyx
context Monoid(a) = (empty: a, combine: (a, a) -> a)

def reduce: [Monoid(a)] list(a) -> a = { xs ->
  xs \fold(empty) { acc, x -> combine(acc, x) }
}

def additive: ([Monoid(int)] () -> b) -> b = { f ->
  use Monoid(int)(empty = 0, combine = { x, y -> x + y }) in f()
}

def multiplicative: ([Monoid(int)] () -> b) -> b = { f ->
  use Monoid(int)(empty = 1, combine = { x, y -> x * y }) in f()
}

def main = {
  println(additive { reduce([1, 2, 3, 4]) }) -- 10
  println(multiplicative { reduce([1, 2, 3, 4]) }) -- 24
}
```

# Contexts are part of the type

A value's context requirement is part of its type. You can annotate just the contexts and let the rest infer:

```nanyx
def greet: [Console] = {
  println("Hi")
}
```

A function that writes its type runs only in the contexts the type declares, so calling `println` from a `def f: int -> int` is an error. A function that does not write its type needs whichever of the host's contexts its body uses, and so does any function that calls it. A script's statements run in the contexts the host provides, such as `Console`.

Passing a function as a value needs its contexts where it runs, not where it is named. Given `def useFoo: [Foo] string -> string` and an `apply` that calls the function it is given, `apply(useFoo, "hi")` is an error unless `Foo` is available at that call, from the type of the function around it or from a `use`. But `def later = useFoo` is fine anywhere: calling `later` needs `Foo`.

A function's type says which contexts it requires, and a lambda's type says which ones it uses: `{ s -> bar(s) }` is a `[Foo] string -> string`. Calling it requires `Foo`, as calling a function that declares `[Foo]` does, and it can't be given where a pure function is expected (`NYX0227`). A higher-order function takes such a lambda with a context variable, as `list.map` does (see [Parametric contexts](../tour/lambdas-and-higher-order-functions#parametric-contexts)). A function named as a value, such as `useFoo` above, has its contexts in its type too. So does a plain context's member named as a value: with `context Holder = (get: () -> int)`, `get` written without a call is a `[Holder] () -> int`, which calls the `get` of the `Holder` where it is called, not the one where it was named. A function that gives back a member as a value says so in its type, `def wrap: [Holder] () -> ([Holder] () -> int) = { get }`, and nothing of an instance outlives its `use` through it. A parameterised context's member is the same: `ToText(int).textlen` written without a call is a `[ToText(int)] int -> int`, so a function it is passed to takes it with a context variable, as `apply: [c] ([c] int -> int), int -> int` does. So does one that doesn't write its type, for the contexts the host provides that it was inferred to require: with `def shout = { s -> println(s) }`, the composition `def greet = { n -> "hi " ++ n } >> shout` is a `[Console] string -> ()`, which takes the `Console` in scope wherever it is called.

Branches that give functions requiring different contexts join as a function that may require any of them, as the
elements of a list do: `if b then { 1 } else { println("y"); 2 }` is a `[Console] () -> int`, and so is each element of
`[{ 1 }, { println("y"); 2 }]`. A function that requires fewer contexts is given where one that may require more is
expected, so a list of contestants for [`race`](./async#racing) can each need contexts of their own.

A lambda checked against a function type that requires a context can call that context's members, as one passed for a parameter of that type can: a lambda given for a record field of type `[Log] string -> ()`, as in `Logger(write = { s -> log(s) })`, or given back by a function whose result is a `[Log] string -> ()`. The function around it doesn't need `Log`: whoever calls the lambda supplies it.

The contexts a function value requires are the ones in scope where it is called: calling a `[Foo] () -> string` inside a `use Foo(...)` uses that `Foo`, whichever `Foo` was in scope where the function was made or named. Each call passes the function the instances in scope there:

```nanyx
context Foo = (bar: string -> string)

def run: [c] ([c] () -> string) -> string = { f ->
  use Foo(bar = { s -> "inner " ++ s }) in f()
}

def main: [Foo] () -> string = {
  run({ bar("x") }) -- "inner x"
}
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
  use Foo(bar = { s -> s ++ "!" }) in println(escaped())   -- hi!
  println(escaped())                                       -- error NYX0205: requires 'Foo'
}
```

A result type that leaves the context out, `[Foo] string -> () -> string`, is an error (`NYX0227`). A lambda that uses only a value computed with the context requires nothing, so `def t = bar(s)` followed by `{ t }` is a `() -> string`.

The same goes for a parameterised context. A function that does not write its type, and calls a function that requires `Show(a)` with a value of one of its own type variables, requires `Show` at that variable itself:

```nanyx fragment
def describe: [Show(a)] a -> string = { v ->
  Show(a).show(v)
}

def twice = { v -> describe(v) ++ describe(v) } -- [Show(α)] α -> string
```

Each call to `twice` then finds a `Show` for the type it is called with, as a call to `describe` does. A generic local `def` that does not write its type is inferred to require it the same way.

This is also how scoped mutation is enforced: values that still require `memory` cannot escape into code that does not provide that context. See [Memory: Values cannot escape memory scope](../advanced/memory#values-cannot-escape-memory-scope).

A type can also belong to a scoped context's scope, as [Types in a scope](#types-in-a-scope) describes, or be generic in contexts, as [Types generic in contexts](#types-generic-in-contexts) does. No other type carries contexts: a record, a tuple, a list or a tag that holds a lambda doesn't take on the lambda's contexts. Each function inside it keeps its own, and calling it requires them where it's called:

```nanyx
def handlers = (log = { (m: string) -> println(m) }, count = { (n: int) -> n + 1 })

def pureCaller: () -> int = {
  handlers.count(1) -- fine: count requires nothing
}
```

Holding or passing `handlers` requires nothing, and building it doesn't run its lambdas, so a top-level value like it needs no context to be computed. Calling `handlers.log` in a function that doesn't declare `Console` is an error (`NYX0211`).

# Combining contexts

You can require several contexts by listing them, separated by `,`:

```nanyx fragment
def readAndPrintFile: [Console, FileSystem] string -> () = { path ->
  def contents = openFile(path).contents
  println(contents)
}
```

# Console by default

The entry function (the one marked `@entry`, see [Attributes](../tour/attributes)) runs in `Console`, which is why `println` is available in `main` without extra setup: the console host, which runs command-line programs, provides it, with `Env` and `FileSystem` (see [Hosts](./hosts)). `println`, `print`, `eprintln` and `eprint` take any value with a [`ToString`](#tostringa): `println(42)` prints `42`, and `println([1, 2])` prints `[1, 2]`.

# Contexts as typeclasses

Contexts can also express typeclass-style constraints. Define a context with the required operations and use it as a constraint:

```nanyx
context Combine(a) = (empty: a, combine: (a, a) -> a)

export def combineAll: [Combine(a)] list(a) -> a = { items ->
  items \fold(empty, combine)
}
```

Inside `combineAll`, `empty` and `combine` are the members of the instance it is given, so it combines whatever type it is called with, as long as that type has a `Combine`.

A call finds the instance from the types of its arguments and its result, so a type variable a context is applied to must also be the type of a parameter or of the result. `[ToString(a)] string -> ()` is an error (`NYX0236`): nothing a call passes says what `a` is.

The operators read through contexts of the prelude in the same way: `x + y` on values of a type variable calls the `` `+` `` of [`Add`](#adda), `x - y` the `` `-` `` of [`Sub`](#suba), `x ++ y` the `` `++` `` of [`Append`](#appenda), and `x == y` the `` `==` `` of [`Eq`](#eqa). On an `int`, a `float`, a `string` or a `bool`, an operator is always the built-in one, as `++` is on a list, so a counter's `n + 1` in a generic function still adds ints.

## Operators find their context

An operator reads the context member of its name (prefix `-` reads `negate`), and its context is the prelude's context with the fewest members that has that member. `Add` and `Num` both have a `` `+` ``, and `Add` is the smaller, so `+` is read through `Add`; in the same way `-` and prefix `-` are read through `Sub`, and only `Num` has a `` `*` ``. Only the prelude's contexts are looked at: a context of your own with a `` `+` `` member is required by name.

So every member of the prelude's contexts has exactly one smallest context. A context that repeats a member of another is larger or smaller than it, never the same size, and a smaller one takes the operator over in every function whose type is inferred. The compiler checks this when it checks the prelude, and reports a tie as `NYX0006`.

A function that does not write its type requires the contexts its operators need, of each of its own type variables they are used on:

```nanyx
def add = { x, y -> x + y } -- [Add(a)] a, a -> a
def lerp = { a, b, t -> a + (b - a) * t } -- [Num(a)] a, a, a -> a
def inc = { x -> x + 1 } -- int -> int: `1` is an int
def diff = { x, y -> x + y - x } -- [Add(a), Sub(a)] a, a -> a
```

Adding, subtracting and multiplying values of one type needs `Num` alone, which has every member of `Add` and `Sub`. A function inside another function that doesn't write its type requires no contexts, so its arithmetic is on ints or floats. A top-level function passed as a value keeps its contexts: `add` in `xs \list.fold(0.0, add)` is passed with the `Add(float)` found where it is named.

Operators on two of a function's type variables need the contexts of each, and each operator calls the member of its own variable's instance, though both members have one name:

```nanyx
def both: [Add(a), Add(b)] a, a, b, b -> (a, b) = { x, y, p, q ->
  (x + y, p + q)
}

def r = { x, y, p, q -> (x + y, p * q) } -- [Add(a), Num(b)] a, a, b, b -> (a, b)
```

`both(1, 2, 1.5, 2.5)` adds the ints with `Add(int)` and the floats with `Add(float)`.

Where a function's operators don't fit one context, its type lists the contexts they need: `[Ord(a), Add(a)]`. A context list holds contexts and context variables only; requiring an operator by its member, as in `` [`+`: a, a -> a] ``, is an error (`NYX0251`) whose fix writes the context that has it, `Add(a)`.

## Associated types

A context can have a member whose value is a type, an associated type, written `name: type`. Its other members name it as a type:

```nanyx fragment
context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)
```

The element type follows from the container's, so a function constrains only the container. Without an associated type, it would be a second parameter, `Iter(c, a)`, written in every signature that uses the context.

A signature names the associated type `Iter(c).elem`, or just `elem` when no other context in the list has one of that name:

```nanyx fragment
def headOr: [Iter(c)] c, elem -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}

def firstPair: [Iter(c), Iter(d)] c, d -> #some((Iter(c).elem, Iter(d).elem)) | #none = { xs, ys ->
  match Iter(c).next(xs), Iter(d).next(ys)
    | #some(x, _), #some(y, _) -> #some((x, y))
    | _, _ -> #none
}
```

When two contexts in the list have one of that name, the bare name is an error (NYX0266) that shows the qualified forms. `Iter(d).elem` where the list doesn't require `Iter(d)`, or a name the context doesn't have, is an error too (NYX0267). An associated type need not be the type of a parameter or of the result, as a context's own parameters must (NYX0236): the instance fixes it, so `def countAll: [Iter(c)] c -> int` is fine.

A constraint fixes an associated type with a named argument, as a call names one:

```nanyx fragment
def joinAll: [Iter(c, elem = string)] c -> string = { items ->
  match next(items)
    | #some(item, rest) -> item ++ joinAll(rest)
    | #done -> ""
}
```

Naming a member the context doesn't have, or one that isn't a type, as in `Iter(c, next = int)`, is NYX0267.

An associated type is one of the function's type variables like any other, so a context the function needs of it is written in its type too. Interpolating an element, comparing two with `==`, adding them with `+` or passing one to a function that requires a context of it needs `ToString(elem)`, `Eq(elem)`, `Add(elem)` or that context in the list, and leaving it out is NYX0205, with a fix that adds it. A function that doesn't write its type infers them, as it does for its other variables.

```nanyx fragment
def iterate: [Iterable(i), ToString(elem), Console] i -> () = { seq ->
  for seq | item ->
    println("Got item: {item}")
}
```

An instance's associated type is what its members' types make it. An instance [synthesized](#context-synthesis) from a type's attached definitions finds `Bag.next` by name, and its type, `Bag -> #some(int, Bag) | #done`, makes the `elem` of `Iter(Bag)` an `int`. An instance a `use` loads is the same, and a `use` can fix one by name too, as in `use Sized(Bag, elem = int)(...)`. Where no member's type mentions an associated type, the type declares it with an alias attached to it:

```nanyx fragment
context Sized(c) = (elem: type, size: c -> int)

type Bag = protected (items: list(int))

def Bag.size: Bag -> int = { bag -> bag.items.length }

type Bag.elem = int
```

An instance that leaves an associated type unfixed is an error that names the instance and the associated type (NYX0268), and so is one whose members fix it two different ways (NYX0269). There are no defaults: a context can't give an associated type one (NYX0265), so an instance never gets a type its author didn't write or imply. An associated type's name is lowercase and longer than one letter, which [type names](../tour/types#type-names) reserve for associated types, and a built-in type's name, such as `list`, can't be one (NYX0265). `type` is the type of an associated type and nothing else, so `(x: type)` elsewhere is NYX0265 too: a function's parameter that takes a type is a [type argument](../tour/functions#type-arguments), written with its type, `Type(a)`. (As a function's named parameter, `into: type` is the old form of `into: Type(into)`: it still compiles, with a warning, NYX0282.)

Associated types cost nothing at run time: types are erased, and a generic function is specialised at each instance as usual, at its associated types too, though its type needn't write them.

An associated type is also a [type argument](../tour/functions#type-arguments): `type Iter(c).elem`, or `type elem` where the bare name would do in the signature, is a value of type `Type(elem)`, so a generic function can pass its element type on to a function that takes one:

```nanyx fragment
def describe: [Named(a)] Type(a) -> string = { t -> nameOf(t) }

def describeElements: [Iterable(c), Named(elem)] c -> string = { xs -> describe(type Iterable(c).elem) }
```

# Common contexts

In practice, a small set of capability contexts appears frequently in generic APIs.

Four of them, `Eq`, `Ord`, `Hash` and `ToString`, are known to the compiler by name. It reads syntax through them (`==` and `!=`, the ordering operators, and interpolation holes), and it derives their instances from a type's structure: an `Eq` of a record from the `Eq` of each field. No other context is derived from structure, and a library can't add one to the list: its own contexts are [synthesized](#context-synthesis) from attached definitions by name, loaded with `use`, or required by a signature.

## Eq(a)

`Eq(a)` is the standard library's context for equality. `x == y` is read through it, and `x != y` is `not (x == y)`.

```nanyx
context Eq(a) = (`==`: (a, a) -> bool)
```

Every type that holds no function has an `Eq`: the compiler compares records field by field, lists element by element, and tags by name and then payload. Functions have no equality, so comparing two functions, or two values that hold one, is a type error.

A function that compares values of one of its type variables requires `Eq` of it:

```nanyx
def contains: [Eq(a)] list(a), a -> bool = { xs, target ->
  xs \any { == target }
}
```

A function that does not write its type has the requirement inferred: `def same = { x, y -> x == y }` is `[Eq(a)] a, a -> bool`. Comparing a type built from a variable requires `Eq` of the variable, so `==` on two `list(a)` needs `Eq(a)`.

Comparing with a tag doesn't make the other operand that tag: `def isNone = { o -> o == #none }` is `#none | r -> bool`, so `isNone(#some(3))` is `false`, as a `match` would join the two. A function whose type has an open union, as `isNone`'s does, needs no `Eq` written for its rest: each call checks that the tags it passes can be compared. A payload of a tag the union names is compared with its own `==`, both by `==` and when the union is passed to a function that requires `Eq` of it; the tags of the rest are compared by the compiler's own. The official linter [warns](../standard-tools/official-linter#comparison-that-is-always-false) when two closed types have no tag in common, as in `#red == #some(1)`, which is always false.

Floats follow IEEE 754: `0.0 == -0.0` is `true`, and `NaN == NaN` is `false`.

A nominal type can attach its own `==`, which `a == b` on it, and a function that requires `Eq`, then use for it:

```nanyx
type Email = protected string

def Email.`==`: Email, Email -> bool = { a, b ->
  string.lowercase(a) == string.lowercase(b)
}
```

Only a nominal type, protected or private, can: a structural type, such as `type Point = (x: int, y: int)`, and a built-in type always compare by their structure, so attaching `==` to one is an error. `!=` cannot be attached to any type, because it is always `not (a == b)`.

An `==` passes through the structures around it. A tag with a payload, a tuple, a record and a list are compared part by part, each part with its own `==`:

```nanyx fragment
#some(Email("A@x.io")) == #some(Email("a@x.io")) -- true
[Email("A@x.io")] == [Email("a@x.io")] -- true

def bothSome: [Eq(a)] a, a -> bool = { x, y ->
  #some(x) == #some(y) -- compares x and y with Eq(a)
}
```

## Ord(a)

`Ord(a)` is the standard library's context for ordering. `x < y`, `x <= y`, `x > y` and `x >= y` are all read through its one member, so they cannot disagree with each other.

```nanyx
context Ord(a) = (compare: (a, a) -> #less | #equal | #greater)
```

The compiler has an `Ord` for numbers, `bool` (`false` first), strings (by their bytes, which is the order of their code points), and tuples and lists of them, compared position by position, with a list that is a prefix of another first. On floats the operators follow IEEE 754, so every comparison with `NaN` is false, while `float.compare` is a total order with `NaN` after every other float; that is what `list.sort` uses.

Records and tags have no order: a record's fields and a union's tags have no order of their own in its type, so `<` on them is a type error. Sort a list of them by a key with `list.sortBy`, or make the type nominal and attach a `compare`:

```nanyx
type Version = protected (major: int, minor: int)

def Version.compare: Version, Version -> #less | #equal | #greater = { a, b ->
  if a.major != b.major then int.compare(a.major, b.major) else int.compare(a.minor, b.minor)
}
```

As with `==`, only a nominal type can attach `compare`. The built-in types have theirs attached as functions, `int.compare` and `float.compare` among them, but their order is always the compiler's own.

A function that orders values of one of its type variables requires `Ord` of it, `def largest: [Ord(a)] list(a) -> #some(a) | #emptyList`, and one that does not write its type has it inferred: `def larger = { x, y -> if x > y then x else y }` is `[Ord(a)] a, a -> a`. `Ord` does not require `Eq`; a type that attaches both should keep them consistent.

## Hash(a)

`Hash(a)` is the standard library's context for hashing: an `int` for each value, the same for any two values `==` finds equal, so that a map or a set can find a key by its hash. It has no operator; a function that needs it requires it and calls `hash`.

```nanyx
context Hash(a) = (hash: a -> int)
```

The compiler has a `Hash` for every type it has an `Eq` for, which hashes by structure: a value's hash is made from its parts' hashes. Functions have no hash. A nominal type that attaches its own `==` must attach a `hash` that agrees with it, since the compiler's own would not; without one, asking for `Hash` of it is a type error that says so:

```nanyx
type Email = protected string

def Email.`==`: Email, Email -> bool = { a, b ->
  string.lowercase(a) == string.lowercase(b)
}

def Email.hash: Email -> int = { e ->
  string.hash(string.lowercase(e))
}
```

As with `==`, only a nominal type can attach `hash`. The built-in types have theirs attached as functions, `int.hash` and `string.hash` among them, for building a type's own `hash`. A type that holds a part with its own `hash`, such as a list of `Email`s, is hashed through it: each part is hashed with its own `Hash` and the hashes are folded together, so two lists that `==` finds equal have the same hash.

## Default(a)

`Default(a)` is the prelude's context for a fallback value of a type: a function that needs one requires it. Its member is a value, not a function of no arguments: a member that's a constant is written as a value, `default: a`, and read as one, `default`. It may be generic, as `list.default: list(a)` is.

```nanyx
context Default(a) = (default: a)
```

```nanyx
def valueOrDefault: [Default(a)] (#some(a) | #none) -> a = {
  | #some(v) -> v
  | #none -> default
}
```

Each built-in type has one: `int`'s default is `0`, `float`'s `0.0`, `string`'s `""`, `bool`'s `false`, a `list`'s `[]`, and `#some(a) | #none`'s `#none`. The prelude attaches them as `int.default` and the others, and the compiler has its own for `#some(a) | #none`, a union, which has no name to attach one to. Any other type gets one by attaching `default`, which is [synthesized](#context-synthesis) into the context:

```nanyx
type Point = (x: int, y: int)

def Point.default = Point(x = 0, y = 0)
```

A call that requires `Default` of a type with no `default`, such as a function, is an error.

## Add(a)

`Add(a)` is the standard library's context for values that add. `x + y` is read through it.

```nanyx
context Add(a) = (zero: a, `+`: (a, a) -> a)
```

The compiler has its own for `int` and `float`. A function that adds values of one of its type variables requires `Add` of it, and in such a function a `0` added to one of those values is `zero`:

```nanyx
def total: [Add(a)] list(a) -> a = { items ->
  items \fold(zero) { + }
}

def orZero: [Add(a)] a -> a = { x ->
  x + 0
}
```

`list.sum` is `total`. A function that writes its type and adds without `[Add(a)]` is an error that says to add it. One that does not write its type has the requirement [inferred](#operators-find-their-context): `def add = { x, y -> x + y }` is `[Add(a)] a, a -> a`, while `def inc = { x -> x + 1 }` is still `int -> int`. Any other type gets an `Add` by attaching the two members (see [Context synthesis](#context-synthesis)), so a type that adds but doesn't subtract can be summed.

## Sub(a)

`Sub(a)` is the standard library's context for values that subtract. `x - y` is read through it, and so is `-x`, which is its `negate`.

```nanyx
context Sub(a) = (`-`: (a, a) -> a, negate: a -> a)
```

The compiler has its own for `int` and `float`, and any other type gets one by attaching the two members. `Add` and `Sub` share no member, so a function that adds and subtracts values of one type requires both:

```nanyx
def distance: [Add(a), Sub(a)] a, a, a -> a = { start, step, back ->
  start + step - back
}
```

## Num(a)

`Num(a)` is the context for numbers: it has every member of `Add(a)` and `Sub(a)`, and `x * y`, `x / y`, `x // y` and `x % y` are read through it too.

```nanyx
context Num(a) = (
  fraction: type
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
  `*`: (a, a) -> a
  `/`: (a, a) -> fraction
  `//`: (a, a) -> a
  `%`: (a, a) -> a
  fromInt: int -> a
  isInteger: bool
)
```

`x / y` divides with a fraction. Its type is `fraction`, an [associated type](#associated-types) of `Num`: `float` for `int`, the sized integers and `float`, and `F32` for `F32`. So `7 / 2` is `3.5`, and `1 / 0` is infinity, as `1.0 / 0.0` is. An `I64` or `U64` past 2^53 is rounded to the nearest `float` first, as any conversion to `float` rounds it.

`x // y` divides and rounds down, toward negative infinity, and `x % y` is what is left, with the sign of `y`, so that `x == (x // y) * y + x % y` always:

```nanyx
println("{7 / 2} {7 // 2} {7 % 2}") -- 3.5 3 1
println("{-7 // 2} {-7 % 2}") -- -4 1
println("{-7.5 // 2.0} {-7.5 % 2.0}") -- -4.0 0.5
```

A negative number still splits into a whole part and a remainder that is not negative, so `i % length` wraps an index into a range, `-1` to the last. On `float` and `F32`, `//` is the floor of the quotient, as a float, and `%` follows it, as Python's do. On an integer type, `x // 0` and `x % 0` stop the program.

`isInteger` is `true` for an integer type and `false` for `float` and `F32`: `x ** -n` asks it, since a negative power of an integer isn't one. It stops the program on an integer type, and says to write `1 / x ** n`, which gives the fraction. A type of your own whose `fraction` is another type, as when its `/` gives a `float`, has no negative power: `x ** -1` on one is an error ([NYX0292](../standard-tools/errors#nyx0292)), or stops the program where the exponent isn't a literal.

In a function that requires `Num(a)`, an integer literal beside a value of type `a` is `fromInt` of it, so `2` in `x * 2` is an `a`. A decimal literal there is an error, since `a` may be an integer type.

```nanyx
def square: [Num(a)] a -> a = { x ->
  x * x
}

def mean: [Num(a)] list(a) -> fraction = { xs ->
  list.sum(xs) / fromInt(list.length(xs))
}
```

`mean` calls `list.sum`, which requires `Add(a)`: contexts are structural, and a `Num` has every member an `Add` has, so the `Num` it is given serves, as it would for a function that requires `Sub(a)`. Its division gives `fraction`, so `mean([1, 2, 4])` is the `float` `2.333...`, and `mean` of a list of `F32`s an `F32`. A function that doesn't write its type and divides values of a type variable with `/` is inferred to return that variable's `fraction`, and one that divides ints with `/` to return a `float`. A measured type, such as a time, has an `Add` and a `Sub` but no `Num`, because multiplying two times gives no time ([Units of measure](./units-of-measure#arithmetic)).

## Append(a)

`Append(a)` is the context for values that join end to end. `x ++ y` on values of a type variable is read through it.

```nanyx
context Append(a) = (`++`: (a, a) -> a)
```

The compiler has its own for `string` and `list(a)`, where `++` is the built-in operator. Any other type gets one by attaching `` `++` ``:

```nanyx
def surround: [Append(a)] a, a -> a = { edge, x ->
  edge ++ x ++ edge
}

surround("*", "hi") -- "*hi*"
surround([0], [1, 2]) -- [0, 1, 2, 0]
```

A function that writes its type and joins values of a type variable without `[Append(a)]` is an error that says to add it, and `def join = { x, y -> x ++ y }` is `[Append(a)] a, a -> a`. So `join(1, 2)` is an error at the call: an `int` has no `++`.

These contexts compose naturally. For example, a generic algorithm can require both `Eq(a)` and `Default(a)` by declaring `[Eq(a), Default(a)]`.

## ToString(a)

`ToString(a)` says how a value of type `a` is written as text. Unlike the contexts above, you don't declare it: the prelude does, and the compiler knows it, because every [interpolation hole](../getting-started/basics#strings) goes through it. `"Total: {total}"` writes `total` with the `ToString` of its type.

```nanyx
context ToString(a) = (toString: a -> string)
```

The compiler has its own `ToString` for `int`, `float`, `bool`, `string` and tags without a payload. A type of your own gets one by attaching a `toString`, which is [synthesized](#context-synthesis) into the context:

```nanyx
type Point = (x: int, y: int)

def Point.toString = { p -> "({p.x}, {p.y})" }

def message = "Moved to {Point(x = 1, y = 2)}" -- "Moved to (1, 2)"
```

A function that interpolates a value of one of its type variables requires `ToString` of it, as it would by calling any function that does. A function that writes its type says so; one that does not has it inferred:

```nanyx
def label: [ToString(a)] string, a -> string = { name, value ->
  "{name}: {value}"
}

def angled = { value -> "<{value}>" } -- [ToString(α)] α -> string
```

Such a function can be called with any type that has a `ToString`, the compiler's own included: `angled(3)` is `"<3>"`, and `angled(#red)` is `"<#red>"`. A tag with a payload has one the compiler derives from its parts, so `angled(#some(1))` is `"<#some(1)>"`.

A type's own `ToString`, the compiler's own, a derived one or one built from an attached `toString`, can't be replaced with a `use` (see [A type's own instance wins](#a-types-own-instance-wins)), so `"{250}"` is `"250"` wherever it is written. To write a value differently, call a function in the hole:

```nanyx
def pounds: int -> string = { pence ->
  "£{pence // 100}.{pence % 100}"
}

def receipt = "Total: {pounds(250)}" -- "Total: £2.50"
```

A `use` gives a `ToString` to a type that has none of its own, such as a [protected type](./nominal-and-protected-types) without a `toString`, and it reaches generic code called from its block:

```nanyx
type Cents = protected int

def receipt = {
  use ToString(Cents)(toString = { c -> "some cents" })
  "Total: {Cents(250)}" -- "Total: some cents"
}
```

The holes in the instance itself are checked before it is loaded, so they write their values as usual.

A tag with a payload, a tuple, a record, a list, a map and a set have a `ToString` when their parts do. The compiler derives it, writing each part with the `ToString` of its type:

```nanyx fragment
"{#some(Point(x = 1, y = 2))}" -- "#some((1, 2))"
"{[1, 2, 3]}" -- "[1, 2, 3]"
"{(name = "Ada", age = 36)}" -- "(age = 36, name = Ada)"
"{["tea" => 3, "cake" => 1]}" -- "[tea => 3, cake => 1]"
"{set.fromList([3, 1, 3])}" -- "[3, 1]"

def wrapped: [ToString(a)] a -> string = { value ->
  "{#some(value)}"
}

def passed: [ToString(a)] a -> string = { value ->
  angled([value])
}
```

Each part is written with the `ToString` found where the value is written: in generic code, the one the function was given for its type variable, and in a block with a `use`, the one it loads, so in the second `receipt` above `"{[Cents(250)]}"` would be `"[some cents]"`.

A record's fields are written in alphabetical order. A string is written without quotes wherever it is; [`dbg`](../stdlib/console#dbg) prints a value as you would write it in code, and ignores any `ToString`. The compiler does not look inside an alias or a nominal type that is a part: such a part is written with its own `ToString`. An empty list, `"{[]}"`, writes `[]`. A map's entries and a set's elements are written in the order `map.toList` and `set.toList` give them, the order of their hashes, and the empty map writes `[=>]`.

`println` and the other printing functions require `ToString` of what they print, so `println(Point(x = 1, y = 2))` prints `(1, 2)` without a hole.

A [private type](./nominal-and-protected-types#private-opaque-types) such as `UserId` can be written only if its module exports a `UserId.toString`. Outside its module the type has no shape to write, so the module chooses how its values are shown, as it chooses everything else that can be done with them.

A value whose type has no `ToString`, such as a function, a private type without a `toString`, or a structure that holds one, is an error at its hole, or at the call to a function that requires `ToString` of it, such as `println`. So is a union open to tags a caller chooses, `#x(a) | r` in a function's type, since those tags may be any: name every tag in the type, or match on the tags it names.

## Typed(a)

`Typed(a)` is the prelude's context for the type `a` as a value: its one member, `typeOf`, is a [type argument](../tour/functions#type-arguments) of type `Type(a)`. Every type has the compiler's own instance, nominal types of other modules too, so a function that is given a value of type `a` can require `Typed(a)` and pass `typeOf` on wherever `type a` is expected, and its callers pass nothing more. A function generic in `a` requires `Typed(a)` to pass it on, as it would any context.

# Context synthesis

When a function requires a context, you don't always have to load or create one manually. If the type in question has [attached definitions](./attached-definitions) that match the context's members, the compiler can **synthesize** the context automatically.

For example, given the prelude's `Add` context:

```nanyx
context Add(a) = (zero: a, `+`: (a, a) -> a)
```

And a type with matching attached definitions:

```nanyx
type Point = (x: int, y: int)

def Point.zero = Point(x = 0, y = 0)

def Point.`+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }
```

You can call `list.sum` on a `list(Point)` without explicitly loading an `Add(Point)` context -- the compiler sees that `Point.zero` and ``Point.`+` `` satisfy the context and synthesizes it. The attached `+` is also what `p + q` on two points calls:

```nanyx fragment
def points = [Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)]

def total = points \list.sum
-- total = Point(x = 9, y = 12)
```

This is analogous to how Haskell derives typeclass instances or how Rust resolves trait implementations. The key difference is that in Nanyx the "instance" is just a collection of attached definitions -- there is no separate declaration required.

This is also where the compiler's own `Add(int)`, `Sub(int)` and the rest come from: the prelude attaches `zero`, `` `+` ``, `` `-` ``, `negate` and `Num`'s other members to `int` and `float`, and a generic function called at `int` is passed those. A concrete expression like `1 + 2` is the built-in operator, not a call.

## When synthesis applies

Context synthesis occurs when:

- A function requires a context parameterized by a concrete type
- That type has attached definitions matching every member of the context
- The types of those definitions are compatible with the context's signatures

If any member is missing or has an incompatible type, synthesis fails and the compiler requires you to load the context explicitly. The error names the definition that is missing, such as `Point.+`, or the one whose type does not fit.

An attached definition may require contexts of its own. A generic type's `==`, `hash` or `toString` usually does, since it reads the values the type holds: `def Box.toString: [ToString(a)] Box(a) -> string` is the `ToString(Box(int))` wherever one is needed, in a string hole, inside a list, or passed to a function that requires it, and is passed the `ToString(int)` it requires, found as a call to it would find it.

A context with more than one parameter is synthesized from the definitions attached to its first parameter's type. Its other parameters need not be known at the call, and nor need its [associated types](#associated-types): the definitions' types settle them. The prelude's [`FromString(a)`](../stdlib/prelude-contexts#reading-from-text) is built for `int` from `int.fromString`, whose type makes its `failure` the tag `#invalidInt`.

Each call looks for its instance in this order, and takes the first it finds:

1. The type's own instance: synthesized from its attached definitions, or, for `Eq`, `Ord`, `Hash` and `ToString`, derived from its parts or the compiler's own. A [private type](./nominal-and-protected-types#private-opaque-types) has none of these derived: only what it attaches, so a structure that holds one has `Eq` and `Hash` derived from its parts only when it attaches `==` and `hash`.
2. An instance in scope: one loaded with `use` around the call, the nearest first, or one the type of the function around the call declares. A generic `def total: [Add(a)] list(a) -> a` passes its own `Add(a)` on to the functions it calls.

An instance is for one type: an `Add(Size)` loaded with `use` does not stand in for an `Add(Point)`. A call whose type nothing at the call settles finds its instance once something does, such as the type its def writes: `def s: string = use Monoid(int)(...) in empty()` needs a `Monoid(string)`, and is an error with only a `Monoid(int)` in scope. One whose type nothing settles at all is an error too, though an instance of the context is in scope.

## A type's own instance wins

A `use` can't replace a type's own instance. Loading one for a type that has its own is an error at the `use` (NYX0250), so `1 == 2` is false, `"{42}"` is `"42"` and `1 + 2` is `3` wherever they are written, and a set built in one place can't be read with another `Hash` somewhere else:

```nanyx fragment
def same: [Eq(a)] a, a -> bool = { x, y -> x == y }

def main = {
  use Eq(int)(`==` = { x, y -> true })
  -- error NYX0250: 'int' already has `==`, which a `use` can't replace: for a different equality, wrap
  -- the value in a protected type with its own `==`, or pass the comparison to a `...By` function
  same(1, 2)
}
```

A different behaviour comes from a type that says so, or from a function given it:

- **A protected type with its own definitions**, such as the `Email` above with its own `==`. Its type says which equality it has wherever it goes, so a set of them is always consistent.
- **A function given what to do**, for a one-off: `list.sortBy` with a key to order by, or a function called in a hole for text.

A `use` still gives an instance to a type that has none of its own, such as `Eq` for a record that holds a function, or `Add` for a type that attaches no `+`, and it still loads contexts that aren't for a type, such as `Console` and `Log`. A tag, tuple or record that holds such a type is then compared through it too: with `Eq(Button)` loaded, `#some(ok) == #some(cancel)` compares the two buttons with the `==` the `use` gives.

# Why contexts matter

- Effects are explicit: if a function touches IO or state, it must declare a context.
- You can swap implementations by loading different context instances.
- They enable typeclass-style generic programming without extra syntax.
