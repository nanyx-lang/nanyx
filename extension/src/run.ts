import * as path from "path";
import * as vscode from "vscode";

/**
 * Runs a saved `.nyx` file with `nanyx run`, as a task: the program gets a terminal of its own in the
 * panel, so it can read from stdin and be stopped with Ctrl+C, and running the file again reuses it.
 */
export async function runFile(executable: string, file: vscode.Uri): Promise<void> {
  const folder = vscode.workspace.getWorkspaceFolder(file);
  const name = path.basename(file.fsPath);

  const task = new vscode.Task(
    { type: "nanyx", command: "run", file: file.fsPath },
    folder ?? vscode.TaskScope.Global,
    `run ${name}`,
    "nanyx",
    // A process, not a shell command line, so a path with spaces or quotes in it needs no escaping.
    new vscode.ProcessExecution(executable, ["run", file.fsPath], { cwd: folder?.uri.fsPath ?? path.dirname(file.fsPath) }),
    // No problem matcher: the language server already reports the file's errors in the editor.
    []
  );
  task.presentationOptions = {
    reveal: vscode.TaskRevealKind.Always,
    panel: vscode.TaskPanelKind.Dedicated,
    clear: true
  };

  await vscode.tasks.executeTask(task);
}
