// The playground's output pane: one stream in the order things happened, as a terminal shows them. What a program
// writes to standard error is in the error colour; each `dbg` is a line of its own, dimmed, after its place; the end is
// shown as the CLI shows it; and while the program waits for input, the input line is the pane's last line. Runs
// aren't cleared: each one starts under a divider.

/** How a program ended, as the runner's `done` gives it. */
export interface Exit {
  code: number | null;
  trap?: string;
  stopped?: boolean;
}

export class Terminal {
  private readonly log: HTMLElement;
  private readonly inputLine: HTMLElement;
  private readonly input: HTMLInputElement;
  private run: HTMLElement | null = null;
  /** Whether the last thing written ended a line, so that a line of its own (a `dbg`, the end) starts on a new one. */
  private atLineStart = true;
  private runs = 0;

  constructor(
    private readonly pane: HTMLElement,
    { onLine, onEnd }: { onLine: (line: string) => void; onEnd: () => void },
  ) {
    this.log = pane.querySelector<HTMLElement>("[data-log]")!;
    this.inputLine = pane.querySelector<HTMLElement>("[data-input-line]")!;
    this.input = this.inputLine.querySelector("input")!;
    // Enter gives the line, which stays in the output; Ctrl+D ends the input.
    this.input.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        const line = this.input.value;
        this.write(line + "\n", "stdin");
        this.hideInput();
        onLine(line);
      } else if (event.key === "d" && event.ctrlKey) {
        event.preventDefault();
        this.hideInput();
        onEnd();
      }
    });
  }

  /** Starts a run's part of the pane, under a divider when it isn't the first. */
  beginRun() {
    this.runs++;
    const run = document.createElement("div");
    run.className = "pg-run";
    run.dataset.run = String(this.runs);
    if (this.log.childElementCount > 0) {
      const divider = document.createElement("div");
      divider.className = "pg-divider";
      divider.setAttribute("role", "separator");
      divider.textContent = `Run ${this.runs} · ${new Date().toLocaleTimeString()}`;
      run.appendChild(divider);
    }
    this.log.appendChild(run);
    this.run = run;
    this.atLineStart = true;
    this.scroll();
  }

  /** Appends `text` to the current run, as `kind`. */
  write(text: string, kind: "stdout" | "stderr" | "stdin" | "note" | "diagnostic" | "warning" | "end" | "dbg") {
    if (text === "") return;
    if (!this.run) this.beginRun();
    const last = this.run!.lastElementChild as HTMLElement | null;
    if (last && last.dataset.kind === kind && (kind === "stdout" || kind === "stderr")) {
      last.textContent += text;
    } else {
      const span = document.createElement("span");
      span.className = `pg-${kind}`;
      span.dataset.kind = kind;
      span.textContent = text;
      this.run!.appendChild(span);
    }
    this.atLineStart = text.endsWith("\n");
    this.scroll();
  }

  /** Writes `text` as a line of its own. */
  line(text: string, kind: "end" | "dbg" | "stderr") {
    this.write((this.atLineStart ? "" : "\n") + text + "\n", kind);
  }

  /** A note from the page, such as why the compiler didn't load, outside any run. */
  notice(text: string, kind: "note" | "diagnostic") {
    const div = document.createElement("div");
    div.className = `pg-${kind}`;
    div.dataset.kind = kind;
    div.textContent = text;
    this.log.appendChild(div);
    this.run = null;
    this.atLineStart = true;
    this.scroll();
  }

  /** A `dbg`'s line: its place and what it printed. */
  dbg({ file, line, text }: { file: string; line: number; text: string }) {
    this.line(`[${file}:${line}] ${text}`, "dbg");
  }

  /** The compiler's diagnostics, each code linked to its section of the error reference. */
  diagnostics(diagnostics: string[], kind: "diagnostic" | "warning") {
    for (const diagnostic of diagnostics) {
      if (!this.run) this.beginRun();
      const span = document.createElement("span");
      span.className = `pg-${kind}`;
      span.dataset.kind = kind;
      if (!this.atLineStart) span.append("\n");
      let rest = diagnostic;
      for (let match = /NYX\d{4}/.exec(rest); match; match = /NYX\d{4}/.exec(rest)) {
        span.append(rest.slice(0, match.index));
        const link = document.createElement("a");
        link.href = `/docs/standard-tools/errors#${match[0].toLowerCase()}`;
        link.target = "_blank";
        link.textContent = match[0];
        span.append(link);
        rest = rest.slice(match.index + match[0].length);
      }
      span.append(rest + "\n");
      this.run!.appendChild(span);
      this.atLineStart = true;
    }
    this.scroll();
  }

  /** How the program ended, as the CLI shows it: nothing when it ran to its end. */
  end(exit: Exit) {
    if (exit.stopped) this.line("stopped", "end");
    else {
      if (exit.trap) this.line(exit.trap, "stderr");
      if (exit.code !== 0) this.line(`exited with ${exit.code}`, "end");
    }
  }

  /** Shows the input line, as the pane's last line, while the program waits for a line. */
  showInput() {
    this.inputLine.hidden = false;
    this.input.value = "";
    this.input.focus({ preventScroll: true });
    this.scroll();
  }

  hideInput() {
    this.inputLine.hidden = true;
  }

  clear() {
    this.log.replaceChildren();
    this.run = null;
    this.atLineStart = true;
  }

  /** Keeps the end in view. */
  private scroll() {
    this.pane.scrollTop = this.pane.scrollHeight;
  }
}
