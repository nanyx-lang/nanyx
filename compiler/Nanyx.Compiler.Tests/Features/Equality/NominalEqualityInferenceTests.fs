module Nanyx.Compiler.Tests.Features.NominalEqualityInference

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// A generic nominal type, and unannotated defs that compare values of its argument, some only inside a lambda.
let private source =
    normalized
        """type Obj(a) = protected (members: list((string, a)))

def eqA = { x: Obj(a), y: Obj(a) ->
  list.all(x.members, { m -> list.contains(y.members, m) })
}

def eqB = { x: list((string, a)), m: (string, a) -> list.contains(x, m) }

def eqC = { x: Obj(a), y: Obj(a) -> x == y }
"""

/// The type of each of `names` in `source`, with the contexts it requires, as `nanyx check` prints it.
let private checkedTypes (source: string) (names: string list) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let types = TypeHints.topLevelTypes Ascii "" result |> List.map (fun hint -> hint.Name, hint.Type) |> Map.ofList
    names |> List.map (fun name -> types.[name])

[<Fact>]
let ``A def that compares a nominal type's argument inside a lambda requires its Eq`` () =
    // Neither def writes `==`: `list.contains` requires `Eq` of the members (#2695).
    Assert.Equal<string list>(
        [ "[Eq(a)] Obj(a), Obj(a) -> bool"; "[Eq(a)] list((string, a)), (string, a) -> bool" ],
        checkedTypes source [ "eqA"; "eqB" ]
    )

[<Fact>]
let ``A def whose Eq of a nominal type's argument is inferred is called at two types`` () =
    // The type a call reads the def's contexts from writes `Obj(a)`. Written `Obj` alone, it was read at the type's own
    // parameter, which the first call then fixed (#2695).
    let calls =
        source
        + normalized
            """
def strings = eqC(Obj(members = [("a", "1")]), Obj(members = [("a", "1")]))
def ints = eqC(Obj(members = [("a", 1)]), Obj(members = [("a", 2)]))
"""
    Assert.Equal<string list>([ "[Eq(a)] Obj(a), Obj(a) -> bool"; "bool"; "bool" ], checkedTypes calls [ "eqC"; "strings"; "ints" ])
