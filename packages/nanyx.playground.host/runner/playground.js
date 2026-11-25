// The page's side of the playground host's runner: `run` starts a program in a web worker (`worker.js`) and hands what
// it does to the callbacks as it happens.

/** The size of the numbers at the start of the input buffer, in bytes, and of the line's bytes after them. */
const inputHeader = 12;
const inputCapacity = 64 * 1024;

/**
 * Whether a program can read input in this page. The worker waits for a line on a `SharedArrayBuffer`, which a page
 * has only when it is cross-origin isolated: served with `Cross-Origin-Opener-Policy: same-origin` and
 * `Cross-Origin-Embedder-Policy: require-corp`. In a page that isn't, a program's input has ended before it starts.
 */
export const canReadInput = typeof SharedArrayBuffer !== "undefined" && globalThis.crossOriginIsolated === true;

const encoder = new TextEncoder();

/**
 * Runs `wasm`, a module compiled for the playground host, with its `Random` seeded by `seed`, an integer. Calls
 * `onStdout(text)`, `onStderr(text)` and `onDbg({ file, line, column, text })` as the program writes, and `onRead()`
 * when it waits for a line of input that hasn't been given yet, which is when the page shows an input line. Gives
 * `{ done, stop, input, closeInput }`: `done` resolves to `{ code }` when the program ends, with `trap`, the trap's
 * message, when it trapped, or `{ code: null, stopped: true }` when `stop` ended it first. `input(text)` gives the
 * program the lines of `text`, in order, as it reads them (a line break at the end of `text` doesn't start another);
 * `closeInput()` ends the input, so that a read after the lines already given gives the end-of-input result.
 */
export function run(wasm, { seed = 0, onStdout = () => {}, onStderr = () => {}, onDbg = () => {}, onRead = () => {} } = {}) {
  const worker = new Worker(new URL("./worker.js", import.meta.url), { type: "module" });
  const buffer = canReadInput ? new SharedArrayBuffer(inputHeader + inputCapacity) : undefined;
  // The lines given and not yet read, as bytes; what is left of the one being read; and whether the worker waits.
  const lines = [];
  let rest = null;
  let waiting = false;
  let closed = false;

  // Answers the worker's read with the next part of the next line, or with the end of the input. The worker waits for
  // `numbers[0]` to become 1.
  const answer = () => {
    if (rest === null && lines.length === 0 && !closed) {
      waiting = true;
      onRead();
      return;
    }
    waiting = false;
    const numbers = new Int32Array(buffer, 0, 3);
    if (rest === null && lines.length > 0) rest = lines.shift();
    if (rest === null) {
      Atomics.store(numbers, 1, -1);
    } else {
      const part = rest.subarray(0, inputCapacity);
      new Uint8Array(buffer, inputHeader, part.length).set(part);
      rest = rest.length > inputCapacity ? rest.subarray(inputCapacity) : null;
      Atomics.store(numbers, 1, part.length);
      Atomics.store(numbers, 2, rest === null ? 0 : 1);
    }
    Atomics.store(numbers, 0, 1);
    Atomics.notify(numbers, 0);
  };

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
      case "read":
        answer();
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
  worker.postMessage({ wasm, seed, input: buffer });
  const stop = () => {
    worker.terminate();
    finish({ code: null, stopped: true });
  };
  const input = (text) => {
    if (closed) return;
    for (const line of text.replace(/\r?\n$/, "").split(/\r?\n/)) lines.push(encoder.encode(line));
    if (waiting) answer();
  };
  const closeInput = () => {
    closed = true;
    if (waiting) answer();
  };
  return { done, stop, input, closeInput };
}
