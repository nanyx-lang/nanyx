/// Implement a context for a type: write a stub for each member of the context the type does not attach, as
/// `def Point.compare: Point, Point -> #less | #equal | #greater = { a, b -> ??? }`, so that the compiler makes the
/// type's instance from them. Positions are character offsets into the source as the parser saw it, with line endings
/// normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.ImplementContext

open Parser.Program
open NyxCompiler
open Nanyx.Formatter
open NyxCompiler.Refactorings.RefactoringText

let private title (context: string) = $"Implement `{context}`"

/// The top-level type definitions of `syntax` that are not contexts, each with its item and name.
let private typeDefinitions (syntax: Module) =
    syntax
    |> List.choose (fun item ->
        match item.Kind with
        | Def (TypeDef(name, modifiers, _, _)) when not (SourceSpan.isNone item.Span) && not (modifiers |> List.contains Context) -> Some (item, name)
        | _ -> None)

/// Where the name of the type `name`, defined by `item`, is written in `source`.
let private nameSpanOf (source: string) (item: TopLevelItem) (name: string) =
    let text = SourceSpan.textIn source item.Span
    let found = System.Text.RegularExpressions.Regex.Match(text, $@"\btype\s+({System.Text.RegularExpressions.Regex.Escape name})\b")
    if found.Success then Some (item.Span.Start + found.Groups[1].Index, item.Span.Start + found.Groups[1].Index + name.Length) else None

/// The top-level defs of `syntax` attached to the type `name`: those named `name.member`.
let private attachedDefs (syntax: Module) (name: string) =
    syntax
    |> List.filter (fun item ->
        match item.Kind with
        | Def (ValueDef(_, defName, _, _)) -> defName.StartsWith $"{name}." && not (SourceSpan.isNone item.Span)
        | _ -> false)

/// Each member's name and type of the context `info`, which has one parameter, with the parameter as `self`.
let private membersFor (info: TypeDefInfo) (self: Ty) =
    match Unifier.unalias info.Underlying with
    | TyRecord(fields, _) -> fields |> Map.toList |> List.map (fun (name, ty) -> name, Unifier.applyToParameters info.Parameters [ self ] ty)
    | _ -> []

/// A member's name as an attached def writes it: an operator in backticks.
let private written (memberName: string) =
    if memberName |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_') then memberName else $"`{memberName}`"

/// The stub of a value of type `ty`: a lambda that takes its parameters, for a function, its body `???`.
let private stubOf (ty: Ty) =
    let names count = List.init count (fun index -> string (char (int 'a' + index))) |> String.concat ", "
    match ty with
    | TyFunc(TyRecord(fields, _), _, _) when fields.IsEmpty -> "{ () -> ??? }"
    | TyFunc(TyRecord(fields, _), _, _) when fields |> Map.forall (fun key _ -> key |> Seq.forall System.Char.IsDigit) ->
        $"{{ {names fields.Count} -> ??? }}"
    | TyFunc _ -> $"{{ {names 1} -> ??? }}"
    | _ -> "???"

/// The refactorings at `offset` in `source`, the text of the root module of `result` compiled as `file`, laid out with
/// `options`, on the name of a type definition (not a context's): for each context in scope with one parameter, whose
/// members the type does not all attach, implementing it, which writes a def for each missing member attached to the
/// type, its type the member's with the context's parameter the type, and its body `???` (a lambda taking the
/// parameters, for a function), after the type's last attached def, or after the type. A context whose members the
/// type attaches only some of is offered first. Refused for a context the type attaches every member of, and, as
/// `recompile` compiles a text of the module as `result` was compiled, where the stubs would not compile, beyond their
/// `???`s. A type with parameters, and a scoped or private context, are not offered. Empty elsewhere, and where the
/// module did not type-check.
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
    | Some syntax, Some typed ->
        let onName =
            typeDefinitions syntax
            |> List.tryFind (fun (item, name) ->
                match nameSpanOf source item name with
                | Some (start, finish) -> start <= offset && offset <= finish
                | None -> false)
        match onName with
        | Some (item, name) when typed.TypeDefs |> Map.tryFind name |> Option.exists (fun info -> info.Parameters.IsEmpty) ->
            let attached = attachedDefs syntax name
            let has (memberName: string) =
                attached |> List.exists (fun def -> match def.Kind with Def (ValueDef(_, defName, _, _)) -> defName = $"{name}.{memberName}" | _ -> false)
            // The type, as a stub's type writes it.
            let self = TyPrimitive name
            let contexts =
                typed.TypeDefs
                |> Map.toList
                |> List.filter (fun (contextName, info) ->
                    info.IsContext && not info.IsScoped && not info.IsPrivate && info.Parameters.Length = 1 && not (contextName.StartsWith "__"))
                |> List.map (fun (contextName, info) -> contextName, membersFor info self)
                |> List.filter (fun (_, members) -> not members.IsEmpty)
                // A context's own name, not the name an import gives it too.
                |> List.distinctBy (fun (_, members) -> members |> List.map fst)
            let after = (item :: attached) |> List.map (fun item -> item.Span.End) |> List.max
            let offer (contextName: string, members: (string * Ty) list) =
                match members |> List.filter (fst >> has >> not) with
                | [] -> Refused(title contextName, RefactoringKind.Rewrite, $"`{name}` attaches every member of `{contextName}`")
                | missing ->
                    let stubs =
                        missing
                        |> List.map (fun (memberName, ty) -> $"def {name}.{written memberName}: {Unifier.tyToDisplayString Ascii ty} = {stubOf ty}")
                        |> List.map (fun stub -> "\n\n" + stub)
                        |> String.concat ""
                    Deferred(title contextName, RefactoringKind.Rewrite, fun () ->
                        let text = appliedAndFormatted options file source [ after, after, stubs ]
                        // The stubs' `???`s are errors of their own, which are what the user is to fill in.
                        let compiled = recompile text
                        let unfilled = DiagnosticCodes.NotImplemented.Code
                        let compiled = { compiled with Diagnostics = compiled.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code <> unfilled) }
                        match newError result compiled with
                        | Some reason -> Refused(title contextName, RefactoringKind.Rewrite, $"The stubs would not compile{reason}")
                        | None -> Available { Title = title contextName; Kind = RefactoringKind.Rewrite; Edits = [ difference file source text ]; Rename = None })
            let partly (_, members: (string * Ty) list) = members |> List.exists (fst >> has)
            contexts |> List.sortBy (fun context -> not (partly context), fst context) |> List.map offer
        | _ -> []
    | _ -> []
