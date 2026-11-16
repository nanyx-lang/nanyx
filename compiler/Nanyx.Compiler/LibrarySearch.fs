/// Searching the standard library by name, by type, or both: `nanyx search` and the extension's *Search library by
/// type*. A query is a name (`trim`), a type written as a def's annotation is (`bool -> string`), or a name and a type
/// with a colon between them (`trim: string -> string`). See "Search by type" on the docs' Info view and library browser
/// page for what matches and how matches are ranked.
module NyxCompiler.LibrarySearch

open System
open System.Text.RegularExpressions
open Parser.Program

/// What a query asks for: names that contain `Name`, ignoring case, and types that can be made equal to `Type`.
type Query = { Name: string option; Type: Ty option }

/// How well a type matches the query, best first.
type MatchRank =
    /// Equal to the query, up to the names of their variables.
    | Equal
    /// Equal once its variables, or the query's, are instantiated.
    | Instantiated
    /// Equal once the inputs are put in another order.
    | Reordered
    /// Equal, but requiring contexts that no instance satisfies at the types of the match.
    | NeedsContexts

/// An export, or a member of a context the library exports, that matches a query.
type SearchMatch =
    { /// As source writes it: `string.trim`, or a context's member as `ToString(a).toString`.
      Name: string
      /// As hover writes the declaration.
      Signature: string
      Doc: string option
      /// The import that brings the name in; `None` for the prelude's.
      Import: string option
      Declaration: SourceSpan
      /// For a query by name alone, `Equal`.
      Rank: MatchRank
      /// The contexts it requires at the types of the match that no instance satisfies there, as written: `Show(a)`.
      Needs: string list }

let private namePattern = Regex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")

/// The type a query writes, `[Show(a)] a -> string` read as `a -> string`: the contexts an export requires are found by
/// matching, not written. Each of the query's variables gets an id below zero, so that none is an export's.
let private queryType (typeExpr: TypeExpr) : Ty =
    let written =
        match typeExpr.Kind with
        | TypeWithContext(_, inner) -> inner
        | _ -> typeExpr
    let ty, _ = TypeResolution.typeExprToTy InferState.emptyState written
    let rec renumber ty =
        match ty with
        | TyVar var -> TyVar { var with Id = -(var.Id + 1) }
        | TyOpenRecord(fields, row) -> TyOpenRecord(Map.map (fun _ field -> renumber field) fields, { row with Id = -(row.Id + 1) })
        | _ -> Ty.map renumber ty
    renumber ty

/// Reads a query: a name, a type, or a name and a type with a colon between them. Text that is not a name is a type.
let parse (text: string) : Result<Query, string> =
    let readType (written: string) =
        Api.parseTypeSignature written
        |> Result.map (queryType >> Some)
        |> Result.mapError (fun message -> $"`{written.Trim()}` is not a type: {message.Trim()}")
    let text = text.Trim()
    let colon = text.IndexOf ':'
    if colon >= 0 && not (text.Contains "::") then
        let name = text.Substring(0, colon).Trim()
        readType (text.Substring(colon + 1))
        |> Result.map (fun ty -> { Name = (if name = "" then None else Some name); Type = ty })
    elif namePattern.IsMatch text then
        Result.Ok { Name = Some text; Type = None }
    elif text = "" then
        Result.Error "The query is empty: write a name, a type, or both, as `trim: string -> string`."
    else
        readType text |> Result.map (fun ty -> { Name = None; Type = ty })

/// The arguments a nominal type is written with: `MRef(a)`'s `a`, without the scope a type in a scoped context's scope
/// has as its last argument.
let private writtenArguments (args: Ty list) =
    args |> List.filter (function FunctionContexts.ScopedContext _ -> false | _ -> true)

/// The name a type is written by, with its arguments: a primitive's, a nominal type's, an applied type's or an
/// alias's, without the module an aliased import qualifies it with.
let private headOf (ty: Ty) : (string * Ty list) option =
    let simple (name: string) = name.Substring(name.LastIndexOf '.' + 1)
    match ty with
    | TyPrimitive name -> Some (simple name, [])
    | TyNominal(id, args, _, _) -> Some (simple id.Name, writtenArguments args)
    | TyApply(name, args)
    | TyAlias(name, args, _) -> Some (simple name, args)
    | _ -> None

type private Substitution = Map<int, Ty>

let rec private resolve (subst: Substitution) (ty: Ty) =
    match ty with
    | TyVar var ->
        match subst.TryFind var.Id with
        | Some bound -> resolve subst bound
        | None -> ty
    | _ -> ty

/// `ty` with every variable `subst` binds replaced, all the way down.
let rec private substitute (subst: Substitution) (ty: Ty) : Ty =
    resolve subst ty |> Ty.map (substitute subst)

let rec private variablesOf (ty: Ty) : int list =
    match ty with
    | TyVar var -> [ var.Id ]
    // A nominal type or an alias is matched by its name and written arguments, and a function without its contexts.
    | TyNominal(_, args, _, _) -> writtenArguments args |> List.collect variablesOf
    | TyFunc(input, output, _) -> variablesOf input @ variablesOf output
    | TyAlias(_, args, _) -> args |> List.collect variablesOf
    | _ -> Ty.children ty |> List.collect variablesOf

/// Whether the variable `id` is anywhere in `ty`, a nominal type's arguments too: binding it to such a type would make
/// it hold itself.
let rec private occurs (id: int) (ty: Ty) =
    match ty with
    | TyVar var -> var.Id = id
    | _ -> Ty.children ty |> List.exists (occurs id)

/// The tags of a union in order of their names, so that two unions of the same tags line up.
let private byTagName (items: Ty list) =
    items |> List.sortBy (function TyTag(name, _) -> name | _ -> "")

/// Makes `left` and `right` equal by binding variables of either, as `subst` already binds some. Contexts a function
/// requires are left out: they are matched against instances afterwards.
let rec private unify (subst: Substitution) (left: Ty) (right: Ty) : Substitution option =
    let unifyAll subst lefts rights =
        if List.length lefts <> List.length rights then None
        else List.fold2 (fun found l r -> found |> Option.bind (fun subst -> unify subst l r)) (Some subst) lefts rights
    match resolve subst left, resolve subst right with
    | TyVar a, TyVar b when a.Id = b.Id -> Some subst
    | TyVar var, other
    | other, TyVar var ->
        if occurs var.Id (substitute subst other) then None else Some (subst |> Map.add var.Id other)
    | left, right ->
        match headOf left, headOf right with
        | Some (leftName, leftArgs), Some (rightName, rightArgs) when leftName = rightName && leftArgs.Length = rightArgs.Length ->
            unifyAll subst leftArgs rightArgs
        | _ ->
            match left, right with
            | TyAlias _, _ -> unify subst (Unifier.unalias left) right
            | _, TyAlias _ -> unify subst left (Unifier.unalias right)
            | TyFunc(leftInput, leftOutput, _), TyFunc(rightInput, rightOutput, _) ->
                unify subst leftInput rightInput |> Option.bind (fun subst -> unify subst leftOutput rightOutput)
            | TyRecord(leftFields, _), TyRecord(rightFields, _)
            | TyOpenRecord(leftFields, _), TyOpenRecord(rightFields, _) when Set.ofSeq leftFields.Keys = Set.ofSeq rightFields.Keys ->
                unifyAll subst (List.ofSeq leftFields.Values) (List.ofSeq rightFields.Values)
            | TyTag(leftName, leftPayload), TyTag(rightName, rightPayload) when leftName = rightName ->
                unifyAll subst (Option.toList leftPayload) (Option.toList rightPayload)
            | TyUnion leftItems, TyUnion rightItems -> unifyAll subst (byTagName leftItems) (byTagName rightItems)
            | _ -> None

/// `ty` written the same way whatever its variables are named: each variable numbered in the order it first appears,
/// each named type by its head, and without the contexts a function requires.
let private canonical (ty: Ty) : Ty =
    let numbers = variablesOf ty |> List.distinct |> List.mapi (fun index id -> id, index) |> Map.ofList
    let rec rewrite ty =
        match ty with
        | TyVar var -> TyVar (TyVar.ordinary (numbers.[var.Id]) None)
        | TyFunc(input, output, _) -> TyFunc(rewrite input, rewrite output, [])
        | TyRecord(fields, order) -> TyRecord(Map.map (fun _ field -> rewrite field) fields, order)
        | TyOpenRecord(fields, row) -> TyOpenRecord(Map.map (fun _ field -> rewrite field) fields, (TyVar.ordinary (numbers.[row.Id]) None))
        | TyTag(name, payload) -> TyTag(name, Option.map rewrite payload)
        | TyUnion items -> TyUnion(byTagName items |> List.map rewrite)
        | _ ->
            match headOf ty with
            | Some (name, args) -> TyApply(name, List.map rewrite args)
            | None -> ty
    rewrite ty

/// A function's inputs, when it takes several: the fields of its input, `0`, `1`, ... in order.
let private inputsOf (input: Ty) : Ty list option =
    match input with
    | TyRecord(fields, _) when fields.Count > 1 && Seq.forall (fun (key: string) -> fst (Int32.TryParse key)) fields.Keys ->
        fields |> Seq.sortBy (fun field -> int field.Key) |> Seq.map _.Value |> List.ofSeq |> Some
    | _ -> None

let rec private permutations (items: 'a list) : 'a list list =
    match items with
    | [] -> [ [] ]
    | _ ->
        items
        |> List.indexed
        |> List.collect (fun (index, item) ->
            let rest = items |> List.indexed |> List.filter (fun (other, _) -> other <> index) |> List.map snd
            permutations rest |> List.map (fun tail -> item :: tail))

/// The most inputs whose orders are tried: a function of more is matched with its inputs in the order written.
let private mostReordered = 5

/// How `exported` matches `query`, and the substitution that makes them equal: in the order written first, then with
/// the inputs of a function of several in each other order.
let private matchType (query: Ty) (exported: Ty) : (MatchRank * Substitution) option =
    match unify Map.empty query exported with
    | Some subst -> Some ((if canonical query = canonical exported then Equal else Instantiated), subst)
    | None ->
        match Unifier.unalias query, Unifier.unalias exported with
        | TyFunc(queryInput, queryOutput, _), TyFunc(exportedInput, exportedOutput, _) ->
            match inputsOf queryInput, inputsOf exportedInput with
            | Some queryInputs, Some exportedInputs when queryInputs.Length = exportedInputs.Length && queryInputs.Length <= mostReordered ->
                permutations exportedInputs
                |> List.tryPick (fun reordered ->
                    unify Map.empty (TyFunc(TypeResolution.tupleRecordType queryInputs, queryOutput, [])) (TyFunc(TypeResolution.tupleRecordType reordered, exportedOutput, [])))
                |> Option.map (fun subst -> Reordered, subst)
            | _ -> None
        | _ -> None

/// A name and type to search: an export the library has that is a value (a def, attached or not), or a member of one
/// of its contexts, with the contexts it requires.
type private Candidate =
    { Name: string
      Signature: string
      Doc: string option
      Import: string option
      Declaration: SourceSpan
      Type: Ty option
      Contexts: Ty list }

/// The parameterised contexts a def of type `ty` requires, as its declared contexts `declared` and its type have them.
/// A context of no parameters, such as `Console`, is supplied where it is used, not found at a type, and a context
/// variable stands for any; neither is matched. A written context can come back as an alias of itself.
let private requiredContexts (declared: Ty list) (ty: Ty) =
    let ofType =
        match Unifier.unalias ty with
        | TyFunc(_, _, contexts) -> contexts
        | _ -> []
    declared @ ofType
    |> List.choose (function
        | TyApply(name, (_ :: _ as args))
        | TyAlias(name, (_ :: _ as args), _) -> Some (TyApply(name, args))
        | _ -> None)
    |> List.distinctBy canonical

/// The candidates a module's compiled exports give: each export, and each member of each context it exports.
let private candidatesOf (compiled: LibraryCatalog.CompiledExports) : Candidate list =
    compiled.Exports
    |> List.collect (fun (export, symbol) ->
        let asExport ty contexts =
            { Name = export.Name
              Signature = export.Signature
              Doc = export.Doc
              Import = export.Import
              Declaration = export.Declaration
              Type = ty
              Contexts = contexts }
        match export.Kind with
        | LibraryCatalog.Type -> [ asExport None [] ]
        | LibraryCatalog.Context ->
            let members =
                match Definition.typeDefinitionOf compiled.Typed symbol with
                | Some info ->
                    let context = TyApply(info.Name, info.Parameters |> List.map TyVar)
                    let written = Unifier.tyToDisplayString Ascii context
                    match Unifier.unalias info.Underlying with
                    | TyRecord(fields, _) ->
                        fields
                        |> Map.toList
                        |> List.map (fun (memberName, memberType) ->
                            let memberSymbol = info.Members |> Map.tryFind memberName |> Option.bind compiled.Typed.Symbols.TryFind
                            { Name = $"{written}.{memberName}"
                              Signature = $"{memberName}: [{written}] {Unifier.tyToDisplayString Ascii memberType}"
                              Doc = memberSymbol |> Option.bind compiled.Reader.ForSymbol
                              Import = export.Import
                              Declaration = memberSymbol |> Option.map _.Declaration |> Option.defaultValue export.Declaration
                              Type = Some memberType
                              Contexts = [ context ] })
                    | _ -> []
                | None -> []
            asExport None [] :: members
        | _ ->
            match Hover.declaredType compiled.Typed symbol with
            | Some (ty, declared) -> [ asExport (Some ty) (requiredContexts declared ty) ]
            | None -> [ asExport None [] ])

/// Whether a context named `context` with the members `members` has an instance at `args`, which have no variables
/// left: the compiler's own `ToString`, or one it builds from a definition attached to the type for each member
/// (`int.compare` for `Compare(int)`), which `hasAttached` says whether the library has.
let private hasInstance (members: Map<string, string list>) (hasAttached: string -> bool) (context: string) (args: Ty list) =
    match args with
    | [ ty ] when context = InterpolationHoles.context && InterpolationHoles.hasBuiltInInstance ty -> true
    | [ ty ] ->
        match headOf ty, members |> Map.tryFind context with
        | Some (typeName, _), Some (_ :: _ as memberNames) -> memberNames |> List.forall (fun memberName -> hasAttached $"{typeName}.{memberName}")
        | _ -> false
    | _ -> false

/// Whether `name` is reserved: one of its parts starts with `__`.
let private isReserved (name: string) =
    name.Split '.' |> Array.exists (fun part -> part.StartsWith ReservedNames.prefix)

/// How well `name` matches the name a query asks for: the whole name or its last part, then a name that starts with
/// it, then one that contains it. `None` when it does not contain it.
let private nameScore (asked: string) (name: string) =
    let last = name.Substring(name.LastIndexOf '.' + 1)
    if String.Equals(name, asked, StringComparison.OrdinalIgnoreCase) || String.Equals(last, asked, StringComparison.OrdinalIgnoreCase) then Some 0
    elif name.StartsWith(asked, StringComparison.OrdinalIgnoreCase) || last.StartsWith(asked, StringComparison.OrdinalIgnoreCase) then Some 1
    elif name.Contains(asked, StringComparison.OrdinalIgnoreCase) then Some 2
    else None

/// How many of the variables of the query `query` the match `subst` makes a type that is not a variable: `ln`'s
/// `float -> float` fixes the `a` of `a -> a`, and is a worse match for it than `abs`'s `a -> a`.
let private fixedQueryVariables (subst: Substitution) (query: Ty) =
    variablesOf query
    |> List.distinct
    |> List.filter (fun id -> match substitute subst (TyVar (TyVar.ordinary id None)) with TyVar _ -> false | _ -> true)
    |> List.length

/// What in the standard library matches `query`, best first: by rank, then by how few of the query's variables the
/// match fixes, then by how well the name matches, then shorter names first. Doc comments' links lead where `linkTo`
/// says.
let search (linkTo: Func<SourceSpan, string>) (query: Query) : SearchMatch list =
    let modules = LibraryCatalog.importPaths () |> List.choose (LibraryCatalog.compiledExports linkTo)
    let candidates = modules |> List.collect candidatesOf |> List.filter (fun candidate -> not (isReserved candidate.Name))
    let exportedNames = modules |> Seq.collect (fun compiled -> compiled.Exports |> Seq.map (fun (export, _) -> export.Name)) |> Set.ofSeq
    let contextMembers =
        modules
        |> Seq.collect (fun compiled -> compiled.Typed.TypeDefs |> Map.values)
        |> Seq.filter _.IsContext
        |> Seq.map (fun info ->
            info.Name,
            match Unifier.unalias info.Underlying with
            | TyRecord(fields, _) -> List.ofSeq fields.Keys
            | _ -> [])
        |> Map.ofSeq
    let needsOf (subst: Substitution) (contexts: Ty list) =
        contexts
        |> List.choose (fun context ->
            match substitute subst context with
            | TyApply(name, args) as instantiated ->
                let isSatisfied = args |> List.forall (variablesOf >> List.isEmpty) && hasInstance contextMembers exportedNames.Contains name args
                if isSatisfied then None else Some (Unifier.tyToDisplayString Ascii instantiated)
            | _ -> None)
    candidates
    |> List.choose (fun candidate ->
        let score = query.Name |> Option.map (fun asked -> nameScore asked candidate.Name)
        match score with
        | Some None -> None
        | _ ->
            let found =
                match query.Type, candidate.Type with
                | None, _ -> Some (Equal, [], 0)
                | Some _, None -> None
                // A value of any type, such as `Num`'s `zero`, would match every query.
                | Some _, Some (TyVar _) -> None
                | Some wanted, Some ty ->
                    matchType wanted ty
                    |> Option.map (fun (rank, subst) ->
                        let fixedVariables = fixedQueryVariables subst wanted
                        match needsOf subst candidate.Contexts with
                        | [] -> rank, [], fixedVariables
                        | needs -> NeedsContexts, needs, fixedVariables)
            found
            |> Option.map (fun (rank, needs, fixedVariables) ->
                (rank, fixedVariables, score |> Option.flatten |> Option.defaultValue 0, candidate.Name.Length, candidate.Name),
                { Name = candidate.Name
                  Signature = candidate.Signature
                  Doc = candidate.Doc
                  Import = candidate.Import
                  Declaration = candidate.Declaration
                  Rank = rank
                  Needs = needs }))
    |> List.sortBy fst
    |> List.map snd
