// The compiler built for the browser and the playground host's runner, loaded once per page from where the docs build
// unpacks them (`site/playground-compiler.mjs`), and a run of a program into a terminal. The playground page and the
// tour's examples both run programs through this.

import type { Exit, Terminal } from "./terminal";

export interface Compiler {
  compile(source: string): { wasm: Uint8Array | null; diagnostics: string[] };
}

export interface Running {
  done: Promise<Exit>;
  stop(): void;
  input(text: string): void;
  closeInput(): void;
}

interface Runner {
  canReadInput: boolean;
  run(
    wasm: Uint8Array,
    callbacks: {
      seed: number;
      onStdout: (text: string) => void;
      onStderr: (text: string) => void;
      onDbg: (dbg: { file: string; line: number; text: string }) => void;
      onRead: () => void;
    },
  ): Running;
}

/** The compiler and the runner, ready, and the compiler release they are, `null` for a compiler built locally. */
export interface Playground {
  compiler: Compiler;
  runner: Runner;
  release: string | null;
}

/** Why the page can't run programs: the browser can't, or the site has no compiler. Its message says so. */
export class PlaygroundUnavailable extends Error {}

/** Where the docs build unpacks the compiler. */
const compilerBase = "/playground/compiler/";

/**
 * A module with one function that tail-calls itself (`return_call`), which the backend emits for tail calls: a browser
 * that doesn't validate it can't run Nanyx programs.
 */
const tailCallModule = new Uint8Array([
  0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00, 0x01, 0x04, 0x01, 0x60, 0x00, 0x00, 0x03, 0x02, 0x01, 0x00, 0x0a, 0x06,
  0x01, 0x04, 0x00, 0x12, 0x00, 0x0b,
]);

let loading: Promise<Playground> | null = null;

/** The playground host's runner, from the package `nanyx.playground.host` in the compiler's bundle. */
const importRunner = () => import(/* @vite-ignore */ compilerBase + "packages/nanyx.playground.host/runner/playground.js");

async function load(warmUp: string | undefined): Promise<Playground> {
  if (!WebAssembly.validate(tailCallModule)) {
    throw new PlaygroundUnavailable(
      "This browser can't run Nanyx programs: they need WebAssembly tail calls, which Safari has had since 18.2, and Chrome, Edge and Firefox for longer.",
    );
  }
  const response = await fetch(compilerBase + "release.json");
  if (!response.ok) {
    throw new PlaygroundUnavailable("The playground's compiler isn't on this site yet: it comes with the next compiler release.");
  }
  const { release } = (await response.json()) as { release: string | null };
  const [{ loadCompiler }, runner] = await Promise.all([
    import(/* @vite-ignore */ compilerBase + "compiler.js"),
    importRunner(),
  ]);
  const compiler: Compiler = await loadCompiler();
  // The first compile is the compiler's warm-up, most of the wait: done now, the first Run is quick.
  if (warmUp !== undefined) compiler.compile(warmUp);
  return { compiler, runner: runner as Runner, release };
}

/**
 * Loads the compiler and the runner, once per page however often it is called, and compiles `warmUp`, if it is given,
 * so that the first Run after is quick. A failure is kept too: the next call gives it again.
 */
export function loadPlayground(warmUp?: string): Promise<Playground> {
  loading ??= load(warmUp);
  return loading;
}

/** The link to a compiler release's notes. */
export function releaseNotesUrl(release: string) {
  return `https://github.com/nanyx-lang/nanyx/releases/tag/compiler%2F${encodeURIComponent(release)}`;
}

const nextPaint = () => new Promise((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));

/**
 * Compiles `source` and, when it compiles, runs it, showing what happens in `terminal` under a run of its own: its
 * diagnostics, what it writes, its input line while it reads, and how it ended. `onCompiling` is called before the
 * compile (the page has painted by then), and `onStarted` with the running program, for Stop and input. Gives how the
 * program ended, or `null` when it didn't compile.
 */
export async function compileAndRun(
  { compiler, runner }: Playground,
  terminal: Terminal,
  source: string,
  { onCompiling = () => {}, onStarted = () => {} }: { onCompiling?: () => void; onStarted?: (running: Running) => void } = {},
): Promise<Exit | null> {
  terminal.beginRun();
  onCompiling();
  await nextPaint();
  const outcome = compiler.compile(source);
  if (!outcome.wasm) {
    terminal.diagnostics(outcome.diagnostics, "diagnostic");
    return null;
  }
  terminal.diagnostics(outcome.diagnostics, "warning");
  const running = runner.run(outcome.wasm, {
    seed: crypto.getRandomValues(new Int32Array(1))[0],
    onStdout: (text) => terminal.write(text, "stdout"),
    onStderr: (text) => terminal.write(text, "stderr"),
    onDbg: (dbg) => terminal.dbg(dbg),
    onRead: () => terminal.showInput(),
  });
  onStarted(running);
  const exit = await running.done;
  terminal.hideInput();
  terminal.end(exit);
  return exit;
}
