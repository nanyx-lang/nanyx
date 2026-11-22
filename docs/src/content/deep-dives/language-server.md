---
title: "Language server"
description: "How the language server, the info view and the library browser are built"
order: 18
---

What the language server does is described for users on [VS Code extension](../standard-tools/vs-code-extension), and
its options on [The nanyx command](../standard-tools/command-line#editors). This page says how it is built. What is
still open is on [Language server and editor](../work-in-progress/language-server) and
[Info view and library browser](../work-in-progress/info-view-and-library-browser) in Work in progress.

# The server

The language server is built into the `nanyx` binary (`nanyx lsp`), in `compiler/Nanyx.LanguageServer`, and calls the
compiler as a library: each document is compiled after a pause in typing, and hover, completion, the type hints and
the rest read that compile. The VS Code extension in `extension/` finds or downloads `nanyx` and starts it. Each
feature is a handler class in `Nanyx.LanguageServer/Handlers/`, and the tests in `Nanyx.LanguageServer.Tests` talk to
the server over the real wire protocol. How refactorings are built is in [Refactorings](./refactorings).

# The info view and the library browser

The info view (`NyxCompiler.InfoView`, `NyxCompiler.ContextFlow`, the server's `nanyx/infoView` request and
`extension/src/infoView.ts`) is modelled on the infoview in Lean 4's VS Code extension. The library browser and search
by type (`NyxCompiler.LibraryCatalog`, `NyxCompiler.LibrarySearch`, and the `nanyx/libraryModules`,
`nanyx/libraryModule`, `nanyx/librarySearch` and `nanyx/addImport` requests) read the exports of the checked standard
library. Each search query is unified against every export.

Doc comments are read by `Documentation.Reader`, which parses the file a symbol is declared in for them.
