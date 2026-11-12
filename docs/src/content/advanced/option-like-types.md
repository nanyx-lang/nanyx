---
title: "Option-like types"
description: "Mapping #some across unions"
order: 9
---

Nanyx is flexible enough that functions can be typed to operate on a tag union whilst specifying only some of the values in the union.

Nanyx has a convention where any tag union that includes `#some(a)` is considered "option-like". The standard library includes an option module that provides functions for working with option-like types, such as a single `map` that works for options, results, and custom unions by preserving all other tags. 

# The core idea

The function below accepts any union that contains `#some(a)` and transforms that value while leaving the rest of the union intact:

```nanyx
def mapSome
  : ((#some(a) | r), (a -> b)) -> (#some(b) | r)
  = { | #some(a), f -> #some(f(a))
      | other, _ -> other }
```

The type variable `r` represents all other tags in the union, allowing us to use it in an output position in the function type.

# Works with options

```nanyx
def name: #some(string) | #none = ...

def upper = name \mapSome { .toUpperCase }
```

# Works with results

```nanyx
def value: #some(int) | #error(#notFound | #dbDown) = ...

def doubled = value \mapSome { * 2 }
```

The error cases stay in the union, so you don’t lose information.

# Why this matters

- A shared `#some` convention lets you abstract across option and result-like types.
- You keep precise failure tags instead of collapsing them into a single error.
- You get reuse: one `map` works for many domain-specific unions.
