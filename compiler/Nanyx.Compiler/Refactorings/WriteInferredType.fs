/// Write the inferred type: on the name of a def or a lambda parameter that does not write its type, write the type
/// the compiler inferred for it, as hover and the type hints show it. Positions are character offsets into the source
/// as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.WriteInferredType

open Parser.Program
open NyxCompiler

let private title = "Write the inferred type"

/// The edit that writes `hint`'s annotation into `source`: after its name, `: type`, or after the contexts its def
/// writes, ` type`.
let private edit (source: string) (hint: TypeHints.TypeHint) (annotation: string) : TextEdit =
    let after = hint.Context |> Option.defaultValue hint.Span
    let text = if hint.Context.IsSome then $" {annotation}" else $": {annotation}"
    { Span = TextEdit.spanBetween source after after.End after.End; NewText = text }

/// Why a declaration's type can't be written, when its hint has no annotation.
let private refusal (hint: TypeHints.TypeHint) =
    if hint.Type.Contains "???" then "Its type has a part that failed to check"
    else
        match hint.Kind with
        | PatternVariable -> "A pattern variable's type can't be written"
        | _ -> "It is one of the names a destructuring def binds, which has no one place to write a type"

/// The refactoring at `offset` in `source`, the text of the root module of `result` compiled as `file`: on the name of
/// a def or a lambda parameter that does not write its type, the edit that writes it, with type variables named as
/// source names them. Its contexts are written before it (`def add: [Add(a)] a, a -> a`), unless the def writes
/// them already, when the rest of the type is written after them. `None` where no name that could have its type
/// written is, or where the module did not type-check.
let at (file: string) (source: string) (result: CompileResult) (offset: int) : Offer option =
    let source = source.Replace("\r\n", "\n").Replace("\r", "\n")
    TypeHints.inlayHintsWithTopLevel Source file result
    |> List.tryFind (fun hint -> hint.Span.Start <= offset && offset <= hint.Span.End)
    |> Option.map (fun hint ->
        match hint.Annotation with
        | Some annotation ->
            Available { Title = $"{title} of `{hint.Name}`"; Kind = RefactoringKind.Rewrite; Edits = [ edit source hint annotation ]; Rename = None }
        | None -> Refused($"{title} of `{hint.Name}`", RefactoringKind.Rewrite, refusal hint))
