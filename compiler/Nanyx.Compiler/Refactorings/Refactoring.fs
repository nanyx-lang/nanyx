namespace NyxCompiler.Refactorings

open NyxCompiler

/// What a refactoring does, which tells an editor which of its menus to offer it in: the code action kinds of the
/// Language Server Protocol. Each refactoring adds the kind it needs.
[<RequireQualifiedAccess>]
type RefactoringKind =
    /// `refactor.extract`: introduce a local.
    | Extract
    /// `refactor.rewrite`: write the inferred type.
    | Rewrite

/// A refactoring that can be made at a selection: its title, as an editor's menu shows it, and the edits that make it.
type Refactoring =
    { Title: string
      Kind: RefactoringKind
      Edits: TextEdit list }

/// A refactoring at a selection, or the reason it can't be made there without changing what the program means.
type Offer =
    | Available of Refactoring
    /// The refactoring, by its title and kind, and why it is refused.
    | Refused of title: string * kind: RefactoringKind * reason: string
