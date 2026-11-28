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
exports.COMMAND_DEBUG_FILE = void 0;
exports.registerDebugger = registerDebugger;
const crypto_1 = require("crypto");
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const debug_1 = require("./debug");
/** The command that debugs the active Nanyx file. */
exports.COMMAND_DEBUG_FILE = "nanyx.debugFile";
/** How long a session waits for `nanyx` in its terminal to say which port it listens on: long enough for a first build. */
const listenWithin = 120000;
/** The active editor's file, if it is a Nanyx file on disk. */
function activeNanyxFile() {
    const document = vscode.window.activeTextEditor?.document;
    return document && document.languageId === "nanyx" && document.uri.scheme === "file" ? document.fileName : undefined;
}
/** The port `nanyx` writes to `file`, once it has, or none if it doesn't within `within` milliseconds or `ended` says it stopped. */
async function portWritten(file, within, ended) {
    const until = Date.now() + within;
    while (Date.now() < until && !ended()) {
        try {
            const port = (0, debug_1.portIn)(fs.readFileSync(file, "utf8"));
            if (port !== undefined) {
                return port;
            }
        }
        catch {
            // Not written yet.
        }
        await new Promise((resolve) => setTimeout(resolve, 50));
    }
    return undefined;
}
/**
 * Registers the `nanyx` debug type (see `debug.ts`): a launch configuration's program, or the active file when it has
 * none, is debugged by `nanyx run --debug`, run by the `nanyx` that `compiler` gives. By default it runs in a terminal
 * of its own, which the program reads and writes, and the debugger listens on a port of 127.0.0.1 that the session
 * connects to; with `"console": "debugConsole"`, or where there is no terminal, it is run as the debug adapter itself,
 * what the program writes is in the Debug Console and its input is empty. **Nanyx: Debug the current file** starts a
 * session for the active file.
 */
function registerDebugger(compiler) {
    const notes = new Map();
    const configurations = {
        resolveDebugConfiguration(_folder, config) {
            const resolved = (0, debug_1.resolvedLaunch)(config, activeNanyxFile());
            if (!resolved) {
                void vscode.window.showInformationMessage("Nanyx: open a .nyx file to debug, or name one as the launch configuration's program.");
                return undefined;
            }
            return resolved;
        }
    };
    const adapters = {
        async createDebugAdapterDescriptor(session) {
            const executable = compiler();
            if (!executable) {
                throw new Error("Nanyx: no nanyx compiler was found to debug with. Run Nanyx: Download the compiler, or set nanyx.path.");
            }
            const config = session.configuration;
            const program = config.program;
            const cwd = (0, debug_1.workingDirectory)(program, config.cwd);
            const where = (0, debug_1.consoleOf)(config, vscode.env.uiKind === vscode.UIKind.Desktop);
            if (where.console === "integratedTerminal") {
                const portFile = path.join(os.tmpdir(), `nanyx-debug-${(0, crypto_1.randomUUID)()}.port`);
                const name = `Nanyx: debug ${path.basename(program)}`;
                let terminal;
                try {
                    terminal = vscode.window.createTerminal({ name, shellPath: executable, shellArgs: (0, debug_1.debugArguments)(program, config.args, portFile), cwd });
                }
                catch (error) {
                    // A terminal that can't be made is no reason not to debug: the Debug Console has what the program writes.
                    notes.set(session.id, `Nanyx: the program couldn't be run in a terminal (${error.message}), so what it writes is in the Debug Console, and its input is empty.`);
                }
                if (terminal) {
                    let ended = false;
                    const closing = vscode.window.onDidCloseTerminal((closed) => {
                        if (closed === terminal) {
                            ended = true;
                        }
                    });
                    terminal.show(true);
                    // It has ended once its process has, which leaves the terminal open with why, or once the terminal is closed.
                    const port = await portWritten(portFile, listenWithin, () => ended || terminal.exitStatus !== undefined);
                    closing.dispose();
                    fs.rmSync(portFile, { force: true });
                    if (port === undefined) {
                        throw new Error(`Nanyx: the debugger didn't start: the terminal "${name}" says why.`);
                    }
                    return new vscode.DebugAdapterServer(port, "127.0.0.1");
                }
            }
            else if (where.note) {
                notes.set(session.id, where.note);
            }
            return new vscode.DebugAdapterExecutable(executable, (0, debug_1.debugArguments)(program, config.args), { cwd });
        }
    };
    return [
        vscode.debug.registerDebugConfigurationProvider("nanyx", configurations),
        vscode.debug.registerDebugAdapterDescriptorFactory("nanyx", adapters),
        vscode.debug.onDidStartDebugSession((session) => {
            const note = notes.get(session.id);
            if (note) {
                notes.delete(session.id);
                vscode.debug.activeDebugConsole.appendLine(note);
            }
        }),
        vscode.commands.registerCommand(exports.COMMAND_DEBUG_FILE, async (uri) => {
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
//# sourceMappingURL=debugger.js.map