module Nanyx.Compiler.Tests.Features.StdLib.StackSafety

open Xunit
open NyxCompiler

// Each standard-library function that walks a collection is stack-safe (docs/src/content/work-in-progress/tail-calls.md): it calls
// itself only in tail position. The runs that show it working on long input are in the Wasm tests
// (Features/StdLib/RunStackSafeTests.fs).

/// What `default/nonTailRecursion` reports in the standard-library module `moduleName`, compiled as the library
/// compiles it (with its intrinsics in scope): each message with the line it is reported on.
let private nonTailRecursionIn (moduleName: string) (source: string) =
    let result = Compiler.compileFrom Linters.everyLinter (StandardLibraryModule moduleName) source
    Assert.True(result.TypeChecked, $"%A{result.Diagnostics}")
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/nonTailRecursion")
    |> List.map (fun diagnostic -> diagnostic.Message, source.Substring(0, diagnostic.Span.Value.Start).Split('\n').Length)

[<Fact>]
let ``The linter finds no recursion outside tail position in the standard library`` () =
    let modules = StandardLibrary.preludeModuleName :: StandardLibrary.moduleNames
    Assert.Equal<string list>([ "prelude"; "console"; "console.host"; "host.runtime"; "math"; "result"; "text" ], modules)
    for moduleName in modules do
        Assert.Equal<(string * int) list>([], nonTailRecursionIn moduleName (StandardLibrary.tryLoadSource moduleName).Value)

[<Fact>]
let ``The check above finds recursion outside tail position in a library module, intrinsics and all`` () =
    let source =
        "module text\n" +
        "def copy: list(int) -> list(int) = {\n  | [] -> []\n  | [x, ...rest] -> __nanyx_list_cons(x, copy(rest))\n}\n"
    let message, line = Assert.Single(nonTailRecursionIn "text" source)
    Assert.Equal(4, line)
    Assert.StartsWith("`copy` calls itself here, but not in tail position: its result is passed to `__nanyx_list_cons`", message)
