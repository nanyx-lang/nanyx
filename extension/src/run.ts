import { randomUUID } from "crypto";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { HitReader } from "./dbgValues";
import { DbgView } from "./dbgView";

/** The files runs still going write their `dbg`s to, which are deleted when the run ends or the extension stops. */
const channels = new Set<string>();

/** Deletes the files of the runs still going, as the extension stops. */
export function deleteDbgChannels(): void {
  channels.forEach((file) => fs.rmSync(file, { force: true }));
  channels.clear();
}

/** How often a run's `dbg` file is read for what it has written since. */
const readEvery = 200;

/**
 * Runs a saved `.nyx` file with `nanyx run`, as a task: the program gets a terminal of its own in the
 * panel, so it can read from stdin and be stopped with Ctrl+C, and running the file again reuses it.
 * In development mode it runs with `--timings`, so the terminal shows how long each module took to check.
 * Its `dbg`s go to a file of their own (`--dbg-to`), which is read as the program runs, and `dbg` shows their values
 * in the editor, not in the terminal.
 */
export async function runFile(executable: string, file: vscode.Uri, dbg: DbgView): Promise<void> {
  const folder = vscode.workspace.getWorkspaceFolder(file);
  const name = path.basename(file.fsPath);
  const channel = path.join(os.tmpdir(), `nanyx-dbg-${randomUUID()}.jsonl`);
  const args = ["run", "--dbg-to", channel, file.fsPath];
  if (vscode.workspace.getConfiguration("nanyx").get<boolean>("developmentMode", false)) {
    args.push("--timings");
  }

  const task = new vscode.Task(
    { type: "nanyx", command: "run", file: file.fsPath },
    folder ?? vscode.TaskScope.Global,
    `run ${name}`,
    "nanyx",
    // A process, not a shell command line, so a path with spaces or quotes in it needs no escaping.
    new vscode.ProcessExecution(executable, args, { cwd: folder?.uri.fsPath ?? path.dirname(file.fsPath) }),
    // No problem matcher: the language server already reports the file's errors in the editor.
    []
  );
  task.presentationOptions = {
    reveal: vscode.TaskRevealKind.Always,
    panel: vscode.TaskPanelKind.Dedicated,
    clear: true
  };

  channels.add(channel);
  const run = dbg.beginRun();
  const reader = new HitReader(channel);
  const read = () => {
    const hits = reader.read();
    if (hits.length > 0) {
      dbg.add(run, hits);
    }
  };
  const reading = setInterval(read, readEvery);
  const execution = await vscode.tasks.executeTask(task);
  const ended = vscode.tasks.onDidEndTaskProcess((event) => {
    if (event.execution === execution) {
      clearInterval(reading);
      ended.dispose();
      read();
      fs.rmSync(channel, { force: true });
      channels.delete(channel);
    }
  });
}
