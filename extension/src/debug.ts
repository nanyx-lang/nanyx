import * as path from "path";

/**
 * The model of the `nanyx` debug type, apart from VS Code (`debugger.ts` registers it): what a launch configuration
 * says, the one a debug session started without a `launch.json` gets, and how `nanyx run --debug` is run for it. The
 * debug adapter is `nanyx` itself, speaking the Debug Adapter Protocol on its standard input and output.
 */

/** A `nanyx` launch configuration's own attributes. */
export interface NanyxLaunch {
  type?: string;
  request?: string;
  name?: string;
  /** The script, or a package's directory, to debug. */
  program?: string;
  /** The program's command-line arguments. */
  args?: string[];
  /** Whether it stops at its first statement. */
  stopOnEntry?: boolean;
  /** The directory it runs in. */
  cwd?: string;
  /** Where the program reads and writes: a terminal of its own, or the Debug Console, with no input. */
  console?: DebugConsole;
}

/** Where a debugged program reads and writes. */
export type DebugConsole = "integratedTerminal" | "debugConsole";

/**
 * The configuration a session is started with: `config` as written, or, for one with no program, the file `active`,
 * the active editor's when it is a Nanyx file; none when there is nothing to debug.
 */
export function resolvedLaunch(config: NanyxLaunch, active: string | undefined): NanyxLaunch | undefined {
  const program = config.program ?? active;
  if (!program) {
    return undefined;
  }
  return {
    ...config,
    type: config.type ?? "nanyx",
    request: config.request ?? "launch",
    name: config.name ?? `Debug ${path.basename(program)}`,
    program
  };
}

/**
 * The arguments of `nanyx` for a session that debugs `program` with `args`, which follow a `--`: on its standard
 * streams, or, given `portFile`, on a port of 127.0.0.1 the system chooses and `nanyx` writes to that file, the program
 * then having the terminal it runs in.
 */
export function debugArguments(program: string, args: string[] = [], portFile?: string): string[] {
  const listening = portFile ? ["--dap-port", "0", "--dap-port-file", portFile] : [];
  return ["run", "--debug", ...listening, program, ...(args.length > 0 ? ["--", ...args] : [])];
}

/**
 * Where a session's program reads and writes, and why it isn't where `config` asked: in a terminal, as by default,
 * unless the configuration asks for the Debug Console, or there is no terminal to run it in (`terminals`), as in VS Code
 * for the web.
 */
export function consoleOf(config: NanyxLaunch, terminals: boolean): { console: DebugConsole; note?: string } {
  const asked = config.console ?? "integratedTerminal";
  if (asked === "integratedTerminal" && !terminals) {
    return {
      console: "debugConsole",
      note: "Nanyx: there is no terminal to run the program in here, so what it writes is in the Debug Console, and its input is empty."
    };
  }
  return { console: asked };
}

/** The port `nanyx` wrote to its port file, `text`, if it is one. */
export function portIn(text: string): number | undefined {
  const port = Number(text.trim());
  return Number.isInteger(port) && port > 0 && port <= 65535 ? port : undefined;
}

/** The directory a session for `program` runs in: `cwd`, or the script's directory, or the package's. */
export function workingDirectory(program: string, cwd: string | undefined): string {
  if (cwd) {
    return cwd;
  }
  return program.endsWith(".nyx") ? path.dirname(program) : program;
}
