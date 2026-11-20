// Loads the compiler built for the browser and hands it the files it reads from beside itself (see
// `BrowserCompiler.AddFile`): the checked library, unless `library` is false, and every file of the packages the
// compiler ships, as `package-files.txt`, which the build writes, lists them.

/** The compiler, ready: `compile(source)` gives `{ wasm, diagnostics, checkMs, emitMs, assembleMs }`, `wasm` the bytes. */
export async function loadCompiler({ library = true } = {}) {
  const { dotnet } = await import("./_framework/dotnet.js");
  const runtime = await dotnet.create();
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  const packageFiles = (await (await fetch("package-files.txt")).text()).split("\n").map((line) => line.trim()).filter((line) => line);
  const files = [...(library ? ["checked-library.bin"] : []), ...packageFiles];
  const contents = await Promise.all(files.map(async (file) => new Uint8Array(await (await fetch(file)).arrayBuffer())));
  files.forEach((file, index) => exports.BrowserCompiler.AddFile(file, contents[index]));
  return {
    compile(source) {
      const outcome = JSON.parse(exports.BrowserCompiler.Compile(source));
      return { ...outcome, wasm: outcome.wasm ? Uint8Array.from(atob(outcome.wasm), (c) => c.charCodeAt(0)) : null };
    },
  };
}
