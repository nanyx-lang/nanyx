module Nanyx.Compiler.Tests.Features.Tooling.LibrarySearch

open System
open Xunit
open NyxCompiler
open NyxCompiler.LibrarySearch

// Searching the standard library by name, by type, or both (`nanyx search`).

/// What matches `query`, best first.
let private search (query: string) =
    match parse query with
    | Ok parsed -> LibrarySearch.search (Func<_, _>(fun _ -> "")) parsed
    | Error message -> failwith message

/// The match named `name` among those for `query`, and its place in them.
let private find (query: string) (name: string) =
    let matches = search query
    match matches |> List.tryFindIndex (fun found -> found.Name = name) with
    | Some index -> index, matches.[index]
    | None -> failwith $"""{name} does not match {query}; these do: {matches |> List.map _.Name |> String.concat ", "}"""

let private isFound (query: string) (name: string) =
    search query |> List.exists (fun found -> found.Name = name)

[<Fact>]
let ``A query is a name, a type, or a name and a type`` () =
    Assert.Equal(Ok { Name = Some "string.trim"; Type = None }, parse "string.trim")
    Assert.Equal(Some (TyFunc(TyPrimitive "bool", TyPrimitive "string", [])), (parse "bool -> string" |> Result.toOption |> Option.get).Type)
    Assert.Equal(Some "trim", (parse "trim: string -> string" |> Result.toOption |> Option.get).Name)

[<Fact>]
let ``A query that is not a type is reported`` () =
    match parse "bool -> " with
    | Error message -> Assert.StartsWith("`bool ->` is not a type", message)
    | Ok parsed -> failwith $"%A{parsed}"

[<Fact>]
let ``A name finds the names that contain it, the one it names first`` () =
    let matches = search "trim"
    Assert.Equal("string.trim", matches.Head.Name)
    Assert.Contains(matches, fun found -> found.Name = "string.trimStart")
    Assert.Equal(Some "import nanyx/text", matches.Head.Import)

[<Fact>]
let ``An export's type variables are instantiated`` () =
    let _, length = find "list(int) -> int" "list.length"
    Assert.Equal(Instantiated, length.Rank)

[<Fact>]
let ``A query's variables stand for any type, but the same one each time`` () =
    let _, hash = find "a -> a" "int.hash"
    Assert.Equal(Instantiated, hash.Rank)
    Assert.False(isFound "a -> a" "int.toString")

[<Fact>]
let ``A function's inputs may come in any order`` () =
    let _, filter = find "(a -> bool), list(a) -> list(a)" "list.filter"
    Assert.Equal(Reordered, filter.Rank)
    let _, inOrder = find "list(a), (a -> bool) -> list(a)" "list.filter"
    Assert.Equal(Equal, inOrder.Rank)

[<Fact>]
let ``A context is satisfied by an instance at the matched type, or shown as still needed`` () =
    let _, atBool = find "bool -> string" "ToString(a).toString"
    Assert.Equal(Instantiated, atBool.Rank)
    Assert.Empty(atBool.Needs)
    let _, atAny = find "a -> string" "ToString(a).toString"
    Assert.Equal(NeedsContexts, atAny.Rank)
    Assert.Equal<string list>([ "ToString(a)" ], atAny.Needs)

[<Fact>]
let ``An attached definition is found by the type it attaches to, as its first input`` () =
    let _, lines = find "string -> list(string)" "string.lines"
    Assert.Equal(Equal, lines.Rank)
    let _, contains = find "string, string -> bool" "string.contains"
    Assert.Equal(Equal, contains.Rank)

[<Fact>]
let ``Equal types come first, then instantiated ones, then reordered ones, then ones that need contexts`` () =
    let equalIndex, _ = find "bool -> string" "bool.toString"
    let instantiatedIndex, _ = find "bool -> string" "ToString(a).toString"
    Assert.True(equalIndex < instantiatedIndex)
    let matches = search "(a -> bool), list(a) -> list(a)"
    let ranks = matches |> List.map _.Rank
    Assert.Equal<MatchRank list>(List.sort ranks, ranks)
    Assert.Contains(Reordered, ranks)
    Assert.Contains(NeedsContexts, ranks)

[<Fact>]
let ``Within a rank shorter names come first`` () =
    let matches = search "string -> list(string)" |> List.filter (fun found -> found.Rank = Equal)
    let lengths = matches |> List.map _.Name.Length
    Assert.Equal<int list>(List.sort lengths, lengths)

[<Fact>]
let ``Reserved names are never shown`` () =
    Assert.Empty(search "__")
    Assert.DoesNotContain(search "a -> string", fun found -> found.Name.Contains "__")

[<Fact>]
let ``A name and a type together find what has both`` () =
    let matches = search "trim: string -> string"
    Assert.Equal("string.trim", matches.Head.Name)
    Assert.All(matches, fun found -> Assert.Contains("trim", found.Name, StringComparison.OrdinalIgnoreCase))
