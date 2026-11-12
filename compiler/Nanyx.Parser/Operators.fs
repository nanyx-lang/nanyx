/// The binary operators: the one list the parser builds both of its operator tables from, and the formatter reads to
/// tell where an expression needs parentheses. Nanyx has these operators and no others (see the operators page of the
/// docs): a type can be given any of them by attaching a definition, but no other operator can be defined.
[<AutoOpen>]
module Parser.Program.Operators

/// Which way a run of one operator groups.
type OperatorAssociativity =
    /// `a - b - c` is `(a - b) - c`.
    | LeftAssociative
    /// `a ++ b ++ c` is `a ++ (b ++ c)`.
    | RightAssociative
    /// `a < b < c` is an error: one of them must be in parentheses. Two operators of one precedence where either does
    /// not group, or that group different ways, cannot be written side by side either.
    | NonAssociative

/// A binary operator. `Text` is how it is written and the name it is defined by (`` def $Money.`+` = ... ``). The
/// higher its `Precedence`, the tighter it binds. A spare operator has no built-in meaning: it means only what a type
/// attaches to it.
type BinaryOperator =
    { Text: string
      Precedence: int
      Associativity: OperatorAssociativity
      IsSpare: bool }

let private operator text precedence associativity =
    { Text = text; Precedence = precedence; Associativity = associativity; IsSpare = false }

let private spare text precedence associativity =
    { operator text precedence associativity with IsSpare = true }

/// Every binary operator, tightest first. Both of the parser's tables take all of them: the full one and the one for a
/// statement or a match arm's body, which does not cross lines. `..` is not among them: it is range syntax (see
/// `rangePrecedence`). Prefix `-` and `not` bind tighter than all of them (see `prefixPrecedence`).
let binaryOperators =
    [ // Powers group to the right: `2 ** 3 ** 2` is `2 ** (3 ** 2)`.
      operator "**" 10 RightAssociative
      operator "*" 9 LeftAssociative
      operator "/" 9 LeftAssociative
      operator "%" 9 LeftAssociative
      spare "^" 9 LeftAssociative
      spare "<*>" 9 LeftAssociative
      operator "+" 8 LeftAssociative
      operator "-" 8 LeftAssociative
      spare "<+>" 8 LeftAssociative
      // Concatenation, of strings and lists: looser than arithmetic, tighter than comparison.
      operator "++" 7 RightAssociative
      spare "<>" 7 RightAssociative
      // Composition: `f >> g` is `f` and then `g`, `g << f` the same function.
      operator ">>" 6 LeftAssociative
      operator "<<" 6 RightAssociative
      operator "<" 5 NonAssociative
      operator ">" 5 NonAssociative
      operator "<=" 5 NonAssociative
      operator ">=" 5 NonAssociative
      spare "~" 5 NonAssociative
      operator "==" 4 NonAssociative
      operator "!=" 4 NonAssociative
      spare ">>=" 3 LeftAssociative
      spare "=<<" 3 RightAssociative
      // Boolean operations are the keywords `and` and `or`, the loosest operators.
      operator "and" 2 LeftAssociative
      operator "or" 1 LeftAssociative ]

/// The precedence of a range, `a..b`, which does not group either: `a..b..c` is an error.
let rangePrecedence = 5

/// The precedence of prefix `-` and `not`, which bind tighter than every binary operator: `-x ** 2` is `(-x) ** 2`.
let prefixPrecedence = (binaryOperators |> List.map _.Precedence |> List.max) + 1

/// The operator written `text`, if Nanyx has one.
let binaryOperatorNamed (text: string) : BinaryOperator option =
    binaryOperators |> List.tryFind (fun op -> op.Text = text)

/// Whether `text` is one of Nanyx's operators, which a type can be given by attaching a definition of that name.
let isDefinableOperator (text: string) =
    binaryOperatorNamed text |> Option.exists (fun op -> not (System.Char.IsLetter op.Text[0]))

/// Whether `text` is a spare operator, which means only what a definition gives it, on a built-in type too.
let isSpareOperator (text: string) =
    binaryOperatorNamed text |> Option.exists _.IsSpare

/// The comparisons and equality, which do not chain: `a < b < c` is written `a < b and b < c`.
let isComparison (text: string) =
    binaryOperatorNamed text |> Option.exists (fun op -> op.Associativity = NonAssociative && not op.IsSpare)
