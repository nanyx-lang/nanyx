using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using Transpiler.Wasm;

/// <summary>
/// What runs a linter package's lint build: the WebAssembly backend, which assembles it, and the runtime, which runs it,
/// neither of which the compiler's core references (see <c>LintBuilds</c>).
/// </summary>
internal static class LintRunners
{
    /// <summary>The runner the CLI, and the language server it starts, make linters from linter packages with.</summary>
    internal static LintRunner Wasm { get; } = new(
        FuncConvert.FromFunc((TypedModule typed) => Nanyx.LargeStack.run(FuncConvert.FromFunc(() => LintBuild.assemble(CodeGen.WasmOptions.Default, typed)))),
        FuncConvert.FromFunc((byte[] wasm) => WasmLinter.load(wasm)));

    /// <summary>Makes linters from linter packages read from <paramref name="store"/>, with <see cref="Wasm"/>.</summary>
    internal static FSharpFunc<LinterPackage, FSharpResult<Linter, string>> MakerFor(PackageStore store) =>
        FuncConvert.FromFunc((LinterPackage linter) => LintBuilds.make(store, Wasm, linter));
}
