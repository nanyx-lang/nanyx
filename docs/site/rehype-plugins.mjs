import { visit } from "unist-util-visit";

/**
 * The markdown files use `#` for their top-level sections, but the page
 * title is already rendered as the h1. Shift every heading down one level
 * so the document outline stays valid.
 */
export function rehypeShiftHeadings() {
  return (tree) => {
    visit(tree, "element", (node) => {
      const match = /^h([1-5])$/.exec(node.tagName);
      if (match) {
        node.tagName = `h${Number(match[1]) + 1}`;
      }
    });
  };
}

/** The text of a hast node and its descendants. */
function textOf(node) {
  if (node.type === "text") return node.value;
  return (node.children ?? []).map(textOf).join("");
}

/**
 * Give a heading that starts with a diagnostic code, such as
 * `NYX0201: Type mismatch`, the code as its id (`nyx0201`), so that the
 * error reference's sections have the anchors the language server links to.
 * Must run before Astro's heading id plugin, which keeps an id it finds.
 */
export function rehypeDiagnosticCodeIds() {
  return (tree) => {
    visit(tree, "element", (node) => {
      if (!/^h[1-6]$/.test(node.tagName)) return;
      const match = /^(NYX\d{4})\b/.exec(textOf(node));
      if (match) {
        node.properties = { ...node.properties, id: match[1].toLowerCase() };
      }
    });
  };
}

const linkIcon = {
  type: "element",
  tagName: "svg",
  properties: {
    xmlns: "http://www.w3.org/2000/svg",
    width: 16,
    height: 16,
    viewBox: "0 0 24 24",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 2,
    strokeLinecap: "round",
    strokeLinejoin: "round",
    ariaHidden: "true",
  },
  children: [
    { type: "element", tagName: "path", properties: { d: "M9 17H7A5 5 0 0 1 7 7h2" }, children: [] },
    { type: "element", tagName: "path", properties: { d: "M15 7h2a5 5 0 1 1 0 10h-2" }, children: [] },
    { type: "element", tagName: "line", properties: { x1: 8, x2: 16, y1: 12, y2: 12 }, children: [] },
  ],
};

/**
 * Append a hover-revealed anchor link to each heading that has an id.
 * Must run after Astro's heading id plugin.
 */
export function rehypeHeadingAnchors() {
  return (tree) => {
    visit(tree, "element", (node) => {
      if (!/^h[2-6]$/.test(node.tagName) || !node.properties?.id) return;
      const id = node.properties.id;
      node.properties.className = [...(node.properties.className ?? []), "heading", "group"];
      node.children = [
        { type: "element", tagName: "span", properties: {}, children: node.children },
        {
          type: "element",
          tagName: "a",
          properties: {
            href: `#${id}`,
            className: ["heading-anchor"],
            ariaLabel: "Copy link to this section",
            dataHeadingAnchor: "",
          },
          children: [linkIcon],
        },
      ];
    });
  };
}

/**
 * The names a page's Nanyx code declares as types and contexts, from the text
 * of its code blocks: those declared `protected` or `private`, which are
 * nominal, and the others, aliases of a shape. Comments are left out.
 */
function declaredTypes(text) {
  const code = text.replace(/--.*$/gm, "");
  const declaration =
    /\b(?:type|context)\s+([A-Z][A-Za-z0-9_]*)\s*(?:\([^)]*\))?\s*=\s*(?:\[[^\]]*\]\s*)?(protected|private)?/g;
  const nominal = new Map();
  const structural = new Set();
  for (const match of code.matchAll(declaration)) {
    if (match[2]) nominal.set(match[1], match[2] === "private");
    else structural.add(match[1]);
  }
  return { nominal, structural };
}

/** `node` with `names` added to its classes, whether it keeps them as `class` or as `className`. */
function addClasses(node, names) {
  if (typeof node.properties.class === "string") {
    node.properties.class = [node.properties.class, ...names].filter(Boolean).join(" ");
  } else {
    node.properties.className = [...[].concat(node.properties.className ?? []), ...names];
  }
}

/**
 * Show the names of nominal types apart in the page's highlighted Nanyx code
 * blocks, as the editor does (see the VS Code extension page's Nominal types):
 * each token that is such a name gets the class `nominal`, and `private` too
 * for a private type's, which the site's styles show in italics. A type is
 * nominal if a code block of the page declares it `protected` or `private`,
 * or if the standard library or a package it documents exports it as one
 * (`library`: the `nominal-types.json` that `nanyx stdlib-docs` writes), and
 * no block of the page declares the name as an alias of a shape. It is read
 * from the text, not from the compiler, which the site's build doesn't have:
 * a name the page imports from elsewhere is missed. Runs after the code is
 * highlighted, as Astro runs the site's rehype plugins.
 *
 * @param {{ protected: string[], private: string[] }} library
 */
export function rehypeNominalTypes(library) {
  return (tree) => {
    const blocks = [];
    visit(tree, "element", (node) => {
      if (node.tagName === "pre" && node.properties?.dataLanguage === "nanyx") blocks.push(node);
    });
    if (blocks.length === 0) return;
    const { nominal, structural } = declaredTypes(blocks.map(textOf).join("\n"));
    const isPrivate = new Map([
      ...library.protected.map((name) => [name, false]),
      ...library.private.map((name) => [name, true]),
    ]);
    for (const name of structural) isPrivate.delete(name);
    for (const [name, isPrivateHere] of nominal) isPrivate.set(name, isPrivateHere);
    if (isPrivate.size === 0) return;
    for (const block of blocks) {
      visit(block, "element", (node) => {
        if (node.tagName !== "span" || node.children.length !== 1 || node.children[0].type !== "text") return;
        const name = node.children[0].value.trim();
        if (isPrivate.has(name)) addClasses(node, isPrivate.get(name) ? ["nominal", "private"] : ["nominal"]);
      });
    }
  };
}
