/// The first lint with `nanyx.lint` after a compiler upgrade builds its lint build: checked, compiled to WebAssembly and
/// loaded, in time an editor can wait for (#3261).
module Nanyx.Compiler.Tests.Features.Linters.FirstLintTime

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Compiling it to WebAssembly took 37 seconds when the backend unfolded `nanyx.analysis`'s syntax tree types for every
// expression's type; with them converted once each it takes about 3.6 in a Debug build (see the Recursive types deep
// dive). What is timed is the work a first lint does that a later one doesn't: the cache, already seeded, holds no
// lint build, and this process has compiled none it could reuse.
[<Fact; Trait("Category", "Timing")>]
let ``The official linter's lint build is checked, compiled and loaded in under five seconds in a Release build`` () =
    let store = SeededPackages.user ()
    let entry : LinterEntry =
        { Package = Linters.official.Package
          Path = None
          Version = VersionRequirement.tryParse Toolchain.version
          Span = Parser.Program.SourceSpan.none
          Settings = None }
    let linter =
        match Package.scriptLinters store Parser.Program.SourceSpan.none [ entry ] with
        | [ linter ], [] -> linter
        | _, errors -> failwith $"nanyx.lint can't be read: %A{errors}"
    let runner = Transpiler.Wasm.LintBuild.runner Nanyx.Runtime.WasmLinter.load
    let milliseconds =
        Nanyx.TestTiming.millisecondsOf (fun () ->
            let root = okOf (LintBuilds.rootResult store linter |> Result.mapError List.singleton)
            let wasm = runner.Assemble root.Typed.Value
            okOf (runner.Load wasm |> Result.mapError List.singleton) |> ignore)
    // Five seconds in a Release build, as the Timing workflow runs it; a Debug build's backend is slower, and is given
    // three times as long, which still fails a return to unfolding the types each time.
#if DEBUG
    let limit = 15000.0
#else
    let limit = 5000.0
#endif
    Assert.True(milliseconds < limit, $"the first lint's build took {milliseconds:N0} ms, against {limit:N0}")
