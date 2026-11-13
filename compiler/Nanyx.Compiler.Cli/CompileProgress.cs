using System.Diagnostics;

/// <summary>
/// A stopwatch that runs on stderr while the compiler works, so a slow compile shows which file it is on and for how
/// long: <c>Compiling src/main.nyx… 4.7s</c>, rewritten in place ten times a second, then replaced by
/// <c>Compiled greeter (12 files) in 4.7s</c> when it finishes. It writes only when stderr is a terminal: a log or a
/// pipe sees nothing, so output under CI and in tests is unchanged. Stop it with <see cref="Finish"/> before anything
/// else is written, or the next line lands on top of the stopwatch.
/// </summary>
internal sealed class CompileProgress : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly TextWriter? _terminal;
    private readonly string _what;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _gate = new();
    private Timer? _timer;
    private string _current;
    private int _written;
    private bool _finished;

    private CompileProgress(TextWriter? terminal, string what)
    {
        _terminal = terminal;
        _what = what;
        _current = what;
        if (terminal is not null)
        {
            _timer = new Timer(_ => Redraw(), null, Tick, Tick);
        }
    }

    /// <summary>
    /// Starts the stopwatch for a compile of <paramref name="what"/> ("greeter (12 files)", "main.nyx"), shown on
    /// <paramref name="errors"/> when that is a terminal; the stopwatch writes nothing when it is any other writer.
    /// </summary>
    public static CompileProgress Start(TextWriter errors, string what) =>
        new(errors == Console.Error && !Console.IsErrorRedirected ? errors : null, what);

    /// <summary>How long the compile has taken so far, or took: "0.3s", "4.7s".</summary>
    public string Elapsed => $"{_stopwatch.Elapsed.TotalSeconds:0.0}s";

    /// <summary>Notes that the compile has moved on to <paramref name="file"/>, which the stopwatch names from now on.</summary>
    public void Compiling(string file)
    {
        lock (_gate)
        {
            _current = file;
        }

        Redraw();
    }

    /// <summary>Stops the stopwatch and leaves "Compiled {what} in {elapsed}" where it was. A second call does nothing.</summary>
    public void Finish()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _stopwatch.Stop();
            timer = _timer;
            _timer = null;
        }

        // Disposing with a handle waits for a tick that is being drawn, so nothing is drawn after the final line.
        if (timer is not null)
        {
            using var done = new ManualResetEvent(false);
            timer.Dispose(done);
            done.WaitOne();
        }

        if (_terminal is not null)
        {
            lock (_gate)
            {
                Overwrite($"Compiled {_what} in {Elapsed}");
                _terminal.WriteLine();
                _written = 0;
            }
        }
    }

    public void Dispose() => Finish();

    private void Redraw()
    {
        if (_terminal is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_timer is null)
            {
                return;
            }

            Overwrite($"Compiling {_current}… {Elapsed}");
        }
    }

    /// <summary>Replaces the stopwatch's line with <paramref name="text"/>, blanking what was longer. Call under the gate.</summary>
    private void Overwrite(string text)
    {
        var padding = Math.Max(0, _written - text.Length);
        _terminal!.Write($"\r{text}{new string(' ', padding)}");
        if (padding > 0)
        {
            _terminal.Write($"\r{text}");
        }

        _written = text.Length;
    }
}
