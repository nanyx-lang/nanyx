import * as path from "path";
import * as vscode from "vscode";
import { debugArguments, NanyxLaunch, resolvedLaunch, workingDirectory } from "./debug";

/** The command that debugs the active Nanyx file. */
export const COMMAND_DEBUG_FILE = "nanyx.debugFile";

/** The active editor's file, if it is a Nanyx file on disk. */
function activeNanyxFile(): string | undefined {
  const document = vscode.window.activeTextEditor?.document;
  return document && document.languageId === "nanyx" && document.uri.scheme === "file" ? document.fileName : undefined;
}

/**
 * Registers the `nanyx` debug type (see `debug.ts`): a launch configuration's program, or the active file when it has
 * none, is debugged by `nanyx run --debug`, run as the debug adapter by the `nanyx` that `compiler` gives, and
 * **Nanyx: Debug the current file** starts a session for the active file.
 */
export function registerDebugger(compiler: () => string | undefined): vscode.Disposable[] {
  const configurations: vscode.DebugConfigurationProvider = {
    resolveDebugConfiguration(_folder, config) {
      const resolved = resolvedLaunch(config as NanyxLaunch, activeNanyxFile());
      if (!resolved) {
        void vscode.window.showInformationMessage("Nanyx: open a .nyx file to debug, or name one as the launch configuration's program.");
        return undefined;
      }
      return resolved as vscode.DebugConfiguration;
    }
  };
  const adapters: vscode.DebugAdapterDescriptorFactory = {
    createDebugAdapterDescriptor(session) {
      const executable = compiler();
      if (!executable) {
        throw new Error("Nanyx: no nanyx compiler was found to debug with. Run Nanyx: Download the compiler, or set nanyx.path.");
      }
      const config = session.configuration as NanyxLaunch;
      const program = config.program!;
      return new vscode.DebugAdapterExecutable(executable, debugArguments(program, config.args), {
        cwd: workingDirectory(program, config.cwd)
      });
    }
  };
  return [
    vscode.debug.registerDebugConfigurationProvider("nanyx", configurations),
    vscode.debug.registerDebugAdapterDescriptorFactory("nanyx", adapters),
    vscode.commands.registerCommand(COMMAND_DEBUG_FILE, async (uri?: vscode.Uri) => {
      const file = uri?.fsPath ?? activeNanyxFile();
      if (!file) {
        void vscode.window.showInformationMessage("Nanyx: open a .nyx file to debug.");
        return;
      }
      await vscode.debug.startDebugging(vscode.workspace.getWorkspaceFolder(vscode.Uri.file(file)), {
        type: "nanyx",
        request: "launch",
        name: `Debug ${path.basename(file)}`,
        program: file
      });
    })
  ];
}
