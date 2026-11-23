/// Where an expression is printed, and how tightly the operators the parser reads bind.
module internal Nanyx.Formatter.Position

open Parser.Program

/// Where an expression is printed, which decides whether it needs parentheses (the parser keeps none).
type Position =
    { /// On the spine of a statement, an arm's body or a top-level expression. The parser reads those without crossing
      /// lines, with an operator table that has no `..`, so a range needs parentheses there.
      Inline: bool
      /// Nothing follows it in the enclosing construct, so a construct that takes everything after it (a `match`, an
      /// `if`, a `with`) needs no parentheses.
      Tail: bool
      /// The loosest operator it may be without parentheses: 0 takes a pipe, `prefixPrecedence`
      /// only an operand of `.` or `not`.
      Prec: int }

/// Inside brackets, where anything goes.
let full = { Inline = false; Tail = true; Prec = 0 }

/// A statement, an arm's body or a top-level expression.
let statementPosition = { Inline = true; Tail = true; Prec = 0 }

/// An operand of `.` or `not`: a primary expression.
let operandPosition = { Inline = false; Tail = false; Prec = prefixPrecedence }

/// Whether `e`, an item in brackets (an argument, a field, an element or an entry), ends where the item does though
/// it takes everything after it elsewhere: a workflow keyword applied to a value, `try x`, `yield x` or `return x`,
/// which the parser ends at the item's `,` or line break. Another item may follow one without brackets.
let endsWithItsItem (e: Expression) =
    match e.Kind with
    | WorkflowBindExpr _
    | WorkflowReturnExpr _ -> true
    | _ -> false

/// An item in brackets, the last of them if `isLast` (see `endsWithItsItem`).
let itemPosition (isLast: bool) (e: Expression) = { full with Tail = isLast || endsWithItsItem e }

/// How tightly the operator the parser reads as `op` binds, as the parser has it (see `binaryOperators`).
let precedence op =
    match binaryOperatorNamed op with
    | Some found -> found.Precedence
    | None -> failwith $"The formatter met the operator {op}, which the parser does not read"

/// Which way the operator `op` groups, as the parser has it.
let associativity op =
    match binaryOperatorNamed op with
    | Some found -> found.Associativity
    | None -> failwith $"The formatter met the operator {op}, which the parser does not read"
