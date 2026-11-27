import * as fs from "fs";
import * as path from "path";

/**
 * The model of live mode, apart from VS Code (`liveMode.ts` runs it): which entry point a file belongs to, whether live
 * mode is on for it, what the sidebar says about it, and the arguments of a live run. Live mode re-runs an entry point
 * on every save of its file, or of any file of its package, with its files in memory and no input, and shows what it
 * printed and its `dbg` values.
 */

/** What the language server says of a file's entry point (`nanyx/liveMode`). */
export interface LiveInfo {
  /** `statements` for a script's, `entryDefinition` for a def marked `@entry`, or none. */
  entry: "statements" | "entryDefinition" | null;
  /** The contexts the entry point requires. */
  contexts: string[];
  /** Whether live mode is offered on its own: it requires none but `Console`, `Async` and `FileSystem`. */
  offered: boolean;
}

/**
 * What a live run runs, which its setting is kept for: a script, by its path, or a package, by the directory of its
 * manifest, whose `@entry` any of its files' saves re-run.
 */
export interface LiveTarget {
  key: string;
  /** What `nanyx run` is given: the script, or the package's directory. */
  target: string;
  /** The directory the program runs in. */
  directory: string;
  /** What the sidebar calls it. */
  name: string;
  kind: "script" | "package";
}

/** The directory of the package `file` is in, by the nearest `.nyxp` manifest at or above its directory, if it has one. */
export function packageDirectoryOf(file: string, exists: (directory: string) => string[] = fs.readdirSync): string | undefined {
  let directory = path.dirname(file);
  for (;;) {
    let names: string[] = [];
    try {
      names = exists(directory);
    } catch {
      names = [];
    }
    if (names.some((name) => name.endsWith(".nyxp"))) {
      return directory;
    }
    const parent = path.dirname(directory);
    if (parent === directory) {
      return undefined;
    }
    directory = parent;
  }
}

/** What a save of `file` re-runs: its package, if it is in one, or else the file as a script. */
export function targetOf(file: string, packageDirectory: string | undefined): LiveTarget {
  return packageDirectory
    ? { key: packageDirectory, target: packageDirectory, directory: packageDirectory, name: path.basename(packageDirectory), kind: "package" }
    : { key: file, target: file, directory: path.dirname(file), name: path.basename(file), kind: "script" };
}

/** Whether live mode is on: as the user set it for the target, or else on where it is offered on its own. */
export function isOn(info: LiveInfo | undefined, setting: boolean | undefined): boolean {
  if (!info || info.entry === null) {
    return false;
  }
  return setting ?? info.offered;
}

/** The arguments of `nanyx run` for a live run of `target`, its `dbg`s written to `dbgTo`. */
export function liveArguments(target: string, dbgTo: string): string[] {
  return ["run", "--dbg-to", dbgTo, "--files-in-memory", target];
}

/** The contexts that hold live mode back until it is turned on: those `info`'s entry point requires beyond the offered ones. */
export function heldBackBy(info: LiveInfo): string[] {
  const offered = new Set(["Async", "Console", "FileSystem"]);
  return info.contexts.filter((context) => !offered.has(context));
}

/** A row of the sidebar: its text, and what it says more of on hover. */
export interface Row {
  label: string;
  description?: string;
  tooltip?: string;
}

/** What the sidebar says of `target`, whose entry point is `info` and whose live mode is `on`. */
export function rowsOf(target: LiveTarget, info: LiveInfo | undefined, on: boolean): Row[] {
  if (!info || info.entry === null) {
    return [{ label: "No entry point", description: target.name, tooltip: "Live mode re-runs a script, or a package's @entry, on every save." }];
  }
  const rows: Row[] = [
    {
      label: on ? "Live mode is on" : "Live mode is off",
      description: target.name,
      tooltip: on ? "It re-runs on every save." : "Turn it on to re-run on every save."
    },
    {
      label: "Needs",
      description: info.contexts.length > 0 ? info.contexts.join(", ") : "no contexts",
      tooltip: "The contexts the entry point requires, from its type."
    },
    {
      label: "Files are in memory",
      description: "writes never reach the disk",
      tooltip: "A live run reads the real files, and keeps what it writes, appends, makes and deletes in memory (nanyx run --files-in-memory)."
    },
    {
      label: "Input is empty",
      description: "readLine sees the end of input",
      tooltip: "A live run is given no input."
    }
  ];
  const held = heldBackBy(info);
  if (held.length > 0) {
    rows.push({
      label: on ? "Runs for real" : "Held back",
      description: held.join(", "),
      tooltip: on
        ? `It was turned on for this ${target.kind}, so these run as they do in a terminal.`
        : `It needs ${held.join(", ")}, which a re-run on every save would use for real: turn live mode on for this ${target.kind} to run it anyway.`
    });
  }
  return rows;
}

/** How a live run ended, as the output channel says it. */
export function endedText(code: number | null, signal: string | null, milliseconds: number, timedOut: boolean, limitSeconds: number): string {
  if (timedOut) {
    return `Stopped after ${limitSeconds} s: give it longer with nanyx.live.timeLimit.`;
  }
  if (signal) {
    return `Stopped (${signal}).`;
  }
  return code === 0 ? `Finished in ${milliseconds} ms.` : `Ended with exit code ${code}, after ${milliseconds} ms.`;
}
