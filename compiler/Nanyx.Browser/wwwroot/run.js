// A page that compiles a program under the playground host (`nanyx.playground`, which the compiler built for the
// browser runs a script under) and runs it with the host's runner, in a web worker. `window.playground.compileAndRun`
// is what the headless test (`../playground-test.mjs`) calls.

import { loadCompiler } from "./compiler.js";
import { run } from "./packages/nanyx.playground/runner/playground.js";

const compiler = await loadCompiler();

/**
 * Compiles `source` and, when it compiles, runs it with its `Random` seeded by `seed`. Gives what happened:
 * `{ diagnostics, stdout, stderr, dbg, exit }`, `exit` as the runner's `done` gives it, `null` when it didn't compile.
 * `onStarted(stop)` is called with the run's `stop` once it is running.
 */
async function compileAndRun(source, seed = 0, onStarted = () => {}) {
  const outcome = compiler.compile(source);
  const result = { diagnostics: outcome.diagnostics, stdout: "", stderr: "", dbg: [], exit: null };
  if (!outcome.wasm) return result;
  const running = run(outcome.wasm, {
    seed,
    onStdout: (text) => (result.stdout += text),
    onStderr: (text) => (result.stderr += text),
    onDbg: ({ file, line, column, text }) => result.dbg.push({ file, line, column, text }),
  });
  onStarted(running.stop);
  result.exit = await running.done;
  return result;
}

/** Runs `source` and stops it after `milliseconds`, for a test that Stop ends a program that doesn't. */
async function runAndStopAfter(source, milliseconds) {
  return compileAndRun(source, 0, (stop) => setTimeout(stop, milliseconds));
}

window.playground = { compileAndRun, runAndStopAfter };

const out = document.getElementById("out");
const runButton = document.getElementById("run");
const stopButton = document.getElementById("stop");
let stopRunning = null;
runButton.onclick = async () => {
  out.textContent = "";
  runButton.disabled = true;
  const result = await compileAndRun(document.getElementById("source").value, Number(document.getElementById("seed").value), (stop) => {
    stopRunning = stop;
    stopButton.disabled = false;
  });
  stopButton.disabled = true;
  runButton.disabled = false;
  out.textContent = [
    ...result.diagnostics,
    result.stdout,
    result.stderr && `stderr:\n${result.stderr}`,
    ...result.dbg.map(({ file, line, text }) => `[${file}:${line}] ${text}`),
    result.exit && (result.exit.stopped ? "stopped" : `exited with ${result.exit.code}${result.exit.trap ? ` (${result.exit.trap})` : ""}`),
  ].filter((part) => part).join("\n");
};
stopButton.onclick = () => stopRunning?.();
runButton.disabled = false;
out.textContent = "Ready.";
window.playgroundReady = true;
