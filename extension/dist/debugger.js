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
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const debug_1 = require("./debug");
/** The command that debugs the active Nanyx file. */
exports.COMMAND_DEBUG_FILE = "nanyx.debugFile";
/** The active editor's file, if it is a Nanyx file on disk. */
function activeNanyxFile() {
    const document = vscode.window.activeTextEditor?.document;
    return document && document.languageId === "nanyx" && document.uri.scheme === "file" ? document.fileName : undefined;
}
/**
 * Registers the `nanyx` debug type (see `debug.ts`): a launch configuration's program, or the active file when it has
 * none, is debugged by `nanyx run --debug`, run as the debug adapter by the `nanyx` that `compiler` gives, and
 * **Nanyx: Debug the current file** starts a session for the active file.
 */
function registerDebugger(compiler) {
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
        createDebugAdapterDescriptor(session) {
            const executable = compiler();
            if (!executable) {
                throw new Error("Nanyx: no nanyx compiler was found to debug with. Run Nanyx: Download the compiler, or set nanyx.path.");
            }
            const config = session.configuration;
            const program = config.program;
            return new vscode.DebugAdapterExecutable(executable, (0, debug_1.debugArguments)(program, config.args), {
                cwd: (0, debug_1.workingDirectory)(program, config.cwd)
            });
        }
    };
    return [
        vscode.debug.registerDebugConfigurationProvider("nanyx", configurations),
        vscode.debug.registerDebugAdapterDescriptorFactory("nanyx", adapters),
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