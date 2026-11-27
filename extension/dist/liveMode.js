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
exports.LiveMode = exports.COMMAND_TOGGLE_SHOW = exports.COMMAND_TOGGLE_LIVE = void 0;
const child_process_1 = require("child_process");
const crypto_1 = require("crypto");
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
const dbgValues_1 = require("./dbgValues");
const live_1 = require("./live");
/** The command that turns live mode on or off for the active file's script or package. */
exports.COMMAND_TOGGLE_LIVE = "nanyx.toggleLiveMode";
/** The command that switches a live run between showing its `dbg`s' values and every value (`nanyx.live.show`). */
exports.COMMAND_TOGGLE_SHOW = "nanyx.toggleShowValues";
/** Where the user's choices are kept, by target, in the workspace's state. */
const settingsKey = "nanyx.liveMode";
/** How often a live run's `dbg` file is read for what it has written since. */
const readEvery = 200;
/**
 * Live mode (see `live.ts`): it re-runs a script, or a package's `@entry`, on every save, with its files in memory, no
 * input, and its `dbg`s shown in the editor, and writes what it printed to the "Nanyx Live" output channel. It is on
 * on its own where the entry point requires none but `Console`, `Async` and `FileSystem`, and elsewhere only once the
 * user turns it on, from the Live mode view in the Nanyx sidebar or **Nanyx: Toggle live mode**. A save while a run is
 * still going stops it and starts another, and a run past `nanyx.live.timeLimit` seconds is stopped. With
 * `nanyx.live.show` set to `every`, which the view's row and **Nanyx: Toggle showing every value** switch, a run shows
 * every value its code binds or gives.
 */
class LiveMode {
    constructor(state, compiler, client, dbg, tests) {
        this.state = state;
        this.compiler = compiler;
        this.client = client;
        this.dbg = dbg;
        this.tests = tests;
        this.output = vscode.window.createOutputChannel("Nanyx Live");
        this.changed = new vscode.EventEmitter();
        this.onDidChangeTreeData = this.changed.event;
        this.infos = new Map();
        this.running = new Map();
        /** The live test runs still going, by file, and how each file's last one went. */
        this.testRuns = new Map();
        this.lastTests = new Map();
        this.disposables = [];
        this.view = vscode.window.createTreeView("nanyx.live", { treeDataProvider: this });
        this.disposables.push(this.output, this.changed, this.view, vscode.commands.registerCommand(exports.COMMAND_TOGGLE_LIVE, () => this.toggle()), vscode.commands.registerCommand(exports.COMMAND_TOGGLE_SHOW, () => this.toggleShow()), vscode.workspace.onDidChangeConfiguration((event) => event.affectsConfiguration("nanyx.live.show") && this.changed.fire()), vscode.window.onDidChangeActiveTextEditor(() => void this.refresh()), vscode.workspace.onDidSaveTextDocument((document) => void this.saved(document)));
        void this.refresh();
    }
    dispose() {
        this.running.forEach((run) => run.process.kill());
        this.testRuns.forEach((cancel) => cancel.cancel());
        this.disposables.forEach((disposable) => disposable.dispose());
    }
    /** The script or package of the active editor's file, if it is a Nanyx file on disk. */
    activeTarget() {
        const document = vscode.window.activeTextEditor?.document;
        if (!document || document.languageId !== "nanyx" || document.uri.scheme !== "file") {
            return undefined;
        }
        return { target: (0, live_1.targetOf)(document.fileName, (0, live_1.packageDirectoryOf)(document.fileName)), document };
    }
    setting(target) {
        return this.state.get(settingsKey, {})[target.key];
    }
    /** Asks the language server what the active file's entry point is, and shows it. */
    async refresh() {
        const active = this.activeTarget();
        if (active) {
            const info = await this.infoOf(active.document);
            // A package's entry point is known from its `@entry` file: another of its files has none of its own.
            if (info && (active.target.kind === "script" || info.entry !== null)) {
                this.infos.set(active.target.key, info);
            }
        }
        this.changed.fire();
    }
    async infoOf(document) {
        const client = this.client();
        if (!client) {
            return undefined;
        }
        try {
            return await client.sendRequest("nanyx/liveMode", { textDocument: { uri: document.uri.toString() } });
        }
        catch {
            return undefined;
        }
    }
    getTreeItem(row) {
        const item = new vscode.TreeItem(row.label);
        item.description = row.description;
        item.tooltip = row.tooltip;
        if (row.label.startsWith("Live mode is")) {
            item.contextValue = "liveSwitch";
            item.iconPath = new vscode.ThemeIcon(row.label.endsWith("on") ? "pass-filled" : "circle-large-outline");
        }
        else if (row.label.startsWith("Shows ")) {
            item.contextValue = "showSwitch";
            item.iconPath = new vscode.ThemeIcon(row.label === "Shows every value" ? "eye" : "debug-console");
        }
        return item;
    }
    getChildren(row) {
        if (row) {
            return [];
        }
        const active = this.activeTarget();
        if (!active) {
            return [{ label: "Open a .nyx file", tooltip: "Live mode re-runs a script, or a package's @entry, on every save." }];
        }
        const info = this.infos.get(active.target.key);
        const setting = this.setting(active.target);
        const tests = this.testsOf(active.document.fileName);
        return (0, live_1.rowsOf)(active.target, info, (0, live_1.isOn)(info, setting), tests, (0, live_1.testsOn)(tests, setting), this.show());
    }
    /** What a live run shows (`nanyx.live.show`). */
    show() {
        return vscode.workspace.getConfiguration("nanyx").get("live.show", "dbg") === "every" ? "every" : "dbg";
    }
    /**
     * Switches what a live run shows, where the setting is set (the workspace's, or else the user's), and runs the active
     * file's script or package again, if live mode is on for it, to show it.
     */
    async toggleShow() {
        const configuration = vscode.workspace.getConfiguration("nanyx");
        const inWorkspace = configuration.inspect("live.show")?.workspaceValue !== undefined;
        await configuration.update("live.show", this.show() === "every" ? "dbg" : "every", inWorkspace ? vscode.ConfigurationTarget.Workspace : vscode.ConfigurationTarget.Global);
        this.changed.fire();
        const active = this.activeTarget();
        if (active && (0, live_1.isOn)(this.infos.get(active.target.key), this.setting(active.target))) {
            void this.run(active.target);
        }
    }
    /** Turns live mode on or off for the active file's script or package, and runs it once when it is turned on. */
    async toggle() {
        const active = this.activeTarget();
        if (!active) {
            void vscode.window.showInformationMessage("Live mode is for a .nyx file: open one first.");
            return;
        }
        await Promise.all([this.refresh(), this.tests.refresh()]);
        const info = this.infos.get(active.target.key);
        const file = active.document.fileName;
        const tests = this.testsOf(file);
        const hasEntry = info !== undefined && info.entry !== null;
        if (!hasEntry && tests.count === 0) {
            void vscode.window.showInformationMessage(`${active.target.name} has no entry point or tests to run.`);
            return;
        }
        const setting = this.setting(active.target);
        const on = !((0, live_1.isOn)(info, setting) || (0, live_1.testsOn)(tests, setting));
        await this.state.update(settingsKey, { ...this.state.get(settingsKey, {}), [active.target.key]: on });
        this.changed.fire();
        if (on) {
            if (hasEntry) {
                void this.run(active.target);
            }
            if (tests.count > 0) {
                void this.runTests(file);
            }
        }
        else {
            this.running.get(active.target.key)?.process.kill();
            this.testRuns.get(file)?.cancel();
        }
    }
    /** The tests written in `file`, as the Testing view last listed them, and how its last live run of them went. */
    testsOf(file) {
        return { ...this.tests.testsOf(file), last: this.lastTests.get(file) };
    }
    /** Re-runs the tests written in `file`, after stopping a live run of them still going, within the time limit. */
    async runTests(file) {
        this.testRuns.get(file)?.cancel();
        const cancel = new vscode.CancellationTokenSource();
        this.testRuns.set(file, cancel);
        const limit = vscode.workspace.getConfiguration("nanyx").get("live.timeLimit", 10);
        const timer = setTimeout(() => cancel.cancel(), limit * 1000);
        try {
            const counts = await this.tests.liveRun(file, cancel.token);
            if (counts && !cancel.token.isCancellationRequested) {
                this.lastTests.set(file, counts);
            }
        }
        finally {
            clearTimeout(timer);
            if (this.testRuns.get(file) === cancel) {
                this.testRuns.delete(file);
            }
            cancel.dispose();
            this.changed.fire();
        }
    }
    /** Re-runs the script or package of a saved file, if live mode is on for it. */
    async saved(document) {
        if (document.languageId !== "nanyx" || document.uri.scheme !== "file") {
            return;
        }
        const target = (0, live_1.targetOf)(document.fileName, (0, live_1.packageDirectoryOf)(document.fileName));
        // Asked again, since the save may have changed what the entry point requires.
        const info = await this.infoOf(document);
        if (info && (target.kind === "script" || info.entry !== null)) {
            this.infos.set(target.key, info);
        }
        // The tests are listed again first, since the save may have added or removed some.
        await this.tests.refresh();
        this.changed.fire();
        const setting = this.setting(target);
        const runs = [];
        if ((0, live_1.isOn)(this.infos.get(target.key), setting)) {
            runs.push(this.run(target));
        }
        // A file's own tests: the tests of the modules that import it aren't re-run (#3157).
        if ((0, live_1.testsOn)(this.testsOf(document.fileName), setting)) {
            runs.push(this.runTests(document.fileName));
        }
        await Promise.all(runs);
    }
    /** Runs `target` live, after stopping a run of it still going. */
    async run(target) {
        const executable = this.compiler();
        if (!executable) {
            return;
        }
        const previous = this.running.get(target.key);
        if (previous) {
            previous.process.kill();
            await previous.finished;
        }
        const channel = path.join(os.tmpdir(), `nanyx-live-${(0, crypto_1.randomUUID)()}.jsonl`);
        const limit = vscode.workspace.getConfiguration("nanyx").get("live.timeLimit", 10);
        const started = Date.now();
        this.output.clear();
        this.output.appendLine(`▶ ${target.name}, live: files in memory, no input`);
        // No input: what the program reads sees the end of it.
        const process = (0, child_process_1.spawn)(executable, (0, live_1.liveArguments)(target.target, channel, this.show()), { cwd: target.directory, stdio: ["ignore", "pipe", "pipe"] });
        const dbgRun = this.dbg.beginRun();
        const reader = new dbgValues_1.HitReader(channel);
        const read = () => {
            const hits = reader.read();
            if (hits.length > 0) {
                this.dbg.add(dbgRun, hits);
            }
        };
        const reading = setInterval(read, readEvery);
        let timedOut = false;
        const timer = setTimeout(() => {
            timedOut = true;
            process.kill();
        }, limit * 1000);
        process.stdout?.setEncoding("utf8");
        process.stdout?.on("data", (chunk) => this.output.append(chunk));
        process.stderr?.setEncoding("utf8");
        process.stderr?.on("data", (chunk) => this.output.append(chunk));
        const finished = new Promise((resolve) => {
            const done = (code, signal) => {
                clearInterval(reading);
                clearTimeout(timer);
                read();
                fs.rmSync(channel, { force: true });
                if (this.running.get(target.key)?.process === process) {
                    this.running.delete(target.key);
                }
                this.output.appendLine((0, live_1.endedText)(code, signal, Date.now() - started, timedOut, limit));
                resolve();
            };
            process.on("close", done);
            process.on("error", (error) => {
                this.output.appendLine(`Couldn't start nanyx: ${error.message}`);
                done(null, null);
            });
        });
        this.running.set(target.key, { process, finished });
        this.output.show(true);
    }
}
exports.LiveMode = LiveMode;
//# sourceMappingURL=liveMode.js.map