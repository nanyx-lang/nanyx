// The compiler built for the browser, in a worker of its own, so that the page keeps responding while it compiles:
// the first compile, the compiler's warm-up, takes seconds. `compiler.ts` starts it and posts it each program; it posts
// back the program's WebAssembly, transferred, and its diagnostics.

/** A diagnostic as the editor shows it, where it is in the code, `from` and `to`, or `null` for one elsewhere. */
export interface EditorDiagnostic {
  code: string;
  headline: string;
  body: string;
  error: boolean;
  from: number | null;
  to: number | null;
}

/**
 * What the page posts: load the compiler, warming it up on `warmUp`, or compile `source`, or check it for the editor's
 * diagnostics, as the request `id`.
 */
export type ToCompiler =
  | { kind: "load"; base: string; warmUp?: string }
  | { kind: "compile"; id: number; source: string }
  | { kind: "check"; id: number; source: string };

/**
 * What the worker posts back: the compiler loaded, and whether it can check code for the editor and give a program's
 * WebAssembly text, which a compiler released before it could didn't; or why it didn't load; or the outcome of the
 * request `id`.
 */
export type FromCompiler =
  | { kind: "loaded"; canCheck: boolean }
  | { kind: "failed"; message: string }
  | { kind: "compiled"; id: number; wasm: Uint8Array | null; diagnostics: string[]; wat: string | null }
  | { kind: "checked"; id: number; diagnostics: EditorDiagnostic[] };

interface LoadedCompiler {
  compile(source: string): { wasm: Uint8Array | null; diagnostics: string[]; wat?: string | null };
  check?(source: string): EditorDiagnostic[];
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
      (loaded) => post({ kind: "loaded", canCheck: typeof loaded.check === "function" }),
      (error) => post({ kind: "failed", message: String(error) }),
    );
  } else if (message.kind === "compile") {
    // A compile, or a check, is posted only once the compiler has loaded.
    void compiler!.then((loaded) => {
      const { wasm, diagnostics, wat } = loaded.compile(message.source);
      post({ kind: "compiled", id: message.id, wasm, diagnostics, wat: wat ?? null }, wasm ? [wasm.buffer] : []);
    });
  } else {
    void compiler!.then((loaded) => post({ kind: "checked", id: message.id, diagnostics: loaded.check!(message.source) }));
  }
});
