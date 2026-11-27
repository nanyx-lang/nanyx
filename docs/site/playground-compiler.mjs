// The compiler built for the browser, which the playground runs: the docs build takes it from a compiler release's
// `nanyx-browser.zip` asset (the Compiler workflow's release job attaches it), since Vercel, which builds the site, has
// no .NET SDK to build it. The build (`astro.config.mjs`) unpacks the asset into `public/playground/compiler` before it
// starts, and the site serves it at `/playground/compiler/`, cross-origin isolated (`vercel.json`). To unpack it for
// `npm run dev`:
//
//   node site/playground-compiler.mjs
//
// To try a compiler you built yourself, set `NANYX_BROWSER_ZIP` to the path of a `nanyx-browser.zip`, or of the
// published `wwwroot` zipped the same way.

import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join, normalize, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { inflateRawSync } from "node:zlib";

/**
 * The compiler release the playground runs, the one place it is named: moving the playground on to a newer release
 * is changing this. `null` until the first release that has a `nanyx-browser.zip`, and then the playground has no
 * compiler.
 *
 * @type {string | null}
 */
export const playgroundRelease = "0.49.1";

const docs = dirname(dirname(fileURLToPath(import.meta.url)));
const target = join(docs, "public", "playground", "compiler");
const marker = join(target, "release.json");

/** Where the release's asset is downloaded from. */
const assetUrl = (release) =>
  `https://github.com/nanyx-lang/nanyx/releases/download/compiler%2F${release}/nanyx-browser.zip`;

/**
 * The files in `zip`, a zip archive's bytes, as `[path, bytes]`, read from its central directory. Only what `zip -r`
 * writes is read: stored and deflated files, no ZIP64 and no encryption.
 *
 * @param {Buffer} zip
 * @returns {[string, Buffer][]}
 */
export function unzip(zip) {
  let end = zip.length - 22;
  while (end >= 0 && zip.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error("not a zip archive: it has no end of central directory");
  const count = zip.readUInt16LE(end + 10);
  let entry = zip.readUInt32LE(end + 16);
  /** @type {[string, Buffer][]} */
  const files = [];
  for (let i = 0; i < count; i++) {
    if (zip.readUInt32LE(entry) !== 0x02014b50) throw new Error("the zip's central directory is malformed");
    const method = zip.readUInt16LE(entry + 10);
    const compressedSize = zip.readUInt32LE(entry + 20);
    const nameLength = zip.readUInt16LE(entry + 28);
    const extraLength = zip.readUInt16LE(entry + 30);
    const commentLength = zip.readUInt16LE(entry + 32);
    const local = zip.readUInt32LE(entry + 42);
    const name = zip.toString("utf8", entry + 46, entry + 46 + nameLength);
    entry += 46 + nameLength + extraLength + commentLength;
    if (name.endsWith("/")) continue;
    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    const data = zip.subarray(start, start + compressedSize);
    if (method === 0) files.push([name, Buffer.from(data)]);
    else if (method === 8) files.push([name, inflateRawSync(data)]);
    else throw new Error(`${name} is compressed with method ${method}, which isn't read`);
  }
  return files;
}

/** Writes `files` under `target`, refusing a path that would land outside it. */
function writeFiles(files) {
  rmSync(target, { recursive: true, force: true });
  for (const [name, bytes] of files) {
    const path = normalize(join(target, name));
    if (!path.startsWith(target + sep)) throw new Error(`the zip's file ${name} is outside the directory it unpacks to`);
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, bytes);
  }
}

/** Unpacks the compiler the playground runs into `public/playground/compiler`, or removes it when none is named. */
export async function unpackPlaygroundCompiler() {
  const local = process.env.NANYX_BROWSER_ZIP;
  if (local) {
    writeFiles(unzip(readFileSync(local)));
    writeFileSync(marker, JSON.stringify({ release: null, from: local }) + "\n");
    console.log(`The playground's compiler is ${local}.`);
    return;
  }
  if (playgroundRelease === null) {
    rmSync(target, { recursive: true, force: true });
    console.log("No compiler release is named for the playground (site/playground-compiler.mjs), so it has none.");
    return;
  }
  if (existsSync(marker) && JSON.parse(readFileSync(marker, "utf8")).release === playgroundRelease) {
    console.log(`The playground's compiler is release ${playgroundRelease}, already unpacked.`);
    return;
  }
  const url = assetUrl(playgroundRelease);
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: ${response.status} ${response.statusText}`);
  writeFiles(unzip(Buffer.from(await response.arrayBuffer())));
  writeFileSync(marker, JSON.stringify({ release: playgroundRelease }) + "\n");
  console.log(`The playground's compiler is release ${playgroundRelease}, from ${url}.`);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) await unpackPlaygroundCompiler();
