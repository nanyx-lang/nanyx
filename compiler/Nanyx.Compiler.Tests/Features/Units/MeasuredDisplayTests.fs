/// Measured values written as text (#2624): the `ToString` a generic storage needs, and the intrinsic a derived
/// `toString` reads a measured value's storage with.
module Nanyx.Compiler.Tests.Features.MeasuredDisplay

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport
open Nanyx.Compiler.Tests.Features.UnitsFixture

[<Fact>]
let ``A hole on a measured value of a known storage needs no context`` () =
    let result = Compiler.compile (withFinalNewline (time + "def a = \"{1500ms}\"\ndef b = \"{1.5ms}\""))
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A hole on a measured value of a generic storage needs the storage's ToString`` () =
    let code, message, _ = onlyDiagnostic (withFinalNewline (time + "def show: Time(a) -> string = { t -> \"{t}\" }"))
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, code)
    Assert.Contains("ToString(a)", message)

[<Fact>]
let ``The storage intrinsic takes only a measured value`` () =
    let code, message, _ = onlyDiagnostic (withFinalNewline (time + "def a = __nanyx_measured_storage(3)"))
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Contains("takes a measured value", message)
