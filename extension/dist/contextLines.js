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
exports.ContextLines = void 0;
exports.contextLinesMode = contextLinesMode;
const vscode = __importStar(require("vscode"));
/** How long a stub's tick is, in characters. */
const STUB_LENGTH = 1;
function contextLinesMode() {
    return vscode.workspace.getConfiguration("nanyx").get("infoView.contextLines", "full");
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
class ContextLines {
    constructor() {
        /** The line down, one piece on each line it crosses. */
        this.down = vscode.window.createTextEditorDecorationType({
            rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
        });
        /** A line across to a highlight. */
        this.across = vscode.window.createTextEditorDecorationType({
            rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
        });
    }
    dispose() {
        this.down.dispose();
        this.across.dispose();
    }
    /** Draws in `editor` the lines to each of `targets`, the starts of its highlights, or clears them. */
    draw(editor, targets, mode) {
        const geometry = mode === "highlight-only" ? undefined : layout(editor, targets);
        if (!geometry) {
            editor.setDecorations(this.down, []);
            editor.setDecorations(this.across, []);
            return;
        }
        const { margin, branches, first, last } = geometry;
        const down = [];
        for (let line = first; line <= last; line++) {
            // The line down starts and ends at the height of the first and last lines across.
            const top = line === first ? branches.get(line).height : 0;
            const bottom = line === last ? branches.get(line).height : 100;
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
exports.ContextLines = ContextLines;
/**
 * Where to draw the lines to `targets` in `editor`: the margin's column, left of every line the line down crosses, and
 * the line across on each line that has a highlight, to the first highlight on it. Nothing for fewer than two lines.
 */
function layout(editor, targets) {
    const tabSize = typeof editor.options.tabSize === "number" ? editor.options.tabSize : 4;
    const branches = new Map();
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
        if (!text.isEmptyOrWhitespace && !(line === first && branches.get(line).height === 100)) {
            indent = Math.min(indent, visualColumn(text.text, text.firstNonWhitespaceCharacterIndex, tabSize));
        }
    }
    // Halfway across the first column, when every line the line down crosses leaves it empty.
    const margin = indent !== Infinity && indent > 0 ? 0.5 : 0;
    return { margin, branches, first, last };
}
/** The column at which `character` is drawn in `text`, with each tab taken to the next tab stop. */
function visualColumn(text, character, tabSize) {
    let column = 0;
    for (let i = 0; i < character && i < text.length; i++) {
        column = text[i] === "\t" ? column + tabSize - (column % tabSize) : column + 1;
    }
    return column;
}
/** One piece of line, drawn with the `nanyx.contextLine` colour as the border of an empty decoration at a line's start. */
function piece(line, where, css) {
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
//# sourceMappingURL=contextLines.js.map