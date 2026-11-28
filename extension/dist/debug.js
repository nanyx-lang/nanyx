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
/** The arguments of `nanyx` for a session that debugs `program` with `args`, which follow a `--`. */
function debugArguments(program, args = []) {
    return ["run", "--debug", program, ...(args.length > 0 ? ["--", ...args] : [])];
}
/** The directory a session for `program` runs in: `cwd`, or the script's directory, or the package's. */
function workingDirectory(program, cwd) {
    if (cwd) {
        return cwd;
    }
    return program.endsWith(".nyx") ? path.dirname(program) : program;
}
//# sourceMappingURL=debug.js.map