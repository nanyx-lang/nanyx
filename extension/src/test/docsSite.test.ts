import { test } from "node:test";
import * as assert from "node:assert/strict";
import { isDocsLink } from "../docsSite";

test("an error code's entry in the errors reference is a docs link", () => {
  assert.equal(isDocsLink("https://nanyx.dev/docs/standard-tools/errors#nyx0201"), true);
});

test("another site is not, even one under nanyx.dev", () => {
  assert.equal(isDocsLink("https://github.com/nanyx-lang/nanyx"), false);
  assert.equal(isDocsLink("https://packages.nanyx.dev/"), false);
  assert.equal(isDocsLink("https://nanyx.dev.example.com/"), false);
});

test("a link to a place in a file is not", () => {
  assert.equal(isDocsLink("file:///c%3A/a.nyx#L3,5"), false);
});
