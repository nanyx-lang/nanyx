# Nanyx VS Code Extension

Editor support for the Nanyx language: syntax highlighting for `.nyx` files and compiler diagnostics
from the Nanyx language server.

## Features

- Syntax highlighting for `.nyx` files.
- Diagnostics as you type, from the Nanyx compiler running as a language server (`nanyx lsp`).
- Inferred types on hover, in a lens above each top-level definition that does not write its type,
  and as inlay hints after unannotated locals and parameters. Set `nanyx.topLevelTypeHints` to
  `inlayHint` to show top-level definitions' types as inlay hints too, instead of lenses, or to `off`. VS Code's
  `editor.codeLens` and `editor.inlayHints.enabled` settings turn the lenses and hints off; under
  `"[nanyx]": { ... }` they do so for Nanyx files only.
- Go to definition.
- Formatting: **Format Document** formats a `.nyx` file with the standard formatter, as `nanyx format`
  does. A file that does not parse is left as it is. To format on save, set `"editor.formatOnSave": true`, or
  `"[nanyx]": { "editor.formatOnSave": true }` for Nanyx files only. Compilers released before formatting
  existed do not offer it.
- Commands: **Nanyx: Download the compiler** and **Nanyx: Restart the language server**.

## How the extension finds `nanyx`

The language server is built into the `nanyx` compiler binary, which is not bundled in the extension.
When a `.nyx` file is opened the extension looks for it in this order:

1. The `nanyx.path` setting, if set. It must point at the executable itself (`nanyx` or `nanyx.exe`).
2. A `nanyx` on your `PATH`.
3. A copy the extension downloaded earlier into its global storage directory: the newest one, or
   the one `nanyx.compilerVersion` names.

If none is found it offers, once per session, to download the newest compiler release
(`nanyx-<version>-<rid>.zip` or `.tar.gz` from the
[GitHub releases](https://github.com/nanyx-lang/nanyx/releases) tagged `compiler/<version>`, skipping
drafts and prereleases) for the current platform (`win-x64`, `linux-x64`, `linux-arm64`, `osx-x64` or
`osx-arm64`). The extension has a minimum compiler version, the oldest release with everything it
relies on, and never picks a release below it. The archive is unpacked with the platform's `tar` into
the extension's storage, so nothing else on the machine is touched.

Once the language server is running from a downloaded copy, the extension asks GitHub (at most once a
day) whether a newer compiler release exists, and if so offers to download it and restart the
server. Older downloaded copies are removed after an update. **Nanyx: Download the compiler** looks
for the newest release straight away.

If GitHub cannot be asked (you are offline, or its API is rate limiting you), the extension uses
the newest copy it already downloaded, or else downloads its minimum version. If a download fails,
the extension says so and you can run **Nanyx: Download the compiler** later or set `nanyx.path`.

## Settings

- `nanyx.path`: path to the `nanyx` executable. Empty (the default) means search `PATH`, then the
  downloaded copy.
- `nanyx.developmentMode`: for working on the compiler; see
  [Using a local build of the compiler](#using-a-local-build-of-the-compiler). Off by default.
- `nanyx.compilerVersion`: pins the compiler release to download and use, e.g. `0.5.2`; the
  extension then never looks for a newer one. Empty means the newest release.
- `nanyx.downloadUrl`: a URL template to download from, with `${version}`, `${rid}` and `${ext}`
  (`zip` on Windows, `tar.gz` elsewhere) substituted. Empty means the GitHub releases of
  `nanyx-lang/nanyx`. Point it somewhere else to install from a mirror, or at a release that has not
  been published on GitHub yet. The extension cannot list the releases at a custom URL, so it
  downloads `nanyx.compilerVersion`, or its minimum version if that is empty.
- `nanyx.greekTypeVariables`: hovers, lenses and inlay hints name inferred type variables `α`, `β`, `γ`, ... (the default).
  Turn it off for `a`, `b`, `c`, ...; this starts the server with `nanyx lsp --latin-type-vars`,
  which compilers before this setting do not understand.
- `nanyx.topLevelTypeHints`: where a top-level definition's inferred type is shown: `codeLens` (the
  default: a lens above it), `inlayHint` (after its name, like `def id: α -> α = { x -> x }`) or
  `off`. The last two start the server with `nanyx lsp --top-level-type-hints inlay` or `off`, which
  compilers before this setting do not understand.

Changing any of these restarts the language server.

## Using a local build of the compiler

While developing the compiler, publish it and point the extension at the result:

```bash
dotnet publish compiler/Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -c Release -o /tmp/nanyx
```

then set `nanyx.path` to `/tmp/nanyx/nanyx` (or `...\nanyx.exe` on Windows). A plain
`dotnet build` output works too: `compiler/Nanyx.Compiler.Cli/bin/Debug/net10.0/nanyx.exe`.

While the language server runs from the build it holds its files open, and on Windows that stops
`dotnet build` from overwriting them. Turn on `nanyx.developmentMode` to avoid this: the extension
copies the folder `nanyx.path` is in to its storage and runs the server from the copy. It also
watches that folder, and once a build has finished writing to it (the folder has been quiet for a
moment) it makes a fresh copy and restarts the server with it, so a `dotnet build` is all it takes
to try a compiler change in the editor. The **Nanyx** output channel logs each restart. Old copies
are deleted once nothing runs from them.

## Developing the extension

```bash
cd extension
npm install
npm run compile
```

Press F5 in VS Code with the `extension` folder open to launch an Extension Development Host, or
package it with `npx @vscode/vsce package` and install the `.vsix`. The server's log, and anything the
extension does to find or download it, is in the **Nanyx** output channel.

## Troubleshooting

- **"nanyx.path is set to ..., but no file exists there"**: fix the path, or clear the setting to
  go back to `PATH` and the downloaded copy.
- **"Could not reach github.com to download the Nanyx compiler"**: you are offline, or a proxy is in
  the way. Connect and run **Nanyx: Download the compiler**, or build the compiler yourself and set
  `nanyx.path`.
- **"No compiler release was found at ..."**: no GitHub release exists for that version yet. Set
  `nanyx.compilerVersion` to one that does, `nanyx.downloadUrl` to where it is, or `nanyx.path` to a
  local build.
- **No diagnostics appear**: check the **Nanyx** output channel for the server's start-up line and
  any errors, then run **Nanyx: Restart the language server**.
