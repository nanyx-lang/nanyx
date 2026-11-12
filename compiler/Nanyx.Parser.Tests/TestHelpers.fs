module ParserTestHelpers

open System
open System.IO
open Parser.Program

// Helper to check if Result is Ok
let isOk result =
    match result with
    | Result.Ok _ -> true
    | Result.Error _ -> false

// Helper to check if Result is Error
let isError result =
    match result with
    | Result.Ok _ -> false
    | Result.Error _ -> true

// The error `source` fails to parse with: its message, its lines (which FParsec wraps) joined into one, and the text
// from where it is reported to the end of that line.
let parseErrorOf (source: string) =
    match parseModuleFileWithNames "t.nyx" source with
    | Result.Error error ->
        let rest = source.Substring(error.Span.Start)
        let lineEnd = rest.IndexOf '\n'
        String.Join(" ", error.Message.Split([| ' '; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)),
        (if lineEnd < 0 then rest else rest.Substring(0, lineEnd))
    | Result.Ok parsed -> failwith $"Expected a parse error, got %A{parsed}"

let private testRootDir = __SOURCE_DIRECTORY__
let private featureRootDir = Path.Combine(testRootDir, "Features")

// Helper to load test file
let loadTestFile relativePath =
    let directPath = Path.Combine(testRootDir, relativePath)
    if File.Exists(directPath) then
        File.ReadAllText(directPath)
    else
        let searchRoot =
            if Directory.Exists(featureRootDir) then featureRootDir else testRootDir
        let fileName = Path.GetFileName(relativePath)
        let matches = Directory.GetFiles(searchRoot, fileName, SearchOption.AllDirectories)
        if matches.Length = 0 then
            failwith $"Test data file not found: {relativePath}"
        File.ReadAllText(matches.[0])

// Helper to parse test file
let parseTestFile relativePath =
    loadTestFile relativePath |> parseModule

let parseFeatureNyxFiles featureName =
    let featureDir = Path.Combine(featureRootDir, featureName)
    if not (Directory.Exists(featureDir)) then
        failwith $"Feature directory not found: {featureName}"

    Directory.GetFiles(featureDir, "*.nyx", SearchOption.AllDirectories)
    |> Array.map (fun filePath -> filePath, File.ReadAllText(filePath) |> parseModule)
