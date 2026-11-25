---
title: "The parser"
description: "What is still open in the parser: recovering inside an item, and a concrete syntax tree"
order: 34
---

How the lexer, the layout pass and the recursive-descent parser work, and how the parser reads past a mistake to the
next top-level item, is in [The parser](../deep-dives/parser) in the Deep dives section. This page keeps what is still
open.

# Recovering inside an item

After a mistake, the parser leaves out the top-level item the mistake is in and reads on at the next one. It could
recover inside an item too, at the next statement of a block or the next arm of a match, with an error node in the
tree where the mistake was, so that the rest of the def being typed keeps its types. The type checker and the
language server would then learn to skip error nodes. Until then the language server reads the last text that parsed
for the item being typed.

It is also where syntax errors can get better. A syntax error the parser has no message of its own for says what it
expected at the furthest point it read to and what it found there, `Expected `]`, found `xs``. Where several readings
failed at that point, the list of what they expected can be long.

# A concrete syntax tree

Comments and whitespace travel with the lexer's tokens, so the formatter and the refactorings could work from a tree that
keeps every token and its trivia, instead of re-attaching comments by position. That is to be decided once the
[refactorings](./refactorings) show whether they need one.
