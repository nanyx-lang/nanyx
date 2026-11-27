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
exports.DbgView = exports.COMMAND_SHOW_DBG_VALUES = void 0;
const fs = __importStar(require("fs"));
const vscode = __importStar(require("vscode"));
const dbgValues_1 = require("./dbgValues");
/** The command that lists every value of the `dbg` at the cursor. */
exports.COMMAND_SHOW_DBG_VALUES = "nanyx.showDbgValues";
/**
 * The values a run's `dbg`s gave, shown at the end of each `dbg`'s line in ghost text (see `ghostText`), with a hover
 * that lists them (see `hoverMarkdown`). A run the editor starts gives them: `nanyx run --dbg-to`, or a test run's
 * `dbg` lists. Each file's values are cleared by the next run that reaches one of its `dbg`s, fade once the file is
 * edited, and follow their `dbg`s as lines change, by the language server's `nanyx/dbgSites`. Closing the file clears
 * them.
 */
class DbgView {
    constructor(client) {
        this.client = client;
        this.files = new Map();
        this.fresh = vscode.window.createTextEditorDecorationType({
            after: { color: new vscode.ThemeColor("editorCodeLens.foreground"), margin: "0 0 0 2em", fontStyle: "italic" },
            rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
        });
        this.faded = vscode.window.createTextEditorDecorationType({
            after: { color: new vscode.ThemeColor("disabledForeground"), margin: "0 0 0 2em", fontStyle: "italic" },
            rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
        });
        this.disposables = [];
        this.remapTimers = new Map();
        this.runs = 0;
        this.disposables.push(this.fresh, this.faded, vscode.window.onDidChangeVisibleTextEditors(() => this.renderAll()), vscode.workspace.onDidChangeTextDocument((event) => this.edited(event.document)), vscode.workspace.onDidCloseTextDocument((document) => this.clear(document.uri.fsPath)), vscode.commands.registerCommand(exports.COMMAND_SHOW_DBG_VALUES, () => this.showValues()));
    }
    dispose() {
        this.remapTimers.forEach((timer) => clearTimeout(timer));
        this.disposables.forEach((disposable) => disposable.dispose());
    }
    /** A new run, whose values replace a file's earlier ones once it gives its first value there. */
    beginRun() {
        return ++this.runs;
    }
    /** Shows `hits`, which the run `run` gave. */
    add(run, hits) {
        const byFile = new Map();
        hits.forEach((hit) => byFile.set(hit.file, [...(byFile.get(hit.file) ?? []), hit]));
        for (const [file, fileHits] of byFile) {
            const known = this.files.get(file);
            const values = known && known.run === run ? known : { run, ranText: this.textOf(file), hits: [], sites: [], places: [], faded: false };
            values.hits.push(...fileHits);
            values.sites = (0, dbgValues_1.sitesOf)(values.hits).get(file) ?? [];
            values.places = values.sites.map((site) => ({ line: site.line, column: site.column }));
            this.files.set(file, values);
            if (values.faded) {
                this.remapLater(file);
            }
        }
        this.renderAll();
    }
    /** The text of the file at `file`: its open document's, or else what is on disk. */
    textOf(file) {
        const open = vscode.workspace.textDocuments.find((document) => document.uri.fsPath === file);
        if (open) {
            return open.getText();
        }
        try {
            return fs.readFileSync(file, "utf8");
        }
        catch {
            return "";
        }
    }
    clear(file) {
        if (this.files.delete(file)) {
            this.renderAll();
        }
    }
    /** Fades a file's values once it is edited, and asks where its `dbg`s are now. */
    edited(document) {
        const values = this.files.get(document.uri.fsPath);
        if (!values) {
            return;
        }
        values.faded = true;
        this.remapLater(document.uri.fsPath);
        this.renderAll();
    }
    /** Asks the language server where a file's `dbg`s are, a moment after the last of several edits. */
    remapLater(file) {
        clearTimeout(this.remapTimers.get(file));
        this.remapTimers.set(file, setTimeout(() => void this.remap(file), 300));
    }
    async remap(file) {
        const values = this.files.get(file);
        const client = this.client();
        if (!values || !client) {
            return;
        }
        try {
            const moved = await client.sendRequest("nanyx/dbgSites", {
                textDocument: { uri: vscode.Uri.file(file).toString() },
                before: values.ranText,
                places: values.sites.map((site) => ({ line: site.line, column: site.column }))
            });
            // None while the text doesn't parse: the places stay as they were.
            if (moved && this.files.get(file) === values) {
                values.places = moved.map((place) => place ?? undefined);
                this.renderAll();
            }
        }
        catch {
            // A server that doesn't answer leaves the places as they were.
        }
    }
    renderAll() {
        vscode.window.visibleTextEditors.forEach((editor) => this.render(editor));
    }
    render(editor) {
        const values = this.files.get(editor.document.uri.fsPath);
        const decorations = [];
        values?.sites.forEach((site, index) => {
            const place = values.places[index];
            if (!place || place.line - 1 >= editor.document.lineCount) {
                return;
            }
            const line = editor.document.lineAt(place.line - 1);
            const start = Math.min(place.column - 1, line.range.end.character);
            decorations.push({
                range: new vscode.Range(line.lineNumber, start, line.lineNumber, line.range.end.character),
                hoverMessage: new vscode.MarkdownString((0, dbgValues_1.hoverMarkdown)(site)),
                renderOptions: { after: { contentText: (0, dbgValues_1.ghostText)(site) } }
            });
        });
        editor.setDecorations(values?.faded ? this.faded : this.fresh, decorations);
        editor.setDecorations(values?.faded ? this.fresh : this.faded, []);
    }
    /** Lists every value of the `dbg` on the cursor's line, in order, to pick one to copy. */
    async showValues() {
        const editor = vscode.window.activeTextEditor;
        const values = editor && this.files.get(editor.document.uri.fsPath);
        const line = editor ? editor.selection.active.line + 1 : 0;
        const index = values ? values.places.findIndex((place) => place?.line === line) : -1;
        if (!values || index < 0) {
            void vscode.window.showInformationMessage("No dbg on this line has values from a run.");
            return;
        }
        const site = values.sites[index];
        const picked = await vscode.window.showQuickPick(site.values.map((value, position) => ({ label: `${position + 1}.`, description: (0, dbgValues_1.oneLine)(value, 200), value })), { title: `dbg values, in the order they came (${site.values.length})`, matchOnDescription: true });
        if (picked) {
            await vscode.env.clipboard.writeText(picked.value);
            void vscode.window.showInformationMessage("Copied the value.");
        }
    }
}
exports.DbgView = DbgView;
//# sourceMappingURL=dbgView.js.map