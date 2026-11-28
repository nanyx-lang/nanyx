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
exports.TestExplorer = exports.COMMAND_RUN_TEST = void 0;
const child_process_1 = require("child_process");
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const dbgValues_1 = require("./dbgValues");
const tests_1 = require("./tests");
/** The command a "Run test" link runs, with the test item's ID. */
exports.COMMAND_RUN_TEST = "nanyx.runTest";
/**
 * The test explorer: the tests of each package in the workspace, and of each script open outside one, as
 * `nanyx test --list --format json` lists them, refreshed when a `.nyx` or `.nyxp` file is saved, created or deleted;
 * runs of a test, a module's tests or a package's, through `nanyx test --format json`; and a "Run test" link above
 * each test (see `tests.ts` for the model, which is tested apart from VS Code).
 */
class TestExplorer {
    constructor(compiler, log, dbg) {
        this.compiler = compiler;
        this.log = log;
        this.dbg = dbg;
        this.controller = vscode.tests.createTestController("nanyx", "Nanyx");
        this.roots = new Map();
        this.ioTag = new vscode.TestTag("io");
        this.crashesTag = new vscode.TestTag("crashes");
        this.lensesChanged = new vscode.EventEmitter();
        this.onDidChangeCodeLenses = this.lensesChanged.event;
        this.disposables = [];
        this.controller.resolveHandler = async (item) => {
            if (!item) {
                await this.refresh();
            }
        };
        this.controller.refreshHandler = () => this.refresh();
        this.controller.createRunProfile("Run", vscode.TestRunProfileKind.Run, async (request, token) => {
            await this.run(request, token);
        }, true);
        const watcher = vscode.workspace.createFileSystemWatcher("**/*.{nyx,nyxp}");
        const later = () => this.refreshLater();
        this.disposables.push(this.controller, this.lensesChanged, watcher, watcher.onDidChange(later), watcher.onDidCreate(later), watcher.onDidDelete(later), vscode.workspace.onDidOpenTextDocument((document) => document.languageId === "nanyx" && later()), vscode.languages.registerCodeLensProvider({ language: "nanyx" }, this), vscode.commands.registerCommand(exports.COMMAND_RUN_TEST, (id) => this.runOne(id)));
    }
    dispose() {
        clearTimeout(this.refreshTimer);
        this.disposables.forEach((disposable) => disposable.dispose());
    }
    /** Lists the tests again a moment after a file changes, once for several changes together. */
    refreshLater() {
        clearTimeout(this.refreshTimer);
        this.refreshTimer = setTimeout(() => void this.refresh(), 1000);
    }
    /** Lists the tests of each package in the workspace, and of each script open outside one, again. */
    async refresh() {
        // A listing asked for later is this one.
        clearTimeout(this.refreshTimer);
        const executable = this.compiler();
        if (!executable) {
            return;
        }
        const manifests = await vscode.workspace.findFiles("**/*.nyxp", "**/{node_modules,.git}/**");
        const directories = manifests.map((manifest) => path.dirname(manifest.fsPath));
        const scripts = vscode.workspace.textDocuments
            .filter((document) => document.languageId === "nanyx" && document.uri.scheme === "file" && document.fileName.endsWith(".nyx"))
            .map((document) => document.fileName)
            .filter((file) => !directories.some((directory) => file.startsWith(directory + path.sep)));
        const targets = [
            ...directories.map((directory) => ({ target: directory, directory })),
            ...scripts.map((script) => ({ target: script, directory: path.dirname(script) }))
        ];
        const found = await Promise.all(targets.map(async (target) => ({ ...target, listing: await this.list(executable, target.target, target.directory) })));
        this.roots.clear();
        const items = [];
        for (const { target, directory, listing } of found) {
            if (listing.tests.length === 0) {
                continue;
            }
            const root = { target, directory, modules: (0, tests_1.modulesOf)(listing.tests) };
            this.roots.set((0, tests_1.rootId)(target), root);
            items.push(this.rootItem(root));
        }
        this.controller.items.replace(items);
        this.lensesChanged.fire();
    }
    /** The listing of `target`'s tests, run in `directory`: none when `nanyx test` can't list them, which it logs. */
    async list(executable, target, directory) {
        const { lines, errors } = await collect((0, child_process_1.spawn)(executable, (0, tests_1.listArguments)(target), { cwd: directory }));
        const listing = (0, tests_1.listingOf)(lines);
        if (!listing.built && errors.trim() !== "") {
            this.log(`nanyx test --list ${target}: ${errors.trim()}`);
        }
        return listing;
    }
    /** The tree of `root`: the package or script, each module, and each test, where it is written. */
    rootItem(root) {
        const item = this.controller.createTestItem((0, tests_1.rootId)(root.target), path.basename(root.target), vscode.Uri.file(root.target));
        for (const module of root.modules) {
            const moduleItem = this.controller.createTestItem((0, tests_1.moduleId)(root.target, module.module), module.module);
            for (const test of module.tests) {
                const at = (0, tests_1.positionOf)(root.directory, test.file, test.line, test.column);
                const testItem = this.controller.createTestItem((0, tests_1.testId)(root.target, test.name), test.test, vscode.Uri.file(at.path));
                testItem.range = new vscode.Range(at.line, at.character, at.line, at.character);
                testItem.tags = [...(test.io ? [this.ioTag] : []), ...(test.crashes ? [this.crashesTag] : [])];
                moduleItem.children.add(testItem);
            }
            item.children.add(moduleItem);
        }
        return item;
    }
    /** A "Run test" link above each test of `document`. */
    provideCodeLenses(document) {
        const lenses = [];
        this.controller.items.forEach((root) => root.children.forEach((module) => module.children.forEach((test) => {
            if (test.uri?.fsPath === document.uri.fsPath && test.range) {
                lenses.push(new vscode.CodeLens(test.range, { title: "Run test", command: exports.COMMAND_RUN_TEST, arguments: [test.id] }));
            }
        })));
        return lenses;
    }
    /** Runs the test whose item has the ID `id`, as its "Run test" link does. */
    async runOne(id) {
        let found;
        this.controller.items.forEach((root) => root.children.forEach((module) => (found ?? (found = module.children.get(id)))));
        if (found) {
            const cancel = new vscode.CancellationTokenSource();
            await this.run(new vscode.TestRunRequest([found]), cancel.token);
            cancel.dispose();
        }
    }
    /**
     * Runs what `request` chooses, a run of `nanyx test` for each package or script it chooses from, and gives how many
     * of its tests passed and how many didn't.
     */
    async run(request, token, filesInMemory = false) {
        const executable = this.compiler();
        const run = this.controller.createTestRun(request);
        const counts = { passed: 0, failed: 0 };
        try {
            if (!executable) {
                run.appendOutput("The Nanyx compiler isn't available.\r\n");
                return counts;
            }
            for (const [id, chosen] of this.chosenByRoot(request)) {
                if (token.isCancellationRequested) {
                    break;
                }
                const root = this.roots.get(id);
                if (root) {
                    await this.runRoot(executable, run, root, chosen, token, counts, filesInMemory);
                }
            }
        }
        finally {
            run.end();
        }
        return counts;
    }
    /** The tests written in `file`, a full path, with the root they are listed under. */
    testsIn(file) {
        for (const root of this.roots.values()) {
            const tests = root.modules
                .flatMap((module) => module.tests)
                .filter((test) => (0, tests_1.positionOf)(root.directory, test.file, test.line).path === file);
            if (tests.length > 0) {
                return { root, names: tests.map((test) => test.name), io: tests.filter((test) => test.io).length };
            }
        }
        return undefined;
    }
    /** How many tests are written in `file`, a full path, and how many of them are `@test(#io)`, as last listed. */
    testsOf(file) {
        const found = this.testsIn(file);
        return { count: found?.names.length ?? 0, io: found?.io ?? 0 };
    }
    /**
     * Runs the tests written in `file`, a full path, as live mode does on a save, as they were last listed: in the Testing
     * view, an `#io` test's files in memory, and their `dbg` values shown in the editor. Gives how many passed and how many didn't, or none when the file has
     * no tests.
     */
    async liveRun(file, token) {
        const found = this.testsIn(file);
        if (!found || token.isCancellationRequested) {
            return undefined;
        }
        const items = found.names.map((name) => this.itemOf(found.root, name)).filter((item) => item !== undefined);
        return this.run(new vscode.TestRunRequest(items), token, true);
    }
    /** The items `request` chooses, by the root they are in: every root when it chooses none. */
    chosenByRoot(request) {
        const chosen = new Map();
        const all = [];
        this.controller.items.forEach((root) => all.push(root));
        for (const item of request.include ?? all) {
            let top = item;
            while (top.parent) {
                top = top.parent;
            }
            chosen.set(top.id, [...(chosen.get(top.id) ?? []), item]);
        }
        return chosen;
    }
    /** Runs the items `chosen` of `root` in `run`, each test's result shown as its object arrives. */
    async runRoot(executable, run, root, chosen, token, counts, filesInMemory) {
        const rootChosen = chosen.some((item) => item.id === (0, tests_1.rootId)(root.target));
        const chosenModules = root.modules.filter((module) => chosen.some((item) => item.id === (0, tests_1.moduleId)(root.target, module.module))).map((module) => module.module);
        const chosenTests = root.modules.flatMap((module) => module.tests).filter((test) => chosen.some((item) => item.id === (0, tests_1.testId)(root.target, test.name))).map((test) => test.name);
        const selection = (0, tests_1.selectionOf)(rootChosen, root.modules, chosenModules, chosenTests);
        const tests = root.modules
            .flatMap((module) => module.tests)
            .filter((test) => selection.kind === "all" || (selection.kind === "module" ? test.module === selection.module : selection.names.includes(test.name)));
        const items = new Map(tests.map((test) => [test.name, this.itemOf(root, test.name)]));
        items.forEach((item) => item && run.enqueued(item));
        // The values of the tests' dbgs replace those of the run before, in each file they reach.
        const dbgRun = this.dbg.beginRun();
        const process = (0, child_process_1.spawn)(executable, (0, tests_1.runArguments)(root.target, selection, filesInMemory), { cwd: root.directory });
        const cancelled = token.onCancellationRequested(() => process.kill());
        const diagnostics = [];
        const splitter = new tests_1.LineSplitter();
        const ended = new Set();
        const take = (line) => {
            const event = (0, tests_1.parseLine)(line);
            switch (event?.type) {
                case "diagnostic":
                    diagnostics.push(event);
                    break;
                case "start":
                    run.appendOutput(`Running ${event.tests} tests in ${event.name}${event.deferredErrors > 0 ? ` with ${event.deferredErrors} deferred errors` : ""}, with seed ${event.seed}\r\n`);
                    items.forEach((item) => item && run.started(item));
                    break;
                case "test":
                    ended.add(event.name);
                    this.show(run, root, items.get(event.name), event);
                    if (event.outcome === "passed") {
                        counts.passed++;
                    }
                    else {
                        counts.failed++;
                    }
                    if (event.dbg && event.dbg.length > 0) {
                        this.dbg.add(dbgRun, (0, dbgValues_1.testHits)(root.directory, event.dbg));
                    }
                    break;
                case "summary":
                    if (!event.built) {
                        const message = new vscode.TestMessage((0, tests_1.buildFailureMessage)(diagnostics));
                        items.forEach((item) => item && run.errored(item, message));
                    }
                    break;
            }
        };
        process.stdout.setEncoding("utf8");
        process.stdout.on("data", (chunk) => splitter.push(chunk).forEach(take));
        let errors = "";
        process.stderr.setEncoding("utf8");
        process.stderr.on("data", (chunk) => (errors += chunk));
        process.on("error", (error) => (errors += error.message));
        await new Promise((resolve) => {
            process.on("close", () => resolve());
            process.on("error", () => resolve());
        });
        splitter.end().forEach(take);
        cancelled.dispose();
        if (errors.trim() !== "") {
            run.appendOutput(errors.replace(/\r?\n/g, "\r\n"));
        }
        // A test the run didn't report, as when it was cancelled or `nanyx test` couldn't start, is skipped.
        items.forEach((item, name) => item && !ended.has(name) && run.skipped(item));
    }
    /** The item of the test of the full name `name` in `root`. */
    itemOf(root, name) {
        let found;
        this.controller.items.get((0, tests_1.rootId)(root.target))?.children.forEach((module) => (found ?? (found = module.children.get((0, tests_1.testId)(root.target, name)))));
        return found;
    }
    /** Shows how the test `event` ended, on its item: its state, and for a failure its message, place and stack. */
    show(run, root, item, event) {
        if (!item) {
            return;
        }
        const result = (0, tests_1.resultOf)(event);
        if (result.output !== "") {
            run.appendOutput(result.output.trimEnd().replace(/\r?\n/g, "\r\n") + "\r\n", undefined, item);
        }
        if (result.state === "passed") {
            run.passed(item);
            return;
        }
        const message = new vscode.TestMessage(result.message ?? "It failed");
        const place = result.place ? (0, tests_1.positionOf)(root.directory, result.place.file, result.place.line, result.place.column) : undefined;
        if (place) {
            message.location = new vscode.Location(vscode.Uri.file(place.path), new vscode.Position(place.line, place.character));
        }
        else if (item.uri && item.range) {
            message.location = new vscode.Location(item.uri, item.range);
        }
        message.stackTrace = result.stack.map((frame) => {
            if (frame.file && frame.line) {
                const at = (0, tests_1.positionOf)(root.directory, frame.file, frame.line);
                return new vscode.TestMessageStackFrame(frame.function, vscode.Uri.file(at.path), new vscode.Position(at.line, 0));
            }
            return new vscode.TestMessageStackFrame(frame.function, undefined, undefined);
        });
        run.failed(item, message);
    }
}
exports.TestExplorer = TestExplorer;
/** What `process` writes to standard output, as lines, and to standard error, once it ends. */
function collect(process) {
    return new Promise((resolve) => {
        const splitter = new tests_1.LineSplitter();
        const lines = [];
        let errors = "";
        process.stdout?.setEncoding("utf8");
        process.stdout?.on("data", (chunk) => lines.push(...splitter.push(chunk)));
        process.stderr?.setEncoding("utf8");
        process.stderr?.on("data", (chunk) => (errors += chunk));
        process.on("error", (error) => (errors += error.message));
        process.on("close", () => resolve({ lines: [...lines, ...splitter.end()], errors }));
    });
}
//# sourceMappingURL=testController.js.map