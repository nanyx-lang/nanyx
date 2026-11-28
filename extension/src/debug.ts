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
}

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

/** The arguments of `nanyx` for a session that debugs `program` with `args`, which follow a `--`. */
export function debugArguments(program: string, args: string[] = []): string[] {
  return ["run", "--debug", program, ...(args.length > 0 ? ["--", ...args] : [])];
}

/** The directory a session for `program` runs in: `cwd`, or the script's directory, or the package's. */
export function workingDirectory(program: string, cwd: string | undefined): string {
  if (cwd) {
    return cwd;
  }
  return program.endsWith(".nyx") ? path.dirname(program) : program;
}
