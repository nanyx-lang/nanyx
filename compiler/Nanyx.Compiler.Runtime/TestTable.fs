namespace Nanyx.Runtime

open System

/// A test of a test build, as its module's `nanyx.tests` section lists it (see `Transpiler.Wasm.WasmBinary.withTests`):
/// the module it is in, its name there, and the file, line and column, each from 1, it is written at.
type ListedTest =
    { Module: string
      Name: string
      File: string
      Line: int
      Column: int }

    /// Its full name, the module's and its own, as they are declared: `geometry.adds points`.
    member this.FullName = $"{this.Module}.{this.Name}"

module TestTable =
    /// The name of the section, as `WasmBinary.testTableName` names it.
    let sectionName = "nanyx.tests"

    /// The tests the module in the binary format `wasm` lists, each at the index its test build's entry point runs it at
    /// (see `NyxCompiler.Package.testRootSource`); none if it lists none.
    let read (wasm: byte array) : ListedTest list =
        match SpanTable.customSection sectionName wasm with
        | None -> []
        | Some(start, _) ->
            let mutable at = start
            let text () =
                let value, next = SpanTable.name wasm at
                at <- next
                value
            let number () =
                let value, next = SpanTable.unsigned wasm at
                at <- next
                value
            List.init (number ()) (fun _ ->
                let moduleName = text ()
                let name = text ()
                let file = text ()
                let line = number ()
                { Module = moduleName; Name = name; File = file; Line = line; Column = number () })
