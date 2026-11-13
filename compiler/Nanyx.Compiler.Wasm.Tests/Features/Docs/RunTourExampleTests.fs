/// Every ```nanyx block in the language tour (docs/src/content/tour), compiled and run as `nanyx run` runs it, so that
/// the tour keeps describing what the compiler does. The words after `nanyx` on a block's opening fence say what kind
/// of example it is; the conventions are in docs/src/content/work-in-progress/compilation.md#tour-examples.
module TranspilerWasmTests.Features.Docs.RunTourExamples

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

let private tourDirectory = Path.Combine(docsContentDirectory, "tour")

/// The ```nanyx blocks of `page`, by the line of their opening fence.
let private examplesOf (page: string) = docExamplesOf (Path.Combine(tourDirectory, page))

let tourExamples () : obj array seq =
    Directory.GetFiles(tourDirectory, "*.md")
    |> Array.sort
    |> Seq.collect (fun path ->
        let page = Path.GetFileName path
        examplesOf page |> Map.keys |> Seq.map (fun line -> [| box page; box line |]))

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

[<Theory>]
[<MemberData(nameof tourExamples)>]
let ``Tour example compiles and runs`` (page: string, line: int) =
    let example = examplesOf page |> Map.find line
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
