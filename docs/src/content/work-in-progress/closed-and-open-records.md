---
title: "Closed and open records"
description: "What is still open about closed and open record types"
order: 23
---

Closed and open records are built. The language docs describe them in
[Closed and open records](../tour/records#closed-and-open-records). This page keeps the open question. It is part of
the [compilation](./compilation) workstream, and its issues carry the `compilation` and `type checker` labels.

# Open questions

- **Spread of an open record.** Record spread works on records whose fields are all known where the spread is
  checked (`RecordSpread.fs`); a spread of an open record, or of a value whose type is not known yet, is `NYX0231`.
  `(...p, z = 1)` where `p` is `(x: int, ...r)` could be `(x: int, z: int, ...r)`, unless `r` has a `z`. Whether a
  spread may override a field that is only in a row needs deciding.
