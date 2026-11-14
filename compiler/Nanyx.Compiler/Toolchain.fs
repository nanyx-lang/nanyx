namespace NyxCompiler

open System.Reflection

/// The compiler's release.
module Toolchain =
    /// The release version, the `<Version>` in compiler/Directory.Build.props (such as 0.28.0): the assembly's
    /// informational version without the commit the SDK appends after a '+'. The packages the compiler ships are this
    /// version, and a manifest names them at it exactly.
    let version =
        let informational =
            match typeof<Severity>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
            | null -> ""
            | attribute -> attribute.InformationalVersion
        match informational.IndexOf '+' with
        | -1 -> informational
        | plus -> informational.Substring(0, plus)
