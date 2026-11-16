namespace NyxCompiler.Refactorings

open Parser.Program
open NyxCompiler
open Nanyx.Formatter

/// What a refactoring does, which tells an editor which of its menus to offer it in: the code action kinds of the
/// Language Server Protocol. Each refactoring adds the kind it needs.
[<RequireQualifiedAccess>]
type RefactoringKind =
    /// `refactor.extract`: introduce a local or a function.
    | Extract
    /// `refactor.rewrite`: write the inferred type.
    | Rewrite

/// A refactoring that can be made at a selection: its title, as an editor's menu shows it, and the edits that make it.
/// `Rename` is where, in the text the edits make, a name the refactoring made up starts, for an editor to start
/// renaming it.
type Refactoring =
    { Title: string
      Kind: RefactoringKind
      Edits: TextEdit list
      Rename: int option }

/// A refactoring at a selection, or the reason it can't be made there without changing what the program means.
type Offer =
    | Available of Refactoring
    /// The refactoring, by its title and kind, and why it is refused.
    | Refused of title: string * kind: RefactoringKind * reason: string

/// What the refactorings that edit text share: making edits, laying out what they write as the formatter does, and
/// finding where new code goes and a name for it. Offsets are into source with line endings normalised to `\n`.
module internal RefactoringText =
    /// `source` with line endings normalised to `\n`, as the parser saw it.
    let normalised (source: string) = source.Replace("\r\n", "\n").Replace("\r", "\n")

    /// Each expression within `expr`, `expr` among them, with the statements of blocks, in the order they are written.
    let rec within (expr: Expression) : Expression list =
        let children, statements = SyntaxTree.childrenOf expr
        expr :: (children @ (statements |> List.collect SyntaxTree.ofStatement) |> List.collect within)

    /// The value of each top-level def and expression of `syntax`.
    let topLevelValues (syntax: Module) =
        syntax
        |> List.choose (function
            | Kind (Def (ValueDef(_, _, _, value)))
            | Kind (Expr value) -> Some value
            | _ -> None)

    /// The start of the line `offset` is on, and the spaces it starts with.
    let lineOf (source: string) (offset: int) =
        let start = source.LastIndexOf('\n', max 0 (offset - 1)) + 1
        let start = if offset = 0 then 0 else start
        let indent = source.Substring(start) |> Seq.takeWhile (fun c -> c = ' ') |> Seq.length
        start, String.replicate indent " "

    /// Where a def before what starts at `offset` goes: before the comments on the lines just above it, which are its
    /// doc comment or about it, and before the attributes written above it.
    let insertionPoint (source: string) (items: TopLevelItem list) (offset: int) =
        let attributeStart =
            items
            |> List.sortBy (fun item -> item.Span.Start)
            |> List.filter (fun item -> item.Span.End <= offset)
            |> List.rev
            |> List.takeWhile (fun item -> match item.Kind with Attribute _ -> true | _ -> false)
            |> List.tryLast
            |> Option.map (fun item -> item.Span.Start)
            |> Option.defaultValue offset
        let rec above (lineStart: int) =
            if lineStart = 0 then lineStart
            else
                let previous, _ = lineOf source (lineStart - 1)
                if source.Substring(previous, lineStart - 1 - previous).TrimStart().StartsWith "--" then above previous else lineStart
        let lineStart, _ = lineOf source attributeStart
        if source.Substring(lineStart, attributeStart - lineStart).Trim() = "" then above lineStart else attributeStart

    /// The names a new name must not be: every symbol's and import's, and every name the module's values write, since
    /// a local named after a function a call finds by name would hide it.
    let takenNames (syntax: Module) (typed: TypedModule) =
        (typed.Symbols |> Map.values |> Seq.map (fun symbol -> symbol.Name) |> Set.ofSeq)
        + (typed.ModuleNames |> Map.keys |> Set.ofSeq)
        + (topLevelValues syntax
           |> List.collect within
           |> List.collect (fun part ->
               match part.Kind with
               | IdentifierExpr name -> [ name ]
               // A pipe or call finds its function by name among the locals first.
               | FunctionCall(name, _, _)
               | Pipe(_, name, _, _) -> [ name; name.Substring(name.LastIndexOf '.' + 1) ]
               | _ -> [])
           |> Set.ofList)

    /// `named` if it is not among `taken`, or else `fallback` numbered to be unique: `result`, `result2`, ...
    let unique (taken: Set<string>) (fallback: string) (named: string) =
        if not (taken.Contains named) then named
        else Seq.initInfinite (fun index -> if index = 0 then fallback else $"{fallback}{index + 1}") |> Seq.find (taken.Contains >> not)

    /// `source` with `edits` made.
    let applied (source: string) (edits: (int * int * string) list) =
        edits
        |> List.sortByDescending (fun (start, _, _) -> start)
        |> List.fold (fun (text: string) (start, finish, text') -> text.Substring(0, start) + text' + text.Substring(finish)) source

    /// The one edit that turns `before` into `after`: what differs between their common start and their common end.
    let difference (file: string) (before: string) (after: string) : TextEdit =
        let prefix = Seq.zip before after |> Seq.takeWhile (fun (a, b) -> a = b) |> Seq.length
        let maxSuffix = min before.Length after.Length - prefix
        let suffix =
            Seq.zip (Seq.rev before) (Seq.rev after) |> Seq.take maxSuffix |> Seq.takeWhile (fun (a, b) -> a = b) |> Seq.length
        let like = { File = file; Start = 0; End = 0; StartLine = 0; StartColumn = 0 }
        { Span = TextEdit.spanBetween before like prefix (before.Length - suffix); NewText = after.Substring(prefix, after.Length - suffix - prefix) }

    /// `text` with the top-level items from `start` to `finish` formatted with `options`, or as it is when it can't be.
    let formatted (options: Formatter.Options) (file: string) (text: string) (start: int) (finish: int) =
        match RangeFormatting.formatRange options file text start finish with
        | Ok edits -> applied text (edits |> List.map (fun edit -> edit.Start, edit.End, edit.Text))
        | Error _ -> text

    /// `source` with `edits` made, and the top-level items they touch formatted with `options`.
    let appliedAndFormatted (options: Formatter.Options) (file: string) (source: string) (edits: (int * int * string) list) =
        let after = applied source edits
        let changedStart = edits |> List.map (fun (start, _, _) -> start) |> List.min
        let growth = after.Length - source.Length
        let changedEnd = (edits |> List.map (fun (_, finish, _) -> finish) |> List.max) + growth
        formatted options file after changedStart changedEnd

    /// The number of errors compiling a module reported.
    let errorCount (result: CompileResult) =
        result.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Severity = Severity.Error) |> List.length

    /// Whether `after`, the module of `before` with a refactoring's edits made, has more errors than it, or did not
    /// type-check, and if so the message of the first error it has that `before` did not, after `: `, or else nothing.
    let newError (before: CompileResult) (after: CompileResult) =
        if after.Typed.IsNone || errorCount after > errorCount before then
            after.Diagnostics
            |> List.tryFind (fun diagnostic -> diagnostic.Severity = Severity.Error && not (before.Diagnostics |> List.exists (fun old -> old.Message = diagnostic.Message)))
            |> Option.map (fun diagnostic -> $": {diagnostic.Message}")
            |> Option.defaultValue ""
            |> Some
        else None

    /// Where `def name` declares `name` in `text`: the start of the name.
    let declarationOf (text: string) (name: string) =
        let found = System.Text.RegularExpressions.Regex.Match(text, $@"\bdef {System.Text.RegularExpressions.Regex.Escape name}\b")
        if found.Success then Some (found.Index + 4) else None
