# Working in this repository

These rules are for anyone changing the compiler and its tools, whether a person or an agent. The compiler
moves fast, with dozens of commits a day, and most of them land in the same few files. These rules keep
those files workable. Problems with the code's structure are tracked as GitHub issues with the
[`code quality`](https://github.com/nanyx-lang/nanyx/labels/code%20quality) label.

## Where new code goes

- **Put code in the file that owns its concern.** Do not put it in the file you happen to have open. Each
  project's `.fsproj` lists its files in compile order. Read that list before adding a function.
- **Don't grow a file past 1,500 lines.** If your change would take a file over that limit, or the file is
  already over it, put the new part in a new file or a smaller existing one. If what you need to extend sits
  in the middle of a big file, move it out first, in its own PR. The limit is for the compiler's F# and C#
  sources. It does not apply to the standard library's `.nyx` files. A library module that grows large is
  split by topic into files that each start `@partial module`, as the prelude is: add to the file whose topic
  it is, or start a new one beside it.
- **One concern per function.** A match arm longer than about 50 lines becomes a named function. A closure
  that changes several captured `mutable`s becomes a function that takes state and returns state.
- **Search before writing a helper.** Look for the function you are about to write, in your project and in
  the other compiler projects. Copies drift apart: one of them gets a bug fix and the others don't. If the
  helper you need is `private` in another file, make it `internal` and move it to a shared file. Don't
  copy it.

## Specific rules

- **Name built-in syntax once.** The parser desugars some syntax into calls with reserved names (`__for`,
  `__with`, `__range`, `__rec`, `dbg`, `not`). Don't add new string matches on these names. Use or extend
  the shared constants and active patterns, the `...Syntax` modules in the parser's `Ast.fs` and `IsSyntax.fs`
  ([#367](https://github.com/nanyx-lang/nanyx/issues/367)).
- **Layout and reserved words live in one place each.** Where a block, a match or a pipeline ends is decided by the
  layout pass (`Layout.fs`), whose rules are numbered on the
  [parser's Deep dives page](docs/src/content/deep-dives/parser.md#the-layout-rules); the parser reads
  its `Indent`, `Newline` and `Dedent` tokens and never measures indentation itself. The reserved words are
  `Keywords.reserved`, which the lexer reads. Change a rule there, not in the parser.
- **Report user errors as diagnostics, with a span.** A bare `failwith` is never the way to tell users
  something. In the backend, a feature that isn't supported yet and a broken compiler invariant are
  different things, so report them differently
  ([#377](https://github.com/nanyx-lang/nanyx/issues/377)).
- **Delete dead code.** Don't comment it out, and don't leave a function nobody calls. Never commit
  placeholder markers such as `// ...existing code...`.
- **Keep comments true.** When you change behaviour, update the doc comments and the docs pages that
  describe it.

## Documentation

- **All documentation lives on the docs site.** Technical detail on how things work today, such as the compiler's
  internals, inlining, desugaring, workflow rewriting and the runtime, goes in the Deep dives section, [docs/src/content/deep-dives](docs/src/content/deep-dives). Design
  notes, proposals, plans and open questions, what is open, undecided or planned, go in the Work in progress section,
  [docs/src/content/work-in-progress](docs/src/content/work-in-progress). Don't add design docs or long READMEs
  elsewhere in the repository; `compiler/README.md` only says how to build and test.
- **Keep the Deep dives and Work in progress sections up to date.** If your change touches something a page
  there describes (a feature, a plan item, an open question, a file or function it names), update the page in the
  same PR: correct what a Deep dives page says about the code, mark plan items done, and move settled
  questions into the design. When something planned is built, describe it in Deep dives and cut the Work in
  progress page down to what is still open, or delete it. If you start on something that has no page, add one.
- **Keep the VS Code extension page up to date.** A change to the extension in `extension/` (a setting, a
  command, a feature, how it finds `nanyx`) updates
  [VS Code extension](docs/src/content/standard-tools/vs-code-extension.md) in the same PR.
- **The docs' examples are tests.** Every ` ```nanyx ` block on every page under `docs/src/content` is compiled and
  run by `RunDocExampleTests.fs`, but for the error reference's, which `ErrorReferenceTests.fs` checks. Mark a block
  that isn't a whole program ` ```nanyx fragment `, and one the compiler gets wrong ` ```nanyx skip=#<issue> `; the
  rules are in
  [Compilation](docs/src/content/deep-dives/compilation.md#docs-examples).
- **Use types, not strings.** Prefer a union case to a string for types, kinds and keys. Never take
  structured data apart with `Split`.

## Nanyx code

- **Format the Nanyx you write.** Before you commit a `.nyx` file you added or changed, in `stdlib/`,
  `examples/` or anywhere else, run the formatter on it. From `compiler/`:

  ```bash
  dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- format <files>
  ```

  CI fails if any file under `stdlib/` is not formatted, and a test fails on an example in `examples/` or a
  docs code block fenced ` ```nanyx ` that is not, naming it and showing its formatted form. Write a docs block
  as the formatter would. Format only the files you touched: formatting other files belongs in a PR of its own.
  Leave test inputs alone where their layout is what the test checks, such as parser and formatter fixtures.
- **Check the repository's Nanyx when you change the formatter.** A change to how the formatter lays code
  out can leave `stdlib/`, `examples/` or the docs unformatted. Run
  `dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- format --check ../stdlib` from
  `compiler/` before you push, and reformat what the corpus test lists in the same PR.
- **Write the standard library's pages after changing it.** The reference on each page in
  `docs/src/content/stdlib` is generated from the doc comments of the library and of the packages in `packages/`, and a
  test fails when it is out of date. After you change a doc comment or what the library or a shipped package exports,
  run, from `compiler/`:

  ```bash
  dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- stdlib-docs ../docs/src/content/stdlib
  ```

  Explain a function in its doc comment, not in a page's prose.
- **Keep the prelude's contexts unambiguous.** An operator on a value of a type variable is read through the
  prelude's context with the fewest members that has the member it reads, so `+` is `Add`'s, though `Num` has
  one too ([Operators find their context](docs/src/content/advanced/contexts.md#operators-find-their-context)). Before you add a
  context to the prelude (`stdlib/nanyx/prelude*.nyx`), or a member to one, check which operators it moves: a context that
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
  first: `TestSupport.fs` in `Nanyx.Compiler.Tests`, `TestHelpers.fs` in the parser, formatter and Wasm test projects.
  Use them. Don't write another temp-directory helper or single-diagnostic helper.
- **Check which error.** An error test asserts the diagnostic's code, or the message it expects. A test
  that only asserts "some error was reported" also passes when an unrelated error breaks the program.
- **Count work rather than time it.** A test that compares costs compares an amount of work where it can: a
  WebAssembly program's fuel (`WasmProgram.Work`), the type checker's full solves (`Solving.countingFullSolves`), the
  tokens the parser reads (`ParseState.TokensRead`), or, for a function with no count of its own, what it allocates
  (`TestSupport.allocatesLinearly`).
  One that has to time something does it with `TestTiming.millisecondsOf` and is marked
  `[<Trait("Category", "Timing")>]`. CI leaves those out, and the nightly Timing workflow runs them
  ([compiler/README.md](compiler/README.md#running-tests)). A change to what a timing test covers runs that test, with
  `--filter "Category=Timing"`, locally or with `gh workflow run Timing --ref <branch>`, and its PR says so.
- **Show what differed.** A failing comparison should print the difference: use `Assert.Equal(expected,
  actual)`, not `failwith "mismatch"`.

## Releasing the compiler

- **A version bump is a release.** Merging a change to `<Version>` in `compiler/Directory.Build.props` makes `main`
  tag `compiler/<version>` and publish the binaries.
- **The shipped packages move with the version.** A package the compiler ships is named at the compiler's own version,
  so the PR that bumps `<Version>` also sets that version in every manifest under `packages/` (the package's own
  `version` and each shipped dependency's), in `examples/`, and in the docs that name a shipped package's version
  (`git grep '<old version>' packages examples docs/src/content`). Without it, the hosts' `nanyx` refuses the new
  compiler (`NYX0052`) and the release job never runs. A shipped manifest's `nanyx`, the compilers it builds with, moves too: a library's is the
  new version's major and minor, `0.43` for `0.43.0`, and a host's or a host library's is that version only, `=0.43.0`,
  since they use the compiler's internals. A test checks both.
- **A release publishes the shipped packages.** The release attaches each shipped package's `<name>-<version>.nyxpkg`
  to the `compiler/<version>` release, and the package index's sync records it from there, as it does any package's
  release ([Releases](docs/src/content/deep-dives/releases.md#the-shipped-packages)). A shipped package whose public
  API changes more than the version allows isn't recorded: below 1.0.0 a patch release may not break one.
- **The PR that bumps the version writes its changelog section.** Add a `# <version>` section at the top of the
  [Changelog](docs/src/content/overview/changelog.md), with the date under it in italics. The release job uses that
  section as the GitHub release's notes, and the Compiler workflow fails if there isn't one.
- **Write it from the PRs, not from memory.** List what has merged since the last release with
  `git log --format=%s compiler/<last version>..origin/main`, and read the bodies of the PRs that matter with
  `gh pr view <n>`. Open with a sentence or two on what the release is about, then the changes a user would notice,
  under bold **Language**, **Standard library**, **Tools**, **Performance** and **Fixes** labels, leaving out the
  empty ones. Link each change to the docs page that describes it. Leave out refactors, test-only and docs-only
  changes, CI, and the extension, which has releases of its own.

- **Release when the tour gets ahead of the playground.** The docs site's playground runs the release that
  `playgroundRelease` in `docs/site/playground-compiler.mjs` names, not `main`. When a change lets the tour use something
  that release can't compile, a release is due: make it, then move `playgroundRelease` to it in a PR of its own, after
  the release is published. The playground page test's run of every example fails until then.

## Refactoring

- **Keep refactors apart from behaviour changes.** A move is pure cut and paste. Behaviour, generated WAT,
  formatter output and test counts all stay the same. If the move turns up a bug, fix it in its own PR,
  with a test.
- **One move per PR.** Branch from `main` and merge quickly. Don't stack refactor PRs: these files change
  too fast for a long-lived branch to merge.
- **A recursive chain stays in one file.** An F# `let rec … and` chain can't be split across files. Move what is
  around it into earlier or later files, and shorten the chain by turning its long match arms into `and` functions
  of their own, or by passing the recursive function in as a parameter, as `Loops.fs` takes `inferExpr`.
- **Order is behaviour in places.** Keep these orders exactly as they are:
  - the order in which the parser tries the alternatives of a construct (`Cursor.Attempt` calls)
  - the order in which the parser's `Forward` references are filled
  - the order of `addConstraint` calls (constraints are solved newest first)
  - the order in which runtime helpers are emitted

## Problems you won't fix now

If you notice a code quality problem outside your change, file an issue. Give it the `code quality` label
and the label for its area (`parser`, `type checker`, `wasm backend`, `formatting`, `lsp`). Include
file:line evidence at a commit, and say what "done" looks like. Then add it to the
[Nanyx workstreams project](https://github.com/orgs/nanyx-lang/projects/1), whose Stage field orders the
backlog: Working set, then Ready; Planning is for issues that still have open questions or no "done when". A daily review files whatever is left over.
