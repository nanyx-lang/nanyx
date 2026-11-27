[<AutoOpen>]
module Parser.Program.ParseState

open Parser.Program

/// A comment the parser skipped: where it starts and ends (from `--` to the end of the line, without the line break, or
/// to the end of a block comment's closing line or a doc comment's last line), and whether it is a doc comment.
type NotedComment =
    { Start: int
      End: int
      /// A doc comment: `--|` on a line of its own, and the lines after it that are indented further.
      IsDoc: bool }

/// What the parser keeps beside the items it reads: what the formatter, doc comments and the compiler need to know of
/// the text that isn't in the tree.
type ParseState =
    { Declared: DeclaredNames
      /// The text of each `module name =` block parsed so far, with the block's name.
      ModuleBlocks: (ModuleName * SourceSpan) list
      /// The comments skipped so far, most recent first. For the formatter, which puts them back (see
      /// `parseModuleWithTrivia`), and for doc comments (see `DocComments`).
      Comments: NotedComment list
      /// Where each construct a doc comment can document starts, from its first keyword (`export def name`, `| #tag`),
      /// with where the name or tag it declares starts (see `noteDocumentable`).
      DocumentableStarts: Map<int, int>
      /// The text of each string literal parsed so far (between the quotes, without any interpolation holes), as
      /// start and end offsets, most recent first. A blank line in one is part of the string, not of the layout.
      StringTexts: (int * int) list
      /// How many code tokens the parser took, each one read again after a reading that didn't match counted again: its
      /// work, which a test of how the parser scales counts rather than times.
      TokensRead: int
      /// How many readings the parser tried that didn't match, each of them an exception: work a test counts too (#2219).
      NoMatches: int }

module ParseState =
    let empty () =
        { Declared = Map.empty
          ModuleBlocks = []
          Comments = []
          DocumentableStarts = Map.empty
          StringTexts = []
          TokensRead = 0
          NoMatches = 0 }
