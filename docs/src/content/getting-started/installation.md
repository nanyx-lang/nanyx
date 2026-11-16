---
title: "Installation"
description: "How to install Nanyx on your system"
order: 2
---

Nanyx is one program, `nanyx`, which compiles, runs, tests and formats Nanyx code and is the language server for
editors. It runs on Windows, macOS and Linux, and needs nothing else installed: each release is self-contained.

There are three ways to get it:

- **With the VS Code extension.** If you use VS Code, install the extension and let it download the compiler for
  you. This is the quickest way to start.
- **From a GitHub release**, on any platform.
- **With Scoop**, on Windows.

# With the VS Code extension

Install the **Nanyx** extension (`nanyx.nanyx`) from the VS Code marketplace. When you open a `.nyx` file and the
extension finds no `nanyx` on your `PATH`, it offers to download the newest compiler release for your platform. It
keeps the download in its own storage, so nothing else on the machine changes, and offers to update it when a new
release comes out. See [VS Code extension](../standard-tools/vs-code-extension) for how it finds the compiler and
for its settings.

The compiler the extension downloads is for the extension's own use: it is not put on your `PATH`. To run `nanyx`
in a terminal as well, install it one of the other ways.

# From a GitHub release

Each compiler release is on the [releases page](https://github.com/nanyx-lang/nanyx/releases), tagged
`compiler/<version>`. Pick the newest release whose title starts with **Compiler** (the **VS Code extension**
releases are the extension, not the compiler), and download the file for your platform:

| Platform | File |
|----------|------|
| Windows (x64) | `nanyx-<version>-win-x64.zip` |
| macOS (Apple silicon) | `nanyx-<version>-osx-arm64.tar.gz` |
| macOS (Intel) | `nanyx-<version>-osx-x64.tar.gz` |
| Linux (x64) | `nanyx-<version>-linux-x64.tar.gz` |
| Linux (ARM64) | `nanyx-<version>-linux-arm64.tar.gz` |

Each archive holds the compiler, `nanyx.exe` on Windows and `nanyx` everywhere else, and the files it reads from
beside it: the standard library, already checked (`checked-library.bin`), and a `packages` folder with the packages
the compiler ships, such as the console host that runs command-line programs. Extract the whole archive into a folder
of its own, keep the files together, and add that folder to your `PATH`.

## Windows

Extract the zip into a folder such as `%LOCALAPPDATA%\Programs\nanyx`. Then add that folder to
your `PATH`: search the Start menu for **Edit environment variables for your account**, select `Path`, choose
**Edit**, then **New**, and paste the folder's path. Or, in PowerShell:

```powershell
$dir = "$env:LOCALAPPDATA\Programs\nanyx"
[Environment]::SetEnvironmentVariable("Path", "$([Environment]::GetEnvironmentVariable('Path', 'User'));$dir", "User")
```

Open a new terminal for the change to take effect.

## macOS and Linux

Extract the archive into a folder of its own, such as `~/.local/nanyx`:

```bash
mkdir -p ~/.local/nanyx
tar -xzf nanyx-<version>-<platform>.tar.gz -C ~/.local/nanyx
```

Then add that folder to your `PATH`: add this line to your shell's start-up file (`~/.zshrc` or `~/.bashrc`) and
open a new terminal:

```bash
export PATH="$HOME/.local/nanyx:$PATH"
```

On macOS, the release isn't signed, so macOS blocks a `nanyx` that was downloaded in a browser. Remove the
quarantine flag once to let it run:

```bash
xattr -d com.apple.quarantine ~/.local/nanyx/nanyx
```

To update, download the newer release and extract it over the old one.

# Package managers

## Windows (Scoop)

The [Scoop](https://scoop.sh) package is in Nanyx's own bucket. Add the bucket once, then install:

```powershell
scoop bucket add nanyx https://github.com/nanyx-lang/scoop-nanyx
scoop install nanyx
```

Scoop puts `nanyx` on your `PATH`. Update it with `scoop update nanyx`; the bucket picks up each new compiler
release within a day.

## macOS (Homebrew)

**Coming soon.** There is no Homebrew tap yet. See [Distribution](../work-in-progress/distribution) for the plan.

## Linux (apt)

**Coming soon.** There is no apt repository yet. See [Distribution](../work-in-progress/distribution) for the plan.

# Verify installation

In a new terminal, run:

```bash
nanyx --version
```

It prints the compiler's version, such as `0.27.0`. Run `nanyx --help` to see its commands.

# Editor support

- **VS Code**: install the **Nanyx** extension (`nanyx.nanyx`); see [VS Code extension](../standard-tools/vs-code-extension)
  for its settings.
- **Other editors**: `nanyx lsp` runs the language server over standard input and output, so an editor with an LSP
  client can start it by hand. In Neovim 0.11 or later, for example:

  ```lua
  vim.filetype.add({ extension = { nyx = "nanyx" } })
  vim.lsp.config("nanyx", {
    cmd = { "nanyx", "lsp" },
    filetypes = { "nanyx" },
    root_markers = { ".git" },
  })
  vim.lsp.enable("nanyx")
  ```

  There is no syntax grammar for editors other than VS Code yet, so the colours come from the server's semantic
  highlighting.

# Next steps

Now that you have Nanyx installed, let's write your [first program](/docs/getting-started/hello-world)!
