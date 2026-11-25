namespace NyxCompiler

open Parser.Program

/// Units of measure: `dimension Time`, `@baseUnit unit ns: Time`, `unit ms: Time = 1_000us` and `500ms` (see
/// `UnitSyntax`). The parser reads them; the checker does not check them yet, so it reports each where it is written.
module internal Units =
    /// The error for a dimension's or a unit's declaration, written at `span`.
    let declarationNotChecked (span: SourceSpan) : Diagnostic =
        Diagnostics.at DiagnosticCodes.UnitsNotChecked span
            "Dimensions and units are read but not checked yet, so a program can't declare them: units of measure are being built"

    /// The error for a number with a unit, `500ms`, written at `span`.
    let literalNotChecked (span: SourceSpan) : Diagnostic =
        Diagnostics.at DiagnosticCodes.UnitsNotChecked span
            "A number with a unit is read but not checked yet, so a program can't use one: units of measure are being built"
