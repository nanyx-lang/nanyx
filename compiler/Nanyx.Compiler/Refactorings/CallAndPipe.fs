/// Convert between a call and a pipe: `f(x, y)` and `x \f(y)`, a chain of them as a whole. Positions are character
/// offsets into the source as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.CallAndPipe

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private toPipeTitle = "Convert to pipe"
let private toCallTitle = "Convert to call"

/// The text of `expr` in `source`.
let private textOf (source: string) (expr: Expression) = SourceSpan.textIn source expr.Span

/// The function `expr` calls, written by name, with the span of the name and its arguments, the piped value first:
/// a call or a pipe the user wrote, with no named arguments.
let private written (source: string) (expr: Expression) =
    let named (name: string) (span: SourceSpan option) (arguments: Expression list) =
        match span with
        | Some span when
            not (MadeUpNames.isMadeUp name)
            && SourceSpan.textIn source span = name
            && not arguments.IsEmpty
            && arguments |> List.forall (fun argument -> not (SourceSpan.isNone argument.Span))
            && not (arguments |> List.exists (function { Kind = RecordExpr fields } -> fields |> List.exists (function NamedField _ -> true | _ -> false) | _ -> false)) ->
            Some (name, span, arguments)
        | _ -> None
    match expr.Kind with
    | FunctionCall(name, span, args) -> named name span (SyntaxTree.flattenCallArgs args)
    | Pipe(value, name, span, args) -> named name span (SyntaxTree.flattenCallArgs (pipeCallArguments value args))
    | _ -> None

let private isCall (expr: Expression) = match expr.Kind with FunctionCall _ -> true | _ -> false
let private isPipe (expr: Expression) = match expr.Kind with Pipe _ -> true | _ -> false

/// `call`, and the calls it takes its first argument from, written as a chain of pipes: `filter(map(xs, f), g)` as
/// `xs \map(f) \filter(g)`. Each function is named by `name`.
let rec private asPipe (source: string) (name: string -> string) (call: Expression) =
    match written source call with
    | Some (called, _, first :: rest) ->
        let value =
            if isCall first && (written source first).IsSome then asPipe source name first else bracketed first (textOf source first)
        let arguments =
            match rest with
            | [] -> ""
            // A lambda alone is written after the name, as a trailing lambda is.
            | [ { Kind = Lambda _ } as lambda ] when SelectedExpression.isWritten source lambda -> $" {textOf source lambda}"
            | _ -> $"""({rest |> List.map (textOf source) |> String.concat ", "})"""
        $"{value} \\{name called}{arguments}"
    | _ -> textOf source call

/// `pipe`, and the pipes it takes its value from, written as nested calls: `xs \map(f) \filter(g)` as
/// `filter(map(xs, f), g)`. Each function is named by `name`.
let rec private asCall (source: string) (name: string -> string) (pipe: Expression) =
    match written source pipe with
    | Some (called, _, first :: rest) ->
        let value = if isPipe first && (written source first).IsSome then asCall source name first else textOf source first
        $"""{name called}({value :: (rest |> List.map (textOf source)) |> String.concat ", "})"""
    | _ -> textOf source pipe

/// What every name in `result` refers to, each as many times as it is named: the name, kind and file of its symbol,
/// since a compile makes up symbols of its own.
let private referenced (result: CompileResult) =
    match result.Typed with
    | Some typed ->
        Definition.referenceSites result
        |> List.choose (fun (_, id) -> typed.Symbols |> Map.tryFind id |> Option.map (fun symbol -> symbol.Name, symbol.Kind, symbol.Declaration.File))
        |> List.sort
    | None -> []

/// The refactorings at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`: on the name of a call, converting it to a pipe of its first argument, `f(x, y)` to `x \f(y)`, and on the
/// name of a pipe, converting it to a call. A chain converts as a whole, from the outermost call or pipe that takes its
/// first argument or value from the one at `offset`. A dotted name is written by its own part in a pipe, `s \trim` for
/// `string.trim(s)`, which finds a definition attached to the value's type; where that finds a different one, or
/// none, the whole name is kept. Converting to a call writes a pipe's function by the name of the definition it found
/// where its own name would not find it. `recompile` compiles a text of the module as `result` was compiled: the
/// result is refused unless it compiles and every name in it refers to what it did. Empty elsewhere, and where the
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
        let expressions = Hover.allExpressions syntax

        let atOffset =
            expressions
            |> List.filter (fun expr ->
                match written source expr with
                // A function that is not found has nothing to convert to.
                | Some (_, span, _) -> span.Start <= offset && offset <= span.End && (WrittenReferences.ofModule typed).ContainsKey expr.Id
                | None -> false)
            |> List.sortBy (fun expr -> expr.Span.End - expr.Span.Start)
            |> List.tryHead
        // The outermost call or pipe of the same kind that takes its first argument from `expr`.
        let rec outermost (isKind: Expression -> bool) (expr: Expression) =
            let takesIt (outer: Expression) =
                isKind outer && (match written source outer with Some (_, _, first :: _) -> first.Id = expr.Id | _ -> false)
            match expressions |> List.tryFind takesIt with
            | Some outer -> outermost isKind outer
            | None -> expr
        let before = referenced result
        // The text with `chain` written by `write` with each name given by one of `namings` in turn, the first that
        // means the same, or why none does.
        // Decided when it is picked, as it compiles each text it tries.
        let convert (title: string) (chain: Expression) (write: (string -> string) -> Expression -> string) (namings: (string -> string) list) =
            let decide () =
                let attempts =
                    namings
                    |> List.distinctBy (fun naming -> write naming chain)
                    |> List.map (fun naming ->
                        let text = $"({write naming chain})"
                        let edits = [ chain.Span.Start, chain.Span.End, text ]
                        let after = recompile (applied source edits)
                        match newError result after with
                        | Some reason -> Error $"The converted module would not compile{reason}"
                        | None when referenced after <> before -> Error "A name would find a different definition"
                        | None -> Ok edits)
                match attempts |> List.tryPick (function Ok edits -> Some edits | Error _ -> None) with
                | Some edits ->
                    let after = appliedAndFormatted options file source edits
                    Available { Title = title; Kind = RefactoringKind.Rewrite; Edits = [ difference file source after ]; Rename = None }
                | None ->
                    let reason = attempts |> List.tryPick (function Error reason -> Some reason | Ok _ -> None) |> Option.defaultValue ""
                    Refused(title, RefactoringKind.Rewrite, reason)
            Deferred(title, RefactoringKind.Rewrite, decide)
        match atOffset with
        | Some expr when isCall expr ->
            [ convert toPipeTitle (outermost isCall expr) (asPipe source) [ Names.ownPart; id ] ]
        | Some expr when isPipe expr ->
            let chain = outermost isPipe expr
            // The name of the definition each pipe in the chain found, by the name it is written with.
            let found =
                let rec steps (pipe: Expression) =
                    match written source pipe with
                    | Some (name, _, first :: _) ->
                        let symbol = WrittenReferences.symbolOf typed pipe.Id
                        (name, symbol |> Option.map (fun symbol -> symbol.Name))
                        :: (if isPipe first then steps first else [])
                    | _ -> []
                steps chain |> List.choose (fun (name, found) -> found |> Option.map (fun found -> name, found)) |> Map.ofList
            let byDefinition (name: string) = found |> Map.tryFind name |> Option.defaultValue name
            [ convert toCallTitle chain (asCall source) [ id; byDefinition ] ]
        | _ -> []
    | _ -> []
