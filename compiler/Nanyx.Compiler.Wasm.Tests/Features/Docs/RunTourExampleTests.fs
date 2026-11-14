/// Every ```nanyx block in the language tour (docs/src/content/tour) and the standard library's pages
/// (docs/src/content/stdlib), compiled and run as `nanyx run` runs it, so that they keep describing what the compiler
/// does. The words after `nanyx` on a block's opening fence say what kind of example it is; the conventions are in
/// docs/src/content/work-in-progress/compilation.md#tour-examples. A standard-library page's generated reference is
/// left out: its blocks are signatures, and its doc comments' examples are checked where they are written.
module TranspilerWasmTests.Features.Docs.RunTourExamples

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

/// The ```nanyx blocks of `page` in the section `section` (`tour` or `stdlib`), by the line of their opening fence,
/// but for those in a standard-library page's generated reference.
let private examplesOf (section: string) (page: string) =
    let path = Path.Combine(docsContentDirectory, section, page)
    let lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n')
    let generated =
        match Array.IndexOf(lines, StandardLibraryDocs.beginMarker), Array.IndexOf(lines, StandardLibraryDocs.endMarker) with
        | start, finish when start >= 0 && finish > start -> fun line -> line > start && line <= finish
        | _ -> fun _ -> false
    docExamplesOf path |> Map.filter (fun line _ -> not (generated line))

let private examplesIn (section: string) : obj array seq =
    Directory.GetFiles(Path.Combine(docsContentDirectory, section), "*.md")
    |> Array.sort
    |> Seq.collect (fun path ->
        let page = Path.GetFileName path
        examplesOf section page |> Map.keys |> Seq.map (fun line -> [| box page; box line |]))

let tourExamples () : obj array seq = examplesIn "tour"

let standardLibraryExamples () : obj array seq = examplesIn "stdlib"

/// The tour's examples print with `nanyx/console` and work on strings with `nanyx/text` without importing them, as
/// though each imported them as the Hello world page does: after its directives and its module declaration, if it has
/// them, and before its first line of code.
let private withImports (code: string) =
    let imports =
        [ "nanyx/console"; "nanyx/text" ]
        |> List.filter (fun name -> not (code.Contains $"import {name}"))
        |> List.map (fun name -> $"import {name}")
    let lines = code.Split('\n') |> List.ofArray
    let isPreamble (line: string) =
        let line = line.Trim()
        line = "" || line.StartsWith "--" || line.StartsWith "::" || line.StartsWith "#!"
    let preamble = lines |> List.takeWhile isPreamble
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
    (Script.compileSource path (withImports code)).Diagnostics
    |> List.filter Diagnostics.isError
    |> List.map _.Code
    |> List.distinct
    |> List.sort

/// Compiles and runs the example at `line` of `page` in `section`, and checks it prints what it says, or reports the
/// errors it says.
let private check (section: string) (page: string) (line: int) =
    let example = examplesOf section page |> Map.find line
    match example.Kind, expectedErrors example.Code with
    | Fragment, _
    | Skip _, _ -> ()
    | Program, [] ->
        let outcome = runSourceWith false "" (withImports example.Code)
        Assert.True((outcome.ExitCode = 0 && outcome.StandardError = ""), $"{page}:{line} failed with exit code {outcome.ExitCode}:\n{outcome.StandardError}")
        let printed = outcome.StandardOutput.ReplaceLineEndings("\n").TrimEnd()
        let expected = expectedOutput example.Code
        if printed <> "" && expected <> "" then
            Assert.Equal(expected, printed)
    | Program, expected -> Assert.Equal<string list>(expected, errorCodes example.Code)

[<Theory>]
[<MemberData(nameof tourExamples)>]
let ``Tour example compiles and runs`` (page: string, line: int) = check "tour" page line

[<Theory>]
[<MemberData(nameof standardLibraryExamples)>]
let ``Standard library page example compiles and runs`` (page: string, line: int) = check "stdlib" page line
