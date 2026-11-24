/// The docs site's examples (see `RunDocExamples`), a module for each of the sections that take longest and one for the
/// rest, so that each is a test collection of its own and they run side by side (#2553).
namespace TranspilerWasmTests.Features.Docs

open Xunit

module StdlibDocExamples =
    let examples () = RunDocExamples.docExamplesIn ((=) "stdlib")

    [<Theory>]
    [<MemberData(nameof examples)>]
    let ``Docs example compiles and runs`` (section: string, page: string, line: int) = RunDocExamples.check section page line

module TourDocExamples =
    let examples () = RunDocExamples.docExamplesIn ((=) "tour")

    [<Theory>]
    [<MemberData(nameof examples)>]
    let ``Docs example compiles and runs`` (section: string, page: string, line: int) = RunDocExamples.check section page line

module CookbookDocExamples =
    let examples () = RunDocExamples.docExamplesIn ((=) "cookbook")

    [<Theory>]
    [<MemberData(nameof examples)>]
    let ``Docs example compiles and runs`` (section: string, page: string, line: int) = RunDocExamples.check section page line

module AdvancedDocExamples =
    let examples () = RunDocExamples.docExamplesIn ((=) "advanced")

    [<Theory>]
    [<MemberData(nameof examples)>]
    let ``Docs example compiles and runs`` (section: string, page: string, line: int) = RunDocExamples.check section page line

/// The examples of every other section.
module OtherDocExamples =
    let private own = set [ "stdlib"; "tour"; "cookbook"; "advanced" ]
    let examples () = RunDocExamples.docExamplesIn (own.Contains >> not)

    [<Theory>]
    [<MemberData(nameof examples)>]
    let ``Docs example compiles and runs`` (section: string, page: string, line: int) = RunDocExamples.check section page line
