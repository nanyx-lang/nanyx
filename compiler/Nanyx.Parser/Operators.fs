/// The binary operators: the one list the parser builds both of its operator tables from, and the formatter reads to
/// tell where an expression needs parentheses.
[<AutoOpen>]
module Parser.Program.Operators

/// Which way a run of one operator groups.
type OperatorAssociativity =
    /// `a - b - c` is `(a - b) - c`.
    | LeftAssociative
    /// `a ++ b ++ c` is `a ++ (b ++ c)`.
    | RightAssociative

/// A binary operator. `Text` is how it is written and `Name` the operator the parser reads it as: `&&` is `and`. The
/// higher its `Precedence`, the tighter it binds.
type BinaryOperator =
    { Text: string
      Name: string
      Precedence: int
      Associativity: OperatorAssociativity }

let private operator text name precedence associativity =
    { Text = text; Name = name; Precedence = precedence; Associativity = associativity }

/// Every binary operator, tightest first. Both of the parser's tables take all of them: the full one and the one for
/// a statement or a match arm's body, which does not cross lines. `..` is not among them: it is range syntax, a call
/// to `__range` (see `RangeSyntax`) at precedence 4, which only the full table takes.
///
/// Boolean operations are the keywords `and` and `or`, the loosest operators; the symbolic `&&` and `||` predate the
/// keywords and are kept as aliases that build the same tree.
let binaryOperators =
    [ // Powers group to the right: `2 ** 3 ** 2` is `2 ** (3 ** 2)`.
      operator "**" "**" 8 RightAssociative
      operator "*" "*" 7 LeftAssociative
      operator "/" "/" 7 LeftAssociative
      operator "%" "%" 7 LeftAssociative
      operator "+" "+" 6 LeftAssociative
      operator "-" "-" 6 LeftAssociative
      // Concatenation, of strings and lists: looser than arithmetic, tighter than comparison.
      operator "++" "++" 5 RightAssociative
      operator "<" "<" 4 LeftAssociative
      operator ">" ">" 4 LeftAssociative
      operator "<=" "<=" 4 LeftAssociative
      operator ">=" ">=" 4 LeftAssociative
      operator "==" "==" 3 LeftAssociative
      operator "!=" "!=" 3 LeftAssociative
      operator "and" "and" 2 LeftAssociative
      operator "&&" "and" 2 LeftAssociative
      operator "or" "or" 1 LeftAssociative
      operator "||" "or" 1 LeftAssociative ]

/// The operator the parser reads as `name`, as it is written in `binaryOperators`.
let binaryOperatorNamed (name: string) : BinaryOperator option =
    binaryOperators |> List.tryFind (fun op -> op.Name = name)
