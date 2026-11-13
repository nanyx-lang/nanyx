[<AutoOpen>]
module Parser.Program.Mutation

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

/// `mut` where an expression is expected, as in `def x = mut 0`: an error. A cell is declared on a line of its own,
/// `mut x = 0` (see `MutSyntax`).
let mutExpr: Parser<Expression, ParseState> =
    let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
    attempt (getPosition .>> pstring "mut" .>> keywordEnd)
    >>= fun position ->
        reportFocused position "`mut` is not an expression: declare a cell on a line of its own, as `mut count = 0` in place of `def count = mut 0`"

/// What `valueParser` parses, and then `:= value` (`rhs`) if it is assigned to: a call to the built-in "__assign" with
/// the two, spanning both, whose name span is the `:=` (see `AssignSyntax`). `:=` binds looser than every operator, pipe
/// and `except`, so `total := total + x` assigns `total + x`. What it assigns to is checked by the type checker.
let withAssignment (rhs: Parser<Expression, ParseState>) (valueParser: Parser<Expression, ParseState>) =
    valueParser .>>. opt (attempt (wsInline >>. withSpan (pstring ":=")) .>> wsInline .>>. rhs)
    |>> function
        | target, Some ((_, operatorSpan), value) ->
            Expression.at (SourceSpan.between target.Span value.Span) (FunctionCall(AssignSyntax.builtIn, Some operatorSpan, [ target; value ]))
        | target, None -> target
