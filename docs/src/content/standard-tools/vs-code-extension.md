---
title: "VS Code extension"
description: "Installing and configuring the Nanyx extension for Visual Studio Code"
order: 3
---

The Nanyx extension for Visual Studio Code highlights `.nyx` files and runs the compiler as a language server (`nanyx lsp`), which gives you:

- errors and warnings as you type
- the inferred type of whatever is under the pointer, on hover
- inferred types in a lens above each top-level definition that does not write its type, and as inlay hints after locals, lambda parameters and pattern variables
- go to definition, find references and the outline view
- completion of names, record fields, module members and import paths
- quick fixes, such as adding the import for a standard-library name
- **Format Document**, which formats the file as `nanyx format` does
- a run button, which runs the file as `nanyx run` does

All of the settings below are under **Nanyx** in VS Code's settings, or can be written in `settings.json`. Changing any of them restarts the language server.

# Finding the compiler

The language server is built into the `nanyx` binary, which is not bundled with the extension. When you open a `.nyx` file, the extension looks for it in this order:

1. The `nanyx.path` setting, if it is set.
2. A `nanyx` on your `PATH`.
3. A compiler the extension downloaded earlier.

If none is found, it offers to download the newest compiler release for your platform into its own storage, so nothing else on the machine is touched. While it runs a downloaded compiler, it checks for a newer release at most once a day and offers to update. You can also run **Nanyx: Download the compiler** from the command palette at any time.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.path` | empty | The path to the `nanyx` executable itself (`nanyx` or `nanyx.exe`). Empty means look on `PATH`, then use a downloaded compiler. |
| `nanyx.compilerVersion` | empty | Pins the release to download and use, such as `0.5.2`. The extension then never looks for a newer one. Empty means the newest release. |
| `nanyx.downloadUrl` | empty | Where to download the compiler from, for a mirror or an unpublished release. `${version}`, `${rid}` (such as `win-x64`) and `${ext}` (`zip` or `tar.gz`) are filled in. The extension cannot list the releases at a custom URL, so it downloads `nanyx.compilerVersion`, or its own minimum version if that is empty. |

# How types are shown

Hovers, lenses, inlay hints and completions write types the way the compiler infers them. A type variable the compiler made up is named with a Greek letter, and a record's other fields are written with an ellipsis:

```nanyx
def identity = { x -> x }        -- α -> α
def name = { p -> p.name }       -- (name: α, …) -> α
def keep = { p -> (p.name, p) }  -- (name: α, …ω) -> (α, (name: α, …ω))
```

The other fields get a name, as `…ω` does in `keep`, only where the type uses them more than once: here, to say that `keep` gives back a record with the same fields it was given. The other tags of a union are named from the end of the alphabet in the same way, as in `#some(α) | ω, (α -> β) -> #some(β) | ω`. A type variable you wrote in an annotation keeps the name you gave it, and a type alias is shown by its name.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.asciiTypes` | off | Write types in ASCII only: type variables are `a`, `b`, `c` and the other fields of a record are `...`, as you would write them in source. Needs compiler 0.20.0 or later. |

`nanyx.greekTypeVariables` is the older name for this setting. It still works, but use `nanyx.asciiTypes` instead.

# Type hints

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.topLevelTypeHints` | `codeLens` | Where to show the inferred type of a top-level definition that does not write its type: `codeLens` (in a lens above it), `inlayHint` (after its name, as for locals) or `off`. Hover still shows it when it is off. |
| `nanyx.inlayHints.localDefinitions` | on | Show the inferred type of each local `def` that does not write its type. |
| `nanyx.inlayHints.parameters` | on | Show the inferred type of each lambda parameter that does not write its type. |
| `nanyx.inlayHints.patternVariables` | on | Show the inferred type of each variable a pattern binds. |

VS Code's own `editor.codeLens` and `editor.inlayHints.enabled` settings turn all lenses and inlay hints off. To change them, or any other editor setting, for Nanyx files only, put them under `"[nanyx]"`:

```json
{
  "[nanyx]": {
    "editor.inlayHints.enabled": "offUnlessPressed",
    "editor.formatOnSave": true
  }
}
```

# Formatting

**Format Document** formats a `.nyx` file with the [standard formatter](./formatter). A file that does not parse is left as it is. To format every time you save, set `editor.formatOnSave` to `true`, as in the example above.

# Running a file

The run button at the top right of a `.nyx` editor, or **Nanyx: Run the current file** from the command palette, saves the file and runs it with `nanyx run`. The program runs in a terminal of its own in the panel, so it can read what you type, and Ctrl+C stops it. Running the file again uses the same terminal.

The program's working directory is the workspace folder the file is in, or the file's own folder when it is in none. The file runs as a script, even when it is part of a package.

The command has no keyboard shortcut. To give it one, look for it in **Keyboard Shortcuts**.

# Commands

| Command | What it does |
|---------|--------------|
| **Nanyx: Run the current file** | Saves the file and runs it in a terminal. See [Running a file](#running-a-file). |
| **Nanyx: Download the compiler** | Looks for the newest compiler release straight away and downloads it. |
| **Nanyx: Restart the language server** | Stops the server and starts it again. |

# Working on the compiler

To try a change to the compiler in the editor, set `nanyx.path` to your build, such as `compiler/Nanyx.Compiler.Cli/bin/Debug/net10.0/nanyx.exe`, and turn on `nanyx.developmentMode`.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.developmentMode` | off | Runs the server from a copy of the build `nanyx.path` points at, so `dotnet build` can overwrite the build while the server runs, and restarts the server with a fresh copy whenever the build changes. Needs `nanyx.path`. |

# Troubleshooting

The **Nanyx** output channel logs how the extension found or downloaded the compiler, the server's start-up and any errors.

- **"nanyx.path is set to ..., but no file exists there"**: fix the path, or clear the setting to go back to `PATH` and the downloaded compiler.
- **"Could not reach github.com to download the Nanyx compiler"**: you are offline, or a proxy is in the way. Connect and run **Nanyx: Download the compiler**, or install the compiler yourself.
- **"No compiler release was found at ..."**: there is no release for that version. Set `nanyx.compilerVersion` to one that exists, `nanyx.downloadUrl` to where it is, or `nanyx.path` to a compiler you have.
- **No errors appear**: check the output channel, then run **Nanyx: Restart the language server**.
- **A setting has no effect**: some settings need a newer compiler than the one you have. Update it with **Nanyx: Download the compiler**.
