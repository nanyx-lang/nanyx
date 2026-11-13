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
const contextLines_1 = require("./contextLines");
/** The language server's request for what the compiler knows at a position (see `InfoViewHandler` in the compiler). */
const INFO_VIEW_REQUEST = "nanyx/infoView";
/** How long the selection must stay put before the view asks the server about it. */
const DEBOUNCE_MILLISECONDS = 150;
/**
 * The info view: a view in the secondary side bar that shows what the compiler knows at the cursor, as Lean's infoview
 * does. It is the webview view `nanyx.infoView`, in the `nanyx` view container that `package.json` contributes to the
 * secondary side bar. It asks the language server (`nanyx/infoView`) when the selection in a Nanyx editor changes, or
 * the document does, once it has stayed put for a moment, and again when the server has compiled the document. Every item that has a place links to it, and shows the doc
 * comment of what it names under it, cut to its first paragraph until clicked. Pinning keeps the view on one position
 * while you edit elsewhere. While it is visible and not pinned, it highlights in the editor what the server says to: on
 * a `use` or a context of a signature, the calls it serves; on a call, what provides its contexts.
 */
class InfoView {
    constructor(client) {
        this.client = client;
        /** Counts requests, so that an answer to one overtaken by a later one is dropped. */
        this.requests = 0;
        this.listeners = [];
        /** How a provider of contexts, and a call it serves, are highlighted in the editor. */
        this.providerHighlight = vscode.window.createTextEditorDecorationType({
            backgroundColor: new vscode.ThemeColor("editor.wordHighlightStrongBackground")
        });
        this.callHighlight = vscode.window.createTextEditorDecorationType({
            backgroundColor: new vscode.ThemeColor("editor.wordHighlightBackground")
        });
        /** The lines that join a provider to its calls, as `nanyx.infoView.contextLines` says. */
        this.contextLines = new contextLines_1.ContextLines();
        this.registration = vscode.window.registerWebviewViewProvider(InfoView.viewId, this, {
            webviewOptions: { retainContextWhenHidden: true }
        });
    }
    /** Called by VS Code the first time the view is shown, and again if it is shown after being closed. */
    resolveWebviewView(view) {
        this.closed();
        this.view = view;
        view.webview.options = { enableScripts: true };
        view.webview.html = page(view.webview);
        view.webview.onDidReceiveMessage((message) => this.receive(message), undefined, this.listeners);
        view.onDidDispose(() => this.closed(), undefined, this.listeners);
        view.onDidChangeVisibility(() => this.schedule(), undefined, this.listeners);
        vscode.window.onDidChangeTextEditorSelection(() => this.schedule(), undefined, this.listeners);
        vscode.window.onDidChangeActiveTextEditor(() => this.schedule(), undefined, this.listeners);
        vscode.workspace.onDidChangeTextDocument((event) => {
            if (event.document.languageId === "nanyx") {
                this.schedule();
            }
        }, undefined, this.listeners);
        // The server publishes a document's diagnostics when it has compiled the document, which after an edit is well after
        // the request the edit scheduled: that one was answered from the compile before. Ask again then.
        vscode.languages.onDidChangeDiagnostics((event) => {
            const shown = (this.pinned ?? this.cursor())?.uri.toString();
            if (shown !== undefined && event.uris.some((uri) => uri.toString() === shown)) {
                this.schedule();
            }
        }, undefined, this.listeners);
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration("nanyx.infoView.contextLines")) {
                this.schedule();
            }
        }, undefined, this.listeners);
        this.schedule();
    }
    /** Reveals the view, opening the secondary side bar if need be, and focuses it. */
    async show() {
        await vscode.commands.executeCommand(`${InfoView.viewId}.focus`);
        this.schedule();
    }
    dispose() {
        this.closed();
        this.registration.dispose();
        this.providerHighlight.dispose();
        this.callHighlight.dispose();
        this.contextLines.dispose();
    }
    /** Asks again, after the language server has restarted say. */
    refresh() {
        this.schedule();
    }
    closed() {
        this.view = undefined;
        this.pinned = undefined;
        clearTimeout(this.timer);
        this.listeners.splice(0).forEach((listener) => listener.dispose());
        this.highlight(undefined, []);
    }
    /**
     * Highlights `highlights` in each editor that shows `uri`, and nothing anywhere else: with no `uri`, nothing at all.
     * The highlights are joined by lines as `nanyx.infoView.contextLines` says.
     */
    highlight(uri, highlights) {
        const ranges = (kind) => highlights
            .filter((highlight) => highlight.kind === kind)
            .map(({ range: { start, end } }) => new vscode.Range(start.line, start.character, end.line, end.character));
        const starts = highlights.map(({ range: { start } }) => new vscode.Position(start.line, start.character));
        const mode = (0, contextLines_1.contextLinesMode)();
        for (const editor of vscode.window.visibleTextEditors) {
            const here = uri !== undefined && editor.document.uri.toString() === uri.toString();
            editor.setDecorations(this.providerHighlight, here ? ranges("provider") : []);
            editor.setDecorations(this.callHighlight, here ? ranges("call") : []);
            this.contextLines.draw(editor, here ? starts : [], mode);
        }
    }
    receive(message) {
        if (message.type === "pin") {
            this.pinned = this.pinned ? undefined : this.cursor();
            this.schedule();
        }
        else if (message.type === "open" && message.location) {
            void open(message.location);
        }
        else if (message.type === "openLink" && message.href) {
            void openLink(message.href);
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
        if (!this.view?.visible || !target) {
            // Away from a Nanyx editor, the view keeps what it last showed. A hidden view catches up when it is shown, and
            // highlights nothing meanwhile.
            if (!this.view?.visible) {
                this.highlight(undefined, []);
            }
            return;
        }
        const request = ++this.requests;
        const where = `${vscode.workspace.asRelativePath(target.uri)}:${target.position.line + 1}:${target.position.character + 1}`;
        const pinned = this.pinned !== undefined;
        let shown;
        let highlights = [];
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
                    ? { kind: "info", where, pinned, sections: await withRenderedDocumentation(result.sections) }
                    : { kind: "message", where, pinned, text: "The language server has not compiled this document yet." };
                // A pinned position is elsewhere than the cursor, so it highlights nothing.
                highlights = pinned ? [] : result?.highlights ?? [];
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
            void this.view?.webview.postMessage(shown);
            this.highlight(target.uri, highlights);
        }
    }
}
exports.InfoView = InfoView;
InfoView.viewId = "nanyx.infoView";
/** Opens a location an item links to, in the editor that already shows its document, or else the first one. */
async function open(location) {
    const { start, end } = location.range;
    const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(location.uri));
    const visible = vscode.window.visibleTextEditors.find((editor) => editor.document === document);
    await vscode.window.showTextDocument(document, {
        viewColumn: visible?.viewColumn ?? vscode.ViewColumn.One,
        selection: new vscode.Range(start.line, start.character, end.line, end.character)
    });
}
/**
 * Opens a link in an item's documentation. A doc comment's link to a name is the URI of its file with the line and
 * column (`file:///a.nyx#L3,5`), which opens at that place; any other link opens as VS Code opens links.
 */
async function openLink(href) {
    const uri = vscode.Uri.parse(href);
    const place = /^L(\d+),(\d+)$/.exec(uri.fragment);
    if (place) {
        const line = Number(place[1]) - 1;
        const character = Number(place[2]) - 1;
        const range = { start: { line, character }, end: { line, character } };
        await open({ uri: uri.with({ fragment: "" }).toString(), range });
    }
    else {
        await vscode.commands.executeCommand("vscode.open", uri);
    }
}
/**
 * The sections with each item's documentation rendered to HTML by VS Code's Markdown renderer (the built-in Markdown
 * extension's `markdown.api.render`). Without that renderer, the webview shows the Markdown as it is.
 */
async function withRenderedDocumentation(sections) {
    return Promise.all(sections.map(async (section) => ({
        ...section,
        items: await Promise.all(section.items.map(async (item) => {
            if (!item.documentation) {
                return item;
            }
            try {
                const html = await vscode.commands.executeCommand("markdown.api.render", item.documentation);
                return { ...item, documentationHtml: html };
            }
            catch {
                return item;
            }
        }))
    })));
}
/** The view's page: it draws what the extension posts to it, and posts back pins and clicks on links. */
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
  hr { border: none; border-top: 1px solid var(--vscode-panel-border); margin: 12px 0; }
  summary { font-weight: 600; cursor: pointer; }
  summary .count { font-weight: normal; color: var(--vscode-descriptionForeground); }
  ul { list-style: none; margin: 4px 0; padding-left: 12px; }
  ul.children { flex-basis: 100%; margin: 0; }
  li { display: flex; flex-wrap: wrap; gap: 0 8px; align-items: baseline; padding: 1px 0; }
  .doc { flex-basis: 100%; color: var(--vscode-descriptionForeground); }
  .doc p { margin: 2px 0 4px; white-space: pre-wrap; }
  .doc[title] { cursor: pointer; }
  .doc.collapsed > :not(:first-child) { display: none; }
  .doc .more { color: var(--vscode-textLink-foreground); }
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

  function item(entry, expanded) {
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
    if (entry.documentationHtml) {
      const doc = element("div", "doc");
      doc.innerHTML = entry.documentationHtml;
      doc.addEventListener("click", (event) => {
        const link = event.target.closest("a");
        if (link && link.getAttribute("href")) {
          event.preventDefault();
          event.stopPropagation();
          vscode.postMessage({ type: "openLink", href: link.getAttribute("href") });
        }
      });
      li.appendChild(collapsible(doc, expanded));
    } else if (entry.documentation) {
      const doc = element("div", "doc");
      entry.documentation.split(/\\n\\s*\\n/).forEach((paragraph) => doc.appendChild(element("p", "", paragraph)));
      li.appendChild(collapsible(doc, expanded));
    }
    if (entry.children && entry.children.length > 0) {
      const children = element("ul", "children");
      entry.children.forEach((child) => children.appendChild(item(child, expanded)));
      li.appendChild(children);
    }
    return li;
  }

  // A doc comment of more than one paragraph (or other block) shows its first until it is clicked, which shows all of
  // it; a second click cuts it back. An expanded one (that of what is at the cursor) starts showing all of it.
  function collapsible(doc, expanded) {
    if (doc.children.length < 2) return doc;
    const more = element("span", "more", " …");
    doc.firstElementChild.appendChild(more);
    const show = (collapsed) => {
      doc.classList.toggle("collapsed", collapsed);
      more.hidden = !collapsed;
      doc.title = collapsed ? "Show all of the doc comment" : "Show only its first paragraph";
    };
    show(!expanded);
    doc.addEventListener("click", () => show(!doc.classList.contains("collapsed")));
    return doc;
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
      data.items.forEach((entry) => list.appendChild(item(entry, data.id === "cursor")));
      details.appendChild(list);
    }
    return details;
  }

  // The sections, with a rule between one group and the next.
  function sections(all) {
    return all.flatMap((data, index) =>
      index > 0 && data.group !== all[index - 1].group ? [element("hr"), section(data)] : [section(data)]);
  }

  window.addEventListener("message", (event) => {
    const shown = event.data;
    where.textContent = (shown.pinned ? "Pinned: " : "") + shown.where;
    pin.textContent = shown.pinned ? "Unpin" : "Pin";
    main.replaceChildren(...(shown.kind === "info" ? sections(shown.sections) : [element("p", "message", shown.text)]));
  });
</script>
</body>
</html>`;
}
//# sourceMappingURL=infoView.js.map