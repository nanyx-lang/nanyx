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
    // The checker and the backend are compiled to machine code once a process, which a first lint after an upgrade
    // pays too, but which says nothing of the lint build: a small program is checked and compiled first.
    let warmUp = Compiler.compile "def twice = { n: int -> n * 2 }\n\nprintln(twice(21))\n"
    Transpiler.Wasm.CodeGen.transpileTypedModuleWithOptions Transpiler.Wasm.CodeGen.WasmOptions.Default warmUp.Typed.Value
    |> fst
    |> Transpiler.Wasm.WasmBinary.assemble
    |> ignore
    // Each step's time, as `LintBuilds` takes them for a lint build it builds: checked, compiled to WebAssembly text with
    // the options a lint build has (see `LintBuild.assemble`), assembled, and loaded.
    let steps = System.Collections.Generic.List<string * float>()
    let watch = System.Diagnostics.Stopwatch()
    let step (name: string) (action: unit -> 'T) : 'T =
        watch.Restart()
        let result = action ()
        steps.Add((name, watch.Elapsed.TotalMilliseconds))
        result
    let milliseconds =
        Nanyx.TestTiming.millisecondsOf (fun () ->
            let root = step "check" (fun () -> okOf (LintBuilds.rootResult store linter |> Result.mapError List.singleton))
            let wat =
                step "transpile" (fun () ->
                    Nanyx.LargeStack.run (fun () ->
                        Transpiler.Wasm.CodeGen.transpileTypedModuleWithOptions
                            { Transpiler.Wasm.CodeGen.WasmOptions.Default with CrashToRuntime = true }
                            root.Typed.Value
                        |> fst))
            let wasm = step "assemble" (fun () -> Transpiler.Wasm.WasmBinary.assemble wat)
            step "load" (fun () -> okOf (Nanyx.Runtime.WasmLinter.load wasm |> Result.mapError List.singleton) |> ignore))
    let breakdown = steps |> Seq.map (fun (name, ms) -> $"{name} {ms:N0} ms") |> String.concat ", "
    // Fifteen seconds in a Release build, as the Timing workflow runs it, and twenty-five in a Debug build, whose
    // backend is slower: 8.7 and 10 seconds here.
#if DEBUG
    let limit = 25000.0
#else
    let limit = 15000.0
#endif
    Assert.True(milliseconds < limit, $"the first lint's build took {milliseconds:N0} ms ({breakdown}), against {limit:N0}")
