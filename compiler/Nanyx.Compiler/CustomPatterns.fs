namespace NyxCompiler

open Parser.Program

/// Custom patterns (see the custom patterns page and `PatternSyntax`): the patterns in scope in a module, found by the
/// tags they return, and the checks on the capitalised tags the module writes.
module internal CustomPatterns =
    /// Where a pattern in scope is declared: in the module itself, where its type is written, or in a module it
    /// imports, by the symbol of its def.
    type Origin =
        | Own of SourceSpan
        | Imported of SymbolId option

    /// A pattern in scope: the name its def is in scope by, what that name says of it, and where it is declared.
    type InScope =
        { Name: Identifier
          Shape: PatternSyntax.Shape
          Origin: Origin }

    /// The patterns in scope, by the tags they return, each tag's own first, and the names more than one import
    /// brings in, with the modules each comes from as `module.name`.
    type Scope =
        { ByTag: Map<Identifier, InScope list>
          AmbiguousImports: Map<Identifier, string list> }

    /// The patterns the module declares itself, as top-level defs (see `PatternSyntax`).
    let private ownPatterns (items: Module) : InScope list =
        items
        |> List.choose (fun item ->
            match item.Kind with
            | Def(ValueDef(_, name, Some signature, _)) ->
                PatternSyntax.shapeOf name |> Option.map (fun shape -> { Name = name; Shape = shape; Origin = Own signature.Span })
            | _ -> None)

    /// The patterns in scope in `items`, a module whose imports gave the names in `env`, more than one import giving
    /// those of `ambiguousImports` (see `InferState.AmbiguousImports`).
    let scopeOf (env: TypeEnv) (ambiguousImports: Map<string, SymbolId * string list>) (items: Module) : Scope =
        let imported =
            env.Names
            |> Map.toList
            |> List.choose (fun (name, scheme) ->
                PatternSyntax.shapeOf name |> Option.map (fun shape -> { Name = name; Shape = shape; Origin = Imported scheme.Symbol }))
        let byTag =
            ownPatterns items @ imported
            |> List.collect (fun pattern -> pattern.Shape.Tags |> List.map (fun tag -> tag, pattern))
            |> List.groupBy fst
            |> List.map (fun (tag, patterns) -> tag, patterns |> List.map snd)
            |> Map.ofList
        { ByTag = byTag
          AmbiguousImports =
            ambiguousImports
            |> Map.filter (fun name _ -> (PatternSyntax.shapeOf name).IsSome)
            |> Map.map (fun _ (_, sources) -> sources) }

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
        { Diagnostics.at
            DiagnosticCodes.UnknownPattern
            span
            $"No pattern in scope returns `#{tag}`. A capitalised tag is a custom pattern's: an ordinary tag is written in lower case, as `#{lowered}`" with
            Fixes = [ { Title = $"Write `#{lowered}`"; Edits = [ { Span = firstLetter span; NewText = string lowered[0] } ] } ] }

    let private ambiguousPattern (tag: Identifier) (span: SourceSpan) (sources: string list) =
        Diagnostics.at
            DiagnosticCodes.AmbiguousPattern
            span
            $"More than one pattern in scope returns `#{tag}`, so a match can't tell which to run. They come from {Diagnostics.listed sources}"

    /// Each capitalised tag written in `items`, with where it is written: in a value, a pattern or a type, but not in a
    /// pattern's own type, which declares its tags.
    let private patternTags (items: Module) : (Identifier * SourceSpan) list =
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
        items |> List.collect ofItem |> List.filter (fst >> PatternSyntax.isPatternTag)

    /// Each of the module's own patterns that returns a tag one declared before it returns.
    let private clashingDeclarations (items: Module) : Diagnostic list =
        ownPatterns items
        |> List.fold (fun (seen: Map<Identifier, InScope>, errors) pattern ->
            let clash = pattern.Shape.Tags |> List.tryFind seen.ContainsKey
            let span = match pattern.Origin with Own span -> span | Imported _ -> SourceSpan.none
            let errors =
                match clash with
                | Some tag ->
                    errors @ [ Diagnostics.at DiagnosticCodes.AmbiguousPattern span $"Another pattern in this module returns `#{tag}` too, so a match couldn't tell which to run" ]
                | None -> errors
            (pattern.Shape.Tags |> List.fold (fun acc tag -> if acc |> Map.containsKey tag then acc else acc |> Map.add tag pattern) seen), errors) (Map.empty, [])
        |> snd

    /// The errors in the capitalised tags `items` writes: each that names no pattern in `scope`, or more than one, and
    /// each of the module's own patterns that returns a tag another of them returns.
    let check (scope: Scope) (items: Module) : Diagnostic list =
        let atUses =
            patternTags items
            |> List.distinct
            |> List.choose (fun (tag, span) ->
                match resolve scope tag with
                | Found _ -> None
                | NoPattern -> Some (unknownPattern tag span)
                | Ambiguous sources -> Some (ambiguousPattern tag span sources))
        clashingDeclarations items @ atUses

    /// A custom pattern's tag at the top of an arm's pattern for one of a match's values: the pattern, and the
    /// pattern the match on what it returns has in its place.
    type private Use =
        { Pattern: InScope
          Result: Pattern }

    /// The use `part`, a pattern at the top of an arm's pattern for one value or one part of an and-pattern there, is,
    /// if it is a pattern's tag that the match is rewritten for.
    let private useOf (scope: Scope) (part: Pattern) : Use option =
        match part.Kind with
        | TagPattern(tag, _) when PatternSyntax.isPatternTag tag ->
            match resolve scope tag with
            | Found pattern when not pattern.Shape.TakesArguments && not pattern.Shape.IsPartial -> Some { Pattern = pattern; Result = part }
            | _ -> None
        | _ -> None

    /// `pattern`, an arm's pattern for one value, split into what is matched against the value itself, which is `_` if
    /// nothing is, and the patterns' tags it uses.
    let private split (scope: Scope) (pattern: Pattern) : Pattern * Use list =
        let parts = match pattern.Kind with AndPattern parts -> parts | _ -> [ pattern ]
        let used = parts |> List.map (fun part -> part, useOf scope part)
        let uses = used |> List.choose snd
        let rest = used |> List.filter (snd >> Option.isNone) |> List.map fst
        let remaining =
            match rest with
            | [] -> Pattern.at pattern.Span WildcardPattern
            | [ _ ] when uses.IsEmpty -> pattern
            | [ single ] -> single
            | several -> { pattern with Kind = AndPattern several }
        remaining, uses

    /// The match `matchExpr` on `scrutinees` with `arms` as a block that runs each pattern its arms use on the value it
    /// is used for, once, and matches on what the patterns return as more values, if its arms use any:
    ///
    ///   match n            def __patternResult_7_0 = __pattern__4Even3Odd(n)
    ///     | #Even -> a     match n, __patternResult_7_0
    ///     | 0 -> b    =>     | _, #Even -> a
    ///     | _ -> c           | 0, _ -> b
    ///                        | _, _ -> c
    ///
    /// A value that is not a name is bound first, so that it is worked out once. Patterns are pure, so running each
    /// one before the arms are tried can't be told from running it when an arm first needs it.
    let private rewriteMatch (scope: Scope) (matchExpr: Expression) (scrutinees: Expression list) (arms: MatchArm list) : ExpressionKind option =
        let splitArms =
            arms |> List.map (fun (patterns, body) -> patterns |> List.map (split scope), body)
        // Each pattern used, on each value, is run once, in the order the arms first use them.
        let columns =
            splitArms
            |> List.collect (fun (patterns, _) -> patterns |> List.indexed |> List.collect (fun (position, (_, uses)) -> uses |> List.map (fun used -> position, used.Pattern.Name)))
            |> List.distinct
        let countsAgree = arms |> List.forall (fun (patterns, _) -> patterns.Length = scrutinees.Length)
        if columns.IsEmpty || not countsAgree then
            None
        else
            let made kind = Expression.from matchExpr kind
            let positions = columns |> List.map fst |> List.distinct
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
            let results =
                columns
                |> List.mapi (fun column (position, patternName) ->
                    let subject, _ = subjects[position]
                    let scrutinee = scrutinees[position]
                    let name = PatternSyntax.resultName matchExpr.Id column
                    let call = Expression.from scrutinee (FunctionCall(patternName, None, [ Expression.from scrutinee (IdentifierExpr subject) ]))
                    Statement.from scrutinee (DefStatement(false, name, None, call)), Expression.from scrutinee (IdentifierExpr name))
            let arms' =
                splitArms
                |> List.map (fun (patterns, body) ->
                    let resultPatterns =
                        columns
                        |> List.map (fun (position, patternName) ->
                            let _, uses = patterns[position]
                            match uses |> List.tryFind (fun used -> used.Pattern.Name = patternName) with
                            | Some used -> used.Result
                            | None -> Pattern.at SourceSpan.none WildcardPattern)
                    (patterns |> List.map fst) @ resultPatterns, body)
            let bindings = (subjects |> Map.toList |> List.collect (snd >> snd)) @ (results |> List.map fst)
            let rewritten = made (Match(scrutinees' @ (results |> List.map snd), arms'))
            Some (Block(bindings @ [ Statement.from matchExpr (ExprStatement rewritten) ]))

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

    /// `items` with each match whose arms use a pattern's tags rewritten to run the patterns (see `rewriteMatch`), and
    /// the errors for pattern tags written where a match can't use them.
    let rewrite (scope: Scope) (items: Module) : Module * Diagnostic list =
        let errors = ResizeArray<Diagnostic>()
        let visit (current: Expression) (kind: ExpressionKind) =
            match kind with
            | Match(scrutinees, arms) ->
                arms |> List.collect fst |> List.collect (nestedUses scope) |> List.map nestedPattern |> errors.AddRange
                rewriteMatch scope current scrutinees arms |> Option.defaultValue kind
            | other -> other
        let inValue = ExpressionRewrites.mapExpressionKinds visit
        let rec inItem (item: TopLevelItem) =
            match item.Kind with
            | Def(ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def(ValueDef(isExport, name, typeOpt, inValue value)) }
            | Expr value -> { item with Kind = Expr(inValue value) }
            | ModuleBlock(name, blockItems) -> { item with Kind = ModuleBlock(name, List.map inItem blockItems) }
            | Def(TypeDef _)
            | ModuleDecl _
            | Import _
            | ReExport _
            | Attribute _ -> item
        let rewritten = List.map inItem items
        rewritten, List.ofSeq errors