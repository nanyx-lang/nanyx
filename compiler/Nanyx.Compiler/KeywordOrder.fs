/// An exported scoped context is written `export scoped context`: `export` comes first, as on every definition, and
/// `scoped` beside `context`, which it describes (#2100). Earlier versions of the language wrote `scoped export context`,
/// which the parser still reads so that it can be reported here, with a fix that writes the keywords in order. A module
/// that writes them so is reported before anything is inferred from it.
module NyxCompiler.KeywordOrder

open Parser.Program

/// The error for `scoped export`, if `source` has it at `scoped`, the span of the `scoped` of a definition.
let private outOfOrder (source: string) (scoped: SourceSpan) : Diagnostic option =
    if SourceSpan.isNone scoped || scoped.End > source.Length then None
    else
        let mutable at = scoped.End
        while at < source.Length && System.Char.IsWhiteSpace source.[at] do at <- at + 1
        let exportEnd = at + "export".Length
        if exportEnd <= source.Length && source.Substring(at, "export".Length) = "export"
           && (exportEnd = source.Length || not (System.Char.IsLetterOrDigit source.[exportEnd] || source.[exportEnd] = '_')) then
            let span = TextEdit.spanBetween source scoped scoped.Start exportEnd
            let diagnostic =
                Diagnostics.at DiagnosticCodes.KeywordsOutOfOrder span "Write `export` before `scoped`: `export scoped context`"
            Some { diagnostic with Fixes = [ { Title = "Write `export scoped`"; Edits = [ { Span = span; NewText = "export scoped" } ] } ] }
        else None

/// The error for each context in `module'`, whose text is `source`, written `scoped export context`.
let check (source: string) (module': Module) : Diagnostic list =
    SyntaxTree.typeDefinitions module'
    |> List.choose (function
        | TypeDef(_, modifiers, _, _) -> TypeDefModifier.scopedSpan modifiers
        | ValueDef _ -> None)
    |> List.choose (outOfOrder source)
    |> List.sortBy (fun diagnostic -> diagnostic.Span |> Option.map _.Start |> Option.defaultValue 0)
