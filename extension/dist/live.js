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
exports.packageDirectoryOf = packageDirectoryOf;
exports.targetOf = targetOf;
exports.isOn = isOn;
exports.testsOn = testsOn;
exports.liveArguments = liveArguments;
exports.heldBackBy = heldBackBy;
exports.rowsOf = rowsOf;
exports.endedText = endedText;
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
/** The directory of the package `file` is in, by the nearest `.nyxp` manifest at or above its directory, if it has one. */
function packageDirectoryOf(file, exists = fs.readdirSync) {
    let directory = path.dirname(file);
    for (;;) {
        let names = [];
        try {
            names = exists(directory);
        }
        catch {
            names = [];
        }
        if (names.some((name) => name.endsWith(".nyxp"))) {
            return directory;
        }
        const parent = path.dirname(directory);
        if (parent === directory) {
            return undefined;
        }
        directory = parent;
    }
}
/** What a save of `file` re-runs: its package, if it is in one, or else the file as a script. */
function targetOf(file, packageDirectory) {
    return packageDirectory
        ? { key: packageDirectory, target: packageDirectory, directory: packageDirectory, name: path.basename(packageDirectory), kind: "package" }
        : { key: file, target: file, directory: path.dirname(file), name: path.basename(file), kind: "script" };
}
/** Whether live mode is on: as the user set it for the target, or else on where it is offered on its own. */
function isOn(info, setting) {
    if (!info || info.entry === null) {
        return false;
    }
    return setting ?? info.offered;
}
/**
 * Whether a file's tests re-run on a save: as the user set it for its script or package, or else on. Every test is safe
 * to re-run: one that isn't `#io` has its own `Console`, and a live run keeps an `#io` test's files in memory (`nanyx test
 * --files-in-memory`), its `Env` only read and its `Console` its own output.
 */
function testsOn(tests, setting) {
    if (!tests || tests.count === 0) {
        return false;
    }
    return setting ?? true;
}
/** The arguments of `nanyx run` for a live run of `target`, its `dbg`s written to `dbgTo`. */
function liveArguments(target, dbgTo) {
    return ["run", "--dbg-to", dbgTo, "--files-in-memory", target];
}
/** The contexts that hold live mode back until it is turned on: those `info`'s entry point requires beyond the offered ones. */
function heldBackBy(info) {
    const offered = new Set(["Async", "Console", "FileSystem"]);
    return info.contexts.filter((context) => !offered.has(context));
}
/**
 * What the sidebar says of `target`, whose entry point is `info`, run live when `on`, and of the tests of its active
 * file, `tests`, re-run when `testsAreOn`.
 */
function rowsOf(target, info, on, tests, testsAreOn = false) {
    const hasEntry = info !== undefined && info.entry !== null;
    const hasTests = tests !== undefined && tests.count > 0;
    if (!hasEntry && !hasTests) {
        return [
            {
                label: "No entry point or tests",
                description: target.name,
                tooltip: "Live mode re-runs a script, or a package's @entry, and a file's tests, on every save."
            }
        ];
    }
    const live = on || testsAreOn;
    const rows = [
        {
            label: live ? "Live mode is on" : "Live mode is off",
            description: target.name,
            tooltip: live ? "It re-runs on every save." : "Turn it on to re-run on every save."
        }
    ];
    if (hasEntry) {
        rows.push({
            label: "Needs",
            description: info.contexts.length > 0 ? info.contexts.join(", ") : "no contexts",
            tooltip: "The contexts the entry point requires, from its type."
        }, {
            label: "Files are in memory",
            description: "writes never reach the disk",
            tooltip: "A live run reads the real files, and keeps what it writes, appends, makes and deletes in memory (nanyx run --files-in-memory)."
        }, {
            label: "Input is empty",
            description: "readLine sees the end of input",
            tooltip: "A live run is given no input."
        });
        const held = heldBackBy(info);
        if (held.length > 0) {
            rows.push({
                label: on ? "Runs for real" : "Held back",
                description: held.join(", "),
                tooltip: on
                    ? `It was turned on for this ${target.kind}, so these run as they do in a terminal.`
                    : `It needs ${held.join(", ")}, which a re-run on every save would use for real: turn live mode on for this ${target.kind} to run it anyway.`
            });
        }
    }
    if (hasTests) {
        const last = tests.last ? `, last run: ${tests.last.passed} passed, ${tests.last.failed} failed` : "";
        rows.push({
            label: "Tests",
            description: `${tests.count} in this file${last}`,
            tooltip: testsAreOn ? "They re-run on every save, in the Testing view." : "They re-run on every save once live mode is on."
        });
        if (tests.io > 0) {
            rows.push({
                label: "Files are in memory",
                description: `for ${tests.io} #io test${tests.io === 1 ? "" : "s"} too`,
                tooltip: "A @test(#io) test reads the real files, and keeps what it writes, appends, makes and deletes in memory (nanyx test --files-in-memory)."
            });
        }
    }
    return rows;
}
/** How a live run ended, as the output channel says it. */
function endedText(code, signal, milliseconds, timedOut, limitSeconds) {
    if (timedOut) {
        return `Stopped after ${limitSeconds} s: give it longer with nanyx.live.timeLimit.`;
    }
    if (signal) {
        return `Stopped (${signal}).`;
    }
    return code === 0 ? `Finished in ${milliseconds} ms.` : `Ended with exit code ${code}, after ${milliseconds} ms.`;
}
//# sourceMappingURL=live.js.map