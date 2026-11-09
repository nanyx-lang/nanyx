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
exports.copyBuild = copyBuild;
exports.removeOldCopies = removeOldCopies;
exports.watchBuild = watchBuild;
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
/**
 * Development mode, for working on the compiler: the language server runs from a copy of the
 * `nanyx.path` build rather than the build itself, so the running server does not lock the build's
 * files (as Windows does) and `dotnet build` can overwrite them. The build's folder is watched, and
 * once it stops changing a fresh copy is made and the server restarted with it.
 */
/** How long the build's folder must be quiet before it is copied, so a build in progress is not caught half written. */
const SETTLE_DELAY = 1500;
const RETRY_DELAY = 3000;
const MAX_RETRIES = 5;
const COPIES_DIR = "development";
/**
 * Copies the folder holding `executable` (a framework-dependent build needs the DLLs beside it, not
 * just the executable) into the extension's storage and returns the copy's executable.
 */
function copyBuild(context, executable) {
    // `dotnet clean`, or a build that has not written it yet.
    if (!fs.existsSync(executable)) {
        throw new Error(`${executable} does not exist`);
    }
    // Named after this window's process, so other windows (each running its own copy) leave it alone.
    const target = path.join(copiesDir(context), `${process.pid}-${Date.now()}`);
    fs.cpSync(path.dirname(executable), target, { recursive: true });
    return path.join(target, path.basename(executable));
}
/**
 * Deletes this window's copies other than the one `keep` runs from, and the copies of windows that
 * have closed. Call it only once the servers they ran have stopped. One still in use is left for next time.
 */
function removeOldCopies(context, keep) {
    const copies = copiesDir(context);
    const kept = keep && path.basename(path.dirname(keep));
    let entries;
    try {
        entries = fs.readdirSync(copies);
    }
    catch {
        return;
    }
    for (const entry of entries) {
        const pid = Number(entry.split("-")[0]);
        if (entry !== kept && (pid === process.pid || !isRunning(pid))) {
            try {
                fs.rmSync(path.join(copies, entry), { recursive: true, force: true });
            }
            catch {
                // Still in use.
            }
        }
    }
}
function copiesDir(context) {
    return path.join(context.globalStorageUri.fsPath, COPIES_DIR);
}
function isRunning(pid) {
    if (!Number.isInteger(pid) || pid <= 0) {
        return false;
    }
    try {
        process.kill(pid, 0);
        return true;
    }
    catch (error) {
        // EPERM means it exists but belongs to someone else.
        return error.code === "EPERM";
    }
}
/**
 * Calls `rebuilt` once the folder holding `executable` has changed and then been quiet for a moment.
 * When `rebuilt` throws (the build may still hold a file open) it is tried again a few times.
 */
function watchBuild(executable, rebuilt, log) {
    const folder = path.dirname(executable);
    let timer;
    let retries = 0;
    const schedule = (delay) => {
        if (timer) {
            clearTimeout(timer);
        }
        timer = setTimeout(async () => {
            timer = undefined;
            try {
                await rebuilt();
                retries = 0;
            }
            catch (error) {
                if (retries++ < MAX_RETRIES) {
                    log(`Could not copy the new build yet (${error instanceof Error ? error.message : String(error)}); trying again.`);
                    schedule(RETRY_DELAY);
                }
                else {
                    retries = 0;
                    log(`Gave up copying the new build: ${error instanceof Error ? error.message : String(error)}`);
                }
            }
        }, delay);
    };
    const changed = () => {
        retries = 0;
        schedule(SETTLE_DELAY);
    };
    const watcher = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(vscode.Uri.file(folder), "*"));
    watcher.onDidCreate(changed);
    watcher.onDidChange(changed);
    return new vscode.Disposable(() => {
        if (timer) {
            clearTimeout(timer);
        }
        watcher.dispose();
    });
}
//# sourceMappingURL=developmentMode.js.map