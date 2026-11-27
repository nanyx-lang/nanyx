namespace NyxCompiler

open Parser.Program

/// The official linter's `repeatedPulls` rule: a sequence whose steps require contexts, `[FileSystem] seq(string)`, that
/// is pulled more than once on some path through a def (#2424). A sequence is a recipe, so pulling it again runs its
/// steps again: it prints again, reads the file again, and may give other elements. A pure sequence, or one whose
/// contexts are only a variable (`[c] seq(a)`, which may stand for none), is left alone.
///
/// What pulls is deduced from types, not written: a `for` loop over the sequence, or passing it to a function that takes
/// a `seq`, or anything `Iterable`, and whose result is not a `seq` (`seq.toList`, `seq.length`). A function whose
/// result is a `seq` (`seq.filter`, `seq.map`) makes a new sequence from it, and pulling that pulls the one it was made
/// from, as a `for` whose body `yield`s does. Passing it anywhere else does neither. Pulls in different arms of an `if`
/// or a `match` are on different paths, and one inside a loop counts as many. One inside a lambda other than a loop's
/// counts once, where the lambda is written, since how often it runs isn't known.
module RepeatedPulls =
    let rule =
        { Id = "repeatedPulls"
          Title = "Sequence with effects pulled more than once"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "A sequence whose steps require contexts is pulled more than once on some path, so its steps, and their effects, run again. Pull it once into a list with `seq.toList` and use that." }

    /// The contexts the steps of a sequence of type `ty` require, if it is one and they hold one that is not a variable.
    let private effectfulSeq (ty: Ty) : Ty list option =
        match Unifier.unalias ty with
        | TyNominal(id, args, _, _) when id = Seqs.nominal ->
            match List.tryLast args |> Option.map Unifier.unalias with
            | Some (TyContexts contexts) when contexts |> List.exists (function TyVar _ -> false | _ -> true) -> Some contexts
            | _ -> None
        | _ -> None

    /// The types the parameters of a function of type `input` have when it is called with `arity` arguments: a function
    /// of several takes a record of them by position.
    let private parameterTypes (arity: int) (input: Ty) : Ty list option =
        match Unifier.unalias input with
        | _ when arity = 1 -> Some [ input ]
        | TyRecord(fields, _) when fields.Count = arity && fields |> Map.forall (fun key _ -> fst (System.Int32.TryParse key)) ->
            Some (fields |> Map.toList |> List.sortBy (fst >> int) |> List.map snd)
        | _ -> None

    /// What a function takes as one of its parameters.
    type private Parameter =
        /// A `seq`.
        | Seq
        /// Anything `Iterable`: a type variable it requires `Iterable` of.
        | Iterable
        | Other

    /// What a call does with its arguments: what it takes each as, by position, and whether what it gives back is a
    /// sequence.
    type private Callee = { Parameters: Parameter list; GivesSeq: bool }

    let private takesSeq (parameter: Parameter) =
        match parameter with
        | Seq
        | Iterable -> true
        | Other -> false

    /// What the function `symbol` does with its `arity` arguments, from the type it is declared with: it takes a sequence
    /// where its parameter is a `seq`, or a type variable it requires `Iterable` of.
    let private calleeOf (model: SemanticModel) (arity: int) (symbol: SymbolId) : Callee option =
        let declared =
            match model.SymbolTypes |> Map.tryFind symbol with
            | Some ty -> Some (ty, model.ContextTypes |> Map.tryFind symbol |> Option.defaultValue [])
            | None -> model.ImportedContextTypes |> Map.tryFind symbol
        match declared with
        | Some (ty, contexts) ->
            match Unifier.unalias ty with
            | TyFunc(input, output, _) ->
                let iterable =
                    contexts
                    |> List.choose (fun context ->
                        match Unifier.unalias context with
                        | TyApply(name, TyVar var :: _) when name = Seqs.iterable -> Some var
                        | _ -> None)
                    |> Set.ofList
                let parameter (ty: Ty) =
                    match Unifier.unalias ty with
                    | Seqs.SeqType _ -> Seq
                    | TyVar var when iterable.Contains var -> Iterable
                    | _ -> Other
                let givesSeq = match Unifier.unalias output with Seqs.SeqType _ -> true | _ -> false
                parameterTypes arity input |> Option.map (fun parameters -> { Parameters = parameters |> List.map parameter; GivesSeq = givesSeq })
            | _ -> None
        | None -> None

    /// The callee of `expr` if it is a call or a pipe of a function the module knows the type of, with the arguments it
    /// is given in order, as they are written: a pipe's value first, and `f(a, b)`'s two, not the tuple it passes them
    /// as (see `SyntaxTree.writtenArguments`).
    let private callOf (model: SemanticModel) (expr: Expression) : (Callee * Expression list) option =
        let arguments =
            match expr.Kind with
            | FunctionCall(_, _, args) -> Some (SyntaxTree.writtenArguments args)
            | Pipe(value, _, _, args) -> Some (value :: SyntaxTree.writtenArguments args)
            | _ -> None
        match arguments, model.References |> Map.tryFind expr.Id with
        | Some args, Some symbol when not args.IsEmpty ->
            calleeOf model args.Length symbol
            |> Option.filter (fun callee -> callee.Parameters.Length = args.Length)
            |> Option.map (fun callee -> callee, args)
        | _ -> None

    /// Whether a `for` loop whose body is `body` `yield`s, in it or in a loop within it but not in another lambda: one
    /// that makes a sequence, which desugaring makes a `seq.flatMap`, not one that walks it.
    let private yields (body: Expression) =
        body
        |> SyntaxTree.expressionsWithinBy (fun expr ->
            match expr.Kind with
            | ForSyntax.Call(iterables, _, _, loopBody) -> loopBody :: iterables
            | Lambda _ -> []
            | _ -> let children, statements = SyntaxTree.childrenOf expr in children @ (statements |> List.collect SyntaxTree.ofStatement))
        |> List.exists (fun expr -> match expr.Kind with WorkflowBindExpr(keyword, _) -> keyword = WorkflowSyntax.yieldKeyword | _ -> false)

    /// What pulling `expr` pulls: the sequences with effects it is, or was made from, each as the local, parameter or
    /// pattern variable that holds it. `madeFrom` has those a local made from others was made from. A sequence a call
    /// makes is made from those it is given, and from those a lambda it is given gives: `seq { for xs | x -> yield x }`
    /// is made from `xs`.
    let rec private sources (model: SemanticModel) (madeFrom: Map<SymbolId, Set<SymbolId>>) (expr: Expression) : Set<SymbolId> =
        let recur = sources model madeFrom
        match expr.Kind with
        | IdentifierExpr _ ->
            match model.References |> Map.tryFind expr.Id with
            | Some symbol when madeFrom.ContainsKey symbol -> madeFrom.[symbol]
            | Some symbol ->
                let local =
                    model.Symbols
                    |> Map.tryFind symbol
                    |> Option.exists (fun found ->
                        match found.Kind with
                        | LocalValue
                        | Parameter
                        | PatternVariable -> true
                        | TopLevelValue
                        | ImportedModule
                        | TypeDefinition
                        | ContextMember
                        | UnitOfMeasure -> false)
                let effectful = model.Types |> Map.tryFind expr.Id |> Option.bind effectfulSeq |> Option.isSome
                if local && effectful then Set.singleton symbol else Set.empty
            | None -> Set.empty
        | ForSyntax.Call(iterables, _, _, body) when yields body -> iterables |> List.map recur |> Set.unionMany
        | FunctionCall _
        | Pipe _ ->
            match callOf model expr with
            | Some (callee, args) when callee.GivesSeq ->
                List.zip callee.Parameters args
                |> List.filter (fun (parameter, arg) -> takesSeq parameter || (match arg.Kind with Lambda _ -> true | _ -> false))
                |> List.map (snd >> recur)
                |> Set.unionMany
            | _ -> Set.empty
        | Lambda(_, body, _) -> recur body
        | Block statements ->
            match List.tryLast statements with
            | Some { Kind = ExprStatement last } -> recur last
            | _ -> Set.empty
        | IfExpr(_, ifTrue, ifFalse) -> Set.union (recur ifTrue) (recur ifFalse)
        | Match(_, arms) -> arms |> List.map (snd >> recur) |> Set.unionMany
        | _ -> Set.empty

    /// The pulls on the path through an expression that pulls each sequence the most, by the symbol that holds it: where
    /// each of its first two pulls on that path is, as a pull's span and whether it is in a loop. Two are all a report
    /// needs.
    type private Pulls = Map<SymbolId, (SourceSpan * bool) list>

    /// The pulls of `first` and then `second`.
    let private andThen (first: Pulls) (second: Pulls) : Pulls =
        second |> Map.fold (fun acc symbol spans -> acc |> Map.add symbol (List.truncate 2 ((acc |> Map.tryFind symbol |> Option.defaultValue []) @ spans))) first

    /// The pulls of whichever of `one` and `other` pulls each sequence more.
    let private either (one: Pulls) (other: Pulls) : Pulls =
        other
        |> Map.fold
            (fun acc symbol spans ->
                match acc |> Map.tryFind symbol with
                | Some found when found.Length >= spans.Length -> acc
                | _ -> acc |> Map.add symbol spans)
            one

    /// The pulls of a loop's body that pulls as `body` does each time round.
    let private repeated (body: Pulls) : Pulls =
        body |> Map.map (fun _ spans -> match spans with (span, _) :: _ -> [ span, true; span, true ] | [] -> [])

    /// The pulls of `expr`, `madeFrom` saying what each local made from sequences with effects was made from.
    let rec private pullsOf (model: SemanticModel) (madeFrom: Map<SymbolId, Set<SymbolId>>) (expr: Expression) : Pulls =
        let recur = pullsOf model madeFrom
        let inOrder (exprs: Expression list) = exprs |> List.map recur |> List.fold andThen Map.empty
        let pull (span: SourceSpan) (pulled: Expression) : Pulls =
            sources model madeFrom pulled |> Seq.map (fun symbol -> symbol, [ span, false ]) |> Map.ofSeq
        match expr.Kind with
        | ForSyntax.Call(iterables, _, _, body) when not (yields body) ->
            let walks = iterables |> List.map (fun iterable -> andThen (recur iterable) (pull iterable.Span iterable))
            andThen (List.fold andThen Map.empty walks) (repeated (recur body))
        | WhileSyntax.Call(condition, body) -> repeated (inOrder [ condition; body ])
        | IfExpr(condition, ifTrue, ifFalse) -> andThen (recur condition) (either (recur ifTrue) (recur ifFalse))
        | Match(values, arms) -> andThen (inOrder (values @ Pattern.armExpressions arms)) (arms |> List.map (snd >> recur) |> List.fold either Map.empty)
        | Except(value, arms) -> andThen (inOrder (value :: Pattern.armExpressions arms)) (arms |> List.map (snd >> recur) |> List.fold either Map.empty)
        | FunctionCall _
        | Pipe _ ->
            let children, _ = SyntaxTree.childrenOf expr
            let pulled =
                match callOf model expr with
                | Some (callee, args) when not callee.GivesSeq ->
                    List.zip callee.Parameters args |> List.filter (fst >> takesSeq) |> List.map (fun (_, arg) -> pull arg.Span arg) |> List.fold andThen Map.empty
                | _ -> Map.empty
            andThen (inOrder children) pulled
        | _ ->
            let children, statements = SyntaxTree.childrenOf expr
            inOrder (children @ (statements |> List.collect SyntaxTree.ofStatement))

    /// What each local made from sequences with effects was made from, by its symbol, from the local defs of `model` in
    /// the order they are written: `def evens = seq.filter(lines, even)` is made from `lines`.
    let private madeFromIn (model: SemanticModel) (defs: (Statement * SymbolId * Expression) list) : Map<SymbolId, Set<SymbolId>> =
        defs
        |> List.fold
            (fun madeFrom (_, symbol, value) ->
                match sources model madeFrom value with
                | found when found.IsEmpty -> madeFrom
                | found -> madeFrom |> Map.add symbol found)
            Map.empty

    /// The local defs of `model` that name one value, each with its symbol and value, in the order they are written.
    let private localDefs (model: SemanticModel) : (Statement * SymbolId * Expression) list =
        let locals =
            model.Symbols
            |> Map.values
            |> Seq.filter (fun symbol -> symbol.Kind = LocalValue)
            |> Seq.sortBy _.Declaration.Start
            |> Seq.groupBy _.Name
            |> Map.ofSeq
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.blocksWithin
        |> List.concat
        |> List.choose (fun statement ->
            match statement.Kind with
            | DefStatement(_, name, _, value) ->
                locals
                |> Map.tryFind name
                |> Option.bind (Seq.tryFind (fun symbol -> SourceSpan.contains statement.Span symbol.Declaration))
                |> Option.map (fun symbol -> statement, symbol.Id, value)
            | _ -> None)
        |> List.sortBy (fun (statement, _, _) -> statement.Span.Start)

    /// The fix for a local, `def name = value`, pulled more than once: pulling it once into a list, `value \seq.toList`,
    /// and passing that as a sequence where a use of it needs one, `seq.length(name \list.toSeq)`. None for a local whose
    /// type is written, which the list would not match.
    let private fixFor (model: SemanticModel) (defs: (Statement * SymbolId * Expression) list) (symbol: Symbol) : Fix option =
        defs
        |> List.tryFind (fun (_, id, _) -> id = symbol.Id)
        |> Option.bind (fun (statement, _, value) ->
            match statement.Kind with
            | DefStatement(_, _, None, _) ->
                let insert (at: int) (text: string) = { Span = TextEdit.spanBetween model.Source value.Span at at; NewText = text }
                let pulled =
                    match value.Kind with
                    | FunctionCall _
                    | Pipe _
                    | IdentifierExpr _
                    | MemberAccess _ -> [ insert value.Span.End " \\seq.toList" ]
                    | _ -> [ insert value.Span.Start "("; insert value.Span.End ") \\seq.toList" ]
                let asSeq =
                    model.Syntax
                    |> List.collect SyntaxTree.ofItem
                    |> List.collect SyntaxTree.expressionsWithin
                    |> List.collect (fun expr ->
                        match callOf model expr with
                        | Some (callee, args) ->
                            List.zip callee.Parameters args
                            |> List.choose (fun (parameter, arg) ->
                                match parameter, arg.Kind with
                                | Seq, IdentifierExpr _ when model.References |> Map.tryFind arg.Id = Some symbol.Id ->
                                    Some (insert arg.Span.End " \\list.toSeq")
                                | _ -> None)
                        | None -> [])
                Some { Title = $"Pull `{symbol.Name}` once into a list"; Edits = pulled @ asSeq }
            | _ -> None)

    /// The contexts the steps of the sequence `symbol` holds require, as its first use has them.
    let private contextsOf (model: SemanticModel) (symbol: SymbolId) : Ty list =
        model.References
        |> Map.toSeq
        |> Seq.filter (fun (_, referenced) -> referenced = symbol)
        |> Seq.tryPick (fun (node, _) -> model.Types |> Map.tryFind node |> Option.bind effectfulSeq)
        |> Option.defaultValue []

    let lint (model: SemanticModel) : Report list =
        let defs = localDefs model
        let madeFrom = madeFromIn model defs
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect (fun value -> pullsOf model madeFrom value |> Map.toList)
        |> List.choose (fun (id, pulls) ->
            match pulls, model.Symbols |> Map.tryFind id with
            | [ (first, _); (again, inLoop) ], Some symbol ->
                let contexts = contextsOf model id |> List.map (Unifier.tyToDisplayString Ascii) |> String.concat ", "
                let how, related =
                    if inLoop then "is pulled each time round the loop", []
                    else "is pulled again here", [ first, $"'{symbol.Name}' is first pulled here" ]
                Some
                    { Rule = rule.Id
                      Subject = Some symbol.Name
                      Span = again
                      Message =
                        $"'{symbol.Name}' {how}, and its steps, which require {contexts}, run again: pull it once into a list with `seq.toList` and use that"
                      Related = related
                      Fixes = Option.toList (fixFor model defs symbol) }
            | _ -> None)
        |> List.sortBy (fun report -> report.Span.Start)
