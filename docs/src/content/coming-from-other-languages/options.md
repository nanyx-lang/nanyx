---
title: "Option types"
description: "Using explicit tags instead of a generic Maybe"
order: 1
---

Many languages use a null reference (`null`, `nil`, `None`, or `undefined`) to represent missing data, the downside of which is well-known. 

What makes Nanyx different is that it also doesn't have a single, universal Option/Maybe type. Instead, it encourages a convention with explicit tag unions to make missing data precise and composable.

# Prefer results for failing operations

When an operation can fail, Nanyx conventions favor a result-like union with a specific error tag. That is more descriptive than a plain optional value, but it still composes without conversion helpers.


There is no `Option(a)` alias in the standard library; the standard library provides a `Result(a, e)` alias for `#some(a) | #error(e)`, but everything else follows a convention where any union type containing `#some(a)` can be treated as an [option-like type](../advanced/option-like-types), whatever its other tags are.

```nanyx
def list.head = {
  | [] -> #emptyList
  | [x, ...] -> #some(x)
}
```

If all failing operations use result-like unions, you avoid having to map between `Result` and `Option` representations.

# Optional record fields

Optional fields can be represented explicitly with tag unions or by using optional field syntax. This keeps intent clear and avoids a generic wrapper type where it adds little value.

# Use explicit unions for richer states

For data that is neither a simple failure nor a missing field, explicit tags are more informative than a generic `Option`:

```nanyx
artist: #loading | #some(Artist)

artist: #unspecified | #some(Artist)
```

Both mean “you might not have an artist,” but the tags explain why not and what to expect next.

# Easy evolution

Explicit unions make it easy to evolve the model over time. If you later need an error state, you can add it without invalidating the existing tags:

```nanyx
artist: #loading | #loaded(Artist) | #errored(LoadError)
```

Code that already handles `#loading` and `#loaded` keeps working, and the new case is explicit.

# Convention over a single type

Nanyx still supports option-like values via the `#some` convention and functions like `mapSome` that preserve other tags:

```nanyx
def mapSome
  : ((#some(a) | r), (a -> b)) -> (#some(b) | r)
  = { | #some(a), f -> #some(f(a))
      | other, _ -> other }
```

This gives you the ergonomics of options without forcing every absence into a single type.
