/// A lint build built from scratch, as the first lint with a linter package does where no build of it is shipped or
/// cached: checked, compiled to WebAssembly and loaded, in time an editor can wait for (#3261).
module Nanyx.Compiler.Tests.Features.Linters.FirstLintTime

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Compiling `nanyx.lint`'s lint build to WebAssembly took 37 seconds when the backend unfolded `nanyx.analysis`'s syntax
// tree types for every expression's type, and about 3 once they were converted once each; with the `unused*` rules
// moved into the package, the whole build from scratch takes 8.7 seconds in a Release build. What is timed is the work a first lint does that a later one doesn't:
// the cache, already seeded, holds no lint build, and this process has compiled none it could reuse.
[<Fact; Trait("Category", "Timing")>]
let ``The official linter's lint build, built from scratch, is checked, compiled and loaded in under fifteen seconds in a Release build`` () =
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
    // Fifteen seconds in a Release build, as the Timing workflow runs it, and twenty-five in a Debug build, whose
    // backend is slower: 8.7 and 10 seconds here.
#if DEBUG
    let limit = 25000.0
#else
    let limit = 15000.0
#endif
    Assert.True(milliseconds < limit, $"the first lint's build took {milliseconds:N0} ms, against {limit:N0}")
