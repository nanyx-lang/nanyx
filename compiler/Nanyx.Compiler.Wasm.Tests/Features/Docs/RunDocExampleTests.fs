/// Every ```nanyx block on the docs site's pages (docs/src/content), compiled and run as `nanyx run` runs it, so that
/// they keep describing what the compiler does. The words after `nanyx` on a block's opening fence say what kind of
/// example it is; the conventions are in docs/src/content/deep-dives/compilation.md#docs-examples. A standard-library
/// page's generated reference is left out: its blocks are signatures, and its doc comments' examples are checked where
/// they are written. So is the error reference, whose examples report errors (`ErrorReferenceTests.fs`). The home page's
/// examples (docs/src/home/*.nyx) are whole programs, run as they are.
module TranspilerWasmTests.Features.Docs.RunDocExamples

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// The ```nanyx blocks of `page` in the section `section` (the directory under docs/src/content, such as `tour`), by the
/// line of their opening fence, but for those in a standard-library page's generated reference.
let private readExamples (section: string, page: string) =
    let path = Path.Combine(docsContentDirectory, section, page)
    let text = File.ReadAllText path
    let lines = text.ReplaceLineEndings("\n").Split('\n')
    let beginMarker = StandardLibraryDocs.beginMarkerOf (StandardLibraryDocs.pinOf text |> Result.defaultValue None)
    let generated =
        match Array.IndexOf(lines, beginMarker), Array.IndexOf(lines, StandardLibraryDocs.endMarker) with
        | start, finish when start >= 0 && finish > start -> fun line -> line > start && line <= finish
        | _ -> fun _ -> false
    docExamplesOf path |> Map.filter (fun line _ -> not (generated line))

/// The pages read so far, each read once, since each of its examples is a test of its own and they run side by side.
let private pages = System.Collections.Concurrent.ConcurrentDictionary<string * string, Lazy<Map<int, DocExample>>>()

/// `readExamples`, of each page once.
let private examplesOf (section: string) (page: string) =
    pages.GetOrAdd((section, page), fun key -> lazy (readExamples key)).Value

/// The error reference, whose examples `ErrorReferenceTests.fs` checks.
let private errorReference = Path.Combine("standard-tools", "errors.md")

/// Every example on every page of the sections `included` says, by its section, its page and its line. The sections are
/// split among the modules of `RunDocExampleSectionTests.fs`, each a test collection of its own, so that they run side
/// by side: an example's rows run one after another (#2553).
let docExamplesIn (included: string -> bool) : obj array seq =
    Directory.GetDirectories docsContentDirectory
    |> Array.sort
    |> Seq.filter (Path.GetFileName >> included)
    |> Seq.collect (fun directory ->
        let section = Path.GetFileName directory
        Directory.GetFiles(directory, "*.md")
        |> Array.sort
        |> Seq.filter (fun path -> Path.GetRelativePath(docsContentDirectory, path) <> errorReference)
        |> Seq.collect (fun path ->
            let page = Path.GetFileName path
            examplesOf section page |> Map.keys |> Seq.map (fun line -> [| box section; box page; box line |])))

/// The docs site's home page's examples, the `.nyx` files in docs/src/home.
let private homePageDirectory = Path.GetFullPath(Path.Combine(docsContentDirectory, "..", "home"))

let homePageExamples () : obj array seq =
    Directory.GetFiles(homePageDirectory, "*.nyx") |> Array.sort |> Seq.map (fun path -> [| box (Path.GetFileName path) |])

/// A docs example works on strings with `nanyx/text` without importing it, as though it imported it: after its
/// directives and its module declaration, if it has them, and before its first line of code. It prints with the
/// console host's prelude, which every program sees.
let private withImports (code: string) =
    // An example that turns a linter on gets no import it doesn't write, which the linter would report unused.
    let imports =
        [ "nanyx/text" ]
        |> List.filter (fun name -> not (code.Contains $"import {name}") && not (code.Contains "::linter"))
        |> List.map (fun name -> $"import {name}")
    let lines = code.Split('\n') |> List.ofArray
    let isPreamble (line: string) =
        let line = line.Trim()
        line = "" || line.StartsWith "--" || line.StartsWith "::" || line.StartsWith "#!"
    // A doc comment's other lines are indented under its `--|`, and a module's comes before its declaration.
    let rec preambleOf (inDocComment: bool) (lines: string list) =
        match lines with
        | line :: rest when isPreamble line ->
            line :: preambleOf (if line.Trim() = "" then inDocComment else line.TrimStart().StartsWith "--|") rest
        | line :: rest when inDocComment && line.StartsWith " " -> line :: preambleOf true rest
        | _ -> []
    let preamble = preambleOf false lines
    let rest = lines |> List.skip preamble.Length
    let declaration, body =
        match rest with
        | first :: body when first.StartsWith "module " && not (first.Contains "=") -> [ first ], body
        | _ -> [], rest
    String.concat "\n" (preamble @ declaration @ imports @ body)

/// The codes of the errors a block says it shows, in comments such as `-- error NYX0201: ...`.
let private expectedErrors (code: string) =
    Regex.Matches(code, @"--.*\berror (NYX\d{4})", RegexOptions.IgnoreCase) |> Seq.map _.Groups[1].Value |> Seq.distinct |> Seq.sort |> List.ofSeq

/// What a block that ends with comments says it prints: those comments, without their `-- `.
let private expectedOutput (code: string) =
    code.TrimEnd().Split('\n')
    |> Array.rev
    |> Array.takeWhile _.StartsWith("--")
    |> Array.rev
    |> Array.map (fun line -> line.Substring(2).TrimStart())
    |> String.concat "\n"

let private errorCodes (code: string) =
    let path = Path.Combine(Path.GetTempPath(), $"nanyx-tour-{Guid.NewGuid():N}.nyx")
    (Script.compileSourceReportingIn (testStore ()) CompileListener.none path (withImports code)).Diagnostics
    |> List.filter Diagnostics.isError
    |> List.map _.Code
    |> List.distinct
    |> List.sort

/// Compiles and runs the example at `line` of `page` in `section`, and checks it prints what it says, or reports the
/// errors it says.
let check (section: string) (page: string) (line: int) =
    let example = examplesOf section page |> Map.find line
    let page = $"{section}/{page}"
    match example.Kind, expectedErrors example.Code with
    | Fragment, _
    | Skip _, _
    | Unformatted, _ -> ()
    | Program, [] ->
        let outcome, notInlined = runSourceBothWays (withImports example.Code)
        Assert.True((outcome.ExitCode = 0 && outcome.StandardError = ""), $"{page}:{line} failed with exit code {outcome.ExitCode}:\n{outcome.StandardError}")
        let printed = outcome.StandardOutput.ReplaceLineEndings("\n").TrimEnd()
        let expected = expectedOutput example.Code
        if printed <> "" && expected <> "" then
            Assert.Equal(expected, printed)
        // Inlining changes no behaviour: emitted without it, the example prints the same (see `CoreIrInline`).
        Assert.Equal(outcome.ExitCode, notInlined.ExitCode)
        Assert.Equal(outcome.StandardOutput, notInlined.StandardOutput)
    | Program, expected -> Assert.Equal<string list>(expected, errorCodes example.Code)

/// A home page example runs as it is, with no import added, since the page's "Run in the playground" link opens it so,
/// and prints what its last comments say.
[<Theory>]
[<MemberData(nameof homePageExamples)>]
let ``Home page example compiles and runs`` (file: string) =
    let code = File.ReadAllText(Path.Combine(homePageDirectory, file)).ReplaceLineEndings "\n"
    let outcome = runSourceWith false "" code
    Assert.True((outcome.ExitCode = 0 && outcome.StandardError = ""), $"{file} failed with exit code {outcome.ExitCode}:\n{outcome.StandardError}")
    Assert.Equal(expectedOutput code, outcome.StandardOutput.ReplaceLineEndings("\n").TrimEnd())
