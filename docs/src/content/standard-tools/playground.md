---
title: "Playground"
description: "Write Nanyx, run it and share it in the browser, with nothing to install"
order: 5
---

The [playground](/playground) runs Nanyx in your browser: an editor, a Run button, the program's output, and a link
that shares what you wrote. The compiler runs in the page, so nothing is sent to a server, and once the page has
loaded it works offline.

# Writing and running

Write a program in the editor and press **Run**, or Ctrl+Enter (⌘+Enter on a Mac). The page compiles it and runs it,
and **Stop** takes Run's place while it runs, to end a program that doesn't finish. The page downloads the compiler,
about 5.5 MB, when it opens, and says "Loading the compiler…" until it is ready.

A program is a script, as `nanyx run` runs a single file, but under the playground's host, `nanyx.playground`, rather
than the console's ([Hosts](../advanced/hosts)). It has `Console`, `Random` and `Clock`, and nothing else, so a program
that needs `FileSystem`, HTTP, `Env` or `Async` is a compile error naming the host. A program can use the standard
library, and the packages the compiler ships, but no package from the index. `Random` is seeded afresh for each run.

The page keeps what you write in the browser's storage, so reloading it loses nothing.

# The output

The output pane shows what happened in order, as a terminal does:

- what the program prints, as it prints it, and what it writes to standard error in the error colour;
- each `dbg` on a line of its own, dimmed, after its place (`[main.nyx:3]`);
- how the program ended, as `nanyx run` shows it: a crash's message, or `exited with 2`, and nothing when it ran to its
  end; `stopped` when you stopped it;
- a program that doesn't compile shows its errors instead, each code linked to its section of
  [Compiler errors](./errors).

When the program reads a line, an input line appears at the bottom of the pane. Enter gives the program the line, which
stays in the output, and Ctrl+D ends the input, so that the program's next `readLine` gives `#endOfInput`.

Each run's output stays, under a divider with its number and time, so you can compare one run with the last. **Clear**
empties the pane.

# Examples

The **Examples** menu holds every example in the [language tour](../tour/variables) that is a whole program, grouped by
its page. Choosing one replaces the editor's code, after asking if you have changed it.

Every example on the docs' pages that is a whole program, the tour's and the others', has **Open in the playground**
under it, which opens it here, as edited; one that prints something can also be run where it is, with **Run**: click
its code to edit it, and **Reset** puts it back. A page loads the compiler on the first Run on it. An example that only
defines things, with no code at its top level that runs, has only the link, as running it would print nothing; so has an
example that reads input, since a docs page can't give a program input. An example that needs another host than the
playground's has neither: one that names a host with `::host`, or uses what only the
[console host](../advanced/hosts) gives a program, such as its arguments (`args`), files or HTTP. Nor has one that
depends on a package from the index, such as `nanyx.json`, which the playground can't download. The
[error reference](./errors)'s examples have neither either: each shows an error.

The docs' examples use strings without importing `nanyx/text`, so the playground adds `import nanyx/text` to them, as
the tests that run them do.

# Sharing

**Share** copies a link to the code in the editor. The code is in the link itself, compressed, after the `#`, so it is
stored nowhere else and never sent to the site. Opening a link shows the code but doesn't run it until you press Run, so
a link can't start a program you haven't seen.

# Browsers

The playground runs in a recent Chrome, Edge or Firefox, and in Safari 18.2 or later: compiled programs use WebAssembly
tail calls, which older browsers don't have, and the page says so in one of them. On a narrow window, such as a phone's,
the output is under the editor. The divider between them can be dragged, or moved with the arrow keys.

The page names the compiler release it runs, at the right of its toolbar, with a link to the release's notes.
