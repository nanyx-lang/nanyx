module Nanyx.Compiler.Tests.TestSupport

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program

/// Matches an `IntLit` whose value fits an `int64`, which a pattern can name: F# has no `bigint` constant patterns.
let (|IntLit64|_|) (literal: Literal) =
    match literal with
    | IntLit value when value >= bigint System.Int64.MinValue && value <= bigint System.Int64.MaxValue -> Some (int64 value)
    | _ -> None

let withTempDir (action: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        action dir
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)

let compileImporting (moduleSource: string) (mainSource: string) =
    let mutable result = Unchecked.defaultof<CompileResult>
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "mod.nyx"), moduleSource)
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, mainSource)
        result <- Compiler.compileFile mainPath)
    result

let compileImportingAll (modules: (string * string) list) (mainSource: string) =
    let mutable result = Unchecked.defaultof<CompileResult>
    withTempDir (fun dir ->
        for fileName, source in modules do
            File.WriteAllText(Path.Combine(dir, fileName), source)
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, mainSource)
        result <- Compiler.compileFile mainPath)
    result

/// `source` with the edits of `fix` applied.
let applyFix (source: string) (fix: Fix) =
    fix.Edits
    |> List.sortByDescending (fun edit -> edit.Span.Start)
    |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Span.Start) + edit.NewText + text.Substring(edit.Span.End)) source

/// The offset of the `occurrence`th (zero-based) appearance of `text` in `source`, plus `within` characters.
let offsetOf (source: string) (text: string) (occurrence: int) (within: int) =
    let rec find from remaining =
        let index = source.IndexOf(text, from, StringComparison.Ordinal)
        if index < 0 then failwith $"'{text}' not found"
        elif remaining = 0 then index
        else find (index + 1) (remaining - 1)
    find 0 occurrence + within

/// `source` compiled, with no diagnostics.
let compileClean (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value

/// The code of the one diagnostic `source` compiles to, and the text it is reported at.
let singleError (source: string) =
    let result = Compiler.compile source
    Assert.True(1 = result.Diagnostics.Length, $"Expected one diagnostic: %A{result.Diagnostics}")
    let diagnostic = result.Diagnostics.Head
    let span = diagnostic.Span.Value
    diagnostic.Code, source.ReplaceLineEndings("\n").Substring(span.Start, span.End - span.Start)

/// The one diagnostic `source` has, which must be a missing context: its message, and the text it is reported at.
let missingContext (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

/// The instances the calls in `typed`'s own module found, by each call's node. `ContextInstances` also holds those of
/// the definitions the module imports, the prelude's among them, for the backend.
let ownContextInstances (typed: TypedModule) =
    typed.ContextInstances |> Map.filter (fun node _ -> typed.ExprTypes.ContainsKey node)

/// Every instance the calls (and holes) in `typed`'s own module found, in the order they are written.
let instancesOf (typed: TypedModule) =
    ownContextInstances typed |> Map.toList |> List.sortBy fst |> List.map snd |> List.concat |> List.choose id
