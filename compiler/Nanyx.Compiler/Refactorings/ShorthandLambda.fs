/// Convert a lambda to its shorthand and back: `{ x -> x * 2 }` and `{ * 2 }`, `{ a, b -> a + b }` and `{ + }`.
/// Positions are character offsets into the source as the parser saw it, with line endings normalised to `\n`, as
/// for `Hover`.
module NyxCompiler.Refactorings.ShorthandLambda

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private toShorthand = "Convert to shorthand lambda"

let private fromShorthand = "Convert to lambda with parameters"

/// Whether `expr` writes the name `name` anywhere within it.
let private mentions (name: string) (expr: Expression) =
    within expr |> List.exists (fun part -> match part.Kind with IdentifierExpr used -> used = name | _ -> false)

/// What converting `lambda`, written in `source`, gives: its text in the other form, and the names of the parameters it
/// makes up, if any; or why it can't be converted, or nothing where it is not a lambda the other form can say.
let private converted (source: string) (taken: Set<string>) (lambda: Expression) : Result<string * string list, string> option =
    let textOf (expr: Expression) = SourceSpan.textIn source expr.Span
    let refusal (parameters: (string * TypeExpr option) list) =
        if parameters |> List.exists (snd >> Option.isSome) then Some (Error "A parameter writes its type, which a shorthand lambda can't write")
        else None
    match lambda.Kind with
    // `{ + }`
    | Lambda([ (first, None); (second, None) ], { Kind = BinaryOp(op, _, Kind (IdentifierExpr left), Kind (IdentifierExpr right)) }, false)
        when first = implicitArgument && second = implicitSecondArgument && left = first && right = second ->
        let x = unique taken "x" "x"
        let y = unique (taken.Add x) "y" "y"
        Some (Ok ($"{{ {x}, {y} -> {x} {op} {y} }}", [ x ]))
    // `{ * 2 }`, `{ .name }`, `{ \f }`
    | Lambda([ (argument, None) ], body, false) when argument = implicitArgument ->
        let inside = source.Substring(lambda.Span.Start + 1, lambda.Span.End - 1 - (lambda.Span.Start + 1)).Trim()
        let x = unique taken "x" "x"
        Some (Ok ((if inside.StartsWith "." then $"{{ {x} -> {x}{inside} }}" else $"{{ {x} -> {x} {inside} }}"), [ x ]))
    // `{ a, b -> a + b }`
    | Lambda([ (a, _); (b, _) ] as parameters, { Kind = BinaryOp(op, _, Kind (IdentifierExpr left), Kind (IdentifierExpr right)) }, false)
        when left = a && right = b && a <> b && not (System.Char.IsLetter op[0]) ->
        refusal parameters |> Option.orElse (Some (Ok ($"{{ {op} }}", [])))
    // `{ x -> x * 2 }`: the parameter is the left operand alone, and the right doesn't use it.
    | Lambda([ (x, _) ] as parameters, { Kind = BinaryOp(op, _, Kind (IdentifierExpr left), right) }, false)
        when left = x && not (mentions x right) && not (System.Char.IsLetter op[0]) ->
        refusal parameters |> Option.orElse (Some (Ok ($"{{ {op} {bracketed right (textOf right)} }}", [])))
    | _ -> None

/// The type of the lambda that starts at `start` in `result`, written for comparing.
let private lambdaTypeAt (result: CompileResult) (start: int) =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        topLevelValues syntax
        |> List.collect within
        |> List.tryFind (fun expr -> expr.Span.Start = start && (match expr.Kind with Lambda _ -> true | _ -> false))
        |> Option.bind (fun lambda -> typed.ExprTypes |> Map.tryFind lambda.Id)
        |> Option.map (Unifier.tyToDisplayString Ascii)
    | _ -> None

/// The refactoring at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`, on the innermost lambda written `{ ... }` there: converting it to a shorthand lambda, where its body is
/// one operator applied to its parameters in order, the one parameter the left operand alone and the right not using
/// it (`{ x -> x * 2 }` is `{ * 2 }`), or its two parameters the two operands (`{ a, b -> a + b }` is `{ + }`); or a
/// shorthand lambda to one with parameters, named `x` and `y`, made unique, the first starting a rename. A body that
/// is a negation is not an operator applied to the parameter, so it is not converted. Refused where a parameter writes
/// its type, which a shorthand can't; and, as `recompile` compiles a text of the module as `result` was compiled,
/// where the result would not compile or the lambda would have another type, as when the operator would read another
/// context instance. Empty elsewhere, and where the module did not type-check.
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
        let lambda =
            topLevelValues syntax
            |> List.collect within
            |> List.filter (fun expr ->
                (match expr.Kind with Lambda _ -> true | _ -> false)
                && SelectedExpression.isWritten source expr
                && expr.Span.Start <= offset
                && offset <= expr.Span.End)
            |> List.sortBy (fun expr -> expr.Span.End - expr.Span.Start)
            |> List.tryHead
        let isShorthand (lambda: Expression) =
            match lambda.Kind with
            | Lambda((first, _) :: _, _, _) -> first = implicitArgument
            | _ -> false
        match lambda with
        | None -> []
        | Some lambda ->
            let title = if isShorthand lambda then fromShorthand else toShorthand
            match converted source (takenNames syntax typed) lambda with
            | None -> []
            | Some (Error reason) -> [ Refused(title, RefactoringKind.Rewrite, reason) ]
            | Some (Ok (text, names)) ->
                [ Deferred(title, RefactoringKind.Rewrite, fun () ->
                      let after = appliedAndFormatted options file source [ lambda.Span.Start, lambda.Span.End, text ]
                      let compiled = recompile after
                      match newError result compiled with
                      | Some reason -> Refused(title, RefactoringKind.Rewrite, $"The lambda would not compile{reason}")
                      | None when lambdaTypeAt compiled lambda.Span.Start <> lambdaTypeAt result lambda.Span.Start ->
                          Refused(title, RefactoringKind.Rewrite, "The lambda would have another type, its operator reading another context instance")
                      | None ->
                          let rename =
                              names
                              |> List.tryHead
                              |> Option.bind (fun name ->
                                  let found = after.IndexOf("{ " + name + " ", lambda.Span.Start, System.StringComparison.Ordinal)
                                  let found = if found < 0 then after.IndexOf("{ " + name + ",", lambda.Span.Start, System.StringComparison.Ordinal) else found
                                  if found < 0 then None else Some (found + 2))
                          Available { Title = title; Kind = RefactoringKind.Rewrite; Edits = [ difference file source after ]; Rename = rename }) ]
    | _ -> []
