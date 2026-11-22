// The playground page: loads the compiler built for the browser from `/playground/compiler/` as the page opens,
// compiles a hello world at once so that the first Run is quick, and runs programs under the `nanyx.playground` host
// with its runner, in a web worker. What the page holds and how it starts is on the Playground page in Deep dives.

import type { EditorView } from "@codemirror/view";
import { PlaygroundUnavailable, compileAndRun, loadPlayground, releaseNotesUrl, type Playground, type Running } from "./compiler";
import { createEditor, setCode } from "./editor";
import { codeOf, shareLink } from "./share";
import { Terminal } from "./terminal";

interface Example {
  id: string;
  title: string;
  code: string;
}

interface PageData {
  groups: { page: string; title: string; examples: Example[] }[];
  defaultExample: Example;
}

const storageKey = "nanyx.playground.code";
const splitKey = "nanyx.playground.split";

const stored = {
  get(key: string) {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key: string, value: string) {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Storage may be turned off; the page works without it.
    }
  },
};

const element = <T extends HTMLElement>(selector: string) => document.querySelector<T>(selector)!;

const data: PageData = JSON.parse(element("#playground-data").textContent!);
const runButton = element<HTMLButtonElement>("[data-run]");
const stopButton = element<HTMLButtonElement>("[data-stop]");
const shareButton = element<HTMLButtonElement>("[data-share]");
const examples = element<HTMLSelectElement>("[data-examples]");
const status = element("[data-status]");
const release = element("[data-release]");

let playground: Playground | null = null;
let running: Running | null = null;
/** The code last opened from an example or a link, unchanged: replacing it needs no asking. */
let opened: string | null = null;

const terminal = new Terminal(element("[data-output]"), {
  onLine: (line) => running?.input(line),
  onEnd: () => running?.closeInput(),
});

let saveTimer: ReturnType<typeof setTimeout> | undefined;
const editor: EditorView = createEditor(element("[data-editor]"), "", {
  onRun: () => void runProgram(),
  onChange: (code) => {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => stored.set(storageKey, code), 300);
  },
});

const code = () => editor.state.doc.toString();
const isEdited = () => code() !== opened && code().trim() !== "";

/** Opens `source` in the editor, as code that can be replaced without asking. */
function open(source: string) {
  opened = source;
  setCode(editor, source);
  stored.set(storageKey, source);
}

function setStatus(text: string) {
  status.textContent = text;
  status.hidden = text === "";
}

/** Shows Run, or Stop in its place while a program runs; Run is enabled once the compiler is ready. */
function setRunning(isRunning: boolean) {
  runButton.hidden = isRunning;
  stopButton.hidden = !isRunning;
  runButton.disabled = playground === null;
}

async function runProgram() {
  if (!playground || running) return;
  runButton.disabled = true;
  await compileAndRun(playground, terminal, code(), {
    onCompiling: () => setStatus("Compiling…"),
    onStarted: (started) => {
      running = started;
      setStatus("");
      setRunning(true);
    },
  });
  running = null;
  setStatus("");
  setRunning(false);
}

/** Opens the code of a share link, if `hash` is one, asking first when the editor's code has changed. */
async function openLink(hash: string, { atLoad }: { atLoad: boolean }) {
  const linked = await codeOf(hash);
  if (linked === null) return false;
  history.replaceState(null, "", location.pathname + location.search);
  if (typeof linked === "object") {
    if (atLoad) open(data.defaultExample.code);
    terminal.notice(`The link's code couldn't be read${atLoad ? ", so the playground opened hello world instead" : ""}.`, "note");
    return true;
  }
  if (!atLoad && isEdited() && !confirm("Replace your code with the code from the link?")) return true;
  open(linked);
  terminal.notice("Opened the code from a link. Press Run to run it.", "note");
  return true;
}

/** What the editor starts with: a share link's code, else what was last written here, else hello world. */
async function startingCode() {
  if (await openLink(location.hash, { atLoad: true })) return;
  const last = stored.get(storageKey);
  if (last !== null && last.trim() !== "") {
    setCode(editor, last);
    opened = last === data.defaultExample.code ? last : null;
  } else {
    open(data.defaultExample.code);
  }
}

function wireExamples() {
  const byId = new Map(data.groups.flatMap((group) => group.examples.map((example) => [example.id, example] as const)));
  examples.addEventListener("change", () => {
    const example = byId.get(examples.value);
    examples.value = "";
    if (!example) return;
    if (isEdited() && !confirm(`Replace your code with the example “${example.title}”?`)) return;
    open(example.code);
    editor.focus();
  });
}

function wireShare() {
  shareButton.addEventListener("click", async () => {
    const link = await shareLink(code());
    shareButton.dataset.link = link;
    try {
      await navigator.clipboard.writeText(link);
      shareButton.textContent = "Link copied";
    } catch {
      window.prompt("Copy this link to share the code:", link);
    }
    setTimeout(() => (shareButton.textContent = "Share"), 2000);
  });
}

/** The divider between the editor and the output: dragged, or moved with the arrow keys, it resizes them. */
function wireDivider() {
  const split = element("[data-split]");
  const divider = element("[data-divider]");
  const stacked = () => getComputedStyle(split).getPropertyValue("--stacked").trim() === "1";
  const setSplit = (percent: number) => {
    const clamped = Math.min(85, Math.max(15, percent));
    split.style.setProperty("--split", `${clamped}%`);
    divider.setAttribute("aria-valuenow", String(Math.round(clamped)));
    stored.set(splitKey, String(clamped));
  };
  const saved = Number(stored.get(splitKey));
  if (saved) setSplit(saved);
  divider.addEventListener("pointerdown", (event) => {
    event.preventDefault();
    divider.setPointerCapture(event.pointerId);
    const move = (moved: PointerEvent) => {
      const box = split.getBoundingClientRect();
      setSplit(stacked() ? ((moved.clientY - box.top) / box.height) * 100 : ((moved.clientX - box.left) / box.width) * 100);
    };
    const up = () => {
      divider.removeEventListener("pointermove", move);
      divider.removeEventListener("pointerup", up);
    };
    divider.addEventListener("pointermove", move);
    divider.addEventListener("pointerup", up);
  });
  divider.addEventListener("keydown", (event) => {
    const current = Number(divider.getAttribute("aria-valuenow")) || 60;
    const step = { ArrowLeft: -2, ArrowUp: -2, ArrowRight: 2, ArrowDown: 2 }[event.key];
    if (step === undefined) return;
    event.preventDefault();
    setSplit(current + step);
  });
}

/** The compiler release the page runs, linked to its notes. */
function showRelease(name: string | null) {
  if (name) {
    const link = document.createElement("a");
    link.href = releaseNotesUrl(name);
    link.target = "_blank";
    link.rel = "noopener";
    link.textContent = `Nanyx ${name}`;
    release.replaceChildren(link);
  } else {
    release.textContent = "Nanyx, built locally";
  }
}

async function loadCompiler() {
  try {
    playground = await loadPlayground(data.defaultExample.code);
  } catch (error) {
    setStatus("");
    terminal.notice(error instanceof PlaygroundUnavailable ? error.message : `The compiler didn't load: ${error}`, "diagnostic");
    return;
  }
  showRelease(playground.release);
  if (!playground.runner.canReadInput) {
    terminal.notice("This page isn't cross-origin isolated, so programs can't read input. Reload it to fix that.", "note");
  }
  setStatus("");
  setRunning(false);
  document.documentElement.dataset.playground = "ready";
}

runButton.addEventListener("click", () => void runProgram());
stopButton.addEventListener("click", () => running?.stop());
element("[data-clear]").addEventListener("click", () => terminal.clear());
// Ctrl+Enter runs from anywhere on the page but the input line, where Enter gives the line. The editor handles its own.
document.addEventListener("keydown", (event) => {
  if (event.defaultPrevented) return;
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !(event.target instanceof HTMLInputElement)) {
    event.preventDefault();
    void runProgram();
  }
});
addEventListener("hashchange", () => void openLink(location.hash, { atLoad: false }));
wireExamples();
wireShare();
wireDivider();
await startingCode();
void loadCompiler();
