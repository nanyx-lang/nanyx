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
exports.hoverLimit = exports.HitReader = void 0;
exports.parseHit = parseHit;
exports.sitesOf = sitesOf;
exports.testHits = testHits;
exports.oneLine = oneLine;
exports.ghostText = ghostText;
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
            ? { file: parsed.file, line: parsed.line, column: parsed.column, value: parsed.value }
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
/** `hits` by their files, full paths, each file's `dbg`s in the order of their places. */
function sitesOf(hits) {
    const byFile = new Map();
    for (const hit of hits) {
        const sites = byFile.get(hit.file) ?? new Map();
        const key = `${hit.line}:${hit.column}`;
        const site = sites.get(key) ?? { line: hit.line, column: hit.column, values: [] };
        site.values.push(hit.value);
        sites.set(key, site);
        byFile.set(hit.file, sites);
    }
    return new Map([...byFile].map(([file, sites]) => [file, [...sites.values()].sort((a, b) => a.line - b.line || a.column - b.column)]));
}
/** The hits of a test run, its `dbg` objects, with their files from `root`, the package's directory or a script's. */
function testHits(root, hits) {
    return hits.map((hit) => ({ ...hit, file: path.isAbsolute(hit.file) ? hit.file : path.join(root, hit.file) }));
}
/** How long a value is shown at the end of a line before it is cut short. */
const shownLength = 80;
/** `value` on one line, its line breaks shown as `⏎`, cut short after `length` characters with `…`. */
function oneLine(value, length = shownLength) {
    const flat = value.replace(/\r?\n/g, " ⏎ ");
    return flat.length > length ? flat.slice(0, length - 1) + "…" : flat;
}
/** What a `dbg` shows at the end of its line: `▸ 84`, or, for one reached several times, `▸ ×3, last #shipped`. */
function ghostText(site) {
    const last = oneLine(site.values[site.values.length - 1]);
    return site.values.length === 1 ? `▸ ${last}` : `▸ ×${site.values.length}, last ${last}`;
}
/** How many of a `dbg`'s values its hover lists; a command lists them all. */
exports.hoverLimit = 200;
/**
 * The hover of a `dbg`, as Markdown: its value whole, or, for one reached several times, each value in order,
 * numbered, the first `limit` of them, and how many more there are.
 */
function hoverMarkdown(site, limit = exports.hoverLimit) {
    const block = (value) => "```\n" + value.replace(/```/g, "`​``") + "\n```";
    if (site.values.length === 1) {
        return block(site.values[0]);
    }
    const shown = site.values.slice(0, limit).map((value, index) => `**${index + 1}.**\n\n${block(value)}`);
    const more = site.values.length > limit ? [`…and ${site.values.length - limit} more: run **Nanyx: Show dbg values** to see every one.`] : [];
    return [`Reached ${site.values.length} times:`, ...shown, ...more].join("\n\n");
}
//# sourceMappingURL=dbgValues.js.map