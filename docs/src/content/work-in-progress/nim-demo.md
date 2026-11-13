---
title: "Nim demo"
description: "Proposal: a playable game of Nim in the browser, to show contexts as swappable effects"
order: 3
---

This page proposes a small demo: the game of Nim, played in the browser, with each player's strategy chosen from a
menu. It shows what contexts are for better than any explanation does: the game's rules are written once, and a
strategy, including a human clicking buttons, is a context the game is run with. It comes after the
[playground](./playground), and its full version needs the [browser host](./hosts#a-browser-host).

# Where it starts

`examples/Nim.nyx` already has the game, after the Nim examples in Hillerström and Lindley's "Liberating effects with
rows and handlers" (TyDe 2016), reworked for Nanyx contexts:

- A strategy is a context, `context Move = (move: (Player, int) -> int)`: how many sticks a player takes from a heap.
  `game: [Move] int -> Player` plays a game from a heap of `n` and gives back the winner. It doesn't know who is
  playing.
- `perfect` and `cheating` are two instances of `Move`, used as `use perfect in game(7)`.
- `checkedGame` wraps every move in a check, so an illegal move ends the game with `#cheated(player)`.
- `bobChooses` builds a strategy from another context, `Choose`, so the same game runs once per choice.
- `gameTree` builds the tree of every possible game, as figure 1 of the paper shows it.
- A scoreboard plays rounds against a coin-flipping Bob and counts wins.

**It doesn't compile today.** It predates several language changes. It calls attached definitions with a dot
(`player.other` and `p.show`, which are now `player \other` and `p \show`), it calls `Math.max` (now `math.max` from
`nanyx/math`), and it uses a `Random` context the standard library doesn't have. Under the
[contexts-are-not-types proposal](./context-escape#proposal-contexts-are-not-types), its named instances (`perfect`,
`cheating`) and `bobChooses` would become handler functions too.

# The demo

**In the playground** (a console program): the example as it is, fixed, as one of the playground's examples. You edit
a strategy, press Run and see who wins, the game tree and the scoreboard. This needs only the playground, and a
`Random` context (below).

**In a page of its own** (a browser app): `nanyx.dev/demos/nim`.

- A heap of sticks, and a menu for each player: Perfect, Random, Cheating or Human.
- A human player takes 1, 2 or 3 sticks with buttons; a computer player moves after a short pause, so you can follow
  it.
- A "Check moves" switch, which plays the game through `checkedGame`, so a cheating player is caught instead of
  winning.
- A scoreboard over many quick rounds between two computer players.
- The game tree for a small heap, drawn as the paper draws it.
- Beside it, the Nanyx source of the strategy being used, short enough to read, with a link that opens the whole
  program in the playground.

# What the page version needs

**The browser host**, which owns the main loop. A program for it is an `init`, `update` and `view`, as in Elm, not a
procedure that runs to the end ([Hosts](./hosts#a-browser-host)). That changes the shape of a game with a human in it:
`move: (Player, int) -> int` is a call that returns a number, and a page can't wait in the middle of a call for a
click. So the page version is a state machine:

- The model is the heap, whose turn it is, both players' strategies and the scores.
- `update` handles a click on a stick button as the human's move, and a timer message as a computer player's move. A
  computer move still comes from the same `Move` instances, called with `use` inside `update`.
- The rules (whose turn is next, when the game ends, what counts as cheating) stay as they are in the example and are
  shared by both versions.

This is a useful thing for the demo to show in itself: the strategies don't change between the console and the page,
only the loop around them.

**A `Random` context.** The standard library has none. It needs one, supplied by each host: from the operating
system under the CLI, from `crypto.getRandomValues` in a page, and seeded in tests so a game is reproducible. That is
worth an issue of its own; the demo is one user.

**Small pieces of `Dom` and `Clock`**: buttons, text, a few shapes for the sticks and the tree, and a timer.

# Plan

1. **Make `examples/Nim.nyx` compile and run** on the current language, without `Random` at first (Bob chooses from a
   fixed list) ([#1238](https://github.com/nanyx-lang/nanyx/issues/1238)).
2. **A `Random` context** in the standard library, supplied by the CLI's host and seedable in tests
   ([#1237](https://github.com/nanyx-lang/nanyx/issues/1237)). Then the coin-flip
   scoreboard comes back.
3. **Nim in the playground**, as one of its examples, once the playground exists.
4. **The page version**, on the browser host: the menus, human moves, the "Check moves" switch and the scoreboard.
5. **The game tree view**, and the source pane with its playground link.

# Open questions

- **Where demos live.** Pages on the docs site under `/demos`, built with it, or a repository of their own that the site
  links to.
- **How it draws.** Plain DOM elements are enough for sticks and buttons. The game tree may want SVG.
- **One program or two.** Whether the console version and the page version share a module of rules that both import,
  which also shows modules and packages, or are two files.
