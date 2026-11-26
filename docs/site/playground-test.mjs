// The headless test of the playground page (#2153) and the tour's runnable examples (#2248): it serves the built site as Vercel does, with the playground
// cross-origin isolated (`vercel.json`), opens `/playground` in headless Chromium, and checks that the page loads the
// compiler, runs hello world, gives a program its input, keeps each run's output, runs tasks that take turns where one
// sleeps, and opens share links and the
// tour's links without running them; and that every example in its menu, the tour's runnable ones, and every example
// that an "Open in the playground" link on another page opens, runs under the playground host; and that a tour page loads the compiler only on an example's first Run, runs an example where it is,
// edits and runs it again, and opens it in the playground as edited; and that an example that only declares things gets
// only its playground link. From `docs/`, with a compiler built for the browser:
//
//   NANYX_BROWSER_ZIP=<nanyx-browser.zip> npm run build
//   node site/playground-test.mjs
//
// It needs Playwright and its Chromium (`npm install --no-save playwright` and `npx playwright install chromium`, where
// they aren't installed already). It exits with 1 when a check fails.

import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { createRequire } from "node:module";
import { dirname, extname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { inflateRawSync } from "node:zlib";
import { remarkPlaygroundLinks } from "./playground-examples.mjs";

const docs = dirname(dirname(fileURLToPath(import.meta.url)));
const dist = join(docs, "dist");
const { chromium } = createRequire(join(docs, "package.json"))("playwright");

if (!existsSync(join(dist, "playground", "compiler", "compiler.js"))) {
  console.error("The site in dist/ has no playground compiler: build it with NANYX_BROWSER_ZIP set (see the top of this file).");
  process.exit(1);
}

const types = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript",
  ".mjs": "text/javascript",
  ".css": "text/css",
  ".json": "application/json",
  ".wasm": "application/wasm",
  ".svg": "image/svg+xml",
  ".txt": "text/plain; charset=utf-8",
  ".ttf": "font/ttf",
  ".woff2": "font/woff2",
};

/** Serves `dist` with clean URLs, and the playground's route with the headers `vercel.json` gives it. */
const server = createServer((request, response) => {
  const pathname = decodeURIComponent(new URL(request.url ?? "/", "http://localhost").pathname);
  const candidates = [pathname, pathname + ".html", join(pathname, "index.html")];
  const file = candidates
    .map((candidate) => normalize(join(dist, candidate)))
    .find((path) => (path === dist || path.startsWith(dist + sep)) && existsSync(path) && statSync(path).isFile());
  if (!file) {
    response.writeHead(404).end();
    return;
  }
  const headers = { "Content-Type": types[extname(file)] ?? "application/octet-stream" };
  if (pathname === "/playground" || pathname.startsWith("/playground/")) {
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Embedder-Policy"] = "require-corp";
  }
  response.writeHead(200, headers).end(readFileSync(file));
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const origin = `http://127.0.0.1:${server.address().port}`;

/** A program that echoes each line it reads, until its input ends. */
const echo = `def echo: [Console] () -> () = { _ ->
  match readLine()
    | #some(line) ->
      println("echo: " ++ line)
      echo()
    | _ -> println("end of input")
}

echo()
`;

/** A program whose two tasks take turns where one sleeps, which the playground host provides `Async` for. */
const tasks = `def later: [Async, Clock, Console] string -> () = { text ->
  sleep(100ms)
  println(text)
}

def both = { _ ->
  def first = start! later("the sleeper wakes")
  def second = start! println("another task runs meanwhile")
  await(first)
  await(second)
}

both()
`;

const browser = await chromium.launch();
const context = await browser.newContext();
await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin });
/** What the page fetched of the compiler, to check that a tour page fetches nothing of it until Run. */
const compilerRequests = [];
const errors = [];
const watch = (watched) => {
  watched.on("request", (request) => {
    if (new URL(request.url()).pathname.startsWith("/playground/compiler/")) compilerRequests.push(request.url());
  });
  watched.on("pageerror", (error) => errors.push(String(error)));
  // Choosing an example over changed code asks first; the test says yes.
  watched.on("dialog", (dialog) => dialog.accept());
};
let page = await context.newPage();
watch(page);

/** The editor's code, from its lines (`innerText` doubles an empty line). */
const linesOf = (target) => target.locator(".cm-line").allTextContents().then((lines) => lines.join("\n").replace(/\u00a0/g, " ").trimEnd());
const editorText = () => linesOf(page);
const outputText = () => page.locator("[data-log]").innerText();
const readyOn = (target) => target.waitForSelector("html[data-playground=ready]", { timeout: 180_000 });
const ready = () => readyOn(page);
const idleOn = (target) => target.waitForSelector("[data-run]:not([hidden]):not([disabled])", { timeout: 60_000 });
const idle = () => idleOn(page);
/**
 * Clicks Run on `target` and waits for the run it starts to begin, by the part of the output pane each run adds, which
 * stays. Waiting for the button to be hidden instead missed a run quick enough to give it back between two looks, and
 * a page behind the others, whose animation frames are throttled, looks seldom: so the waits poll on a timer (#2554).
 */
const startRunOn = async (target) => {
  const runs = await target.locator(".pg-run").count();
  await target.click("[data-run]");
  await target.waitForFunction((before) => document.querySelectorAll(".pg-run").length > before, runs, { polling: 100 });
};
const runOn = async (target) => {
  await startRunOn(target);
  await idleOn(target);
};
const run = () => runOn(page);
const setEditorOn = async (target, code) => {
  await target.click(".cm-content");
  await target.keyboard.press("ControlOrMeta+a");
  await target.keyboard.insertText(code);
};
const setEditor = (code) => setEditorOn(page, code);
/** The code of the tour page's first example that has a Run button: the block before its bar. */
const firstRunnableCode = () =>
  page.locator("[data-tour-run]:has(.tour-run-button)").first().locator("xpath=preceding-sibling::*[1]").locator("xpath=descendant-or-self::pre").first();
/** Runs the editor's code, ending its input whenever it waits for some, until it ends. */
const runToTheEndOn = async (target) => {
  await startRunOn(target);
  const input = target.locator("[data-input-line] input");
  for (;;) {
    await target.waitForFunction(
      () =>
        !document.querySelector("[data-input-line]")?.hidden ||
        (!document.querySelector("[data-run]")?.hidden && !document.querySelector("[data-run]")?.disabled),
      null,
      { timeout: 60_000, polling: 100 },
    );
    if (!(await input.isVisible())) break;
    await input.press("Control+d");
  }
  await idleOn(target);
};
const runToTheEnd = () => runToTheEndOn(page);

/** How many playground pages the checks of every example run on at once, each with a compiler of its own (#2554). */
const pagesAtOnce = 4;

/**
 * The problems `each` finds, given a playground page and one of `items`, with `items` split over `pagesAtOnce` pages
 * that each load the playground and go through their share in turn.
 */
const onPages = async (items, each) => {
  const shares = Array.from({ length: pagesAtOnce }, (_, share) => items.filter((_, index) => index % pagesAtOnce === share));
  const found = await Promise.all(
    shares.map(async (share) => {
      const target = await context.newPage();
      watch(target);
      await target.goto(`${origin}/playground`);
      await readyOn(target);
      const problems = [];
      for (const item of share) {
        const problem = await each(target, item);
        if (problem) problems.push(problem);
      }
      await target.close();
      return problems;
    }),
  );
  return found.flat();
};

/**
 * The code each "Open in the playground" link on the built pages outside the tour opens, which the tour's are in the
 * playground's menu: each link's fragment, decoded as the playground decodes it.
 */
const otherPagesExamples = () => {
  const pages = (/** @type {string} */ directory) =>
    readdirSync(directory, { withFileTypes: true }).flatMap((entry) =>
      entry.isDirectory() ? pages(join(directory, entry.name)) : entry.name.endsWith(".html") ? [join(directory, entry.name)] : [],
    );
  return pages(join(dist, "docs"))
    .filter((file) => !file.startsWith(join(dist, "docs", "tour") + sep))
    .sort()
    .flatMap((file) =>
      [...readFileSync(file, "utf8").matchAll(/class="tour-run-open" href="\/playground#code=([\w-]+)"/g)].map((link) => ({
        page: file.slice(dist.length + 1),
        code: inflateRawSync(Buffer.from(link[1], "base64url")).toString("utf8"),
      })),
    );
};
const helloWorld = JSON.parse(readFileSync(join(dist, "playground.html"), "utf8").match(/<script id="playground-data" type="application\/json">(.*?)<\/script>/s)[1]).defaultExample.code;

const tests = [
  ["the page loads the compiler and starts with the tour's hello world", async () => {
    await page.goto(`${origin}/playground`);
    assert.equal(await page.evaluate(() => crossOriginIsolated), true);
    await ready();
    assert.equal(await editorText(), helloWorld.trimEnd());
    assert.match(await page.locator("[data-release]").innerText(), /Nanyx/);
  }],
  ["Run runs hello world", async () => {
    await run();
    assert.equal((await page.locator(".pg-run").last().innerText()).trim(), "Hello, World!");
  }],
  ["a program reads the lines typed into the input line, and Ctrl+D ends its input", async () => {
    await setEditor(echo);
    await page.click("[data-run]");
    const input = page.locator("[data-input-line] input");
    await input.waitFor({ state: "visible", timeout: 60_000 });
    await input.fill("hello");
    await input.press("Enter");
    await page.waitForFunction(() => document.querySelector("[data-log]")?.textContent?.includes("echo: hello"));
    await input.waitFor({ state: "visible" });
    await input.press("Control+d");
    await page.waitForFunction(() => document.querySelector("[data-log]")?.textContent?.includes("end of input"));
    await idle();
    assert.equal((await page.locator(".pg-run").last().innerText()).replace(/^Run 2.*\n/, "").trim(), "hello\necho: hello\nend of input");
  }],
  ["each run keeps the output before it, under a divider", async () => {
    assert.equal(await page.locator(".pg-run").count(), 2);
    assert.match(await page.locator(".pg-divider").first().innerText(), /^Run 2/);
    assert.match(await outputText(), /Hello, World![\s\S]*echo: hello/);
  }],
  ["a program that doesn't compile shows its errors, linked to the error reference", async () => {
    await setEditor('println(1 + "a")\n');
    await run();
    const link = page.locator(".pg-run").last().locator(".pg-diagnostic a").first();
    assert.match(await link.getAttribute("href"), /^\/docs\/standard-tools\/errors#nyx\d{4}$/);
  }],
  ["tasks take turns where one sleeps", async () => {
    await setEditor(tasks);
    await runToTheEnd();
    const printed = (await page.locator(".pg-run").last().innerText()).replace(/^Run \d+.*\n/, "").trim();
    assert.equal(printed, "another task runs meanwhile\nthe sleeper wakes");
  }],
  ["Share copies a link that opens the code without running it", async () => {
    await setEditor(echo);
    await page.click("[data-share]");
    await page.waitForFunction(() => document.querySelector("[data-share]")?.dataset.link);
    const link = await page.evaluate(() => navigator.clipboard.readText());
    assert.match(link, /\/playground#code=[\w-]+$/);
    const opened = await context.newPage();
    await opened.goto(link);
    await opened.waitForFunction(() => document.querySelector("[data-log]")?.textContent?.includes("Opened the code from a link"));
    assert.equal(await linesOf(opened), echo.trimEnd());
    assert.equal(await opened.locator(".pg-stdout").count(), 0);
    await opened.close();
  }],
  ["a link that doesn't decode opens hello world and says so", async () => {
    // From another page, since going from the playground to it changes only the fragment.
    await page.goto("about:blank");
    await page.goto(`${origin}/playground#code=not-a-link`);
    await page.waitForFunction(() => document.querySelector("[data-log]")?.textContent?.includes("couldn't be read"));
    assert.equal(await editorText(), helloWorld.trimEnd());
  }],
  ["reloading keeps the code last written", async () => {
    await ready();
    await setEditor(echo);
    await page.waitForTimeout(500);
    await page.goto(`${origin}/playground`);
    assert.equal(await editorText(), echo.trimEnd());
  }],
  ["the tour's examples link to the playground, which opens them", async () => {
    await page.goto(`${origin}/docs/tour/attributes`);
    const link = page.locator(".tour-run-open").first();
    assert.equal(await link.getAttribute("data-astro-reload"), "");
    await link.click();
    await page.waitForURL(/\/playground$/);
    assert.equal(await page.evaluate(() => crossOriginIsolated), true);
    assert.equal(await editorText(), helloWorld.trimEnd());
  }],
  ["every example in the menu runs under the playground host", async () => {
    await ready();
    const options = await page.locator("[data-examples] option").evaluateAll((all) => all.map((o) => o.value).filter((v) => v));
    assert.ok(options.length > 100, `the menu has ${options.length} examples`);
    const failed = await onPages(options, async (target, id) => {
      await target.selectOption("[data-examples]", id);
      await runOn(target);
      const problems = await target.locator(".pg-run").last().locator(".pg-diagnostic, .pg-stderr, .pg-end").allInnerTexts();
      return problems.length > 0 ? `${id}: ${problems.join(" ").trim()}` : undefined;
    });
    assert.deepEqual(failed, []);
  }],
  ["every runnable example on every other page opens and runs under the playground host", async () => {
    await page.goto(`${origin}/playground`);
    await ready();
    const examples = otherPagesExamples();
    assert.ok(examples.length > 150, `the pages outside the tour have ${examples.length} runnable examples`);
    const failed = await onPages(examples, async (target, { page: from, code }) => {
      await setEditorOn(target, code);
      await runToTheEndOn(target);
      const problems = await target.locator(".pg-run").last().locator(".pg-diagnostic, .pg-stderr, .pg-end").allInnerTexts();
      return problems.length > 0 ? `${from}: ${problems.join(" ").trim()}\n${code}` : undefined;
    });
    assert.deepEqual(failed, []);
  }],
  ["a tour page doesn't load the compiler until an example's Run is pressed", async () => {
    // A page of its own, so that nothing the playground fetched is counted.
    await page.close();
    page = await context.newPage();
    watch(page);
    compilerRequests.length = 0;
    await page.goto(`${origin}/docs/tour/variables`);
    await page.locator(".tour-run-button").first().waitFor();
    await page.waitForTimeout(1000);
    assert.deepEqual(compilerRequests, []);
    assert.equal(await page.evaluate(() => crossOriginIsolated), false);
  }],
  ["a tour example runs where it is", async () => {
    const run = page.locator(".tour-run-button").first();
    await run.click();
    await page.waitForFunction(() => document.querySelector(".tour-run-button")?.textContent === "Run", null, { timeout: 180_000 });
    assert.ok(compilerRequests.length > 0);
    const output = page.locator(".tour-run-output").first();
    assert.equal(await output.isVisible(), true);
    assert.equal(await output.locator(".pg-diagnostic, .pg-stderr, .pg-end").count(), 0);
  }],
  ["a tour example is edited in place, runs as edited, and Reset gives back its code", async () => {
    const code = firstRunnableCode();
    const original = await code.textContent();
    await code.click();
    const editor = page.locator(".tour-run-editor .cm-content").first();
    await editor.waitFor();
    await page.keyboard.press("ControlOrMeta+a");
    await page.keyboard.insertText('println("edited in place")\n');
    await page.locator(".tour-run-button").first().click();
    const output = page.locator(".tour-run-output").first();
    await output.locator(".pg-stdout", { hasText: "edited in place" }).waitFor({ timeout: 60_000 });
    assert.equal(await output.locator(".pg-run").count(), 2);
    const reset = page.locator(".tour-run-reset").first();
    assert.equal(await reset.isVisible(), true);
    await reset.click();
    assert.equal(await linesOf(page.locator(".tour-run-editor").first()), original.trimEnd());
    assert.equal(await reset.isVisible(), false);
  }],
  ["Open in the playground opens an edited example as edited", async () => {
    await page.locator(".tour-run-editor .cm-content").first().click();
    await page.keyboard.press("ControlOrMeta+a");
    await page.keyboard.insertText('println("opened as edited")\n');
    await page.locator("[data-tour-run]:has(.tour-run-button) .tour-run-open").first().click();
    await page.waitForURL(/\/playground$/);
    await ready();
    assert.match(await editorText(), /println\("opened as edited"\)/);
  }],
  ["an example that only declares things gets only the playground link, and isn't editable", async () => {
    const tree = { type: "root", children: [{ type: "code", lang: "nanyx", meta: "", value: "type Point = (x: int, y: int)\n\ndef origin = (x = 0, y = 0)" }] };
    remarkPlaygroundLinks()(tree, { path: "/docs/src/content/tour/example.md" });
    assert.match(tree.children[1].value, /data-no-output/);
    // The piping page's examples only define things.
    await page.goto(`${origin}/docs/tour/piping`);
    const bar = page.locator("[data-tour-run]").first();
    await bar.waitFor();
    assert.equal(await bar.getAttribute("data-no-output"), "");
    assert.equal(await page.locator(".tour-run-button").count(), 0);
    assert.equal(await bar.locator(".tour-run-open").count(), 1);
    await page.locator(".prose pre").first().click();
    await page.waitForTimeout(500);
    assert.equal(await page.locator(".tour-run-editor").count(), 0);
    assert.equal(await page.locator(".tour-run-output").count(), 0);
  }],
  ["an example that needs another host, and the error reference's, get no playground link", async () => {
    for (const value of ["match args()\n  | [name, ..._] -> println(name)\n  | [] -> ()", "::host acme.server ../server\n\nprintln(1)", "::install nanyx.json 0.2.0\n\nprintln(1)"]) {
      const tree = { type: "root", children: [{ type: "code", lang: "nanyx", meta: "", value }] };
      remarkPlaygroundLinks()(tree, { path: "/docs/src/content/advanced/example.md" });
      assert.equal(tree.children.length, 1, value);
    }
    // A program that declares its own `Request` is its own.
    const own = { type: "root", children: [{ type: "code", lang: "nanyx", meta: "", value: "type Request = (id: int)\n\nprintln(Request(id = 1))" }] };
    remarkPlaygroundLinks()(own, { path: "/docs/src/content/advanced/example.md" });
    assert.equal(own.children.length, 2);
    assert.doesNotMatch(readFileSync(join(dist, "docs", "standard-tools", "errors.html"), "utf8"), /class="tour-run-open"/);
  }],
  ["an example that reads input gets only the playground link", async () => {
    const tree = { type: "root", children: [{ type: "code", lang: "nanyx", meta: "", value: "match readLine()\n  | #some(line) -> println(line)\n  | _ -> ()" }] };
    remarkPlaygroundLinks()(tree, { path: "/docs/src/content/tour/example.md" });
    const html = tree.children[1].value;
    assert.match(html, /data-reads-input/);
    await page.goto(`${origin}/docs/tour/variables`);
    await page.locator(".tour-run-button").first().waitFor();
    await page.evaluate((bar) => {
      document.querySelector(".prose")?.insertAdjacentHTML("beforeend", "<pre><code>readLine()</code></pre>" + bar);
      document.dispatchEvent(new Event("astro:page-load"));
    }, html);
    const added = page.locator("[data-tour-run]").last();
    assert.equal(await added.locator(".tour-run-button").count(), 0);
    assert.equal(await added.locator(".tour-run-open").count(), 1);
  }],
];

let failures = 0;
for (const [name, test] of tests) {
  try {
    await test();
    console.log(`ok   ${name}`);
  } catch (error) {
    failures++;
    console.log(`FAIL ${name}\n${error?.stack ?? error}`);
  }
}
if (errors.length > 0) {
  failures++;
  console.log(`FAIL the page threw:\n${errors.join("\n")}`);
}
await browser.close();
server.close();
console.log(failures === 0 ? `All ${tests.length} checks passed.` : `${failures} failed.`);
process.exit(failures === 0 ? 0 : 1);
