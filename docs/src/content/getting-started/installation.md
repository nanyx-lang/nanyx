---
title: "Installation"
description: "How to install Nanyx on your system"
order: 2
---

Nanyx can be installed on macOS, Linux, and Windows. The recommended way is through the `nanyx` version manager.

# Quick install

Run the following command in your terminal:

```bash
curl -fsSL https://nanyx.dev/install | sh
```

This will install the latest stable version of Nanyx and add it to your `PATH`.

# Verify installation

```bash
nanyx --version
# nanyx 0.4.0
```

# Package Managers

## macOS (Homebrew)

```bash
brew install nanyx
```

## Linux (apt)

```bash
sudo apt install nanyx
```

## Windows (Scoop)

```bash
scoop install nanyx
```

# Editor support

Nanyx has editor plugins available for:

- **VS Code** — install the **Nanyx** extension (`nanyx.nanyx`); see [VS Code extension](../standard-tools/vs-code-extension) for its settings
- **Neovim** — via `nvim-lspconfig` with the Nanyx LSP
- **Zed** — built-in Nanyx support

# Next steps

Now that you have Nanyx installed, let's write your [first program](/docs/getting-started/hello-world)!
