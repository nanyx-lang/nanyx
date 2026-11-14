using System.Diagnostics;
using System.Text;
using Microsoft.FSharp.Core;
using NyxCompiler;

/// <summary>
/// A stopwatch that runs on stderr while the compiler works, so a slow compile shows which file it is on, which module
/// that file's compile is checking, and for how long: <c>Compiling src/main.nyx › nanyx/list… 4.7s</c>, rewritten in
/// place ten times a second, then replaced by <c>Compiled greeter (12 files) in 4.7s</c> when it finishes. It writes
/// only when stderr is a terminal: a log or a pipe sees nothing, so output under CI and in tests is unchanged. With
/// <c>timings</c>, it also records how long each module's check took and writes them as a tree to stderr after the final
/// line, whether or not stderr is a terminal; on a terminal that can be redrawn with ANSI escapes, the stopwatch's line
/// is then the live view of that tree instead (<see cref="ModuleCheckModule.live"/>), erased when the compile finishes.
/// Given the writer of a run's <see cref="RunBox"/> for stderr, it draws inside the box: from the start with timings,
/// since the tree will follow, and otherwise leaves its final line for the box to take in if the box opens.
/// Stop it with <see cref="Finish"/> before anything else is written, or the next line lands on top of the stopwatch.
/// Give the compiler <see cref="Listener"/> to hear from it.
/// </summary>
internal sealed class CompileProgress : CompileListener, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly TextWriter _errors;
    private readonly TextWriter? _terminal;
    private readonly RunBox? _box;
    private readonly Func<string, string> _display;
    private readonly string _what;
    private readonly CompileTimings? _timings;
    private readonly bool _live;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly List<string> _checking = new();
    private Timer? _timer;
    private string _current;
    private int _written;
    private int _drawn;
    private bool _finished;

    private CompileProgress(TextWriter errors, string what, Func<string, string> display, bool timings)
    {
        _errors = errors;
        _box = (errors as RunBox.Writer)?.Box;
        var screen = _box?.Screen ?? errors;
        _terminal = screen == Console.Error && Terminal.IsTerminal(screen) ? screen : null;
        _display = display;
        _what = what;
        _current = what;
        _timings = timings ? new CompileTimings(this) : null;
        _live = _timings is not null && _terminal is not null && Terminal.TakesEscapes(_terminal);
        // The tree of module checks will follow the stopwatch's line, so the run's box is wanted from the start.
        if (_timings is not null)
        {
            _box?.Open();
        }

        if (_terminal is not null)
        {
            _timer = new Timer(_ => Redraw(), null, Tick, Tick);
        }
    }

    /// <summary>
    /// Starts the stopwatch for a compile of <paramref name="what"/> ("greeter (12 files)", "main.nyx"), shown on
    /// <paramref name="errors"/> when that is a terminal; the stopwatch writes nothing when it is any other writer.
    /// <paramref name="display"/> shortens the paths the compiler names for the user. With <paramref name="timings"/>,
    /// the stopwatch is the live view of the modules' checks, and <see cref="Finish"/> writes each module's time to
    /// <paramref name="errors"/>.
    /// </summary>
    public static CompileProgress Start(TextWriter errors, string what, Func<string, string> display, bool timings = false) =>
        new(errors, what, display, timings);

    /// <summary>What the compiler reports to: the stopwatch itself, through the timings when they are recorded.</summary>
    public CompileListener Listener => _timings ?? (CompileListener)this;

    /// <summary>How long the compile has taken so far, or took: "0.3s", "4.7s".</summary>
    public string Elapsed => $"{_stopwatch.Elapsed.TotalSeconds:0.0}s";

    /// <summary>Notes that the compile has moved on to <paramref name="file"/>, which the stopwatch names from now on.</summary>
    public void FileStarted(string file)
    {
        lock (_gate)
        {
            _current = _display(file);
            _checking.Clear();
        }

        Redraw();
    }

    /// <summary>Notes that the check of <paramref name="name"/> has started, which the stopwatch names after the file.</summary>
    public void ModuleStarted(string name)
    {
        lock (_gate)
        {
            _checking.Add(_display(name));
        }

        Redraw();
    }

    /// <summary>Notes that the check of <paramref name="name"/> has finished, so the stopwatch names its importer again.</summary>
    public void ModuleFinished(string name, TimeSpan elapsed)
    {
        lock (_gate)
        {
            var index = _checking.LastIndexOf(_display(name));
            if (index >= 0)
            {
                _checking.RemoveRange(index, _checking.Count - index);
            }
        }

        Redraw();
    }

    /// <summary>A module already checked costs nothing, so the stopwatch has nothing to show for it.</summary>
    public void ModuleReused(string name)
    {
    }

    /// <summary>
    /// Stops the stopwatch and leaves "Compiled {what} in {elapsed}" where it was, erasing the live view if there was
    /// one, then the tree of module checks when timings are recorded. A second call does nothing.
    /// </summary>
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

        lock (_gate)
        {
            if (_terminal is not null)
            {
                if (_live)
                {
                    Draw(Array.Empty<string>());
                }

                var compiled = Fitted([$"Compiled {_what} in {Elapsed}"], Terminal.Size().Width)[0];
                Overwrite(compiled);
                if (_box is { IsOpen: false })
                {
                    // Whether the box is wanted depends on what the compiler writes next, so the line is left open.
                    _box.Pending(compiled);
                }
                else
                {
                    _terminal.WriteLine();
                }

                _written = 0;
            }

            if (_timings is not null)
            {
                _errors.WriteLine(ModuleCheckModule.tree(FuncConvert.FromFunc(_display), _timings.Checks));
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

            if (_live)
            {
                var (width, height) = Terminal.Size();
                var frame = ModuleCheckModule.live(FuncConvert.FromFunc(_display), _timings!.SoFar, height - 1).ToList();
                // Until the first module's check starts there is no tree, so the stopwatch says what is being compiled.
                Draw(Fitted(frame.Count > 0 ? frame : [$"Compiling {_current}… {Elapsed}"], width));
                return;
            }

            // The file's own module is checked first, under its own name: only its imports are worth a mention.
            var imports = string.Concat(_checking.Where(name => name != _current).Select(name => $" › {name}"));
            Overwrite(Fitted([$"Compiling {_current}{imports}… {Elapsed}"], Terminal.Size().Width)[0]);
        }
    }

    /// <summary><paramref name="line"/> as the stopwatch shows it: in the run's box, dimmed, once that is open.</summary>
    private string InBox(string line) => _box is { IsOpen: true } ? RunBox.Line(line, dimmed: true, _box.InColour) : line;

    /// <summary>
    /// <paramref name="lines"/> each cut to the terminal's <paramref name="width"/>, less the box's gutter when they are in
    /// it, so that none wraps: a carriage return or the next frame could then only redraw its last row.
    /// </summary>
    private IReadOnlyList<string> Fitted(IEnumerable<string> lines, int width)
    {
        var room = _box is { IsOpen: true } ? width - RunBox.GutterWidth : width;
        return lines.Select(line => InBox(line.Length < room ? line : line[..Math.Max(0, room - 1)])).ToList();
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

    /// <summary>
    /// Replaces the live view with <paramref name="lines"/>, each already cut to fit the terminal (see
    /// <see cref="Fitted"/>), leaving the cursor at the end of the last. With no lines, the view is erased and the cursor
    /// left where its first line began. Call under the gate.
    /// </summary>
    private void Draw(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder("\r");
        if (_drawn > 1)
        {
            text.Append($"\u001b[{_drawn - 1}A");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            text.Append(index > 0 ? "\n" : "").Append(line).Append("\u001b[K");
        }

        text.Append("\u001b[J");
        _terminal!.Write(text.ToString());
        _drawn = lines.Count;
    }
}
