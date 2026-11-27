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
exports.LineSplitter = void 0;
exports.parseLine = parseLine;
exports.listingOf = listingOf;
exports.modulesOf = modulesOf;
exports.rootId = rootId;
exports.moduleId = moduleId;
exports.testId = testId;
exports.listArguments = listArguments;
exports.runArguments = runArguments;
exports.selectionOf = selectionOf;
exports.resultOf = resultOf;
exports.buildFailureMessage = buildFailureMessage;
exports.positionOf = positionOf;
const path = __importStar(require("path"));
/** One line of `nanyx test --format json` as an event; none for a line that isn't one, such as a blank one. */
function parseLine(line) {
    const trimmed = line.trim();
    if (!trimmed.startsWith("{")) {
        return undefined;
    }
    try {
        const parsed = JSON.parse(trimmed);
        switch (parsed?.type) {
            case "start":
            case "summary": {
                const seed = /"seed":\s*(\d+)/.exec(trimmed);
                return (seed ? { ...parsed, seed: seed[1] } : parsed);
            }
            case "diagnostic":
            case "test":
                return parsed;
            default:
                return undefined;
        }
    }
    catch {
        return undefined;
    }
}
/** Splits a process's output, which arrives in pieces, into whole lines. */
class LineSplitter {
    constructor() {
        this.pending = "";
    }
    /** The lines `chunk` completes. */
    push(chunk) {
        const text = this.pending + chunk;
        const lines = text.split(/\r?\n/);
        this.pending = lines.pop() ?? "";
        return lines;
    }
    /** What is left once the output ends. */
    end() {
        const rest = this.pending;
        this.pending = "";
        return rest === "" ? [] : [rest];
    }
}
exports.LineSplitter = LineSplitter;
/** The listing the lines `lines` of `nanyx test --list --format json` give. */
function listingOf(lines) {
    const listing = { built: false, tests: [], diagnostics: [] };
    for (const line of lines) {
        const event = parseLine(line);
        if (event?.type === "test") {
            listing.tests.push(event);
        }
        else if (event?.type === "diagnostic") {
            listing.diagnostics.push(event);
        }
        else if (event?.type === "summary") {
            listing.built = event.built;
        }
    }
    return listing;
}
/** `tests` by module, the modules in the order their first tests are listed, and each module's tests by where they are written. */
function modulesOf(tests) {
    const byModule = new Map();
    for (const test of tests) {
        const found = byModule.get(test.module) ?? [];
        found.push(test);
        byModule.set(test.module, found);
    }
    return [...byModule].map(([module, moduleTests]) => ({
        module,
        tests: [...moduleTests].sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line || a.column - b.column)
    }));
}
/** The ID of the test item of a package's root (its manifest's directory, or a script's path). */
function rootId(root) {
    return root;
}
/** The ID of the test item of the module `module` of the package at `root`. */
function moduleId(root, module) {
    return `${root}\u0000${module}`;
}
/** The ID of the test item of the test whose full name is `name`, of the package at `root`. */
function testId(root, name) {
    return `${root}\u0000\u0000${name}`;
}
/** The arguments of `nanyx test` that list the tests of `target`, a package's directory or a script. */
function listArguments(target) {
    return ["test", target, "--list", "--format", "json"];
}
/**
 * The arguments of `nanyx test` that run `selection` of `target`, a package's directory or a script, reporting as JSON,
 * with an `#io` test's files in memory when `filesInMemory`, as live mode runs tests.
 */
function runArguments(target, selection, filesInMemory = false) {
    const args = ["test", target, "--format", "json", ...(filesInMemory ? ["--files-in-memory"] : [])];
    switch (selection.kind) {
        case "all":
            return args;
        case "module":
            return [...args, "--module", selection.module];
        case "tests":
            // Several names go in one `--test`, one on each line.
            return [...args, "--test", selection.names.join("\n")];
    }
}
/**
 * What one run of `target` runs, for the items chosen: all its tests if its root is chosen, a module's if that module
 * is the one thing chosen, or else the tests chosen and the tests of the modules chosen, by name.
 */
function selectionOf(root, modules, chosenModules, chosenTests) {
    if (root) {
        return { kind: "all" };
    }
    if (chosenModules.length === 1 && chosenTests.length === 0) {
        return { kind: "module", module: chosenModules[0] };
    }
    const names = new Set(chosenTests);
    for (const module of modules) {
        if (chosenModules.includes(module.module)) {
            module.tests.forEach((test) => names.add(test.name));
        }
    }
    return { kind: "tests", names: [...names] };
}
/** The text of `failure`: the message it has, or else what the test wrote, or else what kind of failure it was. */
function failureText(failure, errors) {
    if (failure?.message) {
        return failure.message;
    }
    if (errors) {
        return errors;
    }
    switch (failure?.kind) {
        case "stackOverflow":
            return `It ran out of stack${failure.function ? ` in '${failure.function}'` : ""}`;
        case "outOfMemory":
            return "It ran out of memory";
        case "exited":
            return `It exited with ${failure.exitCode}`;
        default:
            return "It failed";
    }
}
/** The result of the test `event` (see `Result`). */
function resultOf(event) {
    const output = event.output ?? "";
    const stack = event.failure?.stack ?? [];
    const place = event.failure?.place;
    switch (event.outcome) {
        case "passed":
            return { state: "passed", stack: [], output };
        case "timedOut":
            return {
                state: "failed",
                message: `It ran for longer than ${event.timeoutSeconds} seconds, and was stopped: give it longer with --timeout`,
                stack: [],
                output
            };
        case "didNotCrash":
            return { state: "failed", message: "It finished, and was expected to crash", stack: [], output };
        case "crashedOtherwise":
            return {
                state: "failed",
                message: `It was expected to crash with a message that contains "${event.expected}", and crashed with another:\n${failureText(event.failure, event.errors)}`,
                place,
                stack,
                output
            };
        default:
            return { state: "failed", message: failureText(event.failure, event.errors), place, stack, output };
    }
}
/** What a build that failed says, for each test it would have run: its errors, each with where it is. */
function buildFailureMessage(diagnostics) {
    const errors = diagnostics.filter((diagnostic) => diagnostic.severity === "error");
    const lines = errors.map((diagnostic) => {
        const where = diagnostic.file ? `${diagnostic.file}:${diagnostic.line}:${diagnostic.column}: ` : "";
        return `${where}error ${diagnostic.code}: ${diagnostic.headline}: ${diagnostic.message}`;
    });
    return lines.length > 0 ? `The tests can't be built:\n${lines.join("\n")}` : "The tests can't be built";
}
/**
 * Where `file`, `line` and `column`, a place the compiler wrote, is: the file from `root`, the package's directory or
 * a script's, unless it is a full path already; the line and column from 1, the column 1 where it has none.
 */
function positionOf(root, file, line, column) {
    const full = path.isAbsolute(file) ? file : path.join(root, file);
    return { path: full, line: Math.max(0, line - 1), character: Math.max(0, (column ?? 1) - 1) };
}
//# sourceMappingURL=tests.js.map