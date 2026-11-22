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

/** The words that start a top-level declaration, which defines something and runs nothing. */
const declarationWords = new Set(["def", "type", "context", "module", "import", "export", "private", "protected", "pattern"]);

/**
 * Whether a program does something when it runs: whether it has an `@entry`, or a top-level item that isn't a
 * declaration (a definition, a type, a context, a module, an import or an attribute other than `@entry`). A top-level
 * item starts on a line that isn't indented; a line that starts with a closing bracket, a `|` or another operator goes
 * on with the item before it. A program that does nothing when it runs, one that only declares things, gets only its
 * "Open in the playground" link on a tour page, as running it would print nothing.
 *
 * @param {string} code
 */
export function runsCode(code) {
  for (const line of code.replace(/\r\n/g, "\n").split("\n")) {
    if (line.trim() === "" || /^\s/.test(line) || line.startsWith("--") || line.startsWith("::") || line.startsWith("#!")) continue;
    const attribute = /^@(\w+)/.exec(line);
    if (attribute) {
      if (attribute[1] === "entry") return true;
      continue;
    }
    const word = /^[A-Za-z_]\w*/.exec(line);
    if (word ? !declarationWords.has(word[0]) : !/^[)\]}|<>=+\-*/&.,:]/.test(line)) return true;
  }
  return false;
}
