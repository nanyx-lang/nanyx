module Nanyx.Compiler.Tests.Features.Packages.SemanticVersioning

open System.IO
open System.IO.Compression
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A package's public API, recorded in `api.json`, and the version a change to it needs, as Elm has it (#1470): an
// export removed or changed needs a major bump, one added or made more general a minor bump, and no change a patch.

let private version text = (SemanticVersion.tryParse text).Value

let private def name signature = { Kind = ApiDef; Name = name; Signature = signature; Variances = None }
let private typeNamed name definition = { Kind = ApiType; Name = name; Signature = definition; Variances = None }
let private nominal name definition variances = { Kind = ApiType; Name = name; Signature = definition; Variances = Some variances }

let private api (modules: (string * ApiExport list) list) = { Package = "acme.util"; Version = "1.0.0"; Modules = modules }

/// The changes from a module `strings` that exports `before` to one that exports `after`, each as its bump and
/// description.
let private changes (before: ApiExport list) (after: ApiExport list) =
    PackageApi.compare (api [ "strings", before ]) (api [ "strings", after ]) |> List.map (fun change -> change.Bump, change.Description)

let private onlyChange before after =
    match changes before after with
    | [ change ] -> change
    | other -> failwith $"Expected one change, got %A{other}"

[<Fact>]
let ``No change, or a type variable renamed, needs only a patch`` () =
    Assert.Empty(changes [ def "shout" "string -> string" ] [ def "shout" "string -> string" ])
    Assert.Empty(changes [ def "first" "list(a) -> #some(a) | #emptyList" ] [ def "first" "list(b) -> #some(b) | #emptyList" ])
    Assert.Equal(PatchBump, PackageApi.required [])

[<Fact>]
let ``An export removed needs a major bump`` () =
    let bump, description = onlyChange [ def "shout"  "string -> string"; def "whisper" "string -> string" ] [ def "shout" "string -> string" ]
    Assert.Equal(MajorBump, bump)
    Assert.Equal("strings's def whisper was removed, or is no longer exported", description)

[<Fact>]
let ``A def's type changed needs a major bump`` () =
    let bump, description = onlyChange [ def "count" "string -> int" ] [ def "count" "string -> float" ]
    Assert.Equal(MajorBump, bump)
    Assert.Equal("the type of strings's def count changed from string -> int to string -> float", description)

[<Fact>]
let ``A def's type made less general needs a major bump`` () =
    let bump, _ = onlyChange [ def "first" "list(a) -> #some(a) | #emptyList" ] [ def "first" "list(int) -> #some(int) | #emptyList" ]
    Assert.Equal(MajorBump, bump)

[<Fact>]
let ``A def's type made more general needs a minor bump`` () =
    let bump, description = onlyChange [ def "first" "list(int) -> #some(int) | #emptyList" ] [ def "first" "list(a) -> #some(a) | #emptyList" ]
    Assert.Equal(MinorBump, bump)
    Assert.Contains("was made more general", description)

[<Fact>]
let ``A new context requirement needs a major bump`` () =
    let bump, description = onlyChange [ def "same" "a, a -> bool" ] [ def "same" "[Eq(a)] a, a -> bool" ]
    Assert.Equal(MajorBump, bump)
    Assert.Contains("requires a context it didn't", description)

[<Fact>]
let ``A context requirement dropped needs a minor bump`` () =
    let bump, description = onlyChange [ def "same" "[Eq(a)] a, a -> bool" ] [ def "same" "a, a -> bool" ]
    Assert.Equal(MinorBump, bump)
    Assert.Contains("no longer requires a context", description)

[<Fact>]
let ``A field removed from an exported record needs a major bump`` () =
    let bump, description = onlyChange [ typeNamed "Point" "type Point = (x: int, y: int)" ] [ typeNamed "Point" "type Point = (x: int)" ]
    Assert.Equal(MajorBump, bump)
    Assert.Equal("the field y was removed from strings's type Point", description)

[<Fact>]
let ``A tag added to or removed from an exported union needs a major bump`` () =
    let bump, added = onlyChange [ typeNamed "Colour" "type Colour = #red | #green" ] [ typeNamed "Colour" "type Colour = #red | #green | #blue" ]
    Assert.Equal(MajorBump, bump)
    Assert.Contains("the tag #blue was added to strings's type Colour", added)
    let bump, removed = onlyChange [ typeNamed "Colour" "type Colour = #red | #green" ] [ typeNamed "Colour" "type Colour = #red" ]
    Assert.Equal(MajorBump, bump)
    Assert.Equal("the tag #green was removed from strings's type Colour", removed)

[<Fact>]
let ``A type's parameters changed needs a major bump`` () =
    let bump, description = onlyChange [ typeNamed "Box" "type Box(a) = (value: a)" ] [ typeNamed "Box" "type Box(a, b) = (value: a, other: b)" ]
    Assert.Equal(MajorBump, bump)
    Assert.Equal("the type parameters of strings's type Box changed from (a) to (a, b)", description)
    Assert.Empty(changes [ typeNamed "Box" "type Box(a) = (value: a)" ] [ typeNamed "Box" "type Box(t) = (value: t)" ])

[<Fact>]
let ``An exported type made protected needs a major bump`` () =
    let bump, description = onlyChange [ typeNamed "Email" "type Email = string" ] [ typeNamed "Email" "type Email = protected string" ]
    Assert.Equal(MajorBump, bump)
    Assert.Equal("strings's type Email was made protected", description)

[<Fact>]
let ``A new export or module needs a minor bump, and a module removed a major one`` () =
    let added = PackageApi.compare (api [ "strings", [] ]) (api [ "strings", [ def "shout" "string -> string" ]; "numbers", [] ])
    Assert.Equal<(Bump * string) list>(
        [ MinorBump, "strings's def shout was added"; MinorBump, "the module numbers was added" ],
        added |> List.map (fun change -> change.Bump, change.Description)
    )
    let removed = PackageApi.compare (api [ "strings", []; "numbers", [] ]) (api [ "strings", [] ])
    Assert.Equal(MajorBump, PackageApi.required removed)

[<Fact>]
let ``A version is bumped as the change needs, with breaking changes below 1.0.0 a minor bump`` () =
    let bumped from bump = string (PackageApi.bumped (version from) bump)
    Assert.Equal("2.0.0", bumped "1.2.3" MajorBump)
    Assert.Equal("1.3.0", bumped "1.2.3" MinorBump)
    Assert.Equal("1.2.4", bumped "1.2.3" PatchBump)
    Assert.Equal("0.4.0", bumped "0.3.1" MajorBump)
    Assert.Equal("0.3.2", bumped "0.3.1" MinorBump)
    Assert.True(PackageApi.allows (version "1.2.3") (version "3.0.0") MajorBump)
    Assert.False(PackageApi.allows (version "1.2.3") (version "1.9.0") MajorBump)
    Assert.True(PackageApi.allows (version "0.3.1") (version "0.4.0") MajorBump)

[<Fact>]
let ``Publishing a bump too small for the changes is refused, with the changes listed`` () =
    let before = version "1.2.0", api [ "strings", [ def "shout" "string -> string"; def "whisper" "string -> string" ] ]
    let after = api [ "strings", [ def "shout" "string -> string" ] ]
    Assert.Equal<string list>(
        [ "acme.util 1.3.0 is too small a bump from 1.2.0 for what changed, which needs 2.0.0 at least:"
          "  strings's def whisper was removed, or is no longer exported" ],
        PackageApi.versionErrors before (version "1.3.0", after)
    )
    Assert.Empty(PackageApi.versionErrors before (version "2.0.0", after))
    Assert.Equal<string list>([ "acme.util 1.1.0 is not after 1.2.0, the newest version published" ], PackageApi.versionErrors before (version "1.1.0", after))

[<Fact>]
let ``A nominal type's parameter that takes less than it did needs a major bump, and one that takes more a minor one`` () =
    let stack variances = [ nominal "Stack" "type Stack(a) = private (items: Inner(a))" variances ]
    Assert.Equal(
        (MajorBump,
         "strings's type Stack: the parameter a of strings's type Stack was covariant and is invariant, so a value of it at one argument may no longer be given where one at another is expected"),
        onlyChange (stack [ Covariant ]) (stack [ Invariant ]) |> fun (bump, description) -> bump, $"strings's type Stack: {description}")
    Assert.Equal(MajorBump, fst (onlyChange (stack [ Covariant ]) (stack [ Contravariant ])))
    Assert.Equal(MajorBump, fst (onlyChange (stack [ Unused ]) (stack [ Covariant ])))
    Assert.Equal(
        (MinorBump, "the parameter a of strings's type Stack was invariant and is covariant, so a value of it is given where it wasn't"),
        onlyChange (stack [ Invariant ]) (stack [ Covariant ]))
    Assert.Equal(MinorBump, fst (onlyChange (stack [ Contravariant ]) (stack [ Unused ])))

[<Fact>]
let ``An api.json that records no variances is compared by its definitions alone`` () =
    let written = typeNamed "Stack" "type Stack(a) = private (items: Inner(a))"
    let recorded = nominal "Stack" "type Stack(a) = private (items: Inner(a))" [ Invariant ]
    Assert.Empty(changes [ written ] [ recorded ])

// A package, end to end: its API made from its public modules, packed in its `.nyxpkg`, and `bump` against the version
// published in an index on disk.

let private library (version: string) (body: string) =
    [ "util/acme.util.nyxp", $"(package = (name = \"acme.util\", version = \"{version}\"))\n"
      "util/strings.nyx", withFinalNewline body
      "util/secrets.nyx", withFinalNewline "module secrets\n\nexport def hidden = 1" ]

let private found (package: TempPackage) = okOf (Package.ofManifestFrom (storeOf package) (package.Path "util/acme.util.nyxp"))

[<Fact>]
let ``The API holds every export of each public module, with its type`` () =
    use package =
        new TempPackage(
            library "1.0.0" """export module strings

export type Point = (x: int, y: int)
export type Stack(a) = private list(a)
export type Sink(a) = private (put: a -> ())

export def shout = { s: string -> s ++ "!" }

def helper = 1"""
        )
    let made = okOf (PackageApi.ofPackage (found package))
    Assert.Equal<(string * ApiExport list) list>(
        [ "strings",
          [ typeNamed "Point" "type Point = (x: int, y: int)"
            nominal "Sink" "type Sink(a) = private (put: (a -> ()))" [ Contravariant ]
            nominal "Stack" "type Stack(a) = private list(a)" [ Covariant ]
            def "shout" "string -> string" ] ],
        made.Modules
    )
    Assert.Equal(made, okOf (PackageApi.ofJson (PackageApi.toJson made)))
    Assert.Contains("\"type\": \"string -> string\"", PackageApi.toJson made)
    Assert.Contains("\"variances\": [\n            \"covariant\"\n          ]", PackageApi.toJson made)

[<Fact>]
let ``A packed package holds its api.json`` () =
    use package = new TempPackage(library "1.0.0" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }")
    use zip = new ZipArchive(new MemoryStream(okOf (PackageApi.pack (found package))))
    Assert.Equal<string list>([ "acme.util.nyxp"; "api.json"; "secrets.nyx"; "strings.nyx" ], zip.Entries |> Seq.map _.FullName |> List.ofSeq)

/// Publishes the package in `util` to the index on disk, as a release would: packed with its API, and listed.
let private publish (package: TempPackage) =
    let packageFound = found package
    publishToIndex package packageFound (okOf (PackageApi.pack packageFound)) |> ignore

let private bumpTo (package: TempPackage) (body: string) =
    File.WriteAllText(package.Path "util/strings.nyx", withFinalNewline body)
    okOf (PackageApi.bump (storeOf package) (found package))

[<Fact>]
let ``Bump writes the version the changes since the published version need`` () =
    use package = new TempPackage(library "1.0.0" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }")
    publish package
    let added = bumpTo package "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n\nexport def whisper = { s: string -> s }"
    Assert.Equal(Some(version "1.0.0"), added.Baseline)
    Assert.Equal("1.1.0", added.Version)
    Assert.Equal<(Bump * string) list>([ MinorBump, "strings's def whisper was added" ], added.Changes |> List.map (fun change -> change.Bump, change.Description))
    Assert.Contains("version = \"1.1.0\"", added.Manifest)
    let removed = bumpTo package "export module strings\n\nexport def whisper = { s: string -> s }"
    Assert.Equal("2.0.0", removed.Version)
    let unchanged = bumpTo package "export module strings\n\nexport def shout = { s: string -> s ++ \"!!\" }"
    Assert.Empty(unchanged.Changes)
    Assert.Equal("1.0.1", unchanged.Version)

[<Fact>]
let ``With nothing published, bump leaves the version as it is`` () =
    use package =
        new TempPackage(
            library "0.1.0" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }"
            @ [ "index/config.json", """{ "format": 1, "index": "index/" }""" ]
        )
    let bumped = okOf (PackageApi.bump (storeOf package) (found package))
    Assert.Equal(None, bumped.Baseline)
    Assert.Equal("0.1.0", bumped.Version)
