/// What the printers of lambdas, branches and other constructs that hold expressions print those parts with.
module internal Nanyx.Formatter.ExpressionParts

open Parser.Program
open Nanyx.Formatter.Position

/// The expression printer's functions that the printers taken out of it call back into, as `TypePrinter` and
/// `CallPrinter` call back into `expr`.
type ExpressionParts =
    { /// An expression at a position, in parentheses if it needs them there.
      Expression: Position -> Expression -> Doc
      /// Whether an expression needs parentheses at a position.
      NeedsParentheses: Position -> Expression -> bool
      /// Whether an expression is a block of statements as written.
      IsWrittenBlock: Expression -> bool
      /// An indented block: its statements, or an expression that is not one.
      Block: Expression -> Doc
      /// Statements one per line, with blank lines kept if the first argument says so, and the comments between the
      /// offsets put back among them.
      StatementsInSequence: bool -> int -> int -> Statement list -> Doc
      /// A block written as statements separated by `;` on one line, as `a; b` with each `;` and the space after it a
      /// line break when its group is broken: the function building it. `None` for any other expression.
      SemicolonLine: Expression -> (unit -> Doc) option
      /// Match arms one per line, with the comments between the offsets put back among them.
      ArmsInSequence: int -> int -> MatchArm list -> Doc
      Pattern: Pattern -> Doc
      TypeExpr: TypeExpr -> Doc }
