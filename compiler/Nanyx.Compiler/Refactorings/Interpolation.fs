/// Turn a `++` chain of strings into an interpolated string, and back: `"Hello, " ++ name ++ "!"` and
/// `"Hello, {name}!"`. Positions are character offsets into the source as the parser saw it, with line endings
/// normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.Interpolation

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private toInterpolated = "Convert to interpolated string"

let private toConcatenation = "Convert to concatenation"

/// Whether `expr` is a `++` written in the source, not one the parser made for a list's spread.
let private isConcatenation (expr: Expression) =
    match expr.Kind with
    | BinaryOp("++", Some _, _, _) -> true
    | _ -> false

/// The operands of the `++` chain `expr`, in the order they are written.
let rec private operands (expr: Expression) =
    match expr.Kind with
    | BinaryOp("++", Some _, left, right) -> operands left @ operands right
    | _ -> [ expr ]

/// Whether the checker gave `expr` the type `string`.
let private isString (typed: TypedModule) (expr: Expression) =
    match typed.ExprTypes |> Map.tryFind expr.Id with
    | Some (TyPrimitive "string") -> true
    | _ -> false

/// The value `expr` converts to a string, if it is a call of `toString` that interpolation would make too: the
/// `ToString` context's member, or a type's attached `toString`, which is its instance's.
let private toStringOf (typed: TypedModule) (expr: Expression) =
    let callsToString () =
        WrittenReferences.ofModule typed
        |> Map.tryFind expr.Id
        |> Option.bind typed.Symbols.TryFind
        |> Option.exists (fun symbol ->
            (symbol.Kind = ContextMember && symbol.Name = "toString")
            || (symbol.Kind = TopLevelValue && symbol.Name.EndsWith ".toString"))
    match expr.Kind with
    | Pipe(value, _, _, [])
    | FunctionCall(_, _, [ value ]) when callsToString () -> Some value
    | _ -> None

/// What `operand` of a `++` chain is in an interpolated string: the text between the quotes of a string, or a hole.
let private partOf (source: string) (typed: TypedModule) (operand: Expression) =
    let text = SourceSpan.textIn source operand.Span
    match operand.Kind with
    | LiteralExpr(StringLit _) when text.StartsWith "'" -> Error "It has a raw (single-quoted) string, which can't be interpolated"
    | LiteralExpr(StringLit _)
    | InterpolatedString _ when text.Contains '\n' -> Error "It has a string of several lines"
    | LiteralExpr(StringLit _)
    | InterpolatedString _ -> Ok (text.Substring(1, text.Length - 2))
    | _ ->
        let value = toStringOf typed operand |> Option.defaultValue operand
        Ok $"{{{SourceSpan.textIn source value.Span}}}"

/// The parts of the interpolated string `expr` as they are written in `source`: each text between its holes, and each
/// hole's expression.
let private writtenParts (source: string) (expr: Expression) (parts: StringPart list) =
    let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
    let finish, written =
        holes
        |> List.fold
            (fun (from: int, written) (hole: Expression) ->
                let opening = source.LastIndexOf('{', hole.Span.Start - 1)
                let closing = source.IndexOf('}', hole.Span.End)
                closing + 1, written @ [ Choice1Of2 (source.Substring(from, opening - from)); Choice2Of2 hole ])
            (expr.Span.Start + 1, [])
    written @ [ Choice1Of2 (source.Substring(finish, expr.Span.End - 1 - finish)) ]
    |> List.filter (function Choice1Of2 "" -> false | _ -> true)

/// The `++` chain the interpolated string `expr` is, each hole that is not a string converted with `toString`.
let private concatenation (source: string) (typed: TypedModule) (expr: Expression) (parts: StringPart list) =
    writtenParts source expr parts
    |> List.map (function
        | Choice1Of2 text -> $"\"{text}\""
        | Choice2Of2 hole when isString typed hole -> bracketed hole (SourceSpan.textIn source hole.Span)
        | Choice2Of2 hole -> $"{bracketed hole (SourceSpan.textIn source hole.Span)} \\toString")
    |> String.concat " ++ "

/// The refactoring at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`.
///
/// On a `++` of a chain of them whose value is a string, converting the whole chain to an interpolated string: each
/// string's text goes in as it is written, escapes and all, a call of `toString` (`x \toString`, `toString(x)`,
/// `int.toString(x)`) becomes a hole on `x`, as interpolation converts a hole with its `ToString` instance, and any
/// other operand is a hole as it is. Refused for a raw (single-quoted) string, which can't interpolate, and a string
/// of several lines.
///
/// In an interpolated string, converting it to a `++` chain of its texts and holes, writing `toString` for each hole
/// whose value is not a string.
///
/// As `recompile` compiles a text of the module as `result` was compiled, either is refused where its result would not
/// compile. Empty elsewhere, and where the module did not type-check.
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
        let expressions = topLevelValues syntax |> List.collect within
        let concatenations = expressions |> List.filter isConcatenation
        let inner =
            concatenations
            |> List.collect (fun expr -> match expr.Kind with BinaryOp(_, _, left, right) -> [ left; right ] | _ -> [])
            |> List.filter isConcatenation
            |> List.map (fun expr -> expr.Id)
            |> Set.ofList
        let rec links (expr: Expression) =
            match expr.Kind with
            | BinaryOp("++", Some operator, left, right) -> operator :: links left @ links right
            | _ -> []
        let chain =
            concatenations
            |> List.tryFind (fun expr ->
                not (inner.Contains expr.Id)
                && links expr |> List.exists (fun operator -> operator.Start <= offset && offset <= operator.End))
        let interpolated =
            expressions
            |> List.filter (fun expr ->
                (match expr.Kind with InterpolatedString _ -> true | _ -> false)
                && expr.Span.Start <= offset
                && offset <= expr.Span.End)
            |> List.sortBy (fun expr -> expr.Span.End - expr.Span.Start)
            |> List.tryHead
        let offer (title: string) (expr: Expression) (text: string) =
            Deferred(title, RefactoringKind.Rewrite, fun () ->
                let after = appliedAndFormatted options file source [ expr.Span.Start, expr.Span.End, text ]
                match newError result (recompile after) with
                | Some reason -> Refused(title, RefactoringKind.Rewrite, $"The result would not compile{reason}")
                | None -> Available { Title = title; Kind = RefactoringKind.Rewrite; Edits = [ difference file source after ]; Rename = None })
        match chain, interpolated with
        | Some chain, _ when isString typed chain ->
            let parts = operands chain |> List.map (partOf source typed)
            match parts |> List.tryPick (function Error reason -> Some reason | Ok _ -> None) with
            | Some reason -> [ Refused(toInterpolated, RefactoringKind.Rewrite, reason) ]
            | None ->
                let text = parts |> List.choose (function Ok part -> Some part | Error _ -> None) |> String.concat ""
                [ offer toInterpolated chain $"\"{text}\"" ]
        | Some _, _ -> []
        | None, Some ({ Kind = InterpolatedString parts } as expr) ->
            [ offer toConcatenation expr $"({concatenation source typed expr parts})" ]
        | None, _ -> []
    | _ -> []
