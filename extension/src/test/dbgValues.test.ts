import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { DbgSite, ghostText, HitReader, hoverMarkdown, oneLine, parseHit, sitesOf, testHits } from "../dbgValues";

const hit = (line: number, column: number, value: string, file = "/work/main.nyx", kind: "dbg" | "value" = "dbg") => ({ file, line, column, value, kind });
const site = (line: number, values: string[], kind: "dbg" | "value" = "dbg", count = values.length): DbgSite => ({ line, column: 1, kind, values, count });

test("a line of nanyx run --dbg-to is a hit, and any other line is none", () => {
  assert.deepEqual(parseHit('{"file":"/work/main.nyx","line":2,"column":1,"value":"12","seq":0}'), hit(2, 1, "12"));
  assert.deepEqual(parseHit('{"kind":"dbg","file":"/work/main.nyx","line":2,"column":1,"value":"12","seq":0}'), hit(2, 1, "12"));
  assert.deepEqual(parseHit('{"kind":"value","file":"/work/main.nyx","line":3,"column":5,"value":"84","seq":1}'), hit(3, 5, "84", "/work/main.nyx", "value"));
  assert.equal(parseHit(""), undefined);
  assert.equal(parseHit("{not json"), undefined);
  assert.equal(parseHit('{"file":"/work/main.nyx","line":"2"}'), undefined);
});

test("the reader gives the hits written since it last read, and keeps a line cut short for the next read", () => {
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "nanyx-dbg-")), "hits.jsonl");
  const reader = new HitReader(file);
  assert.deepEqual(reader.read(), []);
  fs.writeFileSync(file, '{"file":"/a.nyx","line":1,"column":1,"value":"1","seq":0}\n{"file":"/a.nyx","li');
  assert.deepEqual(reader.read(), [hit(1, 1, "1", "/a.nyx")]);
  fs.appendFileSync(file, 'ne":2,"column":3,"value":"2","seq":1}\n');
  assert.deepEqual(reader.read(), [hit(2, 3, "2", "/a.nyx")]);
  assert.deepEqual(reader.read(), []);
  fs.rmSync(path.dirname(file), { recursive: true });
});

test("hits are grouped by file, kind and place, each place's values in order, the places in order", () => {
  const sites = sitesOf([hit(4, 22, "2"), hit(2, 1, "12"), hit(4, 22, "4"), hit(4, 22, "6", "/work/main.nyx", "value"), hit(1, 1, "x", "/work/lib.nyx")]);
  assert.deepEqual(sites.get("/work/main.nyx"), [
    { line: 2, column: 1, kind: "dbg", values: ["12"], count: 1 },
    { line: 4, column: 22, kind: "dbg", values: ["2", "4"], count: 2 },
    { line: 4, column: 22, kind: "value", values: ["6"], count: 1 }
  ]);
  assert.deepEqual(sites.get("/work/lib.nyx"), [{ line: 1, column: 1, kind: "dbg", values: ["x"], count: 1 }]);
});

test("a place keeps its latest values, as many as it keeps, and counts them all", () => {
  const sites = sitesOf([1, 2, 3, 4, 5].map((n) => hit(1, 1, `${n}`)), 3);
  assert.deepEqual(sites.get("/work/main.nyx"), [{ line: 1, column: 1, kind: "dbg", values: ["3", "4", "5"], count: 5 }]);
});

test("a test's hits name their files from its package's directory", () => {
  assert.deepEqual(testHits("/work/app", [{ file: "src/a.nyx", line: 5, column: 3, value: "1" }]), [
    { file: path.join("/work/app", "src/a.nyx"), line: 5, column: 3, value: "1", kind: "dbg" }
  ]);
});

test("a dbg shows its value, or how many times it was reached and its last value, on one line, and a shown value with =", () => {
  assert.equal(ghostText(site(2, ["84"])), "▸ 84");
  assert.equal(ghostText(site(4, ["#packed", "#sent", "#shipped"])), "▸ ×3, last #shipped");
  assert.equal(ghostText(site(2, ["84"], "value")), "= 84");
  assert.equal(ghostText(site(4, ["#sent", "#shipped"], "value", 1500)), "= ×1500, last #shipped");
  assert.equal(oneLine("a\nb"), "a ⏎ b");
  assert.equal(oneLine("x".repeat(100), 10), "xxxxxxxxx…");
});

test("its hover has the value whole, or each value in order, numbered, as many as it lists", () => {
  assert.equal(hoverMarkdown(site(1, ["Point(x = 1)"])), "```\nPoint(x = 1)\n```");
  const many = hoverMarkdown(site(1, ["1", "2", "3"]), 2);
  assert.match(many, /^Reached 3 times:/);
  assert.match(many, /\*\*1\.\*\*\n\n```\n1\n```/);
  assert.match(many, /\*\*2\.\*\*\n\n```\n2\n```/);
  assert.doesNotMatch(many, /\*\*3\.\*\*/);
  assert.match(many, /and 1 more/);
});

test("the hover of a place that gave more values than it keeps numbers them among all it gave", () => {
  const latest = hoverMarkdown(site(1, ["9", "10"], "value", 10));
  assert.match(latest, /^Reached 10 times, the last 2 kept:/);
  assert.match(latest, /\*\*9\.\*\*\n\n```\n9\n```/);
  assert.match(latest, /\*\*10\.\*\*\n\n```\n10\n```/);
});
