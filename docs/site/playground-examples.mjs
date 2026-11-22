// The playground's examples: every runnable example in the language tour, grouped by its page, and the links that
// open one in the playground. A tour example is runnable when its block is fenced ```nanyx with nothing after it (not
// a fragment, nor skipped) and it doesn't show an error: these are the blocks `RunTourExampleTests.fs` compiles and runs.
// Plain JS so that both astro.config.mjs and the playground page can import it. What a runnable example is, and the
// code it runs as, are in `playground-code.mjs`, which the tour's pages read in the browser too.

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
 * A remark plugin that puts a bar under each runnable example in the tour, `<div data-tour-run>`, which
 * `src/lib/playground/tour.ts` gives a Run button and an output, and whose code it makes editable in place. An example
 * that only declares things, so that running it would print nothing (`data-no-output`, `runsCode`), or that reads input
 * (`data-reads-input`) gets neither.
 * It holds an "Open in the playground" link, which loads the page afresh (`data-astro-reload`), since a page the client
 * router swaps in isn't cross-origin isolated, and a program in it couldn't read input.
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
