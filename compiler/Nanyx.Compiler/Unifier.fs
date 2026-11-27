namespace NyxCompiler

open System.Collections.Generic
open Parser.Program

/// Substitution map for type variables.
type Subst = Map<int, Ty>

/// What made a constraint fail: two types that differ, or a function that requires contexts given where one that
/// requires fewer is expected, which may be the same type otherwise.
type MismatchKind =
    | TypesDiffer
    /// A `char` and a `string`, which differ as any two types do, but whose error says how to make a string literal a
    /// `char` (see `CharLiterals.withHint`).
    | CharAndString
    | ContextsNotAllowed
    /// A value of the shape of the nominal type it is given for: the type, whether it is private, and its shape.
    | NominalShape of NominalId * isPrivate: bool * shape: Ty
    /// Two records that differ in one field each, whose names are close, so one is likely the other misspelled: the
    /// field only the first has and the second record's type, then the field only the second has and the first's.
    /// Each type is named as its alias if it was written as one: `P`.
    | MisspelledField of onlyLeft: string * rightRecord: string * onlyRight: string * leftRecord: string

/// What a mismatch says, with the types it names written by the function it is given: `Unifier.tyToString`, or one
/// that writes them with the names a module knows them by (see `Unifier.tyToDisplayStringIn`).
type MismatchText = (Ty -> string) -> string

/// A constraint that cannot be satisfied: what is wrong, where (the constraint's span), and what kind of failure it is.
type Mismatch = MismatchText * SourceSpan * MismatchKind

/// The characters a type shown to a reader is written in.
type TypeNotation =
    /// Type variables the compiler made up are `α`, `β`, `γ`, … through the Greek alphabet, and the rest of an open
    /// record is spread with an ellipsis: `(name: α, …)`.
    | Unicode
    /// Only ASCII: type variables are `a`, `b`, `c`, ... and a spread is three dots, `(name: a, ...)`.
    | Ascii
    /// As source writes it, for an annotation an editor inserts: as `Ascii`, except that the rest of a union is always
    /// named, as source can't spread one: `#some(a) | z`.
    | Source

module Unifier =
    /// The type an alias stands for, looking through any number of aliases; any other type as it is. Only the
    /// outermost type is unwrapped, so aliases inside it keep their names. A recursive alias is unfolded one level
    /// (see `RecursiveAliases`).
    let rec unalias (ty: Ty) : Ty =
        match ty with
        | TyAlias _ -> unalias (RecursiveAliases.underlying ty)
        | _ -> ty

    /// The type of an expression that failed to check. It agrees with every type, so nothing that uses the
    /// expression is an error too: a variable unified with it becomes it, and from then on agrees with everything as
    /// well. It is a primitive no source can name, so every pass that handles types handles it without a case of its
    /// own; only a module with errors has it.
    let errorType = TyPrimitive "?error"

    /// Whether `ty` is `errorType`.
    let isError (ty: Ty) = unalias ty = errorType

    /// Whether `ty` is `errorType` or is made of it at any depth, as `list(?error)` is.
    let rec holdsError (ty: Ty) = isError ty || Ty.exists holdsError ty

    /// `undefined`, the bottom type: the union of no tags, which no value is. It is the type of an expression that
    /// never gives a value, such as a call to a function that stops the program. It is assignable to every type, so
    /// such an expression fits wherever a value is expected; nothing but itself is assignable to it, and an `Equal`
    /// constraint treats it as the empty union it is. Given where a variable is expected, it binds nothing: the
    /// variable takes what the other values given for it are.
    let bottomType = TyUnion []

    /// Whether `ty` is `bottomType`.
    let isBottom (ty: Ty) = unalias ty = bottomType

    /// The number of the first rest variable `unify` makes up (see `TypePrinting.firstMadeUpRest`).
    let internal firstMadeUpRest = TypePrinting.firstMadeUpRest

    /// See `TypePrinting.isMadeUpRest`.
    let isMadeUpRest (var: TyVar) = TypePrinting.isMadeUpRest var

    /// Whether `var` is a variable inference made up: not one a type names, nor a rest `unify` made up.
    let private isPlainVar (var: TyVar) = var.Name.IsNone && not (isMadeUpRest var)

    /// See `TypePrinting.writtenContexts`.
    let writtenContexts (contexts: Ty list) = TypePrinting.writtenContexts contexts

    /// `ty` as it would be written in a def's annotation (see `TypePrinting.printTy`).
    let tyToString (ty: Ty) : string = TypePrinting.tyToString ty

    open TypePrinting

    /// The module `origin` is, as a reader knows it: its file's name, or the standard library's module's.
    let internal moduleText (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> $"'{System.IO.Path.GetFileName fullPath}'"
        | StandardLibraryModule name -> $"the standard library's '{name}'"
        | InMemory -> "this module"

    /// Says that two types are not the same: `'int' vs 'string'`, under the headline "Type mismatch". Two nominal types of the same name from
    /// different modules are written alike, so it says where each is defined.
    let private mismatchMessage left right : MismatchText = fun tyToString ->
        let homes =
            match left, right with
            | TyNominal(l, _, _, _), TyNominal(r, _, _, _) when l.Name = r.Name && l.Home <> r.Home ->
                $", defined in {moduleText l.Home} and {moduleText r.Home}"
            | _ -> ""
        $"'{tyToString left}' vs '{tyToString right}'{homes}"

    /// `items`, each in single quotes, as a message lists them.
    let private quotedList (tyToString: Ty -> string) (items: Ty list) =
        items |> List.map (fun item -> $"'{tyToString item}'") |> String.concat ", "

    /// What only one of two types has, as a message says it: `only the first requires 'Console' and only the second
    /// requires 'Log'`, each of `onlyLeft` and `onlyRight` that has any written by `render` after `verb`.
    let private differencesText (verb: string) (render: 'a list -> string) (onlyLeft: 'a list) (onlyRight: 'a list) =
        let onlyIn which items = if List.isEmpty items then None else Some $"only the {which} {verb} {render items}"
        [ onlyIn "first" onlyLeft; onlyIn "second" onlyRight ] |> List.choose id |> String.concat " and "

    /// `names` sorted, each written by `format`, as a message lists tags or fields.
    let private sortedNames (format: string -> string) (names: string list) =
        names |> List.sort |> List.map format |> String.concat ", "

    /// `the context 'Console'`, or `the contexts 'Console', 'Log'`, as a message names `contexts`.
    let private theContexts (tyToString: Ty -> string) (contexts: Ty list) =
        $"""{(if contexts.Length = 1 then "the context" else "the contexts")} {quotedList tyToString contexts}"""

    /// Says that a value of type `given` is given where one of type `expected` is expected.
    let private givenWhereExpected (tyToString: Ty -> string) (given: Ty) (expected: Ty) =
        $"'{tyToString given}' is given where '{tyToString expected}' is expected"

    /// Says which contexts make two function types differ: `onlyLeft` are those the first requires that the second has
    /// no room for, and `onlyRight` the reverse.
    let private contextsMessage (onlyLeft: Ty list) (onlyRight: Ty list) left right : MismatchText = fun tyToString ->
        let differences = differencesText "requires" (quotedList tyToString) onlyLeft onlyRight
        $"Context mismatch: '{tyToString left}' vs '{tyToString right}', where {differences}"

    /// Says that a function of type `given`, which requires `extra`, is given where one of type `expected` is, which
    /// has no room for them.
    let private contextsNotAllowedMessage (extra: Ty list) (given: Ty) (expected: Ty) : MismatchText = fun tyToString ->
        $"{givenWhereExpected tyToString given expected}, which does not require {theContexts tyToString extra}: write the contexts it may require on the type expected, as a context's member does in `log: [Console] string -> ()`, or a context variable for any, as in `[c] a -> b`"

    /// Says that a value of a type generic in contexts that carries `extra` is given where one that does not is expected.
    let private carriedNotAllowedMessage (extra: Ty list) (given: Ty) (expected: Ty) : MismatchText = fun tyToString ->
        $"{givenWhereExpected tyToString given expected}, whose functions don't require {theContexts tyToString extra}: write them in front of the type expected, or a context variable for any, as in `[c] {tyToString expected}`"

    /// Says which contexts make two values of a type generic in contexts, `left` and `right`, differ where they must be
    /// the same type: `onlyLeft` are those the first carries that the second has no room for, and `onlyRight` the reverse.
    let private carriedMismatchMessage (onlyLeft: Ty list) (onlyRight: Ty list) left right : MismatchText = fun tyToString ->
        let differences = differencesText "carries" (quotedList tyToString) onlyLeft onlyRight
        $"Context mismatch: '{tyToString left}' vs '{tyToString right}', where {differences}: write the same contexts in front of both, or a context variable for any"

    /// Says which tags make two unions differ: `onlyLeft` are tags only the first has that the second cannot take,
    /// and `onlyRight` the reverse. `Tag union mismatch: '...' vs '...', where only the first has #bad and only the
    /// second has #underflow`.
    let private tagUnionMessage (onlyLeft: Set<string>) (onlyRight: Set<string>) left right : MismatchText = fun tyToString ->
        let differences = differencesText "has" (sortedNames (sprintf "#%s")) (Set.toList onlyLeft) (Set.toList onlyRight)
        $"Tag union mismatch: '{tyToString left}' vs '{tyToString right}', where {differences}"

    /// Says that the tag `tag` is not one of the tags of the closed union `union`, which are `tags`, in the order the
    /// union writes them, with the closest of them as a question where one is close: `'#purple' is not one of the tags
    /// of 'Color': '#red', '#green' or '#blue'`. The union is named where it is written with an alias's name.
    let private notOneOfTagsMessage (tag: string) (union: Ty) (tags: string list) : MismatchText = fun tyToString ->
        let written = tags |> List.map (fun name -> $"'#{name}'") |> Diagnostics.listedOr
        let whose =
            match tyToString union with
            | named when named <> TypePrinting.tyToString union -> $"the tags of '{named}'"
            | _ -> "the union's tags"
        let suggestions = NameSuggestions.closest tags tag |> List.map (sprintf "#%s")
        $"'#{tag}' is not one of {whose}: {written}{NameSuggestions.question suggestions}"

    /// The error for a tag `ty` that the closed union `union`, whose tags are `items`, does not name, if `ty` is such a
    /// tag: no member's failure says it, as each is only another tag. A tag the union names, with another payload, has
    /// its member's failure, which says what differs.
    let private tagNotInUnion (union: Ty) (items: Ty list) (named: Map<string, Ty>) (rest: TyVar option) (ty: Ty) : MismatchText option =
        match ty, rest with
        | TyTag(name, _), None when not (named.ContainsKey name) ->
            let tags = items |> List.choose (function TyTag(tag, _) -> Some tag | _ -> None)
            Some (notOneOfTagsMessage name union tags)
        | _ -> None

    /// Says which fields only one of two records has: `Record field mismatch: '...' vs '...', where only the first has
    /// 'a' and only the second has 'y'`.
    let private recordKeyMessage leftKeys rightKeys left right : MismatchText = fun tyToString ->
        let differences =
            differencesText "has" (sortedNames (sprintf "'%s'")) (Set.toList (Set.difference leftKeys rightKeys)) (Set.toList (Set.difference rightKeys leftKeys))
        $"Record field mismatch: '{tyToString left}' vs '{tyToString right}', where {differences}"

    let private keySet (fields: Map<string, Ty>) =
        fields |> Map.keys |> Set.ofSeq

    let private ensureTagUnion items =
        items
        |> List.forall (function
            | TyTag _ -> true
            | TyVar _ -> true
            | _ -> false)


    /// A union's rest variable stands for more tags: `#some(a) | r` is never a `string`. (Unions and
    /// nominals are unwrapped before a lone type is matched against a union, so a tag is all that is left.)
    let private isTagLike ty =
        match ty with
        | TyTag _ -> true
        | _ -> false

    let private tagUnionKey ty =
        match ty with
        | TyTag(name, _) -> name
        | _ -> tyToString ty

    let private normalizeUnionItems (items: Ty list) =
        items
        |> List.fold (fun acc ty ->
            let key = tagUnionKey ty
            if acc |> Map.containsKey key then acc else acc |> Map.add key ty) Map.empty

    let private parseTagUnion items =
        if ensureTagUnion items then
            let mutable restVar: TyVar option = None
            let tagMap =
                items
                |> List.choose (function
                    | TyTag _ as tag -> Some tag
                    | TyVar v ->
                        match restVar with
                        | None ->
                            restVar <- Some v
                            None
                        | Some _ ->
                            restVar <- restVar
                            None
                    | _ -> None)
                |> normalizeUnionItems
            Ok (tagMap, restVar)
        else
            Error "Only tag unions are supported"

    /// The tags of a union or a lone tag, and the union's rest, as `parseTagUnion` gives them.
    let private parseTagsOf ty =
        match ty with
        | TyUnion items -> parseTagUnion items
        | _ -> parseTagUnion [ ty ]

    /// The payload of the tag `name` in `ty`, a tag or a union of them, when it has the tag with a payload.
    let payloadOfTag (name: string) (ty: Ty) : Ty option =
        let payloadIn =
            function
            | TyTag(tag, Some payload) when tag = name -> Some payload
            | _ -> None
        match ty with
        | TyUnion items -> items |> List.tryPick payloadIn
        | _ -> payloadIn ty

    /// Whether `ty` is given where `expected` is, and one is a tag or union while the other is a variable: what the
    /// variable stands for has still to be found.
    let private isPendingUnion ty expected =
        let isTagUnion ty = match ty with TyUnion _ | TyTag _ -> true | _ -> false
        match ty, expected with
        | TyVar _, other
        | other, TyVar _ -> isTagUnion other
        | _ -> false

    let empty : Subst = Map.empty

    /// `ty` with each variable replaced by what `resolve` gives for it: a substitution applied (see `apply`).
    let rec private applyResolving (resolve: TyVar -> Ty) (ty: Ty) : Ty =
        let apply = applyResolving resolve
        match ty with
        | TyVar v -> resolve v
        | TyFunc(arg, ret, contexts) ->
            TyFunc(apply arg, apply ret, contexts |> List.map apply |> FunctionContexts.flatten)
        | TyContexts contexts -> TyContexts(contexts |> List.map apply |> FunctionContexts.flatten)
        // A solved row adds its fields: to a record when it was solved to one, still open when to an open one.
        | TyOpenRecord(fields, row) ->
            let applied = fields |> Map.map (fun _ v -> apply v)
            let withFields more = more |> Map.fold (fun acc key fieldTy -> acc |> Map.add key fieldTy) applied
            match unalias (apply (TyVar row)) with
            | TyVar solved -> TyOpenRecord(applied, solved)
            // The open record's own fields come first, then the others in the order of the record the row stands for.
            | TyRecord(more, order) -> TyRecord(withFields more, FieldOrder(List.ofSeq applied.Keys @ order.Names))
            | TyOpenRecord(more, rest) -> TyOpenRecord(withFields more, rest)
            // Renamed for a reader (see `displayRenaming`): the row takes the name.
            | TyPrimitive name -> TyOpenRecord(applied, { row with Name = Some name })
            | _ -> TyOpenRecord(applied, row)
        // A rest variable solved to a union (`r` := `#notFound`) is spliced into the union it is the rest of. One
        // solved to the empty union leaves the others; when that is just one, the union is that one. A member that
        // is an alias of a tag or union is spliced in as what it stands for, as a union's members are tags. A tag
        // whose payload is the empty union has no values, so it is left out (`#error(#notFound)` taken from
        // `#error(#notFound)` leaves `#error` of nothing), and a tag the rest adds again is there once.
        | TyUnion items ->
            let spliced =
                items
                |> List.collect (fun item ->
                    let applied = apply item
                    match unalias applied with
                    | TyUnion inner -> inner
                    | TyTag _ as tag -> [ tag ]
                    | _ -> [ applied ])
                |> List.filter (function TyTag(_, Some (TyUnion [])) -> false | _ -> true)
                |> List.distinct
            match spliced with
            // A union with a member that failed to check is an error too, and so agrees with everything.
            | _ when spliced |> List.exists isError -> errorType
            | [ single ] when items.Length > 1 -> single
            | _ -> TyUnion spliced
        | _ -> Ty.map apply ty

    /// `ty` with `subst` applied: each variable it solves replaced by what it was solved to, itself with `subst`
    /// applied.
    let rec apply (subst: Subst) (ty: Ty) : Ty =
        applyResolving (fun v ->
            match subst |> Map.tryFind v.Id with
            | Some replacement when replacement = TyVar v -> replacement
            | Some replacement -> apply subst replacement
            | None -> TyVar v) ty

    /// What each solved variable resolves to under a substitution `applySolved` was given, by the variable's number.
    let private resolvedVars = System.Runtime.CompilerServices.ConditionalWeakTable<Subst, Dictionary<int, Ty>>()

    /// `ty` with `subst` applied, as `apply` gives it. What a variable resolves to is kept with `subst`, so that a
    /// variable solved to another, solved to another in turn, is followed to its end once, not on each lookup: the type
    /// checker looks up many types against each solution of its constraints so far.
    let applySolved (subst: Subst) (ty: Ty) : Ty =
        let resolved = resolvedVars.GetValue(subst, fun _ -> Dictionary<int, Ty>())
        let rec resolve (v: TyVar) =
            match subst |> Map.tryFind v.Id with
            | Some replacement when replacement = TyVar v -> replacement
            | Some replacement ->
                lock resolved (fun () ->
                    match resolved.TryGetValue v.Id with
                    | true, known -> known
                    | false, _ ->
                        let known = applyResolving resolve replacement
                        resolved[v.Id] <- known
                        known)
            | None -> TyVar v
        applyResolving resolve ty

    /// `ty`, written with the type variables `parameters` (a type definition's or a context's), with each parameter
    /// replaced by the type at the same position in `argTys`, all at once. `apply` would follow a replacement that is
    /// a variable to that variable's own replacement, which is wrong here: an imported definition's parameters are
    /// numbered by the module it came from, so an argument can be a variable of this module with the id of another
    /// parameter.
    let applyToParameters (parameters: TyVar list) (argTys: Ty list) (ty: Ty) : Ty =
        let pairs = List.zip (List.truncate argTys.Length parameters) (List.truncate parameters.Length argTys)
        // First to variables with ids no other variable has, then those to the arguments.
        let placeholder index (parameter: TyVar) = { parameter with Id = System.Int32.MinValue + index }
        let toPlaceholders = pairs |> List.mapi (fun index (parameter, _) -> parameter.Id, TyVar (placeholder index parameter)) |> Map.ofList
        let toArguments = pairs |> List.mapi (fun index (parameter, argTy) -> (placeholder index parameter).Id, argTy) |> Map.ofList
        ty |> apply toPlaceholders |> apply toArguments

    let private greekLetters = "αβγδεζηθικλμνξοπρστυφχψω"

    let private latinLetters = "abcdefghijklmnopqrstuvwxyz"

    /// The ids of the variables in `ty` that stand for the rest of a union or record: `r` in `#some(a) | r` and
    /// in `(name: a, ...r)`; with `ofUnions` false, only a record's.
    let restVarIds (ofUnions: bool) (ty: Ty) : Set<int> =
        let rec collect ty =
            match ty with
            | TyVar _
            | TyPrimitive _ -> Set.empty
            | TyNominal(_, args, _, _)
            | TyAlias(_, args, _) -> args |> List.map collect |> Set.unionMany
            | TyOpenRecord(fields, row) -> collect (TyRecord(fields, FieldOrder.none)) |> Set.add row.Id
            | TyUnion items ->
                let rests =
                    if ofUnions then items |> List.choose (function TyVar v -> Some v.Id | _ -> None) |> Set.ofList
                    else Set.empty
                items |> List.map collect |> Set.unionMany |> Set.union rests
            | _ -> Ty.fold (fun ids child -> Set.union ids (collect child)) Set.empty ty
        collect ty

    /// The ids of the made-up variables in `ty` that stand for the rest of an open record or union and appear nowhere
    /// else in it. Such a rest only says there may be more fields or tags, so it needs no name: `(name: α, …) -> α`,
    /// `#some(int) | … -> int`. In `Source`, only an open record's: source can't spread a union.
    let private loneRests (notation: TypeNotation) (ty: Ty) : Set<int> =
        let occurrences = varOccurrences ty
        let once =
            occurrences
            |> List.countBy (fun v -> v.Id)
            |> List.choose (fun (id, count) -> if count = 1 then Some id else None)
            |> Set.ofList
        let unnamed = occurrences |> List.filter (fun v -> v.Name.IsNone) |> List.map (fun v -> v.Id) |> Set.ofList
        restVarIds (notation <> Source) ty |> Set.intersect once |> Set.intersect unnamed

    /// The arguments for the associated types of the contexts `ty` requires, in the order they are printed, each with
    /// the name of its associated type: `elem, int` for `Iter(c, elem = int)`.
    let private associatedArguments (typeDefs: Map<string, TypeDefInfo>) (ty: Ty) : (string * Ty) list =
        // A context is an application, among a function's contexts or written on its own, as a signature's are.
        let rec contextsIn ty =
            let own = match ty with TyApply _ | TyAlias _ -> [ ty ] | _ -> []
            match ty with
            | TyFunc(arg, ret, contexts) -> writtenContexts contexts @ [ arg; ret ]
            | _ -> Ty.fold (fun found child -> found @ [ child ]) [] ty
            |> List.collect contextsIn
            |> List.append own
        contextsIn ty
        |> List.collect (function
            | TyApply(name, args)
            | TyAlias(name, args, _) ->
                match associatedIn typeDefs name with
                | associated when not associated.IsEmpty && associated.Length <= args.Length ->
                    List.zip associated (List.skip (args.Length - associated.Length) args)
                | _ -> []
            | _ -> [])

    /// The made-up variables of `ty` that are the argument for an associated type of a context it requires and appear
    /// nowhere else, each named after that associated type where no variable of `ty` has the name yet, so that it is
    /// left out, as a signature leaves it out: `[Num(a)] a, a -> a` (see `TypePrinting.printTy`).
    let private associatedNaming (typeDefs: Map<string, TypeDefInfo>) (ty: Ty) : Subst =
        let occurrences = varOccurrences ty
        let taken = occurrences |> List.choose _.Name |> Set.ofList
        let once = occurrences |> List.countBy _.Id |> List.filter (snd >> (=) 1) |> List.map fst |> Set.ofList
        associatedArguments typeDefs ty
        |> List.choose (function
            | name, TyVar v when v.Name.IsNone && once.Contains v.Id && not (taken.Contains name) -> Some (name, v)
            | _ -> None)
        |> List.distinctBy fst
        |> List.distinctBy (fun (_, v) -> v.Id)
        |> List.map (fun (name, v) -> v.Id, TyVar { v with Name = Some name })
        |> Map.ofList

    /// Replaces the type variables the compiler made up in `ty` with primitives that print as their names for a
    /// reader. They are named in the order they appear, through the Greek (`α`, `β`, ...) or Latin (`a`, `b`, ...)
    /// alphabet, skipping names already taken; variables named in an annotation keep their names. A union's rest
    /// variable is named from the other end of the alphabet, as in `#some(α) | ω`.
    ///
    /// When the two ends run into each other, the alphabet is split where they met while giving out its letters
    /// the first time, and each end goes round its own part again with a suffix: `α₁`, `ω₁` (`a1`, `z1`).
    ///
    /// The rests in `unnamedRests` are given no name and no letter, so they print as a bare spread.
    ///
    /// The context variables the compiler inferred (see `TyVar.inferredContextName`) are named before the others, from
    /// their own letter on, `κ` (`c` in ASCII and source), and the others are kept clear of their names:
    /// `[κ] ([κ] α -> β), α -> β`.
    let private displayRenaming (notation: TypeNotation) (unnamedRests: Set<int>) (ty: Ty) : Subst =
        let alphabet, suffix, contextStart =
            match notation with
            | Unicode -> greekLetters, subscript, 'κ'
            | Ascii
            | Source -> latinLetters, string, 'c'
        let vars = varsInPrintOrder ty
        let inferredContexts, vars = vars |> List.partition TyVar.isInferredContext
        let written = vars |> List.choose (fun v -> v.Name) |> Set.ofList
        let contextNames =
            let start = alphabet.IndexOf contextStart
            let letters = alphabet.Substring start + alphabet.Substring(0, start)
            Seq.initInfinite (fun i ->
                let round = i / letters.Length
                if round = 0 then string letters[i] else $"{letters[i % letters.Length]}{suffix round}")
            |> Seq.filter (written.Contains >> not)
        let contextRenaming = Seq.zip inferredContexts contextNames |> List.ofSeq
        let taken = contextRenaming |> List.map snd |> Set.ofList |> Set.union written
        // The letters an annotation took are left out before the alphabet is shared between its two ends.
        let letters =
            match alphabet |> String.filter (fun letter -> not (taken.Contains (string letter))) with
            | "" -> alphabet
            | free -> free
        let unnamed = vars |> List.filter (fun v -> v.Name.IsNone && not (unnamedRests.Contains v.Id))
        let rests = restVarIds true ty
        let isRest (v: TyVar) = rests.Contains v.Id
        let ordinary, rest = unnamed |> List.partition (isRest >> not)
        // The front's share of the alphabet: the letters it gave out before meeting the back, keeping at least
        // one for each end that has variables.
        let frontCount =
            let firstPass = unnamed |> List.truncate letters.Length |> List.filter (isRest >> not) |> List.length
            if ordinary.IsEmpty || rest.IsEmpty then firstPass else firstPass |> max 1 |> min (letters.Length - 1)
        let backCount = letters.Length - frontCount
        // The names from `count` letters, going round them again and again; `letterAt` picks one by its position.
        let goRound count letterAt =
            if count = 0 then Seq.empty
            else
                Seq.initInfinite (fun i ->
                    let letter = letters[letterAt (i % count)]
                    let round = i / count
                    if round = 0 then string letter else $"{letter}{suffix round}")
        let fromFront = goRound frontCount id
        let fromBack = goRound backCount (fun position -> letters.Length - 1 - position)
        let free = Seq.filter (fun name -> not (taken.Contains name))
        // Each made-up variable is replaced by a primitive that prints as its new name.
        Seq.append (Seq.zip ordinary (free fromFront)) (Seq.zip rest (free fromBack))
        |> Seq.append contextRenaming
        |> Seq.map (fun (v, name) -> v.Id, TyPrimitive name)
        |> Seq.append (unnamedRests |> Seq.map (fun id -> id, TyPrimitive ""))
        |> Map.ofSeq

    /// The variables of `ty` that a signature names after the associated type they are the argument for and that `ty`
    /// uses elsewhere too, as `elem` in `[Iter(c)] c, elem -> elem`, each given a name of its own: the next free letter
    /// of the Latin alphabet, as a signature's variables are, as `displayRenaming` gives them in `Source`. They are then
    /// written by name in the context, `[Iter(c, elem = a)] c, a -> a`, not bare, which reads as a type's name. One
    /// that `ty` uses nowhere else keeps its name, so it is left out: `[Iter(c)] c -> int` (see `TypePrinting.printTy`).
    let private associatedRenaming (typeDefs: Map<string, TypeDefInfo>) (ty: Ty) : Subst =
        let occurrences = varOccurrences ty
        let many = occurrences |> List.countBy _.Id |> List.filter (snd >> (<) 1) |> List.map fst |> Set.ofList
        let renamed =
            associatedArguments typeDefs ty
            |> List.choose (function
                | name, TyVar v when v.Name = Some name && many.Contains v.Id -> Some v
                | _ -> None)
            |> List.distinctBy _.Id
        let ids = renamed |> List.map _.Id |> Set.ofList
        let kept = occurrences |> List.filter (fun v -> v.Name.IsSome && not (ids.Contains v.Id))
        let names = displayRenaming Source Set.empty (TyApply("", kept @ (renamed |> List.map (fun v -> { v with Name = None })) |> List.map TyVar))
        renamed
        |> List.map (fun v ->
            match names.[v.Id] with
            | TyPrimitive name -> v.Id, TyVar { v with Name = Some name }
            | other -> v.Id, other)
        |> Map.ofList

    /// `ty` as `tyToString` writes it, with a union that holds all the tags of a union alias of `typeDefs` written with
    /// its name (see `TypePrinting.withUnionAliases`), and a context's associated types written by name, as a signature
    /// writes them, `Iter(c, elem = string)`, a signature's variable named after one given a name of its own (see
    /// `associatedRenaming`): how a diagnostic writes the types it names.
    let tyToStringIn (typeDefs: Map<string, TypeDefInfo>) (ty: Ty) : string =
        let ty = apply (associatedRenaming typeDefs ty) ty
        printTy (printingFor "..." (associatedIn typeDefs) (unionAliasesIn typeDefs) ty) true ty

    /// `ty` as `tyToString` writes it, with a spread written in `notation`, the associated types of the contexts of
    /// `typeDefs` written by name, and a union that holds all the tags of a union alias of `typeDefs` written with its
    /// name (see `TypePrinting.printTy`).
    let private printIn (typeDefs: Map<string, TypeDefInfo>) (notation: TypeNotation) (ty: Ty) : string =
        let spread = match notation with Unicode -> "…" | Ascii | Source -> "..."
        printTy (printingFor spread (associatedIn typeDefs) (unionAliasesIn typeDefs) ty) true ty

    /// `ty` for a reader, in `notation`, with the variables the compiler made up named as `displayRenaming`
    /// describes, a rest that appears nowhere else written as a bare spread: `(name: α, …)`, `#some(α) | …`, and the
    /// associated types of the contexts of `typeDefs` written by name, each a variable bound in its context where the
    /// type uses it: `[Iter(c, elem = b)] c, b -> b` (see `associatedRenaming`), `Iter(c, elem = int)`, and the tags of
    /// a union alias of `typeDefs` by its name: `#some(string) | FsError`.
    let tyToDisplayStringIn (typeDefs: Map<string, TypeDefInfo>) (notation: TypeNotation) (ty: Ty) : string =
        let ty = apply (associatedRenaming typeDefs ty) ty
        let ty = apply (associatedNaming typeDefs ty) ty
        printIn typeDefs notation (apply (displayRenaming notation (loneRests notation ty) ty) ty)

    /// `ty` for a reader, as `tyToDisplayStringIn` writes it, where no context has associated types and no union has an
    /// alias.
    let tyToDisplayString (notation: TypeNotation) (ty: Ty) : string = tyToDisplayStringIn Map.empty notation ty

    /// Names for the variables the compiler made up in `tys`, the types one message writes, shared by all of them: a
    /// rest of an open record or union is `r`, then `r1`, `r2`, ..., and any other `a`, `b`, `c`, ..., then `a1`, ..., in
    /// the order the message writes them, skipping the names the variables an annotation named have. A context variable
    /// the compiler inferred (see `TyVar.inferredContextName`) is named first, `c`, `d`, ..., as `displayRenaming` names it.
    let private messageRenaming (tys: Ty list) : Subst =
        let vars = tys |> List.collect varsInPrintOrder |> List.distinctBy (fun v -> v.Id)
        let inferredContexts, vars = vars |> List.partition TyVar.isInferredContext
        let written = vars |> List.choose (fun v -> v.Name) |> Set.ofList
        let contextLetters = TyVar.inferredContextLetters
        let contextNames =
            Seq.initInfinite (fun i -> if i < contextLetters.Length then string contextLetters[i] else $"{contextLetters[i % contextLetters.Length]}{i / contextLetters.Length}")
            |> Seq.filter (written.Contains >> not)
        let contextRenaming = Seq.zip inferredContexts contextNames |> List.ofSeq
        let taken = contextRenaming |> List.map snd |> Set.ofList |> Set.union written
        let rests = tys |> List.map (restVarIds true) |> Set.unionMany
        let free (names: seq<string>) = names |> Seq.filter (fun name -> not (taken.Contains name))
        let letters = latinLetters |> String.filter (fun letter -> letter <> 'r')
        let ordinaryNames = Seq.initInfinite (fun i -> if i < letters.Length then string letters[i] else $"{letters[i % letters.Length]}{i / letters.Length}") |> free
        let restNames = Seq.initInfinite (fun i -> if i = 0 then "r" else $"r{i}") |> free
        let made = vars |> List.filter (fun v -> v.Name.IsNone)
        let rest, ordinary = made |> List.partition (fun v -> rests.Contains v.Id)
        Seq.append (Seq.zip ordinary ordinaryNames) (Seq.zip rest restNames)
        |> Seq.append contextRenaming
        |> Seq.map (fun (v, name) -> v.Id, TyVar { v with Name = Some name })
        |> Map.ofSeq

    /// The text of a message `text` writes, given how to write each type it names: as `tyToStringIn typeDefs` writes it,
    /// with the variables the compiler made up named across all its types (see `messageRenaming`), so that one that
    /// appears in two of them has one name: `'int' vs '(name: a, ...r)'`, not `'int' vs '(name: t1, ...t6)'` (#2077), as
    /// is a signature's variable named after an associated type (see `associatedRenaming`).
    let messageText (typeDefs: Map<string, TypeDefInfo>) (text: (Ty -> string) -> string) : string =
        let written = ResizeArray<Ty>()
        text (fun ty -> written.Add ty; "") |> ignore
        let renaming = messageRenaming (List.ofSeq written)
        let associated = associatedRenaming typeDefs (TyApply("", written |> Seq.map (apply renaming) |> List.ofSeq))
        text (fun ty -> tyToStringIn typeDefs (apply associated (apply renaming ty)))

    /// The rests of `tys` that are lone (see `loneRests`) in every one of them they appear in.
    let private loneAcross (notation: TypeNotation) (tys: Ty list) : Set<int> =
        let loneIn = tys |> List.map (fun ty -> loneRests notation ty, varOccurrences ty |> List.map (fun v -> v.Id) |> Set.ofList)
        loneIn
        |> List.map fst
        |> Set.unionMany
        |> Set.filter (fun id -> loneIn |> List.forall (fun (lone, all) -> lone.Contains id || not (all.Contains id)))

    /// Each of `tys` for a reader, as `tyToDisplayStringIn` writes it, with a variable they share given the same name
    /// in each. The first one's variables are named first, so it reads as it would alone unless the others use a
    /// name from an annotation that it would have been given. A rest is a bare spread only where it is lone in
    /// every one of `tys` it appears in, so it is not named in one and not in another.
    let tysToDisplayStringsIn (typeDefs: Map<string, TypeDefInfo>) (notation: TypeNotation) (tys: Ty list) : string list =
        let associated = associatedRenaming typeDefs (TyApply("", tys))
        let tys = tys |> List.map (apply associated)
        let named = associatedNaming typeDefs (TyApply("", tys))
        let tys = tys |> List.map (apply named)
        let renamed = displayRenaming notation (loneAcross notation tys) (TyApply("", tys))
        tys |> List.map (apply renamed >> printIn typeDefs notation)

    /// Each of `tys` for a reader, as `tysToDisplayStringsIn` writes them, where no context has associated types.
    let tysToDisplayStrings (notation: TypeNotation) (tys: Ty list) : string list = tysToDisplayStringsIn Map.empty notation tys

    /// The type variables `tysToDisplayStrings` writes in `tys`, in the order it names them, each with the name it
    /// writes. A rest it writes as a bare spread is not among them.
    let displayedVars (notation: TypeNotation) (tys: Ty list) : (TyVar * string) list =
        let lone = loneAcross notation tys
        let renamed = displayRenaming notation lone (TyApply("", tys))
        varsInPrintOrder (TyApply("", tys))
        |> List.filter (fun v -> not (lone.Contains v.Id))
        |> List.map (fun v -> v, printIn Map.empty notation (apply renamed (TyVar v)))

    /// The rest variables of `ty` that only stand for tags it gives out: ones that appear in no function's input.
    /// Nothing can add a tag to such a union, so it has no more tags than those written: its rest is empty. In
    /// `#some(int), (int -> string) -> #some(string) | r`, `r` is; in `#some(a) | r -> #some(a) | r` it is not,
    /// as what the function is given more of it gives back. A nominal's variables are taken to be in an input,
    /// as its underlying type is not shown.
    let outputOnlyRestVars (ty: Ty) : TyVar list =
        // Each variable once for each time it appears, with whether it is a union's rest there and whether that
        // is in an input.
        let rec collect (input: bool) ty : (TyVar * bool * bool) list =
            match ty with
            | TyVar v -> [ v, false, input ]
            | TyPrimitive _ -> []
            | TyNominal(_, _, underlying, _) -> collect true underlying
            | TyAlias(_, _, underlying) -> collect input underlying
            | TyFunc(arg, ret, contexts) -> collect (not input) arg @ collect input ret @ List.collect (collect input) contexts
            // A record's row is not a union's rest: it is never solved to the empty union.
            | TyOpenRecord(fields, row) -> collect input (TyRecord(fields, FieldOrder.none)) @ [ row, false, input ]
            | TyUnion items ->
                items
                |> List.collect (function
                    | TyVar v -> [ v, true, input ]
                    | item -> collect input item)
            | _ -> Ty.children ty |> List.collect (collect input)
        let occurrences = collect false ty
        let inInput = occurrences |> List.choose (fun (v, _, input) -> if input then Some v.Id else None) |> Set.ofList
        occurrences
        |> List.choose (fun (v, isRest, _) -> if isRest && not (inInput.Contains v.Id) then Some v else None)
        |> List.distinctBy (fun v -> v.Id)

    /// The context variables of `ty`'s function types that appear in no function's input, at any depth. No caller
    /// chooses the contexts such a variable stands for, so it stands for none. In `int -> ([c] int -> int)`, `c` is;
    /// in `[c] ([c] a -> b), a -> b` it is not, as the function given for the input chooses it. A nominal's variables
    /// are taken to be in an input, as its underlying type is not shown, but for the contexts a type generic in contexts
    /// carries, which are where the type is: a value of `Stream(int)` made with pure functions carries none.
    let outputOnlyContextVars (ty: Ty) : TyVar list =
        // Each variable once for each time it appears, with whether it is a function's context there and whether that
        // is within a function's input.
        let rec collect (inInput: bool) ty : (TyVar * bool * bool) list =
            let ofContext context =
                match context with
                | TyVar v -> [ v, true, inInput ]
                | other -> collect inInput other
            match ty with
            | TyVar v -> [ v, false, inInput ]
            | TyPrimitive _ -> []
            | FunctionContexts.CarryingNominal(_, own, carried) -> List.collect (collect true) own @ List.collect ofContext carried
            | TyNominal(_, _, underlying, _) -> collect true underlying
            | TyAlias(_, args, underlying) -> List.collect (collect inInput) (underlying :: args)
            | TyFunc(arg, ret, contexts) -> collect true arg @ collect inInput ret @ List.collect ofContext contexts
            | TyOpenRecord(fields, row) -> collect inInput (TyRecord(fields, FieldOrder.none)) @ [ row, false, inInput ]
            | _ -> Ty.children ty |> List.collect (collect inInput)
        let occurrences = collect false ty
        let inInput = occurrences |> List.choose (fun (v, _, input) -> if input then Some v.Id else None) |> Set.ofList
        occurrences
        |> List.choose (fun (v, isContext, _) -> if isContext && not (inInput.Contains v.Id) then Some v else None)
        |> List.distinctBy (fun v -> v.Id)

    let rec occurs (varId: int) (ty: Ty) : bool =
        match ty with
        | TyVar v -> v.Id = varId
        // What an alias stands for is read, not its arguments: an argument it does not use is not in the type.
        | TyAlias(_, _, underlying) -> occurs varId underlying
        | _ -> Ty.exists (occurs varId) ty

    /// The highest number of a variable in `constraints`, or 0 when they have none.
    let internal highestVarId (constraints: ConstraintSet) =
        let rec maxId ty =
            match ty with
            | TyVar v -> v.Id
            | _ -> Ty.fold (fun acc child -> max acc (maxId child)) 0 ty
        constraints |> List.fold (fun acc (left, right, _, _) -> max acc (max (maxId left) (maxId right))) 0

    /// Whether the order a list of constraints was solved in could show (see `unifyFrom`).
    type internal SolveOrder =
        /// It could not.
        | Hidden
        /// These constraints were put off to the end (see `AssignableLater`).
        | PutOff of ConstraintSet
        /// A rest variable made up before them took more.
        | Shown

    /// Solves `constraints` on top of `start`, a substitution that solved constraints given earlier, or gives the
    /// first that cannot be satisfied (see `Mismatch`). Rest variables are made up from `restFrom` on, or past the
    /// constraints' own variables. Also gives where the next made-up rest variable would be numbered, and whether the
    /// order the constraints were solved in could show (see `SolveOrder`).
    let internal unifyFrom (start: Subst) (restFrom: int) (constraints: ConstraintSet) : Result<Subst * int * SolveOrder, Mismatch> =
        // A union, a record or a set of contexts grows through the rest variable made up for it, taking tags, fields
        // or contexts in the order constraints come. So the order shows when one made up before these constraints,
        // for `start`, takes more, or when a constraint is put off to the end (see `AssignableLater`).
        let mutable restTaken = false
        let mutable putOff = []
        let madeUpBefore (var: TyVar) = isMadeUpRest var && var.Id < restFrom
        // Widening a union (below) makes up rest variables. They are numbered past every variable in the
        // constraints, and far past where inference numbers its own, so that they are new to both.
        let mutable nextRestId = max restFrom (max firstMadeUpRest (highestVarId constraints + 1))
        let freshRest () =
            let var = TyVar (TyVar.ordinary nextRestId None)
            nextRestId <- nextRestId + 1
            var
        // Whether `attempt`, a trial of more solving, succeeds, leaving this solve's state as it was before it: a trial
        // must not show in the order the constraints were solved, nor take the made-up rests the solving goes on with.
        let fitsWithoutEffect (attempt: unit -> Result<'a, 'b>) =
            let taken, putOffBefore, nextBefore = restTaken, putOff, nextRestId
            let fits = (attempt ()).IsOk
            restTaken <- taken
            putOff <- putOffBefore
            nextRestId <- nextBefore
            fits
        // A tag's payload that is a closed union, open to more tags through a rest made up for it (#679).
        let openedPayload (payload: Ty) =
            match payload, parseTagsOf payload with
            | TyUnion items, Ok (_, None) -> Some (TyUnion(items @ [ freshRest () ]))
            | TyTag _, Ok (_, None) -> Some (TyUnion [ payload; freshRest () ])
            | _ -> None
        // Where to report that none of a union's members could be the type given for it, at the constraint from
        // `origin`, given how trying each one failed, in the order tried. A member is tried against the constraints
        // after it too, so one whose failure is elsewhere fitted here, and what failed is a constraint further on,
        // which may be in another def altogether: that is reported, not this constraint (`solve` would otherwise set
        // this one aside as wrong too). Only when every member failed here is the last one's failure reported.
        let unionFailure (origin: SourceSpan) (failures: Mismatch list) =
            failures
            |> List.tryFind (fun (_, span, _) -> span <> origin)
            |> Option.orElse (List.tryLast failures)
        // The pairs of references to recursive aliases being compared by what they stand for (see `unfoldBoth`).
        let assumed = HashSet<Ty * Ty>(HashIdentity.Structural)
        // The name of the alias each type compared was written as, by the type it stands for: `P` for a record of `P`'s
        // fields, so a misspelled field can say which record it is not a field of.
        let aliasNames = Dictionary<Ty, string>(HashIdentity.Structural)
        let unfoldAlias (ty: Ty) =
            let underlying = RecursiveAliases.underlying ty
            aliasNames[underlying] <- tyToString ty
            underlying
        // Where a chain of variables each solved to the next ends, in `start`, by the first variable of the chain: a
        // variable that is not solved there to another. Each constraint of an else-if joins a branch to the next, so
        // the solve its constraints were given to makes each branch's variable stand for the one before it, and
        // looking up the last walks every branch (#2362). A chain is walked once and each variable on it remembered.
        // The solving below only solves variables that are not solved yet, and goes back no further than `start`
        // when a trial fails, so the substitution it is at always holds `start`, and a chain in `start` ends where it
        // did.
        let chainEnds = Dictionary<int, TyVar>()
        let chainEnd (first: TyVar) =
            let walked = ResizeArray<int>()
            let rec walk (var: TyVar) =
                match chainEnds.TryGetValue var.Id with
                | true, known -> known
                | false, _ ->
                    match start.TryFind var.Id with
                    | Some (TyVar next) when next.Id <> var.Id ->
                        walked.Add var.Id
                        walk next
                    | _ -> var
            let last = walk first
            for id in walked do
                chainEnds[id] <- last
            last
        // What `var` stands for under `subst`, as `apply` gives it, with a chain of `start` followed to its end at once.
        let rec resolveIn (subst: Subst) (var: TyVar) =
            let var = chainEnd var
            match subst.TryFind var.Id with
            | Some replacement when replacement = TyVar var -> replacement
            | Some replacement -> applyResolving (resolveIn subst) replacement
            | None -> TyVar var
        let applyIn (subst: Subst) (ty: Ty) = applyResolving (resolveIn subst) ty
        let rec loop (subst: Subst) (remaining: ConstraintSet) =
            // A closed record has exactly its fields, so two closed records agree only when they have the same ones:
            // a record with a field more or a field fewer is a different type.
            let unifyRecordFields origin (lFields: Map<string, Ty>) (rFields: Map<string, Ty>) rest left right =
                let lKeys = keySet lFields
                let rKeys = keySet rFields
                if lKeys = rKeys then
                    let pairConstraints =
                        lKeys |> Seq.map (fun key -> (lFields.[key], rFields.[key], Equal, origin)) |> Seq.toList
                    loop subst (pairConstraints @ rest)
                else
                    let kind =
                        match Set.toList (Set.difference lKeys rKeys), Set.toList (Set.difference rKeys lKeys) with
                        | [ onlyLeft ], [ onlyRight ] when NameSuggestions.closest [ onlyRight ] onlyLeft = [ onlyRight ] ->
                            let named ty = match aliasNames.TryGetValue ty with | true, name -> name | _ -> $"'{tyToString ty}'"
                            MisspelledField(onlyLeft, named right, onlyRight, named left)
                        | _ -> TypesDiffer
                    Error (recordKeyMessage lKeys rKeys left right, origin, kind)
            // Two references to recursive aliases are compared by what they stand for, taking them to agree while that
            // is checked: unfolded, they may hold the same two references again, which agree if nothing else differs.
            let unfoldBoth kind origin l r rest =
                if assumed.Contains((l, r)) then
                    loop subst rest
                else
                    assumed.Add((l, r)) |> ignore
                    let result = loop subst ((RecursiveAliases.underlying l, RecursiveAliases.underlying r, kind, origin) :: rest)
                    assumed.Remove((l, r)) |> ignore
                    result
            // `exact` when the two types are to be the same (an `Equal` constraint), and not only one given where the
            // other is expected. A lone tag is then the same as an open union only when the union's rest adds nothing:
            // `#notFound` is `#notFound | r` with `r` empty. A rest an annotation names is left alone, as the
            // annotation says it may be more.
            let unifyEqual (exact: bool) origin l r rest =
                let emptyRest (restVar: TyVar option) =
                    match restVar with
                    | Some restVar when exact && restVar.Name.IsNone -> [ (TyVar restVar, TyUnion [], Equal, origin) ]
                    | _ -> []
                match l, r with
                // A variable a call's scope meets becomes the scope, which stays the call's (see
                // `FunctionContexts.isCallScope`).
                | TyVar _, TyVar v when FunctionContexts.isCallScope l && not (FunctionContexts.isCallScope r) ->
                    loop (subst |> Map.add v.Id l) rest
                // Of two variables inference made up, the newer stands for the older, so a variable that many others
                // stand for stays where they point: in `1 + 1 + ... + 1`, each operand's would otherwise stand for the
                // next one's, and resolving the first would walk the whole chain (#839).
                | TyVar older, TyVar v
                | TyVar v, TyVar older when isPlainVar older && isPlainVar v && v.Id > older.Id ->
                    loop (subst |> Map.add v.Id (TyVar older)) rest
                | TyVar v, ty
                | ty, TyVar v ->
                    if occurs v.Id ty then
                        Error ((fun tyToString -> $"'{tyToString (TyVar v)}' vs '{tyToString ty}', which contains it, so the type would be infinite"), origin, TypesDiffer)
                    else
                        if madeUpBefore v then restTaken <- true
                        let newSubst = subst |> Map.add v.Id ty
                        loop newSubst rest
                // A union of only a rest is that rest, as a context variable solved to make room for more is: met again
                // where a function's contexts are fitted, it takes them (#1908).
                | TyUnion [ TyVar _ as var ], other
                | other, TyUnion [ TyVar _ as var ] ->
                    loop subst ((var, other, Equal, origin) :: rest)
                // Two references to one recursive alias are the same type when their arguments are: unfolded, each
                // would hold the same two references again.
                | RecursiveAliases.RecursiveAlias(lBody, lArgs), RecursiveAliases.RecursiveAlias(rBody, rArgs) when lBody = rBody ->
                    loop subst ((List.map2 (fun lTy rTy -> (lTy, rTy, Equal, origin)) lArgs rArgs) @ rest)
                | RecursiveAliases.RecursiveAlias _, RecursiveAliases.RecursiveAlias _ -> unfoldBoth Equal origin l r rest
                // An alias is the type it stands for.
                | TyAlias _, _ ->
                    loop subst ((unfoldAlias l, r, Equal, origin) :: rest)
                | _, TyAlias _ ->
                    loop subst ((l, unfoldAlias r, Equal, origin) :: rest)
                | TyPrimitive lName, TyPrimitive rName when lName = rName ->
                    loop subst rest
                | TyApply(lName, lArgs), TyApply(rName, rArgs) when lName = rName && lArgs.Length = rArgs.Length ->
                    let pairs = List.zip lArgs rArgs
                    let pairConstraints = pairs |> List.map (fun (lTy, rTy) -> (lTy, rTy, Equal, origin))
                    loop subst (pairConstraints @ rest)
                | TyScope lScope, TyScope rScope -> loop subst ((lScope, rScope, Equal, origin) :: rest)
                // Two measured values are of one type when they are of one dimension and their storages are one type. A
                // measured value is never of a type of another kind, its storage included.
                | TyMeasure(lDimension, lStorage), TyMeasure(rDimension, rStorage) when lDimension = rDimension ->
                    loop subst ((lStorage, rStorage, Equal, origin) :: rest)
                | TyMeasure _, _
                | _, TyMeasure _ -> Error (mismatchMessage l r, origin, TypesDiffer)
                // One nominal type is the same as another when their arguments are, whether its parameters are used
                // in what it is made from or not; that is then the same too. What one that names itself is made from
                // holds the same arguments again (see `RecursiveNominals`).
                | TyNominal(lNominal, lArgs, lUnderlying, lPrivate), TyNominal(rNominal, rArgs, rUnderlying, rPrivate) ->
                    if lNominal = rNominal && lArgs.Length = rArgs.Length then
                        // A value whose contexts nothing has settled yet, as a constructor's, carries the other's, as
                        // where it is given (see the nominal case of assignability): made the same as another such, the
                        // two would otherwise share a made-up variable.
                        // Settled contexts agree as two function types' do, and a mismatch is reported as the two
                        // nominal types', not as the function type that carries them (#1677).
                        let argConstraints =
                            List.map2
                                (fun lTy rTy ->
                                    match lTy, rTy with
                                    | FunctionContexts.Carried [ TyVar unsettled ], FunctionContexts.Carried other
                                    | FunctionContexts.Carried other, FunctionContexts.Carried [ TyVar unsettled ] when isPlainVar unsettled ->
                                        Ok [ (TyVar unsettled, TyUnion other, Equal, origin) ]
                                    | FunctionContexts.Carried lContexts, FunctionContexts.Carried rContexts ->
                                        FunctionContexts.agree freshRest origin lContexts rContexts
                                        |> Result.mapError (fun (onlyLeft, onlyRight) -> carriedMismatchMessage onlyLeft onlyRight l r)
                                    | _ -> Ok [ (lTy, rTy, Equal, origin) ])
                                lArgs rArgs
                        match argConstraints |> List.tryPick (function Error message -> Some message | Ok _ -> None) with
                        | Some message -> Error (message, origin, ContextsNotAllowed)
                        | None ->
                        let argConstraints = argConstraints |> List.collect (function Ok constraints -> constraints | Error _ -> [])
                        match lUnderlying, rUnderlying with
                        | _ when lPrivate || rPrivate -> loop subst (argConstraints @ rest)
                        | TyRecursive _, _
                        | _, TyRecursive _ -> loop subst (argConstraints @ rest)
                        | _ -> loop subst (argConstraints @ (lUnderlying, rUnderlying, Equal, origin) :: rest)
                    else
                        Error (mismatchMessage l r, origin, TypesDiffer)
                // A nominal type is a different type from its shape. Only assignability, which is one-way, lets a
                // nominal value stand for its shape.
                | TyNominal _, _
                | _, TyNominal _ ->
                    Error (mismatchMessage l r, origin, TypesDiffer)
                | TyFunc(lArg, lRet, lContexts), TyFunc(rArg, rRet, rContexts) ->
                    match FunctionContexts.agree freshRest origin (FunctionContexts.withCellMemory lArg lContexts) (FunctionContexts.withCellMemory rArg rContexts) with
                    | Ok contexts -> loop subst ((lArg, rArg, Equal, origin) :: (lRet, rRet, Equal, origin) :: contexts @ rest)
                    | Error (onlyLeft, onlyRight) -> Error (contextsMessage onlyLeft onlyRight l r, origin, TypesDiffer)
                | TyRecord(lFields, _), TyRecord(rFields, _) ->
                    unifyRecordFields origin lFields rFields rest l r
                // An open record is a record with at least its fields: the record must have each of them, and the
                // row is the fields it has besides.
                | TyOpenRecord(openFields, row), TyRecord(fields, order)
                | TyRecord(fields, order), TyOpenRecord(openFields, row) ->
                    let openKeys = keySet openFields
                    if Set.isSubset openKeys (keySet fields) then
                        let pairs = openKeys |> Seq.map (fun key -> (openFields.[key], fields.[key], Equal, origin)) |> Seq.toList
                        let others = fields |> Map.filter (fun key _ -> not (openKeys.Contains key))
                        loop subst (pairs @ [ (TyVar row, TyRecord(others, FieldOrder.within others order), Equal, origin) ] @ rest)
                    else
                        // The message names each side's own fields, whichever of the two is the open one.
                        let lKeys, rKeys =
                            match l with
                            | TyOpenRecord _ -> openKeys, keySet fields
                            | _ -> keySet fields, openKeys
                        Error (recordKeyMessage lKeys rKeys l r, origin, TypesDiffer)
                // Two open records: each row is the other's extra fields and a row they share, as with two open unions.
                | TyOpenRecord(lFields, lRow), TyOpenRecord(rFields, rRow) ->
                    let lKeys = keySet lFields
                    let rKeys = keySet rFields
                    let pairs =
                        Set.intersect lKeys rKeys
                        |> Seq.map (fun key -> (lFields.[key], rFields.[key], Equal, origin))
                        |> Seq.toList
                    if lRow.Id = rRow.Id then
                        if lKeys = rKeys then loop subst (pairs @ rest)
                        else Error (recordKeyMessage lKeys rKeys l r, origin, TypesDiffer)
                    else
                        let shared = match freshRest () with TyVar v -> v | _ -> failwith "unreachable"
                        let rowOf (extras: Map<string, Ty>) =
                            if extras.IsEmpty then TyVar shared else TyOpenRecord(extras, shared)
                        let extraRight = rFields |> Map.filter (fun key _ -> not (lKeys.Contains key))
                        let extraLeft = lFields |> Map.filter (fun key _ -> not (rKeys.Contains key))
                        loop subst (pairs @ [ (TyVar lRow, rowOf extraRight, Equal, origin); (TyVar rRow, rowOf extraLeft, Equal, origin) ] @ rest)
                | TyTag(lName, lPayload), TyTag(rName, rPayload) when lName = rName ->
                    match lPayload, rPayload with
                    | None, None -> loop subst rest
                    | Some lTy, Some rTy -> loop subst ((lTy, rTy, Equal, origin) :: rest)
                    | _ -> Error ((fun tyToString -> $"Tag payload mismatch: '{tyToString l}' vs '{tyToString r}', where only one of them gives #{lName} a payload"), origin, TypesDiffer)
                | TyUnion lItems, TyUnion rItems ->
                    match parseTagUnion lItems, parseTagUnion rItems with
                    | Ok (lMap, lRest), Ok (rMap, rRest) ->
                        let lKeys = lMap |> Map.keys |> Set.ofSeq
                        let rKeys = rMap |> Map.keys |> Set.ofSeq
                        let extraLeft = Set.difference lKeys rKeys
                        let extraRight = Set.difference rKeys lKeys
                        let commonKeys = Set.intersect lKeys rKeys
                        let pairs =
                            commonKeys
                            |> Seq.map (fun key -> (lMap.[key], rMap.[key]))
                            |> Seq.toList
                            |> List.map (fun (lTy, rTy) -> (lTy, rTy, Equal, origin))
                        let restConstraints =
                            match lRest, rRest with
                            // Two open unions: each rest is the other's extra tags and a rest they share, so both
                            // can still grow alike (`#a | r1` and `#b | r2` are `#a | #b | r3`).
                            | Some lVar, Some rVar ->
                                let shared = freshRest ()
                                let restOf (extras: Set<string>) (tags: Map<string, Ty>) =
                                    if extras.IsEmpty then shared
                                    else TyUnion((extras |> Seq.map (fun key -> tags.[key]) |> Seq.toList) @ [ shared ])
                                [ (TyVar lVar, restOf extraRight rMap, Equal, origin); (TyVar rVar, restOf extraLeft lMap, Equal, origin) ]
                            | _ ->
                                [ match lRest with
                                  | Some restVar when not extraRight.IsEmpty ->
                                      let extraTags = extraRight |> Seq.map (fun key -> rMap.[key]) |> Seq.toList
                                      yield (TyVar restVar, TyUnion extraTags, Equal, origin)
                                  | _ -> yield! emptyRest lRest
                                  match rRest with
                                  | Some restVar when not extraLeft.IsEmpty ->
                                      let extraTags = extraLeft |> Seq.map (fun key -> lMap.[key]) |> Seq.toList
                                      yield (TyVar restVar, TyUnion extraTags, Equal, origin)
                                  | _ -> yield! emptyRest rRest ]
                        if lRest.IsNone && rRest.IsNone then
                            if extraLeft.IsEmpty || extraRight.IsEmpty then
                                loop subst (pairs @ rest)
                            else
                                Error (tagUnionMessage extraLeft extraRight l r, origin, TypesDiffer)
                        else if (lRest.IsSome || extraRight.IsEmpty) && (rRest.IsSome || extraLeft.IsEmpty) then
                            loop subst (pairs @ restConstraints @ rest)
                        else
                            // An open union can take the other's extra tags; only a closed one's are named.
                            let onlyLeft = if rRest.IsNone then extraLeft else Set.empty
                            let onlyRight = if lRest.IsNone then extraRight else Set.empty
                            Error (tagUnionMessage onlyLeft onlyRight l r, origin, TypesDiffer)
                    | Error message, _ -> Error ((fun _ -> message), origin, TypesDiffer)
                    | _, Error message -> Error ((fun _ -> message), origin, TypesDiffer)
                | TyUnion lItems, ty ->
                    match parseTagUnion lItems with
                    | Ok (lMap, lRest) ->
                        let tags = lMap |> Map.toList |> List.map snd
                        // A member of the union that unifies with `ty` is tried against the rest of the
                        // constraints too, so when none works the failure may come from later on (see
                        // `unionFailure`) rather than be a bare mismatch here.
                        let rec tryItems items (failures: Mismatch list) =
                            match items with
                            | [] ->
                                // The rest takes a tag the union does not name; one it names is that member, which
                                // did not unify (`#some(int)` is not the rest of `#some() | r`).
                                match lRest, tagNotInUnion l lItems lMap lRest ty with
                                | Some restVar, _ when isTagLike ty && not (lMap.ContainsKey (tagUnionKey ty)) -> loop subst ((TyVar restVar, ty, Equal, origin) :: rest)
                                | _, Some message -> Error (message, origin, TypesDiffer)
                                | _ -> Error (unionFailure origin failures |> Option.defaultValue (mismatchMessage l r, origin, TypesDiffer))
                            | item :: restItems ->
                                match loop subst ((item, ty, Equal, origin) :: emptyRest lRest @ rest) with
                                | Ok result -> Ok result
                                | Error inner -> tryItems restItems (failures @ [ inner ])
                        tryItems tags []
                    | Error message -> Error ((fun _ -> message), origin, TypesDiffer)
                | ty, TyUnion rItems ->
                    match parseTagUnion rItems with
                    | Ok (rMap, rRest) ->
                        let tags = rMap |> Map.toList |> List.map snd
                        let rec tryItems items (failures: Mismatch list) =
                            match items with
                            | [] ->
                                match rRest, tagNotInUnion r rItems rMap rRest ty with
                                | Some restVar, _ when isTagLike ty && not (rMap.ContainsKey (tagUnionKey ty)) -> loop subst ((ty, TyVar restVar, Equal, origin) :: rest)
                                | _, Some message -> Error (message, origin, TypesDiffer)
                                | _ -> Error (unionFailure origin failures |> Option.defaultValue (mismatchMessage l r, origin, TypesDiffer))
                            | item :: restItems ->
                                match loop subst ((ty, item, Equal, origin) :: emptyRest rRest @ rest) with
                                | Ok result -> Ok result
                                | Error inner -> tryItems restItems (failures @ [ inner ])
                        tryItems tags []
                    | Error message -> Error ((fun _ -> message), origin, TypesDiffer)
                | Chars.CharType, TyPrimitive "string"
                | TyPrimitive "string", Chars.CharType -> Error (mismatchMessage l r, origin, CharAndString)
                | _ -> Error (mismatchMessage l r, origin, TypesDiffer)
            match remaining with
            | [] -> Ok subst
            | (left, right, kind, origin) :: rest ->
                let l = applyIn subst left
                let r = applyIn subst right
                if l = r then
                    loop subst rest
                // A variable takes the error type, as it would any other; anything else agrees with it. What a call to
                // a value that failed to check gives back failed to check too: the variables of a function type's
                // result take the error type, so that a function the result is passed to finds it has every context.
                elif (isError l || isError r) && not (match l, r with TyVar _, _ | _, TyVar _ -> true | _ -> false) then
                    match (if isError l then r else l) with
                    | TyFunc(_, output, _) ->
                        let resultVars = varOccurrences output |> List.distinct |> List.map (fun v -> TyVar v, errorType, Equal, origin)
                        loop subst (resultVars @ rest)
                    | _ -> loop subst rest
                // `undefined`, the type of what never gives a value, agrees with anything expected of it where types
                // must be the same, as the error type does: nothing follows it at run time, so `[crash("no")]` is a
                // `list(int)` where one is expected. What a call to it gives back never comes either. Only that way:
                // a value is not an `undefined`.
                elif kind = Equal && isBottom l && not (match r with TyVar _ -> true | _ -> false) then
                    match r with
                    | TyFunc(_, output, _) ->
                        let resultVars = varOccurrences output |> List.distinct |> List.map (fun v -> TyVar v, bottomType, Equal, origin)
                        loop subst (resultVars @ rest)
                    | _ -> loop subst rest
                else
                    match kind with
                    | Assignable | AssignableLater ->
                        match l, r with
                        // The bottom type is assignable to every type, and binds no variable (see `bottomType`).
                        | TyUnion [], _ ->
                            loop subst rest
                        // A tag union given where a type not known yet is expected, or the reverse, is solved after the
                        // other constraints: solved now, the variable would be made equal to the union before a `try`
                        // widens it with another failure (#354). Constraints are otherwise solved newest first, so
                        // this changes their order.
                        | _ when kind = Assignable && isPendingUnion l r ->
                            putOff <- (left, right, kind, origin) :: putOff
                            loop subst (rest @ [ (left, right, AssignableLater, origin) ])
                        | TyVar _, _
                        | _, TyVar _ ->
                            unifyEqual false origin l r rest
                        // A type's arguments may be read in a function's input, so two references to recursive aliases
                        // are compared by what they stand for (see `unfoldBoth`).
                        | RecursiveAliases.RecursiveAlias _, RecursiveAliases.RecursiveAlias _ -> unfoldBoth Assignable origin l r rest
                        | TyAlias _, _ ->
                            loop subst ((unfoldAlias l, r, Assignable, origin) :: rest)
                        | _, TyAlias _ ->
                            loop subst ((l, unfoldAlias r, Assignable, origin) :: rest)
                        // A closed union given where an open one is expected widens it: the tags it has that the
                        // expected union does not name go into the expected union's rest, which stays open for
                        // more. So `#some(string) | #nil` and then `#some(int) | #notFound`, given in turn to a
                        // `#some(a) | r` whose `r` they share, leave `r` as `#nil | #notFound | r'`. A lone tag is a
                        // closed union of one (#353).
                        | (TyUnion _ | TyTag _), TyUnion rItems when
                            (match parseTagsOf l, parseTagUnion rItems with
                             | Ok (_, None), Ok (_, Some _) -> true
                             | _ -> false) ->
                            match parseTagsOf l, parseTagUnion rItems with
                            | Ok (lMap, _), Ok (rMap, Some restVar) ->
                                let pairs =
                                    lMap
                                    |> Map.toList
                                    |> List.choose (fun (key, lTy) -> rMap |> Map.tryFind key |> Option.map (fun rTy -> (lTy, rTy, Assignable, origin)))
                                let extraTags = lMap |> Map.toList |> List.filter (fun (key, _) -> not (rMap.ContainsKey key)) |> List.map snd
                                // A tag the rest takes whose payload is a closed union takes it open to more, as a
                                // payload variable given one does (see below), so that a `try` on a step failing with
                                // `#error(#notFound)` and one on a step failing with `#error(#invalidInt)` fail with
                                // `#error(#notFound | #invalidInt)` (#679).
                                let opened (tag: Ty) =
                                    match tag with
                                    | TyTag(name, Some payload) -> openedPayload payload |> Option.map (fun payload -> TyTag(name, Some payload)) |> Option.defaultValue tag
                                    | _ -> tag
                                let restConstraints =
                                    if extraTags.IsEmpty then [] else [ (TyVar restVar, TyUnion((extraTags |> List.map opened) @ [ freshRest () ]), Equal, origin) ]
                                loop subst (pairs @ restConstraints @ rest)
                            | _ -> unifyEqual false origin l r rest
                        // A union is assignable to a closed union or a tag only if the expected type names each of its
                        // tags: `#some(int) | #odd` is not a `#some(int)`, though as types that must be equal each is
                        // taken to be the other's member (#354). An open union's rest can then have only the expected
                        // type's tags: those it names too, as a rest shared with a branch can repeat them, and the
                        // others. So a `try` that widens it later with another tag is a mismatch. A rest an
                        // annotation names takes only the others, as it did when this was solved as equal.
                        | TyUnion lItems, (TyUnion _ | TyTag _) when
                            (match parseTagUnion lItems, parseTagsOf r with
                             | Ok _, Ok (_, None) -> true
                             | _ -> false) ->
                            match parseTagUnion lItems, parseTagsOf r with
                            | Ok (lMap, lRest), Ok (rMap, _) when lMap |> Map.forall (fun key _ -> rMap.ContainsKey key) ->
                                let pairs = lMap |> Map.toList |> List.map (fun (key, lTy) -> (lTy, rMap.[key], Assignable, origin))
                                let extraTags = rMap |> Map.toList |> List.filter (fun (key, _) -> not (lMap.ContainsKey key)) |> List.map snd
                                let restConstraints =
                                    match lRest with
                                    | Some restVar when restVar.Name.IsNone -> [ (TyVar restVar, TyUnion(rMap |> Map.toList |> List.map snd), Equal, origin) ]
                                    | Some restVar when not extraTags.IsEmpty -> [ (TyVar restVar, TyUnion extraTags, Equal, origin) ]
                                    | _ -> []
                                loop subst (pairs @ restConstraints @ rest)
                            // A rest this closed can repeat a tag, which the message leaves out.
                            | _, Ok (rMap, _) -> Error (mismatchMessage l (TyUnion(rMap |> Map.toList |> List.map snd)), origin, TypesDiffer)
                            | _ -> Error (mismatchMessage l r, origin, TypesDiffer)
                        // A lone tag is given for the member of a closed union that names it, as a union's tags are.
                        | TyTag(name, _), TyUnion rItems when
                            (match parseTagUnion rItems with
                             | Ok (rMap, None) -> rMap.ContainsKey name
                             | _ -> false) ->
                            match parseTagUnion rItems with
                            | Ok (rMap, _) -> loop subst ((l, rMap.[name], Assignable, origin) :: rest)
                            | Error message -> Error ((fun _ -> message), origin, TypesDiffer)
                        // Assignability reaches into what types are built from, so a nominal type stays one-way
                        // there too: `list(Person)` is assignable to `list((name: string))`, but not the reverse.
                        // Values are immutable, so a type's arguments are covariant, and so are a record's fields:
                        // the arguments of `f(a, b)` are one tuple, each item given where its parameter is
                        // expected. A function's argument is contravariant, since the function is given values of
                        // the type it expects.
                        // A closed union given for a tag's payload that is a variable inference made up makes it that
                        // union open to more, through a rest made up for it, so that another closed union given for it
                        // later widens it to their join, as a union's rest is widened (#679): `Result.bind` chains a
                        // step that fails with `#notFound` and one that fails with `#invalidInt`, and its error is
                        // `#notFound | #invalidInt`. The rest is closed where the def is generalised (see
                        // `outputOnlyRestVars`). A payload opened so is given what comes for it; any other union or tag
                        // is the same type as the one given, as a match's arms need: they close the rest their union
                        // has for a payload. A payload of another type is given where the other is expected, as an
                        // argument of a type is: the rest of a stream, `#next(a, [d] seq(a))`, can carry fewer contexts
                        // than the stream that gives it (#1908).
                        | TyTag(lName, Some lPayload), TyTag(rName, Some rPayload) when lName = rName ->
                            match rPayload with
                            | TyVar v when isPlainVar v ->
                                loop subst ((rPayload, openedPayload lPayload |> Option.defaultValue lPayload, Equal, origin) :: rest)
                            | TyUnion items when items |> List.exists (function TyVar v -> isMadeUpRest v | _ -> false) ->
                                loop subst ((lPayload, rPayload, Assignable, origin) :: rest)
                            | TyUnion _
                            | TyTag _ -> loop subst ((lPayload, rPayload, Equal, origin) :: rest)
                            | _ -> loop subst ((lPayload, rPayload, Assignable, origin) :: rest)
                        | TyRecord(lFields, _), TyRecord(rFields, _) when keySet lFields = keySet rFields ->
                            let pairs = keySet lFields |> Seq.map (fun key -> (lFields.[key], rFields.[key], Assignable, origin)) |> Seq.toList
                            loop subst (pairs @ rest)
                        | TyApply(lName, lArgs), TyApply(rName, rArgs) when lName = rName && lArgs.Length = rArgs.Length ->
                            loop subst ((List.map2 (fun lTy rTy -> (lTy, rTy, Assignable, origin)) lArgs rArgs) @ rest)
                        | TyScope lScope, TyScope rScope -> loop subst ((lScope, rScope, Assignable, origin) :: rest)
                        // A function that requires fewer contexts fits where one that requires more is expected.
                        // One that requires none, where the only variable among the contexts expected is a rest made
                        // up to keep room for more, leaves the rest as it is: fitted, it would stand for another made up
                        // for the same room, and a rest made up before that takes more makes the whole list solved
                        // again, so a def of many such calls was checked in quadratic time (#2551).
                        | TyFunc(lArg, lRet, lContexts), TyFunc(rArg, rRet, rContexts) when
                            List.isEmpty (FunctionContexts.withCellMemory lArg lContexts)
                            && (match FunctionContexts.withCellMemory rArg rContexts |> List.filter (function TyVar _ -> true | _ -> false) with
                                | [ TyVar rest ] -> isMadeUpRest rest
                                | _ -> false) ->
                            loop subst ((rArg, lArg, Assignable, origin) :: (lRet, rRet, Assignable, origin) :: rest)
                        | TyFunc(lArg, lRet, lContexts), TyFunc(rArg, rRet, rContexts) ->
                            match FunctionContexts.fit freshRest origin (FunctionContexts.withCellMemory lArg lContexts) (FunctionContexts.withCellMemory rArg rContexts) with
                            // The contexts are solved before the result, which may carry them too: fitted first, a
                            // result's `[d] seq(b)` would solve the variable the contexts' fit then solves again.
                            | Ok contexts -> loop subst ((rArg, lArg, Assignable, origin) :: contexts @ (lRet, rRet, Assignable, origin) :: rest)
                            | Error extra -> Error (contextsNotAllowedMessage extra l r, origin, ContextsNotAllowed)
                        | _ ->
                        match l, r with
                        // A nominal type's arguments are its own, not only what it is made from, so they are the same. The
                        // contexts a type generic in contexts carries are like a function type's: one with fewer fits where
                        // one with more is expected (see `FunctionContexts.carried`).
                        | TyNominal(lNominal, lArgs, _, _), TyNominal(rNominal, rArgs, _, _) when lNominal = rNominal && lArgs.Length = rArgs.Length ->
                            let equalArguments lArgs rArgs = List.map2 (fun lTy rTy -> (lTy, rTy, Equal, origin)) lArgs rArgs
                            match l, r with
                            | FunctionContexts.CarryingNominal(_, lOwn, given), FunctionContexts.CarryingNominal(_, rOwn, expected) ->
                                let arguments = equalArguments lOwn rOwn
                                match given, expected with
                                // A value whose contexts nothing has settled yet, as a constructor's, carries those
                                // expected: its functions were checked against them where it was made, and may require
                                // fewer.
                                | [ TyVar given ], expected when isPlainVar given ->
                                    loop subst (arguments @ (TyVar given, TyUnion expected, Equal, origin) :: rest)
                                // Contexts that are only variables nothing has settled yet, given where a signature's are
                                // expected, are fitted after the other constraints, which settle them: fitted now, the
                                // signature's first variable would take them all and its others none (#1908).
                                | _ :: _, expected when
                                    kind = Assignable
                                    && given |> List.forall (function TyVar v -> isPlainVar v | _ -> false)
                                    && expected |> List.exists (function TyVar v -> v.Name.IsSome | _ -> false) ->
                                    putOff <- (left, right, kind, origin) :: putOff
                                    loop subst (rest @ [ (left, right, AssignableLater, origin) ])
                                | given, expected ->
                                    match FunctionContexts.fit freshRest origin given expected with
                                    | Ok contexts -> loop subst (arguments @ contexts @ rest)
                                    | Error extra -> Error (carriedNotAllowedMessage extra l r, origin, ContextsNotAllowed)
                            | _ -> loop subst (equalArguments lArgs rArgs @ rest)
                        // A value of a nominal type's shape is no value of it, but the mismatch says which type's shape
                        // it has, so that the error can say why (see `ProtectedShapes`). Trying the shape changes
                        // nothing the solving goes on with.
                        | other, RecursiveNominals.Nominal(nominal, _, underlying, isPrivate) when
                            not (match other with TyNominal _ -> true | _ -> false)
                            && fitsWithoutEffect (fun () -> loop subst [ (other, underlying, Assignable, origin) ]) ->
                            Error (mismatchMessage l r, origin, NominalShape(nominal, isPrivate, underlying))
                        | TyNominal(_, _, _, true), _
                        | _, TyNominal(_, _, _, true) ->
                            Error (mismatchMessage l r, origin, TypesDiffer)
                        | RecursiveNominals.Nominal(_, _, underlying, false), other when not (match other with TyNominal _ -> true | _ -> false) ->
                            loop subst ((underlying, other, Equal, origin) :: rest)
                        | other, TyNominal(_, _, _, false) when not (match other with TyNominal _ -> true | _ -> false) ->
                            Error (mismatchMessage l r, origin, TypesDiffer)
                        | TyNominal _, TyNominal _ ->
                            Error (mismatchMessage l r, origin, TypesDiffer)
                        | _ ->
                            unifyEqual false origin l r rest
                    | Equal ->
                        unifyEqual true origin l r rest
        loop start constraints
        |> Result.map (fun subst ->
            let order =
                match restTaken, putOff with
                | true, _ -> Shown
                | false, [] -> Hidden
                | false, putOff -> PutOff putOff
            subst, nextRestId, order)

    /// `subst` with each variable named in `declared`, the types defs declare, standing for the made-up variable it
    /// was solved to, rather than the reverse, so a reader sees the name written. Solving binds whichever of two
    /// variables comes first in a constraint: `f(x)`, where `f` is an `a -> b` and `x` a pattern's variable, binds `a`
    /// to `x`'s. The same types are equal either way; only which one's name is shown differs.
    let keepWrittenNames (declared: Ty seq) (subst: Subst) : Subst =
        let named =
            declared
            |> Seq.toList
            |> List.collect varOccurrences
            |> List.filter (fun v -> v.Name.IsSome)
            |> List.distinctBy (fun v -> v.Id)
        named
        |> List.fold (fun (acc: Subst) v ->
            match apply acc (TyVar v) with
            | TyVar madeUp when madeUp.Name.IsNone && madeUp.Id <> v.Id ->
                acc |> Map.remove v.Id |> Map.add madeUp.Id (TyVar v)
            | _ -> acc) subst
