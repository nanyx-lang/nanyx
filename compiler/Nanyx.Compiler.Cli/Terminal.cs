using System.Runtime.InteropServices;

/// <summary>Whether what the CLI writes reaches a terminal, and whether that terminal takes ANSI escapes.</summary>
internal static class Terminal
{
    /// <summary>
    /// Whether <paramref name="writer"/> is the process's stdout or stderr, and that stream is a terminal rather than a
    /// file or a pipe.
    /// </summary>
    public static bool IsTerminal(TextWriter writer) =>
        (writer == Console.Out && !Console.IsOutputRedirected) || (writer == Console.Error && !Console.IsErrorRedirected);

    /// <summary>
    /// Whether <paramref name="writer"/> is a terminal (see <see cref="IsTerminal"/>) that takes ANSI escapes, turning on
    /// virtual terminal processing for a Windows console first.
    /// </summary>
    public static bool TakesEscapes(TextWriter writer)
    {
        if (!IsTerminal(writer))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        const int StandardOutput = -11;
        const int StandardError = -12;
        const uint VirtualTerminalProcessing = 0x0004;
        var handle = GetStdHandle(writer == Console.Out ? StandardOutput : StandardError);
        return GetConsoleMode(handle, out var mode)
            && ((mode & VirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | VirtualTerminalProcessing));
    }

    /// <summary>
    /// Whether to colour what is written to <paramref name="writer"/>: it is a terminal that takes ANSI escapes, and the
    /// <c>NO_COLOR</c> environment variable is not set (no-color.org).
    /// </summary>
    public static bool InColour(TextWriter writer) =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) && TakesEscapes(writer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
