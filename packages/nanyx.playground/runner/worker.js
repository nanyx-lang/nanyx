// Runs a program compiled for the playground host (`nanyx.playground`) in a web worker, and posts what it does back to
// the page as it does it. The page posts `{ wasm, seed }`: the module, in the binary format, compiled with its start
// exported (`WasmOptions.ExportStart`) and `dbg` given to `nanyx:debug` (`WasmOptions.DbgToEmbedder`), and the seed of
// the program's `Random`. The worker posts, in order:
//
// - `{ kind: "stdout", text }` and `{ kind: "stderr", text }`, for what the program writes;
// - `{ kind: "dbg", file, line, column, text }`, for each `dbg` it reaches;
// - `{ kind: "exit", code }` when it ends: 0 when it ran to its end, the code it exited with, or 1 when it trapped,
//   with `trap`, the trap's message, and nothing more is posted.
//
// Stop is the page terminating the worker.

/** Thrown by `proc_exit`, so that the program unwinds to the code that called its start. */
class Exit {
  constructor(code) {
    this.code = code;
  }
}

/** SplitMix64 over `seed`, a BigInt: each call gives the next 64 bits, as a signed BigInt (an `i64`). */
function splitMix(seed) {
  const mask = (1n << 64n) - 1n;
  let state = BigInt.asUintN(64, seed);
  return () => {
    state = (state + 0x9e3779b97f4a7c15n) & mask;
    let z = state;
    z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & mask;
    z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & mask;
    return BigInt.asIntN(64, z ^ (z >> 31n));
  };
}

const decoder = new TextDecoder();

/** The imports of the playground host, `nanyx:playground`, and those the runtime's own code makes. */
function imports(memory, seed) {
  // The module's memory, read afresh each time, since it can grow.
  const bytes = (address, length) => new Uint8Array(memory().buffer, address, length);
  const text = (address, length) => decoder.decode(bytes(address, length));
  // A Nanyx string: its bytes at `address`, its byte length in the word before them.
  const string = (address) => text(address, new DataView(memory().buffer).getInt32(address - 4, true));
  const post = (kind, value) => postMessage({ kind, text: value });
  const started = performance.now();
  return {
    "nanyx:playground": {
      write: (address, length) => post("stdout", text(address, length)),
      writeError: (address, length) => post("stderr", text(address, length)),
      randomBits: splitMix(BigInt(seed)),
      now: () => BigInt(Date.now()),
      ticks: () => BigInt(Math.floor(performance.now() - started)),
    },
    // What `dbg` gives the page: it deals with every one, so the program never writes it itself.
    "nanyx:debug": {
      dbg: (file, line, column, value) => {
        postMessage({ kind: "dbg", file: string(file), line, column, text: string(value) });
        return 1;
      },
    },
    // The runtime's own code writes a deferred error's or a crash's message to stderr, and exits, through these two.
    wasi_snapshot_preview1: {
      fd_write: (fd, iovs, count, written) => {
        const view = new DataView(memory().buffer);
        let total = 0;
        let out = "";
        for (let i = 0; i < count; i++) {
          const address = view.getUint32(iovs + i * 8, true);
          const length = view.getUint32(iovs + i * 8 + 4, true);
          out += text(address, length);
          total += length;
        }
        view.setUint32(written, total, true);
        post(fd === 1 ? "stdout" : "stderr", out);
        return 0;
      },
      proc_exit: (code) => {
        throw new Exit(code);
      },
    },
  };
}

onmessage = async ({ data: { wasm, seed } }) => {
  let instance;
  try {
    ({ instance } = await WebAssembly.instantiate(wasm, imports(() => instance.exports.memory, seed)));
  } catch (error) {
    postMessage({ kind: "exit", code: 1, trap: String(error) });
    return;
  }
  try {
    instance.exports.__nanyx_start();
    postMessage({ kind: "exit", code: 0 });
  } catch (error) {
    if (error instanceof Exit) postMessage({ kind: "exit", code: error.code });
    else postMessage({ kind: "exit", code: 1, trap: String(error) });
  }
};
