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

const { dotnet } = await import("./_framework/dotnet.js");
const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
// The files the compiler reads from beside itself. With `?library=none` the checked library is left out, and the
// first compile checks the standard library from its source.
const params = new URLSearchParams(location.search);
const files = [
  ...(params.get("library") === "none" ? [] : ["checked-library.bin"]),
  "packages/nanyx.console/nanyx.console.nyxp",
  "packages/nanyx.console/src/console.host.nyx",
  "packages/nanyx.console/src/console.prelude.nyx",
  "packages/nanyx.console.core/nanyx.console.core.nyxp",
  "packages/nanyx.console.core/src/console.nyx",
  "packages/nanyx.console.core/src/env.nyx",
  "packages/nanyx.fs/nanyx.fs.nyxp",
  "packages/nanyx.fs/src/dir.nyx",
  "packages/nanyx.fs/src/file.nyx",
  "packages/nanyx.fs/src/filesystem.nyx",
  "packages/nanyx.fs/src/path.nyx",
  "packages/nanyx.http.core/nanyx.http.core.nyxp",
  "packages/nanyx.http.core/src/client.nyx",
  "packages/nanyx.http.core/src/http.nyx",
  "packages/nanyx.http.core/src/request.nyx",
  "packages/nanyx.http.core/src/response.nyx",
  "packages/nanyx.http.core/src/wire.nyx",
];
const contents = await Promise.all(files.map(async (file) => new Uint8Array(await (await fetch(file)).arrayBuffer())));
files.forEach((file, index) => exports.BrowserCompiler.AddFile(file, contents[index]));
const ready = performance.now();
show(`ready to compile: ${ready.toFixed(0)} ms`);

const compiles = [];
for (const [name, source] of examples) {
  for (const attempt of [1, 2]) {
    const start = performance.now();
    const outcome = JSON.parse(exports.BrowserCompiler.Compile(source));
    const total = performance.now() - start;
    let valid = false;
    if (outcome.wasm) {
      const bytes = Uint8Array.from(atob(outcome.wasm), (c) => c.charCodeAt(0));
      valid = WebAssembly.validate(bytes);
    }
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
