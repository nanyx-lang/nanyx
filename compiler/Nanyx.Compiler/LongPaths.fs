namespace NyxCompiler

open System
open System.Runtime.InteropServices
open System.Text

/// Paths with each directory written by its long name. Windows gives a file or directory whose name is long or has
/// spaces a short 8.3 name too, `RICHAR~1` for `RichardGibson`, and a path may be written with either: a program started
/// through `C:\Users\RICHAR~1\...` sees its own directory spelt so. `Path.GetFullPath` keeps the short names, so two
/// spellings of one directory compare as two (#3116).
module LongPaths =
    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern uint32 private GetLongPathNameW(string shortPath, StringBuilder longPath, uint32 bufferLength)

    /// `path`, an existing file or directory, with every short name in it expanded to the long name, on Windows; `path`
    /// itself anywhere else, where there are no short names, and if it can't be expanded.
    let ofPath (path: string) : string =
        if not (OperatingSystem.IsWindows()) || not (path.Contains '~') then
            path
        else
            let rec expand (capacity: int) =
                let buffer = StringBuilder(capacity)
                let length = int (GetLongPathNameW(path, buffer, uint32 capacity))
                if length = 0 then path
                elif length >= capacity then expand (length + 1)
                else buffer.ToString()
            expand (path.Length + 260)
