module Nanyx.Compiler.Tests.Features.Packages.VersionResolution

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Choosing a version of each package a build needs (#864): PubGrub over the package index, the newest version every
// dependency accepts, and when there is none, an explanation in plain sentences. The solver is tested on its own, with
// versions given in memory, and resolution against an index on disk, in the format of nanyx-lang/packages.

let private version text = (SemanticVersion.tryParse text).Value

let private requirement text = (VersionRequirement.tryParse text).Value

/// A version of a package for the solver, `"1.0.0"`, depending on each `(name, requirement)` of `dependencies`.
let private at (written: string) (dependencies: (string * string) list) =
    { Version = version written
      Dependencies = dependencies |> List.map (fun (name, written) -> name, requirement written)
      Unusable = None }

/// The solver's input for the root `app`, which depends on `requirements`, choosing from `packages`, each a name and its
/// versions; a package not among them has none.
let private inputFor (requirements: (string * string) list) (packages: (string * SolverVersion list) list) =
    { Root = "app"
      Requirements = requirements |> List.map (fun (name, written) -> { From = "app"; Package = name; Requirement = requirement written })
      Versions = fun name -> Result.Ok(Some(packages |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.defaultValue []))
      Locked = Map.empty }

let private solved (outcome: SolverOutcome) =
    match outcome with
    | Solved chosen -> chosen |> List.map (fun (name, version) -> $"{name} {version}")
    | NoSolution lines -> failwith (String.concat "\n" ("Expected a solution, but there is none:" :: lines))
    | IndexFailed message -> failwith message

let private explanation (outcome: SolverOutcome) =
    match outcome with
    | NoSolution lines -> lines
    | other -> failwith $"Expected no solution, but found {other}"

[<Fact>]
let ``The newest version every dependency accepts is chosen`` () =
    let input = inputFor [ "acme.core", "1.0" ] [ "acme.core", [ at "1.0.0" []; at "1.1.0" []; at "2.0.0" [] ] ]
    Assert.Equal<string list>([ "acme.core 1.1.0" ], solved (VersionSolver.solve input))

[<Fact>]
let ``A version whose dependencies conflict is passed over for an older one`` () =
    let input =
        inputFor
            [ "foo", "1.0.0"; "bar", "1.0.0" ]
            [ "foo", [ at "1.0.0" []; at "1.1.0" [ "bar", "2.0.0" ] ]
              "bar", [ at "1.0.0" []; at "1.1.0" []; at "2.0.0" [] ] ]
    Assert.Equal<string list>([ "bar 1.1.0"; "foo 1.0.0" ], solved (VersionSolver.solve input))

[<Fact>]
let ``A version the lockfile locks is kept while it is allowed, though a newer one is too`` () =
    let input = { inputFor [ "acme.core", "1.0" ] [ "acme.core", [ at "1.0.0" []; at "1.1.0" [] ] ] with Locked = Map [ "acme.core", version "1.0.0" ] }
    Assert.Equal<string list>([ "acme.core 1.0.0" ], solved (VersionSolver.solve input))

[<Fact>]
let ``Two packages that want incompatible versions of a third are explained`` () =
    let input =
        inputFor
            [ "acme.web", "1.0"; "acme.json", "2.0" ]
            [ "acme.web", [ at "1.0.0" [ "acme.core", "1.0" ] ]
              "acme.json", [ at "2.0.0" [ "acme.core", "2.0" ] ]
              "acme.core", [ at "1.0.0" []; at "2.0.0" [] ] ]
    Assert.Equal<string list>(
        [ "Because acme.json 2.0.0 depends on acme.core 2.0 and acme.web 1.0.0 depends on acme.core 1.0, acme.json 2.0.0 is incompatible with acme.web 1.0.0."
          "And because app depends on acme.web 1.0 and acme.json 2.0, the dependencies can't all be satisfied." ],
        explanation (VersionSolver.solve input)
    )

[<Fact>]
let ``A dependency no version satisfies, or on a package the index doesn't have, is explained`` () =
    let input = inputFor [ "acme.core", "3.0" ] [ "acme.core", [ at "1.0.0" []; at "2.0.0" [] ] ]
    Assert.Equal<string list>(
        [ "Because app depends on acme.core 3.0, which no version of acme.core in the index satisfies, the dependencies can't all be satisfied." ],
        explanation (VersionSolver.solve input)
    )
    Assert.Equal<string list>(
        [ "Because app depends on acme.missing 1.0, which isn't in the index, the dependencies can't all be satisfied." ],
        explanation (VersionSolver.solve (inputFor [ "acme.missing", "1.0" ] []))
    )

[<Fact>]
let ``A step the explanation uses twice is numbered`` () =
    let input =
        inputFor
            [ "foo", "1.0.0" ]
            [ "foo", [ at "1.0.0" [ "a", "1.0.0"; "b", "1.0.0" ]; at "1.1.0" [ "x", "1.0.0"; "y", "1.0.0" ] ]
              "a", [ at "1.0.0" [ "b", "2.0.0" ] ]
              "b", [ at "1.0.0" []; at "2.0.0" [] ]
              "x", [ at "1.0.0" [ "y", "2.0.0" ] ]
              "y", [ at "1.0.0" []; at "2.0.0" [] ] ]
    Assert.Equal<string list>(
        [ "    Because a 1.0.0 depends on b 2.0.0 and foo 1.0.0 depends on a 1.0.0, foo 1.0.0 requires b 2.0.0."
          "(1) And because foo 1.0.0 depends on b 1.0.0, foo 1.0.0 can't be used."
          ""
          "    Because x 1.0.0 depends on y 2.0.0 and foo 1.1.0 depends on x 1.0.0, foo 1.1.0 requires y 2.0.0."
          "    And because foo 1.1.0 depends on y 1.0.0, foo 1.1.0 can't be used."
          "    And because foo 1.0.0 can't be used (1), no version of foo can be used."
          "    And because app depends on foo 1.0.0, the dependencies can't all be satisfied." ],
        explanation (VersionSolver.solve input)
    )

// Against an index on disk.

let private hashOf (letter: char) = String.replicate 64 (string letter)

/// An entry of an index file: `version`, depending on `dependencies` as the index writes them, with `extra` fields.
let private listed (written: string) (dependencies: (string * string) list) (extra: string) =
    let dependencies = dependencies |> List.map (fun (name, range) -> $"\"{name}\": \"{range}\"") |> String.concat ", "
    $$"""{ "version": "{{written}}", "url": "x.nyxpkg", "sha256": "{{hashOf 'a'}}", "dependencies": { {{dependencies}} }{{extra}} }"""

/// The files of an index that lists `packages`, each a name and its entries, and of an application `app` that depends
/// on `dependencies`, as its manifest writes them.
let private indexAndApp (packages: (string * string list) list) (dependencies: string list) =
    let index =
        packages
        |> List.map (fun (name, entries) ->
            $"index/index/{name.Split('.')[0]}/{name}.json", $"""{{ "name": "{name}", "versions": [{String.concat ", " entries}] }}""")
    [ "index/config.json", """{ "format": 1, "index": "index/" }"""
      "app/app.nyxp", $"""(package = (name = "app", version = "0.1.0"), dependencies = [{String.concat ", " dependencies}])"""
      "app/main.nyx", "export module main\n\nexport def one = 1\n" ]
    @ index

let private resolveIn (package: TempPackage) (lockfile: Lockfile) =
    let manifest = okOf (Manifest.read (package.Path "app/app.nyxp") |> Result.mapError (List.map Manifest.describe))
    Resolution.resolve (storeOf package) manifest lockfile

let private lockedVersions (resolved: Result<Lockfile, ResolutionError>) =
    match resolved with
    | Result.Ok lockfile -> lockfile.Packages |> List.map (fun locked -> $"{locked.Name} {locked.Version}")
    | Result.Error(Conflict diagnostic) -> failwith diagnostic.Message
    | Result.Error(IndexUnreadable message) -> failwith message

let private conflictOf (resolved: Result<Lockfile, ResolutionError>) =
    match resolved with
    | Result.Error(Conflict diagnostic) -> diagnostic
    | other -> failwith $"Expected a conflict, but found {other}"

[<Fact>]
let ``Resolution locks each package the build reaches, with its hash and dependencies`` () =
    use package =
        new TempPackage(
            indexAndApp
                [ "acme.web", [ listed "1.0.0" [ "acme.core", "^2.1"; "nanyx.console.core", Toolchain.version ] "" ]
                  "acme.core", [ listed "2.0.0" [] ""; listed "2.3.0" [] ""; listed "3.0.0" [] "" ] ]
                [ """(package = "acme.web", version = "1.0")""" ]
        )
    match resolveIn package Lockfile.empty with
    | Result.Ok lockfile ->
        Assert.Equal<LockedPackage list>(
            [ { Name = "acme.core"; Version = version "2.3.0"; Sha256 = hashOf 'a'; Dependencies = [] }
              { Name = "acme.web"; Version = version "1.0.0"; Sha256 = hashOf 'a'; Dependencies = [ "acme.core" ] } ],
            lockfile.Packages
        )
    | other -> failwith $"{other}"

[<Fact>]
let ``A yanked version is skipped for a new resolution, but kept when it is already locked`` () =
    use package =
        new TempPackage(
            indexAndApp [ "acme.core", [ listed "1.0.0" [] ""; listed "1.1.0" [] """, "yanked": true""" ] ] [ """(package = "acme.core", version = "1.0")""" ]
        )
    Assert.Equal<string list>([ "acme.core 1.0.0" ], lockedVersions (resolveIn package Lockfile.empty))
    let locked = { Name = "acme.core"; Version = version "1.1.0"; Sha256 = hashOf 'a'; Dependencies = [] }
    Assert.Equal<string list>([ "acme.core 1.1.0" ], lockedVersions (resolveIn package { Packages = [ locked ] }))

[<Fact>]
let ``A version missing from GitHub is never chosen, and a yanked one only when locked`` () =
    use package =
        new TempPackage(
            indexAndApp
                [ "acme.core", [ listed "1.0.0" [] """, "yanked": true"""; listed "1.1.0" [] """, "available": false""" ] ]
                [ """(package = "acme.core", version = "1.0")""" ]
        )
    let diagnostic = conflictOf (resolveIn package Lockfile.empty)
    Assert.Equal(DiagnosticCodes.VersionConflict.Code, diagnostic.Code)
    Assert.Equal("Version conflict 'app'", Diagnostics.headline diagnostic)
    Assert.Equal(
        "Because acme.core 1.0.0 is yanked and acme.core 1.1.0 is missing from GitHub, no version of acme.core can be used.\n"
        + "And because app depends on acme.core 1.0, the dependencies can't all be satisfied.",
        diagnostic.Message
    )

[<Fact>]
let ``A version that needs a newer compiler is passed over, and explained when there is no other`` () =
    let tooNew = """, "nanyx": "99.0" """
    use older =
        new TempPackage(indexAndApp [ "acme.core", [ listed "1.0.0" [] ""; listed "1.1.0" [] tooNew ] ] [ """(package = "acme.core", version = "1.0")""" ])
    Assert.Equal<string list>([ "acme.core 1.0.0" ], lockedVersions (resolveIn older Lockfile.empty))
    use none = new TempPackage(indexAndApp [ "acme.core", [ listed "1.1.0" [] tooNew ] ] [ """(package = "acme.core", version = "1.0")""" ])
    Assert.Equal(
        $"Because acme.core 1.1.0 needs nanyx 99.0 or later (this is {Toolchain.version}) and app depends on acme.core 1.0, the dependencies can't all be satisfied.",
        (conflictOf (resolveIn none Lockfile.empty)).Message
    )

[<Fact>]
let ``The dependencies of a package found by its path are resolved with the build's`` () =
    let files =
        indexAndApp [ "acme.core", [ listed "1.0.0" [] ""; listed "2.0.0" [] "" ] ] [ """(package = "acme.util", path = "../util")""" ]
        @ [ "util/acme.util.nyxp", """(package = (name = "acme.util", version = "0.1.0"), dependencies = [(package = "acme.core", version = "2.0")])"""
            "util/strings.nyx", "export module strings\n\nexport def one = 1\n" ]
    use package = new TempPackage(files)
    Assert.Equal<string list>([ "acme.core 2.0.0" ], lockedVersions (resolveIn package Lockfile.empty))
    File.WriteAllText(package.Path "util/acme.util.nyxp", """(package = (name = "acme.util", version = "0.1.0"), dependencies = [(package = "acme.core", version = "3.0")])""")
    Assert.Equal(
        "Because acme.util depends on acme.core 3.0, which no version of acme.core in the index satisfies, the dependencies can't all be satisfied.",
        (conflictOf (resolveIn package Lockfile.empty)).Message
    )
