/// A host's name ends in `.host`, and only a host's does (`NYX0053`), and a package the compiler ships named by the name
/// it had before that rule is told its new one (`NYX0054`).
module Nanyx.Compiler.Tests.Features.Packages.HostNames

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// Each problem with the manifest `text`, as its code and message; none if it reads.
let private problemsOf (text: string) =
    match Manifest.check "demo.nyxp" (normalized text) with
    | Result.Error errors -> errors |> List.map (fun error -> error.Code, error.Message)
    | Result.Ok _ -> []

let private hostConvention = DiagnosticCodes.HostNameConvention.Code
let private renamed = DiagnosticCodes.RenamedPackage.Code

[<Fact>]
let ``A host whose name doesn't end in .host is an error at its name`` () =
    Assert.Equal<(string * string) list>(
        [ hostConvention,
          "The package 'acme.server' declares a `host`, so its name must end in `.host`, as 'acme.server.host' does: a host is named for the library it runs programs against, with `.host` after it" ],
        problemsOf """(package = (name = "acme.server", version = "0.1.0", host = (root = "server.host")))""")

[<Fact>]
let ``A library whose name ends in .host is an error at its name`` () =
    Assert.Equal<(string * string) list>(
        [ hostConvention,
          "The package 'acme.server.host' declares no `host`, so its name must not end in `.host`, which only a host's does: rename it, or declare the host it is" ],
        problemsOf """(package = (name = "acme.server.host", version = "0.1.0"))""")

[<Fact>]
let ``A host named .host and a library named otherwise read`` () =
    Assert.Empty(problemsOf """(package = (name = "acme.server.host", version = "0.1.0", host = (root = "server.host")))""")
    Assert.Empty(problemsOf """(package = (name = "acme.server", version = "0.1.0"))""")

[<Fact>]
let ``A dependency on a shipped package by its old name gives its new one`` () =
    let dependingOn (name: string) =
        problemsOf $"""(package = (name = "demo", version = "0.1.0"), dependencies = [(package = "{name}", version = "{Toolchain.version}")])"""
    Assert.Equal<(string * string) list>([ renamed, "'nanyx.console.core' is now 'nanyx.cli'" ], dependingOn "nanyx.console.core")
    Assert.Equal<(string * string) list>([ renamed, "'nanyx.http.core' is now 'nanyx.http'" ], dependingOn "nanyx.http.core")
    Assert.Equal<(string * string) list>(
        [ renamed, "'nanyx.console' is now 'nanyx.cli.host', a host: a program names its host in its manifest's `host`, not as a dependency" ],
        dependingOn "nanyx.console")
    // `nanyx.http` is the library's name now.
    Assert.Empty(dependingOn "nanyx.http")

[<Fact>]
let ``A host named by its old name gives its new one`` () =
    let hostedBy (name: string) =
        problemsOf $"""(package = (name = "demo", version = "0.1.0"), host = (package = "{name}", version = "{Toolchain.version}"))"""
    Assert.Equal<(string * string) list>([ renamed, "The host 'nanyx.console' is now 'nanyx.cli.host'" ], hostedBy "nanyx.console")
    Assert.Equal<(string * string) list>([ renamed, "The host 'nanyx.playground' is now 'nanyx.playground.host'" ], hostedBy "nanyx.playground")
    Assert.Equal<(string * string) list>(
        [ renamed, "The HTTP host is now 'nanyx.http.host': 'nanyx.http' is the library it runs programs against" ],
        hostedBy "nanyx.http")
    Assert.Empty(hostedBy "nanyx.http.host")

[<Fact>]
let ``A script's directives and --host name a shipped package by its new name`` () =
    let diagnosticsOf (source: string) =
        (Script.read "" (withFinalNewline source)).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message)
    Assert.Equal<(string * string) list>(
        [ renamed, "The host 'nanyx.console' is now 'nanyx.cli.host'" ],
        diagnosticsOf "::host nanyx.console\n\nprintln(1)")
    Assert.Equal<(string * string) list>(
        [ renamed, "'nanyx.http.core' is now 'nanyx.http'" ],
        diagnosticsOf "::dependency nanyx.http.core\n\nprintln(1)")
    // `--host` reads a shipped host by its name.
    match HostPackages.tryShipped "nanyx.console" with
    | Result.Error message -> Assert.Equal("The host 'nanyx.console' is now 'nanyx.cli.host'", message)
    | Result.Ok _ -> Assert.Fail "Expected the old name to be refused"
