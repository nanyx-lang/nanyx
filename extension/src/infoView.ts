import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";
import { ContextLines, contextLinesMode } from "./contextLines";
import { HighlightedCode, Highlighter } from "./highlighter";
import { movedThrough, rangeMovedThrough } from "./pinnedPosition";

/** The language server's request for what the compiler knows at a position (see `InfoViewHandler` in the compiler). */
const INFO_VIEW_REQUEST = "nanyx/infoView";
/** How long the selection must stay put before the view asks the server about it. */
const DEBOUNCE_MILLISECONDS = 150;

interface LspPosition {
  line: number;
  character: number;
}

export interface LspLocation {
  uri: string;
  range: { start: LspPosition; end: LspPosition };
}

interface InfoViewItem {
  text: string;
  detail?: string;
  location?: LspLocation;
  /** The doc comment of what the item names, as Markdown: for an import, that of the module it brings in. */
  documentation?: string;
  /** `documentation` rendered to HTML by VS Code's Markdown renderer, filled in by the extension. */
  documentationHtml?: string;
  /** `text` highlighted as code, filled in by the extension. */
  code?: HighlightedCode;
  /** The part of `text` that links to `location`: the name it starts with. An item without one has no link in its text. */
  name?: { start: number; length: number };
  /** The other parts of `text` that link: the names of types, each to its definition. */
  links?: { start: number; length: number; location: LspLocation }[];
  /** The items under it: the calls a provider in the context flow serves. */
  children?: InfoViewItem[];
}

interface InfoViewSection {
  id: string;
  title: string;
  items: InfoViewItem[];
}

/** A fix of a message: its title, and the LSP workspace edit it makes, the one its quick fix makes. */
interface InfoViewFix {
  title: string;
  edit: unknown;
}

/**
 * A message on the position's line, in full, as `nanyx run` prints it: its severity and code, the code linking to
 * `reference`, its section of the errors reference; its headline, at `location`; its notes; the other places that
 * explain it, each linking there; and its fixes.
 */
interface InfoViewMessage {
  severity: "error" | "warning" | "info" | "hint";
  code?: string;
  reference?: string;
  headline: string;
  location: LspLocation;
  notes: string[];
  related: InfoViewItem[];
  fixes: InfoViewFix[];
}

/**
 * A scope around the position, innermost first: the line's messages, if it has any, then the expression selected or at
 * the cursor, if no scope below shows it, then each lambda, each def, the module. The view draws a rule between one scope
 * and the next.
 */
interface InfoViewScope {
  kind: "messages" | "expression" | "lambda" | "function" | "module";
  /** What kind of code the heading is, where it doesn't say: `expression`, `lambda`. */
  label?: string;
  /** The scope's code (`x + 1`, `{ message -> … } (line 30)`, `for xs | x -> … (line 4)`, `def greet`), or the messages' title. */
  heading: string;
  /** Where the heading links: the expression, the lambda, the `for` loop, the def's name, the module's declaration. */
  location?: LspLocation;
  /** The module's doc comment, as Markdown. */
  documentation?: string;
  /** `documentation` rendered to HTML by VS Code's Markdown renderer, filled in by the extension. */
  documentationHtml?: string;
  /** `heading` highlighted as code, filled in by the extension for every scope but the messages. */
  headingCode?: HighlightedCode;
  collapsed?: boolean;
  sections: InfoViewSection[];
  /** The messages scope's messages. */
  messages?: InfoViewMessage[];
}

/** A range to highlight in the editor: a provider of contexts, or a call it serves. */
interface InfoViewHighlight {
  range: { start: LspPosition; end: LspPosition };
  kind: "provider" | "call";
}

interface InfoViewResult {
  scopes: InfoViewScope[];
  highlights?: InfoViewHighlight[];
}

/** What the view shows, posted to its webview. */
type Shown =
  | { kind: "info"; where: string; pinned: boolean; scopes: InfoViewScope[] }
  | { kind: "message"; where: string; pinned: boolean; text: string };

/** A position in a document, and the selection there, for the view to show. */
interface Target {
  uri: vscode.Uri;
  position: vscode.Position;
  selection: vscode.Range;
}

/**
 * The info view: a view in the secondary side bar that shows what the compiler knows at the cursor, as Lean's infoview
 * does. It is the webview view `nanyx.infoView`, in the `nanyx` view container that `package.json` contributes to the
 * secondary side bar. It asks the language server (`nanyx/infoView`) when the selection in a Nanyx editor changes, or
 * the document does, once it has stayed put for a moment, and again when the server has compiled the document. When the
 * cursor's line has messages, it shows them first, each in full, with a button for each fix. Then it shows the selected
 * expression with its type, or with nothing selected the one at the cursor, unless a scope below already shows it. Every item that has a place links to it, and shows the doc
 * comment of what it names under it, cut to its first paragraph until clicked. Pinning keeps the view on one position,
 * and the selection there, while you edit elsewhere, moving them with the edits above them and unpinning it when its
 * text is deleted. While it is visible and not pinned, it highlights in the editor what the server says to: on
 * a `use` or a context of a signature, the calls it serves; on a call, what provides its contexts. The code it shows is
 * highlighted as the editor highlights it, in the current theme's colours (see `Highlighter`). The view tells the server
 * how many characters fit across it, so that a signature too long for it is laid out as the formatter lays out a def's,
 * and asks again when it is resized.
 */
export class InfoView implements vscode.WebviewViewProvider, vscode.Disposable {
  static readonly viewId = "nanyx.infoView";

  private view: vscode.WebviewView | undefined;
  private pinned: Target | undefined;
  /** How many characters of code fit across the view, as the webview measures it: the width signatures are laid out to. */
  private columns: number | undefined;
  private timer: NodeJS.Timeout | undefined;
  /** Counts requests, so that an answer to one overtaken by a later one is dropped. */
  private requests = 0;
  private readonly listeners: vscode.Disposable[] = [];
  private readonly registration: vscode.Disposable;
  /** How a provider of contexts, and a call it serves, are highlighted in the editor. */
  private readonly providerHighlight = vscode.window.createTextEditorDecorationType({
    backgroundColor: new vscode.ThemeColor("editor.wordHighlightStrongBackground")
  });
  private readonly callHighlight = vscode.window.createTextEditorDecorationType({
    backgroundColor: new vscode.ThemeColor("editor.wordHighlightBackground")
  });
  /** The lines that join a provider to its calls, as `nanyx.infoView.contextLines` says. */
  private readonly contextLines = new ContextLines();
  private readonly highlighter: Highlighter;

  constructor(private readonly client: () => LanguageClient | undefined, extensionPath: string) {
    this.highlighter = new Highlighter(extensionPath);
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
        this.followEdits(event);
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
      if (["workbench.colorTheme", "workbench.preferredDarkColorTheme", "workbench.preferredLightColorTheme",
        "workbench.preferredHighContrastColorTheme", "workbench.preferredHighContrastLightColorTheme",
        "editor.tokenColorCustomizations"].some((setting) => event.affectsConfiguration(setting))) {
        this.highlighter.themeChanged();
        this.schedule();
      }
    }, undefined, this.listeners);
    vscode.window.onDidChangeActiveColorTheme(() => {
      this.highlighter.themeChanged();
      this.schedule();
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
    this.providerHighlight.dispose();
    this.callHighlight.dispose();
    this.contextLines.dispose();
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
    this.highlight(undefined, []);
  }

  /**
   * Highlights `highlights` in each editor that shows `uri`, and nothing anywhere else: with no `uri`, nothing at all.
   * The highlights are joined by lines as `nanyx.infoView.contextLines` says.
   */
  private highlight(uri: vscode.Uri | undefined, highlights: InfoViewHighlight[]): void {
    const ranges = (kind: InfoViewHighlight["kind"]) =>
      highlights
        .filter((highlight) => highlight.kind === kind)
        .map(({ range: { start, end } }) => new vscode.Range(start.line, start.character, end.line, end.character));
    const starts = highlights.map(({ range: { start } }) => new vscode.Position(start.line, start.character));
    const mode = contextLinesMode();
    for (const editor of vscode.window.visibleTextEditors) {
      const here = uri !== undefined && editor.document.uri.toString() === uri.toString();
      editor.setDecorations(this.providerHighlight, here ? ranges("provider") : []);
      editor.setDecorations(this.callHighlight, here ? ranges("call") : []);
      this.contextLines.draw(editor, here ? starts : [], mode);
    }
  }

  private receive(message: { type: string; location?: LspLocation; href?: string; columns?: number; edit?: unknown }): void {
    if (message.type === "width" && message.columns !== undefined) {
      if (message.columns !== this.columns) {
        this.columns = message.columns;
        this.schedule();
      }
    } else if (message.type === "pin") {
      this.pinned = this.pinned ? undefined : this.cursor();
      this.schedule();
    } else if (message.type === "open" && message.location) {
      void openLocation(message.location);
    } else if (message.type === "openLink" && message.href) {
      void openLink(message.href);
    } else if (message.type === "fix" && message.edit) {
      void this.applyFix(message.edit);
    }
  }

  /** Makes the edit of a message's fix, as its quick fix would. */
  private async applyFix(edit: unknown): Promise<void> {
    const client = this.client();
    const converted = client && await client.protocol2CodeConverter.asWorkspaceEdit(edit as Parameters<typeof client.protocol2CodeConverter.asWorkspaceEdit>[0]);
    if (converted) {
      await vscode.workspace.applyEdit(converted);
    }
  }

  /**
   * Moves the pinned position and selection with `event`'s edits to its document, so that they stay on the text they
   * were on, and unpins it if they replace the position's text (see `movedThrough`). A selection whose text they replace
   * is left empty, at the position.
   */
  private followEdits(event: vscode.TextDocumentChangeEvent): void {
    if (this.pinned === undefined || event.document.uri.toString() !== this.pinned.uri.toString()) {
      return;
    }
    const changes = event.contentChanges.map((change) => ({ start: change.range.start, end: change.range.end, text: change.text }));
    const position = movedThrough(this.pinned.position, changes);
    if (position === undefined) {
      this.pinned = undefined;
      return;
    }
    const at = new vscode.Position(position.line, position.character);
    const range = rangeMovedThrough(this.pinned.selection, changes);
    this.pinned = {
      uri: this.pinned.uri,
      position: at,
      selection: range
        ? new vscode.Range(range.start.line, range.start.character, range.end.line, range.end.character)
        : new vscode.Range(at, at)
    };
  }

  /** The cursor in the active Nanyx editor, and its selection, if there is one. */
  private cursor(): Target | undefined {
    const editor = vscode.window.activeTextEditor;
    return editor && editor.document.languageId === "nanyx"
      ? { uri: editor.document.uri, position: editor.selection.active, selection: new vscode.Range(editor.selection.start, editor.selection.end) }
      : undefined;
  }

  private schedule(): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.update(), DEBOUNCE_MILLISECONDS);
  }

  private async update(): Promise<void> {
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
    let shown: Shown;
    let highlights: InfoViewHighlight[] = [];
    const client = this.client();
    if (!client) {
      shown = { kind: "message", where, pinned, text: "The Nanyx language server is not running." };
    } else {
      try {
        const result = await client.sendRequest<InfoViewResult | null>(INFO_VIEW_REQUEST, {
          textDocument: { uri: client.code2ProtocolConverter.asUri(target.uri) },
          position: client.code2ProtocolConverter.asPosition(target.position),
          selection: client.code2ProtocolConverter.asRange(target.selection),
          width: this.columns
        });
        shown = result
          ? { kind: "info", where, pinned, scopes: await withHighlightedCode(this.highlighter, await withRenderedDocumentation(result.scopes)) }
          : { kind: "message", where, pinned, text: "The language server has not compiled this document yet." };
        // A pinned position is elsewhere than the cursor, so it highlights nothing.
        highlights = pinned ? [] : result?.highlights ?? [];
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
      this.highlight(target.uri, highlights);
    }
  }
}

/** Opens a location an item links to, in the editor that already shows its document, or else the first one. */
export async function openLocation(location: LspLocation): Promise<void> {
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
async function openLink(href: string): Promise<void> {
  const uri = vscode.Uri.parse(href);
  const place = /^L(\d+),(\d+)$/.exec(uri.fragment);
  if (place) {
    const line = Number(place[1]) - 1;
    const character = Number(place[2]) - 1;
    const range = { start: { line, character }, end: { line, character } };
    await openLocation({ uri: uri.with({ fragment: "" }).toString(), range });
  } else {
    await vscode.commands.executeCommand("vscode.open", uri);
  }
}

/**
 * The scopes with their documentation and each item's rendered to HTML by VS Code's Markdown renderer (the built-in
 * Markdown extension's `markdown.api.render`). Without that renderer, the webview shows the Markdown as it is.
 */
async function withRenderedDocumentation(scopes: InfoViewScope[]): Promise<InfoViewScope[]> {
  return Promise.all(scopes.map(async (scope) => ({
    ...scope,
    documentationHtml: await rendered(scope.documentation),
    sections: await Promise.all(scope.sections.map(async (section) => ({
      ...section,
      items: await Promise.all(section.items.map(async (item) => ({ ...item, documentationHtml: await rendered(item.documentation) })))
    })))
  })));
}

/**
 * The scopes with their code highlighted: each heading but the messages', and the text of each item, and of the items
 * under it. The messages' heading, and the messages, are prose.
 */
async function withHighlightedCode(highlighter: Highlighter, scopes: InfoViewScope[]): Promise<InfoViewScope[]> {
  const highlighted = async (item: InfoViewItem): Promise<InfoViewItem> => ({
    ...item,
    code: await highlighter.highlight(item.text),
    children: item.children && await Promise.all(item.children.map(highlighted))
  });
  return Promise.all(scopes.map(async (scope) => ({
    ...scope,
    headingCode: scope.kind === "messages" ? undefined : await highlighter.highlight(scope.heading),
    sections: await Promise.all(scope.sections.map(async (section) => ({
      ...section,
      items: await Promise.all(section.items.map(highlighted))
    })))
  })));
}

/** `markdown` rendered to HTML by VS Code's Markdown renderer, if there is any and the renderer is there. */
async function rendered(markdown: string | undefined): Promise<string | undefined> {
  if (!markdown) {
    return undefined;
  }
  try {
    return await vscode.commands.executeCommand<string>("markdown.api.render", markdown);
  } catch {
    return undefined;
  }
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
  hr { border: none; border-top: 1px solid var(--vscode-panel-border); margin: 12px 0; }
  summary { font-weight: 600; cursor: pointer; }
  summary .kind { font-weight: normal; color: var(--vscode-descriptionForeground); margin-right: 6px; }
  summary a { font-family: var(--vscode-editor-font-family); }
  details.scope > .doc { padding-left: 12px; }
  h4 { font-size: 0.9em; font-weight: 600; margin: 8px 0 0 12px; color: var(--vscode-descriptionForeground); }
  .count { font-weight: normal; color: var(--vscode-descriptionForeground); }
  ul { list-style: none; margin: 4px 0; padding-left: 12px; }
  ul.children { flex-basis: 100%; margin: 0; }
  li { display: flex; flex-wrap: wrap; gap: 0 8px; align-items: baseline; padding: 1px 0; }
  .doc { flex-basis: 100%; color: var(--vscode-descriptionForeground); }
  .doc p { margin: 2px 0 4px; white-space: pre-wrap; }
  .doc[title] { cursor: pointer; }
  .doc.collapsed > :not(:first-child) { display: none; }
  .doc .more { color: var(--vscode-textLink-foreground); }
  code { font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size); color: var(--vscode-editor-foreground); white-space: pre-wrap; word-break: break-word; background: none; padding: 0; }
  /* VS Code gives code in a webview a background; the view's code is drawn as the editor draws it, except in doc comments. */
  .doc code { background: var(--vscode-textPreformat-background); padding: 1px 3px; border-radius: 3px; }
  a { color: var(--vscode-textLink-foreground); text-decoration: none; cursor: pointer; }
  a:hover { text-decoration: underline; }
  /* A name in code that links somewhere looks like the rest of the code until it is hovered. */
  code a.name, summary a { color: inherit; }
  code a.name:hover, summary a:hover { color: var(--vscode-textLink-foreground); }
  code a.name:hover span { color: inherit !important; }
  .detail { color: var(--vscode-descriptionForeground); font-size: 0.9em; white-space: nowrap; }
  .message { color: var(--vscode-descriptionForeground); }
  code.ruler { position: absolute; visibility: hidden; white-space: pre; }
  /* A message, as the terminal prints one: its severity, code and headline, then its notes, places and fixes. */
  .diagnostic { margin: 6px 0 10px 12px; }
  .diagnostic .headline { font-weight: 600; }
  .diagnostic .severity.error { color: var(--vscode-errorForeground); }
  .diagnostic .severity.warning { color: var(--vscode-editorWarning-foreground); }
  .diagnostic .severity.info, .diagnostic .severity.hint { color: var(--vscode-editorInfo-foreground); }
  .diagnostic .note { margin: 2px 0 0 12px; white-space: pre-wrap; font-family: var(--vscode-editor-font-family); }
  .diagnostic ul { padding-left: 12px; }
  .diagnostic .fixes { display: flex; flex-wrap: wrap; gap: 4px; margin: 4px 0 0 12px; }
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
  // Which scopes are open, by kind and heading, so that a scope stays as it was left from one position to the next.
  const open = {};
  pin.textContent = "Pin";
  pin.addEventListener("click", () => vscode.postMessage({ type: "pin" }));

  // How many characters of code fit across an item, in the editor's font: the server lays out a signature that is
  // longer on several lines, as the formatter does. Measured again, and the view asked again, when the view is resized.
  const ruler = element("code", "ruler", "0".repeat(100));
  document.body.appendChild(ruler);
  let columns;
  let resized;
  function measure() {
    const character = ruler.getBoundingClientRect().width / 100;
    // An item's code starts past the list's indent.
    const fit = character > 0 ? Math.max(20, Math.floor((main.clientWidth - 16) / character)) : undefined;
    if (fit !== undefined && fit !== columns) {
      columns = fit;
      vscode.postMessage({ type: "width", columns });
    }
  }
  new ResizeObserver(() => {
    clearTimeout(resized);
    resized = setTimeout(measure, 100);
  }).observe(main);
  measure();

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  // Code, as the extension highlighted it, a span for each token in its colour and font style; or else as it is. Each
  // of \`links\` (a start and length in the text, and a link element) holds its part of the code, a token split at its
  // ends.
  function highlightedCode(text, highlighted, links) {
    const node = element("code");
    const lines = highlighted || text.split("\\n").map((line) => [{ text: line }]);
    const parts = (links || []).map((link) => ({ start: link.start, end: link.start + link.length, element: link.element }));
    let offset = 0;
    const place = (child, at) => {
      const part = parts.find((candidate) => at >= candidate.start && at < candidate.end);
      if (part) {
        if (!part.element.parentNode) node.appendChild(part.element);
        part.element.appendChild(child);
      } else {
        node.appendChild(child);
      }
    };
    lines.forEach((line, index) => {
      if (index > 0) {
        place(document.createTextNode("\\n"), offset);
        offset += 1;
      }
      line.forEach((token) => {
        // The token's pieces between the links' ends.
        const cuts = [0, token.text.length, ...parts.flatMap((part) => [part.start - offset, part.end - offset])]
          .filter((cut) => cut >= 0 && cut <= token.text.length)
          .sort((a, b) => a - b);
        for (let i = 0; i + 1 < cuts.length; i++) {
          if (cuts[i] === cuts[i + 1]) continue;
          const span = element("span", "", token.text.slice(cuts[i], cuts[i + 1]));
          if (token.color) span.style.color = token.color;
          if (token.bold) span.style.fontWeight = "bold";
          if (token.italic) span.style.fontStyle = "italic";
          const decorations = [token.underline && "underline", token.strikethrough && "line-through"].filter(Boolean);
          if (decorations.length > 0) span.style.textDecoration = decorations.join(" ");
          place(span, offset + cuts[i]);
        }
        offset += token.text.length;
      });
    });
    return node;
  }

  // A link to \`location\`, which goes there when clicked.
  function linkTo(location, className) {
    const link = element("a", className);
    link.title = "Go to " + location.uri + ":" + (location.range.start.line + 1);
    link.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      vscode.postMessage({ type: "open", location });
    });
    return link;
  }

  // An item: its code, of which the name it starts with, if any, links to where it is, and each type's name to its
  // definition; and its detail. Under it, its doc comment and the items under it.
  function item(entry) {
    const li = element("li");
    const links = (entry.links || []).map((link) => ({ start: link.start, length: link.length, element: linkTo(link.location, "name") }));
    if (entry.location && entry.name) {
      links.push({ start: entry.name.start, length: entry.name.length, element: linkTo(entry.location, "name") });
    }
    li.appendChild(highlightedCode(entry.text, entry.code, links));
    if (entry.detail) li.appendChild(element("span", "detail", entry.detail));
    const doc = documentation(entry);
    if (doc) li.appendChild(doc);
    if (entry.children && entry.children.length > 0) {
      const children = element("ul", "children");
      entry.children.forEach((child) => children.appendChild(item(child)));
      li.appendChild(children);
    }
    return li;
  }

  // A message, as the terminal prints it: its severity and code, the code linking to its entry in the errors reference,
  // and its headline, linking to where it is; then its notes, each after "=", the other places that explain it, each
  // linking there, and a button for each of its fixes, which makes the fix as its quick fix does.
  function message(data) {
    const part = element("div", "diagnostic");
    const first = element("div");
    first.appendChild(element("span", "severity " + data.severity, data.severity + " "));
    if (data.code && data.reference) {
      const code = element("a", "", data.code);
      code.title = "Open " + data.reference;
      code.addEventListener("click", (event) => {
        event.preventDefault();
        vscode.postMessage({ type: "openLink", href: data.reference });
      });
      first.appendChild(code);
      first.appendChild(document.createTextNode(": "));
    } else if (data.code) {
      first.appendChild(document.createTextNode(data.code + ": "));
    }
    const headline = linkTo(data.location, "headline");
    headline.textContent = data.headline;
    first.appendChild(headline);
    part.appendChild(first);
    data.notes.forEach((note) => part.appendChild(element("div", "note", "= " + note)));
    if (data.related.length > 0) {
      const places = element("ul");
      data.related.forEach((place) => {
        const li = element("li");
        const link = linkTo(place.location, "");
        link.textContent = place.detail;
        li.appendChild(element("span", "", "note: " + place.text));
        li.appendChild(link);
        places.appendChild(li);
      });
      part.appendChild(places);
    }
    if (data.fixes.length > 0) {
      const fixes = element("div", "fixes");
      data.fixes.forEach((fix) => {
        const button = element("button", "", fix.title);
        button.title = "Make this fix";
        button.addEventListener("click", () => vscode.postMessage({ type: "fix", edit: fix.edit }));
        fixes.appendChild(button);
      });
      part.appendChild(fixes);
    }
    return part;
  }

  // The doc comment of an item or a scope, if it has one: rendered, or else its Markdown as it is, one paragraph each.
  function documentation(data) {
    const doc = element("div", "doc");
    if (data.documentationHtml) {
      doc.innerHTML = data.documentationHtml;
      doc.addEventListener("click", (event) => {
        const link = event.target.closest("a");
        if (link && link.getAttribute("href")) {
          event.preventDefault();
          event.stopPropagation();
          vscode.postMessage({ type: "openLink", href: link.getAttribute("href") });
        }
      });
    } else if (data.documentation) {
      data.documentation.split(/\\n\\s*\\n/).forEach((paragraph) => doc.appendChild(element("p", "", paragraph)));
    } else {
      return undefined;
    }
    return collapsible(doc);
  }

  // A doc comment of more than one paragraph (or other block) shows its first until it is clicked, which shows all of
  // it; a second click cuts it back.
  function collapsible(doc) {
    if (doc.children.length < 2) return doc;
    const more = element("span", "more", " …");
    doc.firstElementChild.appendChild(more);
    const show = (collapsed) => {
      doc.classList.toggle("collapsed", collapsed);
      more.hidden = !collapsed;
      doc.title = collapsed ? "Show all of the doc comment" : "Show only its first paragraph";
    };
    show(true);
    doc.addEventListener("click", () => show(!doc.classList.contains("collapsed")));
    return doc;
  }

  // A section of a scope: its title, with how many items it has, and its items. The server leaves out empty ones.
  function section(data) {
    const part = element("section");
    const title = element("h4", "", data.title + " ");
    title.appendChild(element("span", "count", "(" + data.items.length + ")"));
    part.appendChild(title);
    const list = element("ul");
    data.items.forEach((entry) => list.appendChild(item(entry)));
    part.appendChild(list);
    return part;
  }

  // A scope: its heading, which links to it, then its doc comment and its sections. It starts closed if the server says
  // so, and stays as it was left from one position to the next.
  function scope(data) {
    const key = data.kind + " " + data.heading.replace(/ \\(line \\d+\\)$/, "");
    const details = element("details", "scope");
    details.open = open[key] ?? !data.collapsed;
    details.addEventListener("toggle", () => { open[key] = details.open; });
    const summary = element("summary");
    // A def's, a loop's and the module's headings say what they are; the server labels the others.
    if (data.label) summary.appendChild(element("span", "kind", data.label));
    if (data.location) {
      const link = element("a");
      link.appendChild(data.headingCode ? highlightedCode(data.heading, data.headingCode) : document.createTextNode(data.heading));
      link.title = "Go to " + data.location.uri + ":" + (data.location.range.start.line + 1);
      link.addEventListener("click", (event) => {
        event.preventDefault();
        vscode.postMessage({ type: "open", location: data.location });
      });
      summary.appendChild(link);
    } else {
      summary.appendChild(data.headingCode ? highlightedCode(data.heading, data.headingCode) : document.createTextNode(data.heading));
    }
    details.appendChild(summary);
    const doc = documentation(data);
    if (doc) details.appendChild(doc);
    data.sections.forEach((part) => details.appendChild(section(part)));
    (data.messages || []).forEach((entry) => details.appendChild(message(entry)));
    return details;
  }

  // The scopes, innermost first, with a rule between one and the next.
  function scopes(all) {
    return all.flatMap((data, index) => index > 0 ? [element("hr"), scope(data)] : [scope(data)]);
  }

  window.addEventListener("message", (event) => {
    const shown = event.data;
    where.textContent = (shown.pinned ? "Pinned: " : "") + shown.where;
    pin.textContent = shown.pinned ? "Unpin" : "Pin";
    main.replaceChildren(...(shown.kind === "info" ? scopes(shown.scopes) : [element("p", "message", shown.text)]));
  });
</script>
</body>
</html>`;
}
