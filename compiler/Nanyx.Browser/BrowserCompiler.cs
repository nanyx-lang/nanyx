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
    /// Where a page's program is, as far as the compiler is concerned: a script of its own, in a directory of the
    /// runtime's in-memory file system that holds nothing else, so that it imports only the standard library and the
    /// packages its directives name.
    /// </summary>
    private const string ScriptPath = "/playground/main.nyx";

    /// <summary>
    /// Where the packages a script's directives name are found: none but those the compiler ships, since a page
    /// downloads no others, read from the compiler's own copies, which a page has no need to pack.
    /// </summary>
    private static readonly PackageStore Store = SeededPackages.inMemory("/playground/cache", "/playground/index", GitHub.unsupported);

    /// <summary>
    /// How a page's program is compiled: its start exported, so that the runner calls it once it can read the module's
    /// memory, and what its <c>dbg</c>s print given to the page, apart from what it writes.
    /// </summary>
    private static readonly CodeGen.WasmOptions Options =
        new(checkMemory: false, @inline: true, release: false, sourceRoot: FSharpOption<string>.None, places: false, probes: false, crashToRuntime: false, dbgToEmbedder: true, exportStart: true, component: null, componentExports: Microsoft.FSharp.Collections.FSharpList<System.Tuple<string, ComponentFunction>>.Empty, yieldAtEveryWait: false, frameDrops: false, showValuesIn: Microsoft.FSharp.Collections.FSharpList<string>.Empty);

    /// <summary>
    /// Compiles <paramref name="source"/> as <c>nanyx build</c> compiles a script, but under the playground host,
    /// <c>nanyx.playground.host</c>, unless its <c>::host</c> names another; and gives a JSON object: <c>wasm</c>, the
    /// binary in base 64 (absent when there are errors), <c>diagnostics</c>, and <c>checkMs</c>, <c>emitMs</c> and
    /// <c>assembleMs</c>, the times of parsing and checking, lowering to WebAssembly text, and assembling the binary, and
    /// <c>wat</c>, the WebAssembly text the binary is assembled from (absent when there is none). The module exports its
    /// start, as <c>__nanyx_start</c>, for the playground host's runner to call.
    /// </summary>
    [JSExport]
    public static string Compile(string source)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Script.compileUnder(Hosts.playgroundPackage, Store, ScriptPath, source);
        var checkMs = stopwatch.Elapsed.TotalMilliseconds;
        var diagnostics = result.Diagnostics.Select(d => $"{d.Code}: {Diagnostics.oneLine(d)}").ToList();
        string? wasm = null, wat = null;
        double emitMs = 0, assembleMs = 0;
        if (FSharpOption<TypedModule>.get_IsSome(result.Typed) && !result.Diagnostics.Any(Diagnostics.isError))
        {
            stopwatch.Restart();
            var emitted = CodeGen.tryTranspileTypedModuleWith(Options, result.Typed.Value);
            emitMs = stopwatch.Elapsed.TotalMilliseconds;
            if (emitted.IsError)
            {
                diagnostics.Add($"{emitted.ErrorValue.Code}: {Diagnostics.oneLine(emitted.ErrorValue)}");
            }
            else
            {
                stopwatch.Restart();
                wat = emitted.ResultValue;
                wasm = System.Convert.ToBase64String(WasmBinary.assemble(wat));
                assembleMs = stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        return JsonSerializer.Serialize(new CompileOutcome(wasm, diagnostics.ToArray(), checkMs, emitMs, assembleMs, wat), OutcomeJson.Default.CompileOutcome);
    }

    /// <summary>
    /// Checks <paramref name="source"/> as <see cref="Compile"/> does, without writing WebAssembly, for an editor to show
    /// its diagnostics as the code is typed; and gives a JSON object whose <c>diagnostics</c> are each one's <c>code</c>,
    /// <c>headline</c>, <c>body</c>, whether it is an <c>error</c>, and <c>from</c> and <c>to</c>, where it is in
    /// <paramref name="source"/>, or none for one elsewhere, as in a package it imports.
    /// </summary>
    [JSExport]
    public static string Check(string source)
    {
        var result = Script.compileUnder(Hosts.playgroundPackage, Store, ScriptPath, source);
        var diagnostics =
            result.Diagnostics
                .Select(d =>
                {
                    var inSource = FSharpOption<Parser.Program.SourceSpan>.get_IsSome(d.Span) && d.Span.Value.File == ScriptPath && d.Span.Value.Start >= 0;
                    int? from = inSource ? System.Math.Min(d.Span.Value.Start, source.Length) : null;
                    int? to = inSource ? System.Math.Min(System.Math.Max(d.Span.Value.End, d.Span.Value.Start), source.Length) : null;
                    return new EditorDiagnostic(d.Code, Diagnostics.headline(d), d.Message, Diagnostics.isError(d), from, to);
                })
                .ToArray();
        return JsonSerializer.Serialize(new CheckOutcome(diagnostics), OutcomeJson.Default.CheckOutcome);
    }
}

public sealed record CompileOutcome(string? wasm, string[] diagnostics, double checkMs, double emitMs, double assembleMs, string? wat);

/// <summary>A diagnostic as the editor shows it: its code, headline and body, and where it is in the code, if it is there.</summary>
public sealed record EditorDiagnostic(string code, string headline, string body, bool error, int? from, int? to);

public sealed record CheckOutcome(EditorDiagnostic[] diagnostics);

[System.Text.Json.Serialization.JsonSerializable(typeof(CompileOutcome))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CheckOutcome))]
internal sealed partial class OutcomeJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
