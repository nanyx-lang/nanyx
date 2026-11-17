/// Introduce type alias: the selected type, written in an annotation or a type definition, becomes a `type` definition
/// of its own before the top-level item that holds it, and the selection becomes its name. Its type variables become
/// the alias's parameters. Positions are character offsets into the source as the parser saw it, with line endings
/// normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.IntroduceTypeAlias

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private title = "Introduce type alias"

/// A type written in the module, with the top-level item it is written in, whether that item is exported, and whether
/// it is in a list of contexts (`[Eq(a)]`), which names contexts rather than types.
type internal WrittenType =
    { Type: TypeExpr
      Item: TopLevelItem
      Exported: bool
      InContexts: bool }

/// Each type written within `root`, `root` among them, marking those in a list of contexts.
let rec private typesIn (inContexts: bool) (root: TypeExpr) : (TypeExpr * bool) list =
    let inner =
        match root.Kind with
        | TypeWithContext(contexts, body) -> (contexts |> List.collect (typesIn true)) @ typesIn inContexts body
        | TypeContext items -> items |> List.collect (typesIn true)
        | _ -> TypeExpr.children root |> List.collect (typesIn inContexts)
    (root, inContexts) :: inner

/// Whether `written` is its exported item's own annotation or type body, whose types another module may name.
let private isExported (written: SyntaxTree.WrittenType) =
    match written.Item.Kind with
    | Def (ValueDef(isExport, _, Some annotation, _)) -> isExport && annotation.Id = written.Type.Id
    | Def (TypeDef(_, modifiers, _, body)) -> body.Id = written.Type.Id && List.contains Export modifiers
    | _ -> false

/// Every type written in `syntax` within an annotation or a type definition's body, top-level or local, or a lambda
/// parameter's annotation, in module blocks too, in the order they are written.
let internal writtenTypes (syntax: Module) =
    SyntaxTree.writtenTypes syntax
    |> List.filter (fun written ->
        match written.As with
        | SyntaxTree.DefAnnotation _
        | SyntaxTree.TypeBody _
        | SyntaxTree.LambdaParameter -> true
        | SyntaxTree.ParameterConstraint
        | SyntaxTree.ContextNamed
        | SyntaxTree.PassedType -> false)
    |> List.collect (fun root ->
        typesIn false root.Type
        |> List.map (fun (written, inContexts) -> { Type = written; Item = root.Item; Exported = isExported root; InContexts = inContexts }))
    |> List.filter (fun written -> not (SourceSpan.isNone written.Type.Span))

/// `typeExpr` with its type variables renamed by `rename`.
let rec private renamed (rename: Map<string, string>) (typeExpr: TypeExpr) : TypeExpr =
    match typeExpr.Kind with
    | TypeVar name -> { typeExpr with Kind = TypeVar(rename |> Map.tryFind name |> Option.defaultValue name) }
    | _ -> TypeExpr.mapChildren (renamed rename) typeExpr

/// `typeExpr` with its type variables named by where they first appear, so that two types that differ only by the
/// names of their variables are equal.
let private canonical (typeExpr: TypeExpr) =
    renamed (TypeExpr.variablesOf typeExpr |> List.mapi (fun index name -> name, $"v{index}") |> Map.ofList) typeExpr

/// The names of types `typeExpr` writes.
let private typeNamesIn (typeExpr: TypeExpr) =
    TypeExpr.within typeExpr
    |> List.choose (fun part ->
        match part.Kind with
        | TypeName name
        | TypeApply(name, _) -> Some name
        | _ -> None)

/// Why `written`, the selected type, can't be made an alias, if it can't.
let private refusal (written: WrittenType) =
    match written.Type.Kind with
    | _ when written.InContexts -> Some "It is in a list of contexts, which names contexts rather than types"
    | TypeVar _ -> Some "A type variable alone is already a name"
    | TypeHole -> Some "A `_` is left for inference to fill in"
    | TypeName name when System.Char.IsLower name[0] && not (name.Contains '.') -> Some "A built-in type's name alone is already a name"
    | _ -> None

/// The names a new type must not be: every name `takenNames` gives, and every type name the module writes or defines.
let private takenTypeNames (syntax: Module) (typed: TypedModule) (written: WrittenType list) =
    let defined =
        syntax
        |> List.collect (fun item ->
            match item.Kind with
            | Def (TypeDef(name, _, _, _)) -> [ name ]
            | Def (ValueDef(_, _, _, value))
            | Expr value ->
                within value
                |> List.collect (fun expr ->
                    match expr.Kind with
                    | Block statements -> statements |> List.choose (fun s -> match s.Kind with TypeDefStatement(name, _, _, _) -> Some name | _ -> None)
                    | _ -> [])
            | _ -> [])
    takenNames syntax typed + Set.ofList defined + (written |> List.collect (fun w -> typeNamesIn w.Type) |> Set.ofList)

/// The names of the types local `type` definitions define, which may hide a module's types of the same name.
let private locallyDefined (syntax: Module) =
    topLevelValues syntax
    |> List.collect within
    |> List.collect (fun expr ->
        match expr.Kind with
        | Block statements -> statements |> List.choose (fun s -> match s.Kind with TypeDefStatement(name, _, _, _) -> Some name | _ -> None)
        | _ -> [])
    |> Set.ofList

/// Where `type name` declares `name` in `text`: the start of the name.
let private typeDeclarationOf (text: string) (name: string) =
    let found = System.Text.RegularExpressions.Regex.Match(text, $@"\btype {System.Text.RegularExpressions.Regex.Escape name}\b")
    if found.Success then Some (found.Index + 5) else None

/// The refactorings at `selection` in `source`, the text of the root module of `result` compiled as `file`, laid out
/// with `options`: where the selection, less the spaces around it (and brackets around a type that are only brackets),
/// is exactly a type written in a def's or local def's annotation, a lambda parameter's, or a type definition (a
/// context's members among them), making it a type alias, `type Extracted = ...`, for that occurrence, and for every
/// occurrence in the module written the same up to the names of its type variables, when there are others. Its type
/// variables are its parameters, in the order they first appear. The alias goes before the top-level item that holds the
/// first occurrence it replaces, exported if an exported def's signature or an exported type uses it. Refused for a type
/// variable alone, a built-in type's name alone, a `_`, and a type in a list of contexts; and, as `recompile` compiles
/// a text of the module as `result` was compiled, where the result would not compile. Empty where the selection is not
/// a written type, or where the module did not type-check.
let at
    (options: Formatter.Options)
    (file: string)
    (source: string)
    (result: CompileResult)
    (selection: SourceSpan)
    (recompile: string -> CompileResult)
    : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some typed when selection.End > selection.Start ->
        let text = SourceSpan.textIn source selection
        let selection =
            { selection with Start = selection.Start + (text.Length - text.TrimStart().Length); End = selection.End - (text.Length - text.TrimEnd().Length) }
        let written = writtenTypes syntax
        let exactlyAt (span: SourceSpan) =
            written |> List.tryFind (fun w -> w.Type.Span.Start = span.Start && w.Type.Span.End = span.End)
        // A type in brackets that are only brackets is selected with them, or without.
        let found =
            match exactlyAt selection with
            | Some found -> Some (found, selection)
            | None ->
                let inner = SourceSpan.textIn source selection
                if inner.Length > 2 && inner.StartsWith "(" && inner.EndsWith ")" then
                    let body = inner.Substring(1, inner.Length - 2)
                    let start = selection.Start + 1 + (body.Length - body.TrimStart().Length)
                    let finish = selection.End - 1 - (body.Length - body.TrimEnd().Length)
                    exactlyAt { selection with Start = start; End = finish } |> Option.map (fun found -> found, selection)
                else None
        match found with
        | None -> []
        | Some (selected, _) when (refusal selected).IsSome -> [ Refused(title, RefactoringKind.Extract, (refusal selected).Value) ]
        | Some (selected, selectedSpan) ->
            let name = unique (takenTypeNames syntax typed written) "Extracted" "Extracted"
            let parameters = TypeExpr.variablesOf selected.Type
            let typeText = SourceSpan.textIn source selected.Type.Span
            let useOf (occurrence: TypeExpr) =
                match TypeExpr.variablesOf occurrence with
                | [] -> name
                | variables -> $"""{name}({String.concat ", " variables})"""
            // The other occurrences written the same, unless a local type may hide one of the names it writes.
            let others =
                if typeNamesIn selected.Type |> List.exists (locallyDefined syntax).Contains then []
                else
                    let shape = canonical selected.Type
                    written
                    |> List.filter (fun w -> w.Type.Id <> selected.Type.Id && not w.InContexts && canonical w.Type = shape)
                    // An occurrence inside another is replaced with it.
                    |> fun found -> found |> List.filter (fun w -> not (found |> List.exists (fun outer -> outer.Type.Id <> w.Type.Id && SourceSpan.contains outer.Type.Span w.Type.Span)))
                    |> List.filter (fun w -> not (SourceSpan.contains selected.Type.Span w.Type.Span || SourceSpan.contains w.Type.Span selected.Type.Span))
            let offer (offerTitle: string) (occurrences: (WrittenType * SourceSpan) list) =
                let first = occurrences |> List.map (fun (w, _) -> w.Item) |> List.minBy (fun item -> item.Span.Start)
                let exported = occurrences |> List.exists (fun (w, _) -> w.Exported)
                let at = insertionPoint source syntax first.Span.Start
                // Indented as the item is, which is in a module block's body when it is one of its items.
                let _, indent = lineOf source first.Span.Start
                let parameterList = if parameters.IsEmpty then "" else $"""({String.concat ", " parameters})"""
                let definition = $"""{indent}{if exported then "export " else ""}type {name}{parameterList} = {typeText}{"\n\n"}"""
                let edits = (at, at, definition) :: (occurrences |> List.map (fun (w, span) -> span.Start, span.End, useOf w.Type))
                Deferred(offerTitle, RefactoringKind.Extract, fun () ->
                    let after = appliedAndFormatted options file source edits
                    match newError result (recompile after) with
                    | Some reason -> Refused(offerTitle, RefactoringKind.Extract, $"The alias would not compile{reason}")
                    | None ->
                        Available
                            { Title = offerTitle
                              Kind = RefactoringKind.Extract
                              Edits = [ difference file source after ]
                              Rename = typeDeclarationOf after name })
            offer $"{title} `{name}`" [ selected, selectedSpan ]
            :: (if others.IsEmpty then []
                else [ offer $"{title} `{name}` for all {others.Length + 1} occurrences" ((selected, selectedSpan) :: (others |> List.map (fun w -> w, w.Type.Span))) ])
    | _ -> []
