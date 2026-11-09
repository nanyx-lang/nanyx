[<AutoOpen>]
module Parser.Program.Refs

open System
open FParsec
// After the libraries, so that the parser's names shadow theirs (`identifier`, `Attribute`) as they did in one file.
open Parser.Program

/// The parameters of a shorthand lambda: the operand its body leaves out (`{ * 2 }`, `{ .name }`), or the two
/// operands of an operator written alone (`{ + }`). Like `???`, they are names no code can write, so a shorthand
/// lambda never captures a name the code around it declares.
let implicitArgument = "<arg>"
let implicitSecondArgument = "<arg2>"

/// The parameter a lambda takes in place of the pattern written as its `position`th (counted from 1) parameter,
/// `{ $Email(s) -> s }`, which a match at the top of its body takes apart. Like `implicitArgument`, no code can
/// write it.
let patternArgument (position: int) = $"<pattern{position}>"

// Forward references for recursive expression parsing
let expression, expressionRef = createParserForwardedToRef<Expression, ParseState>()
let exprWithoutCrossingNewlines, exprWithoutCrossingNewlinesRef = createParserForwardedToRef<Expression, ParseState>()
let inlineExpression, inlineExpressionRef = createParserForwardedToRef<Expression, ParseState>()
let simpleExpression, simpleExpressionRef = createParserForwardedToRef<Expression, ParseState>()
let lambda, lambdaRef = createParserForwardedToRef<Expression, ParseState>()
let functionCall, functionCallRef = createParserForwardedToRef<Expression, ParseState>()
let matchExpr, matchExprRef = createParserForwardedToRef<Expression, ParseState>()
let statement, statementRef = createParserForwardedToRef<Statement, ParseState>()
/// One line of a block as the statements it stands for: a single statement, or the several a
/// destructuring `def (x, y) = ...` desugars into.
let statementItems, statementItemsRef = createParserForwardedToRef<Statement list, ParseState>()
let typeExpr, typeExprRef = createParserForwardedToRef<TypeExpr, ParseState>()
/// The type in a def's annotation: a type expression, or bare parameters `int, int -> int` (see `typeExpr`).
let typeSignature, typeSignatureRef = createParserForwardedToRef<TypeExpr, ParseState>()
/// Types separated by commas, and the result they go to when an arrow follows them, with the span of the whole.
let typeList, typeListRef = createParserForwardedToRef<(TypeExpr list * TypeExpr option) * SourceSpan, ParseState>()

/// What a `typeList` means: `a, b -> c` is a function of two parameters, since `,` binds tighter than `->`. The
/// parameters' tuple spans the parameters.
let internal typeListToType ((items: TypeExpr list, resultOpt: TypeExpr option), span: SourceSpan) : TypeExpr =
    let tuple () = TypeExpr.at (SourceSpan.between (List.head items).Span (List.last items).Span) (TypeTuple items)
    match items, resultOpt with
    | [ single ], None -> single
    | _, None -> TypeExpr.at span (TypeTuple items)
    | [ single ], Some result -> TypeExpr.at span (TypeFunc(single, result))
    | _, Some result -> TypeExpr.at span (TypeFunc(tuple (), result))

// Pattern parsers
let pattern, patternRef = createParserForwardedToRef<Pattern, ParseState>()
