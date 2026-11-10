import * as vscode from "vscode";
import { LanguageClient, LanguageClientOptions, ServerOptions } from "vscode-languageclient/node";
import {
  CompilerSettings,
  compareVersions,
  downloadCompiler,
  locateCompiler,
  newestRelease,
  readSettings,
  removeDownloadedExcept,
  tracksNewestRelease,
  versionToDownload
} from "./compiler";
import { copyBuild, removeOldCopies, watchBuild } from "./developmentMode";

const COMMAND_DOWNLOAD = "nanyx.downloadCompiler";
const COMMAND_RESTART = "nanyx.restartServer";

/** When the extension last asked GitHub for a newer compiler release (milliseconds since the epoch). */
const LAST_UPDATE_CHECK = "nanyx.lastUpdateCheck";
const UPDATE_CHECK_INTERVAL = 24 * 60 * 60 * 1000;

let client: LanguageClient | undefined;
/** In development mode, watches the `nanyx.path` build for changes. */
let buildWatcher: vscode.Disposable | undefined;
let outputChannel: vscode.OutputChannel;
let declinedDownload = false;
let declinedUpdate = false;

export async function activate(context: vscode.ExtensionContext) {
  outputChannel = vscode.window.createOutputChannel("Nanyx");
  context.subscriptions.push(outputChannel);

  context.subscriptions.push(
    vscode.commands.registerCommand(COMMAND_DOWNLOAD, async () => {
      declinedDownload = false;
      const settings = readSettings();
      const version = await versionToDownload(context, settings, log);
      const executable = await downloadAndRecordCheck(context, settings, version);
      if (executable) {
        await restartClient(context, executable);
        if (tracksNewestRelease(settings)) {
          removeDownloadedExcept(context, version);
        }
      }
    }),
    vscode.commands.registerCommand(COMMAND_RESTART, () => startOrRestart(context)),
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (
        event.affectsConfiguration("nanyx.path") ||
        event.affectsConfiguration("nanyx.developmentMode") ||
        event.affectsConfiguration("nanyx.compilerVersion") ||
        event.affectsConfiguration("nanyx.downloadUrl") ||
        event.affectsConfiguration("nanyx.asciiTypes") ||
        event.affectsConfiguration("nanyx.greekTypeVariables") ||
        event.affectsConfiguration("nanyx.topLevelTypeHints") ||
        event.affectsConfiguration("nanyx.inlayHints")
      ) {
        void startOrRestart(context);
      }
    })
  );

  await startOrRestart(context);
}

export async function deactivate() {
  buildWatcher?.dispose();
  await stopClient();
}

/**
 * Starts the language server with whatever `nanyx` can be found, downloading one if the user agrees.
 * When it is a downloaded copy, a newer compiler release is looked for afterwards, never before.
 */
async function startOrRestart(context: vscode.ExtensionContext): Promise<void> {
  buildWatcher?.dispose();
  buildWatcher = undefined;

  const settings = readSettings();
  if (vscode.workspace.getConfiguration("nanyx").get<boolean>("developmentMode", false)) {
    if (settings.path) {
      await startDevelopmentBuild(context, settings.path);
      return;
    }

    void vscode.window.showWarningMessage("Nanyx: development mode needs nanyx.path to point at your local build of the compiler.");
  }

  let located;
  try {
    located = locateCompiler(context, settings);
  } catch (error) {
    void vscode.window.showErrorMessage(`Nanyx: ${messageOf(error)}`);
    return;
  }

  const executable = located?.executable ?? (await promptAndDownload(context, settings));
  if (executable) {
    await restartClient(context, executable);
    removeOldCopies(context);
  }

  if (located?.downloadedVersion && tracksNewestRelease(settings)) {
    void checkForUpdate(context, settings, located.downloadedVersion);
  }
}

/**
 * Development mode: runs the language server from a copy of `build`, so `dotnet build` can overwrite
 * it, and restarts the server with a fresh copy whenever the build changes.
 */
async function startDevelopmentBuild(context: vscode.ExtensionContext, build: string): Promise<void> {
  let copy: string;
  try {
    copy = copyBuild(context, build);
  } catch (error) {
    void vscode.window.showErrorMessage(`Nanyx: could not copy the build at ${build} (${messageOf(error)}).`);
    return;
  }

  log(`Development mode: running a copy of ${build}, and restarting when it is rebuilt.`);
  await restartClient(context, copy);
  removeOldCopies(context, copy);

  buildWatcher = watchBuild(
    build,
    async () => {
      const fresh = copyBuild(context, build);
      log(`${build} was rebuilt; restarting the language server.`);
      await restartClient(context, fresh);
      removeOldCopies(context, fresh);
      vscode.window.setStatusBarMessage("$(sync) Nanyx: restarted with the new build", 5000);
    },
    log
  );
}

/** Offers to download the compiler once per session; returns the executable if the user agreed and it worked. */
async function promptAndDownload(context: vscode.ExtensionContext, settings: CompilerSettings): Promise<string | undefined> {
  if (declinedDownload) {
    return undefined;
  }

  const version = await versionToDownload(context, settings, log);
  const download = "Download";
  const choice = await vscode.window.showInformationMessage(
    `The Nanyx compiler was not found on PATH. Download nanyx ${version} for this platform? ` +
      "(It is kept in the extension's storage; set nanyx.path to use your own build instead.)",
    download,
    "Not now"
  );

  if (choice !== download) {
    declinedDownload = true;
    return undefined;
  }

  return downloadAndRecordCheck(context, settings, version);
}

async function downloadAndRecordCheck(context: vscode.ExtensionContext, settings: CompilerSettings, version: string): Promise<string | undefined> {
  const executable = await download(context, settings, version);
  if (executable && tracksNewestRelease(settings)) {
    // The version was just chosen from GitHub's list, so there is nothing newer to look for today.
    await context.globalState.update(LAST_UPDATE_CHECK, Date.now());
  }

  return executable;
}

/**
 * Looks for a compiler release newer than the downloaded copy in use, at most once a day, and offers
 * to download it and restart the server. Being offline or rate limited just means no offer.
 */
async function checkForUpdate(context: vscode.ExtensionContext, settings: CompilerSettings, installed: string): Promise<void> {
  if (declinedUpdate || Date.now() - context.globalState.get<number>(LAST_UPDATE_CHECK, 0) < UPDATE_CHECK_INTERVAL) {
    return;
  }

  await context.globalState.update(LAST_UPDATE_CHECK, Date.now());

  let newest: string | undefined;
  try {
    newest = await newestRelease();
  } catch (error) {
    log(`Could not check for a newer compiler release: ${messageOf(error)}`);
    return;
  }

  if (!newest || compareVersions(newest, installed) <= 0) {
    return;
  }

  const update = "Update";
  const choice = await vscode.window.showInformationMessage(
    `Nanyx compiler ${newest} is available (this is ${installed}). Download it and restart the language server?`,
    update,
    "Not now"
  );

  if (choice !== update) {
    declinedUpdate = true;
    return;
  }

  const executable = await download(context, settings, newest);
  if (executable) {
    await restartClient(context, executable);
    removeDownloadedExcept(context, newest);
  }
}

/** Downloads a compiler release with a progress notification; returns the executable, or `undefined` after reporting a failure. */
async function download(context: vscode.ExtensionContext, settings: CompilerSettings, version: string): Promise<string | undefined> {
  try {
    return await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: `Downloading nanyx ${version}`, cancellable: true },
      (progress, token) => downloadCompiler(context, settings, version, progress, token)
    );
  } catch (error) {
    log(`Download failed: ${messageOf(error)}`);
    void vscode.window.showErrorMessage(`Nanyx: ${messageOf(error)}`);
    return undefined;
  }
}

async function restartClient(context: vscode.ExtensionContext, executable: string): Promise<void> {
  await stopClient();

  // Each switch is passed only when it is on, so the defaults still work with compilers that predate it.
  const config = vscode.workspace.getConfiguration("nanyx");
  const args = ["lsp"];
  // A `nanyx.greekTypeVariables: false` from before `nanyx.asciiTypes` replaced it still counts.
  if (config.get<boolean>("asciiTypes", false) || config.get<boolean>("greekTypeVariables") === false) {
    args.push("--ascii");
  }
  const topLevel = config.get<string>("topLevelTypeHints", "codeLens");
  if (topLevel === "inlayHint") {
    args.push("--top-level-type-hints", "inlay");
  } else if (topLevel === "off") {
    args.push("--top-level-type-hints", "off");
  }
  if (!config.get<boolean>("inlayHints.localDefinitions", true)) {
    args.push("--no-local-type-hints");
  }
  if (!config.get<boolean>("inlayHints.parameters", true)) {
    args.push("--no-parameter-type-hints");
  }
  if (!config.get<boolean>("inlayHints.patternVariables", true)) {
    args.push("--no-pattern-variable-type-hints");
  }

  // No `transport`: stdio is the default, and naming it makes the client append `--stdio` to the args.
  const serverOptions: ServerOptions = {
    run: { command: executable, args },
    debug: { command: executable, args }
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ language: "nanyx" }],
    outputChannel
  };

  log(`Starting the Nanyx language server: ${executable} ${args.join(" ")}`);
  client = new LanguageClient("nanyx", "Nanyx Language Server", serverOptions, clientOptions);

  try {
    await client.start();
  } catch (error) {
    void vscode.window.showErrorMessage(`Nanyx: the language server failed to start (${messageOf(error)}). See the Nanyx output channel.`);
    client = undefined;
  }
}

async function stopClient(): Promise<void> {
  if (client) {
    const stopping = client;
    client = undefined;
    await stopping.stop().catch(() => undefined);
    await stopping.dispose();
  }
}

function log(line: string): void {
  outputChannel.appendLine(line);
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
