// What a runnable example in the tour is, and the code it runs as. The docs build reads these (`playground-examples.mjs`)
// and so do the tour's pages in the browser (`src/lib/playground/tour.ts`), which run an example as it is edited.

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
 * Whether a program reads input. A tour page isn't cross-origin isolated, so a program on it can't read input, and an
 * example that does gets only its "Open in the playground" link.
 *
 * @param {string} code
 */
export function readsInput(code) {
  return /\b(readLine|readToEnd)\b/.test(code);
}
