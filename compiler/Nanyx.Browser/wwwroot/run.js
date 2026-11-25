// A page that compiles a program under the playground host (`nanyx.playground.host`, which the compiler built for the
// browser runs a script under) and runs it with the host's runner, in a web worker. What the program writes shows as it
// writes it, and when it reads, an input line shows under it. `window.playground` is what the headless test
// (`../playground-test.mjs`) calls.

import { loadCompiler } from "./compiler.js";
import { canReadInput, run } from "./packages/nanyx.playground.host/runner/playground.js";

const compiler = await loadCompiler();

/**
 * Compiles `source` and, when it compiles, runs it with its `Random` seeded by `seed`. Gives what happened:
 * `{ diagnostics, stdout, stderr, dbg, exit }`, `exit` as the runner's `done` gives it, `null` when it didn't compile.
 * `onStarted(running)` is called with the runner's `{ stop, input, closeInput }` once it is running, `onOutput(text)`
 * with what it writes, to either stream, and `onRead()` when it waits for input.
 */
async function compileAndRun(source, { seed = 0, onStarted = () => {}, onOutput = () => {}, onRead = () => {} } = {}) {
  const outcome = compiler.compile(source);
  const result = { diagnostics: outcome.diagnostics, stdout: "", stderr: "", dbg: [], exit: null };
  if (!outcome.wasm) return result;
  const running = run(outcome.wasm, {
    seed,
    onStdout: (text) => {
      result.stdout += text;
      onOutput(text);
    },
    onStderr: (text) => {
      result.stderr += text;
      onOutput(text);
    },
    onDbg: ({ file, line, column, text }) => result.dbg.push({ file, line, column, text }),
    onRead,
  });
  onStarted(running);
  result.exit = await running.done;
  return result;
}

/** Runs `source` and stops it after `milliseconds`, for a test that Stop ends a program that doesn't. */
async function runAndStopAfter(source, milliseconds) {
  return compileAndRun(source, { onStarted: ({ stop }) => setTimeout(stop, milliseconds) });
}

/** Runs `source` with `text` given as its input before it starts, and the input then ended. */
async function runWithInput(source, text) {
  return compileAndRun(source, {
    onStarted: ({ input, closeInput }) => {
      input(text);
      closeInput();
    },
  });
}

window.playground = { canReadInput, compileAndRun: (source, seed) => compileAndRun(source, { seed }), runAndStopAfter, runWithInput };

const out = document.getElementById("out");
const runButton = document.getElementById("run");
const stopButton = document.getElementById("stop");
const stdin = document.getElementById("stdin");
let running = null;
// The input line: shown while the program waits for a line. Enter gives it the line, which stays in the output, and
// Ctrl+D ends the input.
stdin.onkeydown = (event) => {
  if (event.key === "Enter") {
    out.textContent += stdin.value + "\n";
    running?.input(stdin.value);
    stdin.value = "";
    stdin.hidden = true;
  } else if (event.key === "d" && event.ctrlKey) {
    event.preventDefault();
    running?.closeInput();
    stdin.hidden = true;
  }
};
runButton.onclick = async () => {
  out.textContent = "";
  runButton.disabled = true;
  const result = await compileAndRun(document.getElementById("source").value, {
    seed: Number(document.getElementById("seed").value),
    onStarted: (started) => {
      running = started;
      stopButton.disabled = false;
    },
    onOutput: (text) => (out.textContent += text),
    onRead: () => {
      stdin.hidden = false;
      stdin.focus();
    },
  });
  running = null;
  stdin.hidden = true;
  stopButton.disabled = true;
  runButton.disabled = false;
  out.textContent += [
    ...result.diagnostics,
    ...result.dbg.map(({ file, line, text }) => `[${file}:${line}] ${text}`),
    result.exit && (result.exit.stopped ? "stopped" : `exited with ${result.exit.code}${result.exit.trap ? ` (${result.exit.trap})` : ""}`),
  ].filter((part) => part).join("\n");
};
stopButton.onclick = () => running?.stop();
runButton.disabled = false;
out.textContent = "Ready.";
window.playgroundReady = true;
