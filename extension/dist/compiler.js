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
exports.DEFAULT_DOWNLOAD_URL = exports.MINIMUM_COMPILER_VERSION = void 0;
exports.readSettings = readSettings;
exports.tracksNewestRelease = tracksNewestRelease;
exports.locateCompiler = locateCompiler;
exports.newestDownloaded = newestDownloaded;
exports.versionToDownload = versionToDownload;
exports.newestRelease = newestRelease;
exports.compareVersions = compareVersions;
exports.runtimeIdentifier = runtimeIdentifier;
exports.downloadUrl = downloadUrl;
exports.downloadCompiler = downloadCompiler;
exports.removeDownloadedExcept = removeDownloadedExcept;
const child_process_1 = require("child_process");
const fs = __importStar(require("fs"));
const https = __importStar(require("https"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
const vscode = __importStar(require("vscode"));
/**
 * The oldest compiler release the extension works with. The extension downloads the newest compiler
 * release at or above it, so bump it only when the extension starts relying on something a compiler
 * release added (0.20.0: `nanyx lsp --ascii`).
 */
exports.MINIMUM_COMPILER_VERSION = "0.20.0";
/**
 * Where a release lives. `${version}`, `${rid}` and `${ext}` are substituted; this is what the
 * `nanyx.downloadUrl` setting overrides.
 */
exports.DEFAULT_DOWNLOAD_URL = "https://github.com/nanyx-lang/nanyx/releases/download/compiler/${version}/nanyx-${version}-${rid}.${ext}";
/** Lists the repository's releases. Compiler and extension releases share it, so its single "latest release" is no use. */
const RELEASES_API = "https://api.github.com/repos/nanyx-lang/nanyx/releases?per_page=100";
const RELEASE_TAG_PREFIX = "compiler/";
const EXECUTABLE = process.platform === "win32" ? "nanyx.exe" : "nanyx";
function readSettings() {
    const config = vscode.workspace.getConfiguration("nanyx");
    const downloadUrl = config.get("downloadUrl", "").trim();
    return {
        path: config.get("path", "").trim(),
        pinnedVersion: config.get("compilerVersion", "").trim(),
        downloadUrl: downloadUrl || exports.DEFAULT_DOWNLOAD_URL,
        customDownloadUrl: downloadUrl !== ""
    };
}
/** Whether the extension picks the compiler release itself, which is when it can look for newer ones. */
function tracksNewestRelease(settings) {
    return !settings.pinnedVersion && !settings.customDownloadUrl;
}
/**
 * Finds the `nanyx` executable to run the language server with, in this order: the `nanyx.path`
 * setting, PATH, then a copy this extension downloaded earlier (the pinned release, or else the newest
 * one at or above the minimum). Returns `undefined` when none exists.
 */
function locateCompiler(context, settings) {
    if (settings.path) {
        if (fs.existsSync(settings.path)) {
            return { executable: settings.path };
        }
        throw new Error(`nanyx.path is set to "${settings.path}", but no file exists there.`);
    }
    const onPath = findOnPath();
    if (onPath) {
        return { executable: onPath };
    }
    const downloaded = settings.pinnedVersion
        ? findDownloaded(context).find((copy) => copy.version === settings.pinnedVersion)
        : newestDownloaded(context);
    return downloaded && { executable: downloaded.executable, downloadedVersion: downloaded.version };
}
/** The newest downloaded copy at or above the minimum version. */
function newestDownloaded(context) {
    return findDownloaded(context).find((copy) => compareVersions(copy.version, exports.MINIMUM_COMPILER_VERSION) >= 0);
}
/**
 * The release to download: the pinned one; the minimum when downloading from a custom URL; otherwise
 * the newest compiler release on GitHub, falling back to the newest downloaded copy and then the
 * minimum when GitHub cannot be asked.
 */
async function versionToDownload(context, settings, log) {
    if (settings.pinnedVersion) {
        return settings.pinnedVersion;
    }
    if (settings.customDownloadUrl) {
        return exports.MINIMUM_COMPILER_VERSION;
    }
    try {
        const newest = await newestRelease();
        if (newest) {
            return newest;
        }
        log(`No compiler release at or above ${exports.MINIMUM_COMPILER_VERSION} is published on GitHub.`);
    }
    catch (error) {
        log(`Could not list the compiler releases: ${error instanceof Error ? error.message : String(error)}`);
    }
    return newestDownloaded(context)?.version ?? exports.MINIMUM_COMPILER_VERSION;
}
/**
 * The newest published (not draft or prerelease) compiler release at or above the minimum, or
 * `undefined` when there is none. Throws when GitHub cannot be reached or refuses (e.g. rate limiting).
 */
async function newestRelease() {
    const releases = JSON.parse(await fetchText(RELEASES_API));
    return releases
        .filter((release) => !release.draft && !release.prerelease && release.tag_name?.startsWith(RELEASE_TAG_PREFIX))
        .map((release) => release.tag_name.slice(RELEASE_TAG_PREFIX.length))
        .filter((version) => parseVersion(version) && compareVersions(version, exports.MINIMUM_COMPILER_VERSION) >= 0)
        .sort(compareVersions)
        .pop();
}
/** Compares `major.minor.patch` versions; anything unparseable sorts first. */
function compareVersions(a, b) {
    const left = parseVersion(a) ?? [-1, -1, -1];
    const right = parseVersion(b) ?? [-1, -1, -1];
    for (let i = 0; i < 3; i++) {
        if (left[i] !== right[i]) {
            return left[i] - right[i];
        }
    }
    return 0;
}
function parseVersion(version) {
    const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(version);
    return match ? match.slice(1).map(Number) : undefined;
}
/** The platform's runtime identifier as the compiler release names it, or `undefined` when there is no build for it. */
function runtimeIdentifier() {
    const arch = process.arch;
    switch (process.platform) {
        case "win32":
            return arch === "x64" ? "win-x64" : undefined;
        case "linux":
            return arch === "x64" ? "linux-x64" : arch === "arm64" ? "linux-arm64" : undefined;
        case "darwin":
            return arch === "x64" ? "osx-x64" : arch === "arm64" ? "osx-arm64" : undefined;
        default:
            return undefined;
    }
}
function downloadUrl(settings, version, rid) {
    const ext = rid.startsWith("win-") ? "zip" : "tar.gz";
    return settings.downloadUrl
        .replace(/\$\{version\}/g, version)
        .replace(/\$\{rid\}/g, rid)
        .replace(/\$\{ext\}/g, ext);
}
/**
 * Downloads a compiler release for this platform into the extension's global storage and returns
 * the path of the executable. Throws with a readable message when offline or when the release is missing.
 */
async function downloadCompiler(context, settings, version, progress, token) {
    const rid = runtimeIdentifier();
    if (!rid) {
        throw new Error(`There is no Nanyx compiler build for ${process.platform}/${process.arch}. ` +
            "Build it from source and point the nanyx.path setting at it.");
    }
    const url = downloadUrl(settings, version, rid);
    const targetDir = installDir(context, version, rid);
    const archive = path.join(os.tmpdir(), `nanyx-${version}-${rid}-${process.pid}.${url.endsWith(".zip") ? "zip" : "tar.gz"}`);
    progress.report({ message: `downloading ${url}` });
    await fetchToFile(url, archive, token);
    try {
        progress.report({ message: "unpacking" });
        const staging = `${targetDir}.partial`;
        fs.rmSync(staging, { recursive: true, force: true });
        fs.mkdirSync(staging, { recursive: true });
        extract(archive, staging);
        const executable = path.join(staging, EXECUTABLE);
        if (!fs.existsSync(executable)) {
            throw new Error(`The archive from ${url} does not contain ${EXECUTABLE}.`);
        }
        if (process.platform !== "win32") {
            fs.chmodSync(executable, 0o755);
        }
        fs.rmSync(targetDir, { recursive: true, force: true });
        fs.renameSync(staging, targetDir);
        return path.join(targetDir, EXECUTABLE);
    }
    finally {
        fs.rmSync(archive, { force: true });
    }
}
/** Deletes the downloaded copies other than `keep`. One still in use (e.g. by another window on Windows) is left for next time. */
function removeDownloadedExcept(context, keep) {
    for (const copy of findDownloaded(context)) {
        if (copy.version !== keep) {
            try {
                fs.rmSync(path.dirname(copy.executable), { recursive: true, force: true });
            }
            catch {
                // In use; it goes on a later update.
            }
        }
    }
}
function installDir(context, version, rid) {
    return path.join(context.globalStorageUri.fsPath, `nanyx-${version}-${rid}`);
}
/** The copies this extension downloaded for this platform, newest first. */
function findDownloaded(context) {
    const rid = runtimeIdentifier();
    if (!rid) {
        return [];
    }
    let entries;
    try {
        entries = fs.readdirSync(context.globalStorageUri.fsPath);
    }
    catch {
        return [];
    }
    const suffix = `-${rid}`;
    return entries
        .filter((entry) => entry.startsWith("nanyx-") && entry.endsWith(suffix))
        .map((entry) => ({
        version: entry.slice("nanyx-".length, -suffix.length),
        executable: path.join(context.globalStorageUri.fsPath, entry, EXECUTABLE)
    }))
        .filter((copy) => fs.existsSync(copy.executable))
        .sort((a, b) => compareVersions(b.version, a.version));
}
function findOnPath() {
    const directories = (process.env.PATH ?? "").split(path.delimiter).filter(Boolean);
    for (const directory of directories) {
        const candidate = path.join(directory, EXECUTABLE);
        if (fs.existsSync(candidate)) {
            return candidate;
        }
    }
    return undefined;
}
/** Fetches a small text response, such as a GitHub API listing. */
function fetchText(url) {
    return new Promise((resolve, reject) => {
        const request = https.get(url, { headers: { "User-Agent": "nanyx-vscode", Accept: "application/vnd.github+json" }, timeout: 10000 }, (response) => {
            const status = response.statusCode ?? 0;
            if (status !== 200) {
                response.resume();
                reject(new Error(status === 403 || status === 429 ? `GitHub is rate limiting requests (HTTP ${status}).` : `${url} returned HTTP ${status}.`));
                return;
            }
            const chunks = [];
            response.on("data", (chunk) => chunks.push(chunk));
            response.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
            response.on("error", reject);
        });
        request.on("timeout", () => request.destroy(new Error(`${new URL(url).host} did not answer in time.`)));
        request.on("error", reject);
    });
}
/** Downloads `url` to `file`, following the redirects GitHub release assets go through. */
function fetchToFile(url, file, token, redirects = 0) {
    return new Promise((resolve, reject) => {
        if (redirects > 10) {
            reject(new Error(`Too many redirects while downloading ${url}.`));
            return;
        }
        const request = https.get(url, { headers: { "User-Agent": "nanyx-vscode" } }, (response) => {
            const status = response.statusCode ?? 0;
            if (status >= 300 && status < 400 && response.headers.location) {
                response.resume();
                const next = new URL(response.headers.location, url).toString();
                fetchToFile(next, file, token, redirects + 1).then(resolve, reject);
                return;
            }
            if (status === 404) {
                response.resume();
                reject(new Error(`No compiler release was found at ${url}. Check the nanyx.compilerVersion and nanyx.downloadUrl settings, or set nanyx.path.`));
                return;
            }
            if (status !== 200) {
                response.resume();
                reject(new Error(`Downloading ${url} failed with HTTP ${status}.`));
                return;
            }
            const output = fs.createWriteStream(file);
            const cancel = token.onCancellationRequested(() => {
                request.destroy(new Error("The download was cancelled."));
            });
            response.pipe(output);
            output.on("finish", () => {
                cancel.dispose();
                output.close(() => resolve());
            });
            output.on("error", (error) => {
                cancel.dispose();
                reject(error);
            });
        });
        request.on("error", (error) => {
            const offline = error.code === "ENOTFOUND" || error.code === "EAI_AGAIN" || error.code === "ECONNREFUSED" || error.code === "ETIMEDOUT";
            reject(offline
                ? new Error(`Could not reach ${new URL(url).host} to download the Nanyx compiler (${error.code}). Connect to the internet and run "Nanyx: Download the compiler", or set nanyx.path to a local build.`)
                : error);
        });
    });
}
/**
 * Unpacks an archive with the platform's `tar`: macOS and Linux have it, and the `tar.exe` that ships
 * with Windows 10 and later also reads zip files.
 */
function extract(archive, into) {
    const result = (0, child_process_1.spawnSync)("tar", ["-xf", archive, "-C", into], { encoding: "utf8" });
    if (result.status === 0) {
        return;
    }
    if (process.platform === "win32" && archive.endsWith(".zip")) {
        const fallback = (0, child_process_1.spawnSync)("powershell", ["-NoProfile", "-NonInteractive", "-Command", `Expand-Archive -LiteralPath '${archive}' -DestinationPath '${into}' -Force`], { encoding: "utf8" });
        if (fallback.status === 0) {
            return;
        }
    }
    const detail = (result.stderr || result.error?.message || `exit code ${result.status}`).trim();
    throw new Error(`Unpacking ${path.basename(archive)} failed: ${detail}`);
}
//# sourceMappingURL=compiler.js.map