/// What a `???` stands for: the type of value the code around it expects there, and the names in scope that would fit.
/// `???` itself has the type `undefined`, which fits anywhere without fixing a type variable (see `Infer`), so inference
/// says nothing about it. Once the module is inferred, this reads what it is in instead: the parameter of the call it is
/// an argument of, the type a def writes, the other branches of an `if` or a `match`, the other elements of a list, the
/// other operand of an operator, or a field of the record type a literal is checked against. NYX0111 says the type and
/// the names (see `withExpectedTypes`), and hover shows them (see `Hover.holeAt`).
module internal NyxCompiler.TypedHoles

open System.Collections.Generic
open Parser.Program
open NyxCompiler.InferState

/// A name that could fill a hole, if it is in scope there and its type fits.
type Candidate =
    { Name: Identifier
      Type: Ty
      Kind: SymbolKind
      /// Where it is declared, and where it can be written (see `Scope`): anywhere in the module for none.
      Declaration: SourceSpan
      Scope: SourceSpan option
      /// Whether it writes its own type, as a def's annotation does.
      Annotated: bool
      /// Whether it is the module's own: an imported one comes after the module's.
      Own: bool }

/// What the pass reads of an inferred module: each expression's type and each call's callee type, by node, both
/// resolved, and the names the module can write, made only for a module with a hole.
type Typing =
    { TypeOf: NodeId -> Ty option
      CalleeOf: NodeId -> Ty option
      Candidates: Lazy<Candidate list> }

/// Whether a symbol of `kind` names a value, which can fill a hole.
let private isValue (kind: SymbolKind) =
    match kind with
    | TopLevelValue
    | LocalValue
    | Parameter
    | PatternVariable -> true
    | _ -> false

/// The candidates among `symbols` that are in `scopes`, with the types `typeOf` gives them, and whether they are the
/// module's own (`own`) and write their types (`annotated`).
let private candidatesOf (symbols: Map<SymbolId, Symbol>) (scopes: Map<SymbolId, SourceSpan>) (typeOf: SymbolId -> Ty option) (own: SymbolId -> bool) (annotated: SymbolId -> bool) =
    scopes
    |> Seq.choose (fun entry ->
        match symbols.TryFind entry.Key, typeOf entry.Key with
        | Some symbol, Some ty when isValue symbol.Kind ->
            Some
                { Name = symbol.Name
                  Type = ty
                  Kind = symbol.Kind
                  Declaration = symbol.Declaration
                  Scope = if entry.Value.End = System.Int32.MaxValue then None else Some entry.Value
                  Annotated = annotated entry.Key
                  Own = own entry.Key }
        | _ -> None)
    |> List.ofSeq

/// What the pass reads of a module just inferred to `state`, its types resolved by `subst`, with the names `env` imports.
let ofInferState (state: InferState) (subst: Subst) (env: TypeEnv) : Typing =
    let own () =
        candidatesOf state.Symbols state.Scopes (fun id -> state.SymbolTypes.TryFind id |> Option.map (Unifier.apply subst)) (fun _ -> true) state.AnnotatedSymbols.Contains
    let imported (own: Candidate list) =
        let ownNames = own |> List.map _.Name |> Set.ofList
        env.Names
        |> Map.toList
        |> List.filter (fun (name, _) -> not (ownNames.Contains name))
        |> List.map (fun (name, scheme) ->
            { Name = name; Type = scheme.Type; Kind = TopLevelValue; Declaration = SourceSpan.none; Scope = None; Annotated = false; Own = false })
    { TypeOf = fun id -> state.ExprTypes.TryFind id |> Option.map (Unifier.apply subst)
      CalleeOf = fun id -> state.CalleeTypes.TryFind id |> Option.map (Unifier.apply subst)
      Candidates = lazy (let own = own () in own @ imported own) }

/// What the pass reads of `typed`, the compiled root module, whose types are resolved.
let ofTypedModule (typed: TypedModule) : Typing =
    let typeOf id = typed.SymbolTypes.TryFind id |> Option.orElseWith (fun () -> typed.ImportedTypes.TryFind id)
    { TypeOf = typed.ExprTypes.TryFind
      CalleeOf = typed.CalleeTypes.TryFind
      Candidates = lazy (candidatesOf typed.Symbols typed.Scopes typeOf (typed.ImportedTypes.ContainsKey >> not) typed.AnnotatedSymbols.Contains) }

/// A hole's expected type, and the code that would fill it, best first: a name, or a call that gives the type.
type Hole = { Expected: Ty; Fills: string list }

/// At most this many fills are offered for a hole.
let maxFills = 5

/// What an expression is directly in, outward from it: an expression, or the def whose value it is, by the def's name
/// and the span of the item or statement that declares it.
type private Parent =
    | InExpr of Expression
    | InDef of Identifier * annotated: bool * SourceSpan

let private isHole (expr: Expression) =
    match expr.Kind with
    | IdentifierExpr name -> name = ImplicitNames.notImplemented
    | _ -> false

/// Whether `ty` says what goes somewhere: not a variable nothing settled, `undefined`, or a type that failed to check.
let private informative (ty: Ty) =
    match Unifier.unalias ty with
    | TyVar _ -> false
    | unaliased -> not (Unifier.isBottom unaliased) && not (Unifier.holdsError unaliased)

/// The fields of the record `ty` is, or a nominal type's shape is, by name: a positional field's is its index.
let rec private fieldsOf (ty: Ty) : Map<string, Ty> option =
    match Unifier.unalias ty with
    | TyNominal(_, _, underlying, _) -> fieldsOf underlying
    | TyRecord(fields, _)
    | TyOpenRecord(fields, _) -> Some fields
    | _ -> None

/// The payload the tag `name` carries in `ty`, a union of tags or a nominal type made of one.
let rec private payloadOf (name: Identifier) (ty: Ty) : Ty option =
    match Unifier.unalias ty with
    | TyNominal(_, _, underlying, _) -> payloadOf name underlying
    | TyTag(tag, payload) when tag = name -> payload
    | TyUnion members -> members |> List.tryPick (payloadOf name)
    | _ -> None

/// The type of the `index`th of `count` arguments a function of type `callee` is given: its input for the only one, a
/// field of its input for one of several.
let private argumentOf (callee: Ty) (index: int) (count: int) : Ty option =
    match Unifier.unalias callee with
    | TyFunc(input, _, _) when count = 1 -> Some input
    | TyFunc(input, _, _) -> fieldsOf input |> Option.bind (Map.tryFind (TypeResolution.tupleFieldName index))
    | _ -> None

/// The type the code around `child` expects it to have, where `parents` are what it is in, innermost first.
let rec private expectedOf (typing: Typing) (child: Expression) (parents: Parent list) : Ty option =
    let settled = Option.filter informative
    match parents with
    | [] -> None
    | InDef(name, true, declaredIn) :: _ ->
        typing.Candidates.Value
        |> List.tryFind (fun candidate -> candidate.Annotated && candidate.Name = name && SourceSpan.contains declaredIn candidate.Declaration)
        |> Option.map _.Type
        |> settled
    | InDef(_, false, _) :: _ -> None
    | InExpr parent :: outer ->
        let isChild (expr: Expression) = obj.ReferenceEquals(expr, child) || expr.Id = child.Id
        let indexIn (items: Expression list) = items |> List.findIndex isChild
        // What the parent is expected to be: its own type if that says, else what is expected of it in turn.
        let ownOrOuter () = typing.TypeOf parent.Id |> settled |> Option.orElseWith (fun () -> expectedOf typing parent outer)
        let outerOrOwn () = expectedOf typing parent outer |> Option.orElseWith (fun () -> typing.TypeOf parent.Id |> settled)
        let firstSettled (others: Expression list) =
            others |> List.filter (isChild >> not) |> List.tryPick (fun other -> typing.TypeOf other.Id |> settled)
        let argument (args: Expression list) =
            typing.CalleeOf parent.Id |> Option.bind (fun callee -> argumentOf callee (indexIn args) args.Length) |> settled
        match parent.Kind with
        | FunctionCall(_, _, args)
        | ContextMemberCall(_, _, args) when args |> List.exists isChild -> argument args
        | Pipe(value, _, _, args) -> argument (value :: args)
        | BinaryOp(op, _, left, right) ->
            argument [ left; right ]
            |> Option.orElseWith (fun () ->
                match op with
                | "&&"
                | "||" -> Some (TyPrimitive "bool")
                | _ -> firstSettled [ left; right ])
        | IfExpr(condition, _, _) when isChild condition -> Some (TyPrimitive "bool")
        | IfExpr(_, ifTrue, ifFalse) -> firstSettled [ ifTrue; ifFalse ] |> Option.orElseWith ownOrOuter
        | Match(_, arms) when arms |> List.exists (snd >> isChild) -> firstSettled (arms |> List.map snd) |> Option.orElseWith ownOrOuter
        | Except(_, arms) when arms |> List.exists (snd >> isChild) -> ownOrOuter ()
        | Block statements ->
            match List.tryLast statements with
            | Some { Kind = ExprStatement last } when isChild last -> ownOrOuter ()
            | _ -> None
        | UseIn(_, body) when isChild body -> ownOrOuter ()
        | ContextNames _ -> expectedOf typing parent outer
        | Lambda _ ->
            // What is expected of the lambda comes first: its own type gives back `undefined` where its body is the
            // hole, and a record with an `undefined` field where its body is a record with a hole in it.
            let resultOf ty = match Unifier.unalias ty with TyFunc(_, result, _) -> Some result | _ -> None
            expectedOf typing parent outer |> Option.bind resultOf |> settled
            |> Option.orElseWith (fun () -> typing.TypeOf parent.Id |> Option.bind resultOf |> settled)
        | ListExpr items ->
            let elementOf ty = match Unifier.unalias ty with Lists.ListType element -> Some element | _ -> None
            expectedOf typing parent outer |> Option.bind elementOf |> settled
            |> Option.orElseWith (fun () -> firstSettled items)
            |> Option.orElseWith (fun () -> typing.TypeOf parent.Id |> Option.bind elementOf |> settled)
        | TupleExpr items ->
            outerOrOwn () |> Option.bind fieldsOf |> Option.bind (Map.tryFind (TypeResolution.tupleFieldName (indexIn items))) |> settled
        | RecordExpr fields ->
            let key =
                fields
                |> List.mapi (fun index field -> index, field)
                |> List.tryPick (fun (index, field) ->
                    match field with
                    | NamedField(name, value) when isChild value -> Some name
                    | PositionalField value when isChild value -> Some (TypeResolution.tupleFieldName index)
                    | _ -> None)
            key |> Option.bind (fun key -> outerOrOwn () |> Option.bind fieldsOf |> Option.bind (Map.tryFind key)) |> settled
        | TagExpr(name, Some _) -> expectedOf typing parent outer |> Option.bind (payloadOf name) |> settled
        | _ -> None

/// Each hole in `module'` with what it is in, innermost first, and the names of the defs around it.
let private holesIn (module': Module) : (Expression * Parent list) list =
    let found = ResizeArray()
    let rec walk (parents: Parent list) (expr: Expression) =
        if isHole expr then found.Add((expr, parents))
        let inner = InExpr expr :: parents
        let children, statements = SyntaxTree.childrenOf expr
        children |> List.iter (walk inner)
        for statement in statements do
            match statement.Kind with
            | DefStatement(_, name, annotation, value) -> walk (InDef(name, annotation.IsSome, statement.Span) :: inner) value
            | _ -> SyntaxTree.ofStatement statement |> List.iter (walk inner)
    let rec item (topLevel: TopLevelItem) =
        match topLevel.Kind with
        | Def (ValueDef(_, name, annotation, value)) -> walk [ InDef(name, annotation.IsSome, topLevel.Span) ] value
        | ModuleBlock(_, items) -> items |> List.iter item
        | Expr expr -> walk [] expr
        | _ -> ()
    module' |> List.iter item
    List.ofSeq found

/// Whether a value of type `candidate` fits where one of type `expected` goes. A variable of `candidate` that `expected`
/// doesn't share stands for any type when `generic`, the same one each time it is written; any other is only itself.
let private fits (generic: bool) (candidate: Ty) (expected: Ty) =
    let shared = HashSet<int>()
    let rec collect ty =
        match ty with
        | TyVar var -> shared.Add var.Id |> ignore
        | _ -> Ty.children ty |> List.iter collect
    collect expected
    let bound = Dictionary<int, Ty>()
    let rec go (candidate: Ty) (expected: Ty) =
        match Unifier.unalias candidate, Unifier.unalias expected with
        | TyVar var, other when generic && not (shared.Contains var.Id) ->
            match bound.TryGetValue var.Id with
            | true, earlier -> earlier = other
            | _ ->
                bound[var.Id] <- other
                true
        | TyFunc(input, result, _), TyFunc(expectedInput, expectedResult, _) -> go input expectedInput && go result expectedResult
        | TyApply(name, args), TyApply(expectedName, expectedArgs) -> name = expectedName && all args expectedArgs
        | TyNominal(id, args, _, _), TyNominal(expectedId, expectedArgs, _, _) -> id = expectedId && all args expectedArgs
        | TyRecord(fields, _), TyRecord(expectedFields, _) ->
            Set.ofSeq fields.Keys = Set.ofSeq expectedFields.Keys && fields |> Map.forall (fun name ty -> go ty expectedFields[name])
        | TyTag(name, payload), TyTag(expectedName, expectedPayload) ->
            name = expectedName
            && match payload, expectedPayload with
               | Some payload, Some expectedPayload -> go payload expectedPayload
               | None, None -> true
               | _ -> false
        | TyUnion members, TyUnion expectedMembers -> all members expectedMembers
        | candidate, expected -> candidate = expected
    and all (candidates: Ty list) (expected: Ty list) = candidates.Length = expected.Length && List.forall2 go candidates expected
    go candidate expected

/// How a call of a function whose input is `input` is written with each argument a hole: `f()`, `f(???)`, `f(???, ???)`.
let private callText (name: Identifier) (input: Ty) =
    let holes count = List.replicate count ImplicitNames.notImplemented |> String.concat ", "
    match Unifier.unalias input with
    | TyPrimitive "unit" -> $"{name}()"
    | TyRecord(fields, _) when fields.Count > 1 && fields.Keys |> Seq.forall (fun key -> key |> Seq.forall System.Char.IsDigit) ->
        $"{name}({holes fields.Count})"
    | _ -> $"{name}({holes 1})"

/// The code that would fill a hole at `at` that expects `expected`, best first, leaving out the defs around it (`around`):
/// the names in scope there of that type, then the calls of the module's functions that give it. Of each, the module's own
/// come first, the nearest local first, then imported ones, and then by name.
let private fillsAt (typing: Typing) (around: Set<Identifier>) (at: SourceSpan) (expected: Ty) : string list =
    let inScope =
        typing.Candidates.Value
        |> List.filter (fun candidate ->
            (candidate.Scope |> Option.forall (fun scope -> SourceSpan.contains scope at))
            && not (around.Contains candidate.Name)
            && not (candidate.Name.StartsWith ReservedNames.prefix)
            && not (candidate.Name.Contains '.')
            && candidate.Name <> ImplicitNames.notImplemented
            && match Unifier.unalias candidate.Type with TyVar _ -> false | ty -> not (Unifier.isBottom ty))
        // A local hides an earlier one of its name.
        |> List.sortByDescending (fun candidate -> candidate.Declaration.Start)
        |> List.distinctBy _.Name
    let rank (candidate: Candidate) =
        let local = candidate.Kind <> TopLevelValue
        (if local then 0 elif candidate.Own then 1 else 2), (if local then -candidate.Declaration.Start else 0), candidate.Name
    let generic (candidate: Candidate) = candidate.Kind = TopLevelValue
    let names =
        inScope |> List.filter (fun candidate -> fits (generic candidate) candidate.Type expected) |> List.sortBy rank |> List.map _.Name
    let calls =
        inScope
        |> List.choose (fun candidate ->
            match Unifier.unalias candidate.Type with
            | TyFunc(input, result, _) when candidate.Own && fits (generic candidate) result expected -> Some (rank candidate, callText candidate.Name input)
            | _ -> None)
        |> List.sortBy fst
        |> List.map snd
    names @ calls |> List.distinct |> List.truncate maxFills

/// Each hole in `module'` whose expected type the code around it says, by where it is written.
let holes (typing: Typing) (module': Module) : Map<SourceSpan, Hole> =
    holesIn module'
    |> List.choose (fun (hole, parents) ->
        expectedOf typing hole parents
        |> Option.map (fun expected ->
            let around = parents |> List.choose (function InDef(name, _, _) -> Some name | InExpr _ -> None) |> Set.ofList
            hole.Span, { Expected = expected; Fills = fillsAt typing around hole.Span expected }))
    |> Map.ofList

/// `fills` written as a list of code: "`a`", "`a` or `b`", "`a`, `b` or `c`".
let private listed (fills: string list) =
    let quoted = fills |> List.map (fun fill -> $"`{fill}`")
    match List.rev quoted with
    | [] -> ""
    | [ only ] -> only
    | last :: earlier -> $"""{List.rev earlier |> String.concat ", "} or {last}"""

/// What the hole's NYX0111 adds to its message: the type that goes there, written by `typeText`, and what would fill it.
let describe (typeText: Ty -> string) (hole: Hole) =
    let text = typeText hole.Expected
    let article = if text.Length > 0 && "aeiouAEIOU".Contains text[0] then "an" else "a"
    let goes = $"{article} `{text}` goes here"
    match hole.Fills with
    | [] -> goes
    | fills -> $"{goes}, such as {listed fills}"

/// `diagnostics` with each NYX0111 of a hole in `holes` saying the type that goes there, written by `typeText`, and what
/// would fill it, each a fix that writes it in the hole's place.
let withExpectedTypes (typeText: Ty -> string) (holes: Map<SourceSpan, Hole>) (diagnostics: Diagnostic list) =
    if holes.IsEmpty then diagnostics
    else
        diagnostics
        |> List.map (fun diagnostic ->
            match diagnostic.Span |> Option.bind holes.TryFind with
            | Some hole when diagnostic.Code = DiagnosticCodes.NotImplemented.Code ->
                { diagnostic with
                    Message = $"{diagnostic.Message}: {describe typeText hole}"
                    Fixes =
                        diagnostic.Fixes
                        @ (hole.Fills |> List.map (fun fill -> { Title = $"Fill in `{fill}`"; Edits = [ { Span = diagnostic.Span.Value; NewText = fill } ] })) }
            | _ -> diagnostic)
