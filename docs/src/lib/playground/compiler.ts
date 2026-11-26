// The compiler built for the browser and the playground host's runner, loaded once per page from where the docs build
// unpacks them (`site/playground-compiler.mjs`), and a run of a program into a terminal. The playground page and the
// tour's examples both run programs through this. The compiler runs in a worker of its own (`compile-worker.ts`), so
// the page keeps responding while it compiles; the program runs in the runner's.

import type { Exit, Terminal } from "./terminal";
import type { EditorDiagnostic, FromCompiler, ToCompiler } from "./compile-worker";

export type { EditorDiagnostic };

/** What compiling a program gives: its WebAssembly, if it compiles, and its text, and its diagnostics. */
export interface Compiled {
  wasm: Uint8Array | null;
  diagnostics: string[];
  wat: string | null;
}

/**
 * The compiler, in its worker. `check` gives the diagnostics the editor shows as the code is typed; a compiler released
 * before it had it has none, nor gives a program's WebAssembly text.
 */
export interface Compiler {
  compile(source: string): Promise<Compiled>;
  check: ((source: string) => Promise<EditorDiagnostic[]>) | null;
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
  const [compiler, runner] = await Promise.all([startCompiler(warmUp), importRunner()]);
  return { compiler, runner: runner as Runner, release };
}

/**
 * The compiler, loaded in a worker of its own, once it has compiled `warmUp`, if it is given: the first compile is the
 * compiler's warm-up, most of the wait, so that the first Run is quick.
 */
function startCompiler(warmUp: string | undefined): Promise<Compiler> {
  const worker = new Worker(new URL("./compile-worker.ts", import.meta.url), { type: "module" });
  const post = (message: ToCompiler) => worker.postMessage(message);
  // The answers awaited, by request: each request has an id of its own, whatever its kind.
  const pending = new Map<number, (answer: FromCompiler) => void>();
  let next = 0;
  const ask = (request: (id: number) => ToCompiler) =>
    new Promise<FromCompiler>((answered) => {
      const id = next++;
      pending.set(id, answered);
      post(request(id));
    });
  return new Promise((resolve, reject) => {
    worker.addEventListener("message", (event: MessageEvent<FromCompiler>) => {
      const message = event.data;
      if (message.kind === "loaded") {
        resolve({
          compile: async (source) => {
            const answer = await ask((id) => ({ kind: "compile", id, source }));
            return answer.kind === "compiled" ? { wasm: answer.wasm, diagnostics: answer.diagnostics, wat: answer.wat } : { wasm: null, diagnostics: [], wat: null };
          },
          check: message.canCheck
            ? async (source) => {
                const answer = await ask((id) => ({ kind: "check", id, source }));
                return answer.kind === "checked" ? answer.diagnostics : [];
              }
            : null,
        });
      } else if (message.kind === "failed") {
        reject(new Error(message.message));
      } else {
        pending.get(message.id)?.(message);
        pending.delete(message.id);
      }
    });
    worker.addEventListener("error", (event) => reject(new Error(event.message)));
    post({ kind: "load", base: new URL(compilerBase, location.href).href, warmUp });
  });
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

/**
 * Compiles `source` and, when it compiles, runs it, showing what happens in `terminal` under a run of its own: its
 * diagnostics, what it writes, its input line while it reads, and how it ended. `onCompiling` is called before the
 * compile, which runs in the compiler's worker while the page goes on, and `onStarted` with the running program, for
 * Stop and input. Gives how the program ended, or `null` when it didn't compile.
 */
export async function compileAndRun(
  { compiler, runner }: Playground,
  terminal: Terminal,
  source: string,
  { onCompiling = () => {}, onStarted = () => {} }: { onCompiling?: () => void; onStarted?: (running: Running) => void } = {},
): Promise<Exit | null> {
  terminal.beginRun();
  onCompiling();
  const outcome = await compiler.compile(source);
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
