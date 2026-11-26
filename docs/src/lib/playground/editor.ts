// The playground's editor, which the tour's examples edit in too: CodeMirror 6, with a small Nanyx highlighter of its
// own whose keywords are the TextMate grammar's (`extension/syntaxes/nanyx.tmLanguage.json`), coloured as the docs'
// code blocks are, in the site's light and dark themes.

import { Compartment, EditorState } from "@codemirror/state";
import { EditorView, drawSelection, highlightActiveLine, highlightActiveLineGutter, keymap, lineNumbers } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { HighlightStyle, StreamLanguage, bracketMatching, indentUnit, syntaxHighlighting } from "@codemirror/language";
import { tags } from "@lezer/highlight";
import grammar from "../../../../extension/syntaxes/nanyx.tmLanguage.json";

/** The words of the grammar's keyword pattern named `scope`, a `\b(a|b|c)\b`. */
function keywordsOf(scope: string): string[] {
  const pattern = grammar.repository.keywords.patterns.find((p: { name: string }) => p.name === scope);
  const match = pattern && /^\\b\(([\w|]+)\)\\b$/.exec(pattern.match);
  if (!match) throw new Error(`The Nanyx grammar has no keyword pattern ${scope} the editor can read.`);
  return match[1].split("|");
}

const keywords = {
  declaration: keywordsOf("keyword.declaration.nanyx"),
  control: [...keywordsOf("keyword.control.nanyx"), ...keywordsOf("keyword.control.workflow.nanyx")],
  logical: keywordsOf("keyword.operator.logical.nanyx"),
};

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

function nanyx() {
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

/** Whether an editor's code can be changed (see `setReadOnly`). */
const editable = new Compartment();

/**
 * An editor in `parent` holding `code`. `onRun` is called on Ctrl+Enter (⌘+Enter on a Mac), and `onChange` with the
 * code after each change. `numbered` gives it line numbers, as the playground has and a tour example hasn't.
 */
export function createEditor(
  parent: HTMLElement,
  code: string,
  { onRun, onChange, numbered = true }: { onRun: () => void; onChange: (code: string) => void; numbered?: boolean },
) {
  return new EditorView({
    parent,
    state: EditorState.create({
      doc: code,
      extensions: [
        ...(numbered ? [lineNumbers(), highlightActiveLineGutter(), highlightActiveLine()] : []),
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
        nanyx(),
        editable.of([]),
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

/** Makes the editor's code read-only, as an example the playground can't run is shown, or editable again. */
export function setReadOnly(view: EditorView, readOnly: boolean) {
  view.dispatch({ effects: editable.reconfigure(readOnly ? [EditorState.readOnly.of(true), EditorView.editable.of(false)] : []) });
}
