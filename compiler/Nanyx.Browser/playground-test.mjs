// The headless test of the playground host (#2150): it serves the published compiler page, opens `run.html` in
// headless Chromium, and has the page compile programs under the playground host and run them with the host's runner,
// in a web worker, checking what each printed and how it ended. From `compiler/`, after publishing the page:
//
//   dotnet publish Nanyx.Browser/Nanyx.Browser.csproj -c Release
//   node Nanyx.Browser/playground-test.mjs [<publish>/wwwroot]
//
// It needs Playwright and its Chromium (`npm install playwright` and `npx playwright install chromium`, where they
// aren't installed already). It exits with 1 when a check fails.

import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { createServer } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = process.argv[2] ?? join(here, "bin", "Release", "net10.0", "publish", "wwwroot");
const { chromium } = createRequire(import.meta.url)("playwright");

/** A port nothing listens on, as the system gives it for a moment. */
const freePort = () =>
  new Promise((resolve) => {
    const server = createServer().listen(0, () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });

/** The tour's hello world: the program of its `@entry` section. */
const tourHelloWorld = () => {
  const page = readFileSync(join(here, "..", "..", "docs", "src", "content", "tour", "attributes.md"), "utf8").replaceAll("\r\n", "\n");
  return page.match(/```nanyx\n(module main\n\n@entry\n[^`]*?)```/)[1];
};

const tests = [
  ["the tour's hello world prints, and ends with 0", async (playground) => {
    const result = await playground(tourHelloWorld());
    assert.deepEqual(result.diagnostics, []);
    assert.equal(result.stdout, "Hello, World!\n");
    assert.equal(result.stderr, "");
    assert.deepEqual(result.exit, { code: 0 });
  }],
  ["Random draws from the page's seed, as random.seeded does with the same seed", async (playground) => {
    const source = "import nanyx/random as random\n\nprintln(bits())\nprintln(random.between(1, 6))\nprintln(random.shuffle([1, 2, 3, 4, 5]))\n";
    const result = await playground(source, 42);
    assert.deepEqual(result.diagnostics, []);
    // What `random.seeded(42, ...)` prints for the same program under `nanyx run`.
    assert.equal(result.stdout, "-4767286540954276203\n4\n[5, 4, 1, 3, 2]\n");
    assert.equal((await playground(source, 42)).stdout, result.stdout);
    assert.notEqual((await playground(source, 7)).stdout, result.stdout);
  }],
  ["Clock reads the browser's time", async (playground) => {
    const result = await playground("println(now() > 1_700_000_000_000)\ndef start = ticks()\nprintln(ticks() >= start)\n");
    assert.deepEqual(result.diagnostics, []);
    assert.equal(result.stdout, "true\ntrue\n");
  }],
  ["a program that crashes shows its message, after what it printed, and ends with 1", async (playground) => {
    const result = await playground('println("before")\ndef xs = [1, 2]\nprintln(list.get(xs, 5) \\orCrash("no fifth element"))\n');
    assert.deepEqual(result.diagnostics, []);
    assert.equal(result.stdout, "before\n");
    assert.match(result.stderr, /crash: no fifth element/);
    assert.equal(result.exit.code, 1);
  }],
  ["dbg goes to the page apart from what the program writes", async (playground) => {
    const result = await playground('def answer = dbg(21 * 2)\neprintln("err")\nprintln(answer)\n');
    assert.equal(result.stdout, "42\n");
    assert.equal(result.stderr, "err\n");
    assert.deepEqual(result.dbg, [{ file: "main.nyx", line: 1, column: 14, text: "42" }]);
    assert.deepEqual(result.exit, { code: 0 });
  }],
  ["a program that needs FileSystem doesn't compile, with an error naming the host", async (playground) => {
    const result = await playground('::dependency nanyx.fs\n\nimport nanyx.fs/file as file\nimport nanyx.fs/path as path\n\nignore(file.read(path.of("notes.txt")))\n');
    assert.equal(result.exit, null);
    assert.equal(result.diagnostics.length, 1);
    assert.match(result.diagnostics[0], /^NYX0205: .*'nanyx\.playground\/playground\.host' does not provide/);
  }],
  ["Stop ends a program that never does", async (_, page) => {
    const result = await page.evaluate(() => window.playground.runAndStopAfter("def spin: int -> int = { n -> spin(n + 1) }\n\nprintln(spin(0))\n", 500));
    assert.deepEqual(result.exit, { code: null, stopped: true });
  }],
];

const port = await freePort();
const server = spawn(process.execPath, [join(here, "serve.mjs"), root, String(port)], { stdio: ["ignore", "pipe", "inherit"] });
await new Promise((resolve) => server.stdout.once("data", resolve));
const browser = await chromium.launch();
let failed = 0;
try {
  const page = await browser.newPage();
  page.on("pageerror", (error) => console.error(`page error: ${error.message}`));
  await page.goto(`http://localhost:${port}/run.html`);
  await page.waitForFunction(() => window.playgroundReady === true, null, { timeout: 120_000 });
  const playground = (source, seed = 0) => page.evaluate(([source, seed]) => window.playground.compileAndRun(source, seed), [source, seed]);
  for (const [name, test] of tests) {
    try {
      await test(playground, page);
      console.log(`passed: ${name}`);
    } catch (error) {
      failed++;
      console.error(`FAILED: ${name}\n${error.message}`);
    }
  }
} finally {
  await browser.close();
  server.kill();
}
console.log(`${tests.length - failed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
