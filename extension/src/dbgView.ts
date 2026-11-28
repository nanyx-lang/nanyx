import * as fs from "fs";
import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";
import { addHits, DbgHit, DbgSite, firstKept, ghostText, hoverMarkdown, inOrder, oneLine } from "./dbgValues";

/** The command that lists every value of the `dbg` at the cursor. */
export const COMMAND_SHOW_DBG_VALUES = "nanyx.showDbgValues";

/** A `dbg`'s place where the language server says it is now, counted from 1 as a run names it. */
interface Place {
  line: number;
  column: number;
}

/** The values a run gave in one file: its text when they came, each `dbg`'s values, and where each `dbg` is now. */
interface FileValues {
  run: number;
  /** The file's text when the run's first value for it came, which the language server maps the places from. */
  ranText: string;
  /** Each place's values, by kind and place (see `addHits`), and the places in order. */
  byPlace: Map<string, DbgSite>;
  sites: DbgSite[];
  /** Where each of `sites` is now, `undefined` for one whose `dbg` is gone. */
  places: (Place | undefined)[];
  /** Whether the file has been edited since, when the values are shown faded. */
  faded: boolean;
}

/**
 * The values a run's `dbg`s gave, shown at the end of each `dbg`'s line in ghost text (see `ghostText`), with a hover
 * that lists them (see `hoverMarkdown`), and every value it showed, with `--show-values`, the same way. A run the
 * editor starts gives them: `nanyx run --dbg-to`, or a test run's `dbg` lists. Each file's values are cleared by the next run that reaches one of its `dbg`s, fade once the file is
 * edited, and follow their `dbg`s as lines change, by the language server's `nanyx/dbgSites`. Closing the file clears
 * them.
 */
export class DbgView implements vscode.Disposable {
  private readonly files = new Map<string, FileValues>();
  private readonly fresh = vscode.window.createTextEditorDecorationType({
    after: { color: new vscode.ThemeColor("editorCodeLens.foreground"), margin: "0 0 0 2em", fontStyle: "italic" },
    rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
  });
  private readonly faded = vscode.window.createTextEditorDecorationType({
    after: { color: new vscode.ThemeColor("disabledForeground"), margin: "0 0 0 2em", fontStyle: "italic" },
    rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
  });
  private readonly disposables: vscode.Disposable[] = [];
  private readonly remapTimers = new Map<string, NodeJS.Timeout>();
  private runs = 0;

  constructor(private readonly client: () => LanguageClient | undefined) {
    this.disposables.push(
      this.fresh,
      this.faded,
      vscode.window.onDidChangeVisibleTextEditors(() => this.renderAll()),
      vscode.workspace.onDidChangeTextDocument((event) => this.edited(event.document)),
      vscode.workspace.onDidCloseTextDocument((document) => this.clear(document.uri.fsPath)),
      vscode.commands.registerCommand(COMMAND_SHOW_DBG_VALUES, () => this.showValues())
    );
  }

  dispose(): void {
    this.remapTimers.forEach((timer) => clearTimeout(timer));
    this.disposables.forEach((disposable) => disposable.dispose());
  }

  /** A new run, whose values replace a file's earlier ones once it gives its first value there. */
  beginRun(): number {
    return ++this.runs;
  }

  /** Shows `hits`, which the run `run` gave. */
  add(run: number, hits: DbgHit[]): void {
    const byFile = new Map<string, DbgHit[]>();
    hits.forEach((hit) => byFile.set(hit.file, [...(byFile.get(hit.file) ?? []), hit]));
    for (const [file, fileHits] of byFile) {
      const known = this.files.get(file);
      const values: FileValues =
        known && known.run === run ? known : { run, ranText: this.textOf(file), byPlace: new Map(), sites: [], places: [], faded: false };
      addHits(values.byPlace, fileHits);
      values.sites = inOrder(values.byPlace);
      values.places = values.sites.map((site) => ({ line: site.line, column: site.column }));
      this.files.set(file, values);
      if (values.faded) {
        this.remapLater(file);
      }
    }
    this.renderAll();
  }

  /** The text of the file at `file`: its open document's, or else what is on disk. */
  private textOf(file: string): string {
    const open = vscode.workspace.textDocuments.find((document) => document.uri.fsPath === file);
    if (open) {
      return open.getText();
    }
    try {
      return fs.readFileSync(file, "utf8");
    } catch {
      return "";
    }
  }

  private clear(file: string): void {
    if (this.files.delete(file)) {
      this.renderAll();
    }
  }

  /** Fades a file's values once it is edited, and asks where its `dbg`s are now. */
  private edited(document: vscode.TextDocument): void {
    const values = this.files.get(document.uri.fsPath);
    if (!values) {
      return;
    }
    values.faded = true;
    this.remapLater(document.uri.fsPath);
    this.renderAll();
  }

  /** Asks the language server where a file's `dbg`s are, a moment after the last of several edits. */
  private remapLater(file: string): void {
    clearTimeout(this.remapTimers.get(file));
    this.remapTimers.set(file, setTimeout(() => void this.remap(file), 300));
  }

  private async remap(file: string): Promise<void> {
    const values = this.files.get(file);
    const client = this.client();
    if (!values || !client) {
      return;
    }
    try {
      const moved = await client.sendRequest<(Place | null)[] | null>("nanyx/dbgSites", {
        textDocument: { uri: vscode.Uri.file(file).toString() },
        before: values.ranText,
        places: values.sites.map((site) => ({ line: site.line, column: site.column }))
      });
      // None while the text doesn't parse: the places stay as they were.
      if (moved && this.files.get(file) === values) {
        values.places = moved.map((place) => place ?? undefined);
        this.renderAll();
      }
    } catch {
      // A server that doesn't answer leaves the places as they were.
    }
  }

  private renderAll(): void {
    vscode.window.visibleTextEditors.forEach((editor) => this.render(editor));
  }

  private render(editor: vscode.TextEditor): void {
    const values = this.files.get(editor.document.uri.fsPath);
    const decorations: vscode.DecorationOptions[] = [];
    values?.sites.forEach((site, index) => {
      const place = values.places[index];
      if (!place || place.line - 1 >= editor.document.lineCount) {
        return;
      }
      const line = editor.document.lineAt(place.line - 1);
      const start = Math.min(place.column - 1, line.range.end.character);
      decorations.push({
        range: new vscode.Range(line.lineNumber, start, line.lineNumber, line.range.end.character),
        hoverMessage: new vscode.MarkdownString(hoverMarkdown(site)),
        renderOptions: { after: { contentText: ghostText(site) } }
      });
    });
    editor.setDecorations(values?.faded ? this.faded : this.fresh, decorations);
    editor.setDecorations(values?.faded ? this.fresh : this.faded, []);
  }

  /** Lists every value kept of the `dbg`, or else the shown value, on the cursor's line, in order, to pick one to copy. */
  private async showValues(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    const values = editor && this.files.get(editor.document.uri.fsPath);
    const line = editor ? editor.selection.active.line + 1 : 0;
    const onLine = values ? values.sites.filter((_, index) => values.places[index]?.line === line) : [];
    const site = onLine.find((found) => found.kind === "dbg") ?? onLine[0];
    if (!site) {
      void vscode.window.showInformationMessage("No dbg or value on this line has values from a run.");
      return;
    }
    const first = firstKept(site);
    const kept = first === 1 ? "" : `, the last ${site.values.length} kept`;
    const picked = await vscode.window.showQuickPick(
      site.values.map((value, position) => ({ label: `${first + position}.`, description: oneLine(value, 200), value })),
      { title: `${site.kind === "value" ? "Values" : "dbg values"}, in the order they came (${site.count}${kept})`, matchOnDescription: true }
    );
    if (picked) {
      await vscode.env.clipboard.writeText(picked.value);
      void vscode.window.showInformationMessage("Copied the value.");
    }
  }
}
