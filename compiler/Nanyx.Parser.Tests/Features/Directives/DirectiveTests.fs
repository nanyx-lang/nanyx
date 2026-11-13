module ParserTests.Features.Directives

open Xunit
open Parser.Program

/// The key and value of the one directive at the top of `source`.
let private directiveOf (source: string) =
    match Directives.read "t.nyx" source with
    | [ directive ] -> directive.Key, directive.Value
    | other -> failwith $"Expected one directive, got %A{other}"

[<Fact>]
let ``A table directive has its dotted key and no value`` () =
    Assert.Equal((([ "linters"; "default" ], None): string list * Result<obj, string> option), directiveOf "::linters.default\n")

[<Fact>]
let ``A directive with a value has it as TOML reads it`` () =
    match directiveOf "::formatter.lineWidth =  80\n" with
    | [ "formatter"; "lineWidth" ], Some (Result.Ok (:? int64 as width)) -> Assert.Equal(80L, width)
    | other -> failwith $"Unexpected directive: %A{other}"
    match directiveOf "::linters.default.unusedBindings = \"error\"\n" with
    | [ "linters"; "default"; "unusedBindings" ], Some (Result.Ok (:? string as severity)) -> Assert.Equal("error", severity)
    | other -> failwith $"Unexpected directive: %A{other}"

[<Fact>]
let ``A quoted part of the key is one part, whatever it holds`` () =
    match directiveOf "::dependencies.\"acme.json=2\" = \"1.2\"\n" with
    | [ "dependencies"; "acme.json=2" ], Some (Result.Ok (:? string as version)) -> Assert.Equal("1.2", version)
    | other -> failwith $"Unexpected directive: %A{other}"

[<Fact>]
let ``A value that is not TOML is an error saying so, under the directive's key`` () =
    match directiveOf "::formatter.lineWidth = wide\n" with
    | [ "formatter"; "lineWidth" ], Some (Result.Error message) ->
        Assert.Equal("'wide' is not a TOML value; a string needs quotes, as in a manifest: \"wide\"", message)
    | other -> failwith $"Unexpected directive: %A{other}"

[<Fact>]
let ``A directive that is not a TOML key has no key`` () =
    Assert.Empty(fst (directiveOf "::linter default\n"))
    Assert.Empty(fst (directiveOf "::\n"))
