/// A module's types are converted to IR types once each (see `CoreIrLowerTypes.toIrTypeIn`), and a group of recursive
/// types once, whichever of its types is reached: lowering asks for the IR type of every expression, and unfolding a large
/// group each time took `nanyx.lint`'s lint build 37 seconds (#3261).
module TranspilerWasmTests.Features.Types.IrTypeConversions

open Xunit
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// Six types that name each other, as a syntax tree's do, and a function over each that takes it apart.
let private group =
    let types =
        [ for i in 0..5 ->
              let next, after = (i + 1) % 6, (i + 2) % 6
              $"type T{i} = (id: int, kind: #leaf(int) | #one(T{next}) | #many(list(T{after})) | #pair(T{i}, T{i}))" ]
    let sizes =
        [ for i in 0..5 ->
              let next, after = (i + 1) % 6, (i + 2) % 6
              $"""def size{i}: T{i} -> int = {{ t ->
  match t.kind
    | #leaf(n) -> n
    | #one(u) -> 1 + size{next}(u)
    | #many(us) -> 1 + list.sum(list.map(us, size{after}))
    | #pair(a, b) -> size{i}(a) + size{i}(b)
}}""" ]
    String.concat "\n\n" (types @ sizes) + "\n"

/// The module `group` compiled with what converting its types finds kept or not, and how many types that converted.
let private compiled (keeping: bool) =
    Nanyx.LargeStack.run (fun () ->
        CoreIrLowerTypes.keepingConverted.Value <- keeping
        let before = CoreIrLowerTypes.conversionCount ()
        let wat = transpileWatWith WasmOptions.Default group
        wat, CoreIrLowerTypes.conversionCount () - before)

[<Fact>]
let ``Keeping what converting a module's types finds changes none of its code`` () =
    let kept, _ = compiled true
    let unkept, _ = compiled false
    Assert.Equal(unkept, kept)

[<Fact>]
let ``A module converts each of its types once, and a group of recursive types once`` () =
    let _, keptCount = compiled true
    let _, unkeptCount = compiled false
    // 199 kept, against 21,510 when each expression's type unfolded the whole group again.
    Assert.True(keptCount * 20L < unkeptCount, $"converted {keptCount} types kept, {unkeptCount} not")
