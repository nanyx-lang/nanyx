namespace Nanyx.Runtime

open System

/// The crash a test marked `@test(#crashes)` expects (see `ListedTest.Crashes`).
type ExpectedCrash =
    /// Any crash: `@test(#crashes)`.
    | AnyCrash
    /// A crash whose message contains the text: `@test(#crashes("too big"))`.
    | CrashContaining of text: string

/// A test of a test build, as its module's `nanyx.tests` section lists it (see `Transpiler.Wasm.WasmBinary.withTests`):
/// the module it is in, its name there, the file, line and column, each from 1, it is written at, whether it is `#io`,
/// and the crash it expects.
type ListedTest =
    { Module: string
      Name: string
      File: string
      Line: int
      Column: int
      /// Whether it is marked `@test(#io)`, and so runs with the console host's real `Console`, `Env` and `FileSystem`,
      /// in the package's directory.
      Io: bool
      /// The crash it is marked to expect, `@test(#crashes)`, if it is: it passes only by reaching one (#3078).
      Crashes: ExpectedCrash option }

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
                let column = number ()
                // 1 for `#io`, 2 for a crash it expects, and 4 for a text that crash's message must contain, which follows.
                let flags = number ()
                let crashes =
                    if flags &&& 4 <> 0 then Some(CrashContaining(text ()))
                    elif flags &&& 2 <> 0 then Some AnyCrash
                    else None
                { Module = moduleName; Name = name; File = file; Line = line; Column = column; Io = flags &&& 1 <> 0; Crashes = crashes })
