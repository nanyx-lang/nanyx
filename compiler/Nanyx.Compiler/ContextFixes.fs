/// The fix for a def that needs a context its type does not declare: it adds the context to the def's type, after
/// the contexts it writes, joined with `&` (`[Num(a)]` becomes `[Num(a) & Ord(a)]`), or in front of its type when it
/// writes none (`a -> a` becomes `[Ord(a)] a -> a`).
module NyxCompiler.ContextFixes

open Parser.Program
open NyxCompiler.InferState

/// The fix that adds `context`, as it is written, to the type of the def `defName`, if it writes one. None where the
/// type was made up rather than written.
let addToType (state: InferState) (defName: Identifier) (context: string) : Fix list =
    let edit =
        match state.WrittenTypes |> Map.tryFind defName with
        | Some { Kind = TypeWithContext(contexts, _) } ->
            match List.tryLast contexts with
            | Some last when not (SourceSpan.isNone last.Span) -> Some { Span = { last.Span with Start = last.Span.End }; NewText = $" & {context}" }
            | _ -> None
        | Some written when not (SourceSpan.isNone written.Span) ->
            Some { Span = { written.Span with End = written.Span.Start }; NewText = $"[{context}] " }
        | _ -> None
    edit |> Option.map (fun edit -> { Title = $"Add `{context}` to the type of '{defName}'"; Edits = [ edit ] }) |> Option.toList

/// Whether the def `defName` writes only contexts, not its type, and among them `ctxName` of the type variable
/// `varName`, as `[ToString(a)]` does. Its type is inferred, so that `a` is not the type of any of its parameters,
/// though it is written the same as one's.
let writesOnContextsAlone (state: InferState) (defName: Identifier) (ctxName: Identifier) (varName: string) =
    match state.WrittenTypes |> Map.tryFind defName with
    | Some { Kind = TypeContext contexts } ->
        contexts |> List.exists (fun ctx -> match ctx.Kind with TypeApply(name, [ Kind (TypeVar v) ]) -> name = ctxName && v = varName | _ -> false)
    | _ -> false

/// `diagnostic` with the fix that adds `context` to the type of `defName`.
let withAddToType (state: InferState) (defName: Identifier) (context: string) (diagnostic: Diagnostic) : Diagnostic =
    { diagnostic with Fixes = diagnostic.Fixes @ addToType state defName context }
