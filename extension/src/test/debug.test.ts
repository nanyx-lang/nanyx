import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "path";
import { consoleOf, debugArguments, portIn, resolvedLaunch, workingDirectory } from "../debug";

test("a launch with no program debugs the active file, and one with none to debug starts no session", () => {
  assert.deepEqual(resolvedLaunch({}, "/work/tool.nyx"), {
    type: "nanyx",
    request: "launch",
    name: "Debug tool.nyx",
    program: "/work/tool.nyx"
  });
  assert.equal(resolvedLaunch({}, undefined), undefined);
});

test("a launch that names its program keeps it, and what else it says", () => {
  const resolved = resolvedLaunch({ type: "nanyx", request: "launch", name: "Shop", program: "/work/app", stopOnEntry: true }, "/work/other.nyx");
  assert.equal(resolved?.program, "/work/app");
  assert.equal(resolved?.name, "Shop");
  assert.equal(resolved?.stopOnEntry, true);
});

test("a session runs nanyx run --debug, with the program's arguments after a --", () => {
  assert.deepEqual(debugArguments("/work/tool.nyx"), ["run", "--debug", "/work/tool.nyx"]);
  assert.deepEqual(debugArguments("/work/tool.nyx", ["--verbose", "in.txt"]), ["run", "--debug", "/work/tool.nyx", "--", "--verbose", "in.txt"]);
});

test("a session runs in its cwd, or the script's directory, or the package's", () => {
  assert.equal(workingDirectory("/work/tool.nyx", "/elsewhere"), "/elsewhere");
  assert.equal(workingDirectory(path.join("/work", "tool.nyx"), undefined), path.join("/work"));
  assert.equal(workingDirectory("/work/app", undefined), "/work/app");
});

test("a session listening on a port has nanyx write it to a file, before the program and its arguments", () => {
  assert.deepEqual(debugArguments("/work/tool.nyx", ["in.txt"], "/tmp/x.port"), [
    "run",
    "--debug",
    "--dap-port",
    "0",
    "--dap-port-file",
    "/tmp/x.port",
    "/work/tool.nyx",
    "--",
    "in.txt"
  ]);
});

test("the program runs in a terminal by default, in the Debug Console when asked, and there with a note when there is no terminal", () => {
  assert.deepEqual(consoleOf({}, true), { console: "integratedTerminal" });
  assert.deepEqual(consoleOf({ console: "debugConsole" }, true), { console: "debugConsole" });
  const fallback = consoleOf({}, false);
  assert.equal(fallback.console, "debugConsole");
  assert.match(fallback.note ?? "", /no terminal/);
});

test("a port file holds a port, and anything else is none", () => {
  assert.equal(portIn("51234\n"), 51234);
  assert.equal(portIn(""), undefined);
  assert.equal(portIn("0"), undefined);
  assert.equal(portIn("70000"), undefined);
});
