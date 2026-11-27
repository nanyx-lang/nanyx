/**
 * Moving a position with a document's edits, as a decoration moves: the info view's pinned position follows the text it
 * was on, so that an edit above it doesn't leave the view showing another place (#2780). It works on plain positions,
 * so that it can be tested without VS Code.
 */

/** A position in a document: a zero-based line, and a zero-based character in it. */
export interface Position {
  line: number;
  character: number;
}

/** An edit: the text between `start` and `end`, before the edit, replaced by `text`, as VS Code reports a change. */
export interface Change {
  start: Position;
  end: Position;
  text: string;
}

/** Whether `a` comes before `b`. */
function before(a: Position, b: Position): boolean {
  return a.line < b.line || (a.line === b.line && a.character < b.character);
}

/**
 * Where `position` is after `change`: unchanged if the edit is after it, moved by the lines and characters the edit adds
 * or removes if the edit is before it, and `undefined` if the edit replaces the text it is on. Text inserted at the
 * position itself goes before it, so the position stays on the text it was on.
 */
export function moved(position: Position, change: Change): Position | undefined {
  if (before(position, change.start)) {
    return position;
  }
  // The edit starts at or before the position: if it replaces text that reaches past it, the text it was on is gone.
  if (before(change.start, change.end) && before(position, change.end)) {
    return undefined;
  }
  const lines = change.text.split(/\r\n|\r|\n/);
  const addedLines = lines.length - 1;
  const endLine = change.start.line + addedLines;
  const endCharacter = addedLines === 0 ? change.start.character + lines[0].length : lines[addedLines].length;
  if (position.line === change.end.line) {
    return { line: endLine, character: endCharacter + (position.character - change.end.character) };
  }
  return { line: position.line + endLine - change.end.line, character: position.character };
}

/**
 * Where `position` is after `changes`, applied in the order given, as VS Code gives a document's changes; `undefined`
 * once one of them replaces the text it is on.
 */
export function movedThrough(position: Position, changes: readonly Change[]): Position | undefined {
  let current: Position | undefined = position;
  for (const change of changes) {
    if (current === undefined) {
      return undefined;
    }
    current = moved(current, change);
  }
  return current;
}
