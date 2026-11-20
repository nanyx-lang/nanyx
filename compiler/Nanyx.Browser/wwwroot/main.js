import { loadCompiler } from "./compiler.js";

// Loads the compiler, then compiles each example twice, timing each step. Times are milliseconds since the page
// started loading. The results are shown, and posted to /results when `serve.mjs` serves the page. With `?run=cold`
// the page then loads itself again with `run=warm`, so the second load is from the browser's cache.

const out = document.getElementById("out");
const show = (line) => { out.textContent += "\n" + line; };
out.textContent = "";

const examples = [
  ["hello", `@entry\ndef main = {\n  println("Hello, world!")\n}\n`],
  ["fizzbuzzbaz", await (await fetch("examples/fizzbuzzbaz.nyx")).text()],
  ["Nim", await (await fetch("examples/Nim.nyx")).text()],
  ["shop", await (await fetch("examples/shop.nyx")).text()],
];

// With `?library=none` the checked library is left out, and the first compile checks the standard library from its
// source.
const params = new URLSearchParams(location.search);
const compiler = await loadCompiler({ library: params.get("library") !== "none" });
const ready = performance.now();
show(`ready to compile: ${ready.toFixed(0)} ms`);

const compiles = [];
for (const [name, source] of examples) {
  for (const attempt of [1, 2]) {
    const start = performance.now();
    const outcome = compiler.compile(source);
    const total = performance.now() - start;
    const valid = outcome.wasm !== null && WebAssembly.validate(outcome.wasm);
    const entry = { name, attempt, total, check: outcome.checkMs, emit: outcome.emitMs, assemble: outcome.assembleMs, valid, diagnostics: outcome.diagnostics };
    compiles.push(entry);
    show(`${name} #${attempt}: ${total.toFixed(0)} ms (check ${outcome.checkMs.toFixed(0)}, emit ${outcome.emitMs.toFixed(0)}, assemble ${outcome.assembleMs.toFixed(0)}), valid wasm: ${valid}${outcome.diagnostics.length ? ", " + outcome.diagnostics.join("; ") : ""}`);
  }
}

const resources = performance.getEntriesByType("resource");
const transferred = resources.reduce((sum, r) => sum + r.transferSize, 0);
const encoded = resources.reduce((sum, r) => sum + r.encodedBodySize, 0);
const decoded = resources.reduce((sum, r) => sum + r.decodedBodySize, 0);
show(`resources: ${resources.length}, transferred ${(transferred / 1e6).toFixed(2)} MB, encoded ${(encoded / 1e6).toFixed(2)} MB, decoded ${(decoded / 1e6).toFixed(2)} MB`);

const run = params.get("run");
const library = params.get("library") ?? "checked";
const report = { run, library, userAgent: navigator.userAgent, ready, transferred, encoded, decoded, resources: resources.length, compiles };
try {
  await fetch("results", { method: "POST", body: JSON.stringify(report) });
} catch {
  // Served by something other than serve.mjs.
}
if (run === "cold") {
  params.set("run", "warm");
  location.search = "?" + params;
}
