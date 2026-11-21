// Serves the published compiler page as a static host would: each file brotli-compressed when the browser accepts it
// (the publish writes a .br beside each file), cacheable, and cross-origin isolated, as the playground's route on the
// docs site is (`docs/vercel.json`), so that a program can read input. It appends each report the page posts to
// results.jsonl.
//
//   node serve.mjs <publish>/wwwroot [port]

import { createServer } from "node:http";
import { appendFileSync, existsSync, readFileSync, statSync } from "node:fs";
import { extname, join, normalize } from "node:path";

const root = process.argv[2];
const port = Number(process.argv[3] ?? 8123);
const types = {
  ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".json": "application/json",
  ".wasm": "application/wasm", ".dat": "application/octet-stream", ".nyx": "text/plain", ".bin": "application/octet-stream",
};

createServer((request, response) => {
  const url = new URL(request.url, "http://localhost");
  if (request.method === "POST" && url.pathname === "/results") {
    let body = "";
    request.on("data", (chunk) => (body += chunk));
    request.on("end", () => {
      appendFileSync("results.jsonl", body + "\n");
      console.log(body);
      response.end();
    });
    return;
  }
  let path = normalize(join(root, decodeURIComponent(url.pathname)));
  if (!path.startsWith(normalize(root)) || !existsSync(path)) { response.writeHead(404).end(); return; }
  if (statSync(path).isDirectory()) path = join(path, "index.html");
  const headers = {
    "Content-Type": types[extname(path)] ?? "application/octet-stream",
    "Cache-Control": "max-age=3600",
    "Cross-Origin-Opener-Policy": "same-origin",
    "Cross-Origin-Embedder-Policy": "require-corp",
  };
  if ((request.headers["accept-encoding"] ?? "").includes("br") && existsSync(path + ".br")) {
    headers["Content-Encoding"] = "br";
    path += ".br";
  }
  response.writeHead(200, headers).end(readFileSync(path));
}).listen(port, () => console.log(`http://localhost:${port}/`));
