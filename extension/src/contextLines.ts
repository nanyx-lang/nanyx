import * as vscode from "vscode";

/** How the editor shows the info view's highlights, from the `nanyx.infoView.contextLines` setting. */
export type ContextLinesMode = "highlight-only" | "stubs" | "full";

/** How long a stub's tick is, in characters. */
const STUB_LENGTH = 1;

export function contextLinesMode(): ContextLinesMode {
  return vscode.workspace.getConfiguration("nanyx").get<ContextLinesMode>("infoView.contextLines", "full");
}

/**
 * Lines in the editor that join a provider of contexts to the calls it serves, as a bracket in the left margin: a line
 * down from the first highlighted line to the last, and from it a line across to each highlight. With `stubs`, the
 * line across is a short tick at the margin; with `full`, it reaches the highlight.
 *
 * VS Code has no API to draw in the editor, so each line's piece is the `before` or `after` of an empty decoration at
 * its start, placed absolutely by CSS that rides in `textDecoration`, as extensions that draw indent guides do. The
 * pieces are measured in `ch`, which is exact in the editor's fixed-width font.
 */
export class ContextLines implements vscode.Disposable {
  /** The line down, one piece on each line it crosses. */
  private readonly down = vscode.window.createTextEditorDecorationType({
    rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
  });
  /** A line across to a highlight. */
  private readonly across = vscode.window.createTextEditorDecorationType({
    rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
  });

  dispose(): void {
    this.down.dispose();
    this.across.dispose();
  }

  /** Draws in `editor` the lines to each of `targets`, the starts of its highlights, or clears them. */
  draw(editor: vscode.TextEditor, targets: vscode.Position[], mode: ContextLinesMode): void {
    const geometry = mode === "highlight-only" ? undefined : layout(editor, targets);
    if (!geometry) {
      editor.setDecorations(this.down, []);
      editor.setDecorations(this.across, []);
      return;
    }

    const { margin, branches, first, last } = geometry;
    const down: vscode.DecorationOptions[] = [];
    for (let line = first; line <= last; line++) {
      // The line down starts and ends at the height of the first and last lines across.
      const top = line === first ? branches.get(line)!.height : 0;
      const bottom = line === last ? branches.get(line)!.height : 100;
      if (bottom > top) {
        down.push(piece(line, "before", `left: ${margin}ch; top: ${top}%; height: ${bottom - top}%; border-left-width: 1px;`));
      }
    }
    const across = [...branches].map(([line, { column, height }]) => {
      const width = mode === "stubs" ? STUB_LENGTH : Math.max(column - margin, STUB_LENGTH);
      return piece(line, "after", `left: ${margin}ch; top: 0; height: ${height}%; width: ${width}ch; border-bottom-width: 1px;`);
    });
    editor.setDecorations(this.down, down);
    editor.setDecorations(this.across, across);
  }
}

/** Where a line across meets its line: the highlight's column, and its height there as a percentage of the line. */
interface Branch {
  column: number;
  height: number;
}

/**
 * Where to draw the lines to `targets` in `editor`: the margin's column, left of every line the line down crosses, and
 * the line across on each line that has a highlight, to the first highlight on it. Nothing for fewer than two lines.
 */
function layout(
  editor: vscode.TextEditor,
  targets: vscode.Position[]
): { margin: number; branches: Map<number, Branch>; first: number; last: number } | undefined {
  const tabSize = typeof editor.options.tabSize === "number" ? editor.options.tabSize : 4;
  const branches = new Map<number, Branch>();
  for (const target of [...targets].sort((a, b) => a.compareTo(b))) {
    if (branches.has(target.line)) {
      continue;
    }
    const text = editor.document.lineAt(target.line).text;
    // A highlight with only spaces before it is met halfway up its line; one after other text is underlined, so the
    // line across does not strike through that text.
    const height = text.slice(0, target.character).trim() === "" ? 50 : 100;
    branches.set(target.line, { column: visualColumn(text, target.character, tabSize), height });
  }
  if (branches.size < 2) {
    return undefined;
  }

  const lines = [...branches.keys()];
  const first = Math.min(...lines);
  const last = Math.max(...lines);
  let indent = Infinity;
  for (let line = first; line <= last; line++) {
    const text = editor.document.lineAt(line);
    // A first line met at its foot has the line down below its text, not beside it.
    if (!text.isEmptyOrWhitespace && !(line === first && branches.get(line)!.height === 100)) {
      indent = Math.min(indent, visualColumn(text.text, text.firstNonWhitespaceCharacterIndex, tabSize));
    }
  }
  // Halfway across the first column, when every line the line down crosses leaves it empty.
  const margin = indent !== Infinity && indent > 0 ? 0.5 : 0;
  return { margin, branches, first, last };
}

/** The column at which `character` is drawn in `text`, with each tab taken to the next tab stop. */
function visualColumn(text: string, character: number, tabSize: number): number {
  let column = 0;
  for (let i = 0; i < character && i < text.length; i++) {
    column = text[i] === "\t" ? column + tabSize - (column % tabSize) : column + 1;
  }
  return column;
}

/** One piece of line, drawn with the `nanyx.contextLine` colour as the border of an empty decoration at a line's start. */
function piece(line: number, where: "before" | "after", css: string): vscode.DecorationOptions {
  return {
    range: new vscode.Range(line, 0, line, 0),
    renderOptions: {
      [where]: {
        contentText: "",
        borderColor: new vscode.ThemeColor("nanyx.contextLine"),
        textDecoration: `none; position: absolute; box-sizing: border-box; border-style: solid; border-width: 0; pointer-events: none; ${css}`
      }
    }
  };
}
