namespace NyxCompiler

open Parser.Program

/// The `@attribute` lines of a module, which the compiler resolves before inference.
module Attributes =
    /// A def marked `@test`: its name, where it is written, where its attribute is, whether the attribute is
    /// `@test(#io)` (see `ioTestArgument`), and the crash it expects, if it is `@test(#crashes)` (see
    /// `crashesTestArgument`).
    type MarkedTest =
        { Name: string
          Span: SourceSpan
          Attribute: SourceSpan
          Io: bool
          Crashes: ExpectedCrash option }

    /// The defs of a module marked `@component`, which a component exports, and `@import`, which it imports, each by name
    /// with where it is written; and those marked `@test`.
    type ComponentDefs =
        { Exports: (string * SourceSpan) list
          Imports: (string * SourceSpan) list
          /// The exports that require contexts, each run under the host's by a def `applyAttributes` made up (see
          /// `providedExport`): the call of the host's `provide` in it, as an entry point's call of `run` is.
          Provided: EntryPoint list
          /// The defs marked `@test`, which a test build runs (see `testAttribute`).
          Tests: MarkedTest list }

    /// The attribute that marks a definition as the program's entry point: `@entry def main = { ... }`.
    let entryAttribute = "entry"

    /// The attribute that marks a definition as a test, which a test build runs and every other build leaves out:
    /// `` @test def `adds points` = { ... } ``.
    let testAttribute = "test"

    /// The argument of `@test(#io)`, which runs a test with the console host's real `Console`, `Env` and `FileSystem`
    /// (see `Hosts.ioTestContexts`).
    let ioTestArgument = "io"

    /// The argument of `@test(#crashes)`, which passes a test only if it crashes, and of `@test(#crashes("too big"))`,
    /// only if it crashes with a message that contains the text (#3078).
    let crashesTestArgument = "crashes"

    /// What `@test`'s argument, `argument`, asks for: whether the test is `#io`, and the crash it expects. It is `#io`,
    /// `#crashes`, `#crashes("text")`, or a tuple of `#io` and one of the others, in either order; none for any other.
    let private testArguments (argument: Expression option) : (bool * ExpectedCrash option) option =
        let one (argument: Expression) =
            match argument.Kind with
            | TagExpr(tag, None) when tag = ioTestArgument -> Some(true, None)
            | TagExpr(tag, None) when tag = crashesTestArgument -> Some(false, Some AnyCrash)
            | TagExpr(tag, Some (Kind (LiteralExpr(StringLit text)))) when tag = crashesTestArgument -> Some(false, Some(CrashContaining text))
            | _ -> None
        match argument with
        | None -> Some(false, None)
        | Some (Kind (TupleExpr [ first; second ])) ->
            match one first, one second with
            | Some(true, None), Some(false, Some crash)
            | Some(false, Some crash), Some(true, None) -> Some(true, Some crash)
            | _ -> None
        | Some argument -> one argument

    /// The def a test build adds to each of its program's modules that has tests, which the build's root calls to run
    /// one: exported, `[Console, Random] int -> ()`, requiring the contexts the test host provides (`Hosts.testContexts`),
    /// it runs the module's test at the index it is given, in the order they are written. No other build has it.
    let testDispatcher = "__tests"

    /// `export def __tests = { index -> if index == 0 then t0() else if index == 1 then t1() else () }` for the tests
    /// `tests`, each with where it is written, which is where its part of the def is (see `testDispatcher`). Its type,
    /// and the contexts it requires, which are its tests', are inferred, as a def's written without a type are.
    let private dispatcherOf (tests: (string * SourceSpan) list) : TopLevelItem =
        let index = "index"
        let span = snd tests.Head
        let body =
            List.foldBack
                (fun (position, (test, at)) otherwise ->
                    let expr kind = Expression.at at kind
                    let isPosition = expr (BinaryOp("==", None, expr (IdentifierExpr index), expr (LiteralExpr(IntLit(bigint (position: int))))))
                    expr (IfExpr(isPosition, expr (FunctionCall(test, None, [])), otherwise)))
                (List.indexed tests)
                (Expression.at span UnitExpr)
        TopLevelItem.at span (Def (ValueDef(true, testDispatcher, None, Expression.at span (Lambda([ index, None ], body, false)))))

    /// `declared`, the names `module'` declares as the parser found them, with those of the `testDispatcher` a test build
    /// gave it, if it did: its name, at the first test, and its parameter. Inference finds a def's symbol, and the
    /// contexts an unannotated one requires, by where it is declared.
    let withDispatcherDeclared (declared: DeclaredNames) (module': Module) : DeclaredNames =
        module'
        |> List.tryPick (function
            | Kind (Def (ValueDef(_, name, _, ({ Kind = Lambda(parameters, _, _) } as value)))) when name = testDispatcher ->
                Some (value, parameters)
            | _ -> None)
        |> Option.map (fun (value, parameters) ->
            declared
            |> Map.add (value.Id, DefValue) [ testDispatcher, value.Span ]
            |> Map.add (value.Id, LambdaParameters) (parameters |> List.map (fun (parameter, _) -> parameter, value.Span)))
        |> Option.defaultValue declared

    /// The attribute that marks an exported definition as one a component built from the program exports:
    /// `@component export def area: Shape -> float = ...`, in the WIT interface its module names (see
    /// `ComponentInterface`).
    let componentAttribute = "component"

    /// The attribute that declares a function another component exports, which a component built from the program
    /// imports: `@import("acme:geometry/shapes@1.0.0", "area", ["width", "height"])` before a def written with a type and
    /// no value, naming the WIT interface, the function, whose name is the def's in kebab case if the attribute doesn't
    /// give it, and the function's parameters, which are `p0`, `p1` and on if it doesn't name them.
    let componentImportAttribute = "import"

    /// The component import `@import` with `argument`, at `attributeSpan`, declares of the def `name` with `typeOpt`
    /// and `value`, as its interface, its function and the names of the function's parameters, none if the attribute
    /// gives none, or the errors in it.
    let private componentImport (attributeSpan: SourceSpan) (argument: Expression option) (name: string) (defSpan: SourceSpan) (typeOpt: TypeExpr option) (value: Expression) =
        let about (diagnostic: Diagnostic) = diagnostic |> Diagnostics.about $"@{componentImportAttribute}"
        let named =
            match argument with
            | Some (Kind (LiteralExpr(StringLit witInterface))) -> Ok (witInterface, ComponentInterface.witName name, [])
            | Some (Kind (TupleExpr [ Kind (LiteralExpr(StringLit witInterface)); Kind (LiteralExpr(StringLit functionName)) ])) -> Ok (witInterface, functionName, [])
            | Some (Kind (TupleExpr [ Kind (LiteralExpr(StringLit witInterface)); Kind (LiteralExpr(StringLit functionName)); Kind (ListExpr parameters) ])) ->
                let names = parameters |> List.choose (function Kind (LiteralExpr(StringLit parameter)) -> Some parameter | _ -> None)
                if names.Length = parameters.Length then Ok (witInterface, functionName, names)
                else
                    Error [ Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument attributeSpan
                                $"The function's parameters are named by a list of strings, as `[\"width\", \"height\"]`"
                            |> about ]
            | _ ->
                Error [ Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument attributeSpan
                            $"It takes the WIT interface the function is exported from, the function's name if it isn't the def's in kebab case, and its parameters' names if they aren't `p0`, `p1` and on, as `@{componentImportAttribute}(\"acme:geometry/shapes@1.0.0\", \"area\", [\"width\", \"height\"])`"
                        |> about ]
        let named =
            named
            |> Result.bind (fun (witInterface, functionName, parameters) ->
                let unnamed = parameters |> List.filter (fun parameter -> ComponentInterface.witName parameter = "")
                let repeated = parameters |> List.countBy ComponentInterface.witName |> List.filter (fun (_, count) -> count > 1) |> List.map fst
                if not (ComponentInterface.isInterfaceName witInterface) then
                    Error [ Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument attributeSpan
                                $"'{witInterface}' isn't a WIT interface's full name, which is its package's namespace and name, then its own, as `acme:geometry/shapes`, and its version, as `@1.0.0`, if it has one"
                            |> about ]
                elif not unnamed.IsEmpty then
                    Error [ Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument attributeSpan
                                $"'{unnamed.Head}' isn't a parameter's name: a WIT name is words of letters and digits, as `width` or `area-of`"
                            |> about ]
                elif not repeated.IsEmpty then
                    Error [ Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument attributeSpan $"Two of the function's parameters are named '{repeated.Head}'" |> about ]
                else Ok (witInterface, functionName, parameters))
        let bodiless =
            match value.Kind, typeOpt with
            | ExternSyntax.Bodiless, Some _ -> Ok ()
            | ExternSyntax.Bodiless, None ->
                Error [ Diagnostics.at DiagnosticCodes.MisplacedAttribute defSpan $"The import '{name}' needs its type written, as `def {name}: string -> int`" |> about ]
            | _ -> Error [ Diagnostics.at DiagnosticCodes.MisplacedAttribute value.Span $"The import '{name}' has a value: another component provides it, so write only its type" |> about ]
        match named, bodiless with
        | Ok named, Ok () -> Ok named
        | _ -> Error ((match named with Error errors -> errors | Ok _ -> []) @ (match bodiless with Error errors -> errors | Ok _ -> []))

    /// The import of `host` itself, under an alias of its own, which a module that runs under it gets.
    let internal hostImport (host: Host) =
        TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport host.Import) (Some Hosts.importAlias) ])

    /// The attribute that says how a workflow keyword, a member of a builder's context, takes its arguments:
    /// `@customKeywordType(#bind)` (see the workflows page).
    let customKeywordTypeAttribute = "customKeywordType"

    /// The attribute that lets a def break linter rules: `@allow(default/unusedBindings)` before a def, a local one too,
    /// or a top-level type, suppresses that rule's diagnostics inside it (see `Allows`).
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
            $"It takes the linter rules it allows, separated by commas, as `@{allowAttribute}(default/unusedBindings)`"
        |> Diagnostics.about $"@{allowAttribute}"

    /// The error for the attribute `name`, written at `span`, that no definition follows.
    let private misplaced (name: Identifier) (span: SourceSpan) =
        Diagnostics.at DiagnosticCodes.MisplacedAttribute span "It must be followed by a definition" |> Diagnostics.about $"@{name}"

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
                        Some (Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span $"In a block, only '@{allowAttribute}' can come before a def" |> Diagnostics.about $"@{name}")
                    elif (allowedRules argument).IsNone then
                        Some (allowArgumentError attribute.Span)
                    elif not followedByDef then
                        Some (misplaced name attribute.Span)
                    else
                        None
                Option.toList error @ inBlock rest
            | _ :: rest -> inBlock rest
        module'
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.blocksWithin
        |> List.collect inBlock

    /// `items` without their `@allow` attributes, which are for the linters (see `Allows`), with the errors in them: an
    /// `@allow` must name the rules it allows, and be followed by a def or a type, perhaps after other attributes.
    let rec private withoutAllows (items: TopLevelItem list) : TopLevelItem list * Diagnostic list =
        match items with
        | [] -> [], []
        | Kind (Attribute(name, argument)) as attribute :: rest when name = allowAttribute ->
            let others, errors = withoutAllows rest
            let next = rest |> List.skipWhile (function Kind (Attribute _) -> true | _ -> false) |> List.tryHead
            let error =
                match next with
                | _ when (allowedRules argument).IsNone -> [ allowArgumentError attribute.Span ]
                | Some (Kind (Def _)) -> []
                | _ -> [ misplaced name attribute.Span ]
            others, error @ errors
        | Kind (ModuleBlock(name, blockItems)) as block :: rest ->
            let blockItems, blockErrors = withoutAllows blockItems
            let others, errors = withoutAllows rest
            { block with Kind = ModuleBlock(name, blockItems) } :: others, blockErrors @ errors
        | item :: rest ->
            let others, errors = withoutAllows rest
            item :: others, errors

    /// The kind a `@customKeywordType` attribute's argument names, as `#delay` does (see `WorkflowSyntax.keywordKinds`).
    let private (|KeywordKindTag|_|) (argument: Expression option) =
        match argument with
        | Some (Kind (TagExpr(tag, None))) -> WorkflowSyntax.keywordKinds |> List.tryFind (fst >> (=) tag) |> Option.map snd
        | _ -> None

    /// The attributes written before the members of a context declaration (see `TypeFieldAttribute`), each with the
    /// member it is written before.
    let private memberAttributes (modifiers: TypeDefModifier list) (body: TypeExpr) =
        match body.Kind with
        | TypeRecord fields
        | TypeOpenRecord(fields, _) when List.contains Context modifiers ->
            TypeRecordField.withAttributes fields |> List.collect (fun (attributes, field) -> attributes |> List.map (fun attribute -> attribute, field))
        | _ -> []

    /// The attributes written before the members of `module'`'s context declarations, each with its member: those at the
    /// top level, in module blocks and in blocks.
    let private contextMemberAttributes (module': Module) =
        let rec typeDefs (item: TopLevelItem) =
            match item.Kind with
            | Def (TypeDef(_, modifiers, _, body)) -> [ modifiers, body ]
            | ModuleBlock(_, items) -> items |> List.collect typeDefs
            | _ -> []
        let localTypeDefs =
            module'
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.expressionsWithin
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements |> List.choose (fun statement ->
                        match statement.Kind with
                        | TypeDefStatement(_, modifiers, _, body) -> Some (modifiers, body)
                        | _ -> None)
                | _ -> [])
        (module' |> List.collect typeDefs) @ localTypeDefs
        |> List.collect (fun (modifiers, body) -> memberAttributes modifiers body)

    /// The top-level defs of `module'` marked `@customKeywordType`, with the kind it names, by name: a `#delay` keyword
    /// needs no builder, so a def can be one (`nanyx/async`'s `start`).
    /// The bare name of `defName`, if it is a def attached to a context `module'` declares, as `Async.start` is to
    /// `Async`: the name the def is in scope by where the context is available.
    let private attachedToContext (module': Module) (defName: Identifier) : Identifier option =
        let dot = defName.LastIndexOf '.'
        if dot <= 0 then None
        else
            let qualifier = defName.Substring(0, dot)
            let declares =
                module'
                |> List.exists (function
                    | Kind (Def (TypeDef(name, modifiers, _, _))) -> name = qualifier && modifiers |> List.contains Context
                    | _ -> false)
            if declares then Some (defName.Substring(dot + 1)) else None

    let private keywordDefs (module': Module) : (Identifier * KeywordKind) list =
        module'
        |> List.pairwise
        |> List.choose (fun (attribute, def) ->
            match attribute.Kind, def.Kind with
            | Attribute(name, KeywordKindTag kind), Def (ValueDef(_, defName, _, _)) when name = customKeywordTypeAttribute ->
                attachedToContext module' defName |> Option.map (fun bare -> bare, kind)
            | _ -> None)

    /// The kinds of the workflow keywords `module'`'s context declarations mark with `@customKeywordType`, by the
    /// member's name, and of the defs attached to its contexts it marks so, by their bare names, which reach them
    /// where the context is available (see `WorkflowDesugaring`, `AttachedContextDefs`).
    let keywordKinds (module': Module) : Map<Identifier, KeywordKind> =
        contextMemberAttributes module'
        |> List.choose (fun ((name, argument, _), field) ->
            match argument, field with
            | KeywordKindTag kind, TypeField(memberName, _, _, _) when name = customKeywordTypeAttribute -> Some (memberName, kind)
            | _ -> None)
        |> List.append (keywordDefs module')
        |> Map.ofList

    /// `kinds` with the keywords of `nearer` added, whose kinds win where both have a keyword.
    let withKeywordKinds (nearer: Map<Identifier, KeywordKind>) (kinds: Map<Identifier, KeywordKind>) =
        nearer |> Map.fold (fun acc name kind -> Map.add name kind acc) kinds

    /// The parameters of the function type `typeExpr`, its contexts and constraints aside, if it is one.
    let rec private parameterTypes (typeExpr: TypeExpr) =
        match typeExpr.Kind with
        | TypeWithContext(_, inner)
        | TypeConstraint(_, inner, _)
        | TypeWhere(inner, _) -> parameterTypes inner
        | TypeFunc(Kind (TypeTuple parameters), _) -> Some parameters
        | TypeFunc(parameter, _) -> Some [ parameter ]
        | _ -> None

    /// The error for a member of kind `kind` whose type, `typeOpt`, does not take what the kind passes it: a `#delay`
    /// member takes only a function of no parameters, the expression after the keyword, and a `#bindF` member takes one
    /// and then the rest of the block.
    let private keywordTypeError (span: SourceSpan) (memberName: Identifier) (kind: KeywordKind) (typeOpt: TypeExpr option) =
        let takesThunk (parameter: TypeExpr) = match parameterTypes parameter with Some [ Kind TypeUnit ] -> true | _ -> false
        let fits, expected =
            match kind, typeOpt |> Option.bind parameterTypes with
            | KeywordKind.Bind, _
            | KeywordKind.Yield, _ -> true, ""
            | KeywordKind.Delay, parameters -> (match parameters with Some [ thunk ] -> takesThunk thunk | _ -> false), "`(() -> a) -> r`"
            | KeywordKind.BindF, parameters -> (match parameters with Some [ thunk; _ ] -> takesThunk thunk | _ -> false), "`(() -> a, a -> r) -> r`"
        if fits then None
        else
            let tag = WorkflowSyntax.keywordKinds |> List.find (snd >> (=) kind) |> fst
            Some (
                Diagnostics.at DiagnosticCodes.KeywordMemberType span
                    $"'{memberName}' is a `#{tag}` keyword, which passes the expression after it as a function of no parameters, so its type must be of the form {expected}"
                |> Diagnostics.about memberName)

    /// The error for a `@customKeywordType` attribute, `name` at `span`, whose argument, `argument`, names no kind.
    let private keywordKindError (name: Identifier) (span: SourceSpan) (argument: Expression option) =
        let kinds = WorkflowSyntax.keywordKinds |> List.map (fun (kind, _) -> $"`#{kind}`") |> String.concat ", "
        match argument with
        | Some (Kind (TagExpr(kind, None))) ->
            Some (Diagnostics.at DiagnosticCodes.UnknownKeywordKind span $"The kinds are {kinds}" |> Diagnostics.about $"#{kind}")
        | _ ->
            Some (Diagnostics.at DiagnosticCodes.UnknownKeywordKind span $"Attribute '@{name}' takes the keyword's kind, as `@{name}(#bind)`: the kinds are {kinds}")

    /// The attributes written before the fields of `module'`'s record type definitions, which a type's description
    /// gives to a library (see `FieldAttribute`): each with its argument and span.
    let private typeFieldAttributes (module': Module) =
        let rec typeDefs (item: TopLevelItem) =
            match item.Kind with
            | Def (TypeDef(_, modifiers, _, body)) -> [ modifiers, body ]
            | ModuleBlock(_, items) -> items |> List.collect typeDefs
            | _ -> []
        let localTypeDefs =
            module'
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.expressionsWithin
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements |> List.choose (fun statement ->
                        match statement.Kind with
                        | TypeDefStatement(_, modifiers, _, body) -> Some (modifiers, body)
                        | _ -> None)
                | _ -> [])
        (module' |> List.collect typeDefs) @ localTypeDefs
        |> List.collect (fun (modifiers, body) ->
            match body.Kind with
            | TypeRecord fields when not (List.contains Context modifiers) ->
                fields |> List.choose (function TypeFieldAttribute(name, argument, span) -> Some (name, argument, span) | _ -> None)
            | _ -> [])

    /// The errors in the attributes of `module'`'s record types' fields. A member of a context declaration may have
    /// only `@customKeywordType`, with a kind from `WorkflowSyntax.keywordKinds` that the member's type fits. A field of
    /// a record type's definition may have any, whose argument is made of literals. No other record type's field may
    /// have one.
    let private fieldAttributeErrors (module': Module) : Diagnostic list =
        let expressions = module' |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
        let members = contextMemberAttributes module'
        let onFields = typeFieldAttributes module'
        let onMembers = (members |> List.map (fun ((_, _, span), _) -> span)) @ (onFields |> List.map (fun (_, _, span) -> span)) |> Set.ofList
        let unreadable =
            onFields
            |> List.choose (fun (name, argument, span) ->
                match TypeResolution.fieldAttributeArguments argument with
                | Some _ -> None
                | None ->
                    Some (Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument span
                              $"On a field, it takes literals: a string, a number, a bool or a tag, or a record or tuple of them, as in `@{name}(name = \"id\")`"
                          |> Diagnostics.about $"@{name}"))
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
                Diagnostics.at DiagnosticCodes.MisplacedAttribute span "It can only come before a field of a record type's definition or a member of a context declaration"
                |> Diagnostics.about $"@{name}")
        let onMember ((name: Identifier, argument: Expression option, span: SourceSpan), field: TypeRecordField) =
            match argument, field with
            | _ when name <> customKeywordTypeAttribute ->
                Some (Diagnostics.at DiagnosticCodes.UnknownAttribute span $"A context's member can have only '@{customKeywordTypeAttribute}'" |> Diagnostics.about $"@{name}")
            | KeywordKindTag kind, TypeField(memberName, _, typeOpt, _) -> keywordTypeError span memberName kind typeOpt
            | KeywordKindTag _, _ -> None
            | _ -> keywordKindError name span argument
        misplaced @ unreadable @ List.choose onMember members

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
                      $"'{name}' has no value: write `= value` after it, or mark it `@{Externs.attribute}(\"module\", \"name\")` to declare a foreign function"
                  |> Diagnostics.about name ]
            | ModuleBlock(_, blockItems) -> bodilessErrors blockItems
            | _ -> [])

    /// The def a component exports in place of `name`, an export marked `@component` whose type, `typeExpr`, requires
    /// contexts, written at `attributeSpan`, and the entry point (see `EntryPoint`) its call of the host's `provide`
    /// is: `def __component_name = { width, height -> __host.provide({ -> name(width, height) }) }`, which runs `name`
    /// under the contexts the host's component root provides, as `run` runs the program's entry point. Its parameters
    /// are named as `value`'s lambda names them, which the WIT names them by (see `ComponentInterface`). It writes no
    /// type, so that it requires `Async` and `HostRuntime`, which `provide` does, as the top level would.
    let private providedExport (host: Host) (attribute: TopLevelItem) (name: string) (typeExpr: TypeExpr) (value: Expression) =
        let count =
            match parameterTypes typeExpr with
            | Some [ Kind TypeUnit ] | None -> 0
            | Some parameters -> parameters.Length
        let names =
            match value.Kind with
            | Lambda(parameters, _, _) when parameters.Length = count && parameters |> List.forall (fun (parameter, _) -> parameter <> name) ->
                parameters |> List.map fst
            | _ -> List.init count (fun index -> $"p{index}")
        let at = Expression.at attribute.Span
        let call = at (FunctionCall(name, None, names |> List.map (IdentifierExpr >> at)))
        // The function `provide` is passed is at the attribute, so that a context it doesn't provide is told apart.
        let provide = at (FunctionCall($"{Hosts.importAlias}.{Hosts.provideFunction}", None, [ at (Lambda([], call, false)) ]))
        let wrapper = at (Lambda(names |> List.map (fun parameter -> parameter, None), provide, false))
        let def = TopLevelItem.at attribute.Span (Def (ValueDef(false, Hosts.providedExportName name, None, wrapper)))
        let entry =
            { Attribute = attribute.Id
              Name = name
              Host = host
              Argument = attribute.Span
              Call = SyntaxTree.expressionsWithin provide |> List.map _.Id }
        def, entry

    /// Resolves `@attribute` lines. `@entry` on a definition appends a call of the host's `run` with that definition
    /// at the end of the module, so it runs as the program's start function, and imports the host and the modules of
    /// the contexts it provides (see `Host`). `@extern("module", "name")` on a definition written with a type and no
    /// value makes it a foreign function (see `Externs`): its value becomes the import's module and name (see
    /// `ExternSyntax`). Any other attribute, an attribute that does not precede a value definition, or a definition with
    /// no value and no `@extern`, is an error. `@entry` takes no argument. `@component`, which takes none either, marks an
    /// exported def as one a component built from the program exports: it is left as it is, and given back by name, with
    /// where it is written; one whose written type requires contexts also gets a def that calls it under the host's,
    /// which the component exports in its place, and the host's import (see `providedExport`). `@import("interface", "name", ["parameter", ...])` on a def written with a type and no value makes
    /// it a function another component exports: its value becomes the interface, the function and the names of its
    /// parameters (see `ComponentImportSyntax`). The host is `host`: the package's, or the
    /// console host for a file in no package. A package with no host is a library, so an `@entry` in it is an error. With the module, each entry point (see `EntryPoint`) and each def marked `@component`. An attribute before
    /// a member of a context declaration is checked here too, and stays where it is (see `fieldAttributeErrors`), as is
    /// one in a block (see `statementAttributeErrors`). `@allow` is for the linters: it is checked and left out (see
    /// `withoutAllows`). `@test` marks a test, which is left as it is and given back by name, with where it is written; in
    /// a test build, `testBuild`, a module with tests also gets its `testDispatcher`, and an `@entry` is a def like any
    /// other.
    let applyAttributes (host: Host option) (testBuild: bool) (module': Module) : Result<Module * EntryPoint list * ComponentDefs, Diagnostic list> =
        // The defs marked `@component` and `@import`, each with where it is written, as `loop` finds them.
        let components = ResizeArray<string * SourceSpan>()
        let imports = ResizeArray<string * SourceSpan>()
        let provided = ResizeArray<TopLevelItem * EntryPoint>()
        let tests = ResizeArray<MarkedTest>()
        let rec loop (remaining: TopLevelItem list) (acc: TopLevelItem list) (entries: (EntryPoint * TopLevelItem) list) (externs: (string * Extern * SourceSpan) list) (errors: Diagnostic list) =
            match remaining with
            | [] -> List.rev acc, List.rev entries, List.rev externs, List.rev errors
            | Kind (Attribute(name, Some argument)) :: rest when name = testAttribute && (testArguments (Some argument)).IsNone ->
                let error =
                    Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span
                        ($"It takes no argument; `#{ioTestArgument}`, to run the test with the console host's real `Console`, `Env` and `FileSystem`; "
                         + $"`#{crashesTestArgument}`, to pass it only if it crashes, or `#{crashesTestArgument}(\"text\")`, only if it crashes with a message that "
                         + $"contains the text; or `(#{ioTestArgument}, #{crashesTestArgument})`")
                    |> Diagnostics.about $"@{name}"
                loop rest acc entries externs (error :: errors)
            | Kind (Attribute(name, argument)) as attribute :: (Kind (Def (ValueDef(_, defName, _, _))) as def) :: rest when name = testAttribute ->
                let io, crashes = testArguments argument |> Option.defaultValue (false, None)
                tests.Add({ Name = defName; Span = def.Span; Attribute = attribute.Span; Io = io; Crashes = crashes })
                loop rest (def :: acc) entries externs (List.rev (bodilessErrors [ def ]) @ errors)
            | Kind (Attribute(name, Some argument)) :: rest when name = componentAttribute ->
                let error = Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span "It takes no argument" |> Diagnostics.about $"@{name}"
                loop rest acc entries externs (error :: errors)
            | Kind (Attribute(name, None)) as attribute :: (Kind (Def (ValueDef(true, defName, typeOpt, value))) as def) :: rest when name = componentAttribute ->
                components.Add((defName, def.Span))
                match host, typeOpt with
                | Some host, Some typeExpr when ComponentInterface.writesContexts typeExpr -> provided.Add(providedExport host attribute defName typeExpr value)
                | _ -> ()
                loop rest (def :: acc) entries externs (List.rev (bodilessErrors [ def ]) @ errors)
            | Kind (Attribute(name, None)) as attribute :: (Kind (Def (ValueDef(false, defName, _, _))) as def) :: rest when name = componentAttribute ->
                let error =
                    Diagnostics.at DiagnosticCodes.MisplacedAttribute attribute.Span
                        $"A component exports only what its module exports: write `export def {defName}`"
                    |> Diagnostics.about $"@{name}"
                loop rest (def :: acc) entries externs (List.rev (bodilessErrors [ def ]) @ error :: errors)
            | Kind (Attribute(name, argument)) as attribute :: (Kind (Def (ValueDef(isExport, defName, typeOpt, value))) as def) :: rest when name = Externs.attribute ->
                match Externs.declared attribute.Span argument defName def.Span typeOpt value with
                | Ok declared ->
                    let imported = { def with Kind = Def (ValueDef(isExport, defName, typeOpt, ExternSyntax.import declared.Module declared.Name value)) }
                    loop rest (imported :: acc) entries ((defName, declared, def.Span) :: externs) errors
                | Error diagnostics -> loop rest (def :: acc) entries externs (List.rev diagnostics @ errors)
            | Kind (Attribute(name, argument)) as attribute :: (Kind (Def (ValueDef(isExport, defName, typeOpt, value))) as def) :: rest when name = componentImportAttribute ->
                match componentImport attribute.Span argument defName def.Span typeOpt value with
                | Ok (witInterface, functionName, parameters) ->
                    imports.Add((defName, def.Span))
                    let imported = { def with Kind = Def (ValueDef(isExport, defName, typeOpt, ComponentImportSyntax.import witInterface functionName parameters value)) }
                    loop rest (imported :: acc) entries externs errors
                | Error diagnostics -> loop rest (def :: acc) entries externs (List.rev diagnostics @ errors)
            // A keyword belongs to a context: a def attached to one can be a `#delay` keyword, which takes the
            // expression after the keyword as a function of no parameters. Any other kind needs a workflow's builder,
            // so only a context's member can be one, and a def attached to nothing can be no keyword.
            | Kind (Attribute(name, argument)) as attribute :: (Kind (Def (ValueDef(_, defName, typeOpt, _))) as def) :: rest when name = customKeywordTypeAttribute ->
                let error =
                    match argument, attachedToContext module' defName with
                    | KeywordKindTag _, None ->
                        Some (
                            Diagnostics.at DiagnosticCodes.UnknownKeywordKind attribute.Span
                                $"A keyword belongs to a context: '@{name}' marks a member of a context, or a def attached to a context this module declares, as `def Async.start`, not '{defName}'"
                            |> Diagnostics.about $"@{name}")
                    | KeywordKindTag KeywordKind.Delay, Some _ -> keywordTypeError attribute.Span defName KeywordKind.Delay typeOpt
                    | KeywordKindTag _, Some _ ->
                        Some (
                            Diagnostics.at DiagnosticCodes.UnknownKeywordKind attribute.Span
                                $"A def attached to a context can only be a `#delay` keyword, which rewrites nothing but itself: any other kind needs a workflow's builder, so it is a member of the builder's context"
                            |> Diagnostics.about $"@{name}")
                    | _ -> keywordKindError name attribute.Span argument
                loop rest (def :: acc) entries externs (List.rev (bodilessErrors [ def ]) @ Option.toList error @ errors)
            // `@baseUnit` and `@storageUnit` mark a dimension's default display and storage units, which the unit's
            // declaration, after them, is checked with (see `Units`).
            | attribute :: (next :: _ as rest) when UnitSyntax.isUnitAttributeDeclaration attribute next ->
                loop rest (attribute :: acc) entries externs errors
            | Kind (Attribute(name, _)) as attribute :: rest when name <> entryAttribute && name <> Externs.attribute && name <> componentAttribute && name <> componentImportAttribute && name <> testAttribute ->
                let error =
                    Diagnostics.at DiagnosticCodes.UnknownAttribute attribute.Span
                        $"A definition can have '@{entryAttribute}', '@{testAttribute}', '@{Externs.attribute}', '@{componentAttribute}', '@{componentImportAttribute}' or '@{allowAttribute}'"
                    |> Diagnostics.about $"@{name}"
                loop rest acc entries externs (error :: errors)
            | Kind (Attribute(name, Some argument)) :: rest when name = entryAttribute ->
                let error = Diagnostics.at DiagnosticCodes.UnexpectedAttributeArgument argument.Span "It takes no argument" |> Diagnostics.about $"@{name}"
                loop rest acc entries externs (error :: errors)
            // A test build runs the program's tests, from its root, rather than the program.
            | Kind (Attribute(attributeName, None)) :: (Kind (Def _) as def) :: rest when attributeName = entryAttribute && testBuild ->
                loop rest (def :: acc) entries externs (List.rev (bodilessErrors [ def ]) @ errors)
            | Kind (Attribute(attributeName, _)) as attribute :: (Kind (Def (ValueDef(_, name, _, _))) as def) :: rest when attributeName = entryAttribute && host.IsNone ->
                let error =
                    Diagnostics.at
                        DiagnosticCodes.EntryInLibrary
                        attribute.Span
                        $"'{name}' is an entry point, but its package names no host, so it is a library, which has none: to make it an application, name the host its `@entry` runs under in its manifest, as `host = (package = \"{Hosts.consolePackage}\", version = \"{Toolchain.version}\")`"
                    |> Diagnostics.about name
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
                loop rest acc entries externs (misplaced name attribute.Span :: errors)
            | item :: rest -> loop rest (item :: acc) entries externs (List.rev (bodilessErrors [ item ]) @ errors)
        let module', allowErrors = withoutAllows module'
        match loop module' [] [] [] (List.rev (fieldAttributeErrors module')) with
        | items, entries, externs, errors ->
            match allowErrors @ statementAttributeErrors module' @ errors @ Externs.conflicts externs with
            | [] ->
                // The host's prelude is imported into every module of the program, not here (see `Library`).
                let entryImport = match host with Some host when not entries.IsEmpty || provided.Count > 0 -> [ hostImport host ] | _ -> []
                let wrappers = provided |> Seq.map fst |> List.ofSeq
                let components = { Exports = List.ofSeq components; Imports = List.ofSeq imports; Provided = provided |> Seq.map snd |> List.ofSeq; Tests = List.ofSeq tests }
                let dispatcher = if testBuild && tests.Count > 0 then [ dispatcherOf (tests |> Seq.map (fun test -> test.Name, test.Span) |> List.ofSeq) ] else []
                Ok (entryImport @ items @ wrappers @ dispatcher @ List.map snd entries, List.map fst entries, components)
            | errors -> Error errors
