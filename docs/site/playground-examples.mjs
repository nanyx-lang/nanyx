// The playground's examples, and the links that open an example in the playground. The examples menu is every runnable
// example in the language tour, grouped by its page; the links go under every runnable example on every page of the
// docs. An example is runnable when its block is fenced ```nanyx with nothing after it (not a fragment, nor skipped) and
// it doesn't show an error: these are the blocks `RunDocExampleTests.fs` compiles and runs. The playground runs one
// only if it needs no other host than the playground's (`needsAnotherHost`).
// Plain JS so that both astro.config.mjs and the playground page can import it. What a runnable example is, and the
// code it runs as, are in `playground-code.mjs`, which the docs' pages read in the browser too.

import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateRawSync } from "node:zlib";
import { visit } from "unist-util-visit";
import { isRunnable, playgroundCode, readsInput, runsCode } from "./playground-code.mjs";

/**
 * @typedef {{ id: string; title: string; code: string }} PlaygroundExample
 * @typedef {{ page: string; title: string; examples: PlaygroundExample[] }} PlaygroundExampleGroup
 */

/**
 * The fragment of a share link to `code`: `code=` and the code, compressed with raw deflate and written in base64url.
 * The playground page decodes it with `DecompressionStream("deflate-raw")`.
 *
 * @param {string} code
 */
export function shareFragment(code) {
  return "code=" + deflateRawSync(Buffer.from(code, "utf8"), { level: 9 }).toString("base64url");
}

/** The link that opens `code`, a docs example, in the playground. */
export function playgroundLink(/** @type {string} */ code) {
  return "/playground#" + shareFragment(playgroundCode(code));
}

/**
 * The repository, the first directory above this file, or above the directory the build runs in, with the console
 * host's package: the build bundles this file into the site's own chunks, which are elsewhere.
 */
const repository = (() => {
  for (let directory of [dirname(fileURLToPath(import.meta.url)), process.cwd()]) {
    for (;;) {
      if (existsSync(join(directory, "packages", "nanyx.console"))) return directory;
      if (dirname(directory) === directory) break;
      directory = dirname(directory);
    }
  }
  throw new Error("The repository's packages/nanyx.console wasn't found above the docs site.");
})();

/** The source file of a module named as a prelude names it: `nanyx.fs/filesystem` or `nanyx/async`. */
function moduleFile(/** @type {string} */ module) {
  const [owner, name] = module.split("/");
  return owner === "nanyx" ? join(repository, "stdlib", "nanyx", `${name}.nyx`) : join(repository, "packages", owner, "src", `${name}.nyx`);
}

/**
 * What a host's prelude brings into every program: `export <module> (<names>)` lines, as a map from each name to the
 * module it comes from.
 *
 * @param {string} file
 */
function preludeNames(file) {
  /** @type {Map<string, string>} */
  const names = new Map();
  for (const match of readFileSync(file, "utf8").matchAll(/^export ([\w./]+) \(([^)]*)\)/gm)) {
    for (const name of match[2].split(",")) names.set(name.trim(), match[1]);
  }
  return names;
}

/** The members of the context `name` that `module` exports, by name: each of its lines `  member: type`. */
function contextMembers(/** @type {string} */ module, /** @type {string} */ name) {
  const file = moduleFile(module);
  if (!existsSync(file)) return [];
  const lines = readFileSync(file, "utf8").replace(/\r\n/g, "\n").split("\n");
  const start = lines.findIndex((line) => line.startsWith(`export context ${name} = (`));
  if (start < 0) return [];
  const members = [];
  for (let index = start + 1; index < lines.length && !lines[index].startsWith(")"); index++) {
    const member = /^  (\w+)\s*:/.exec(lines[index]);
    if (member) members.push(member[1]);
  }
  return members;
}

/**
 * What only the console host gives a program, and the playground's host doesn't: the names its prelude brings in that
 * the playground's doesn't, with the members of the contexts among them (`args`, `exit`, `readFile`, ...); and what an
 * import of the modules they come from names: the module (`nanyx.console.core/env`) when the playground's prelude takes
 * something else from its package, else the package (`nanyx.fs`).
 *
 * @type {{ names: Set<string>; imports: Set<string> } | null}
 */
let consoleOnly = null;

function consoleOnlyNames() {
  if (consoleOnly) return consoleOnly;
  const console = preludeNames(join(repository, "packages", "nanyx.console", "src", "console.prelude.nyx"));
  const playground = preludeNames(join(repository, "packages", "nanyx.playground", "src", "playground.prelude.nyx"));
  const playgroundPackages = new Set([...playground.values()].map((module) => module.split("/")[0]));
  const names = new Set();
  const imports = new Set();
  for (const [name, module] of console) {
    if (playground.has(name)) continue;
    names.add(name);
    const owner = module.split("/")[0];
    imports.add(playgroundPackages.has(owner) ? module : owner);
    for (const member of contextMembers(module, name)) names.add(member);
  }
  consoleOnly = { names, imports };
  return consoleOnly;
}

/**
 * Whether a program needs a host other than the playground's, which the playground can't run, so it gets no playground
 * link: whether it names a host with `::host` (but for `::host nanyx.playground`); or depends on a package from the
 * index, `::dependency nanyx.json 0.2.0`, which the playground can't download; or imports or depends on a module
 * that only the console host can run, such as `nanyx.fs`'s; or uses, outside a comment, a name that only the console
 * host's prelude brings in, such as `args`, `exit` or `FileSystem`, and that it doesn't declare itself. What the
 * console host brings in and the playground's doesn't is read from the two hosts' preludes (`packages/nanyx.console`
 * and `packages/nanyx.playground`).
 *
 * @param {string} code
 */
export function needsAnotherHost(code) {
  const { names, imports } = consoleOnlyNames();
  const lines = code.replace(/\r\n/g, "\n").split("\n").map((line) => line.replace(/--.*$/, ""));
  for (const line of lines) {
    const host = /^::host\s+(\S+)/.exec(line);
    if (host && host[1] !== "nanyx.playground") return true;
    // A package from the index, which the playground can't download; the packages the compiler ships, it has.
    const dependency = /^::dependency\s+(\S+)\s+\d/.exec(line);
    if (dependency && !existsSync(join(repository, "packages", dependency[1]))) return true;
    // An import, a dependency, or a line of an `import (...)` group.
    const imported = /^(::dependency|import)\s/.test(line) || /^\s+[\w.]+\/[\w/]+(\s|$)/.test(line);
    if (imported && [...imports].some((module) => new RegExp(`(^|\\s|\\()${module.replace(/\./g, "\\.")}(\\/|\\s|$)`).test(line))) return true;
  }
  // A name the program declares itself, such as its own `type Request`, is its own.
  const declared = new Set(lines.flatMap((line) => [...line.matchAll(/\b(?:type|def|context|pattern)\s+([A-Za-z_]\w*)/g)].map((match) => match[1])));
  return lines.some((line) => (line.match(/[A-Za-z_]\w*/g) ?? []).some((word) => names.has(word) && !declared.has(word)));
}

/**
 * Whether a ```nanyx block, with `meta` the words after `nanyx` on its fence, opens in the playground: it is runnable
 * (`isRunnable`), is one file (not files that each start `-- file: <name>`), and needs no other host.
 *
 * @param {string} meta
 * @param {string} code
 */
export function opensInPlayground(meta, code) {
  return isRunnable(meta, code) && !code.startsWith("-- file:") && !needsAnotherHost(code);
}

/**
 * The ```nanyx blocks of a markdown page, with their fence's words and the heading above them.
 *
 * @param {string} text
 * @returns {{ meta: string; code: string; heading: string }[]}
 */
function blocksOf(text) {
  const lines = text.replace(/\r\n/g, "\n").split("\n");
  const blocks = [];
  let heading = "";
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].trim();
    const headingMatch = /^#{1,6}\s+(.*)$/.exec(lines[index]);
    if (headingMatch) heading = headingMatch[1].replace(/`/g, "").trim();
    if (!line.startsWith("```")) continue;
    let close = index + 1;
    while (close < lines.length && !lines[close].trim().startsWith("```")) close++;
    if (line.startsWith("```nanyx")) {
      blocks.push({ meta: line.slice("```nanyx".length), code: lines.slice(index + 1, close).join("\n") + "\n", heading });
    }
    index = close;
  }
  return blocks;
}

/** A frontmatter field of a markdown page. */
function frontmatter(/** @type {string} */ text, /** @type {string} */ field) {
  const match = new RegExp(`^${field}:\\s*"?(.*?)"?\\s*$`, "m").exec(text.split(/^---$/m)[1] ?? "");
  return match ? match[1] : "";
}

/**
 * Every runnable tour example, grouped by its page in the tour's order, each titled by the heading it is under (and
 * numbered when a heading has several), with the code the playground opens. `tour` is the tour's pages, by their
 * paths: the page reads them with `import.meta.glob`.
 *
 * @param {Record<string, string>} tour
 * @returns {PlaygroundExampleGroup[]}
 */
export function tourExampleGroups(tour) {
  const pages = Object.entries(tour)
    .map(([file, text]) => {
      const page = (file.split("/").pop() ?? file).replace(/\.md$/, "");
      return { page, text, title: frontmatter(text, "title"), order: Number(frontmatter(text, "order") || 99) };
    })
    .sort((a, b) => a.order - b.order || a.page.localeCompare(b.page));
  return pages
    .map(({ page, text, title }) => {
      const runnable = blocksOf(text).filter(({ meta, code }) => opensInPlayground(meta, code));
      const counts = new Map();
      for (const { heading } of runnable) counts.set(heading, (counts.get(heading) ?? 0) + 1);
      const seen = new Map();
      const examples = runnable.map(({ code, heading }, index) => {
        const n = (seen.get(heading) ?? 0) + 1;
        seen.set(heading, n);
        const name = heading || title;
        return { id: `${page}-${index + 1}`, title: counts.get(heading) > 1 ? `${name} (${n})` : name, code: playgroundCode(code) };
      });
      return { page, title, examples };
    })
    .filter((group) => group.examples.length > 0);
}

/** The example the playground starts with when it has nothing else: the tour's hello world, from its `@entry` section. */
export function defaultExample(/** @type {PlaygroundExampleGroup[]} */ groups) {
  for (const group of groups) {
    for (const example of group.examples) {
      if (/^@entry\s*\ndef main = \{\s*\n\s*println\("Hello, World!"\)/m.test(example.code)) return example;
    }
  }
  throw new Error("The tour has no hello world for the playground to start with (attributes.md, under @entry).");
}

/**
 * A remark plugin that puts a bar under each example on a docs page that opens in the playground (`opensInPlayground`),
 * `<div data-tour-run>`, which `src/lib/playground/tour.ts` gives a Run button and an output, and whose code it makes
 * editable in place. The error reference's examples get none, as each shows the error of its section, and nor do those
 * in a standard library page's generated reference. An example
 * that only declares things, so that running it would print nothing (`data-no-output`, `runsCode`), or that reads input
 * (`data-reads-input`) gets neither.
 * It holds an "Open in the playground" link, which loads the page afresh (`data-astro-reload`), since a page the client
 * router swaps in isn't cross-origin isolated, and a program in it couldn't read input.
 */
export function remarkPlaygroundLinks() {
  return (/** @type {any} */ tree, /** @type {any} */ file) => {
    const filePath = String(file?.path ?? file?.history?.[0] ?? "").replace(/\\/g, "/");
    if (!filePath.includes("/content/") || filePath.endsWith("/content/standard-tools/errors.md")) return;
    // A standard library page's generated reference, between these comments, holds doc comments' examples, which are
    // checked where they are written, not as the page's.
    /** @type {[number, number][]} */
    const generated = [];
    visit(tree, "html", (/** @type {any} */ node) => {
      const line = node.position?.start.line ?? 0;
      if (node.value.startsWith("<!-- The reference below is generated")) generated.push([line, Infinity]);
      else if (node.value.startsWith("<!-- End of the generated reference.") && generated.length > 0) generated[generated.length - 1][1] = line;
    });
    const inGenerated = (/** @type {any} */ node) =>
      generated.some(([start, end]) => (node.position?.start.line ?? 0) > start && (node.position?.start.line ?? 0) < end);
    /** @type {[any, number][]} */
    const found = [];
    visit(tree, "code", (/** @type {any} */ node, /** @type {number | undefined} */ index, /** @type {any} */ parent) => {
      if (parent && index !== undefined && node.lang === "nanyx" && !inGenerated(node) && opensInPlayground(node.meta ?? "", node.value + "\n")) {
        found.push([parent, index]);
      }
    });
    // From the last, so that inserting a link doesn't move the blocks still to come.
    for (const [parent, index] of found.reverse()) {
      const code = parent.children[index].value + "\n";
      const flag = !runsCode(code) ? " data-no-output" : readsInput(code) ? " data-reads-input" : "";
      parent.children.splice(index + 1, 0, {
        type: "html",
        value:
          `<div class="tour-run" data-tour-run${flag}>` +
          `<a class="tour-run-open" href="${playgroundLink(code)}" data-astro-reload>Open in the playground</a></div>`,
      });
    }
  };
}

/**
 * A rehype plugin that has every link to the playground load the page afresh (`data-astro-reload`), not through the
 * client router: a page the router swaps in isn't cross-origin isolated, since the headers come with the page's own
 * response, and a program in it couldn't read input.
 */
export function rehypePlaygroundReload() {
  return (/** @type {any} */ tree) => {
    visit(tree, "element", (/** @type {any} */ node) => {
      const href = node.tagName === "a" ? String(node.properties?.href ?? "") : "";
      if (href === "/playground" || href.startsWith("/playground#")) node.properties.dataAstroReload = "";
    });
  };
}
