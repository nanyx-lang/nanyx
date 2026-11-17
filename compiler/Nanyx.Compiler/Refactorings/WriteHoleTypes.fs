/// Write the inferred types of the `_`s: in an annotation that writes `_` for parts of a type, write in what each was
/// inferred to be, as Write the inferred type writes types. Positions are character offsets into the source as the
/// parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.WriteHoleTypes

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private oneTitle = "Write the inferred type of this `_`"

let private everyTitle = "Write the inferred types of every `_`"

/// An annotation that writes a `_`: its type, the symbol of the def it is the annotation of, if it is a def's, and
/// whether it is a lambda parameter's, whose annotation ends at an arrow.
type private Annotation =
    { Type: TypeExpr
      Def: Symbol option
      IsParameter: bool }

/// Every annotation in `syntax` that writes a `_`: a top-level def's, a local def's and a lambda parameter's, each def's
/// with its symbol, found by its name within the def.
let private annotations (syntax: Module) (typed: TypedModule) =
    let symbolOf (span: SourceSpan) (name: string) =
        typed.Symbols
        |> Map.values
        |> Seq.filter (fun symbol ->
            symbol.Name = name
            && (symbol.Kind = TopLevelValue || symbol.Kind = LocalValue)
            && SourceSpan.contains span symbol.Declaration)
        |> Seq.sortBy (fun symbol -> symbol.Declaration.Start)
        |> Seq.tryHead
    let ofDef (span: SourceSpan) (name: string) (annotation: TypeExpr) = { Type = annotation; Def = symbolOf span name; IsParameter = false }
    let topLevel =
        syntax
        |> List.choose (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, Some annotation, _)) -> Some (ofDef item.Span name annotation)
            | _ -> None)
    let local =
        topLevelValues syntax
        |> List.collect within
        |> List.collect (fun expr ->
            match expr.Kind with
            | Lambda(parameters, _, _) ->
                parameters |> List.choose (fun (_, annotation) -> annotation |> Option.map (fun t -> { Type = t; Def = None; IsParameter = true }))
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, name, Some annotation, _) -> Some (ofDef statement.Span name annotation)
                    | _ -> None)
            | _ -> [])
    topLevel @ local |> List.filter (fun annotation -> not (TypeExpr.holes annotation.Type).IsEmpty)

/// Each `_` in `annotation`, with whether a function written in its place needs brackets: everywhere but the whole of a
/// def's annotation, the result of a function, and the type after a list of contexts.
let private holesOf (annotation: Annotation) =
    let rec walk (takesFunction: bool) (typeExpr: TypeExpr) =
        match typeExpr.Kind with
        | TypeHole -> [ typeExpr, not takesFunction ]
        | TypeFunc(input, result) -> walk false input @ walk true result
        | TypeWithContext(contexts, inner) -> (contexts |> List.collect (walk false)) @ walk takesFunction inner
        | _ -> TypeExpr.children typeExpr |> List.collect (walk false)
    walk (not annotation.IsParameter) annotation.Type

/// The names of the type variables `typeExpr` writes.
let private variablesOf (typeExpr: TypeExpr) =
    TypeExpr.within typeExpr |> List.choose (fun part -> match part.Kind with TypeVar name -> Some name | _ -> None) |> List.distinct

/// `ty` without the context variables of its functions that are not among `bound`: in a def's annotation, a function's
/// context variable must be one of the def's contexts, which a `_` the compiler filled in did not need.
let rec private withoutUnbound (bound: Ty list) (ty: Ty) =
    match ty with
    | TyFunc(input, result, contexts) ->
        let contexts = contexts |> List.filter (fun context -> match context with TyVar _ -> bound |> List.contains context | _ -> true)
        TyFunc(withoutUnbound bound input, withoutUnbound bound result, List.map (withoutUnbound bound) contexts)
    | _ -> Ty.map (withoutUnbound bound) ty

/// The edits that write the types of `holes`, of `annotation`, or why they can't be written. The contexts the def
/// requires are already written: a def's annotation must list them, `_`s or not (NYX0205).
let private writing (typed: TypedModule) (annotation: Annotation) (holes: (TypeExpr * bool) list) =
    let tys = holes |> List.map (fun (hole, _) -> typed.ExprTypes |> Map.tryFind hole.Id)
    if tys |> List.exists (fun ty -> ty |> Option.forall TypeHints.mentionsError) then Error "It has a part that failed to check"
    else
        let named = Signatures.writable typed.TypeDefs typed.Symbols
        let required =
            annotation.Def
            |> Option.map (fun symbol ->
                [ typed.ContextTypes; typed.InferredContextTypes ] |> List.collect (Map.tryFind symbol.Id >> Option.defaultValue []))
            |> Option.defaultValue []
            |> List.distinct
            |> List.map named
        let tys = tys |> List.map (Option.get >> named >> if annotation.Def.IsSome then withoutUnbound required else id)
        // Named together with the def's type and the variables the annotation names, so that a new variable takes a
        // letter none of them has.
        let around =
            let defType = annotation.Def |> Option.bind (fun symbol -> typed.SymbolTypes |> Map.tryFind symbol.Id) |> Option.map named
            let variables = variablesOf annotation.Type |> List.mapi (fun index name -> TyVar(TyVar.ordinary (-1 - index) (Some name)))
            TyApply("", Option.toList defType @ variables)
        match Unifier.tysToDisplayStrings Source (around :: tys) |> List.tail with
        | written when written |> List.exists (Seq.exists (fun c -> c >= '₀' && c <= '₉')) ->
            Error "Its type requires a scoped context at two scopes, which a type can't be written with yet"
        | written ->
            List.zip3 holes tys written
            |> List.map (fun ((hole, needsBrackets), ty, text) ->
                let text = match ty with TyFunc _ when needsBrackets -> $"({text})" | _ -> text
                hole.Span.Start, hole.Span.End, text)
            |> Ok

/// The refactorings at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`: on a `_` in a def's, a local def's or a lambda parameter's annotation, writing what it was inferred to
/// be, and what each `_` in that annotation was, when it has several; on the name of a def whose annotation writes a
/// `_`, writing every one. Types are written as Write the inferred type writes them (`Signatures.writable`): a type
/// variable the annotation names keeps its name, a new one takes the next free letter, and a function is bracketed
/// where it is a parameter; in a def's annotation, a function's context variable that is not one of the def's contexts is
/// left out, as the `_` did not need it. Refused where a `_` failed to check, and, as `recompile` compiles a
/// text of the module as `result` was compiled, where the result would not compile. Empty elsewhere, and where the
/// module did not type-check.
let at
    (options: Formatter.Options)
    (file: string)
    (source: string)
    (result: CompileResult)
    (offset: int)
    (recompile: string -> CompileResult)
    : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let found = annotations syntax typed
        let offer (title: string) (annotation: Annotation) (holes: (TypeExpr * bool) list) =
            match writing typed annotation holes with
            | Error reason -> Refused(title, RefactoringKind.Rewrite, reason)
            | Ok edits ->
                let after = appliedAndFormatted options file source edits
                match newError result (recompile after) with
                | Some reason -> Refused(title, RefactoringKind.Rewrite, $"The annotation would not compile{reason}")
                | None -> Available { Title = title; Kind = RefactoringKind.Rewrite; Edits = [ difference file source after ]; Rename = None }
        let onHole =
            found
            |> List.tryPick (fun annotation ->
                holesOf annotation
                |> List.tryFind (fun (hole, _) -> hole.Span.Start <= offset && offset <= hole.Span.End)
                |> Option.map (fun hole -> annotation, hole))
        match onHole with
        | Some (annotation, hole) ->
            let every = holesOf annotation
            offer oneTitle annotation [ hole ] :: (if every.Length > 1 then [ offer everyTitle annotation every ] else [])
        | None ->
            found
            |> List.tryFind (fun annotation ->
                annotation.Def
                |> Option.exists (fun symbol -> symbol.Declaration.Start <= offset && offset <= symbol.Declaration.End))
            |> Option.map (fun annotation -> [ offer everyTitle annotation (holesOf annotation) ])
            |> Option.defaultValue []
    | _ -> []
