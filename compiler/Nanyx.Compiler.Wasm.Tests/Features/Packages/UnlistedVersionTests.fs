/// A version the index has unlisted (#2389): no new resolution chooses it, but a lockfile that locks it, or a script
/// that pins it, keeps it, and a command that reads the index for it warns (`NYX0051`) and goes on. A command that reads
/// only the package cache doesn't warn.
module TranspilerWasmTests.Features.Packages.UnlistedVersion

open System.IO
open Xunit
open NyxCompiler
open TranspilerWasmTestHelpers

let private normalised (text: string) = text.ReplaceLineEndings "\n"

/// The warning, as the command line shows it, that acme.core 1.1.0, which `namedBy` names, is unlisted, with how to
/// move off it, `move`.
let private warning (namedBy: string) (move: string) =
    "warning NYX0051: Unlisted version 'acme.core 1.1.0'\n"
    + $"  = acme.core 1.1.0, which {namedBy}, is unlisted, so no new resolution chooses it: {move}, or keep it if you need it\n"

let private lockedWarning =
    warning
        "'nanyx.lock' locks"
        "to move to a listed version, delete its entry from 'nanyx.lock' and run `nanyx pull`, first changing the version the manifest asks for if it accepts no listed one"

/// The index of `store` with acme.core 1.0.0, and 1.1.0 unlisted, or listed unless `unlisted`.
let private publishBoth (directory: TempDirectory) (store: PackageStore) (unlisted: bool) =
    publishCore directory store "1.0.0" 1 ""
    publishCore directory store "1.1.0" 2 (if unlisted then """, "unlisted": true""" else "")

/// An app that prints acme.core's answer, depending on acme.core 1.0 and on `more`, with a lockfile that locks
/// acme.core 1.1.0. Gives its directory.
let private appLocking (directory: TempDirectory) (more: string) =
    directory.Write
        "app/app.nyxp"
        $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  {consoleHostEntry}\n  dependencies = [(package = \"acme.core\", version = \"1.0\"){more}]\n)\n"
    directory.Write "app/main.nyx" "module main\n\nimport acme.core/core as core\n\n@entry\ndef main = { println(core.answer) }\n"
    let hash = File.ReadAllBytes(Path.Combine(directory.Path, "index", "artifacts", "acme.core-1.1.0.nyxpkg")) |> PackageArchive.sha256
    let locked = { Name = "acme.core"; Version = (SemanticVersion.tryParse "1.1.0").Value; Sha256 = hash; Dependencies = [] }
    directory.Write "app/nanyx.lock" (Lockfile.write { Packages = [ locked ] })
    Path.Combine(directory.Path, "app")

/// The script `script.nyx`, in its own directory, that pins acme.core 1.1.0 and prints its answer. Gives its directory.
let private scriptPinning (directory: TempDirectory) =
    directory.Write "scripts/script.nyx" "::install acme.core 1.1.0\n\nimport acme.core/core as core\n\nprintln(core.answer)\n"
    Path.Combine(directory.Path, "scripts")

[<Fact>]
let ``Pull - an unlisted version nanyx.lock locks is downloaded, with a warning`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishBoth directory store true
    let app = appLocking directory ""
    let pulled = nanyxInStore store app [ "pull" ]
    Assert.Equal(lockedWarning, normalised pulled.StandardError)
    Assert.Equal("acme.core 1.1.0 (downloaded)\n", normalised pulled.StandardOutput)
    Assert.Equal(0, pulled.ExitCode)

[<Fact>]
let ``Pull - once the cache holds the version, nothing reads the index and nothing warns`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishBoth directory store true
    let app = appLocking directory ""
    Assert.Equal(0, (nanyxInStore store app [ "pull" ]).ExitCode)
    let again = nanyxInStore store app [ "pull" ]
    Assert.Equal("", again.StandardError)
    Assert.Equal("acme.core 1.1.0 (in the cache)\n", normalised again.StandardOutput)

[<Fact>]
let ``Build - locking a new dependency keeps the unlisted version, with a warning, and the build goes on`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishBoth directory store true
    directory.Write "web/web.nyxp" "(package = (name = \"acme.web\", version = \"1.0.0\"))\n"
    directory.Write "web/web.nyx" "export module web\n\nexport def page = 1\n"
    publishToIndex store (Path.Combine(directory.Path, "web")) [] "" |> ignore
    let app = appLocking directory """, (package = "acme.web", version = "1.0")"""
    let built = nanyxInStore store app [ "run" ]
    Assert.StartsWith("Locked acme.web 1.0.0\n" + lockedWarning, normalised built.StandardError)
    Assert.Equal("2\n", normalised built.StandardOutput)
    Assert.Equal(0, built.ExitCode)

[<Fact>]
let ``Pull - deleting the unlisted version's entry from nanyx.lock moves to a listed one`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishBoth directory store true
    let app = appLocking directory ""
    File.WriteAllText(Path.Combine(app, "nanyx.lock"), Lockfile.write Lockfile.empty)
    let pulled = nanyxInStore store app [ "pull" ]
    Assert.Equal("", pulled.StandardError)
    Assert.Equal("Locked acme.core 1.0.0\nacme.core 1.0.0 (downloaded)\n", normalised pulled.StandardOutput)

[<Fact>]
let ``Run - an unlisted version a script pins is used, with a warning`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishBoth directory store true
    let scripts = scriptPinning directory
    let ran = nanyxInStore store scripts [ "run"; "script.nyx" ]
    Assert.Equal(
        warning "'script.nyx' pins with `::install`" "to move to a listed version, change the version its `::install` names",
        normalised ran.StandardError
    )
    Assert.Equal("2\n", normalised ran.StandardOutput)
    Assert.Equal(0, ran.ExitCode)
    let pulled = nanyxInStore store scripts [ "pull"; "script.nyx" ]
    Assert.StartsWith("warning NYX0051: Unlisted version 'acme.core 1.1.0'\n", normalised pulled.StandardError)
    Assert.Equal("acme.core 1.1.0\n", normalised pulled.StandardOutput)

[<Fact>]
let ``A listed version, locked or pinned, doesn't warn`` () =
    use directory = new TempDirectory()
    let store = storeIn directory
    publishBoth directory store false
    let pulled = nanyxInStore store (appLocking directory "") [ "pull" ]
    Assert.Equal("", pulled.StandardError)
    Assert.Equal("acme.core 1.1.0 (downloaded)\n", normalised pulled.StandardOutput)
    let ran = nanyxInStore store (scriptPinning directory) [ "run"; "script.nyx" ]
    Assert.Equal("", ran.StandardError)
    Assert.Equal("2\n", normalised ran.StandardOutput)
