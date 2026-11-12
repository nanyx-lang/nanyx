/// The operator precedence parsers' tables, built from `binaryOperators`, and what the parser reports for operators Nanyx
/// does not have: `&&`, `||`, a prefix `!`, and two operators that cannot be written side by side.
[<AutoOpen>]
module Parser.Program.OperatorTables

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

/// What follows an operator's text: `after`, and then the span of the operator itself, which ends where `after` starts.
/// Every operator is written on one line, so it starts `op.Length` characters before that.
let operatorSpan (op: string) (after: Parser<unit, ParseState>) : Parser<SourceSpan, ParseState> =
    getPosition .>> after |>> fun opEnd ->
        let length = int64 op.Length
        spanOf (Position(opEnd.StreamName, opEnd.Index - length, opEnd.Line, opEnd.Column - length)) (int opEnd.Index)

/// There is no prefix `!`: logical not is `not`. A `!` where an operand starts is reported as that, at the `!`.
let bang: Parser<Identifier * SourceSpan, ParseState> =
    userStateSatisfies (fun state -> state.ImplicitArgumentNext.IsNone)
    >>. followedBy (pchar '!' >>. notFollowedBy (pchar '='))
    >>. getPosition
    >>= fun position -> reportFocused position "There is no prefix `!` in Nanyx: logical not is the keyword `not`, as in `not x`"

/// `&&` and `||` are not operators: Boolean operations are the keywords `and` and `or`. Either after an operand is
/// reported as that, at the operator.
let symbolicBoolean: Parser<unit, ParseState> =
    fun stream ->
        // Read without a parser, so that neither is among what an error after an operand says was expected.
        match [ "&&"; "||" ] |> List.tryFind stream.Match with
        | Some op ->
            let keyword = if op = "&&" then "and" else "or"
            (getPosition >>= fun position -> reportFocused position $"`{op}` is not an operator in Nanyx: write `{keyword}`") stream
        | None -> Reply(())

/// Two operators that cannot be written side by side (see `NonAssociative`), as the operator precedence parser reports
/// them: the message, carried in its error for `reportingConflicts`.
type private OperatorConflict = OperatorConflict of string

/// `table`'s expressions, with two operators that cannot be written side by side reported at the second of them (see
/// `reportFocused`): the failure is otherwise backtracked over, and an enclosing construct reported in its place.
let reportingConflicts (table: OperatorPrecedenceParser<'Term, SourceSpan, ParseState>) : Parser<'Term, ParseState> =
    fun stream ->
        let reply = table.ExpressionParser stream
        let conflict =
            if reply.Status = Ok then None
            else
                ErrorMessageList.ToSortedArray reply.Error
                |> Array.tryPick (function
                    | :? OtherErrorMessage as other ->
                        match other.Data with
                        | :? OperatorConflict as conflict -> let (OperatorConflict message) = conflict in Some message
                        | _ -> None
                    | _ -> None)
        match conflict with
        | Some message -> (getPosition >>= fun position -> reportFocused position message) stream
        | None -> reply

let private associativity (associativity: OperatorAssociativity) =
    match associativity with
    | LeftAssociative -> Associativity.Left
    | RightAssociative -> Associativity.Right
    | NonAssociative -> Associativity.None

// `-` must not take the start of a `->` (as in `for 1..2 -> ...`) or of a `--` comment.
let private minusEnd = notFollowedBy (anyOf ">-")

// `??` must not take the start of the placeholder `???`.
let private coalesceEnd = notFollowedBy (pchar '?')

/// An operator is written with a space on each side, `x + y`, not `x+y`, so that the characters it is made of stay free
/// for other uses. A line break counts as a space, and so does the `{` of a shorthand lambda whose body starts with the
/// operator, as in `{ + 1 }`. Run just after `op`, it notes the operator when either side is not spaced, and the parse
/// fails there once it is done (see `ParseState.MisspacedOperator`).
let private spacedAround (op: string) : Parser<unit, ParseState> =
    fun stream ->
        let isSpace (c: char) = c = ' ' || c = '\t' || c = '\r' || c = '\n'
        let before = stream.Peek(-(op.Length + 1))
        let after = stream.Peek()
        let spacedBefore = isSpace before || before = '{'
        let spacedAfter = isSpace after || after = CharStream.EndOfStreamChar
        let misspaced = stream.UserState.MisspacedOperator
        if not (spacedBefore && spacedAfter) then
            let start = stream.Position
            let position = Position(start.StreamName, start.Index - int64 op.Length, start.Line, start.Column - int64 op.Length)
            match misspaced.Value with
            | Some (first, _) when first.Index <= position.Index -> ()
            | _ -> misspaced.Value <- Some (position, $"Operators are written with a space on each side: `x {op} y`, not `x{op}y`")
        Reply(())

/// Adds every operator in `binaryOperators` to `table`, each taking `space` after it and building its term with
/// `binary`, and the message for two operators that cannot be written side by side. A keyword operator must not swallow
/// the start of an identifier such as `order` or `android`.
let addBinaryOperators
    (table: OperatorPrecedenceParser<'Term, SourceSpan, ParseState>)
    (space: Parser<unit, ParseState>)
    (binary: string -> SourceSpan -> 'Term -> 'Term -> 'Term)
    =
    for op in binaryOperators do
        let operatorEnd =
            if Char.IsLetter op.Text[0] then keywordEnd
            elif op.Text = "-" then minusEnd
            elif op.Text = coalesceOperator then coalesceEnd
            else preturn ()
        table.AddOperator(InfixOperator(op.Text, operatorSpan op.Text (operatorEnd >>. spacedAround op.Text >>. space),op.Precedence, associativity op.Associativity, (), binary op.Text))
    table.OperatorConflictErrorFormatter <-
        fun (_, first: Operator<_, _, _>, _) (_, second: Operator<_, _, _>, _) ->
            let first, second = first.String, second.String
            let message =
                if isComparison first && isComparison second then
                    $"Comparisons do not chain: write `a {first} b and b {second} c`, or put one comparison in parentheses"
                else
                    $"`{first}` and `{second}` cannot be written side by side: put one of them in parentheses"
            mergeErrors (messageError message) (otherError (OperatorConflict message))
