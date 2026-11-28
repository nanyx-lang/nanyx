import * as fs from "fs";
import * as path from "path";

/**
 * The model of the values a run's `dbg`s gave, apart from VS Code (`dbgView.ts` shows them): what `nanyx run --dbg-to`
 * writes, one hit a line, read as the file grows; the hits grouped by the `dbg` they came from; and the text each
 * shows, at the end of its line and in its hover. With `--show-values`, a run writes every value its code binds or
 * gives as well, each a hit of the kind `value`, which is shown as a `dbg`'s is. A test run's hits come from
 * `nanyx test --format json` (see `tests.ts`), with no sequence number, and are shown the same way.
 */

/** What gave a hit: a `dbg` the program writes, or a value `nanyx run --show-values` shows. */
export type HitKind = "dbg" | "value";

/**
 * A `dbg` a run reached, or a value it showed: its file, a full path, its line and column, from 1, the value as `dbg`
 * writes it, and which of them it is.
 */
export interface DbgHit {
  file: string;
  line: number;
  column: number;
  value: string;
  kind: HitKind;
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
      ? { file: parsed.file, line: parsed.line, column: parsed.column, value: parsed.value, kind: parsed.kind === "value" ? "value" : "dbg" }
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

/**
 * A `dbg` of a file, or a place a value was shown, of the kind `kind`: how many values it gave, `count`, and the latest
 * of them, at most `keptValues`, in order.
 */
export interface DbgSite {
  line: number;
  column: number;
  kind: HitKind;
  values: string[];
  count: number;
}

/** How many of a place's values are kept, its latest: a loop can give a value millions of times. */
export const keptValues = 1000;

/** Adds `hits`, all of one file, to its places, `sites`, by kind and place, keeping the latest `keep` values of each. */
export function addHits(sites: Map<string, DbgSite>, hits: DbgHit[], keep = keptValues): void {
  const touched = new Set<DbgSite>();
  for (const hit of hits) {
    const key = `${hit.kind}:${hit.line}:${hit.column}`;
    const site = sites.get(key) ?? { line: hit.line, column: hit.column, kind: hit.kind, values: [], count: 0 };
    site.values.push(hit.value);
    site.count++;
    sites.set(key, site);
    touched.add(site);
  }
  touched.forEach((site) => site.values.length > keep && site.values.splice(0, site.values.length - keep));
}

/** `sites` in the order of their places. */
export function inOrder(sites: Map<string, DbgSite>): DbgSite[] {
  return [...sites.values()].sort((a, b) => a.line - b.line || a.column - b.column);
}

/** `hits` by their files, full paths, each file's places in order, keeping the latest `keep` values of each. */
export function sitesOf(hits: DbgHit[], keep = keptValues): Map<string, DbgSite[]> {
  const byFile = new Map<string, DbgHit[]>();
  hits.forEach((hit) => byFile.set(hit.file, [...(byFile.get(hit.file) ?? []), hit]));
  return new Map(
    [...byFile].map(([file, fileHits]) => {
      const sites = new Map<string, DbgSite>();
      addHits(sites, fileHits, keep);
      return [file, inOrder(sites)];
    })
  );
}

/** The hits of a test run, its `dbg` objects, with their files from `root`, the package's directory or a script's. */
export function testHits(root: string, hits: { file: string; line: number; column: number; value: string }[]): DbgHit[] {
  return hits.map((hit) => ({ ...hit, file: path.isAbsolute(hit.file) ? hit.file : path.join(root, hit.file), kind: "dbg" }));
}

/** How long a value is shown at the end of a line before it is cut short. */
const shownLength = 80;

/** `value` on one line, its line breaks shown as `⏎`, cut short after `length` characters with `…`. */
export function oneLine(value: string, length = shownLength): string {
  const flat = value.replace(/\r?\n/g, " ⏎ ");
  return flat.length > length ? flat.slice(0, length - 1) + "…" : flat;
}

/**
 * What a `dbg` shows at the end of its line: `▸ 84`, or, for one reached several times, `▸ ×3, last #shipped`; a shown
 * value, `= 84` or `= ×3, last #shipped`.
 */
export function ghostText(site: DbgSite): string {
  const mark = site.kind === "value" ? "=" : "▸";
  const last = oneLine(site.values[site.values.length - 1]);
  return site.count === 1 ? `${mark} ${last}` : `${mark} ×${site.count}, last ${last}`;
}

/** How many of a `dbg`'s values its hover lists; a command lists them all. */
export const hoverLimit = 200;

/** The number of the first value `site` keeps, counted from 1 among all it gave. */
export function firstKept(site: DbgSite): number {
  return site.count - site.values.length + 1;
}

/**
 * The hover of a `dbg` or a shown value, as Markdown: its value whole, or, for one reached several times, each value
 * it keeps in order, numbered, the first `limit` of them, and how many more there are.
 */
export function hoverMarkdown(site: DbgSite, limit = hoverLimit): string {
  const block = (value: string) => "```\n" + value.replace(/```/g, "`​``") + "\n```";
  if (site.count === 1) {
    return block(site.values[0]);
  }
  const first = firstKept(site);
  const heading = first === 1 ? `Reached ${site.count} times:` : `Reached ${site.count} times, the last ${site.values.length} kept:`;
  const shown = site.values.slice(0, limit).map((value, index) => `**${first + index}.**\n\n${block(value)}`);
  const more = site.values.length > limit ? [`…and ${site.values.length - limit} more: run **Nanyx: Show dbg values** to see every one kept.`] : [];
  return [heading, ...shown, ...more].join("\n\n");
}
