namespace NyxCompiler

open Parser.Program

/// Custom patterns (see the custom patterns page and `PatternSyntax`): the patterns in scope in a module, found by the
/// tags they return, and the checks on the capitalised tags the module writes.
module internal CustomPatterns =
    /// Where a pattern in scope is declared: in the module itself, at its top level or in a block, where its type is
    /// written, or in a module it imports, by the symbol of its def.
    type Origin =
        | Own of SourceSpan
        | Imported of SymbolId option

    /// A pattern in scope: the name its def is in scope by, what that name says of it, and where it is declared.
    type InScope =
        { Name: Identifier
          Shape: PatternSyntax.Shape
          Origin: Origin }

    /// A pattern declared in a block, as a local def is, with where it is in scope: from its declaration, whose body
    /// returns its tags, to the end of its block.
    type Local =
        { Region: SourceSpan
          Pattern: InScope }

    /// The patterns in scope at the top level, by the tags they return, each tag's own first; the names more than one
    /// import brings in, with the modules each comes from as `module.name`; and the patterns blocks declare, outer
    /// blocks' first, which are in scope only within their blocks (see `at`).
    type Scope =
        { ByTag: Map<Identifier, InScope list>
          AmbiguousImports: Map<Identifier, string list>
          Locals: Local list
          /// The patterns `module name =` blocks declare and don't export, by the tags they return, with the block's
          /// name: in scope only within their blocks, as `Locals`, and named by the error for a tag outside them.
          Unexported: Map<Identifier, ModuleName * InScope> }

    /// The patterns the module declares itself, as top-level defs (see `PatternSyntax`), each with whether it is
    /// exported and where its def is.
    let private declaredPatterns (items: Module) : (InScope * bool * SourceSpan) list =
        items
        |> List.choose (fun item ->
            match item.Kind with
            | Def(ValueDef(isExport, name, Some signature, _)) ->
                PatternSyntax.shapeOf name
                |> Option.map (fun shape -> { Name = name; Shape = shape; Origin = Own signature.Span }, isExport, item.Span)
            | _ -> None)

    /// The patterns the module declares itself, as top-level defs (see `PatternSyntax`).
    let private ownPatterns (items: Module) : InScope list = declaredPatterns items |> List.map (fun (pattern, _, _) -> pattern)

    /// The patterns the blocks of `items` declare, outer blocks' first and each block's in order.
    let private localPatterns (items: Module) : Local list =
        items
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.collect (fun expr ->
            match expr.Kind with
            | Block statements ->
                statements
                |> List.choose (fun statement ->
                    match statement.Kind with
                    | DefStatement(_, name, Some signature, _) ->
                        PatternSyntax.shapeOf name
                        |> Option.map (fun shape ->
                            { Region = { expr.Span with Start = statement.Span.Start }
                              Pattern = { Name = name; Shape = shape; Origin = Own signature.Span } })
                    | _ -> None)
            | _ -> [])

    /// The patterns in scope in `items`, a module whose imports gave the names in `env`, more than one import giving
    /// those of `ambiguousImports` (see `InferState.AmbiguousImports`), with the `module name =` blocks `moduleBlocks`.
    /// A pattern a block declares and doesn't export is in scope only within the block (#2961).
    let scopeOf (env: TypeEnv) (ambiguousImports: Map<string, SymbolId * string list>) (moduleBlocks: (ModuleName * SourceSpan) list) (items: Module) : Scope =
        let blockOf (_, isExport, span) = if isExport then None else InferState.blockAround moduleBlocks span
        let declared = declaredPatterns items
        let unexported =
            declared |> List.choose (fun ((pattern, _, _) as each) -> blockOf each |> Option.map (fun (block, blockSpan) -> block, blockSpan, pattern))
        let own = declared |> List.filter (blockOf >> Option.isNone) |> List.map (fun (pattern, _, _) -> pattern)
        // The names of a block's unexported patterns are in `env` too, qualified with the block's name.
        let hidden = unexported |> List.map (fun (_, _, pattern) -> pattern.Name) |> Set.ofList
        let imported =
            env.Names
            |> Map.toList
            |> List.filter (fun (name, _) -> not (hidden.Contains name))
            |> List.choose (fun (name, scheme) ->
                PatternSyntax.shapeOf name |> Option.map (fun shape -> { Name = name; Shape = shape; Origin = Imported scheme.Symbol }))
        let byTag =
            own @ imported
            |> List.collect (fun pattern -> pattern.Shape.Tags |> List.map (fun tag -> tag, pattern))
            |> List.groupBy fst
            |> List.map (fun (tag, patterns) -> tag, patterns |> List.map snd)
            |> Map.ofList
        { ByTag = byTag
          AmbiguousImports =
            ambiguousImports
            |> Map.filter (fun name _ -> (PatternSyntax.shapeOf name).IsSome)
            |> Map.map (fun _ (_, sources) -> sources)
          // A block's own patterns come before those of the blocks of functions within it.
          Locals = (unexported |> List.map (fun (_, blockSpan, pattern) -> { Region = blockSpan; Pattern = pattern })) @ localPatterns items
          Unexported =
            unexported
            |> List.collect (fun (block, _, pattern) -> pattern.Shape.Tags |> List.map (fun tag -> tag, (block, pattern)))
            |> Map.ofList }

    /// `scope` as it is at `span`: with the patterns of the blocks `span` is written in from their declarations on, which
    /// come before the others that return their tags, as a local def hides another of its name, the innermost and
    /// latest first.
    let at (scope: Scope) (span: SourceSpan) : Scope =
        scope.Locals
        |> List.filter (fun local -> SourceSpan.contains local.Region span)
        |> List.fold
            (fun scope local ->
                let byTag =
                    local.Pattern.Shape.Tags
                    |> List.fold (fun byTag tag -> byTag |> Map.add tag (local.Pattern :: (byTag |> Map.tryFind tag |> Option.defaultValue []))) scope.ByTag
                { scope with ByTag = byTag })
            scope

    /// What a tag names in `scope`: the one pattern that returns it, the several that do, or none.
    type Resolution =
        | Found of InScope
        | Ambiguous of string list
        | NoPattern

    /// The pattern that returns `tag` in `scope`. The module's own pattern wins over imported ones, as its own defs do;
    /// two imported ones are ambiguous, unless they are the same pattern by two names.
    let resolve (scope: Scope) (tag: Identifier) : Resolution =
        let candidates = scope.ByTag |> Map.tryFind tag |> Option.defaultValue []
        let own = candidates |> List.filter (fun pattern -> match pattern.Origin with Own _ -> true | Imported _ -> false)
        match own, candidates with
        | first :: _, _ -> Found first
        | [], [] -> NoPattern
        | [], _ ->
            let distinct = candidates |> List.distinctBy (fun pattern -> match pattern.Origin with Imported (Some symbol) -> Choice1Of2 symbol | _ -> Choice2Of2 pattern.Name)
            match distinct with
            | [ single ] ->
                match scope.AmbiguousImports |> Map.tryFind single.Name with
                | Some sources -> Ambiguous sources
                | None -> Found single
            | several -> Ambiguous (several |> List.map _.Name)

    /// The span of the first letter of the tag written at `span`, after its `#`.
    let private firstLetter (span: SourceSpan) =
        { span with Start = span.Start + 1; End = span.Start + 2; StartColumn = span.StartColumn + 1 }

    /// The tag `tag`, written at `span`, as a pattern's when it names none: an error, with a fix that writes it in lower
    /// case, as an ordinary tag is.
    let private unknownPattern (tag: Identifier) (span: SourceSpan) =
        let lowered = string (System.Char.ToLowerInvariant tag[0]) + tag.Substring 1
        { (Diagnostics.at
               DiagnosticCodes.UnknownPattern
               span
               $"No pattern in scope returns `#{tag}`, and a capitalised tag is a custom pattern's: write an ordinary tag in lower case, as `#{lowered}`"
           |> Diagnostics.about $"#{tag}") with
            Fixes = [ { Title = $"Write `#{lowered}`"; Edits = [ { Span = firstLetter span; NewText = string lowered[0] } ] } ] }

    /// The tag `tag`, written at `span`, outside the `module name =` block `block`, which declares `pattern`, the
    /// pattern that returns it, and doesn't export it.
    let private unexportedPattern (tag: Identifier) (span: SourceSpan) (block: ModuleName) (pattern: InScope) =
        { (Diagnostics.at
               DiagnosticCodes.UnknownPattern
               span
               $"`#{tag}` is a pattern of module block '{block}', which doesn't export it: write `export pattern` there to use it here"
           |> Diagnostics.about $"#{tag}") with
            Related = [ match pattern.Origin with Own declared -> declared, "declared here" | Imported _ -> () ] }

    let private ambiguousPattern (tag: Identifier) (span: SourceSpan) (sources: string list) =
        Diagnostics.at
            DiagnosticCodes.AmbiguousPattern
            span
            $"More than one pattern in scope returns `#{tag}`, from {Diagnostics.listed sources}, so a match can't tell which to run"
        |> Diagnostics.about $"#{tag}"

    /// Each capitalised tag written in `items`, with where it is written: in a value, a pattern or a type, but not in a
    /// pattern's own type, which declares its tags, as `locals`' are written within theirs.
    let private patternTags (locals: Local list) (items: Module) : (Identifier * SourceSpan) list =
        let inLocalDeclaration span =
            locals |> List.exists (fun local -> match local.Pattern.Origin with Own declared -> SourceSpan.contains declared span | Imported _ -> false)
        let rec ofItem (item: TopLevelItem) =
            let ownTypes =
                match item.Kind with
                | Def(ValueDef(_, name, Some _, _)) when (PatternSyntax.shapeOf name).IsSome -> []
                | _ -> SyntaxTree.topLevelTypes item
            let expressions = SyntaxTree.ofItem item |> List.collect SyntaxTree.expressionsWithin
            let inExpressions =
                expressions
                |> List.collect (fun expr ->
                    let arms =
                        match expr.Kind with
                        | Match(_, arms)
                        | Except(_, arms) -> arms |> List.collect fst |> List.collect Pattern.within
                        | _ -> []
                    let inPatterns =
                        arms
                        |> List.choose (fun pattern ->
                            match pattern.Kind with
                            | TagPattern(tag, _)
                            | ArgumentTagPattern(tag, _, _) -> Some (tag, pattern.Span)
                            | _ -> None)
                    let inValue =
                        match expr.Kind with
                        | TagExpr(tag, _) -> [ tag, expr.Span ]
                        | _ -> []
                    inValue @ inPatterns @ (SyntaxTree.expressionTypes expr |> List.collect typeTags))
            (ownTypes |> List.collect typeTags) @ inExpressions
        and typeTags (typeExpr: TypeExpr) =
            TypeExpr.within typeExpr |> List.choose (function { Kind = TypeTag(tag, _); Span = span } -> Some (tag, span) | _ -> None)
        items |> List.collect ofItem |> List.filter (fun (tag, span) -> PatternSyntax.isPatternTag tag && not (inLocalDeclaration span))

    /// Each of the module's own patterns that returns a tag one declared before it returns.
    let private clashingDeclarations (items: Module) : Diagnostic list =
        ownPatterns items
        |> List.fold (fun (seen: Map<Identifier, InScope>, errors) pattern ->
            let clash = pattern.Shape.Tags |> List.tryFind seen.ContainsKey
            let span = match pattern.Origin with Own span -> span | Imported _ -> SourceSpan.none
            let errors =
                match clash with
                | Some tag ->
                    errors @ [ Diagnostics.at DiagnosticCodes.AmbiguousPattern span $"Another pattern in this module returns `#{tag}` too, so a match couldn't tell which to run" |> Diagnostics.about $"#{tag}" ]
                | None -> errors
            (pattern.Shape.Tags |> List.fold (fun acc tag -> if acc |> Map.containsKey tag then acc else acc |> Map.add tag pattern) seen), errors) (Map.empty, [])
        |> snd

    /// The errors in the capitalised tags `items` writes: each that names no pattern in `scope`, or more than one, and
    /// each of the module's own patterns that returns a tag another of them returns.
    let check (scope: Scope) (items: Module) : Diagnostic list =
        let atUses =
            patternTags scope.Locals items
            |> List.distinct
            |> List.choose (fun (tag, span) ->
                match resolve (at scope span) tag with
                | Found _ -> None
                | NoPattern ->
                    match scope.Unexported |> Map.tryFind tag with
                    | Some (block, pattern) -> Some (unexportedPattern tag span block pattern)
                    | None -> Some (unknownPattern tag span)
                | Ambiguous sources -> Some (ambiguousPattern tag span sources))
        clashingDeclarations items @ atUses

    /// What a column of a match on patterns' results stands for: a pattern, the value it runs on, by position, and its
    /// arguments. Arguments the compiler can see are the same, literals or names, are one column; any others are a column
    /// of their own, by their node.
    type private Column =
        { Position: int
          PatternName: Identifier
          Arguments: Choice<Expression, NodeId> option }

    /// A custom pattern's tag at the top of an arm's pattern for one of a match's values: the pattern, its arguments if
    /// it takes any, and the pattern the match on what it returns has in its place.
    type private Use =
        { Pattern: InScope
          Arguments: Expression option
          Result: Pattern }

    /// Whether `arguments` are literals and names, so that the compiler can see two uses are given the same ones.
    let rec private isPlain (arguments: Expression) =
        match arguments.Kind with
        | LiteralExpr _
        | IdentifierExpr _
        | UnitExpr -> true
        | TupleExpr items -> items |> List.forall isPlain
        | _ -> false

    /// `pattern`, the brackets after a tag that the parser read as a payload, as the arguments it stands for when the tag
    /// is a pattern's that takes them: `#DivisibleBy(3)`, `#StartsWith(prefix)`. None if it is no value.
    let rec private asArguments (pattern: Pattern) : Expression option =
        let at kind = Some (Expression.at pattern.Span kind)
        match pattern.Kind with
        | LiteralPattern literal -> at (LiteralExpr literal)
        | IdentifierPattern name -> at (IdentifierExpr name)
        | UnitPattern -> at UnitExpr
        | TuplePattern(items, false) ->
            let values = items |> List.map asArguments
            if values |> List.forall Option.isSome then at (TupleExpr(values |> List.choose id)) else None
        | TagPattern(tag, None) -> at (TagExpr(tag, None))
        | TagPattern(tag, Some payload) -> asArguments payload |> Option.bind (fun value -> at (TagExpr(tag, Some value)))
        | ListPattern(items, None) ->
            let values = items |> List.map asArguments
            if values |> List.forall Option.isSome then at (ListExpr(values |> List.choose id)) else None
        | _ -> None

    /// The error for the pattern tag `tag`, written at `span`, given arguments wrongly: `message` says how.
    let private wrongArguments (span: SourceSpan) (message: string) =
        Diagnostics.at DiagnosticCodes.PatternArguments span message

    /// The first part of `arguments`, a pattern's arguments written as an expression, that is not one of the kinds an
    /// argument may be: a literal, a name or a qualified name, or a tuple, list or tag of those (#2064). Anything else, as
    /// a call, is bound with a `def` first: in a pattern it would read as a payload, and run each time the arm is tried.
    let rec private firstDisallowed (arguments: Expression) : Expression option =
        match arguments.Kind with
        | LiteralExpr _
        | IdentifierExpr _
        | UnitExpr
        | TagExpr(_, None) -> None
        | MemberAccess({ Kind = IdentifierExpr _ | MemberAccess _ } as qualifier, _, _) -> firstDisallowed qualifier
        | TagExpr(_, Some payload) -> firstDisallowed payload
        | TupleExpr items
        | ListExpr items -> items |> List.tryPick firstDisallowed
        | _ -> Some arguments

    /// The name a fix binds an argument of the pattern tag `tag` to: `matchesRegexArgument` for `#MatchesRegex`.
    let private argumentName (tag: Identifier) =
        let own = Names.ownPart tag
        string (System.Char.ToLowerInvariant own[0]) + own.Substring 1 + "Argument"

    /// The error for `value`, an argument of the pattern tag `tag` of a kind an argument can't be (see
    /// `firstDisallowed`), with the fixes `bind` gives for binding it to a name first.
    let private notAnArgument (bind: Identifier -> Expression -> Fix list) (tag: Identifier) (value: Expression) =
        { wrongArguments
              value.Span
              $"A pattern's arguments are literals, names, and tuples, lists and tags of them: bind this value with a `def` before the `match`, and give `#{tag}` its name" with
            Fixes = bind (argumentName tag) value }

    /// The use `part`, a pattern at the top of an arm's pattern for one value or one part of an and-pattern there, is,
    /// if it is a pattern's tag that the match is rewritten for; or the error in how it is given arguments.
    let private useOf (bind: Identifier -> Expression -> Fix list) (scope: Scope) (part: Pattern) : Result<Use, Diagnostic> option =
        let found tag = match resolve scope tag with Found pattern -> Some pattern | _ -> None
        // A pattern that returns a `bool` returns `true` for its tag (see `lowerDeclaration`). A use that leaves out the
        // payload of a tag that carries one matches the tag whatever its payload is.
        let result (pattern: InScope) tag payload =
            if pattern.Shape.ReturnsBool then
                Pattern.at part.Span (LiteralPattern(BoolLit true))
            else
                let payload =
                    match payload with
                    | None when List.contains tag pattern.Shape.WithPayload -> Some (Pattern.at part.Span WildcardPattern)
                    | written -> written
                { part with Kind = TagPattern(tag, payload) }
        match part.Kind with
        | TagPattern(tag, payload) when PatternSyntax.isPatternTag tag ->
            found tag
            |> Option.map (fun pattern ->
                match pattern.Shape.TakesArguments, payload with
                | false, _ -> Ok { Pattern = pattern; Arguments = None; Result = result pattern tag payload }
                | true, None -> Error (wrongArguments part.Span $"`#{tag}` is a pattern that takes arguments: write them in brackets after it, as in `#{tag}(...)`")
                | true, Some written ->
                    match asArguments written with
                    | Some arguments -> Ok { Pattern = pattern; Arguments = Some arguments; Result = result pattern tag None }
                    | None -> Error (wrongArguments written.Span $"`#{tag}` takes arguments, which are values, not patterns: its first brackets are its arguments, and the payload to take apart goes in a second pair"))
        | ArgumentTagPattern(tag, arguments, payload) when PatternSyntax.isPatternTag tag ->
            found tag
            |> Option.map (fun pattern ->
                if not pattern.Shape.TakesArguments then
                    Error (wrongArguments arguments.Span $"`#{tag}` is a pattern that takes no arguments, so it has only the brackets of its payload")
                else
                    match firstDisallowed arguments with
                    | Some value -> Error (notAnArgument bind tag value)
                    | None -> Ok { Pattern = pattern; Arguments = Some arguments; Result = result pattern tag payload })
        | _ -> None

    /// `pattern`, an arm's pattern for one value, split into what is matched against the value itself, which is `_` if
    /// nothing is, and the patterns' tags it uses; and the errors in those that are given arguments wrongly, which are
    /// matched as `_`.
    let private split (bind: Identifier -> Expression -> Fix list) (scope: Scope) (pattern: Pattern) : (Pattern * Use list) * Diagnostic list =
        let parts = match pattern.Kind with AndPattern parts -> parts | _ -> [ pattern ]
        let used = parts |> List.map (fun part -> part, useOf bind scope part)
        let uses = used |> List.choose (function _, Some (Ok used) -> Some used | _ -> None)
        let errors = used |> List.choose (function _, Some (Error error) -> Some error | _ -> None)
        let rest = used |> List.filter (snd >> Option.isNone) |> List.map fst
        let remaining =
            match rest with
            | [] -> Pattern.at pattern.Span WildcardPattern
            | [ _ ] when uses.IsEmpty && errors.IsEmpty -> pattern
            | [ single ] -> single
            | several -> { pattern with Kind = AndPattern several }
        (remaining, uses), errors

    /// The column of a match on patterns' results that `used`, at `position`, is matched in.
    let private columnOf (position: int) (used: Use) : Column =
        { Position = position
          PatternName = used.Pattern.Name
          Arguments = used.Arguments |> Option.map (fun arguments -> if isPlain arguments then Choice1Of2 arguments else Choice2Of2 arguments.Id) }

    /// The match `matchExpr` on `scrutinees` with `arms` as a block that runs each pattern its arms use on the value it
    /// is used for, once, and matches on what the patterns return as more values, if its arms use any:
    ///
    ///   match n            def __patternResult_7_0 = __pattern__4Even3Odd(n)
    ///     | #Even -> a     match n, __patternResult_7_0
    ///     | 0 -> b    =>     | _, #Even -> a
    ///     | _ -> c           | 0, _ -> b
    ///                        | _, _ -> c
    ///
    /// A value that is not a name is bound first, so that it is worked out once. A pattern with arguments is given them
    /// first, and what it gives back runs on the value: `__pattern_A_...(3)(n)`. Patterns are pure, so running each one
    /// before the arms are tried can't be told from running it when an arm first needs it. Also the errors in the uses
    /// given arguments wrongly.
    let private rewriteMatch (bind: Identifier -> Expression -> Fix list) (scope: Scope) (matchExpr: Expression) (scrutinees: Expression list) (arms: MatchArm list) : ExpressionKind option * Diagnostic list =
        let splitArms, errors =
            arms
            |> List.map (fun (patterns, body) ->
                let split = patterns |> List.map (split bind scope)
                (split |> List.map fst, body), split |> List.collect snd)
            |> List.unzip
        let errors = List.concat errors
        // Each pattern used, on each value, with each of its arguments, is run once, in the order the arms first use them.
        let columns =
            splitArms
            |> List.collect (fun (patterns, _) -> patterns |> List.indexed |> List.collect (fun (position, (_, uses)) -> uses |> List.map (columnOf position)))
            |> List.distinct
        let countsAgree = arms |> List.forall (fun (patterns, _) -> patterns.Length = scrutinees.Length)
        if not countsAgree then
            None, errors
        // Only uses given arguments wrongly, which are reported, and matched as `_` so that nothing more is.
        elif columns.IsEmpty then
            let arms' = splitArms |> List.map (fun (patterns, body) -> patterns |> List.map fst, body)
            (if errors.IsEmpty then None else Some (Match(scrutinees, arms'))), errors
        else
            let made kind = Expression.from matchExpr kind
            let positions = columns |> List.map _.Position |> List.distinct
            // The values the patterns run on, each by a name.
            let subjects =
                positions
                |> List.map (fun position ->
                    let scrutinee = scrutinees[position]
                    match scrutinee.Kind with
                    | IdentifierExpr name -> position, (name, [])
                    | _ ->
                        let name = PatternSyntax.subjectName matchExpr.Id position
                        position, (name, [ Statement.from scrutinee (DefStatement(false, name, None, scrutinee)) ]))
                |> Map.ofList
            let scrutinees' =
                scrutinees |> List.mapi (fun position scrutinee ->
                    match subjects |> Map.tryFind position with
                    | Some (name, _ :: _) -> Expression.from scrutinee (IdentifierExpr name)
                    | _ -> scrutinee)
            let argumentsOf (column: Column) =
                splitArms
                |> List.tryPick (fun (patterns, _) ->
                    snd patterns[column.Position] |> List.tryFind (fun used -> columnOf column.Position used = column))
                |> Option.bind _.Arguments
            let results =
                columns
                |> List.mapi (fun index column ->
                    let subject, _ = subjects[column.Position]
                    let scrutinee = scrutinees[column.Position]
                    let made kind = Expression.from scrutinee kind
                    let name = PatternSyntax.resultName matchExpr.Id index
                    let call =
                        match argumentsOf column with
                        | None -> made (FunctionCall(column.PatternName, None, [ made (IdentifierExpr subject) ]))
                        | Some arguments ->
                            // As `f(arguments)(subject)` is written (see `CallResultSyntax`).
                            let given = Expression.from arguments (FunctionCall(column.PatternName, None, SyntaxTree.flattenCallArgs [ arguments ]))
                            made (Block [ Statement.from arguments (DefStatement(false, CallResultSyntax.name, None, given))
                                          Statement.from scrutinee (ExprStatement(made (FunctionCall(CallResultSyntax.name, None, [ made (IdentifierExpr subject) ])))) ])
                    Statement.from scrutinee (DefStatement(false, name, None, call)), made (IdentifierExpr name))
            let arms' =
                splitArms
                |> List.map (fun (patterns, body) ->
                    let resultPatterns =
                        columns
                        |> List.map (fun column ->
                            let _, uses = patterns[column.Position]
                            match uses |> List.tryFind (fun used -> columnOf column.Position used = column) with
                            | Some used -> used.Result
                            | None -> Pattern.at SourceSpan.none WildcardPattern)
                    (patterns |> List.map fst) @ resultPatterns, body)
            let bindings = (subjects |> Map.toList |> List.collect (snd >> snd)) @ (results |> List.map fst)
            let rewritten = made (Match(scrutinees' @ (results |> List.map snd), arms'))
            Some (Block(bindings @ [ Statement.from matchExpr (ExprStatement rewritten) ])), errors
    /// The pattern tags of `pattern`, an arm's pattern for one value, that are inside another pattern, where a match
    /// can't use them, each with where it is written.
    let private nestedUses (scope: Scope) (pattern: Pattern) : (Identifier * SourceSpan) list =
        let isPattern tag = PatternSyntax.isPatternTag tag && (match resolve scope tag with Found _ -> true | _ -> false)
        let parts = match pattern.Kind with AndPattern parts -> parts | _ -> [ pattern ]
        parts
        |> List.collect (fun part ->
            let inner =
                match part.Kind with
                | TagPattern(tag, payload)
                | ArgumentTagPattern(tag, _, payload) when isPattern tag -> Option.toList payload
                | _ -> [ part ]
            inner
            |> List.collect Pattern.within
            |> List.choose (fun within ->
                match within.Kind with
                | TagPattern(tag, _)
                | ArgumentTagPattern(tag, _, _) when isPattern tag -> Some (tag, within.Span)
                | _ -> None))

    let private nestedPattern (tag: Identifier, span: SourceSpan) =
        Diagnostics.at
            DiagnosticCodes.PatternInsidePattern
            span
            $"`#{tag}` is a custom pattern, which a match runs on one of the values it matches, so it goes at the top of an arm's pattern for a value, or of an and-pattern there, not inside another pattern"

    /// The def of a pattern the module declares, `written` its type and `body` its body, as one the type checker can
    /// check: the pattern's function. A partial pattern returns `PatternSyntax.noMatchTag` for `_`, among its tags and
    /// in its body, and one that returns a `bool` returns `true` for its tag and `false` for `_`.
    let private lowerDeclaration (shape: PatternSyntax.Shape) (written: TypeExpr) (body: Expression) : TypeExpr * Expression =
        let lowerResults (results: TypeExpr) =
            if shape.ReturnsBool then
                { results with Kind = TypeName "bool" }
            else
                results |> TypeExpr.mapChildren (fun item -> match item.Kind with TypeHole -> { item with Kind = TypeTag(PatternSyntax.noMatchTag, None) } | _ -> item)
        let loweredType =
            match written.Kind with
            | TypeFunc(arguments, ({ Kind = TypeFunc(input, results) } as inner)) when shape.TakesArguments ->
                { written with Kind = TypeFunc(arguments, { inner with Kind = TypeFunc(input, lowerResults results) }) }
            | TypeFunc(input, results) -> { written with Kind = TypeFunc(input, lowerResults results) }
            | _ -> written
        let loweredBody =
            body
            |> ExpressionRewrites.mapExpressionKinds (fun _ kind ->
                match kind with
                | IdentifierExpr name when name = PatternSyntax.noMatch ->
                    if shape.ReturnsBool then LiteralExpr(BoolLit false) else TagExpr(PatternSyntax.noMatchTag, None)
                | TagExpr(tag, None) when shape.ReturnsBool && List.contains tag shape.Tags -> LiteralExpr(BoolLit true)
                | other -> other)
        loweredType, loweredBody

    /// The error for `_` written as a value outside a pattern's body, where it means nothing.
    let private noMatchOutsidePattern (span: SourceSpan) =
        Diagnostics.at
            DiagnosticCodes.NoMatchOutsidePattern
            span
            "`_` as a value means \"no match\", which only a custom pattern's body can return"

    /// `items` with each match whose arms use a pattern's tags rewritten to run the patterns in scope at it (see
    /// `rewriteMatch` and `at`), and each pattern the module declares, at its top level or in a block, as the function it
    /// is (see `lowerDeclaration`); and the errors for pattern tags
    /// written where a match can't use them, and for `_` as a value outside a pattern's body (see `withoutUnknownNoMatch`).
    let rewrite (source: string) (scope: Scope) (items: Module) : Module * Diagnostic list =
        let errors = ResizeArray<Diagnostic>()
        // The fix that binds `value`, an argument a pattern can't be given, to `name` with a `def` on a line of its own
        // before the match `matchExpr`, and gives the pattern the name: offered where the match starts its line, so that
        // the def goes in the same block.
        let bindBefore (matchExpr: Expression) (name: Identifier) (value: Expression) : Fix list =
            let span = matchExpr.Span
            let valid (s: SourceSpan) = not (SourceSpan.isNone s) && s.End <= source.Length
            if not (valid span && valid value.Span) then []
            else
                let lineStart = if span.Start = 0 then 0 else source.LastIndexOf('\n', span.Start - 1) + 1
                let indent = source.Substring(lineStart, span.Start - lineStart)
                if indent |> Seq.exists (System.Char.IsWhiteSpace >> not) then []
                else
                    let text = source.Substring(value.Span.Start, value.Span.End - value.Span.Start)
                    let lineSpan = { span with Start = lineStart; End = lineStart; StartColumn = 0 }
                    [ { Title = $"Bind it with `def {name}`"
                        Edits = [ { Span = lineSpan; NewText = $"{indent}def {name} = {text}\n" }; { Span = value.Span; NewText = name } ] } ]
        let visit (current: Expression) (kind: ExpressionKind) =
            match kind with
            | Match(scrutinees, arms) ->
                let scope = at scope current.Span
                arms |> List.collect fst |> List.collect (nestedUses scope) |> List.map nestedPattern |> errors.AddRange
                let rewritten, argumentErrors = rewriteMatch (bindBefore current) scope current scrutinees arms
                errors.AddRange argumentErrors
                rewritten |> Option.defaultValue kind
            | IdentifierExpr name when name = PatternSyntax.noMatch ->
                errors.Add (noMatchOutsidePattern current.Span)
                kind
            | other -> other
        // A block's patterns are lowered as the top level's are, before the `_` in their bodies is visited.
        let lowerLocal =
            ExpressionRewrites.mapExpressionKinds (fun _ kind ->
                match kind with
                | Block statements ->
                    statements
                    |> List.map (fun statement ->
                        match statement.Kind with
                        | DefStatement(isExport, name, Some written, value) when (PatternSyntax.shapeOf name).IsSome ->
                            let loweredType, loweredBody = lowerDeclaration (PatternSyntax.shapeOf name).Value written value
                            { statement with Kind = DefStatement(isExport, name, Some loweredType, loweredBody) }
                        | _ -> statement)
                    |> Block
                | other -> other)
        let inValue value = ExpressionRewrites.mapExpressionKinds visit (lowerLocal value)
        let rec inItem (item: TopLevelItem) =
            match item.Kind with
            | Def(ValueDef(isExport, name, Some written, value)) when (PatternSyntax.shapeOf name).IsSome ->
                let loweredType, loweredBody = lowerDeclaration (PatternSyntax.shapeOf name).Value written value
                { item with Kind = Def(ValueDef(isExport, name, Some loweredType, inValue loweredBody)) }
            | Def(ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def(ValueDef(isExport, name, typeOpt, inValue value)) }
            | Expr value -> { item with Kind = Expr(inValue value) }
            | ModuleBlock(name, blockItems) -> { item with Kind = ModuleBlock(name, List.map inItem blockItems) }
            | Def(TypeDef _)
            | ModuleDecl _
            | Import _
            | ReExport _
            | Attribute _
            | DimensionDecl _
            | UnitDecl _ -> item
        let rewritten = List.map inItem items
        rewritten, List.ofSeq errors
    /// `inferErrors`, the type checker's errors, without its unknown name for each `_` that `patternErrors` reports
    /// as a value outside a pattern's body: that error says what `_` is.
    let withoutUnknownNoMatch (patternErrors: Diagnostic list) (inferErrors: Diagnostic list) : Diagnostic list =
        let reported =
            patternErrors
            |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.NoMatchOutsidePattern.Code)
            |> List.choose _.Span
            |> Set.ofList
        inferErrors |> List.filter (fun diagnostic -> diagnostic.Code <> DiagnosticCodes.UnknownIdentifier.Code || not (diagnostic.Span |> Option.exists reported.Contains))