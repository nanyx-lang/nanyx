/// Convert a match to an `if`, and an `if` to a match: `match ok | true -> a | false -> b` and `if ok then a else b`,
/// `match x | #none -> a | _ -> b` and `if x == #none then a else b`. Positions are character offsets into the source
/// as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.MatchAndIf

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private toIf = "Convert to if"

let private toMatch = "Convert to match"

/// The text of `span` in `source`.
let private textOf (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

/// Whether `expr` is written in `source` starting with `keyword`, as a word.
let private startsWith (source: string) (keyword: string) (expr: Expression) =
    let at = expr.Span.Start
    not (SourceSpan.isNone expr.Span)
    && at + keyword.Length <= source.Length
    && source.Substring(at, keyword.Length) = keyword
    && (at + keyword.Length = source.Length || not (System.Char.IsLetterOrDigit source[at + keyword.Length]))

/// Whether `expr` is a name or a field of one: what reads the same value however many times it is evaluated.
let rec private isPlain (expr: Expression) =
    match expr.Kind with
    | IdentifierExpr _ -> true
    | MemberAccess(inner, _, _) -> isPlain inner
    | _ -> false

/// What an arm's pattern tests the matched value for.
type private Test =
    | IsTrue
    | IsFalse
    /// Equal to the literal or tag written.
    | Equals of string
    /// Anything: `_` or `else`.
    | Otherwise

/// What `pattern` tests, or why an `if` can't test it.
let private testOf (source: string) (pattern: Pattern) =
    match pattern.Kind with
    | LiteralPattern(BoolLit true) -> Ok IsTrue
    | LiteralPattern(BoolLit false) -> Ok IsFalse
    | LiteralPattern _
    | TagPattern(_, None) -> Ok (Equals (textOf source pattern.Span))
    | WildcardPattern
    | ElsePattern -> Ok Otherwise
    | GuardPattern _ -> Error "An arm has a guard, which an `if` has no place for"
    | _ when not (Pattern.boundNames pattern).IsEmpty -> Error "An arm binds a variable, which an `if` has no place for"
    | _ -> Error "An arm's pattern is not a literal, a tag or `_`, which an `if` can compare with `==`"

/// `body` written as a branch: on the same line, or indented by `indent` on the lines after.
let private branch (source: string) (indent: string) (body: Expression) =
    let text = textOf source body.Span
    if text.Contains '\n' then "\n" + reindented source body.Span.Start indent text else " " + text

/// The text of `if` with `conditions`, each with the branch it guards, then `otherwise`, at a line indented by `indent`.
let private ifText (source: string) (indent: string) (step: string) (conditions: (string * Expression) list) (otherwise: Expression) =
    let multiLine = (otherwise :: (conditions |> List.map snd)) |> List.exists (fun body -> (textOf source body.Span).Contains '\n')
    let separator = if multiLine then "\n" + indent else " "
    let heads = conditions |> List.map (fun (condition, body) -> $"if {condition} then{branch source (indent + step) body}")
    $"""{String.concat $"{separator}else " heads}{separator}else{branch source (indent + step) otherwise}"""

/// The text of a match on `value` with `arms`, each a pattern and its body, at a line indented by `indent`.
let private matchText (source: string) (indent: string) (step: string) (value: string) (arms: (string * string) list) =
    let arm (pattern, body: string) =
        if body.Contains '\n' then $"\n{indent}{step}| {pattern} ->\n{body}" else $"\n{indent}{step}| {pattern} -> {body.TrimStart()}"
    $"""match {value}{arms |> List.map arm |> String.concat ""}"""

/// Converting the match `expr` (`match value | ...`) to an `if`, or why it can't be.
let private matchToIf (source: string) (indent: string) (step: string) (expr: Expression) (value: Expression) (arms: MatchArm list) =
    let tested =
        arms
        |> List.map (fun (patterns, body) ->
            match patterns with
            | [ pattern ] -> testOf source pattern |> Result.map (fun test -> test, body)
            | _ -> Error "An arm matches several values")
    match tested |> List.tryPick (function Error reason -> Some reason | Ok _ -> None) with
    | Some reason -> Error reason
    | None ->
        let tested = tested |> List.choose (function Ok arm -> Some arm | Error _ -> None)
        let valueText = bracketed value (textOf source value.Span)
        match tested with
        | [ IsTrue, ifTrue; (IsFalse | Otherwise), ifFalse ]
        | [ IsFalse, ifFalse; (IsTrue | Otherwise), ifTrue ] -> Ok (ifText source indent step [ valueText, ifTrue ] ifFalse)
        | _ ->
            match List.rev tested with
            | (Otherwise, otherwise) :: compared when not compared.IsEmpty ->
                let compared = List.rev compared
                match compared |> List.map (function Equals pattern, body -> Some (pattern, body) | _ -> None) with
                | tests when tests |> List.forall Option.isSome ->
                    if compared.Length > 1 && not (isPlain value) then
                        Error "The matched value would be evaluated once for each arm, not once"
                    else
                        let conditions = tests |> List.choose id |> List.map (fun (pattern, body) -> $"{valueText} == {pattern}", body)
                        Ok (ifText source indent step conditions otherwise)
                | _ -> Error "An arm tests for `true` or `false` among other values"
            | _ -> Error "The last arm is not `_`, which an `if` needs for its `else`"

/// The written `if ... else if ...` chain that starts with `expr`: each condition with its branch, and the last `else`'s
/// branch. A left-out `else` is the `()` the parser makes up at the `then` branch.
let rec private chainOf (source: string) (expr: Expression) =
    match expr.Kind with
    | IfExpr(condition, ifTrue, ifFalse) when startsWith source "if" ifFalse && not (isMultiBranch source ifFalse) ->
        let conditions, otherwise = chainOf source ifFalse
        (condition, ifTrue) :: conditions, otherwise
    | IfExpr(condition, ifTrue, ifFalse) -> [ condition, ifTrue ], ifFalse
    | _ -> [], expr

/// Whether `expr` is a multi-branch `if | cond -> a | ...`, whose arms the parser makes into nested ifs.
and private isMultiBranch (source: string) (expr: Expression) =
    let rest = source.Substring(expr.Span.Start + 2).TrimStart()
    rest.StartsWith "|"

/// The value and the literal or tag compared in `condition`, a `value == literal`.
let private comparison (source: string) (condition: Expression) =
    match condition.Kind with
    | BinaryOp("==", _, value, ({ Kind = LiteralExpr _ | TagExpr(_, None) } as compared)) -> Some (value, textOf source compared.Span)
    | _ -> None

/// Converting the `if` `expr` to a match, or why it can't be.
let private ifToMatch (source: string) (indent: string) (step: string) (expr: Expression) =
    let conditions, otherwise = chainOf source expr
    let body (branch: Expression) =
        let text = textOf source branch.Span
        if text.Contains '\n' then reindented source branch.Span.Start (indent + step + step) text else text
    // A left-out `else` is written `()`.
    let otherwiseText = if otherwise.Kind = UnitExpr && otherwise.Span = (List.last conditions |> snd).Span then "()" else body otherwise
    let compared = conditions |> List.map (fst >> comparison source)
    match compared with
    | Some (value, _) :: _ when compared |> List.forall Option.isSome ->
        let values = compared |> List.choose id
        if values |> List.exists (fun (other, _) -> textOf source other.Span <> textOf source value.Span) then
            Error "The conditions don't compare the same value"
        elif values.Length > 1 && not (isPlain value) then
            Error "The compared value would be evaluated once, not once for each condition"
        else
            let arms = List.map2 (fun (_, pattern) (_, branch) -> pattern, body branch) values conditions
            Ok (matchText source indent step (bracketed value (textOf source value.Span)) (arms @ [ "_", otherwiseText ]))
    | _ ->
        match expr.Kind with
        | IfExpr(condition, ifTrue, ifFalse) ->
            // An `else if` stays an `if`, in the `false` arm.
            let ifFalse = if conditions.Length = 1 then otherwiseText else body ifFalse
            Ok (matchText source indent step (bracketed condition (textOf source condition.Span)) [ "true", body ifTrue; "false", ifFalse ])
        | _ -> Error "It is not an `if`"

/// The refactoring at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`, on the `match` or `if` keyword of the innermost one there.
///
/// A match on one value becomes an `if`: one of two arms `true` and `false` (or one of them and `_`) an `if` on the
/// value, and arms that are each a literal or a tag alone, then `_`, an `if ... else if ...` chain comparing the value
/// with each by `==`, the `_` arm its `else`. Refused where an arm binds a variable or has a guard, which an `if` has
/// no place for, where the last arm is not `_`, and, with more than one comparison, where the value is not a name or
/// a field of one, which the chain would evaluate once for each arm.
///
/// An `if ... else if ...` chain whose every condition compares one value with `==` against a literal or a tag becomes
/// a match on that value, the last `else` a `_` arm, and any other `if` a match on its condition with arms `true` and
/// `false`, an `else if` staying an `if` in the `false` arm. A left-out `else` is `()`. Refused where the conditions
/// compare different values, and, with more than one, where the value is not a name or a field of one, which the
/// match would evaluate once instead of once for each condition. A multi-branch `if | ...` and an if-match
/// (`if value is pattern`) are not offered.
///
/// As `recompile` compiles a text of the module as `result` was compiled, either is refused where its result would not
/// compile, as when the value's type has no `==`. Empty elsewhere, and where the module did not type-check.
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
    | Some syntax, Some _ ->
        let onKeyword (keyword: string) (expr: Expression) =
            startsWith source keyword expr && expr.Span.Start <= offset && offset <= expr.Span.Start + keyword.Length
        let found =
            topLevelValues syntax
            |> List.collect within
            |> List.tryPick (fun expr ->
                match expr.Kind with
                | Match([ value ], arms) when onKeyword "match" expr -> Some (toIf, fun indent step -> matchToIf source indent step expr value arms)
                | Match(_ :: _ :: _, _) when onKeyword "match" expr -> Some (toIf, fun _ _ -> Error "It matches several values")
                | IfExpr _ when onKeyword "if" expr && not (isMultiBranch source expr) -> Some (toMatch, fun indent step -> ifToMatch source indent step expr)
                | _ -> None
                |> Option.map (fun (title, convert) -> expr, title, convert))
        match found with
        | None -> []
        | Some (expr, title, convert) ->
            let _, indent = lineOf source expr.Span.Start
            match convert indent (String.replicate options.IndentSize " ") with
            | Error reason -> [ Refused(title, RefactoringKind.Rewrite, reason) ]
            | Ok text ->
                [ Deferred(title, RefactoringKind.Rewrite, fun () ->
                      let after = appliedAndFormatted options file source [ expr.Span.Start, expr.Span.End, text ]
                      match newError result (recompile after) with
                      | Some reason ->
                          let made = if title = toIf then "if" else "match"
                          Refused(title, RefactoringKind.Rewrite, $"The {made} would not compile{reason}")
                      | None -> Available { Title = title; Kind = RefactoringKind.Rewrite; Edits = [ difference file source after ]; Rename = None }) ]
    | _ -> []
