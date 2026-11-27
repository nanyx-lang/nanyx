import * as path from "path";

/**
 * The test explorer's model, apart from VS Code: what `nanyx test --format json` writes, one object a line, read into
 * events; a package's listing (`nanyx test --list`) as a tree; the arguments of each run; and each test's result. The
 * controller (`testController.ts`) shows these in the editor.
 */

/** A place in a source file, from 1, as the compiler writes it: the file from the package's root, or a full path. */
export interface Place {
  file: string;
  line: number;
  column?: number;
}

/** The crash a test is marked to expect: any, `@test(#crashes)`, or one whose message contains `text`. */
export interface ExpectedCrash {
  text?: string;
}

/** A test, as a listing or a run names it. */
export interface ListedTest {
  /** Its full name, as the report writes it: geometry.`adds points`. */
  name: string;
  module: string;
  /** Its own name in its module. */
  test: string;
  file: string;
  line: number;
  column: number;
  io: boolean;
  crashes: ExpectedCrash | null;
}

/** A frame of a failed test's stack, innermost first. */
export interface Frame {
  function: string;
  file?: string;
  line?: number;
}

/** Why a test failed, as the compiler writes it. */
export interface Failure {
  kind: string;
  message?: string;
  place?: Place;
  stack?: Frame[];
  exitCode?: number;
  function?: string;
}

/** A test that ran, with how it ended. */
export interface TestRunEvent extends ListedTest {
  type: "test";
  outcome?: "passed" | "failed" | "timedOut" | "didNotCrash" | "crashedOtherwise";
  timeoutSeconds?: number;
  expected?: string;
  failure?: Failure;
  output?: string;
  errors?: string;
}

export interface DiagnosticEvent {
  type: "diagnostic";
  code: string;
  severity: string;
  headline: string;
  message: string;
  file?: string;
  line?: number;
  column?: number;
  deferred: boolean;
}

export interface StartEvent {
  type: "start";
  name: string;
  tests: number;
  /** As written: a seed is a 64-bit number, more than a JavaScript number holds exactly. */
  seed: string;
  deferredErrors: number;
}

export interface SummaryEvent {
  type: "summary";
  built: boolean;
  passed: number;
  failed: number;
  seed?: string;
}

export type TestEvent = DiagnosticEvent | StartEvent | TestRunEvent | SummaryEvent;

/** One line of `nanyx test --format json` as an event; none for a line that isn't one, such as a blank one. */
export function parseLine(line: string): TestEvent | undefined {
  const trimmed = line.trim();
  if (!trimmed.startsWith("{")) {
    return undefined;
  }
  try {
    const parsed = JSON.parse(trimmed);
    switch (parsed?.type) {
      case "start":
      case "summary": {
        const seed = /"seed":\s*(\d+)/.exec(trimmed);
        return (seed ? { ...parsed, seed: seed[1] } : parsed) as TestEvent;
      }
      case "diagnostic":
      case "test":
        return parsed as TestEvent;
      default:
        return undefined;
    }
  } catch {
    return undefined;
  }
}

/** Splits a process's output, which arrives in pieces, into whole lines. */
export class LineSplitter {
  private pending = "";

  /** The lines `chunk` completes. */
  push(chunk: string): string[] {
    const text = this.pending + chunk;
    const lines = text.split(/\r?\n/);
    this.pending = lines.pop() ?? "";
    return lines;
  }

  /** What is left once the output ends. */
  end(): string[] {
    const rest = this.pending;
    this.pending = "";
    return rest === "" ? [] : [rest];
  }
}

/** The tests of a listing, the `test` events of `nanyx test --list --format json`, and whether it was built. */
export interface Listing {
  built: boolean;
  tests: ListedTest[];
  diagnostics: DiagnosticEvent[];
}

/** The listing the lines `lines` of `nanyx test --list --format json` give. */
export function listingOf(lines: string[]): Listing {
  const listing: Listing = { built: false, tests: [], diagnostics: [] };
  for (const line of lines) {
    const event = parseLine(line);
    if (event?.type === "test") {
      listing.tests.push(event);
    } else if (event?.type === "diagnostic") {
      listing.diagnostics.push(event);
    } else if (event?.type === "summary") {
      listing.built = event.built;
    }
  }
  return listing;
}

/** A module of a package and its tests, in the order they are written. */
export interface ModuleTests {
  module: string;
  tests: ListedTest[];
}

/** `tests` by module, the modules in the order their first tests are listed, and each module's tests by where they are written. */
export function modulesOf(tests: ListedTest[]): ModuleTests[] {
  const byModule = new Map<string, ListedTest[]>();
  for (const test of tests) {
    const found = byModule.get(test.module) ?? [];
    found.push(test);
    byModule.set(test.module, found);
  }
  return [...byModule].map(([module, moduleTests]) => ({
    module,
    tests: [...moduleTests].sort((a, b) => a.file.localeCompare(b.file) || a.line - b.line || a.column - b.column)
  }));
}

/** The ID of the test item of a package's root (its manifest's directory, or a script's path). */
export function rootId(root: string): string {
  return root;
}

/** The ID of the test item of the module `module` of the package at `root`. */
export function moduleId(root: string, module: string): string {
  return `${root}\u0000${module}`;
}

/** The ID of the test item of the test whose full name is `name`, of the package at `root`. */
export function testId(root: string, name: string): string {
  return `${root}\u0000\u0000${name}`;
}

/** What a run runs of a package: all its tests, a module's, or the tests named. */
export type Selection = { kind: "all" } | { kind: "module"; module: string } | { kind: "tests"; names: string[] };

/** The arguments of `nanyx test` that list the tests of `target`, a package's directory or a script. */
export function listArguments(target: string): string[] {
  return ["test", target, "--list", "--format", "json"];
}

/** The arguments of `nanyx test` that run `selection` of `target`, a package's directory or a script, reporting as JSON. */
export function runArguments(target: string, selection: Selection): string[] {
  const args = ["test", target, "--format", "json"];
  switch (selection.kind) {
    case "all":
      return args;
    case "module":
      return [...args, "--module", selection.module];
    case "tests":
      // Several names go in one `--test`, one on each line.
      return [...args, "--test", selection.names.join("\n")];
  }
}

/**
 * What one run of `target` runs, for the items chosen: all its tests if its root is chosen, a module's if that module
 * is the one thing chosen, or else the tests chosen and the tests of the modules chosen, by name.
 */
export function selectionOf(root: boolean, modules: ModuleTests[], chosenModules: string[], chosenTests: string[]): Selection {
  if (root) {
    return { kind: "all" };
  }
  if (chosenModules.length === 1 && chosenTests.length === 0) {
    return { kind: "module", module: chosenModules[0] };
  }
  const names = new Set(chosenTests);
  for (const module of modules) {
    if (chosenModules.includes(module.module)) {
      module.tests.forEach((test) => names.add(test.name));
    }
  }
  return { kind: "tests", names: [...names] };
}

/** How a test ended, for the editor: its state, why it failed, where, its stack, and what it printed. */
export interface Result {
  state: "passed" | "failed" | "errored";
  message?: string;
  /** Where it failed: the program's code it crashed in, when the crash says. */
  place?: Place;
  stack: Frame[];
  output: string;
}

/** The text of `failure`: the message it has, or else what the test wrote, or else what kind of failure it was. */
function failureText(failure: Failure | undefined, errors: string | undefined): string {
  if (failure?.message) {
    return failure.message;
  }
  if (errors) {
    return errors;
  }
  switch (failure?.kind) {
    case "stackOverflow":
      return `It ran out of stack${failure.function ? ` in '${failure.function}'` : ""}`;
    case "outOfMemory":
      return "It ran out of memory";
    case "exited":
      return `It exited with ${failure.exitCode}`;
    default:
      return "It failed";
  }
}

/** The result of the test `event` (see `Result`). */
export function resultOf(event: TestRunEvent): Result {
  const output = event.output ?? "";
  const stack = event.failure?.stack ?? [];
  const place = event.failure?.place;
  switch (event.outcome) {
    case "passed":
      return { state: "passed", stack: [], output };
    case "timedOut":
      return {
        state: "failed",
        message: `It ran for longer than ${event.timeoutSeconds} seconds, and was stopped: give it longer with --timeout`,
        stack: [],
        output
      };
    case "didNotCrash":
      return { state: "failed", message: "It finished, and was expected to crash", stack: [], output };
    case "crashedOtherwise":
      return {
        state: "failed",
        message: `It was expected to crash with a message that contains "${event.expected}", and crashed with another:\n${failureText(event.failure, event.errors)}`,
        place,
        stack,
        output
      };
    default:
      return { state: "failed", message: failureText(event.failure, event.errors), place, stack, output };
  }
}

/** What a build that failed says, for each test it would have run: its errors, each with where it is. */
export function buildFailureMessage(diagnostics: DiagnosticEvent[]): string {
  const errors = diagnostics.filter((diagnostic) => diagnostic.severity === "error");
  const lines = errors.map((diagnostic) => {
    const where = diagnostic.file ? `${diagnostic.file}:${diagnostic.line}:${diagnostic.column}: ` : "";
    return `${where}error ${diagnostic.code}: ${diagnostic.headline}: ${diagnostic.message}`;
  });
  return lines.length > 0 ? `The tests can't be built:\n${lines.join("\n")}` : "The tests can't be built";
}

/** A place in a file, from 0 as the editor counts: the file's full path, its line and its character. */
export interface Position {
  path: string;
  line: number;
  character: number;
}

/**
 * Where `file`, `line` and `column`, a place the compiler wrote, is: the file from `root`, the package's directory or
 * a script's, unless it is a full path already; the line and column from 1, the column 1 where it has none.
 */
export function positionOf(root: string, file: string, line: number, column?: number): Position {
  const full = path.isAbsolute(file) ? file : path.join(root, file);
  return { path: full, line: Math.max(0, line - 1), character: Math.max(0, (column ?? 1) - 1) };
}
