using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Microsoft.FSharp.Core;
using NyxCompiler;
using Transpiler.Wasm;

/// <summary>
/// The compiler as the page calls it: Nanyx source in, the program's WebAssembly binary and its diagnostics out, with
/// how long each stage took.
/// </summary>
public static partial class BrowserCompiler
{
    public static void Main()
    {
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/>, relative to where the compiler looks for the files
    /// it has beside it: the checked library and the shipped packages.
    /// </summary>
    [JSExport]
    public static void AddFile(string path, byte[] bytes)
    {
        var full = System.IO.Path.Combine(System.AppContext.BaseDirectory, path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, bytes);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> as <c>nanyx build</c> does, and gives a JSON object: <c>wasm</c>, the binary
    /// in base 64 (absent when there are errors), <c>diagnostics</c>, and <c>checkMs</c>, <c>emitMs</c> and
    /// <c>assembleMs</c>, the times of parsing and checking, lowering to WebAssembly text, and assembling the binary.
    /// </summary>
    [JSExport]
    public static string Compile(string source)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Compiler.compile(source);
        var checkMs = stopwatch.Elapsed.TotalMilliseconds;
        var diagnostics = result.Diagnostics.Select(d => $"{d.Code}: {d.Message}").ToList();
        string? wasm = null;
        double emitMs = 0, assembleMs = 0;
        if (FSharpOption<TypedModule>.get_IsSome(result.Typed) && !result.Diagnostics.Any(Diagnostics.isError))
        {
            stopwatch.Restart();
            var emitted = CodeGen.tryTranspileTypedModuleWith(new CodeGen.WasmOptions(false, true), result.Typed.Value);
            emitMs = stopwatch.Elapsed.TotalMilliseconds;
            if (emitted.IsError)
            {
                diagnostics.Add($"{emitted.ErrorValue.Code}: {emitted.ErrorValue.Message}");
            }
            else
            {
                stopwatch.Restart();
                wasm = System.Convert.ToBase64String(WasmBinary.assemble(emitted.ResultValue));
                assembleMs = stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        return JsonSerializer.Serialize(new CompileOutcome(wasm, diagnostics.ToArray(), checkMs, emitMs, assembleMs), OutcomeJson.Default.CompileOutcome);
    }
}

public sealed record CompileOutcome(string? wasm, string[] diagnostics, double checkMs, double emitMs, double assembleMs);

[System.Text.Json.Serialization.JsonSerializable(typeof(CompileOutcome))]
internal sealed partial class OutcomeJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
