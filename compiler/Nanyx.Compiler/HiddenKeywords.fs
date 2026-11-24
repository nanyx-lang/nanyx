/// A `#delay` keyword's call, `k! e`, is `k({ e })` (see `WorkflowDesugaring`), so a local def, a parameter or a
/// pattern's variable named `k` hides the keyword's def, as any nearer name does: `def start = now()` makes `start!`
/// call the local. That is allowed, and a local that takes a block is called. One that doesn't fit gives a mismatch
/// that says nothing of the local, so the mismatch at such a call is reported at the keyword, naming the local.
module internal NyxCompiler.HiddenKeywords

open Parser.Program
open NyxCompiler.InferState

/// `state` with the call `call` of `name`, bound in `env` by `scheme`, noted if it is a `#delay` keyword's whose
/// keyword names a local rather than the keyword's def.
let note (env: TypeEnv) (call: Expression) (name: Identifier) (scheme: TypeScheme) (state: InferState) : InferState =
    match state.DelayCalls |> Map.tryFind call.Span, scheme.Symbol |> Option.bind state.Symbols.TryFind with
    | Some (at, keyword), Some local when keyword = name ->
        match local.Kind with
        | LocalValue
        | Parameter
        | PatternVariable ->
            let keywordDef =
                match AttachedContextDefs.qualifiedFor env state name at with
                | Some (Ok qualified) -> Some qualified
                | _ -> None
            let hidden = { Keyword = keyword; At = at; Local = local; LocalType = scheme.Type; KeywordDef = keywordDef }
            { state with HiddenKeywords = state.HiddenKeywords |> Map.add call.Span hidden }
        | _ -> state
    | _ -> state

/// The error for a mismatch at the call `hidden` notes, at its keyword: the local is in the way. `typeText` writes a
/// type as the module's other errors do.
let diagnostic (typeText: Ty -> string) (hidden: HiddenKeyword) : Diagnostic =
    let what =
        match hidden.Local.Kind with
        | Parameter -> "parameter"
        | PatternVariable -> "pattern's variable"
        | _ -> "local"
    let instead, insteadAdvice =
        match hidden.KeywordDef with
        | Some keywordDef -> $"'{keywordDef}'", $", or write '{keywordDef}({{ ... }})'"
        | None -> "the keyword's own def", ""
    Diagnostics.at
        DiagnosticCodes.TypeMismatch
        hidden.At
        $"'{hidden.Keyword}!' calls the {what} '{hidden.Keyword}' defined at line {hidden.Local.Declaration.StartLine + 1}, of type '{typeText hidden.LocalType}', not {instead}: rename the {what}{insteadAdvice}"
    |> Diagnostics.about hidden.Keyword
