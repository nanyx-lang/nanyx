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
exports.activate = activate;
exports.deactivate = deactivate;
const vscode = __importStar(require("vscode"));
const node_1 = require("vscode-languageclient/node");
const compiler_1 = require("./compiler");
const developmentMode_1 = require("./developmentMode");
const run_1 = require("./run");
const COMMAND_DOWNLOAD = "nanyx.downloadCompiler";
const COMMAND_RESTART = "nanyx.restartServer";
const COMMAND_RUN = "nanyx.runFile";
/** When the extension last asked GitHub for a newer compiler release (milliseconds since the epoch). */
const LAST_UPDATE_CHECK = "nanyx.lastUpdateCheck";
const UPDATE_CHECK_INTERVAL = 24 * 60 * 60 * 1000;
let client;
/** The `nanyx` the language server was last started with, which is also the one that runs files. */
let compiler;
/** In development mode, watches the `nanyx.path` build for changes. */
let buildWatcher;
let outputChannel;
let declinedDownload = false;
let declinedUpdate = false;
async function activate(context) {
    outputChannel = vscode.window.createOutputChannel("Nanyx");
    context.subscriptions.push(outputChannel);
    context.subscriptions.push(vscode.commands.registerCommand(COMMAND_DOWNLOAD, async () => {
        declinedDownload = false;
        const settings = (0, compiler_1.readSettings)();
        const version = await (0, compiler_1.versionToDownload)(context, settings, log);
        const executable = await downloadAndRecordCheck(context, settings, version);
        if (executable) {
            await restartClient(context, executable);
            if ((0, compiler_1.tracksNewestRelease)(settings)) {
                (0, compiler_1.removeDownloadedExcept)(context, version);
            }
        }
    }), vscode.commands.registerCommand(COMMAND_RESTART, () => startOrRestart(context)), vscode.commands.registerCommand(COMMAND_RUN, (uri) => runCurrentFile(context, uri)), vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration("nanyx.path") ||
            event.affectsConfiguration("nanyx.developmentMode") ||
            event.affectsConfiguration("nanyx.compilerVersion") ||
            event.affectsConfiguration("nanyx.downloadUrl") ||
            event.affectsConfiguration("nanyx.asciiTypes") ||
            event.affectsConfiguration("nanyx.greekTypeVariables") ||
            event.affectsConfiguration("nanyx.topLevelTypeHints") ||
            event.affectsConfiguration("nanyx.inlayHints")) {
            void startOrRestart(context);
        }
    }));
    await startOrRestart(context);
}
async function deactivate() {
    buildWatcher?.dispose();
    await stopClient();
}
/**
 * Starts the language server with whatever `nanyx` can be found, downloading one if the user agrees.
 * When it is a downloaded copy, a newer compiler release is looked for afterwards, never before.
 */
async function startOrRestart(context) {
    buildWatcher?.dispose();
    buildWatcher = undefined;
    const settings = (0, compiler_1.readSettings)();
    if (vscode.workspace.getConfiguration("nanyx").get("developmentMode", false)) {
        if (settings.path) {
            await startDevelopmentBuild(context, settings.path);
            return;
        }
        void vscode.window.showWarningMessage("Nanyx: development mode needs nanyx.path to point at your local build of the compiler.");
    }
    let located;
    try {
        located = (0, compiler_1.locateCompiler)(context, settings);
    }
    catch (error) {
        void vscode.window.showErrorMessage(`Nanyx: ${messageOf(error)}`);
        return;
    }
    const executable = located?.executable ?? (await promptAndDownload(context, settings));
    if (executable) {
        await restartClient(context, executable);
        (0, developmentMode_1.removeOldCopies)(context);
    }
    if (located?.downloadedVersion && (0, compiler_1.tracksNewestRelease)(settings)) {
        void checkForUpdate(context, settings, located.downloadedVersion);
    }
}
/**
 * Runs a `.nyx` file with `nanyx run`: the one the editor title's button was pressed on, or else the
 * active editor's. It is saved first, since the compiler reads it from disk.
 */
async function runCurrentFile(context, uri) {
    const document = uri ? await vscode.workspace.openTextDocument(uri) : vscode.window.activeTextEditor?.document;
    if (!document || document.languageId !== "nanyx") {
        void vscode.window.showWarningMessage("Nanyx: open a .nyx file to run it.");
        return;
    }
    if (document.isUntitled || document.uri.scheme !== "file") {
        void vscode.window.showWarningMessage("Nanyx: save the file to disk before running it.");
        return;
    }
    if (document.isDirty && !(await document.save())) {
        void vscode.window.showErrorMessage(`Nanyx: could not save ${document.fileName}, so it was not run.`);
        return;
    }
    if (!compiler) {
        // Asking to run is asking for a compiler, so offer the download again even if it was declined earlier.
        declinedDownload = false;
        await startOrRestart(context);
        if (!compiler) {
            return;
        }
    }
    await (0, run_1.runFile)(compiler, document.uri);
}
/**
 * Development mode: runs the language server from a copy of `build`, so `dotnet build` can overwrite
 * it, and restarts the server with a fresh copy whenever the build changes.
 */
async function startDevelopmentBuild(context, build) {
    let copy;
    try {
        copy = (0, developmentMode_1.copyBuild)(context, build);
    }
    catch (error) {
        void vscode.window.showErrorMessage(`Nanyx: could not copy the build at ${build} (${messageOf(error)}).`);
        return;
    }
    log(`Development mode: running a copy of ${build}, and restarting when it is rebuilt.`);
    await restartClient(context, copy);
    (0, developmentMode_1.removeOldCopies)(context, copy);
    buildWatcher = (0, developmentMode_1.watchBuild)(build, async () => {
        const fresh = (0, developmentMode_1.copyBuild)(context, build);
        log(`${build} was rebuilt; restarting the language server.`);
        await restartClient(context, fresh);
        (0, developmentMode_1.removeOldCopies)(context, fresh);
        vscode.window.setStatusBarMessage("$(sync) Nanyx: restarted with the new build", 5000);
    }, log);
}
/** Offers to download the compiler once per session; returns the executable if the user agreed and it worked. */
async function promptAndDownload(context, settings) {
    if (declinedDownload) {
        return undefined;
    }
    const version = await (0, compiler_1.versionToDownload)(context, settings, log);
    const download = "Download";
    const choice = await vscode.window.showInformationMessage(`The Nanyx compiler was not found on PATH. Download nanyx ${version} for this platform? ` +
        "(It is kept in the extension's storage; set nanyx.path to use your own build instead.)", download, "Not now");
    if (choice !== download) {
        declinedDownload = true;
        return undefined;
    }
    return downloadAndRecordCheck(context, settings, version);
}
async function downloadAndRecordCheck(context, settings, version) {
    const executable = await download(context, settings, version);
    if (executable && (0, compiler_1.tracksNewestRelease)(settings)) {
        // The version was just chosen from GitHub's list, so there is nothing newer to look for today.
        await context.globalState.update(LAST_UPDATE_CHECK, Date.now());
    }
    return executable;
}
/**
 * Looks for a compiler release newer than the downloaded copy in use, at most once a day, and offers
 * to download it and restart the server. Being offline or rate limited just means no offer.
 */
async function checkForUpdate(context, settings, installed) {
    if (declinedUpdate || Date.now() - context.globalState.get(LAST_UPDATE_CHECK, 0) < UPDATE_CHECK_INTERVAL) {
        return;
    }
    await context.globalState.update(LAST_UPDATE_CHECK, Date.now());
    let newest;
    try {
        newest = await (0, compiler_1.newestRelease)();
    }
    catch (error) {
        log(`Could not check for a newer compiler release: ${messageOf(error)}`);
        return;
    }
    if (!newest || (0, compiler_1.compareVersions)(newest, installed) <= 0) {
        return;
    }
    const update = "Update";
    const choice = await vscode.window.showInformationMessage(`Nanyx compiler ${newest} is available (this is ${installed}). Download it and restart the language server?`, update, "Not now");
    if (choice !== update) {
        declinedUpdate = true;
        return;
    }
    const executable = await download(context, settings, newest);
    if (executable) {
        await restartClient(context, executable);
        (0, compiler_1.removeDownloadedExcept)(context, newest);
    }
}
/** Downloads a compiler release with a progress notification; returns the executable, or `undefined` after reporting a failure. */
async function download(context, settings, version) {
    try {
        return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Downloading nanyx ${version}`, cancellable: true }, (progress, token) => (0, compiler_1.downloadCompiler)(context, settings, version, progress, token));
    }
    catch (error) {
        log(`Download failed: ${messageOf(error)}`);
        void vscode.window.showErrorMessage(`Nanyx: ${messageOf(error)}`);
        return undefined;
    }
}
async function restartClient(context, executable) {
    await stopClient();
    compiler = executable;
    // Each switch is passed only when it is on, so the defaults still work with compilers that predate it.
    const config = vscode.workspace.getConfiguration("nanyx");
    const args = ["lsp"];
    // A `nanyx.greekTypeVariables: false` from before `nanyx.asciiTypes` replaced it still counts.
    if (config.get("asciiTypes", false) || config.get("greekTypeVariables") === false) {
        args.push("--ascii");
    }
    const topLevel = config.get("topLevelTypeHints", "codeLens");
    if (topLevel === "inlayHint") {
        args.push("--top-level-type-hints", "inlay");
    }
    else if (topLevel === "off") {
        args.push("--top-level-type-hints", "off");
    }
    if (!config.get("inlayHints.localDefinitions", true)) {
        args.push("--no-local-type-hints");
    }
    if (!config.get("inlayHints.parameters", true)) {
        args.push("--no-parameter-type-hints");
    }
    if (!config.get("inlayHints.patternVariables", true)) {
        args.push("--no-pattern-variable-type-hints");
    }
    // No `transport`: stdio is the default, and naming it makes the client append `--stdio` to the args.
    const serverOptions = {
        run: { command: executable, args },
        debug: { command: executable, args }
    };
    const clientOptions = {
        documentSelector: [{ language: "nanyx" }],
        outputChannel
    };
    log(`Starting the Nanyx language server: ${executable} ${args.join(" ")}`);
    client = new node_1.LanguageClient("nanyx", "Nanyx Language Server", serverOptions, clientOptions);
    try {
        await client.start();
    }
    catch (error) {
        void vscode.window.showErrorMessage(`Nanyx: the language server failed to start (${messageOf(error)}). See the Nanyx output channel.`);
        client = undefined;
    }
}
async function stopClient() {
    if (client) {
        const stopping = client;
        client = undefined;
        await stopping.stop().catch(() => undefined);
        await stopping.dispose();
    }
}
function log(line) {
    outputChannel.appendLine(line);
}
function messageOf(error) {
    return error instanceof Error ? error.message : String(error);
}
//# sourceMappingURL=extension.js.map