// The tour's runnable examples, each a small playground where it is: the docs build puts a bar under each
// (`remarkPlaygroundLinks` in `site/playground-examples.mjs`), and this gives it a Run button and an output, as the
// playground's, and makes the example's code editable in place. The compiler loads on the first Run on the page, not
// with the page, and the editor when an example is first edited. An example that reads input keeps only its "Open in
// the playground" link: a tour page isn't cross-origin isolated, so its programs can't read input.

import type { EditorView } from "@codemirror/view";
import { playgroundCode } from "../../../site/playground-code.mjs";
import { PlaygroundUnavailable, compileAndRun, loadPlayground, type Running } from "./compiler";
import { shareLink } from "./share";
import { Terminal } from "./terminal";

/** The programs running on the page, which a swap to another page stops. */
const running = new Set<Running>();

function button(text: string, className: string) {
  const element = document.createElement("button");
  element.type = "button";
  element.className = className;
  element.textContent = text;
  return element;
}

/** An output pane under `bar`, hidden until the first run, with what a terminal needs. */
function outputAfter(bar: HTMLElement) {
  const output = document.createElement("div");
  output.className = "tour-run-output pg-terminal";
  output.hidden = true;
  output.setAttribute("aria-live", "polite");
  const log = document.createElement("div");
  log.dataset.log = "";
  const inputLine = document.createElement("label");
  inputLine.dataset.inputLine = "";
  inputLine.className = "pg-input-line";
  inputLine.hidden = true;
  inputLine.append(document.createElement("input"));
  output.append(log, inputLine);
  bar.after(output);
  return output;
}

function wire(bar: HTMLElement) {
  if (bar.dataset.wired !== undefined) return;
  bar.dataset.wired = "";
  const before = bar.previousElementSibling;
  const pre = before?.matches("pre") ? (before as HTMLPreElement) : before?.querySelector("pre");
  if (!pre) return;
  // The code as the block shows it, without the line break at its end, which the editor would show as an empty line.
  const original = pre.textContent!.replace(/\n*$/, "");
  let editor: EditorView | null = null;
  let setCode: ((view: EditorView, code: string) => void) | null = null;
  const code = () => (editor ? editor.state.doc.toString() : original) + "\n";

  // "Open in the playground" opens the code as it is now, edited or not.
  bar.querySelector<HTMLAnchorElement>(".tour-run-open")?.addEventListener("click", async (event) => {
    if (code() === original + "\n") return;
    event.preventDefault();
    location.href = await shareLink(playgroundCode(code()));
  });
  if (bar.dataset.readsInput !== undefined) return;

  const run = button("Run", "tour-run-button");
  const reset = button("Reset", "tour-run-reset");
  reset.hidden = true;
  bar.prepend(run, reset);
  const output = outputAfter(bar);
  const terminal = new Terminal(output, { onLine: () => {}, onEnd: () => {} });
  let program: Running | null = null;

  const runIt = async () => {
    if (program) {
      program.stop();
      return;
    }
    if (run.disabled) return;
    output.hidden = false;
    run.disabled = true;
    run.textContent = "Loading the compiler…";
    let playground;
    try {
      playground = await loadPlayground();
    } catch (error) {
      terminal.notice(error instanceof PlaygroundUnavailable ? error.message : `The compiler didn't load: ${error}`, "diagnostic");
      run.textContent = "Run";
      run.disabled = false;
      return;
    }
    const exit = await compileAndRun(playground, terminal, playgroundCode(code()), {
      onCompiling: () => (run.textContent = "Compiling…"),
      onStarted: (started) => {
        program = started;
        running.add(started);
        run.textContent = "Stop";
        run.disabled = false;
      },
    });
    // Most of the tour's examples only define things: say so, rather than leave the run empty.
    if (exit?.code === 0 && terminal.runIsEmpty()) terminal.line("(no output)", "note");
    if (program) running.delete(program);
    program = null;
    run.textContent = "Run";
    run.disabled = false;
  };

  // Clicking the code, or Enter on it, puts an editor in its place, with the caret where it was clicked.
  const edit = async (event?: MouseEvent) => {
    if (editor) return;
    const editing = await import("./editor");
    if (editor) return;
    setCode = editing.setCode;
    const host = document.createElement("div");
    host.className = "tour-run-editor";
    pre.replaceWith(host);
    editor = editing.createEditor(host, original, {
      numbered: false,
      onRun: () => void runIt(),
      onChange: (changed) => (reset.hidden = changed === original),
    });
    const at = event ? editor.posAtCoords({ x: event.clientX, y: event.clientY }) : null;
    if (at !== null) editor.dispatch({ selection: { anchor: at } });
    editor.focus();
  };
  pre.tabIndex = 0;
  pre.title = "Click to edit";
  pre.addEventListener("click", (event) => {
    // A click that ends a selection leaves the text to copy.
    if (window.getSelection()?.toString()) return;
    void edit(event);
  });
  pre.addEventListener("keydown", (event) => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    void edit();
  });

  run.addEventListener("click", () => void runIt());
  reset.addEventListener("click", () => {
    if (!editor || !setCode) return;
    setCode(editor, original);
    reset.hidden = true;
  });
}

document.addEventListener("astro:page-load", () => {
  for (const bar of document.querySelectorAll<HTMLElement>("[data-tour-run]")) wire(bar);
});
document.addEventListener("astro:before-swap", () => {
  for (const program of running) program.stop();
  running.clear();
});
