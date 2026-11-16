using System.Globalization;
using Nanyx.Runtime;
using Transpiler.Wasm;

/// <summary>What <c>nanyx run</c> checks of the program's memory as it runs.</summary>
internal enum MemoryCheck
{
    /// <summary>Nothing: the program is compiled with the allocator that counts nothing.</summary>
    Off,

    /// <summary><c>--check-memory</c>: stop at a block freed twice or an object used after it was freed, and fail if
    /// anything is left on the heap at the end.</summary>
    Check,

    /// <summary><c>--memory</c>: the checks of <see cref="Check"/>, and the report of <see cref="MemoryReport"/> once the
    /// program has stopped.</summary>
    Report
}

/// <summary>
/// The report <c>nanyx run --memory</c> writes to stderr once the program has stopped, from the counters of the checked
/// allocator (see <c>CodeGen.CheckedExports</c>):
/// <code>
/// Memory
///   allocations        1,204
///   frees              1,204
///   live at exit       0
///   reused in place    310
///   bytes allocated    48,160
///   peak live bytes    6,240
///   peak heap          8,192 bytes (1 page)
/// </code>
/// Bytes are the blocks' sizes, without the word before each that holds its size. The peak heap is the highest the top
/// of the heap reached, as an address, so it counts the string literals and the free lists' table below the heap too;
/// the pages are how big the memory grew.
/// </summary>
internal static class MemoryReport
{
    /// <summary>The counters the report reads, in the order <see cref="Write"/> takes their values.</summary>
    public static string[] Counters => CodeGen.CheckedExports.counters.ToArray();

    /// <summary>Writes the report of <paramref name="counters"/>, read with <see cref="Counters"/>, to <paramref name="writer"/>.</summary>
    public static void Write(TextWriter writer, HeapCounters counters)
    {
        long Counter(string name) => counters.Globals[Array.IndexOf(Counters, name)];

        var allocations = Counter(CodeGen.CheckedExports.allocations);
        var live = Counter(CodeGen.CheckedExports.liveBlocks);
        var pages = counters.MemoryBytes / 65536;
        writer.WriteLine("Memory");
        foreach (var (label, value) in new (string, string)[]
        {
            ("allocations", Number(allocations)),
            ("frees", Number(allocations - live)),
            ("live at exit", Number(live)),
            ("reused in place", Number(Counter(CodeGen.CheckedExports.reusedInPlace))),
            ("bytes allocated", Number(Counter(CodeGen.CheckedExports.bytesAllocated))),
            ("peak live bytes", Number(Counter(CodeGen.CheckedExports.peakLiveBytes))),
            ("peak heap", $"{Number(Counter(CodeGen.CheckedExports.peakHeap))} bytes ({Number(pages)} page{(pages == 1 ? "" : "s")})"),
        })
        {
            writer.WriteLine($"  {label,-18} {value}");
        }
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
