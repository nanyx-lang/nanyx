namespace NyxCompiler

open Parser.Program

/// The `@attribute` lines of a module, which the compiler resolves before inference.
module Attributes =
    /// The attribute that marks a definition as the program's entry point: `@entry def main = { ... }`.
    let entryAttribute = "entry"

    /// The standard-library module the entry point runs inside: `@entry` is called by the runtime with its
    /// `Console` instance, so the module that declares the entry point sees `nanyx/console` without importing it.
    let entryContextModule = StandardLibrary.importPrefix + "console"

    let private importsModule (moduleName: string) (module': Module) =
        SyntaxTree.collectImports module'
        |> List.exists (fun item ->
            match item.Source with
            | ModuleImport name -> name = moduleName
            | PathImport _ -> false)

    /// Resolves `@attribute` lines. `@entry` on a definition appends a call to that definition at the end
    /// of the module, so it runs as the program's start function, and imports `nanyx/console` for the module
    /// (the context the runtime supplies to the entry point); any other attribute, or an attribute that does
    /// not precede a value definition, is an error. `@entry` takes no argument. With the module, the call made up for
    /// each `@entry`, by the node of the attribute.
    let applyAttributes (module': Module) : Result<Module * Map<NodeId, NodeId list>, Diagnostic list> =
        let rec loop (remaining: TopLevelItem list) (acc: TopLevelItem list) (entryCalls: (NodeId * TopLevelItem) list) (errors: Diagnostic list) =
            match remaining with
            | [] -> List.rev acc, List.rev entryCalls, List.rev errors
            | Kind (Attribute(name, _)) as attribute :: rest when name <> entryAttribute ->
                loop rest acc entryCalls (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"Unknown attribute '@{name}'" :: errors)
            | Kind (Attribute(name, Some argument)) :: rest ->
                let error = Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span $"Attribute '@{name}' takes no argument"
                loop rest acc entryCalls (error :: errors)
            | Kind (Attribute _) as attribute :: (Kind (Def (ValueDef(_, name, _, value))) as def) :: rest ->
                // The call is made up for the def, and reported there.
                let call = TopLevelItem.at def.Span (Expr (Expression.from value (FunctionCall(name, None, []))))
                loop rest (def :: acc) ((attribute.Id, call) :: entryCalls) errors
            | Kind (Attribute(name, _)) as attribute :: rest ->
                loop rest acc entryCalls (Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span $"Attribute '@{name}' must be followed by a definition" :: errors)
            | item :: rest -> loop rest (item :: acc) entryCalls errors
        match loop module' [] [] [] with
        | items, entryCalls, [] ->
            let entryImport =
                if not entryCalls.IsEmpty && not (importsModule entryContextModule items) then
                    [ TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport entryContextModule) None ]) ]
                else
                    []
            let lowered = entryCalls |> List.map (fun (attribute, call) -> attribute, SyntaxTree.ofItem call |> List.map _.Id) |> Map.ofList
            Ok (entryImport @ items @ List.map snd entryCalls, lowered)
        | _, _, errors -> Error errors
