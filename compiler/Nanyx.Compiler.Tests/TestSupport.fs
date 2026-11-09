module Nanyx.Compiler.Tests.TestSupport

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program

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

/// The offset of the `occurrence`th (zero-based) appearance of `text` in `source`, plus `within` characters.
let offsetOf (source: string) (text: string) (occurrence: int) (within: int) =
    let rec find from remaining =
        let index = source.IndexOf(text, from, StringComparison.Ordinal)
        if index < 0 then failwith $"'{text}' not found"
        elif remaining = 0 then index
        else find (index + 1) (remaining - 1)
    find 0 occurrence + within
