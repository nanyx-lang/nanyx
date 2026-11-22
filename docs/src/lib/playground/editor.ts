// The playground's editor: CodeMirror 6, with a small Nanyx highlighter of its own whose keywords are the TextMate
// grammar's (`extension/syntaxes/nanyx.tmLanguage.json`, which the page reads at build time), coloured as the docs'
// code blocks are, in the site's light and dark themes.

import { EditorState } from "@codemirror/state";
import { EditorView, drawSelection, highlightActiveLine, highlightActiveLineGutter, keymap, lineNumbers } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { HighlightStyle, StreamLanguage, bracketMatching, indentUnit, syntaxHighlighting } from "@codemirror/language";
import { tags } from "@lezer/highlight";

/** The grammar's keywords, by the kind it colours them as. */
export interface Keywords {
  declaration: string[];
  control: string[];
  logical: string[];
}

interface State {
  inString: boolean;
}

/** Reads a string's text up to its closing quote, or to the end of the line when it goes on. */
function readString(stream: { next(): string | void }, state: State) {
  let escaped = false;
  let ch: string | void;
  while ((ch = stream.next()) != null) {
    if (ch === '"' && !escaped) {
      state.inString = false;
      break;
    }
    escaped = !escaped && ch === "\\";
  }
  return "string";
}

function nanyx(keywords: Keywords) {
  const declaration = new Set(keywords.declaration);
  const control = new Set(keywords.control);
  const logical = new Set(keywords.logical);
  return StreamLanguage.define<State>({
    name: "nanyx",
    startState: () => ({ inString: false }),
    token(stream, state) {
      if (state.inString) return readString(stream, state);
      if (stream.eatSpace()) return null;
      if (stream.match("--")) {
        stream.skipToEnd();
        return "comment";
      }
      if (stream.sol() && stream.match(/^::\w+/)) {
        stream.skipToEnd();
        return "meta";
      }
      if (stream.eat('"')) {
        state.inString = true;
        return readString(stream, state);
      }
      if (stream.match(/^0x[0-9a-fA-F_]+/) || stream.match(/^\d[\d_]*(\.\d[\d_]*)?([eE][+-]?\d+)?/)) return "number";
      if (stream.match(/^@\w+/)) return "attributeName";
      if (stream.match(/^#\w+/)) return "atom";
      const word = stream.match(/^[A-Za-z_][\w']*/) as RegExpMatchArray | null;
      if (word) {
        const name = word[0];
        if (declaration.has(name)) return "definitionKeyword";
        if (control.has(name)) return "controlKeyword";
        if (logical.has(name)) return "operatorKeyword";
        if (name === "true" || name === "false") return "bool";
        if (/^[A-Z]/.test(name)) return "typeName";
        return null;
      }
      if (stream.match(/^(\|>|\\)/)) return "controlKeyword";
      stream.next();
      return null;
    },
    languageData: { commentTokens: { line: "--" } },
  });
}

/** Colours by class, so that the stylesheet gives each its light and dark colour. */
const highlighting = HighlightStyle.define([
  { tag: tags.comment, class: "nyx-comment" },
  { tag: tags.string, class: "nyx-string" },
  { tag: [tags.number, tags.bool], class: "nyx-number" },
  { tag: tags.controlKeyword, class: "nyx-control" },
  { tag: [tags.definitionKeyword, tags.operatorKeyword, tags.attributeName], class: "nyx-declaration" },
  { tag: tags.typeName, class: "nyx-type" },
  { tag: tags.atom, class: "nyx-tag" },
  { tag: tags.meta, class: "nyx-directive" },
]);

const theme = EditorView.theme({
  "&": { height: "100%", backgroundColor: "transparent", color: "hsl(var(--code-text))" },
  ".cm-scroller": { fontFamily: '"Hasklig", "JetBrains Mono", monospace', fontSize: "0.875rem", lineHeight: "1.6" },
  ".cm-content": { caretColor: "hsl(var(--foreground))" },
  ".cm-gutters": { backgroundColor: "transparent", color: "hsl(var(--muted-foreground))", border: "none" },
  ".cm-activeLine, .cm-activeLineGutter": { backgroundColor: "hsl(var(--muted) / 0.5)" },
  "&.cm-focused": { outline: "none" },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground": { backgroundColor: "hsl(var(--primary) / 0.25) !important" },
  ".cm-cursor": { borderLeftColor: "hsl(var(--foreground))" },
});

/**
 * An editor in `parent` holding `code`. `onRun` is called on Ctrl+Enter (⌘+Enter on a Mac), and `onChange` with the
 * code after each change.
 */
export function createEditor(
  parent: HTMLElement,
  code: string,
  keywords: Keywords,
  { onRun, onChange }: { onRun: () => void; onChange: (code: string) => void },
) {
  return new EditorView({
    parent,
    state: EditorState.create({
      doc: code,
      extensions: [
        lineNumbers(),
        highlightActiveLineGutter(),
        highlightActiveLine(),
        drawSelection(),
        history(),
        bracketMatching(),
        indentUnit.of("  "),
        EditorState.tabSize.of(2),
        keymap.of([
          {
            key: "Mod-Enter",
            run: () => {
              onRun();
              return true;
            },
          },
          indentWithTab,
          ...defaultKeymap,
          ...historyKeymap,
        ]),
        nanyx(keywords),
        syntaxHighlighting(highlighting),
        theme,
        EditorView.contentAttributes.of({ "aria-label": "Code" }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) onChange(update.state.doc.toString());
        }),
      ],
    }),
  });
}

/** Replaces the editor's code with `code`, as one change that Undo takes back. */
export function setCode(view: EditorView, code: string) {
  view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: code }, selection: { anchor: 0 } });
}
