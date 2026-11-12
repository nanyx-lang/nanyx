import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";

/** The language server's request for what the compiler knows at a position (see `InfoViewHandler` in the compiler). */
const INFO_VIEW_REQUEST = "nanyx/infoView";
/** How long the selection must stay put before the view asks the server about it. */
const DEBOUNCE_MILLISECONDS = 150;

interface LspPosition {
  line: number;
  character: number;
}

interface LspLocation {
  uri: string;
  range: { start: LspPosition; end: LspPosition };
}

interface InfoViewItem {
  text: string;
  detail?: string;
  location?: LspLocation;
}

interface InfoViewSection {
  id: string;
  title: string;
  collapsed?: boolean;
  items: InfoViewItem[];
}

interface InfoViewResult {
  sections: InfoViewSection[];
}

/** What the view shows, posted to its webview. */
type Shown =
  | { kind: "info"; where: string; pinned: boolean; sections: InfoViewSection[] }
  | { kind: "message"; where: string; pinned: boolean; text: string };

/** A position in a document, for the view to show. */
interface Target {
  uri: vscode.Uri;
  position: vscode.Position;
}

/**
 * The info view: a view in the secondary side bar that shows what the compiler knows at the cursor, as Lean's infoview
 * does. It is the webview view `nanyx.infoView`, in the `nanyx` view container that `package.json` contributes to the
 * secondary side bar. It asks the language server (`nanyx/infoView`) when the selection in a Nanyx editor changes, or
 * the document does, once it has stayed put for a moment. Every item that has a place links to it. Pinning keeps the
 * view on one position while you edit elsewhere.
 */
export class InfoView implements vscode.WebviewViewProvider, vscode.Disposable {
  static readonly viewId = "nanyx.infoView";

  private view: vscode.WebviewView | undefined;
  private pinned: Target | undefined;
  private timer: NodeJS.Timeout | undefined;
  /** Counts requests, so that an answer to one overtaken by a later one is dropped. */
  private requests = 0;
  private readonly listeners: vscode.Disposable[] = [];
  private readonly registration: vscode.Disposable;

  constructor(private readonly client: () => LanguageClient | undefined) {
    this.registration = vscode.window.registerWebviewViewProvider(InfoView.viewId, this, {
      webviewOptions: { retainContextWhenHidden: true }
    });
  }

  /** Called by VS Code the first time the view is shown, and again if it is shown after being closed. */
  resolveWebviewView(view: vscode.WebviewView): void {
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
    this.schedule();
  }

  /** Reveals the view, opening the secondary side bar if need be, and focuses it. */
  async show(): Promise<void> {
    await vscode.commands.executeCommand(`${InfoView.viewId}.focus`);
    this.schedule();
  }

  dispose(): void {
    this.closed();
    this.registration.dispose();
  }

  /** Asks again, after the language server has restarted say. */
  refresh(): void {
    this.schedule();
  }

  private closed(): void {
    this.view = undefined;
    this.pinned = undefined;
    clearTimeout(this.timer);
    this.listeners.splice(0).forEach((listener) => listener.dispose());
  }

  private receive(message: { type: string; location?: LspLocation }): void {
    if (message.type === "pin") {
      this.pinned = this.pinned ? undefined : this.cursor();
      this.schedule();
    } else if (message.type === "open" && message.location) {
      void open(message.location);
    }
  }

  /** The cursor in the active Nanyx editor, if there is one. */
  private cursor(): Target | undefined {
    const editor = vscode.window.activeTextEditor;
    return editor && editor.document.languageId === "nanyx"
      ? { uri: editor.document.uri, position: editor.selection.active }
      : undefined;
  }

  private schedule(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.update(), DEBOUNCE_MILLISECONDS);
  }

  private async update(): Promise<void> {
    const target = this.pinned ?? this.cursor();
    if (!this.view?.visible || !target) {
      // Away from a Nanyx editor, the view keeps what it last showed. A hidden view catches up when it is shown.
      return;
    }

    const request = ++this.requests;
    const where = `${vscode.workspace.asRelativePath(target.uri)}:${target.position.line + 1}:${target.position.character + 1}`;
    const pinned = this.pinned !== undefined;
    let shown: Shown;
    const client = this.client();
    if (!client) {
      shown = { kind: "message", where, pinned, text: "The Nanyx language server is not running." };
    } else {
      try {
        const result = await client.sendRequest<InfoViewResult | null>(INFO_VIEW_REQUEST, {
          textDocument: { uri: client.code2ProtocolConverter.asUri(target.uri) },
          position: client.code2ProtocolConverter.asPosition(target.position)
        });
        shown = result
          ? { kind: "info", where, pinned, sections: result.sections }
          : { kind: "message", where, pinned, text: "The language server has not compiled this document yet." };
      } catch (error) {
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
    }
  }
}

/** Opens a location an item links to, in the editor that already shows its document, or else the first one. */
async function open(location: LspLocation): Promise<void> {
  const { start, end } = location.range;
  const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(location.uri));
  const visible = vscode.window.visibleTextEditors.find((editor) => editor.document === document);
  await vscode.window.showTextDocument(document, {
    viewColumn: visible?.viewColumn ?? vscode.ViewColumn.One,
    selection: new vscode.Range(start.line, start.character, end.line, end.character)
  });
}

/** The view's page: it draws what the extension posts to it, and posts back pins and clicks on links. */
function page(webview: vscode.Webview): string {
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
