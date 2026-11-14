using System.Text;

/// <summary>
/// The open-sided box <c>nanyx run</c> draws on a terminal around what the compiler writes, so that the program's output,
/// which follows it unmarked, stands apart:
/// <code>
/// ╭─ nanyx run emails ──────────────
/// │ Compiled emails (3 files) in 1.9s
/// │ src/main.nyx:3:9: error NYX0201: ...
/// │ Running with 1 deferred error
/// ╰─ output ────────────────────────
/// </code>
/// The rules span the terminal's width and the lines have no right border, so a long line or a resized terminal can't
/// break it. On a terminal in colour, the gutter, the rules and the compiler's own lines are dimmed; diagnostics keep their
/// colours, and since every line shifts by the gutter's two columns, their underlines stay aligned.
/// <para>
/// The box opens only when the compiler has more to say than its stopwatch's line. Until then, the stopwatch's final
/// line is left open (see <see cref="Pending"/>): if nothing more is written, closing the box ends it, and a plain run
/// writes exactly what it would without a box; if something is, the box's top rule is written over it and the line moves
/// into the box. Give <see cref="Lines"/> to what writes the compiler's lines, and close the box with
/// <see cref="Close"/> before the program runs. Once closed, both writers pass what they are given straight through.
/// </para>
/// </summary>
internal sealed class RunBox : IDisposable
{
    private const string Dim = "\u001b[2m";
    private const string Reset = "\u001b[0m";
    private const string Gutter = "│ ";

    private readonly string _title;
    private readonly Func<int> _width;
    private readonly bool _rounded;
    private string? _pending;
    private bool _open;
    private bool _closed;

    /// <summary>
    /// A box titled <paramref name="title"/> on <paramref name="terminal"/>, its rules as wide as
    /// <paramref name="width"/> says the terminal is, dimmed with <paramref name="colour"/>, with rounded corners when
    /// <paramref name="rounded"/>.
    /// </summary>
    internal RunBox(TextWriter terminal, string title, Func<int> width, bool colour, bool rounded = true)
    {
        Screen = terminal;
        _title = title;
        _width = width;
        InColour = colour;
        _rounded = rounded;
        Lines = new Writer(this, dimmed: true);
        Diagnostics = new Writer(this, dimmed: false);
    }

    /// <summary>
    /// The box for <c>nanyx {what}</c> ("run emails") on <paramref name="shell"/>'s stderr when that is the process's,
    /// and a terminal; otherwise null, and nothing the compiler writes changes.
    /// </summary>
    public static RunBox? For(Shell shell, string what) =>
        shell.Error == Console.Error && Terminal.IsTerminal(Console.Error)
            ? new RunBox(Console.Error, $"nanyx {what}", () => Terminal.Size().Width, Terminal.InColour(Console.Error), CanWrite(Console.Error.Encoding, "╭╰"))
            : null;

    /// <summary>Where the box is drawn.</summary>
    public TextWriter Screen { get; }

    /// <summary>Whether the box is dimmed, and what is written in it may be coloured.</summary>
    public bool InColour { get; }

    /// <summary>Whether the box's top rule has been written.</summary>
    public bool IsOpen => _open;

    /// <summary>Writes the compiler's own lines into the box, dimmed, opening it first.</summary>
    public TextWriter Lines { get; }

    /// <summary>Writes diagnostics into the box, with only the gutter dimmed, opening it first.</summary>
    public TextWriter Diagnostics { get; }

    /// <summary>"╭─ nanyx run emails ───…", <paramref name="width"/> columns but one, or the title's own width if that's more.</summary>
    public static string Top(string title, int width, bool colour, bool rounded = true) =>
        Rule($"{(rounded ? '╭' : '┌')}─ {title} ", width, colour);

    /// <summary>"╰─ output ───…", or a plain rule without <paramref name="label"/>, as wide as <see cref="Top"/>.</summary>
    public static string Bottom(string? label, int width, bool colour, bool rounded = true) =>
        Rule($"{(rounded ? '╰' : '└')}{(label is null ? "" : $"─ {label} ")}", width, colour);

    /// <summary>
    /// Whether <paramref name="encoding"/> can write <paramref name="text"/>. A Windows console's code page is often 437,
    /// which has the square corners ┌ and └ but not the rounded ╭ and ╰, so the box falls back to the square ones there.
    /// </summary>
    public static bool CanWrite(Encoding encoding, string text) => encoding.GetString(encoding.GetBytes(text)) == text;

    /// <summary>
    /// <paramref name="line"/> in the box: after the gutter, and dimmed with it when <paramref name="dimmed"/>, as the
    /// compiler's own lines are.
    /// </summary>
    public static string Line(string line, bool dimmed, bool colour) =>
        !colour ? Gutter + line
        : dimmed ? $"{Dim}{Gutter}{line}{Reset}"
        : $"{Dim}{Gutter}{Reset}{line}";

    /// <summary>The columns the gutter takes from each line in the box.</summary>
    public static int GutterWidth => Gutter.Length;

    private static string Rule(string start, int width, bool colour)
    {
        // One column short of the width, as the live view's lines are, so that the rule never wraps.
        var rule = start + new string('─', Math.Max(0, width - 1 - start.Length));
        return colour ? $"{Dim}{rule}{Reset}" : rule;
    }

    /// <summary>
    /// Notes that the terminal's current line is <paramref name="line"/>, the stopwatch's last, with the cursor after it
    /// and no line ending yet: the box takes the line in if it opens, and ends it if it closes first.
    /// </summary>
    public void Pending(string line)
    {
        if (!_closed)
        {
            _pending = line;
        }
    }

    /// <summary>Writes the box's top rule, over the stopwatch's line if that is pending, which then moves into the box.</summary>
    public void Open()
    {
        if (_open || _closed)
        {
            return;
        }

        _open = true;
        var top = Top(_title, _width(), InColour, _rounded);
        if (_pending is null)
        {
            Screen.WriteLine(top);
            return;
        }

        var padding = Math.Max(0, _pending.Length - (_width() - 1));
        Screen.Write($"\r{top}{new string(' ', padding)}");
        Screen.WriteLine();
        Screen.WriteLine(Line(_pending, dimmed: true, InColour));
        _pending = null;
    }

    /// <summary>
    /// Ends the box: its bottom rule, reading <paramref name="label"/>, if it is open, or else the stopwatch's line if
    /// that is pending. A second call does nothing.
    /// </summary>
    public void Close(string? label)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (_open)
        {
            Screen.WriteLine(Bottom(label, _width(), InColour, _rounded));
        }
        else if (_pending is not null)
        {
            Screen.WriteLine();
        }

        _pending = null;
    }

    /// <summary>Ends the box with a plain rule if nothing closed it, as when the program doesn't run.</summary>
    public void Dispose() => Close(null);

    /// <summary>A writer that puts each line it is given in the box, or passes it straight through once the box is closed.</summary>
    internal sealed class Writer : TextWriter
    {
        private readonly bool _dimmed;
        private readonly StringBuilder _line = new();

        public Writer(RunBox box, bool dimmed)
        {
            Box = box;
            _dimmed = dimmed;
        }

        /// <summary>The box this writes into.</summary>
        public RunBox Box { get; }

        public override Encoding Encoding => Box.Screen.Encoding;

        public override void Write(char value)
        {
            if (Box._closed)
            {
                Box.Screen.Write(value);
                return;
            }

            // Lines are written whole, each when it ends; a carriage return is part of the line ending.
            if (value == '\n')
            {
                Box.Open();
                Box.Screen.WriteLine(Line(_line.ToString(), _dimmed, Box.InColour));
                _line.Clear();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }
    }
}
