import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "path";
import {
  buildFailureMessage,
  LineSplitter,
  listArguments,
  listingOf,
  modulesOf,
  parseLine,
  positionOf,
  resultOf,
  runArguments,
  selectionOf,
  TestRunEvent
} from "../tests";

// Lines as `nanyx test --format json` writes them.
const listed = (name: string, module: string, own: string, line: number, extra: object = {}) =>
  JSON.stringify({ type: "test", name, module, test: own, file: "src/geometry.nyx", line, column: 1, io: false, crashes: null, ...extra });

test("a line that is an object of a known type is an event, and any other line is none", () => {
  assert.equal(parseLine('{"type":"summary","built":true,"passed":1,"failed":0}')?.type, "summary");
  assert.equal(parseLine(""), undefined);
  assert.equal(parseLine("Running 2 tests"), undefined);
  assert.equal(parseLine('{"type":"other"}'), undefined);
  assert.equal(parseLine("{not json"), undefined);
});

test("a seed is kept as it is written, though a JavaScript number can't hold it exactly", () => {
  const start = parseLine('{"type":"start","name":"geo.nyx","tests":2,"seed":2498087690890199580,"deferredErrors":0}');
  assert.equal(start?.type === "start" && start.seed, "2498087690890199580");
});

test("output that arrives in pieces is split into whole lines", () => {
  const splitter = new LineSplitter();
  assert.deepEqual(splitter.push('{"a":1}\n{"b"'), ['{"a":1}']);
  assert.deepEqual(splitter.push(':2}\r\n{"c":3}'), ['{"b":2}']);
  assert.deepEqual(splitter.end(), ['{"c":3}']);
  assert.deepEqual(splitter.end(), []);
});

test("a listing has its tests, its diagnostics, and whether it was built", () => {
  const listing = listingOf([
    listed("geometry.adds", "geometry", "adds", 6),
    '{"type":"diagnostic","code":"NYX0201","severity":"warning","headline":"h","message":"m","deferred":true}',
    '{"type":"summary","built":true,"passed":0,"failed":0}'
  ]);
  assert.equal(listing.built, true);
  assert.deepEqual(listing.tests.map((t) => t.name), ["geometry.adds"]);
  assert.equal(listing.diagnostics.length, 1);
  assert.equal(listingOf(['{"type":"summary","built":false,"passed":0,"failed":0}']).built, false);
});

test("tests are grouped by module, in the order modules are first listed, each by where its tests are written", () => {
  const tests = listingOf([
    listed("shapes.squares", "shapes", "squares", 4),
    listed("geometry.`adds twice`", "geometry", "adds twice", 11),
    listed("geometry.adds", "geometry", "adds", 6)
  ]).tests;
  assert.deepEqual(
    modulesOf(tests).map((m) => [m.module, m.tests.map((t) => t.test)]),
    [["shapes", ["squares"]], ["geometry", ["adds", "adds twice"]]]
  );
});

test("a run of all of a package's tests, of a module's, or of tests by name has the arguments for it", () => {
  assert.deepEqual(listArguments("/p"), ["test", "/p", "--list", "--format", "json"]);
  assert.deepEqual(runArguments("/p", { kind: "all" }), ["test", "/p", "--format", "json"]);
  assert.deepEqual(runArguments("/p", { kind: "module", module: "geometry" }), ["test", "/p", "--format", "json", "--module", "geometry"]);
  assert.deepEqual(runArguments("/p", { kind: "tests", names: ["geometry.adds", "shapes.squares"] }), [
    "test", "/p", "--format", "json", "--test", "geometry.adds\nshapes.squares"
  ]);
});

test("what is chosen becomes a run of all tests, one module's, or tests by name", () => {
  const modules = modulesOf(listingOf([listed("geometry.adds", "geometry", "adds", 6), listed("shapes.squares", "shapes", "squares", 4)]).tests);
  assert.deepEqual(selectionOf(true, modules, [], []), { kind: "all" });
  assert.deepEqual(selectionOf(false, modules, ["geometry"], []), { kind: "module", module: "geometry" });
  assert.deepEqual(selectionOf(false, modules, [], ["shapes.squares"]), { kind: "tests", names: ["shapes.squares"] });
  assert.deepEqual(selectionOf(false, modules, ["geometry"], ["shapes.squares"]), { kind: "tests", names: ["shapes.squares", "geometry.adds"] });
});

const ran = (extra: Partial<TestRunEvent>): TestRunEvent =>
  ({ type: "test", name: "geometry.halves", module: "geometry", test: "halves", file: "src/geometry.nyx", line: 11, column: 1, io: false, crashes: null, output: "halving", errors: "", ...extra }) as TestRunEvent;

test("a test that passed passes, with what it printed", () => {
  assert.deepEqual(resultOf(ran({ outcome: "passed" })), { state: "passed", stack: [], output: "halving" });
});

test("a test that crashed fails with the crash's message, place and stack", () => {
  const result = resultOf(
    ran({
      outcome: "failed",
      failure: {
        kind: "crashed",
        message: "src/geometry.nyx:13:3: assert(double(3) == 5)\n  double(3) = 6\n",
        place: { file: "src/geometry.nyx", line: 13, column: 3 },
        stack: [{ function: "geometry_halves", file: "src/geometry.nyx", line: 13 }]
      }
    })
  );
  assert.equal(result.state, "failed");
  assert.equal(result.message, "src/geometry.nyx:13:3: assert(double(3) == 5)\n  double(3) = 6\n");
  assert.deepEqual(result.place, { file: "src/geometry.nyx", line: 13, column: 3 });
  assert.equal(result.stack.length, 1);
  assert.equal(result.output, "halving");
});

test("a failure with no message says what it wrote, or what kind it was", () => {
  assert.equal(resultOf(ran({ outcome: "failed", failure: { kind: "trapped" }, errors: "it wrote this" })).message, "it wrote this");
  assert.equal(resultOf(ran({ outcome: "failed", failure: { kind: "stackOverflow", function: "spin" } })).message, "It ran out of stack in 'spin'");
});

test("a timeout, a test that didn't crash and one that crashed otherwise each say so", () => {
  assert.match(resultOf(ran({ outcome: "timedOut", timeoutSeconds: 0.2 })).message ?? "", /longer than 0.2 seconds/);
  assert.equal(resultOf(ran({ outcome: "didNotCrash" })).message, "It finished, and was expected to crash");
  const otherwise = resultOf(ran({ outcome: "crashedOtherwise", expected: "too big", failure: { kind: "crashed", message: "boom" } }));
  assert.equal(otherwise.message, 'It was expected to crash with a message that contains "too big", and crashed with another:\nboom');
});

test("a build that fails says so with its errors, each with where it is", () => {
  const message = buildFailureMessage([
    { type: "diagnostic", code: "NYX0001", severity: "error", headline: "Syntax error", message: "Expected an expression", file: "src/broken.nyx", line: 5, column: 14, deferred: false },
    { type: "diagnostic", code: "W", severity: "warning", headline: "h", message: "m", deferred: false }
  ]);
  assert.equal(message, "The tests can't be built:\nsrc/broken.nyx:5:14: error NYX0001: Syntax error: Expected an expression");
});

test("a place from the package's root is in its directory, counted from 0, and a full path stays as it is", () => {
  assert.deepEqual(positionOf("/work/app", "src/geometry.nyx", 13, 3), { path: path.join("/work/app", "src/geometry.nyx"), line: 12, character: 2 });
  assert.deepEqual(positionOf("/work/app", "/elsewhere/x.nyx", 1), { path: "/elsewhere/x.nyx", line: 0, character: 0 });
});
