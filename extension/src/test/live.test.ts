import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "path";
import { endedText, heldBackBy, isOn, liveArguments, packageDirectoryOf, rowsOf, targetOf, testsOn } from "../live";

const printing = { entry: "statements" as const, contexts: ["Console"], offered: true };
const fetching = { entry: "statements" as const, contexts: ["Console", "Http", "Env"], offered: false };

test("a file's package is the nearest directory with a manifest, and a file in none has none", () => {
  const tree: Record<string, string[]> = {
    [path.join("/work", "app", "src")]: ["main.nyx"],
    [path.join("/work", "app")]: ["app.nyxp", "src"],
    ["/work"]: ["app"]
  };
  const list = (directory: string) => tree[directory] ?? [];
  assert.equal(packageDirectoryOf(path.join("/work", "app", "src", "main.nyx"), list), path.join("/work", "app"));
  assert.equal(packageDirectoryOf(path.join("/work", "script.nyx"), list), undefined);
});

test("a save re-runs the file's package, or the file as a script", () => {
  assert.deepEqual(targetOf("/work/app/src/a.nyx", "/work/app"), {
    key: "/work/app",
    target: "/work/app",
    directory: "/work/app",
    name: "app",
    kind: "package"
  });
  assert.equal(targetOf("/work/tool.nyx", undefined).kind, "script");
});

test("live mode is on where it is offered unless turned off, and on elsewhere only when turned on", () => {
  assert.equal(isOn(printing, undefined), true);
  assert.equal(isOn(printing, false), false);
  assert.equal(isOn(fetching, undefined), false);
  assert.equal(isOn(fetching, true), true);
  assert.equal(isOn({ entry: null, contexts: [], offered: false }, true), false);
  assert.equal(isOn(undefined, true), false);
});

test("a live run writes its dbgs to a file and keeps its files in memory", () => {
  assert.deepEqual(liveArguments("/work/tool.nyx", "/tmp/hits.jsonl"), ["run", "--dbg-to", "/tmp/hits.jsonl", "--files-in-memory", "/work/tool.nyx"]);
});

test("the contexts beyond Console, Async and FileSystem hold it back", () => {
  assert.deepEqual(heldBackBy(fetching), ["Http", "Env"]);
  assert.deepEqual(heldBackBy({ entry: "statements", contexts: ["Async", "FileSystem"], offered: true }), []);
});

test("the sidebar says whether it is on, what the entry point needs, that files are in memory and input empty, and what holds it back", () => {
  const target = targetOf("/work/tool.nyx", undefined);
  const offered = rowsOf(target, printing, true).map((row) => row.label);
  assert.deepEqual(offered, ["Live mode is on", "Needs", "Files are in memory", "Input is empty"]);
  const held = rowsOf(target, fetching, false);
  assert.equal(held[0].label, "Live mode is off");
  assert.deepEqual(held[held.length - 1], {
    label: "Held back",
    description: "Http, Env",
    tooltip: "It needs Http, Env, which a re-run on every save would use for real: turn live mode on for this script to run it anyway."
  });
  assert.equal(rowsOf(target, fetching, true).at(-1)?.label, "Runs for real");
  assert.equal(rowsOf(target, { entry: null, contexts: [], offered: false }, false)[0].label, "No entry point or tests");
});

test("how a run ended is said in a line", () => {
  assert.equal(endedText(0, null, 120, false, 10), "Finished in 120 ms.");
  assert.equal(endedText(1, null, 80, false, 10), "Ended with exit code 1, after 80 ms.");
  assert.equal(endedText(null, "SIGTERM", 10000, true, 10), "Stopped after 10 s: give it longer with nanyx.live.timeLimit.");
});

test("a file's tests re-run by themselves, #io ones too, unless turned off", () => {
  assert.equal(testsOn({ count: 2, io: 0 }, undefined), true);
  assert.equal(testsOn({ count: 2, io: 1 }, undefined), true);
  assert.equal(testsOn({ count: 2, io: 1 }, false), false);
  assert.equal(testsOn({ count: 0, io: 0 }, true), false);
  assert.equal(testsOn(undefined, true), false);
});

test("the sidebar says how many tests the file has, how their last run went, and that #io tests' files are in memory", () => {
  const target = targetOf("/work/app/src/geometry.nyx", "/work/app");
  const rows = rowsOf(target, { entry: null, contexts: [], offered: false }, false, { count: 3, io: 1, last: { passed: 2, failed: 1 } }, true);
  assert.deepEqual(
    rows.map((row) => [row.label, row.description]),
    [
      ["Live mode is on", "app"],
      ["Tests", "3 in this file, last run: 2 passed, 1 failed"],
      ["Files are in memory", "for 1 #io test too"]
    ]
  );
  assert.equal(rowsOf(target, { entry: null, contexts: [], offered: false }, false, { count: 3, io: 0 }, false)[0].label, "Live mode is off");
});
