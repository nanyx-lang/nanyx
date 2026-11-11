---
title: "Compiler workstreams"
description: "How work on the compiler and its tools is split up, labelled and tracked"
order: 20
---

The pages from here to the end of this section are the design of the compiler and its tools: how they work today,
what is decided and what is still open. They change as the work does, so they say where the code is as well as where
it is going. Anyone who changes something one of them describes updates the page in the same pull request.

Work on the compiler and its tools is split into workstreams. Each has a page here, a label on its issues, and a
tracking issue with the plan as a checklist. The
[Nanyx workstreams project](https://github.com/orgs/nanyx-lang/projects/1) shows all of them on one board,
with a Stream field. An issue that serves several workstreams is placed under the one that tracks it.

| Workstream | Design | Label |
|------------|--------|-------|
| Compilation | [Compilation](./compilation) | [`compilation`](https://github.com/nanyx-lang/nanyx/labels/compilation) |
| Linters | [Linters: compiler design](./linters) | [`linters`](https://github.com/nanyx-lang/nanyx/labels/linters) |
| Error messages | [Error messages](./error-messages) | [`error messages`](https://github.com/nanyx-lang/nanyx/labels/error%20messages) |
| Language server and editor | [Language server and editor](./language-server) | [`lsp`](https://github.com/nanyx-lang/nanyx/labels/lsp) |
| Packaging | [Packaging](./packaging) | [`packaging`](https://github.com/nanyx-lang/nanyx/labels/packaging) |
| Formatter | [Formatter](./formatter) | [`formatting`](https://github.com/nanyx-lang/nanyx/labels/formatting) |
| Code quality | [Code quality](./code-quality) | [`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) |

The compilation workstream has pages of its own for the larger pieces of its design:
[deferred errors](./deferred-errors), [closed and open records](./closed-and-open-records),
[tail calls](./tail-calls), [memory management](./memory-management), [context calls](./context-calls) and
[the standard library's implementation](./standard-library-implementation).
[The `nanyx` command](./command-line) lists what the CLI does today.

An issue that serves more than one workstream gets every label that applies, and appears in each tracking
issue. The finer labels (`parser`, `type checker`, `wasm backend`) say which part of the compiler an issue is in,
and are used alongside the workstream label.
