---
title: "Language server and editor"
description: "What is still open about the language server built into nanyx, and the VS Code extension"
order: 30
---

> What the language server does is described for users on
> [VS Code extension](../standard-tools/vs-code-extension), and its options on
> [The nanyx command](../standard-tools/command-line#editors). This page keeps what is [still open](#still-open).
> Work is tracked under the `lsp` label.

The language server is built into the `nanyx` binary (`nanyx lsp`), in `compiler/Nanyx.LanguageServer`, and calls the
compiler as a library: each document is compiled after a pause in typing, and hover, completion, the type hints and
the rest read that compile. The VS Code extension in `extension/` finds or downloads `nanyx` and starts it. Each
feature is one handler class in `Nanyx.LanguageServer/Handlers/`, and the tests in `Nanyx.LanguageServer.Tests` talk
to the server over the real wire protocol.

# Still open

- **Exact ranges.** The syntax tree keeps no span for a record field's name, so a diagnostic about one is reported at
  the nearest node that has a span, and semantic tokens read field names from the text.
- **Parse errors.** The parser has no error recovery, so a parse error hides the rest of the file: hover, completion
  and the type hints read the last text that parsed, less the top-level items edited since (see
  [The parser](./parser)).
- **Downloaded packages.** The server does not resolve packages from the lockfile and the cache, or offer to restore
  them ([#866](https://github.com/nanyx-lang/nanyx/issues/866)).
- **Find references and rename** search the files of the document's package, but not other packages that depend on
  it.
- **Fixes in another file.** A quick fix whose edit is in another file is not offered.
- **The library browser, search by type and the info view** have open questions of their own, on
  [Info view and library browser](./info-view-and-library-browser#open-questions).

# Open questions

- Incremental re-checking: is compiling the whole document after each pause in typing fast enough as programs grow?
- Editors beyond VS Code: is `nanyx lsp` plus documented client setup enough?
