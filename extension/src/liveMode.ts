import { ChildProcess, spawn } from "child_process";
import { randomUUID } from "crypto";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";
import { HitReader } from "./dbgValues";
import { DbgView } from "./dbgView";
import { endedText, isOn, LiveInfo, liveArguments, LiveTarget, packageDirectoryOf, Row, rowsOf, targetOf } from "./live";

/** The command that turns live mode on or off for the active file's script or package. */
export const COMMAND_TOGGLE_LIVE = "nanyx.toggleLiveMode";

/** Where the user's choices are kept, by target, in the workspace's state. */
const settingsKey = "nanyx.liveMode";

/** How often a live run's `dbg` file is read for what it has written since. */
const readEvery = 200;

/** A live run still going: its process, and what to do once it has ended. */
interface Running {
  process: ChildProcess;
  finished: Promise<void>;
}

/**
 * Live mode (see `live.ts`): it re-runs a script, or a package's `@entry`, on every save, with its files in memory, no
 * input, and its `dbg`s shown in the editor, and writes what it printed to the "Nanyx Live" output channel. It is on
 * on its own where the entry point requires none but `Console`, `Async` and `FileSystem`, and elsewhere only once the
 * user turns it on, from the Live mode view in the Nanyx sidebar or **Nanyx: Toggle live mode**. A save while a run is
 * still going stops it and starts another, and a run past `nanyx.live.timeLimit` seconds is stopped.
 */
export class LiveMode implements vscode.Disposable, vscode.TreeDataProvider<Row> {
  private readonly output = vscode.window.createOutputChannel("Nanyx Live");
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;
  private readonly infos = new Map<string, LiveInfo>();
  private readonly running = new Map<string, Running>();
  private readonly disposables: vscode.Disposable[] = [];
  private view: vscode.TreeView<Row> | undefined;

  constructor(
    private readonly state: vscode.Memento,
    private readonly compiler: () => string | undefined,
    private readonly client: () => LanguageClient | undefined,
    private readonly dbg: DbgView
  ) {
    this.view = vscode.window.createTreeView("nanyx.live", { treeDataProvider: this });
    this.disposables.push(
      this.output,
      this.changed,
      this.view,
      vscode.commands.registerCommand(COMMAND_TOGGLE_LIVE, () => this.toggle()),
      vscode.window.onDidChangeActiveTextEditor(() => void this.refresh()),
      vscode.workspace.onDidSaveTextDocument((document) => void this.saved(document))
    );
    void this.refresh();
  }

  dispose(): void {
    this.running.forEach((run) => run.process.kill());
    this.disposables.forEach((disposable) => disposable.dispose());
  }

  /** The script or package of the active editor's file, if it is a Nanyx file on disk. */
  private activeTarget(): { target: LiveTarget; document: vscode.TextDocument } | undefined {
    const document = vscode.window.activeTextEditor?.document;
    if (!document || document.languageId !== "nanyx" || document.uri.scheme !== "file") {
      return undefined;
    }
    return { target: targetOf(document.fileName, packageDirectoryOf(document.fileName)), document };
  }

  private setting(target: LiveTarget): boolean | undefined {
    return this.state.get<Record<string, boolean>>(settingsKey, {})[target.key];
  }

  /** Asks the language server what the active file's entry point is, and shows it. */
  async refresh(): Promise<void> {
    const active = this.activeTarget();
    if (active) {
      const info = await this.infoOf(active.document);
      // A package's entry point is known from its `@entry` file: another of its files has none of its own.
      if (info && (active.target.kind === "script" || info.entry !== null)) {
        this.infos.set(active.target.key, info);
      }
    }
    this.changed.fire();
  }

  private async infoOf(document: vscode.TextDocument): Promise<LiveInfo | undefined> {
    const client = this.client();
    if (!client) {
      return undefined;
    }
    try {
      return await client.sendRequest<LiveInfo>("nanyx/liveMode", { textDocument: { uri: document.uri.toString() } });
    } catch {
      return undefined;
    }
  }

  getTreeItem(row: Row): vscode.TreeItem {
    const item = new vscode.TreeItem(row.label);
    item.description = row.description;
    item.tooltip = row.tooltip;
    if (row.label.startsWith("Live mode is")) {
      item.contextValue = "liveSwitch";
      item.iconPath = new vscode.ThemeIcon(row.label.endsWith("on") ? "pass-filled" : "circle-large-outline");
    }
    return item;
  }

  getChildren(row?: Row): Row[] {
    if (row) {
      return [];
    }
    const active = this.activeTarget();
    if (!active) {
      return [{ label: "Open a .nyx file", tooltip: "Live mode re-runs a script, or a package's @entry, on every save." }];
    }
    const info = this.infos.get(active.target.key);
    return rowsOf(active.target, info, isOn(info, this.setting(active.target)));
  }

  /** Turns live mode on or off for the active file's script or package, and runs it once when it is turned on. */
  private async toggle(): Promise<void> {
    const active = this.activeTarget();
    if (!active) {
      void vscode.window.showInformationMessage("Live mode is for a .nyx file: open one first.");
      return;
    }
    await this.refresh();
    const info = this.infos.get(active.target.key);
    if (!info || info.entry === null) {
      void vscode.window.showInformationMessage(`${active.target.name} has no entry point to run.`);
      return;
    }
    const on = !isOn(info, this.setting(active.target));
    await this.state.update(settingsKey, { ...this.state.get<Record<string, boolean>>(settingsKey, {}), [active.target.key]: on });
    this.changed.fire();
    if (on) {
      void this.run(active.target);
    } else {
      this.running.get(active.target.key)?.process.kill();
    }
  }

  /** Re-runs the script or package of a saved file, if live mode is on for it. */
  private async saved(document: vscode.TextDocument): Promise<void> {
    if (document.languageId !== "nanyx" || document.uri.scheme !== "file") {
      return;
    }
    const target = targetOf(document.fileName, packageDirectoryOf(document.fileName));
    // Asked again, since the save may have changed what the entry point requires.
    const info = await this.infoOf(document);
    if (info && (target.kind === "script" || info.entry !== null)) {
      this.infos.set(target.key, info);
    }
    this.changed.fire();
    if (isOn(this.infos.get(target.key), this.setting(target))) {
      await this.run(target);
    }
  }

  /** Runs `target` live, after stopping a run of it still going. */
  private async run(target: LiveTarget): Promise<void> {
    const executable = this.compiler();
    if (!executable) {
      return;
    }
    const previous = this.running.get(target.key);
    if (previous) {
      previous.process.kill();
      await previous.finished;
    }

    const channel = path.join(os.tmpdir(), `nanyx-live-${randomUUID()}.jsonl`);
    const limit = vscode.workspace.getConfiguration("nanyx").get<number>("live.timeLimit", 10);
    const started = Date.now();
    this.output.clear();
    this.output.appendLine(`▶ ${target.name}, live: files in memory, no input`);
    // No input: what the program reads sees the end of it.
    const process = spawn(executable, liveArguments(target.target, channel), { cwd: target.directory, stdio: ["ignore", "pipe", "pipe"] });
    const dbgRun = this.dbg.beginRun();
    const reader = new HitReader(channel);
    const read = () => {
      const hits = reader.read();
      if (hits.length > 0) {
        this.dbg.add(dbgRun, hits);
      }
    };
    const reading = setInterval(read, readEvery);
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      process.kill();
    }, limit * 1000);
    process.stdout?.setEncoding("utf8");
    process.stdout?.on("data", (chunk: string) => this.output.append(chunk));
    process.stderr?.setEncoding("utf8");
    process.stderr?.on("data", (chunk: string) => this.output.append(chunk));
    const finished = new Promise<void>((resolve) => {
      const done = (code: number | null, signal: string | null) => {
        clearInterval(reading);
        clearTimeout(timer);
        read();
        fs.rmSync(channel, { force: true });
        if (this.running.get(target.key)?.process === process) {
          this.running.delete(target.key);
        }
        this.output.appendLine(endedText(code, signal, Date.now() - started, timedOut, limit));
        resolve();
      };
      process.on("close", done);
      process.on("error", (error) => {
        this.output.appendLine(`Couldn't start nanyx: ${error.message}`);
        done(null, null);
      });
    });
    this.running.set(target.key, { process, finished });
    this.output.show(true);
  }
}
