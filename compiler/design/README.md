# Compiler design

Work on the compiler and its tools is split into workstreams. Each has a design document here, a label on its
issues, and a tracking issue with the plan as a checklist. The
[Nanyx workstreams project](https://github.com/orgs/nanyx-lang/projects/1) shows all of them on one board,
with a Stream field. An issue that serves several workstreams is placed under the one that tracks it.

| Workstream | Design | Label |
|------------|--------|-------|
| Compilation | [compilation.md](compilation.md) | [`compilation`](https://github.com/nanyx-lang/nanyx/labels/compilation) |
| Linters | [linters.md](linters.md) | [`linters`](https://github.com/nanyx-lang/nanyx/labels/linters) |
| Error messages | [error-messages.md](error-messages.md) | [`error messages`](https://github.com/nanyx-lang/nanyx/labels/error%20messages) |
| Language server and editor | [language-server.md](language-server.md) | [`lsp`](https://github.com/nanyx-lang/nanyx/labels/lsp) |
| Packaging | [packaging.md](packaging.md) | [`packaging`](https://github.com/nanyx-lang/nanyx/labels/packaging) |
| Formatter | [formatter.md](formatter.md) | [`formatting`](https://github.com/nanyx-lang/nanyx/labels/formatting) |
| Code quality | [code-quality.md](code-quality.md) | [`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) |

An issue that serves more than one workstream gets every label that applies, and appears in each tracking
issue. The finer labels (`parser`, `type checker`, `wasm backend`) say which part of the compiler an issue is in,
and are used alongside the workstream label.
