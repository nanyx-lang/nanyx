import * as fs from "fs";
import * as path from "path";

/**
 * The model of the values a run's `dbg`s gave, apart from VS Code (`dbgView.ts` shows them): what `nanyx run --dbg-to`
 * writes, one hit a line, read as the file grows; the hits grouped by the `dbg` they came from; and the text each
 * shows, at the end of its line and in its hover. A test run's hits come from `nanyx test --format json` (see
 * `tests.ts`), with no sequence number, and are shown the same way.
 */

/** A `dbg` a run reached: its file, a full path, its line and column, from 1, and the value as `dbg` writes it. */
export interface DbgHit {
  file: string;
  line: number;
  column: number;
  value: string;
}

/** One line of `nanyx run --dbg-to` as a hit; none for a line that isn't one. */
export function parseHit(line: string): DbgHit | undefined {
  const trimmed = line.trim();
  if (!trimmed.startsWith("{")) {
    return undefined;
  }
  try {
    const parsed = JSON.parse(trimmed);
    return typeof parsed?.file === "string" &&
      typeof parsed.line === "number" &&
      typeof parsed.column === "number" &&
      typeof parsed.value === "string"
      ? { file: parsed.file, line: parsed.line, column: parsed.column, value: parsed.value }
      : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Reads the hits a run writes to the file at `file` as it grows: `read` gives those written since the last read, in
 * order, each whole line once, and keeps a line cut short for the next.
 */
export class HitReader {
  private offset = 0;
  private pending = "";

  constructor(readonly file: string) {}

  /** The hits written since the last read: none if the file isn't there yet. */
  read(): DbgHit[] {
    let fd: number;
    try {
      fd = fs.openSync(this.file, "r");
    } catch {
      return [];
    }
    try {
      const size = fs.fstatSync(fd).size;
      if (size <= this.offset) {
        return [];
      }
      const buffer = Buffer.alloc(size - this.offset);
      const read = fs.readSync(fd, buffer, 0, buffer.length, this.offset);
      this.offset += read;
      const lines = (this.pending + buffer.subarray(0, read).toString("utf8")).split(/\r?\n/);
      this.pending = lines.pop() ?? "";
      return lines.map(parseHit).filter((hit): hit is DbgHit => hit !== undefined);
    } finally {
      fs.closeSync(fd);
    }
  }
}

/** A `dbg` of a file and every value it gave, in order. */
export interface DbgSite {
  line: number;
  column: number;
  values: string[];
}

/** `hits` by their files, full paths, each file's `dbg`s in the order of their places. */
export function sitesOf(hits: DbgHit[]): Map<string, DbgSite[]> {
  const byFile = new Map<string, Map<string, DbgSite>>();
  for (const hit of hits) {
    const sites = byFile.get(hit.file) ?? new Map<string, DbgSite>();
    const key = `${hit.line}:${hit.column}`;
    const site = sites.get(key) ?? { line: hit.line, column: hit.column, values: [] };
    site.values.push(hit.value);
    sites.set(key, site);
    byFile.set(hit.file, sites);
  }
  return new Map(
    [...byFile].map(([file, sites]) => [file, [...sites.values()].sort((a, b) => a.line - b.line || a.column - b.column)])
  );
}

/** The hits of a test run, its `dbg` objects, with their files from `root`, the package's directory or a script's. */
export function testHits(root: string, hits: { file: string; line: number; column: number; value: string }[]): DbgHit[] {
  return hits.map((hit) => ({ ...hit, file: path.isAbsolute(hit.file) ? hit.file : path.join(root, hit.file) }));
}

/** How long a value is shown at the end of a line before it is cut short. */
const shownLength = 80;

/** `value` on one line, its line breaks shown as `⏎`, cut short after `length` characters with `…`. */
export function oneLine(value: string, length = shownLength): string {
  const flat = value.replace(/\r?\n/g, " ⏎ ");
  return flat.length > length ? flat.slice(0, length - 1) + "…" : flat;
}

/** What a `dbg` shows at the end of its line: `▸ 84`, or, for one reached several times, `▸ ×3, last #shipped`. */
export function ghostText(site: DbgSite): string {
  const last = oneLine(site.values[site.values.length - 1]);
  return site.values.length === 1 ? `▸ ${last}` : `▸ ×${site.values.length}, last ${last}`;
}

/** How many of a `dbg`'s values its hover lists; a command lists them all. */
export const hoverLimit = 200;

/**
 * The hover of a `dbg`, as Markdown: its value whole, or, for one reached several times, each value in order,
 * numbered, the first `limit` of them, and how many more there are.
 */
export function hoverMarkdown(site: DbgSite, limit = hoverLimit): string {
  const block = (value: string) => "```\n" + value.replace(/```/g, "`​``") + "\n```";
  if (site.values.length === 1) {
    return block(site.values[0]);
  }
  const shown = site.values.slice(0, limit).map((value, index) => `**${index + 1}.**\n\n${block(value)}`);
  const more = site.values.length > limit ? [`…and ${site.values.length - limit} more: run **Nanyx: Show dbg values** to see every one.`] : [];
  return [`Reached ${site.values.length} times:`, ...shown, ...more].join("\n\n");
}
