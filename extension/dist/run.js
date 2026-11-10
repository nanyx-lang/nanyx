"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.runFile = runFile;
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
/**
 * Runs a saved `.nyx` file with `nanyx run`, as a task: the program gets a terminal of its own in the
 * panel, so it can read from stdin and be stopped with Ctrl+C, and running the file again reuses it.
 */
async function runFile(executable, file) {
    const folder = vscode.workspace.getWorkspaceFolder(file);
    const name = path.basename(file.fsPath);
    const task = new vscode.Task({ type: "nanyx", command: "run", file: file.fsPath }, folder ?? vscode.TaskScope.Global, `run ${name}`, "nanyx", 
    // A process, not a shell command line, so a path with spaces or quotes in it needs no escaping.
    new vscode.ProcessExecution(executable, ["run", file.fsPath], { cwd: folder?.uri.fsPath ?? path.dirname(file.fsPath) }), 
    // No problem matcher: the language server already reports the file's errors in the editor.
    []);
    task.presentationOptions = {
        reveal: vscode.TaskRevealKind.Always,
        panel: vscode.TaskPanelKind.Dedicated,
        clear: true
    };
    await vscode.tasks.executeTask(task);
}
//# sourceMappingURL=run.js.map