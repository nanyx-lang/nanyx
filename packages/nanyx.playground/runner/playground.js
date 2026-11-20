// The page's side of the playground host's runner: `run` starts a program in a web worker (`worker.js`) and hands what
// it does to the callbacks as it happens.

/**
 * Runs `wasm`, a module compiled for the playground host, with its `Random` seeded by `seed`, an integer. Calls
 * `onStdout(text)`, `onStderr(text)` and `onDbg({ file, line, column, text })` as the program writes, and gives
 * `{ done, stop }`: `done` resolves to `{ code }` when the program ends, with `trap`, the trap's message, when it trapped,, or `{ code: null, stopped: true }` when
 * `stop` ended it first.
 */
export function run(wasm, { seed = 0, onStdout = () => {}, onStderr = () => {}, onDbg = () => {} } = {}) {
  const worker = new Worker(new URL("./worker.js", import.meta.url), { type: "module" });
  let finish;
  const done = new Promise((resolve) => (finish = resolve));
  worker.onmessage = ({ data }) => {
    switch (data.kind) {
      case "stdout":
        onStdout(data.text);
        break;
      case "stderr":
        onStderr(data.text);
        break;
      case "dbg":
        onDbg(data);
        break;
      case "exit":
        worker.terminate();
        finish(data.trap === undefined ? { code: data.code } : { code: data.code, trap: data.trap });
        break;
    }
  };
  worker.onerror = (event) => {
    worker.terminate();
    finish({ code: 1, trap: event.message });
  };
  worker.postMessage({ wasm, seed });
  const stop = () => {
    worker.terminate();
    finish({ code: null, stopped: true });
  };
  return { done, stop };
}
