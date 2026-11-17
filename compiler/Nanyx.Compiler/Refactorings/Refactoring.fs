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
    /// `refactor.inline`: inline a local or a function.
    | Inline
    /// `source.organizeImports`: organise imports.
    | OrganiseImports

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

    /// The text removing a definition from `start`, where its doc comment or attributes start, to `finish` removes: to
    /// the end of its last line, and the blank lines after it.
    let removal (source: string) (start: int) (finish: int) =
        let lineEnd = source.IndexOf('\n', finish)
        let finish = if lineEnd < 0 then source.Length else lineEnd + 1
        // The blank lines after it go with it.
        let rec blanks (at: int) =
            let next = source.IndexOf('\n', at)
            if next >= 0 && source.Substring(at, next - at).Trim() = "" then blanks (next + 1) else at
        start, blanks finish

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

    /// Whether any of `exprs` calls a function that requires a context or a context's member, or makes or changes a
    /// cell: something that may do something different each time it runs.
    let requiresAContext (typed: TypedModule) (exprs: Expression list) =
        exprs
        |> List.exists (fun part ->
            match typed.CalleeTypes |> Map.tryFind part.Id with
            | Some (TyFunc(_, _, contexts)) when not contexts.IsEmpty -> true
            | _ ->
                typed.References
                |> Map.tryFind part.Id
                |> Option.bind typed.Symbols.TryFind
                |> Option.exists (fun symbol -> symbol.Kind = ContextMember))
        || exprs |> List.exists (fun part -> match part.Kind with MutSyntax.Call _ | AssignSyntax.Call _ -> true | _ -> false)

    /// The cells `values` declare, each by its name and the span of what declares it: a `mut` def, or a lambda that
    /// takes a cell as a parameter.
    let cellsIn (values: Expression list) =
        values
        |> List.collect within
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, name, _, { Kind = MutSyntax.Call _ }) -> Some (name, statement.Span)
                    | _ -> None)
            | Lambda(parameters, _, _) ->
                parameters
                |> List.choose (fun (name, annotation) ->
                    match annotation with
                    | CellPassingSyntax.CellParameter _ -> Some (name, expr.Span)
                    | _ -> None)
            | _ -> [])

    /// Whether `symbol` is one of `cells`, as `cellsIn` finds them.
    let isCell (cells: (string * SourceSpan) list) (symbol: Symbol) =
        cells |> List.exists (fun (name, span) -> name = symbol.Name && SourceSpan.contains span symbol.Declaration)

    /// Whether `expr`, written in place of a name, may need brackets: an operator, a pipe, a branch or a lambda, which
    /// could take in what is around it. The formatter drops the brackets it doesn't need.
    let isLoose (expr: Expression) =
        match expr.Kind with
        | BinaryOp _
        | Pipe _
        | IfExpr _
        | Match _
        | Except _
        | UseIn _
        | Lambda _
        | WorkflowBindExpr _
        | WorkflowReturnExpr _ -> true
        | _ -> false

    /// `text`, the text of `expr`, in brackets if it may need them.
    let bracketed (expr: Expression) (text: string) = if isLoose expr then $"({text})" else text

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
