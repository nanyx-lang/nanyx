---
title: "result"
description: "Composing success/error values"
order: 6
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
Result.bind: (#some(a) | #error(e), (a -> (#some(b) | #error(f)))) -> #some(b) | #error(e | f)
```

Chains result-producing operations. The error cases of both steps are preserved in the result, so the caller can still distinguish them.

```nanyx
readLine()
  \Result.bind(parseInt)
-- #some(int) | #error(IoError | #invalidInt)
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
Result.mapOr(#some(21), 0) { * 2 }
-- 42

Result.mapOr(#error("bad"), 0) { * 2 }
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
