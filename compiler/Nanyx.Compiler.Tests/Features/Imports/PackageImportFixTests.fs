module Nanyx.Compiler.Tests.Features.PackageImportFixes

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// A name another module of the package, or a public module of a package it depends on, exports gets a fix that imports
// it, written as the file writes its imports (#1952).

/// A library, acme.util, whose public module `strings` exports `shout`, in a directory beside the app's.
let private util =
    [ "util/util.nyxp", "(package = (name = \"acme.util\", version = \"0.1.0\"))\n"
      "util/strings.nyx", "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n" ]

/// The app's manifest, which depends on acme.util and is run by the console host.
let private manifest =
    $"(\n  package = (name = \"app\", version = \"0.1.0\")\n  dependencies = [(package = \"acme.util\", path = \"../util\")]\n  {consoleHostEntry}\n)\n"

/// The module `billing`, which exports a type, a value and a context.
let private billing =
    """module billing

export type Money = (amount: int)

export def zero: Money = (amount = 0)

export context Priced(a) = (price: a -> Money)
"""

/// The module `catalogue`, which exports a type and a value that `ledger` exports too.
let private catalogue =
    """module catalogue

export type Book = (title: string)

export def total: int = 1
"""

/// Each fix for the one unknown name in `main`, the file `app/main.nyx` of the app beside `files` (each a path in the
/// app's directory and its text), as its title and the text it gives, checked to compile and to be formatted; and the
/// error's message.
let private fixesIn (files: (string * string) list) (main: string) =
    let main = normalized main
    use package = new TempPackage(util @ [ "app/app.nyxp", manifest; "app/main.nyx", main ] @ (files |> List.map (fun (path, text) -> $"app/{path}", normalized text)))
    let compile (text: string) = Package.compileSourceWith (SeededPackages.user ()) Linters.none (package.Path "app/main.nyx") text
    let result = compile main
    let diagnostic = single result
    let fixes =
        diagnostic.Fixes
        |> List.map (fun fix ->
            let after = applyEdits main fix.Edits
            let compiled = compile after
            Assert.True(compiled.Diagnostics.IsEmpty, sprintf "%s\n%A" after compiled.Diagnostics)
            match FormatCommand.formatText Formatter.Options.defaults "" after with
            | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
            | other -> failwithf "%A" other
            fix.Title, after)
    // The refactoring at the name offers the same imports.
    let offset = diagnostic.Span.Value.Start
    Assert.Equal<string list>(fixes |> List.map fst, ImportName.at result offset |> List.map (Offer.titleAndKind >> fst))
    fixes, diagnostic.Message

[<Fact>]
let ``An unknown type another module of the package exports is imported on a line of its own`` () =
    let fixes, _ = fixesIn [ "billing.nyx", billing ] "module main\n\nexport def fine: Money = (amount = 5)\n"
    Assert.Equal<(string * string) list>(
        [ "Import `Money` from `billing`", "module main\n\nimport billing\n\nexport def fine: Money = (amount = 5)\n" ],
        fixes)

[<Fact>]
let ``An unknown value and an unknown context are imported too`` () =
    let value, _ = fixesIn [ "billing.nyx", billing ] "module main\n\nexport def owed = zero\n"
    Assert.Equal<string list>([ "Import `zero` from `billing`" ], value |> List.map fst)
    let context, _ = fixesIn [ "billing.nyx", billing ] "module main\n\nexport def priced: [Priced(a)] a -> int = { x -> 1 }\n"
    Assert.Equal<string list>([ "Import `Priced` from `billing`" ], context |> List.map fst)

[<Fact>]
let ``The module is added to an import block in the formatter's order`` () =
    let fixes, _ =
        fixesIn
            [ "billing.nyx", billing; "catalogue.nyx", catalogue ]
            "module main\n\nimport (\n  acme.util/strings\n  catalogue\n)\n\nexport def fine: Money = (amount = total)\n"
    Assert.Equal<(string * string) list>(
        [ "Import `Money` from `billing`",
          "module main\n\nimport (\n  acme.util/strings\n  billing\n  catalogue\n)\n\nexport def fine: Money = (amount = total)\n" ],
        fixes)

[<Fact>]
let ``A name is added to an import that selects names from its module`` () =
    let fixes, _ = fixesIn [ "billing.nyx", billing ] "module main\n\nimport billing as (zero)\n\nexport def fine: Money = zero\n"
    Assert.Equal<(string * string) list>(
        [ "Import `Money` from `billing`", "module main\n\nimport billing as (zero, Money)\n\nexport def fine: Money = zero\n" ],
        fixes)

[<Fact>]
let ``A name two modules export offers both, the module nearest the file first`` () =
    let near = "module near\n\nexport def total: int = 2\n"
    let far = "module far\n\nexport def total: int = 3\n"
    let fixes, _ = fixesIn [ "deep/er/far.nyx", far; "near.nyx", near ] "module main\n\nexport def sum = total\n"
    Assert.Equal<string list>([ "Import `total` from `near`"; "Import `total` from `far`" ], fixes |> List.map fst)

[<Fact>]
let ``A name a dependency's public module exports is imported by the package's name`` () =
    let fixes, _ = fixesIn [] "module main\n\nexport def loud = shout(\"hi\")\n"
    Assert.Equal<(string * string) list>(
        [ "Import `shout` from `acme.util/strings`", "module main\n\nimport acme.util/strings\n\nexport def loud = shout(\"hi\")\n" ],
        fixes)

[<Fact>]
let ``An import that would make a name the file uses ambiguous is not offered, and the error says why`` () =
    let ledger = "module ledger\n\nexport type Entry = (amount: int)\n\nexport def total: int = 2\n"
    let fixes, message =
        fixesIn [ "catalogue.nyx", catalogue; "ledger.nyx", ledger ] "module main\n\nimport catalogue\n\nexport def entry: Entry = (amount = total)\n"
    Assert.Empty fixes
    Assert.EndsWith("'ledger' exports 'Entry', but importing it would make 'total' ambiguous between 'catalogue' and 'ledger'", message)

[<Fact>]
let ``A compile reads the package's other modules for an unknown name, and not for another error`` () =
    // A file a compile reads is one its module depends on, which the language server compiles it again for whenever it
    // changes: any error read them all (#2222).
    let wrong = normalized "module main\n\nexport def wrong: string = 1\n"
    let unknown = normalized "module main\n\nexport def fine: Money = (amount = 5)\n"
    use package = new TempPackage(util @ [ "app/app.nyxp", manifest; "app/main.nyx", wrong; "app/billing.nyx", normalized billing ])
    let readFor (main: string) =
        let read = System.Collections.Generic.List<string>()
        let readFile (path: string) =
            read.Add(System.IO.Path.GetFileName path)
            System.IO.File.ReadAllText path
        let result = Package.compileSourceReading (SeededPackages.user ()) CompileListener.none readFile Linters.none (package.Path "app/main.nyx") main
        Assert.NotEmpty(result.Diagnostics)
        // The package's other module and its dependency's public module, of the files read (the host's are read too).
        read |> Seq.filter (fun file -> file = "billing.nyx" || file = "strings.nyx") |> Seq.distinct |> Seq.sort |> List.ofSeq
    Assert.Equal<string list>([], readFor wrong)
    Assert.Equal<string list>([ "billing.nyx"; "strings.nyx" ], readFor unknown)
