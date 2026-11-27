import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { ghostText, HitReader, hoverMarkdown, oneLine, parseHit, sitesOf, testHits } from "../dbgValues";

const hit = (line: number, column: number, value: string, file = "/work/main.nyx") => ({ file, line, column, value });

test("a line of nanyx run --dbg-to is a hit, and any other line is none", () => {
  assert.deepEqual(parseHit('{"file":"/work/main.nyx","line":2,"column":1,"value":"12","seq":0}'), hit(2, 1, "12"));
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

test("hits are grouped by file and dbg, each dbg's values in order, the dbgs by place", () => {
  const sites = sitesOf([hit(4, 22, "2"), hit(2, 1, "12"), hit(4, 22, "4"), hit(1, 1, "x", "/work/lib.nyx")]);
  assert.deepEqual(sites.get("/work/main.nyx"), [
    { line: 2, column: 1, values: ["12"] },
    { line: 4, column: 22, values: ["2", "4"] }
  ]);
  assert.deepEqual(sites.get("/work/lib.nyx"), [{ line: 1, column: 1, values: ["x"] }]);
});

test("a test's hits name their files from its package's directory", () => {
  assert.deepEqual(testHits("/work/app", [{ file: "src/a.nyx", line: 5, column: 3, value: "1" }]), [
    { file: path.join("/work/app", "src/a.nyx"), line: 5, column: 3, value: "1" }
  ]);
});

test("a dbg shows its value, or how many times it was reached and its last value, on one line", () => {
  assert.equal(ghostText({ line: 2, column: 1, values: ["84"] }), "▸ 84");
  assert.equal(ghostText({ line: 4, column: 1, values: ["#packed", "#sent", "#shipped"] }), "▸ ×3, last #shipped");
  assert.equal(oneLine("a\nb"), "a ⏎ b");
  assert.equal(oneLine("x".repeat(100), 10), "xxxxxxxxx…");
});

test("its hover has the value whole, or each value in order, numbered, as many as it lists", () => {
  assert.equal(hoverMarkdown({ line: 1, column: 1, values: ["Point(x = 1)"] }), "```\nPoint(x = 1)\n```");
  const many = hoverMarkdown({ line: 1, column: 1, values: ["1", "2", "3"] }, 2);
  assert.match(many, /^Reached 3 times:/);
  assert.match(many, /\*\*1\.\*\*\n\n```\n1\n```/);
  assert.match(many, /\*\*2\.\*\*\n\n```\n2\n```/);
  assert.doesNotMatch(many, /\*\*3\.\*\*/);
  assert.match(many, /and 1 more/);
});
