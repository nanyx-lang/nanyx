"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.hoverLimit = exports.keptValues = exports.HitReader = void 0;
exports.parseHit = parseHit;
exports.addHits = addHits;
exports.inOrder = inOrder;
exports.sitesOf = sitesOf;
exports.testHits = testHits;
exports.oneLine = oneLine;
exports.ghostText = ghostText;
exports.firstKept = firstKept;
exports.hoverMarkdown = hoverMarkdown;
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
/** One line of `nanyx run --dbg-to` as a hit; none for a line that isn't one. */
function parseHit(line) {
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
    }
    catch {
        return undefined;
    }
}
/**
 * Reads the hits a run writes to the file at `file` as it grows: `read` gives those written since the last read, in
 * order, each whole line once, and keeps a line cut short for the next.
 */
class HitReader {
    constructor(file) {
        this.file = file;
        this.offset = 0;
        this.pending = "";
    }
    /** The hits written since the last read: none if the file isn't there yet. */
    read() {
        let fd;
        try {
            fd = fs.openSync(this.file, "r");
        }
        catch {
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
            return lines.map(parseHit).filter((hit) => hit !== undefined);
        }
        finally {
            fs.closeSync(fd);
        }
    }
}
exports.HitReader = HitReader;
/** How many of a place's values are kept, its latest: a loop can give a value millions of times. */
exports.keptValues = 1000;
/** Adds `hits`, all of one file, to its places, `sites`, by kind and place, keeping the latest `keep` values of each. */
function addHits(sites, hits, keep = exports.keptValues) {
    const touched = new Set();
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
function inOrder(sites) {
    return [...sites.values()].sort((a, b) => a.line - b.line || a.column - b.column);
}
/** `hits` by their files, full paths, each file's places in order, keeping the latest `keep` values of each. */
function sitesOf(hits, keep = exports.keptValues) {
    const byFile = new Map();
    hits.forEach((hit) => byFile.set(hit.file, [...(byFile.get(hit.file) ?? []), hit]));
    return new Map([...byFile].map(([file, fileHits]) => {
        const sites = new Map();
        addHits(sites, fileHits, keep);
        return [file, inOrder(sites)];
    }));
}
/** The hits of a test run, its `dbg` objects, with their files from `root`, the package's directory or a script's. */
function testHits(root, hits) {
    return hits.map((hit) => ({ ...hit, file: path.isAbsolute(hit.file) ? hit.file : path.join(root, hit.file), kind: "dbg" }));
}
/** How long a value is shown at the end of a line before it is cut short. */
const shownLength = 80;
/** `value` on one line, its line breaks shown as `⏎`, cut short after `length` characters with `…`. */
function oneLine(value, length = shownLength) {
    const flat = value.replace(/\r?\n/g, " ⏎ ");
    return flat.length > length ? flat.slice(0, length - 1) + "…" : flat;
}
/**
 * What a `dbg` shows at the end of its line: `▸ 84`, or, for one reached several times, `▸ ×3, last #shipped`; a shown
 * value, `= 84` or `= ×3, last #shipped`.
 */
function ghostText(site) {
    const mark = site.kind === "value" ? "=" : "▸";
    const last = oneLine(site.values[site.values.length - 1]);
    return site.count === 1 ? `${mark} ${last}` : `${mark} ×${site.count}, last ${last}`;
}
/** How many of a `dbg`'s values its hover lists; a command lists them all. */
exports.hoverLimit = 200;
/** The number of the first value `site` keeps, counted from 1 among all it gave. */
function firstKept(site) {
    return site.count - site.values.length + 1;
}
/**
 * The hover of a `dbg` or a shown value, as Markdown: its value whole, or, for one reached several times, each value
 * it keeps in order, numbered, the first `limit` of them, and how many more there are.
 */
function hoverMarkdown(site, limit = exports.hoverLimit) {
    const block = (value) => "```\n" + value.replace(/```/g, "`​``") + "\n```";
    if (site.count === 1) {
        return block(site.values[0]);
    }
    const first = firstKept(site);
    const heading = first === 1 ? `Reached ${site.count} times:` : `Reached ${site.count} times, the last ${site.values.length} kept:`;
    const shown = site.values.slice(0, limit).map((value, index) => `**${first + index}.**\n\n${block(value)}`);
    const more = site.values.length > limit ? [`…and ${site.values.length - limit} more: run **Nanyx: Show dbg values** to see every one kept.`] : [];
    return [heading, ...shown, ...more].join("\n\n");
}
//# sourceMappingURL=dbgValues.js.map