"use strict";
/**
 * Moving a position, or a range, with a document's edits, as a decoration moves: the info view's pinned position and
 * selection follow the text they were on, so that an edit above them doesn't leave the view showing another place
 * (#2780). It works on plain positions, so that it can be tested without VS Code.
 */
Object.defineProperty(exports, "__esModule", { value: true });
exports.moved = moved;
exports.movedThrough = movedThrough;
exports.rangeMovedThrough = rangeMovedThrough;
/** Whether `a` comes before `b`. */
function before(a, b) {
    return a.line < b.line || (a.line === b.line && a.character < b.character);
}
/**
 * Where `position` is after `change`: unchanged if the edit is after it, moved by the lines and characters the edit adds
 * or removes if the edit is before it, and `undefined` if the edit replaces the text it is on. Text inserted at the
 * position itself goes before it, so the position stays on the text it was on.
 */
function moved(position, change) {
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
function movedThrough(position, changes) {
    let current = position;
    for (const change of changes) {
        if (current === undefined) {
            return undefined;
        }
        current = moved(current, change);
    }
    return current;
}
/** Whether `a` and `b` are the same position. */
function same(a, b) {
    return a.line === b.line && a.character === b.character;
}
/**
 * Where `range` is after `changes`, applied in the order given: its start moves as `moved` moves a position, and so
 * does its end, except that text inserted at the end of a range that isn't empty goes after it, outside the range.
 * `undefined` once one of them replaces the text at either end.
 */
function rangeMovedThrough(range, changes) {
    let current = range;
    for (const change of changes) {
        if (current === undefined) {
            return undefined;
        }
        const empty = same(current.start, current.end);
        const start = moved(current.start, change);
        const end = !empty && same(change.start, current.end) && same(change.end, current.end) ? current.end : moved(current.end, change);
        current = start && end && { start, end: empty ? start : end };
    }
    return current;
}
//# sourceMappingURL=pinnedPosition.js.map