using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Transpiler.Wasm;

/// <summary>
/// <c>nanyx test --format json</c>: what the test run reports, as one JSON object on each line of standard output, for a
/// tool such as an editor to read rather than the text a person reads. Each object has a <c>type</c>: a
/// <c>diagnostic</c> for each error or warning the build reports, then <c>start</c>, a <c>test</c> for each test as it
/// ends, and a <c>summary</c> last, which a build that fails or a package with no tests writes too. With <c>--list</c>,
/// a <c>test</c> for each test the run would run, with no outcome, and a <c>summary</c>.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>The value of <c>--format</c> that writes JSON.</summary>
    private const string JsonFormat = "json";

    /// <summary>The value of <c>--format</c> that writes text, as without it.</summary>
    private const string TextFormat = "text";

    /// <summary>Writes one object to <paramref name="writer"/>, on a line of its own, its members written by <paramref name="members"/>.</summary>
    private static void WriteJsonLine(TextWriter writer, Action<Utf8JsonWriter> members)
    {
        using var stream = new MemoryStream();
        // Written as the names are, backticks and all: the output is read as JSON, not put in a page.
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            members(json);
            json.WriteEndObject();
        }

        writer.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Writes each of <paramref name="diagnostics"/> as a <c>diagnostic</c> object: its code, severity, headline, body,
    /// and where it is, file, line and column from 1, when it has a place, the file named from the package's root,
    /// <paramref name="root"/>, as a test's is; and <c>deferred</c>, whether a debug build runs in spite of it, which is
    /// what <paramref name="deferred"/> says of every error of the build.
    /// </summary>
    private static void WriteJsonDiagnostics(TextWriter writer, FSharpList<Diagnostic> diagnostics, bool deferred, string? root)
    {
        foreach (var diagnostic in diagnostics)
        {
            WriteJsonLine(writer, json =>
            {
                json.WriteString("type", "diagnostic");
                json.WriteString("code", diagnostic.Code);
                json.WriteString("severity", SeverityName(diagnostic.Severity));
                json.WriteString("headline", NyxCompiler.Diagnostics.headline(diagnostic));
                json.WriteString("message", diagnostic.Message);
                if (FSharpOption<Parser.Program.SourceSpan>.get_IsSome(diagnostic.Span))
                {
                    var span = diagnostic.Span.Value;
                    json.WriteString("file", PackageModulesModule.shownFile(SourceRoot(root), span.File));
                    json.WriteNumber("line", span.StartLine + 1);
                    json.WriteNumber("column", span.StartColumn + 1);
                }

                json.WriteBoolean("deferred", deferred && NyxCompiler.Diagnostics.isError(diagnostic));
            });
        }
    }

    /// <summary>A severity as the JSON writes it: <c>error</c>, <c>warning</c>, <c>info</c> or <c>hint</c>.</summary>
    private static string SeverityName(Severity severity) =>
        severity.IsWarning ? "warning" : severity.IsInfo ? "info" : severity.IsHint ? "hint" : "error";

    /// <summary>The <c>start</c> object: the package's or script's name, how many tests run, the seed, and how many errors are deferred.</summary>
    private static void WriteJsonStart(TextWriter writer, string name, int count, long seed, int deferredErrors) =>
        WriteJsonLine(writer, json =>
        {
            json.WriteString("type", "start");
            json.WriteString("name", name);
            json.WriteNumber("tests", count);
            json.WriteNumber("seed", seed);
            json.WriteNumber("deferredErrors", deferredErrors);
        });

    /// <summary>
    /// The <c>test</c> object for <paramref name="run"/>: its full name as the text report shows it, its module and own
    /// name, where it is written, whether it is <c>#io</c>, its <c>outcome</c> (<c>passed</c>, <c>failed</c> or
    /// <c>timedOut</c>), what it printed (<c>output</c>) and wrote to its standard error (<c>errors</c>); and for one that
    /// failed, a <c>failure</c>: its <c>kind</c> (<c>crashed</c>, <c>trapped</c>, <c>outOfMemory</c>, <c>stackOverflow</c>,
    /// <c>arithmetic</c>, <c>exited</c> or <c>other</c>), its message, the place of the program's code it crashed in, its
    /// file, line and column, and its stack, innermost first.
    /// </summary>
    private static void WriteJsonTest(TextWriter writer, TestRun run) =>
        WriteJsonLine(writer, json =>
        {
            WriteJsonTestFields(json, run.Test);
            switch (run.Outcome)
            {
                case var outcome when outcome.IsPassed:
                    json.WriteString("outcome", "passed");
                    break;
                case TestOutcome.TimedOut timedOut:
                    json.WriteString("outcome", "timedOut");
                    json.WriteNumber("timeoutSeconds", timedOut.Item.TotalSeconds);
                    break;
                case TestOutcome.Failed failed:
                    json.WriteString("outcome", "failed");
                    json.WritePropertyName("failure");
                    WriteJsonFailure(json, failed.Item);
                    break;
            }

            json.WriteString("output", run.Output);
            json.WriteString("errors", run.Errors);
        });

    /// <summary>
    /// The fields a <c>test</c> object starts with, which say which test it is, as a run and a listing both write them:
    /// its full name, its module and own name, where it is written and whether it is <c>#io</c>.
    /// </summary>
    private static void WriteJsonTestFields(Utf8JsonWriter json, ListedTest test)
    {
        json.WriteString("type", "test");
        json.WriteString("name", Shown(test));
        json.WriteString("module", test.Module);
        json.WriteString("test", test.Name);
        json.WriteString("file", test.File);
        json.WriteNumber("line", test.Line);
        json.WriteNumber("column", test.Column);
        json.WriteBoolean("io", test.Io);
    }

    /// <summary>The <c>test</c> object for <paramref name="test"/> in a listing, <c>--list</c>: which test it is, with no outcome.</summary>
    private static void WriteJsonListedTest(TextWriter writer, ListedTest test) =>
        WriteJsonLine(writer, json => WriteJsonTestFields(json, test));

    /// <summary>Writes why a test failed, <paramref name="error"/>, as an object (see <see cref="WriteJsonTest"/>).</summary>
    private static void WriteJsonFailure(Utf8JsonWriter json, RunError error)
    {
        json.WriteStartObject();
        switch (error)
        {
            case RunError.Crashed crashed:
                json.WriteString("kind", "crashed");
                if (FSharpOption<string>.get_IsSome(crashed.message))
                {
                    json.WriteString("message", crashed.message.Value);
                }

                if (FSharpOption<Tuple<string, int, int>>.get_IsSome(crashed.place))
                {
                    var (file, line, column) = crashed.place.Value;
                    json.WritePropertyName("place");
                    json.WriteStartObject();
                    json.WriteString("file", file);
                    json.WriteNumber("line", line);
                    json.WriteNumber("column", column);
                    json.WriteEndObject();
                }

                WriteJsonStack(json, crashed.stack);
                break;
            case RunError.Trapped trapped:
                json.WriteString("kind", "trapped");
                json.WriteString("message", trapped.message);
                WriteJsonStack(json, trapped.stack);
                break;
            case RunError.ArithmeticError arithmetic:
                json.WriteString("kind", "arithmetic");
                json.WriteString("message", WasmRunner.describeFault(arithmetic.fault, arithmetic.functionName));
                WriteJsonStack(json, arithmetic.stack);
                break;
            case RunError.StackOverflow overflow:
                json.WriteString("kind", "stackOverflow");
                if (FSharpOption<string>.get_IsSome(overflow.functionName))
                {
                    json.WriteString("function", overflow.functionName.Value);
                }

                break;
            case RunError.Exited exited:
                json.WriteString("kind", "exited");
                json.WriteNumber("exitCode", exited.Item);
                break;
            case var _ when error.IsOutOfMemory:
                json.WriteString("kind", "outOfMemory");
                break;
            default:
                json.WriteString("kind", "other");
                json.WriteString("message", error.ToString());
                break;
        }

        json.WriteEndObject();
    }

    /// <summary>Writes <paramref name="stack"/> as the array <c>stack</c>: each frame's function and, when it has one, its file and line.</summary>
    private static void WriteJsonStack(Utf8JsonWriter json, FSharpList<Frame> stack)
    {
        json.WritePropertyName("stack");
        json.WriteStartArray();
        foreach (var frame in stack)
        {
            json.WriteStartObject();
            json.WriteString("function", frame.Name);
            if (FSharpOption<Tuple<string, int>>.get_IsSome(frame.Place))
            {
                var (file, line) = frame.Place.Value;
                json.WriteString("file", file);
                json.WriteNumber("line", line);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// The <c>summary</c> object, last: whether the tests were built and run, how many passed and failed, and the seed
    /// they ran with, when they ran.
    /// </summary>
    private static void WriteJsonSummary(TextWriter writer, bool built, int passed, int failed, long? seed) =>
        WriteJsonLine(writer, json =>
        {
            json.WriteString("type", "summary");
            json.WriteBoolean("built", built);
            json.WriteNumber("passed", passed);
            json.WriteNumber("failed", failed);
            if (seed is { } chosen)
            {
                json.WriteNumber("seed", chosen);
            }
        });
}
