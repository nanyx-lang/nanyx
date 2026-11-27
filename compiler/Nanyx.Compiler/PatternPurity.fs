/// A custom pattern is pure: nothing in its body requires a context, so it can't print, read input or change a cell, and
/// how often it runs never matters (see "Patterns are pure" on the docs' Custom patterns page).
module internal NyxCompiler.PatternPurity

open Parser.Program

/// Whether the def `defName` is a custom pattern, as `pattern` declares it.
let isPattern (defName: Identifier) = (PatternSyntax.shapeOf defName).IsSome

/// The error for `what` (which ends "requires", say) the context `ctxName` at `span`, in a pattern's body.
let requires (what: string) (ctxName: Identifier) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.PatternRequiresContexts span $"{what} the context '{ctxName}', but a pattern is pure: nothing in its body can require a context"
