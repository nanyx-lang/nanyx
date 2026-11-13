/// The error reference (docs/src/content/standard-tools/errors.md) has a section for every code in `DiagnosticCodes`,
/// headed with its code and title, and each ```nanyx block in a section is a program that reports that section's code.
/// The conventions are in docs/src/content/work-in-progress/error-messages.md#the-error-reference.
module TranspilerWasmTests.Features.Docs.ErrorReference

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open Transpiler.Wasm
open TranspilerWasmTestHelpers

let private pagePath = Path.Combine(docsContentDirectory, "standard-tools", "errors.md")

/// The page's sections, `# NYX0201: Type mismatch`, in order: the line of the heading, the code and the title.
let private sections () =
    File.ReadAllText(pagePath).ReplaceLineEndings("\n").Split('\n')
    |> Seq.indexed
    |> Seq.choose (fun (index, line) ->
        let heading = Regex.Match(line, @"^# (NYX\d{4}): (.+)$")
        if heading.Success then Some(index + 1, heading.Groups[1].Value, heading.Groups[2].Value) else None)
    |> List.ofSeq

/// The code of the section the line `line` is in.
let private sectionAt (line: int) =
    match sections () |> List.filter (fun (heading, _, _) -> heading < line) |> List.tryLast with
    | Some(_, code, _) -> code
    | None -> failwith $"errors.md:{line}: an example is in no code's section"

[<Fact>]
let ``Every code has a section, in order, with the compiler's title`` () =
    let expected = DiagnosticCodes.all |> List.map (fun code -> $"{code.Code}: {code.Title}")
    let actual = sections () |> List.map (fun (_, code, title) -> $"{code}: {title}")
    Assert.Equal<string list>(expected, actual)

[<Fact>]
let ``A code's reference link is its section of this page`` () =
    let relative = Path.GetRelativePath(docsContentDirectory, pagePath).Replace('\\', '/').Replace(".md", "")
    Assert.Equal(Some $"https://nanyx.dev/docs/{relative}#nyx0201", DiagnosticCodes.referenceUrl DiagnosticCodes.TypeMismatch.Code)
    Assert.Equal(None, DiagnosticCodes.referenceUrl "default/unusedBindings")

/// The codes whose sections have no example, because no program reports them: the file given to `nanyx` is missing, a
/// standard library module is broken, or the backend has a bug.
let private withoutExamples =
    [ DiagnosticCodes.FileNotFound; DiagnosticCodes.InvalidStandardLibraryModule; DiagnosticCodes.BackendInternalError ]
    |> List.map _.Code

[<Fact>]
let ``Every section has an example, unless no program reports its code`` () =
    let withExamples = docExamplesOf pagePath |> Map.keys |> Seq.map sectionAt |> Set.ofSeq
    let expected = DiagnosticCodes.all |> List.map _.Code |> List.filter (fun code -> not (List.contains code withoutExamples))
    Assert.Equal<string list>(expected, sections () |> List.map (fun (_, code, _) -> code) |> List.filter withExamples.Contains)

let errorExamples () : obj array seq =
    docExamplesOf pagePath |> Map.keys |> Seq.map (fun line -> [| box line |])

/// The files of an example: each starts with a line `-- file: <name>`, and the first is the one compiled. An example
/// with no such line is one file, `main.nyx`.
let private filesOf (code: string) =
    let lines = code.TrimEnd().Split('\n') |> List.ofArray
    let isMarker (line: string) = line.StartsWith "-- file: "
    let rec split (lines: string list) =
        match lines with
        | [] -> []
        | marker :: rest ->
            let body = rest |> List.takeWhile (isMarker >> not)
            (marker.Substring("-- file: ".Length).Trim(), String.concat "\n" body + "\n") :: split (rest |> List.skip body.Length)
    match lines with
    | first :: _ when isMarker first -> split lines
    | _ -> [ "main.nyx", code ]

/// The codes of the errors compiling `code` reports, with the backend's if the type checker reports none.
let private errorsOf (code: string) =
    use directory = new TempDirectory()
    let files = filesOf code
    for name, text in files do
        File.WriteAllText(Path.Combine(directory.Path, name), text)
    let name, text = List.head files
    let result = Script.compileSource (Path.Combine(directory.Path, name)) text
    let errors = result.Diagnostics |> List.filter Diagnostics.isError
    match errors, result.Typed with
    | [], Some typed ->
        match CodeGen.tryTranspileTypedModuleToWat typed with
        | Error diagnostic -> [ diagnostic ]
        | Ok _ -> []
    | _ -> errors

[<Theory>]
[<MemberData(nameof errorExamples)>]
let ``Error reference example reports its section's code`` (line: int) =
    let example = docExamplesOf pagePath |> Map.find line
    match example.Kind with
    | Fragment
    | Skip _ -> ()
    | Program ->
        let errors = errorsOf example.Code
        let reported = errors |> List.map _.Code |> List.distinct
        let shown = errors |> List.map (fun error -> $"{error.Code}: {error.Message}") |> String.concat "\n"
        Assert.True((reported = [ sectionAt line ]), $"errors.md:{line} should report only {sectionAt line}, but reported:\n{shown}")
