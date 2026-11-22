// The playground's examples: every runnable example in the language tour, grouped by its page, and the links that
// open one in the playground. A tour example is runnable when its block is fenced ```nanyx with nothing after it (not
// a fragment, nor skipped) and it doesn't show an error: these are the blocks `RunTourExampleTests.fs` compiles and runs.
// Plain JS so that both astro.config.mjs and the playground page can import it.

import { deflateRawSync } from "node:zlib";
import { visit } from "unist-util-visit";

/**
 * @typedef {{ id: string; title: string; code: string }} PlaygroundExample
 * @typedef {{ page: string; title: string; examples: PlaygroundExample[] }} PlaygroundExampleGroup
 */

/**
 * Whether a ```nanyx block, with `meta` the words after `nanyx` on its fence, is a whole program that runs: not a
 * fragment, not skipped, and not one that shows an error (`-- error NYX0201: ...`).
 *
 * @param {string} meta
 * @param {string} code
 */
export function isRunnable(meta, code) {
  return meta.trim() === "" && !/--.*\berror NYX\d{4}/i.test(code);
}

/**
 * The code the playground opens for a tour example: the example with `import nanyx/text` added, as the tests add it,
 * since the tour uses strings without importing it. It goes after the example's module declaration when it has one
 * (and the comments and directives before it), else after its directives, and isn't added to an example that already
 * imports it or turns a linter on, which would report it unused.
 *
 * @param {string} code
 */
export function playgroundCode(code) {
  const lines = code.replace(/\r\n/g, "\n").replace(/\n+$/, "").split("\n");
  if (code.includes("import nanyx/text") || code.includes("::linter")) return lines.join("\n") + "\n";
  const isDirective = (/** @type {string} */ line) => line.startsWith("::") || line.startsWith("#!") || line.trim() === "";
  const isPreamble = (/** @type {string} */ line) => isDirective(line) || line.trim().startsWith("--");
  // A doc comment's other lines are indented under its `--|`.
  let index = 0;
  let inDocComment = false;
  while (index < lines.length && (isPreamble(lines[index]) || (inDocComment && lines[index].startsWith(" ")))) {
    if (lines[index].trim() !== "") inDocComment = lines[index].trimStart().startsWith("--|") || (inDocComment && lines[index].startsWith(" "));
    index++;
  }
  if (index < lines.length && lines[index].startsWith("module ") && !lines[index].includes("=")) {
    index++;
  } else {
    index = 0;
    while (index < lines.length && isDirective(lines[index])) index++;
  }
  // Keep a blank line between the import and what is around it, as the formatter writes it.
  const before = lines.slice(0, index);
  const after = lines.slice(index);
  while (before.length > 0 && before[before.length - 1].trim() === "") before.pop();
  while (after.length > 0 && after[0].trim() === "") after.shift();
  return [...before, ...(before.length > 0 ? [""] : []), "import nanyx/text", "", ...after].join("\n") + "\n";
}

/**
 * The fragment of a share link to `code`: `code=` and the code, compressed with raw deflate and written in base64url.
 * The playground page decodes it with `DecompressionStream("deflate-raw")`.
 *
 * @param {string} code
 */
export function shareFragment(code) {
  return "code=" + deflateRawSync(Buffer.from(code, "utf8"), { level: 9 }).toString("base64url");
}

/** The link that opens `code`, a tour example, in the playground. */
export function playgroundLink(/** @type {string} */ code) {
  return "/playground#" + shareFragment(playgroundCode(code));
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
      const runnable = blocksOf(text).filter(({ meta, code }) => isRunnable(meta, code));
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
 * A remark plugin that puts an "Open in the playground" link under each runnable example in the tour. The link loads
 * the page afresh (`data-astro-reload`), since a page the client router swaps in isn't cross-origin isolated, and a
 * program in it couldn't read input.
 */
export function remarkPlaygroundLinks() {
  return (/** @type {any} */ tree, /** @type {any} */ file) => {
    const filePath = String(file?.path ?? file?.history?.[0] ?? "").replace(/\\/g, "/");
    if (!filePath.includes("/content/tour/")) return;
    /** @type {[any, number][]} */
    const found = [];
    visit(tree, "code", (/** @type {any} */ node, /** @type {number | undefined} */ index, /** @type {any} */ parent) => {
      if (parent && index !== undefined && node.lang === "nanyx" && isRunnable(node.meta ?? "", node.value + "\n")) {
        found.push([parent, index]);
      }
    });
    // From the last, so that inserting a link doesn't move the blocks still to come.
    for (const [parent, index] of found.reverse()) {
      const code = parent.children[index].value + "\n";
      parent.children.splice(index + 1, 0, {
        type: "html",
        value: `<p class="playground-link"><a href="${playgroundLink(code)}" data-astro-reload>Open in the playground</a></p>`,
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
