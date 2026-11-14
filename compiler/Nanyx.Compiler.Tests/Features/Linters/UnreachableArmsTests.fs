module Nanyx.Compiler.Tests.Features.Linters.UnreachableArms

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// The official linter's `unreachableArms` rule.

/// What `default/unreachableArms` reports for `source`: the text of each arm it reports, with the texts of the arms
/// it says cover it.
let private unreachableIn (source: string) =
    (compileWithOfficial source).Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/unreachableArms")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        textAt source diagnostic.Span.Value, diagnostic.Related |> List.map (fst >> textAt source))

[<Fact>]
let ``An arm after a catch-all or a variable is unreachable`` () =
    // The example from the official linter's page in the docs.
    let afterWildcard = normalized """def f: int -> string = { n ->
  match n
    | _ -> "always"
    | 0 -> "never reached"
}"""
    Assert.Equal<(string * string list) list>([ "0", [ "_" ] ], unreachableIn afterWildcard)
    let diagnostic = (compileWithOfficial afterWildcard).Diagnostics |> List.find (fun d -> d.Code = "default/unreachableArms")
    Assert.Equal("This arm can never match: an arm before it matches every value it does.", diagnostic.Message)
    let afterVariable = "def f: int -> int = { n ->\n  match n\n    | _other -> 1\n    | 2 -> 2\n}"
    Assert.Equal<(string * string list) list>([ "2", [ "_other" ] ], unreachableIn afterVariable)

[<Fact>]
let ``An arm after arms that handle every tag, boolean or length of list is unreachable`` () =
    let tags = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(_n) -> 1\n    | #none -> 0\n    | _ -> 2\n}"
    Assert.Equal<(string * string list) list>([ "_", [ "#some(_n)"; "#none" ] ], unreachableIn tags)
    let booleans = "def f: bool -> int = { b ->\n  match b\n    | true -> 1\n    | false -> 0\n    | _ -> 2\n}"
    Assert.Equal<(string * string list) list>([ "_", [ "true"; "false" ] ], unreachableIn booleans)
    let lists = "def f: list(int) -> int = { xs ->\n  match xs\n    | [] -> 0\n    | [_x, ..._rest] -> 1\n    | _ -> 2\n}"
    Assert.Equal<(string * string list) list>([ "_", [ "[]"; "[_x, ..._rest]" ] ], unreachableIn lists)

[<Fact>]
let ``An arm that an earlier one matches everything of is unreachable, at any position`` () =
    let subsumed = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(_) -> 1\n    | #some(5) -> 2\n    | #none -> 0\n}"
    Assert.Equal<(string * string list) list>([ "#some(5)", [ "#some(_)" ] ], unreachableIn subsumed)
    let range = "def f: int -> int = { n ->\n  match n\n    | 1..10 -> 1\n    | 5 -> 2\n    | _ -> 0\n}"
    Assert.Equal<(string * string list) list>([ "5", [ "1..10" ] ], unreachableIn range)
    // With two values matched, arms that handle each tag of the first whatever the second is cover any later arm.
    let twoValues =
        "def f: (#some(int) | #none), bool -> int = { o, b ->\n  match o, b\n    | #some(_x), _ -> 1\n    | #none, _ -> 2\n    | _, true -> 3\n}"
    Assert.Equal<(string * string list) list>([ "_, true", [ "#some(_x), _"; "#none, _" ] ], unreachableIn twoValues)
    // Arms that cover a combination of values between them, none of them with catch-alls elsewhere.
    let combinations =
        "def f: bool, bool -> int = { a, b ->\n  match a, b\n    | true, true -> 1\n    | false, true -> 2\n    | _, false -> 3\n    | _, true -> 4\n}"
    Assert.Equal<(string * string list) list>([ "_, true", [ "true, true"; "false, true" ] ], unreachableIn combinations)
    let ranges = "def f: int -> int = { n ->\n  match n\n    | 1..5 -> 1\n    | 6..10 -> 2\n    | 3..8 -> 3\n    | _ -> 0\n}"
    Assert.Equal<(string * string list) list>([ "3..8", [ "1..5"; "6..10" ] ], unreachableIn ranges)

[<Fact>]
let ``An arm is not unreachable after guards, partial patterns or other values`` () =
    let afterGuard = "def f: int -> int = { n ->\n  match n\n    | { > 0 } -> 1\n    | 5 -> 2\n    | _ -> 3\n}"
    Assert.Empty(unreachableIn afterGuard)
    let afterPartialTag = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(5) -> 1\n    | #some(_n) -> 2\n    | #none -> 0\n}"
    Assert.Empty(unreachableIn afterPartialTag)
    // `#some(5)` leaves the other `#some`s: the catch-all after it and `#none` can match `#some(6)`.
    let catchAllAfterPartialTag = "def f: #some(int) | #none -> int = { o ->\n  match o\n    | #some(5) -> 1\n    | #none -> 0\n    | _ -> 2\n}"
    Assert.Empty(unreachableIn catchAllAfterPartialTag)
    let otherLiterals = "def f: int -> int = { n ->\n  match n\n    | 1 -> 1\n    | 2 -> 2\n    | _ -> 3\n}"
    Assert.Empty(unreachableIn otherLiterals)
    // `[_x, _y, ..._rest]` leaves lists of one: the catch-all after it can match.
    let listsLeft = "def f: list(int) -> int = { xs ->\n  match xs\n    | [] -> 0\n    | [_x, _y, ..._rest] -> 1\n    | _ -> 2\n}"
    Assert.Empty(unreachableIn listsLeft)

[<Fact>]
let ``An and-pattern matches what all its parts do`` () =
    // `n & 5` matches only 5, so the arm for 5 after it can never match, but the catch-all can (#427).
    let literal = "def f: int -> int = { n ->\n  match n\n    | m & 5 -> m\n    | 5 -> 2\n    | _ -> 3\n}"
    Assert.Equal<(string * string list) list>([ "5", [ "m & 5" ] ], unreachableIn literal)
    let ranges = "def f: int -> int = { n ->\n  match n\n    | 1..10 & 5..20 -> 1\n    | 6 -> 2\n    | 11 -> 3\n    | _ -> 0\n}"
    Assert.Equal<(string * string list) list>([ "6", [ "1..10 & 5..20" ] ], unreachableIn ranges)
    // A guard part covers nothing for certain.
    let guarded = "def f: int -> int = { n ->\n  match n\n    | m & { > 0 } -> m\n    | 5 -> 2\n    | _ -> 3\n}"
    Assert.Empty(unreachableIn guarded)
    // An and-pattern asked about that cannot be told apart from nothing is not reported.
    let disjoint = "def f: int -> int = { n ->\n  match n\n    | 1 & 2 -> 1\n    | _ -> 0\n}"
    Assert.Empty(unreachableIn disjoint)
    let afterCatchAll = "def f: int -> int = { n ->\n  match n\n    | _ -> 1\n    | m & { > 0 } -> m\n}"
    Assert.Equal<(string * string list) list>([ "m & { > 0 }", [ "_" ] ], unreachableIn afterCatchAll)

[<Fact>]
let ``The else of an if-is after a pattern that always matches is unreachable, but the unit an if-is makes up is not reported`` () =
    let withElse = "def f: (int, int) -> int = { p -> if p is (_a, _b) then 1 else 2 }"
    Assert.Equal<(string * string list) list>([ "2", [ "(_a, _b)" ] ], unreachableIn withElse)
    // There is no `|` arm to remove: the fix would be to drop the `else`, which is the user's to write.
    let diagnostic = (compileWithOfficial withElse).Diagnostics |> List.find (fun d -> d.Code = "default/unreachableArms")
    Assert.Empty(diagnostic.Fixes)
    Assert.Empty(unreachableIn "def f: (int, int) -> () = { p -> if p is (_a, _b) then () }")

[<Fact>]
let ``The fix for an unreachable arm removes it, with its line when it has one to itself`` () =
    let ownLine = normalized """def f: int -> string = { n ->
  match n
    | _ -> "always"
    | 0 -> "never" -- dead
}"""
    let text, title = fixed' ownLine
    Assert.Equal("Remove the unreachable arm", title)
    Assert.Equal("def f: int -> string = { n ->\n  match n\n    | _ -> \"always\"\n}", text)
    Assert.Empty((compileWithOfficial text).Diagnostics)
    let sameLine, _ = fixed' "def f: int -> int = { | _ -> 1 | 0 -> 2 }"
    Assert.Equal("def f: int -> int = { | _ -> 1 }", sameLine)

[<Fact>]
let ``A lone else in a match on several values is unreachable after arms that match everything`` () =
    let source = "def f: int, int -> int = { x, y ->\n  match x, y\n    | 0, _ -> 0\n    | _, _ -> 1\n    | else -> 2\n}"
    Assert.Equal<(string * string list) list>([ "else", [ "_, _" ] ], unreachableIn source)
