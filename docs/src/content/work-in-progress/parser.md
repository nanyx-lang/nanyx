---
title: "The parser"
description: "What is still open in the parser after the move from FParsec to a lexer and a recursive-descent parser"
order: 34
---

The parser moved from FParsec combinators to a lexer, a layout pass and a hand-written recursive-descent parser in six
stages, tracked in [#846](https://github.com/nanyx-lang/nanyx/issues/846). All six are done: the corpus and the
differential test, the lexer, the layout pass, the new parser a part of the grammar at a time, the new parser as the
default, error recovery at the top level, and the removal of FParsec. How the parser works now is
[The parser](../deep-dives/parser) in the Deep dives section. This page is what is still open.

# Recovering inside an item

After a mistake, the parser leaves out the top-level item the mistake is in and reads on at the next one. It could
recover inside an item too, at the next statement of a block or the next arm of a match, with an error node in the
tree where the mistake was, so that the rest of the def being typed keeps its types. The type checker and the
language server would then learn to skip error nodes. Until then the language server reads the last text that parsed
for the item being typed.

# A concrete syntax tree

Now that comments and whitespace travel with tokens, the formatter and the refactorings could work from a tree that
keeps every token and its trivia, instead of re-attaching comments by position. That is to be decided once the
[refactorings](./refactorings) show whether they need one.
