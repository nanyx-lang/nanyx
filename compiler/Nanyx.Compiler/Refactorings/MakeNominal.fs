/// Make a structural type protected or private: `type Email = string` becomes `type Email = protected string`, with an
/// attached `Email.of` that makes one from its shape, and each place another module of the package gave a value of the
/// shape where an `Email` is expected wrapped in a call to it. Positions are character offsets into the source as the
/// parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.MakeNominal

open System.IO
open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

/// The errors that say a value of a type's shape is given where the type is expected: a type mismatch, and a record of
/// a protected type's shape outside its home module.
let private shapeGivenCodes = set [ "NYX0201"; "NYX0260" ]

/// Whether `diagnostic`, reported in the file `path`, says a value of the shape of `name` is given where `name` is
/// expected.
let private givesShapeOf (name: string) (path: string) (diagnostic: Diagnostic) =
    diagnostic.Severity = Severity.Error
    && shapeGivenCodes.Contains diagnostic.Code
    && diagnostic.Message.Contains $"'{name}'"
    && diagnostic.Span |> Option.exists (fun span -> PackageModules.samePath span.File path)

/// `source`, the text of the file `path` compiled as `result`, with each expression where a value of the shape of
/// `name` is given where `name` is expected wrapped in `name.of(...)`: the outermost of those that overlap.
let private wrapped (name: string) (path: string) (source: string) (result: CompileResult) =
    let sites =
        result.Diagnostics
        |> List.filter (givesShapeOf name path)
        |> List.map (fun diagnostic -> diagnostic.Span.Value.Start, diagnostic.Span.Value.End)
        |> List.distinct
    let outermost =
        sites |> List.filter (fun (start, finish) -> not (sites |> List.exists (fun (s, f) -> (s, f) <> (start, finish) && s <= start && finish <= f)))
    applied source (outermost |> List.map (fun (start, finish) -> start, finish, $"{name}.of({source.Substring(start, finish - start)})"))

/// The refactorings at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`: on the name of a top-level `type` definition whose type is structural, making it protected and making it
/// private. Each writes `protected` or `private` before its shape and adds an attached `of` after it, exported if the
/// type is, that takes the shape and makes the type (unless the module has one), so the `unconstructibleType` rule
/// stays quiet. The home module may give a value of the shape where the type is expected, so it needs nothing else; for
/// an exported type, each place another file of the package does is wrapped in a call to `of`. `package` gives every
/// source file of the package, this one among them, or None for a file in no package, and `recompile` compiles them
/// again, given each one's path and text, in the order given. Refused for a type whose shape is a type variable, for an
/// exported type in a file in no package, since other files may use it, and where the result would not compile, with
/// the first new error, as a place of another module that reads a private type's shape. Empty elsewhere, and where the
/// module did not type-check.
let at
    (options: Formatter.Options)
    (file: string)
    (source: string)
    (result: CompileResult)
    (offset: int)
    (package: unit -> Rename.ModuleFile list option)
    (recompile: (string * string) list -> CompileResult list)
    : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let onName (alias: InlineTypeAlias.Alias) = alias.NameStart <= offset && offset <= alias.NameStart + alias.Name.Length
        match InlineTypeAlias.aliasesIn source syntax |> List.tryFind onName with
        | Some alias when not (TypeDefModifier.isNominal alias.Modifiers) ->
            let exported = alias.Modifiers |> List.contains Export
            let hasOf = typed.Symbols |> Map.exists (fun _ symbol -> symbol.Kind = TopLevelValue && symbol.Name = $"{alias.Name}.of")
            let offer (keyword: string) =
                let title = $"Make `{alias.Name}` {keyword}"
                let refused reason = Refused(title, RefactoringKind.Rewrite, reason)
                match alias.Body.Kind with
                | TypeVar _ -> refused $"The shape of `{alias.Name}` is a type variable"
                | _ ->
                    Deferred(title, RefactoringKind.Rewrite, fun () ->
                        let constructor =
                            if hasOf then []
                            else
                                let representation = SourceSpan.textIn source alias.Body.Span
                                let indent = String.replicate alias.Item.Span.StartColumn " "
                                let definition = UnconstructibleTypes.ofDefinition exported alias.Name alias.Parameters representation
                                [ alias.Item.Span.End, alias.Item.Span.End, $"\n\n{indent}{definition}" ]
                        let edits = (alias.Body.Span.Start, alias.Body.Span.Start, $"{keyword} ") :: constructor
                        let after = appliedAndFormatted options file source edits
                        let files =
                            if exported then package () |> Option.map (List.map (fun moduleFile -> { moduleFile with Source = normalised moduleFile.Source }))
                            else Some [ { Rename.Path = file; Rename.Source = source; Rename.Result = result } ]
                        match files with
                        | None -> refused $"`{alias.Name}` is exported, and this file is in no package, so the files that use it can't be found"
                        | Some files ->
                            let texts = files |> List.map (fun moduleFile -> moduleFile.Path, if moduleFile.Path = file then after else moduleFile.Source)
                            let compiled = recompile texts
                            // Each other file wraps the values of the shape the type no longer takes, compiled with them.
                            let texts =
                                List.zip texts compiled
                                |> List.map (fun ((path, text), compiledFile) -> path, if path = file then text else wrapped alias.Name path text compiledFile)
                            let compiled = recompile texts
                            let newError =
                                List.zip3 files texts compiled
                                |> List.tryPick (fun (before, (path, _), after) ->
                                    newError before.Result after
                                    |> Option.map (fun reason -> if path = file || reason = "" then reason else $"{reason} (in {Path.GetFileName path})"))
                            match newError with
                            | Some reason -> refused $"The result would not compile{reason}"
                            | None ->
                                let changes =
                                    List.zip files texts
                                    |> List.filter (fun (before, (_, text)) -> before.Source <> text)
                                    |> List.map (fun (before, (path, text)) -> difference path before.Source text)
                                Available { Title = title; Kind = RefactoringKind.Rewrite; Edits = changes; Rename = None })
            [ offer "protected"; offer "private" ]
        | _ -> []
    | _ -> []
