import { test } from "node:test";
import * as assert from "node:assert/strict";
import { moved, movedThrough } from "../pinnedPosition";

// The pinned position is on `total` in `  def total = a + b`, line 3, character 6.
const pinned = { line: 3, character: 6 };
const at = (line: number, character: number) => ({ line, character });

test("a line inserted above moves the pinned position down with its text", () => {
  assert.deepEqual(moved(pinned, { start: at(1, 0), end: at(1, 0), text: "def added = 1\n" }), at(4, 6));
});

test("lines deleted above move it up", () => {
  assert.deepEqual(moved(pinned, { start: at(0, 0), end: at(2, 0), text: "" }), at(1, 6));
});

test("text inserted before it on its line moves it along the line", () => {
  assert.deepEqual(moved(pinned, { start: at(3, 2), end: at(3, 2), text: "rec " }), at(3, 10));
  assert.deepEqual(moved(pinned, { start: at(3, 6), end: at(3, 6), text: "grand" }), at(3, 11));
});

test("a line broken before it carries it to the new line", () => {
  assert.deepEqual(moved(pinned, { start: at(3, 2), end: at(3, 2), text: "\n  " }), at(4, 6));
});

test("an edit after it leaves it where it is", () => {
  assert.deepEqual(moved(pinned, { start: at(3, 12), end: at(3, 13), text: "+" }), pinned);
  assert.deepEqual(moved(pinned, { start: at(9, 0), end: at(9, 0), text: "x\n" }), pinned);
});

test("an edit that replaces the text it is on unpins it", () => {
  assert.equal(moved(pinned, { start: at(3, 2), end: at(3, 11), text: "" }), undefined);
  assert.equal(moved(pinned, { start: at(3, 6), end: at(3, 11), text: "sum" }), undefined);
  assert.equal(moved(pinned, { start: at(2, 0), end: at(4, 0), text: "" }), undefined);
});

test("several changes are applied in turn, as VS Code gives them", () => {
  const changes = [
    { start: at(5, 0), end: at(5, 0), text: "-- after\n" },
    { start: at(0, 0), end: at(0, 0), text: "-- one\n-- two\n" }
  ];
  assert.deepEqual(movedThrough(pinned, changes), at(5, 6));
  assert.equal(movedThrough(pinned, [{ start: at(3, 0), end: at(4, 0), text: "" }, changes[1]]), undefined);
});
