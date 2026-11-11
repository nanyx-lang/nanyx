---
title: "result"
description: "Composing success/error values"
order: 8
---

The `Result` helpers work on values represented as `#some(a) | #error(e)`, the `Result(a, e)` type. They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed.

# map

```nanyx
Result.map: (#some(a) | #error(e), (a -> b)) -> #some(b) | #error(e)
```

Transforms successful values and leaves errors unchanged.

```nanyx
#some(21) \Result.map { * 2 }
-- #some(42)
```

---

# mapError

```nanyx
Result.mapError: (#some(a) | #error(e), (e -> f)) -> #some(a) | #error(f)
```

Transforms error values.

```nanyx
#error("bad") \Result.mapError { e -> "ERR: {e}" }
-- #error("ERR: bad")
```

---

# bind

```nanyx
Result.bind: (#some(a) | #error(e), (a -> (#some(b) | #error(e)))) -> #some(b) | #error(e)
```

Chains result-producing operations. Both steps must have the same error type; to chain a step whose errors differ, bring them to one type with `mapError` first.

```nanyx
def parse: string -> Result(int, string) = { s ->
  Int.fromString(s) \toResultOption("not a number: {s}")
}

def half: int -> Result(int, string) = { n ->
  if n % 2 == 0 then #some(n / 2) else #error("odd: {n}")
}

parse("42") \Result.bind(half)
-- #some(21)

parse("7") \Result.bind(half)
-- #error("odd: 7")
```

---

# defaultValue

```nanyx
Result.defaultValue: ((#some(a) | #error(e)), a) -> a
```

Returns the success value or fallback.

```nanyx
Result.defaultValue(#error("x"), 0)
-- 0
```

---

# mapOr

```nanyx
Result.mapOr: ((#some(a) | #error(e)), b, (a -> b)) -> b
```

Maps the success value or returns a default for an error.

```nanyx
#some(21) \Result.mapOr(0) { * 2 }
-- 42

#error("bad") \Result.mapOr(0) { * 2 }
-- 0
```

---

# toOption

```nanyx
Result.toOption: (#some(a) | #error(e)) -> #some(a) | #none
```

Keeps the success value and drops the error.

```nanyx
Result.toOption(#error("bad"))
-- #none
```

---

# orElse

```nanyx
Result.orElse: ((#some(a) | #error(e)), (e -> (#some(a) | #error(f)))) -> #some(a) | #error(f)
```

Recovers from an error: the fallback gets the error and gives another result. A success is left as it is.

```nanyx
readConfig(path)
  \Result.orElse { _ -> readConfig(defaultPath) }
```

---

# isOk

```nanyx
Result.isOk: (#some(a) | #error(e)) -> bool
```

```nanyx
Result.isOk(#some(1))
-- true
```

---

# isError

```nanyx
Result.isError: (#some(a) | #error(e)) -> bool
```

```nanyx
Result.isError(#error("oops"))
-- true
```
