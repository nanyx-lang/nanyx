module Nanyx.Compiler.Tests.Features.Refactorings.MakeNominal

open System.IO
open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Make a structural type protected or private, with an attached `of` that makes one from its shape.

/// The offers at `$$` in `marked`, a module in no package, and the source without the mark.
let private offersAt (marked: string) =
    let source, offset = unmarked marked
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let recompile (texts: (string * string) list) = texts |> List.map (snd >> Compiler.compile)
    source, MakeNominal.at Formatter.Options.defaults "" source result offset (fun () -> None) recompile |> List.map Offer.decided

/// The text after the offer titled `title` at `$$`, checked to compile and to be formatted.
let private rewrittenBy (title: string) (marked: string) =
    let source, offers = offersAt marked
    match offers |> List.tryPick (function Available refactoring when refactoring.Title = title -> Some refactoring | _ -> None) with
    | Some refactoring -> refactoredText source refactoring
    | None -> failwithf "expected %s, got %A" title offers

/// The reason the offer titled `title` at `$$` is refused.
let private refusal (title: string) (marked: string) =
    let _, offers = offersAt marked
    match offers |> List.tryPick (function Refused(t, RefactoringKind.Rewrite, reason) when t = title -> Some reason | _ -> None) with
    | Some reason -> reason
    | None -> failwithf "expected %s to be refused, got %A" title offers

[<Fact>]
let ``The type becomes protected, with an of after it`` () =
    Assert.Equal(
        withFinalNewline
            """type Email = protected string

def Email.of: string -> Email = { value ->
  Email(value)
}

def home: Email = "ada@example.com"

def send: Email -> string = { e ->
  e
}""",
        rewrittenBy
            "Make `Email` protected"
            """type Email$$ = string

def home: Email = "ada@example.com"

def send: Email -> string = { e ->
  e
}
""")

[<Fact>]
let ``The type becomes private, generic, with its parameters in the of`` () =
    Assert.Equal(
        withFinalNewline
            """type Box(a) = private (v: a)

def Box.of: (v: a) -> Box(a) = { value ->
  Box(value)
}

def get: Box(int) -> int = { b ->
  b.v
}""",
        rewrittenBy
            "Make `Box` private"
            """type Box$$(a) = (v: a)

def get: Box(int) -> int = { b ->
  b.v
}
""")

[<Fact>]
let ``An of the type has already is kept`` () =
    Assert.Equal(
        withFinalNewline
            """type Email = protected string

def Email.of: string -> Email = { s ->
  s
}"""
        ,
        rewrittenBy
            "Make `Email` protected"
            """type Email$$ = string

def Email.of: string -> Email = { s ->
  s
}
""")

[<Fact>]
let ``Not offered on a type that is nominal already`` () =
    let _, offers = offersAt "type Email$$ = protected string\n"
    Assert.Empty(offers)

[<Fact>]
let ``Refused for a type whose shape is a type variable`` () =
    Assert.Equal("The shape of `Same` is a type variable", refusal "Make `Same` protected" "type Same$$(a) = a\n")

[<Fact>]
let ``Refused for an exported type in a file in no package`` () =
    Assert.Equal(
        "`Email` is exported, and this file is in no package, so the files that use it can't be found",
        refusal "Make `Email` private" "export type Email$$ = string\n")

// An exported type in a package: the other files that give a value of its shape where it is expected call `of`.

let private manifest = """(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))"""

let private emails =
    withFinalNewline """module emails

export type Email = string

export def domain: Email -> string = { e ->
  e
}"""

/// The offer titled `title` on `Email` in `emails` of a package of it and `main`, with each file's text after it, by
/// name, or why it is refused.
let private madeIn (title: string) (main: string) =
    use package = new TempPackage([ "app.nyxp", manifest; "src/emails.nyx", emails; "src/main.nyx", main ])
    let path = Path.GetFullPath(package.Path "src/emails.nyx")
    let compile (readFile: string -> string) =
        (Package.compilePackageReading readFile path).Value
        |> List.map (fun (file, result) -> { Rename.Path = Path.GetFullPath file; Rename.Source = readFile file; Rename.Result = result })
    let files = compile File.ReadAllText
    for file in files do
        Assert.True(file.Result.Diagnostics.IsEmpty, sprintf "%A" file.Result.Diagnostics)
    let recompile (texts: (string * string) list) =
        let changed = Map.ofList texts
        let compiled = compile (fun file -> changed |> Map.tryFind (Path.GetFullPath file) |> Option.defaultWith (fun () -> File.ReadAllText file))
        texts |> List.map (fun (path, _) -> (compiled |> List.find (fun file -> file.Path = path)).Result)
    let home = files |> List.find (fun file -> file.Path = path)
    let offset = offsetOf home.Source "Email =" 0 0
    let offers = MakeNominal.at Formatter.Options.defaults path home.Source home.Result offset (fun () -> Some files) recompile |> List.map Offer.decided
    match offers |> List.find (fun offer -> fst (Offer.titleAndKind offer) = title) with
    | Available refactoring ->
        Ok (files |> List.map (fun file -> Path.GetFileName file.Path, refactoring.Edits |> List.filter (fun edit -> edit.Span.File = file.Path) |> applyEdits file.Source) |> Map.ofList)
    | Refused(_, _, reason) -> Error reason
    | Deferred _ -> failwith "the offer was not decided"

[<Fact>]
let ``Another file of the package wraps the values of the shape it gives where the type is expected`` () =
    let main =
        withFinalNewline """module main

import app/emails

def host = domain("ada@example.com")
def twice = [domain("bob@example.com"), "x"]"""
    match madeIn "Make `Email` protected" main with
    | Ok texts ->
        Assert.Equal(
            withFinalNewline """module emails

export type Email = protected string

export def Email.of: string -> Email = { value ->
  Email(value)
}

export def domain: Email -> string = { e ->
  e
}""",
            texts["emails.nyx"])
        Assert.Equal(
            withFinalNewline """module main

import app/emails

def host = domain(Email.of("ada@example.com"))
def twice = [domain(Email.of("bob@example.com")), "x"]""",
            texts["main.nyx"])
    | Error reason -> failwith reason

[<Fact>]
let ``Refused where another file reads the shape of a type made private`` () =
    let main =
        withFinalNewline """module main

import app/emails

def size: Email -> int = { e ->
  string.length(e)
}"""
    match madeIn "Make `Email` private" main with
    | Error reason -> Assert.StartsWith("The result would not compile: ", reason)
    | Ok texts -> failwithf "expected a refusal, got %A" texts
