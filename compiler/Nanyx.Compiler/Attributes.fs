namespace NyxCompiler

open Parser.Program

/// The `@attribute` lines of a module, which the compiler resolves before inference.
module Attributes =
    /// The attribute that marks a definition as the program's entry point: `@entry def main = { ... }`.
    let entryAttribute = "entry"

    let private importsModule (moduleName: string) (module': Module) =
        SyntaxTree.collectImports module'
        |> List.exists (fun item ->
            match item.Source with
            | ModuleImport name -> name = moduleName
            | PathImport _ -> false)

    /// The import of `host` itself, under an alias of its own, which a module that runs under it gets.
    let internal hostImport (host: Host) =
        TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport host.Module) (Some Hosts.importAlias) ])

    /// The imports the module that declares the entry point gets from `host`: the modules of the contexts it provides,
    /// which the module sees without importing them, and the host itself (see `hostImport`).
    let private hostImports (host: Host) (module': Module) =
        let provided =
            host.ProvidedModules
            |> List.filter (fun moduleName -> not (importsModule moduleName module'))
            |> List.map (fun moduleName -> TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport moduleName) None ]))
        provided @ [ hostImport host ]

    /// Resolves `@attribute` lines. `@entry` on a definition appends a call of the host's `run` with that definition
    /// at the end of the module, so it runs as the program's start function, and imports the host and the modules of
    /// the contexts it provides (see `Host`); any other attribute, or an attribute that does not precede a value
    /// definition, is an error. `@entry` takes no argument. With the module, each entry point (see `EntryPoint`).
    let applyAttributes (module': Module) : Result<Module * EntryPoint list, Diagnostic list> =
        let host = Hosts.console
        let rec loop (remaining: TopLevelItem list) (acc: TopLevelItem list) (entries: (EntryPoint * TopLevelItem) list) (errors: Diagnostic list) =
            match remaining with
            | [] -> List.rev acc, List.rev entries, List.rev errors
            | Kind (Attribute(name, _)) as attribute :: rest when name <> entryAttribute ->
                loop rest acc entries (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"Unknown attribute '@{name}'" :: errors)
            | Kind (Attribute(name, Some argument)) :: rest ->
                let error = Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span $"Attribute '@{name}' takes no argument"
                loop rest acc entries (error :: errors)
            | Kind (Attribute _) as attribute :: (Kind (Def (ValueDef(_, name, _, value))) as def) :: rest ->
                // The call is made up for the def, and reported there. The entry point passed to `run` is at the
                // attribute, so that a mismatch with the host's `run` is told apart from any other.
                let argument = Expression.at attribute.Span (IdentifierExpr name)
                let run = $"{Hosts.importAlias}.{Hosts.runFunction}"
                let call = TopLevelItem.at def.Span (Expr (Expression.from value (FunctionCall(run, None, [ argument ]))))
                let entry =
                    { Attribute = attribute.Id
                      Name = name
                      Host = host
                      Argument = attribute.Span
                      Call = SyntaxTree.ofItem call |> List.collect SyntaxTree.expressionsWithin |> List.map _.Id }
                loop rest (def :: acc) ((entry, call) :: entries) errors
            | Kind (Attribute(name, _)) as attribute :: rest ->
                loop rest acc entries (Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span $"Attribute '@{name}' must be followed by a definition" :: errors)
            | item :: rest -> loop rest (item :: acc) entries errors
        match loop module' [] [] [] with
        | items, entries, [] ->
            let entryImport = if entries.IsEmpty then [] else hostImports host items
            Ok (entryImport @ items @ List.map snd entries, List.map fst entries)
        | _, _, errors -> Error errors
