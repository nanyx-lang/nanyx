[<AutoOpen>]
module Parser.Program.Mutation

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

/// `mut e`: a cell holding the value of `e`, a call to the built-in "__mut" (see `MutSyntax`). The value is on the same
/// line, and takes the rest of the expression, as `return` does: `mut x + 1` holds `x + 1`.
let mutExpr =
    let keywordEnd = notFollowedBy (satisfy (fun c -> Char.IsLetterOrDigit c || c = '_'))
    spanned (
        attempt (pstring "mut" .>> keywordEnd) >>. wsInline >>. expression
        |>> fun value -> FunctionCall(MutSyntax.builtIn, None, [ value ]))
    <?> "mut expression"

/// What `valueParser` parses, and then `:= value` (`rhs`) if it is assigned to: a call to the built-in "__assign" with
/// the two, spanning both, whose name span is the `:=` (see `AssignSyntax`). `:=` binds looser than every operator, pipe
/// and `except`, so `total := total + x` assigns `total + x`. What it assigns to is checked by the type checker.
let withAssignment (rhs: Parser<Expression, ParseState>) (valueParser: Parser<Expression, ParseState>) =
    valueParser .>>. opt (attempt (wsInline >>. withSpan (pstring ":=")) .>> wsInline .>>. rhs)
    |>> function
        | target, Some ((_, operatorSpan), value) ->
            Expression.at (SourceSpan.between target.Span value.Span) (FunctionCall(AssignSyntax.builtIn, Some operatorSpan, [ target; value ]))
        | target, None -> target
