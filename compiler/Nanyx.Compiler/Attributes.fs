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

    /// The imports the module that declares the entry point gets from `host`: the modules of the contexts it provides
    /// that the module sees without importing them, and the host itself (see `hostImport`).
    let private hostImports (host: Host) (module': Module) =
        let provided =
            host.ImplicitModules
            |> List.filter (fun moduleName -> not (importsModule moduleName module'))
            |> List.map (fun moduleName -> TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport moduleName) None ]))
        provided @ [ hostImport host ]

    /// The attribute that says how a workflow keyword, a member of a builder's context, takes its arguments:
    /// `@customKeywordType(#bind)` (see the workflows page).
    let customKeywordTypeAttribute = "customKeywordType"

    /// The kinds `@customKeywordType` takes. A `#bind` keyword takes a value and the rest of the block, which is how every
    /// keyword is desugared (see `WorkflowDesugaring`).
    let keywordKinds = [ "bind" ]

    /// The attributes written before the members of a context declaration (see `TypeFieldAttribute`).
    let private memberAttributes (modifiers: TypeDefModifier list) (body: TypeExpr) =
        match body.Kind with
        | TypeRecord fields
        | TypeOpenRecord(fields, _) when List.contains Context modifiers ->
            fields |> List.choose (function TypeFieldAttribute(name, argument, span) -> Some (name, argument, span) | _ -> None)
        | _ -> []

    /// The errors in the attributes of `module'`'s record types' fields. Only a member of a context declaration may
    /// have one, and the only one it may have is `@customKeywordType`, with a kind from `keywordKinds`.
    let private fieldAttributeErrors (module': Module) : Diagnostic list =
        let expressions = module' |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
        let rec typeDefs (item: TopLevelItem) =
            match item.Kind with
            | Def (TypeDef(_, modifiers, _, body)) -> [ modifiers, body ]
            | ModuleBlock(_, items) -> items |> List.collect typeDefs
            | _ -> []
        let localTypeDefs =
            expressions
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements |> List.choose (fun statement ->
                        match statement.Kind with
                        | TypeDefStatement(_, modifiers, _, body) -> Some (modifiers, body)
                        | _ -> None)
                | _ -> [])
        let members =
            (module' |> List.collect typeDefs) @ localTypeDefs
            |> List.collect (fun (modifiers, body) -> memberAttributes modifiers body)
        let onMembers = members |> List.map (fun (_, _, span) -> span) |> Set.ofList
        let types = (module' |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
        let misplaced =
            types
            |> List.collect TypeExpr.within
            |> List.collect (fun typeExpr ->
                match typeExpr.Kind with
                | TypeRecord fields
                | TypeOpenRecord(fields, _) ->
                    fields |> List.choose (function TypeFieldAttribute(name, _, span) when not (onMembers.Contains span) -> Some (name, span) | _ -> None)
                | _ -> [])
            |> List.map (fun (name, span) ->
                Diagnostics.at DiagnosticCodes.MisplacedAttribute span $"Attribute '@{name}' can only come before a member of a context declaration")
        let kinds = keywordKinds |> List.map (fun kind -> $"`#{kind}`") |> String.concat ", "
        let onMember (name: Identifier, argument: Expression option, span: SourceSpan) =
            match argument with
            | _ when name <> customKeywordTypeAttribute ->
                Some (Diagnostics.at DiagnosticCodes.UnknownAttribute span $"Unknown attribute '@{name}' on a context's member")
            | Some (Kind (TagExpr(kind, None))) when List.contains kind keywordKinds -> None
            | Some (Kind (TagExpr(kind, None))) ->
                Some (Diagnostics.at DiagnosticCodes.UnknownKeywordKind span $"Unknown keyword kind '#{kind}': the kinds are {kinds}")
            | _ ->
                Some (Diagnostics.at DiagnosticCodes.UnknownKeywordKind span $"Attribute '@{name}' takes the keyword's kind, as `@{name}(#bind)`: the kinds are {kinds}")
        misplaced @ List.choose onMember members

    /// Resolves `@attribute` lines. `@entry` on a definition appends a call of the host's `run` with that definition
    /// at the end of the module, so it runs as the program's start function, and imports the host and the modules of
    /// the contexts it provides (see `Host`); any other attribute, or an attribute that does not precede a value
    /// definition, is an error. `@entry` takes no argument. The host is `host`: the package's, or the console host for a
    /// file in no package. With the module, each entry point (see `EntryPoint`). An attribute before a member of a
    /// context declaration is checked here too, and stays where it is (see `fieldAttributeErrors`).
    let applyAttributes (host: Host) (module': Module) : Result<Module * EntryPoint list, Diagnostic list> =
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
        match loop module' [] [] (List.rev (fieldAttributeErrors module')) with
        | items, entries, [] ->
            let entryImport = if entries.IsEmpty then [] else hostImports host items
            Ok (entryImport @ items @ List.map snd entries, List.map fst entries)
        | _, _, errors -> Error errors
