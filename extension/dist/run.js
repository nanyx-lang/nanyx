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
exports.deleteDbgChannels = deleteDbgChannels;
exports.runFile = runFile;
const crypto_1 = require("crypto");
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const dbgValues_1 = require("./dbgValues");
/** The files runs still going write their `dbg`s to, which are deleted when the run ends or the extension stops. */
const channels = new Set();
/** Deletes the files of the runs still going, as the extension stops. */
function deleteDbgChannels() {
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
async function runFile(executable, file, dbg) {
    const folder = vscode.workspace.getWorkspaceFolder(file);
    const name = path.basename(file.fsPath);
    const channel = path.join(os.tmpdir(), `nanyx-dbg-${(0, crypto_1.randomUUID)()}.jsonl`);
    const args = ["run", "--dbg-to", channel, file.fsPath];
    if (vscode.workspace.getConfiguration("nanyx").get("developmentMode", false)) {
        args.push("--timings");
    }
    const task = new vscode.Task({ type: "nanyx", command: "run", file: file.fsPath }, folder ?? vscode.TaskScope.Global, `run ${name}`, "nanyx", 
    // A process, not a shell command line, so a path with spaces or quotes in it needs no escaping.
    new vscode.ProcessExecution(executable, args, { cwd: folder?.uri.fsPath ?? path.dirname(file.fsPath) }), 
    // No problem matcher: the language server already reports the file's errors in the editor.
    []);
    task.presentationOptions = {
        reveal: vscode.TaskRevealKind.Always,
        panel: vscode.TaskPanelKind.Dedicated,
        clear: true
    };
    channels.add(channel);
    const run = dbg.beginRun();
    const reader = new dbgValues_1.HitReader(channel);
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
//# sourceMappingURL=run.js.map