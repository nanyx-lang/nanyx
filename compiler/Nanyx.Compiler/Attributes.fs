namespace NyxCompiler

open Parser.Program

/// The `@attribute` lines of a module, which the compiler resolves before inference.
module Attributes =
    /// The attribute that marks a definition as the program's entry point: `@entry def main = { ... }`.
    let entryAttribute = "entry"

    /// The import of `host` itself, under an alias of its own, which a module that runs under it gets.
    let internal hostImport (host: Host) =
        TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport host.Import) (Some Hosts.importAlias) ])

    /// The attribute that says how a workflow keyword, a member of a builder's context, takes its arguments:
    /// `@customKeywordType(#bind)` (see the workflows page).
    let customKeywordTypeAttribute = "customKeywordType"

    /// The attribute that lets a def break linter rules: `@allow(default/unusedBindings)` before a def, a local one too,
    /// suppresses that rule's diagnostics inside it (see `Allows`).
    let allowAttribute = "allow"

    /// The rules an `@allow` names, each as its linter, its rule and where it is written, or `None` if `argument` is not
    /// one or more rules separated by `,`.
    let allowedRules (argument: Expression option) : (Identifier * Identifier * SourceSpan) list option =
        match argument with
        | Some (LinterRuleSyntax.Rules rules) -> Some (rules |> List.map (fun (linter, rule, expr) -> linter, rule, expr.Span))
        | _ -> None

    let private allowArgumentError (span: SourceSpan) =
        Diagnostics.at
            DiagnosticCodes.UnexpectedAttributeArgument
            span
            $"Attribute '@{allowAttribute}' takes the linter rules it allows, separated by commas, as `@{allowAttribute}(default/unusedBindings)`"

    /// The errors in the attributes of the blocks of `module'`: only `@allow` may be written in a block, with the rules
    /// it allows, and it must be followed by a def, perhaps after other attributes. They stay where they are.
    let private statementAttributeErrors (module': Module) : Diagnostic list =
        let rec inBlock (statements: Statement list) =
            match statements with
            | [] -> []
            | ({ Kind = AttributeStatement(name, argument) } as attribute) :: rest ->
                let followedByDef =
                    rest
                    |> List.skipWhile (fun statement -> match statement.Kind with AttributeStatement _ -> true | _ -> false)
                    |> List.tryHead
                    |> Option.exists (fun statement -> match statement.Kind with DefStatement _ -> true | _ -> false)
                let error =
                    if name <> allowAttribute then
                        Some (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"Unknown attribute '@{name}': in a block, only '@{allowAttribute}' can come before a def")
                    elif (allowedRules argument).IsNone then
                        Some (allowArgumentError attribute.Span)
                    elif not followedByDef then
                        Some (Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span $"Attribute '@{name}' must be followed by a definition")
                    else
                        None
                Option.toList error @ inBlock rest
            | _ :: rest -> inBlock rest
        module'
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.blocksWithin
        |> List.collect inBlock

    /// `items` without their `@allow` attributes, which are for the linters (see `Allows`), with the errors in them: an
    /// `@allow` must name the rules it allows, and be followed by a value definition, perhaps after other attributes.
    let rec private withoutAllows (items: TopLevelItem list) : TopLevelItem list * Diagnostic list =
        match items with
        | [] -> [], []
        | Kind (Attribute(name, argument)) as attribute :: rest when name = allowAttribute ->
            let others, errors = withoutAllows rest
            let next = rest |> List.skipWhile (function Kind (Attribute _) -> true | _ -> false) |> List.tryHead
            let error =
                match next with
                | _ when (allowedRules argument).IsNone -> [ allowArgumentError attribute.Span ]
                | Some (Kind (Def (ValueDef _))) -> []
                | _ -> [ Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span $"Attribute '@{name}' must be followed by a definition" ]
            others, error @ errors
        | Kind (ModuleBlock(name, blockItems)) as block :: rest ->
            let blockItems, blockErrors = withoutAllows blockItems
            let others, errors = withoutAllows rest
            { block with Kind = ModuleBlock(name, blockItems) } :: others, blockErrors @ errors
        | item :: rest ->
            let others, errors = withoutAllows rest
            item :: others, errors

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

    /// The errors for the defs in `items` written with no value that no `@extern` declares a foreign function: those in
    /// module blocks, where no attribute applies, and those at the top level `applyAttributes` leaves as they are.
    let rec private bodilessErrors (items: TopLevelItem list) : Diagnostic list =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, _, Kind ExternSyntax.Bodiless)) ->
                [ Diagnostics.at
                      DiagnosticCodes.DefinitionWithoutValue
                      item.Span
                      $"'{name}' has no value: write `= value` after it, or mark it `@{Externs.attribute}(\"module\", \"name\")` to declare a foreign function" ]
            | ModuleBlock(_, blockItems) -> bodilessErrors blockItems
            | _ -> [])

    /// Resolves `@attribute` lines. `@entry` on a definition appends a call of the host's `run` with that definition
    /// at the end of the module, so it runs as the program's start function, and imports the host and the modules of
    /// the contexts it provides (see `Host`). `@extern("module", "name")` on a definition written with a type and no
    /// value makes it a foreign function (see `Externs`): its value becomes the import's module and name (see
    /// `ExternSyntax`). Any other attribute, an attribute that does not precede a value definition, or a definition with
    /// no value and no `@extern`, is an error. `@entry` takes no argument. The host is `host`: the package's, or the
    /// console host for a file in no package. A package with no host is a library, so an `@entry` in it is an error. With the module, each entry point (see `EntryPoint`). An attribute before
    /// a member of a context declaration is checked here too, and stays where it is (see `fieldAttributeErrors`), as is
    /// one in a block (see `statementAttributeErrors`). `@allow` is for the linters: it is checked and left out (see
    /// `withoutAllows`).
    let applyAttributes (host: Host option) (module': Module) : Result<Module * EntryPoint list, Diagnostic list> =
        let rec loop (remaining: TopLevelItem list) (acc: TopLevelItem list) (entries: (EntryPoint * TopLevelItem) list) (externs: (string * Extern * SourceSpan) list) (errors: Diagnostic list) =
            match remaining with
            | [] -> List.rev acc, List.rev entries, List.rev externs, List.rev errors
            | Kind (Attribute(name, argument)) as attribute :: (Kind (Def (ValueDef(isExport, defName, typeOpt, value))) as def) :: rest when name = Externs.attribute ->
                match Externs.declared attribute.Span argument defName def.Span typeOpt value with
                | Ok declared ->
                    let imported = { def with Kind = Def (ValueDef(isExport, defName, typeOpt, ExternSyntax.import declared.Module declared.Name value)) }
                    loop rest (imported :: acc) entries ((defName, declared, def.Span) :: externs) errors
                | Error diagnostics -> loop rest (def :: acc) entries externs (List.rev diagnostics @ errors)
            | Kind (Attribute(name, _)) as attribute :: rest when name <> entryAttribute && name <> Externs.attribute ->
                loop rest acc entries externs (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"Unknown attribute '@{name}'" :: errors)
            | Kind (Attribute(name, Some argument)) :: rest when name = entryAttribute ->
                let error = Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span $"Attribute '@{name}' takes no argument"
                loop rest acc entries externs (error :: errors)
            | Kind (Attribute(attributeName, _)) as attribute :: (Kind (Def (ValueDef(_, name, _, _))) as def) :: rest when attributeName = entryAttribute && host.IsNone ->
                let error =
                    Diagnostics.at
                        DiagnosticCodes.EntryInLibrary
                        attribute.Span
                        $"'{name}' is an entry point, but its package names no host, so it is a library, which has none: to make it an application, name the host its `@entry` runs under in its manifest, as `host = (package = \"{Hosts.consolePackage}\", version = \"{Toolchain.version}\")`"
                loop rest (def :: acc) entries externs (error :: errors)
            | Kind (Attribute(attributeName, _)) as attribute :: (Kind (Def (ValueDef(_, name, _, value))) as def) :: rest when attributeName = entryAttribute ->
                // The call is made up for the def, and reported there. The entry point passed to `run` is at the
                // attribute, so that a mismatch with the host's `run` is told apart from any other.
                let argument = Expression.at attribute.Span (IdentifierExpr name)
                let run = $"{Hosts.importAlias}.{Hosts.runFunction}"
                let call = TopLevelItem.at def.Span (Expr (Expression.from value (FunctionCall(run, None, [ argument ]))))
                let entry =
                    { Attribute = attribute.Id
                      Name = name
                      Host = host.Value
                      Argument = attribute.Span
                      Call = SyntaxTree.ofItem call |> List.collect SyntaxTree.expressionsWithin |> List.map _.Id }
                loop rest (def :: acc) ((entry, call) :: entries) externs (List.rev (bodilessErrors [ def ]) @ errors)
            | Kind (Attribute(name, _)) as attribute :: rest ->
                loop rest acc entries externs (Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span $"Attribute '@{name}' must be followed by a definition" :: errors)
            | item :: rest -> loop rest (item :: acc) entries externs (List.rev (bodilessErrors [ item ]) @ errors)
        let module', allowErrors = withoutAllows module'
        match loop module' [] [] [] (List.rev (fieldAttributeErrors module')) with
        | items, entries, externs, errors ->
            match allowErrors @ statementAttributeErrors module' @ errors @ Externs.conflicts externs with
            | [] ->
                // The host's prelude is imported into every module of the program, not here (see `Library`).
                let entryImport = match host with Some host when not entries.IsEmpty -> [ hostImport host ] | _ -> []
                Ok (entryImport @ items @ List.map snd entries, List.map fst entries)
            | errors -> Error errors
