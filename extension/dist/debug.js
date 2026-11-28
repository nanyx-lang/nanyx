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
exports.resolvedLaunch = resolvedLaunch;
exports.debugArguments = debugArguments;
exports.consoleOf = consoleOf;
exports.portIn = portIn;
exports.workingDirectory = workingDirectory;
const path = __importStar(require("path"));
/**
 * The configuration a session is started with: `config` as written, or, for one with no program, the file `active`,
 * the active editor's when it is a Nanyx file; none when there is nothing to debug.
 */
function resolvedLaunch(config, active) {
    const program = config.program ?? active;
    if (!program) {
        return undefined;
    }
    return {
        ...config,
        type: config.type ?? "nanyx",
        request: config.request ?? "launch",
        name: config.name ?? `Debug ${path.basename(program)}`,
        program
    };
}
/**
 * The arguments of `nanyx` for a session that debugs `program` with `args`, which follow a `--`: on its standard
 * streams, or, given `portFile`, on a port of 127.0.0.1 the system chooses and `nanyx` writes to that file, the program
 * then having the terminal it runs in.
 */
function debugArguments(program, args = [], portFile) {
    const listening = portFile ? ["--dap-port", "0", "--dap-port-file", portFile] : [];
    return ["run", "--debug", ...listening, program, ...(args.length > 0 ? ["--", ...args] : [])];
}
/**
 * Where a session's program reads and writes, and why it isn't where `config` asked: in a terminal, as by default,
 * unless the configuration asks for the Debug Console, or there is no terminal to run it in (`terminals`), as in VS Code
 * for the web.
 */
function consoleOf(config, terminals) {
    const asked = config.console ?? "integratedTerminal";
    if (asked === "integratedTerminal" && !terminals) {
        return {
            console: "debugConsole",
            note: "Nanyx: there is no terminal to run the program in here, so what it writes is in the Debug Console, and its input is empty."
        };
    }
    return { console: asked };
}
/** The port `nanyx` wrote to its port file, `text`, if it is one. */
function portIn(text) {
    const port = Number(text.trim());
    return Number.isInteger(port) && port > 0 && port <= 65535 ? port : undefined;
}
/** The directory a session for `program` runs in: `cwd`, or the script's directory, or the package's. */
function workingDirectory(program, cwd) {
    if (cwd) {
        return cwd;
    }
    return program.endsWith(".nyx") ? path.dirname(program) : program;
}
//# sourceMappingURL=debug.js.map