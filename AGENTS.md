# Working in this repository

These rules are for anyone changing the compiler and its tools, whether a person or an agent. The compiler
moves fast, with dozens of commits a day, and most of them land in the same few files. These rules keep
those files workable. The reasoning behind them, and the current state of the code, are in
[Code quality](docs/src/content/work-in-progress/code-quality.md), and its issues carry the `code quality`
label.

## Where new code goes

- **Put code in the file that owns its concern.** Do not put it in the file you happen to have open. Each
  project's `.fsproj` lists its files in compile order. Read that list before adding a function.
- **Don't grow a file past 1,500 lines.** If your change would take a file over that limit, or the file is
  already over it, put the new part in a new file or a smaller existing one. If what you need to extend sits
  in the middle of a big file, move it out first, in its own PR.
- **One concern per function.** A match arm longer than about 50 lines becomes a named function. A closure
  that changes several captured `mutable`s becomes a function that takes state and returns state.
- **Search before writing a helper.** Look for the function you are about to write, in your project and in
  the other compiler projects. Copies drift apart: one of them gets a bug fix and the others don't. If the
  helper you need is `private` in another file, make it `internal` and move it to a shared file. Don't
  copy it.

## Specific rules

- **Name built-in syntax once.** The parser desugars some syntax into calls with reserved names (`__for`,
  `__with`, `__range`, `__rec`, `dbg`, `not`). Don't add new string matches on these names. Use or extend
  the shared constants and active patterns ([#367](https://github.com/nanyx-lang/nanyx/issues/367)).
- **Report user errors as diagnostics, with a span.** A bare `failwith` is never the way to tell users
  something. In the backend, a feature that isn't supported yet and a broken compiler invariant are
  different things, so report them differently
  ([#377](https://github.com/nanyx-lang/nanyx/issues/377)).
- **Delete dead code.** Don't comment it out, and don't leave a function nobody calls. Never commit
  placeholder markers such as `// ...existing code...`.
- **Keep comments true.** When you change behaviour, update the doc comments and the docs pages that
  describe it.

## Documentation

- **All documentation lives on the docs site.** Design notes, proposals and descriptions of how the
  compiler works today go in the Work in progress section,
  [docs/src/content/work-in-progress](docs/src/content/work-in-progress). Don't add design docs or long
  READMEs elsewhere in the repository; `compiler/README.md` only says how to build and test.
- **Keep the Work in progress section up to date.** If your change touches something a page there
  describes (a feature, a plan item, an open question, a file or function it names), update the page in
  the same PR: mark plan items done, move settled questions into the design, and correct what the page
  says about the code. If you start on something that has no page, add one. When a feature is finished
  and described in the language docs, cut the page down to what is still open, or delete it.
- **Keep the VS Code extension page up to date.** A change to the extension in `extension/` (a setting, a
  command, a feature, how it finds `nanyx`) updates
  [VS Code extension](docs/src/content/standard-tools/vs-code-extension.md) in the same PR.
- **Use types, not strings.** Prefer a union case to a string for types, kinds and keys. Never take
  structured data apart with `Split`.

## Nanyx code

- **Format the Nanyx you write.** Before you commit a `.nyx` file you added or changed, in `stdlib/`,
  `examples/` or anywhere else, run the formatter on it. From `compiler/`:

  ```bash
  dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- format <files>
  ```

  CI fails if any file under `stdlib/` is not formatted. Format only the files you touched: formatting other
  files belongs in a PR of its own. Leave test inputs alone where their layout is what the test checks, such
  as parser and formatter fixtures.
- **Check the standard library when you change the formatter.** A change to how the formatter lays code
  out can leave `stdlib/` unformatted. Run
  `dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- format --check ../stdlib` from
  `compiler/` before you push.
- **Keep the prelude's contexts unambiguous.** An operator on a value of a type variable is read through the
  prelude's context with the fewest members that has the member it reads, so `+` is `Add`'s, though `Num` has
  one too ([Operator contexts](docs/src/content/work-in-progress/operator-contexts.md)). Before you add a
  context to `stdlib/nanyx/prelude.nyx`, or a member to one, check which operators it moves: a context that
  repeats another's member must be larger or smaller than it, never the same size, and a smaller one takes the
  operator over in every function whose type is inferred. The compiler reports a tie as `NYX0006` when it
  checks the prelude.

## Tests

- **Run the tests before you merge.** CI runs nothing on pull requests, only on `main` after a merge, so a broken
  PR merges unless you catch it. From `compiler/`, run `dotnet test Nyx.Compiler.slnx` and
  `dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- format --check ../stdlib`; for a change to
  `extension/`, run `npm run compile` there. To run CI itself on your branch, use
  `gh workflow run Compiler --ref <branch>` or `gh workflow run "VS Code extension" --ref <branch>`.
- **One file per feature.** A test goes in the `Features/<Area>/` file for the feature it covers. If no such
  file exists, create one. Don't append to a catch-all file. Keep each project's `<Compile Include>` list
  in order.
- **Shared helpers live in one file.** Each test project keeps its shared helpers in one file, compiled
  first: `TestSupport.fs` in `Nanyx.Compiler.Tests`, `TestHelpers.fs` in the parser and Wasm test projects.
  Use them. Don't write another temp-directory helper or single-diagnostic helper.
- **Check which error.** An error test asserts the diagnostic's code, or the message it expects. A test
  that only asserts "some error was reported" also passes when an unrelated error breaks the program.
- **Show what differed.** A failing comparison should print the difference: use `Assert.Equal(expected,
  actual)`, not `failwith "mismatch"`.

## Refactoring

- **Keep refactors apart from behaviour changes.** A move is pure cut and paste. Behaviour, generated WAT,
  formatter output and test counts all stay the same. If the move turns up a bug, fix it in its own PR,
  with a test.
- **One move per PR.** Branch from `main` and merge quickly. Don't stack refactor PRs: these files change
  too fast for a long-lived branch to merge.
- **Order is behaviour in places.** Keep these orders exactly as they are:
  - FParsec choice order and where `attempt` is placed
  - the order in which the forward refs are filled
  - the order of `addConstraint` calls (constraints are solved newest first)
  - the order in which runtime helpers are emitted

## Problems you won't fix now

If you notice a code quality problem outside your change, file an issue. Give it the `code quality` label
and the label for its area (`parser`, `type checker`, `wasm backend`, `formatting`, `lsp`). Include
file:line evidence at a commit, and say what "done" looks like. Then add it to the
[Nanyx workstreams project](https://github.com/orgs/nanyx-lang/projects/1), whose Priority field orders the
backlog. A daily review files whatever is left over.
