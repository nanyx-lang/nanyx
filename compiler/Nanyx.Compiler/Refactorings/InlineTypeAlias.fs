/// Inline a type alias: the uses of a `type` alias become the type it stands for, its parameters replaced by the
/// arguments of each use. Positions are character offsets into the source as the parser saw it, with line endings
/// normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.InlineTypeAlias

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

/// A top-level `type` definition: its name, where the name is written, its modifiers, parameters and body, and the
/// item that holds it.
type private Alias =
    { Name: string
      NameStart: int
      Modifiers: TypeDefModifier list
      Parameters: string list
      Body: TypeExpr
      Item: TopLevelItem }

/// The top-level `type` definitions of `syntax`, other than contexts.
let private aliasesIn (source: string) (syntax: Module) =
    syntax
    |> List.choose (fun item ->
        match item.Kind with
        | Def (TypeDef(name, modifiers, parameters, body)) when not (modifiers |> List.contains Context) ->
            let text = source.Substring(item.Span.Start, item.Span.End - item.Span.Start)
            let found = System.Text.RegularExpressions.Regex.Match(text, $@"\btype\s+{System.Text.RegularExpressions.Regex.Escape name}\b")
            if found.Success then
                Some
                    { Name = name
                      NameStart = item.Span.Start + found.Index + found.Length - name.Length
                      Modifiers = modifiers
                      Parameters = List.map fst parameters
                      Body = body
                      Item = item }
            else None
        | _ -> None)

/// The arguments a use of a type gives, if it is a use of `name`.
let private usesOf (name: string) (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeName used when used = name -> Some []
    | TypeApply(used, arguments) when used = name -> Some arguments
    | _ -> None

/// Whether a type written as `typeExpr` is a function, which needs brackets where it is a parameter or an argument.
let private isFunction (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeFunc _
    | TypeWithContext _ -> true
    | _ -> false

/// Each type variable in `typeExpr`, with whether a function written in its place needs brackets: everywhere but the
/// result of a function and the type after a list of contexts.
let rec private variablesIn (takesFunction: bool) (typeExpr: TypeExpr) =
    match typeExpr.Kind with
    | TypeVar name -> [ typeExpr, name, not takesFunction ]
    | TypeFunc(input, result) -> variablesIn false input @ variablesIn true result
    | TypeWithContext(contexts, inner) -> (contexts |> List.collect (variablesIn false)) @ variablesIn takesFunction inner
    | _ -> TypeExpr.children typeExpr |> List.collect (variablesIn false)

/// The text of `alias`'s body with its parameters replaced by `arguments`, as written in `source`, in brackets if it is
/// a function.
let private substituted (source: string) (alias: Alias) (arguments: TypeExpr list) =
    let textOf (typeExpr: TypeExpr) = source.Substring(typeExpr.Span.Start, typeExpr.Span.End - typeExpr.Span.Start)
    let byParameter = List.zip (List.truncate arguments.Length alias.Parameters) (List.truncate alias.Parameters.Length arguments) |> Map.ofList
    let body = alias.Body
    let replacements =
        variablesIn true body
        |> List.choose (fun (variable, name, needsBrackets) ->
            byParameter
            |> Map.tryFind name
            |> Option.map (fun argument ->
                let text = textOf argument
                variable.Span.Start - body.Span.Start, variable.Span.End - body.Span.Start, (if needsBrackets && isFunction argument then $"({text})" else text)))
    let text = applied (textOf body) replacements
    if isFunction body then $"({text})" else text

/// The refactorings at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`: on the name of a top-level `type` alias, or a use of it, inlining it, which puts the type it stands for in
/// place of every use, its parameters replaced by each use's arguments, and removes the alias with its doc comment; and
/// on a use, inlining that use alone. Refused for a nominal type (`protected` or `private`), which is not an alias, and
/// for an alias that names itself; for an exported alias, which another module may use, only a use is inlined. As
/// `recompile` compiles a text of the module as `result` was compiled, refused where the result would not compile.
/// Empty elsewhere, and where the module did not type-check.
let at
    (options: Formatter.Options)
    (file: string)
    (source: string)
    (result: CompileResult)
    (offset: int)
    (recompile: string -> CompileResult)
    : Offer list =
    let source = normalised source
    match result.Syntax, result.Typed with
    | Some syntax, Some _ ->
        let aliases = aliasesIn source syntax
        let written = IntroduceTypeAlias.writtenTypes syntax |> List.filter (fun w -> not w.InContexts)
        let onName (start: int) (name: string) = start <= offset && offset <= start + name.Length
        let onDefinition = aliases |> List.tryFind (fun alias -> onName alias.NameStart alias.Name)
        let onUse =
            written
            |> List.tryPick (fun w ->
                aliases
                |> List.tryFind (fun alias -> (usesOf alias.Name w.Type).IsSome && onName w.Type.Span.Start alias.Name)
                |> Option.map (fun alias -> alias, w.Type))
        let wholeTitle (alias: Alias) = $"Inline `{alias.Name}`"
        let useTitle = "Inline this use"
        let offer (title: string) (edits: (int * int * string) list) =
            let after = appliedAndFormatted options file source edits
            match newError result (recompile after) with
            | Some reason -> Refused(title, RefactoringKind.Inline, $"The result would not compile{reason}")
            | None -> Available { Title = title; Kind = RefactoringKind.Inline; Edits = [ difference file source after ]; Rename = None }
        let replace (alias: Alias) (used: TypeExpr) = used.Span.Start, used.Span.End, substituted source alias (usesOf alias.Name used).Value
        let refusal (alias: Alias) =
            if TypeDefModifier.isNominal alias.Modifiers then Some $"`{alias.Name}` is a nominal type, not an alias of its shape"
            elif TypeExpr.within alias.Body |> List.exists (fun part -> (usesOf alias.Name part).IsSome) then Some $"`{alias.Name}` names itself"
            else None
        let whole (alias: Alias) =
            match refusal alias with
            | Some reason -> Refused(wholeTitle alias, RefactoringKind.Inline, reason)
            | None when alias.Modifiers |> List.contains Export ->
                Refused(wholeTitle alias, RefactoringKind.Inline, $"`{alias.Name}` is exported, so another module may use it")
            | None ->
                let uses = written |> List.filter (fun w -> (usesOf alias.Name w.Type).IsSome) |> List.map (fun w -> replace alias w.Type)
                let start, finish = removal source (insertionPoint source syntax alias.Item.Span.Start) alias.Item.Span.End
                offer (wholeTitle alias) ((start, finish, "") :: uses)
        match onDefinition, onUse with
        | Some alias, _ -> [ whole alias ]
        | None, Some (alias, _) when TypeDefModifier.isNominal alias.Modifiers -> []
        | None, Some (alias, used) ->
            let one =
                match refusal alias with
                | Some reason -> Refused(useTitle, RefactoringKind.Inline, reason)
                | None -> offer useTitle [ replace alias used ]
            [ whole alias; one ]
        | None, None -> []
    | _ -> []
