module Nanyx.Compiler.Tests.Features.Diagnostics.DiagnosticCodes

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Every compiler code is distinct and has the NYX form`` () =
    let codes = DiagnosticCodes.all |> List.map (fun code -> code.Code)
    Assert.Equal(codes.Length, (List.distinct codes).Length)
    for code in codes do
        Assert.Matches("^NYX\\d{4}$", code)

[<Fact>]
let ``The compiler reports only critical and error severities, but for the warnings that a version is unlisted and that a shipped package differs from the index's or was replaced`` () =
    let warnings = [ DiagnosticCodes.UnlistedVersion; DiagnosticCodes.ShippedPackageDiffers; DiagnosticCodes.ShippedPackageReplaced ]
    for code in DiagnosticCodes.all |> List.filter (fun code -> not (List.contains code warnings)) do
        Assert.True(code.Severity = Severity.Critical || code.Severity = Severity.Error, $"{code.Code} is {code.Severity}")
    for code in warnings do
        Assert.Equal(Severity.Warning, code.Severity)
