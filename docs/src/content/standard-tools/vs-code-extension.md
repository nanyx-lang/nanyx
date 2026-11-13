---
title: "VS Code extension"
description: "Installing and configuring the Nanyx extension for Visual Studio Code"
order: 3
---

The Nanyx extension for Visual Studio Code highlights `.nyx` files (the type in a definition attached to a built-in type, `def string.shout`, is coloured as a type, the escapes in a double-quoted string, such as `\n` and `\{`, are coloured as escapes, a [doc comment](../getting-started/basics#doc-comments) is coloured as one throughout its lines, and only [Nanyx's operators](../tour/operators#the-operators) are coloured as operators) and runs the compiler as a language server (`nanyx lsp`), which gives you:

- errors and warnings as you type, each compiler error's code (such as `NYX0201`) a link to its section of
  [Compiler errors](./errors)
- the inferred type of whatever is under the pointer, on hover, after the name on a name (`def name: [Console] string -> ()`, `n: int`), with the doc comment of the name there. On a module's name, in its `module` line, an import (its path or its alias) or an alias that qualifies a name (`m` in `m.foo`), hover shows the module's doc comment, the `--|` comment before its `module` line
- inferred types in a lens above each top-level definition that does not write its type, and as inlay hints after locals, lambda parameters and pattern variables
- go to definition (from a name, or from a `` [`name`] `` link in a doc comment), find references and the outline view
- completion of names, record fields, module members and import paths, each name with its doc comment and each module with the module's. The names the standard library keeps for itself, those that start with `__`, are never offered outside it. VS Code's own suggestions of words already in a file come only from the file being edited (`editor.wordBasedSuggestions` is `currentDocument` for Nanyx), so a standard-library file you opened from go to definition does not fill completion with its words
- quick fixes, such as adding the import for a standard-library name, or writing `int.fromString` for the deprecated `Int.fromString`
- **Format Document**, which formats the file as `nanyx format` does
- an [info view](#info-view), a view in the secondary side bar that shows what the compiler knows at the cursor
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

# Info view

The info view follows the cursor and shows what the compiler knows there. It lives in VS Code's secondary side bar (the
side bar on the opposite side from the Explorer, where Chat is), under a **Nanyx** tab, so it stays beside the code
while editors open and close. **Nanyx: Show info view** (Ctrl+Shift+Enter, or Cmd+Shift+Enter on a Mac, in a `.nyx`
editor) opens the secondary side bar if it is closed, shows the view and moves the focus to it; click back in the
editor, or press Ctrl+1, to carry on typing. You can drag the **Nanyx** tab to the primary side bar or to the panel,
and VS Code remembers where you put it. The extension needs VS Code 1.106 or later.

It is organised by scope, innermost first, with a rule between one scope and the next: the cursor, each lambda around
it, each definition around it (a local one as well as a top-level one), and the module. Each scope's heading names it
and links to it: a lambda by its parameters and line, `{ message -> … } (line 4)`, a definition by its name,
`def greet`, the module by its name. Click the heading's arrow to open or close a scope. The module starts closed, and
so does every scope after the third, since the side bar is narrow. A `memory` block or a `use` is part of the scope it
is in.

- **At the cursor**:
  - **Expression**: the type of the expression at the cursor, as hover shows it. On a use of a generic function, it
    shows the function's declared signature first (its annotation, or the generic type it was inferred to have), then
    its type at this use, then what this use fills in each type variable with, such as
    `a = string, b = #some(int) | #nothing`. Each links to the function's definition. A function that is not generic is
    shown once. On a module's name (in its `module` line, an import, an import's alias, or an alias that qualifies a
    name), it shows `module name` and the module's doc comment.
  - **Messages**: the errors and warnings on the cursor's line.
  - **This call**: on a call that requires contexts, the innermost one around the cursor, each context the call needs
    and where it finds it: a `use`, a definition's signature, a definition that does not write its type and was
    inferred to need it, or the runtime, linked, or an instance the compiler builds, from the definitions attached to
    the type (`attached Point.show`), derived from the type's parts, or its own (`built in`).
- **A lambda**: its type, with the contexts it carries, such as `[Log] int -> ()`; its parameters, locals and pattern
  variables, each with its type, nearest first; and each `use` in it, with the context it gives.
- **A definition**: its signature, marked `declared` if it writes its type and `inferred` if not, with its doc
  comment; its parameters and locals; its contexts, each with where it comes from (its signature, inference, or a
  `use` in it); and, in the innermost definition, its **Context flow**: each context its signature writes or it was
  inferred to need, and each `use` in it, with the calls each one serves under it. A `use` that serves no call is
  marked `unused`.
- **The module**: its doc comment, the top-level definitions and imports in scope, the file's own first, and the
  contexts the runtime supplies (`Console`, once `nanyx/console` is imported).

While the view is visible and not pinned, it also highlights in the editor how contexts flow: with the cursor on a
`use`, or on a context in a definition's signature, the calls it serves are highlighted; with the cursor on a call,
the `use` or signature context it draws on is.

It can also join them with lines. A line runs down the left margin from the first highlighted line to the last, and
from it a line runs across to each highlight. A highlight with only spaces before it is met halfway up its line;
one further along a line, such as a context in a signature, is underlined, so the line does not strike through the
text before it.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.infoView.contextLines` | `full` | `full` (**Full context lines**) draws the line down and a line across to each highlight. `stubs` (**Context line stubs**) draws the line down with a short tick at each highlighted line. `highlight-only` (**Highlight only**) draws no lines. |

The lines are drawn in the `nanyx.contextLine` theme colour, which you can change under
`workbench.colorCustomizations`. VS Code has no API for drawing in the editor, so the extension draws them with
decorations and CSS, as extensions that draw indent guides do. They do not follow a wrapped or folded line.

Under each item that has one comes its [doc comment](../getting-started/basics#doc-comments), as hover shows it: a name's own, an
import's module's, a context's own, or, for a context a `use` gives, that of the definition the `use` names if it has one. A doc comment of
more than one paragraph shows its first, except at the cursor, where it shows in full; click it to show the rest, and again
to cut it back. Its links go where the
names they name are declared.

Click an item to go to it: a name to where it is declared, a context to the `use` or signature that gives it. **Pin**
keeps the view on the cursor's current position while you move around and edit elsewhere; **Unpin** follows the cursor
again. After an edit, the view catches up when the compiler has checked the new text, without the cursor moving. While
the file does not parse, the view shows what the compiler knew the last time it did. It needs a compiler
newer than 0.23.0.

# Running a file

The run button at the top right of a `.nyx` editor, or **Nanyx: Run the current file** from the command palette, saves the file and runs it with `nanyx run`. The program runs in a terminal of its own in the panel, so it can read what you type, and Ctrl+C stops it. Running the file again uses the same terminal.

The program's working directory is the workspace folder the file is in, or the file's own folder when it is in none. The file runs as a script, even when it is part of a package. In [development mode](#working-on-the-compiler), it runs with `--timings`, so the terminal also shows how long each module took to check.

The command has no keyboard shortcut. To give it one, look for it in **Keyboard Shortcuts**.

# Commands

| Command | What it does |
|---------|--------------|
| **Nanyx: Run the current file** | Saves the file and runs it in a terminal. See [Running a file](#running-a-file). |
| **Nanyx: Show info view** | Shows and focuses the view that shows what the compiler knows at the cursor. See [Info view](#info-view). |
| **Nanyx: Download the compiler** | Looks for the newest compiler release straight away and downloads it. |
| **Nanyx: Restart the language server** | Stops the server and starts it again. |

# Working on the compiler

To try a change to the compiler in the editor, set `nanyx.path` to your build, such as `compiler/Nanyx.Compiler.Cli/bin/Debug/net10.0/nanyx.exe`, and turn on `nanyx.developmentMode`.

| Setting | Default | What it does |
|---------|---------|--------------|
| `nanyx.developmentMode` | off | Runs the server from a copy of the build `nanyx.path` points at, so `dotnet build` can overwrite the build while the server runs, and restarts the server with a fresh copy whenever the build changes. Also starts the server with `--timings`, so the output channel shows how long each module took to check in each compile, not only how long the compile took, and runs a file with `nanyx run --timings`, so the run terminal shows the same tree. Needs `nanyx.path`. |

# Troubleshooting

The **Nanyx** output channel logs how the extension found or downloaded the compiler, the server's start-up, how long each compile of a file took, and any errors.

- **"nanyx.path is set to ..., but no file exists there"**: fix the path, or clear the setting to go back to `PATH` and the downloaded compiler.
- **"Could not reach github.com to download the Nanyx compiler"**: you are offline, or a proxy is in the way. Connect and run **Nanyx: Download the compiler**, or install the compiler yourself.
- **"No compiler release was found at ..."**: there is no release for that version. Set `nanyx.compilerVersion` to one that exists, `nanyx.downloadUrl` to where it is, or `nanyx.path` to a compiler you have.
- **No errors appear**: check the output channel, then run **Nanyx: Restart the language server**.
- **A setting has no effect**: some settings need a newer compiler than the one you have. Update it with **Nanyx: Download the compiler**.
