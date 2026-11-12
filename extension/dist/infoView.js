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
exports.InfoView = void 0;
const vscode = __importStar(require("vscode"));
/** The language server's request for what the compiler knows at a position (see `InfoViewHandler` in the compiler). */
const INFO_VIEW_REQUEST = "nanyx/infoView";
/** How long the selection must stay put before the panel asks the server about it. */
const DEBOUNCE_MILLISECONDS = 150;
/**
 * The info view: a panel beside the editor that shows what the compiler knows at the cursor, as Lean's infoview does.
 * It asks the language server (`nanyx/infoView`) when the selection in a Nanyx editor changes, or the document does,
 * once it has stayed put for a moment. Every item that has a place links to it. Pinning keeps the panel on one
 * position while you edit elsewhere.
 */
class InfoView {
    constructor(client) {
        this.client = client;
        /** Counts requests, so that an answer to one overtaken by a later one is dropped. */
        this.requests = 0;
        this.listeners = [];
    }
    /** Opens the panel beside the active editor, or brings it forward, and shows the cursor's position. */
    show() {
        if (this.panel) {
            this.panel.reveal(vscode.ViewColumn.Beside, true);
        }
        else {
            this.panel = vscode.window.createWebviewPanel("nanyx.infoView", "Nanyx info view", { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true }, { enableScripts: true, retainContextWhenHidden: true });
            this.panel.webview.html = page(this.panel.webview);
            this.panel.webview.onDidReceiveMessage((message) => this.receive(message), undefined, this.listeners);
            this.panel.onDidDispose(() => this.closed(), undefined, this.listeners);
            vscode.window.onDidChangeTextEditorSelection(() => this.schedule(), undefined, this.listeners);
            vscode.window.onDidChangeActiveTextEditor(() => this.schedule(), undefined, this.listeners);
            vscode.workspace.onDidChangeTextDocument((event) => {
                if (event.document.languageId === "nanyx") {
                    this.schedule();
                }
            }, undefined, this.listeners);
        }
        this.schedule();
    }
    dispose() {
        this.panel?.dispose();
        this.closed();
    }
    /** Asks again, after the language server has restarted say. */
    refresh() {
        if (this.panel) {
            this.schedule();
        }
    }
    closed() {
        this.panel = undefined;
        this.pinned = undefined;
        clearTimeout(this.timer);
        this.listeners.splice(0).forEach((listener) => listener.dispose());
    }
    receive(message) {
        if (message.type === "pin") {
            this.pinned = this.pinned ? undefined : this.cursor();
            this.schedule();
        }
        else if (message.type === "open" && message.location) {
            void open(message.location);
        }
    }
    /** The cursor in the active Nanyx editor, if there is one. */
    cursor() {
        const editor = vscode.window.activeTextEditor;
        return editor && editor.document.languageId === "nanyx"
            ? { uri: editor.document.uri, position: editor.selection.active }
            : undefined;
    }
    schedule() {
        clearTimeout(this.timer);
        this.timer = setTimeout(() => void this.update(), DEBOUNCE_MILLISECONDS);
    }
    async update() {
        const target = this.pinned ?? this.cursor();
        if (!this.panel || !target) {
            // Away from a Nanyx editor, the panel keeps what it last showed.
            return;
        }
        const request = ++this.requests;
        const where = `${vscode.workspace.asRelativePath(target.uri)}:${target.position.line + 1}:${target.position.character + 1}`;
        const pinned = this.pinned !== undefined;
        let shown;
        const client = this.client();
        if (!client) {
            shown = { kind: "message", where, pinned, text: "The Nanyx language server is not running." };
        }
        else {
            try {
                const result = await client.sendRequest(INFO_VIEW_REQUEST, {
                    textDocument: { uri: client.code2ProtocolConverter.asUri(target.uri) },
                    position: client.code2ProtocolConverter.asPosition(target.position)
                });
                shown = result
                    ? { kind: "info", where, pinned, sections: result.sections }
                    : { kind: "message", where, pinned, text: "The language server has not compiled this document yet." };
            }
            catch (error) {
                shown = {
                    kind: "message",
                    where,
                    pinned,
                    text: `The language server did not answer ${INFO_VIEW_REQUEST}: ${error instanceof Error ? error.message : String(error)}. It needs a compiler that has the info view.`
                };
            }
        }
        if (request === this.requests) {
            void this.panel?.webview.postMessage(shown);
        }
    }
}
exports.InfoView = InfoView;
/** Opens a location an item links to, in the editor beside the panel. */
async function open(location) {
    const { start, end } = location.range;
    const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(location.uri));
    const visible = vscode.window.visibleTextEditors.find((editor) => editor.document === document);
    await vscode.window.showTextDocument(document, {
        viewColumn: visible?.viewColumn ?? vscode.ViewColumn.One,
        selection: new vscode.Range(start.line, start.character, end.line, end.character)
    });
}
/** The panel's page: it draws what the extension posts to it, and posts back pins and clicks on links. */
function page(webview) {
    const nonce = Array.from({ length: 32 }, () => Math.floor(Math.random() * 36).toString(36)).join("");
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Nanyx info view</title>
<style>
  body { font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); color: var(--vscode-foreground); padding: 0 12px; }
  header { display: flex; align-items: center; gap: 8px; padding: 8px 0; border-bottom: 1px solid var(--vscode-panel-border); }
  header .where { flex: 1; color: var(--vscode-descriptionForeground); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  button { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); border: none; padding: 2px 8px; cursor: pointer; }
  button:hover { background: var(--vscode-button-secondaryHoverBackground); }
  details { margin: 8px 0; }
  summary { font-weight: 600; cursor: pointer; }
  summary .count { font-weight: normal; color: var(--vscode-descriptionForeground); }
  ul { list-style: none; margin: 4px 0; padding-left: 12px; }
  li { display: flex; gap: 8px; align-items: baseline; padding: 1px 0; }
  code { font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size); white-space: pre-wrap; word-break: break-word; }
  a { color: var(--vscode-textLink-foreground); text-decoration: none; cursor: pointer; }
  a:hover { text-decoration: underline; }
  .detail { color: var(--vscode-descriptionForeground); font-size: 0.9em; white-space: nowrap; }
  .empty, .message { color: var(--vscode-descriptionForeground); }
  li.error code { color: var(--vscode-errorForeground); }
</style>
</head>
<body>
<header><span class="where" id="where"></span><button id="pin" title="Keep showing this position while the cursor moves"></button></header>
<main id="main"><p class="message">Move the cursor in a Nanyx file.</p></main>
<script nonce="${nonce}">
  const vscode = acquireVsCodeApi();
  const main = document.getElementById("main");
  const where = document.getElementById("where");
  const pin = document.getElementById("pin");
  // Which sections are open, by id, so that a section stays as it was left from one position to the next.
  const open = {};
  pin.textContent = "Pin";
  pin.addEventListener("click", () => vscode.postMessage({ type: "pin" }));

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function item(entry) {
    const li = element("li", entry.detail && entry.detail.startsWith("error") ? "error" : "");
    const code = element("code", "", entry.text);
    if (entry.location) {
      const link = element("a");
      link.title = "Go to " + entry.location.uri + ":" + (entry.location.range.start.line + 1);
      link.addEventListener("click", () => vscode.postMessage({ type: "open", location: entry.location }));
      link.appendChild(code);
      li.appendChild(link);
    } else {
      li.appendChild(code);
    }
    if (entry.detail) li.appendChild(element("span", "detail", entry.detail));
    return li;
  }

  function section(data) {
    const details = element("details");
    details.open = open[data.id] ?? !data.collapsed;
    details.addEventListener("toggle", () => { open[data.id] = details.open; });
    const summary = element("summary", "", data.title + " ");
    summary.appendChild(element("span", "count", "(" + data.items.length + ")"));
    details.appendChild(summary);
    if (data.items.length === 0) {
      details.appendChild(element("p", "empty", "Nothing here."));
    } else {
      const list = element("ul");
      data.items.forEach((entry) => list.appendChild(item(entry)));
      details.appendChild(list);
    }
    return details;
  }

  window.addEventListener("message", (event) => {
    const shown = event.data;
    where.textContent = (shown.pinned ? "Pinned: " : "") + shown.where;
    pin.textContent = shown.pinned ? "Unpin" : "Pin";
    main.replaceChildren(...(shown.kind === "info" ? shown.sections.map(section) : [element("p", "message", shown.text)]));
  });
</script>
</body>
</html>`;
}
//# sourceMappingURL=infoView.js.map