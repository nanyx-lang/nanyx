// The compiler built for the browser, in a worker of its own, so that the page keeps responding while it compiles:
// the first compile, the compiler's warm-up, takes seconds. `compiler.ts` starts it and posts it each program; it posts
// back the program's WebAssembly, transferred, and its diagnostics.

/** What the page posts: load the compiler, warming it up on `warmUp`, or compile `source` as the request `id`. */
export type ToCompiler = { kind: "load"; base: string; warmUp?: string } | { kind: "compile"; id: number; source: string };

/** What the worker posts back: the compiler loaded, or why it didn't, or the outcome of the compile `id`. */
export type FromCompiler =
  | { kind: "loaded" }
  | { kind: "failed"; message: string }
  | { kind: "compiled"; id: number; wasm: Uint8Array | null; diagnostics: string[] };

interface LoadedCompiler {
  compile(source: string): { wasm: Uint8Array | null; diagnostics: string[] };
}

let compiler: Promise<LoadedCompiler> | null = null;

const post = (message: FromCompiler, transfer: Transferable[] = []) => self.postMessage(message, { transfer });

self.addEventListener("message", (event: MessageEvent<ToCompiler>) => {
  const message = event.data;
  if (message.kind === "load") {
    compiler = (async () => {
      const { loadCompiler } = await import(/* @vite-ignore */ message.base + "compiler.js");
      const loaded: LoadedCompiler = await loadCompiler();
      if (message.warmUp !== undefined) loaded.compile(message.warmUp);
      return loaded;
    })();
    compiler.then(
      () => post({ kind: "loaded" }),
      (error) => post({ kind: "failed", message: String(error) }),
    );
  } else {
    // A compile is posted only once the compiler has loaded.
    void compiler!.then((loaded) => {
      const { wasm, diagnostics } = loaded.compile(message.source);
      post({ kind: "compiled", id: message.id, wasm, diagnostics }, wasm ? [wasm.buffer] : []);
    });
  }
});
