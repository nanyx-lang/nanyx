/// The standard-library imports that would bring a name that is not in scope into it. The error for the name says
/// which they are, and `ImportFixes` offers to add each, so the two always name the same modules.
module NyxCompiler.StandardLibraryImports

open Parser.Program

/// An import of a standard-library module: its path, as `nanyx/text`, and the name it is imported as, if any.
type Import = { Path: string; Alias: string option }

/// The imports that would bring `name` into scope, given the standard-library modules that export each name, by
/// their import paths (`exports`): `nanyx/<module> as <module>` when `name` is qualified by the name of a module that
/// exports the rest of it, as `math.abs`, then `nanyx/<module>` for each module that exports `name` itself.
let candidates (exports: Map<string, string list>) (name: string) : Import list =
    let exportersOf name = exports |> Map.tryFind name |> Option.defaultValue []
    let moduleName (path: string) = path.Substring(path.IndexOf '/' + 1)
    let qualified =
        match name.IndexOf '.' with
        | dot when dot > 0 ->
            let qualifier = name.Substring(0, dot)
            exportersOf (name.Substring(dot + 1))
            |> List.filter (fun path -> moduleName path = qualifier)
            |> List.map (fun path -> { Path = path; Alias = Some qualifier })
        | _ -> []
    qualified @ (exportersOf name |> List.map (fun path -> { Path = path; Alias = None }))

/// `import` as a line of source: `import nanyx/math as math`.
let line (import: Import) =
    match import.Alias with
    | Some alias -> $"import {import.Path} as {alias}"
    | None -> $"import {import.Path}"

/// The end of the error for `name`, which is not in scope, that says where the standard library defines it, given
/// the modules that export each name (`exports`): ". 'trim' is defined in 'nanyx/text': add `import nanyx/text`".
/// Empty when no module exports it.
let hint (exports: Map<string, string list>) (name: string) : string =
    match candidates exports name with
    | [] -> ""
    | first :: _ as imports ->
        let defined = if first.Alias.IsSome then name.Substring(name.IndexOf '.' + 1) else name
        let modules = imports |> List.map (fun import -> $"'{import.Path}'") |> List.distinct
        let lines = imports |> List.map (fun import -> $"`{line import}`") |> String.concat " or "
        $". '{defined}' is defined in {Diagnostics.listed modules}: add {lines}"

/// The error `code` for `name`, written at `span`, which is not in scope, with `message`. When a standard-library
/// module exports `name` (`exports`), the message goes on to say which import brings it in; otherwise it asks about
/// the names of `candidates` closest to it, as `NameSuggestions.unknown` does.
let unknown (exports: Map<string, string list>) (code: DiagnosticCode) (span: SourceSpan) (message: string) (candidates: string seq) (name: string) : Diagnostic =
    match hint exports name with
    | "" -> NameSuggestions.unknown code span message candidates name
    | found -> Diagnostics.at code span (message + found)
