namespace NyxCompiler

open System.Collections.Generic
open Parser.Program

/// Substitution map for type variables.
type Subst = Map<int, Ty>

/// What made a constraint fail: two types that differ, or a function that requires contexts given where one that
/// requires fewer is expected, which may be the same type otherwise.
type MismatchKind =
    | TypesDiffer
    | ContextsNotAllowed

/// A constraint that cannot be satisfied: what is wrong, where (the constraint's span), and what kind of failure it is.
type Mismatch = string * SourceSpan * MismatchKind

/// The characters a type shown to a reader is written in.
type TypeNotation =
    /// Type variables the compiler made up are `α`, `β`, `γ`, … through the Greek alphabet, and the rest of an open
    /// record is spread with an ellipsis: `(name: α, …)`.
    | Unicode
    /// Only ASCII: type variables are `a`, `b`, `c`, ... and a spread is three dots, `(name: a, ...)`.
    | Ascii

module Unifier =
    /// The type an alias stands for, looking through any number of aliases; any other type as it is. Only the
    /// outermost type is unwrapped, so aliases inside it keep their names.
    let rec unalias (ty: Ty) : Ty =
        match ty with
        | TyAlias(_, _, underlying) -> unalias underlying
        | _ -> ty

    /// The type of an expression that failed to check. It agrees with every type, so nothing that uses the
    /// expression is an error too: a variable unified with it becomes it, and from then on agrees with everything as
    /// well. It is a primitive no source can name, so every pass that handles types handles it without a case of its
    /// own; only a module with errors has it.
    let errorType = TyPrimitive "?error"

    /// Whether `ty` is `errorType`.
    let isError (ty: Ty) = unalias ty = errorType

    /// `undefined`, the bottom type: the union of no tags, which no value is. It is the type of an expression that
    /// never gives a value, such as a call to a function that stops the program. It is assignable to every type, so
    /// such an expression fits wherever a value is expected; nothing but itself is assignable to it, and an `Equal`
    /// constraint treats it as the empty union it is. Given where a variable is expected, it binds nothing: the
    /// variable takes what the other values given for it are.
    let bottomType = TyUnion []

    /// Whether `ty` is `bottomType`.
    let isBottom (ty: Ty) = unalias ty = bottomType

    /// Whether `var` is a rest variable `unify` made up when widening a union, or to keep room for more contexts in a
    /// context variable (see `FunctionContexts`). Its number depends on the constraints it was given, so a later
    /// `unify` of more constraints may give the same rest another, and a constraint naming it may name nothing then.
    let isMadeUpRest (var: TyVar) = var.Id >= 1_000_000_000

    /// The contexts of a function type that are written: not a made-up variable, which only keeps room for more
    /// contexts (see `FunctionContexts.fit`).
    let writtenContexts (contexts: Ty list) =
        contexts |> List.filter (function TyVar v -> not (isMadeUpRest v) | _ -> true)

    let private tryTupleLikeRecord (fields: Map<string, Ty>) : Ty list option * (string * Ty) list =
        let tupleFields, namedFields =
            fields
            |> Map.toList
            |> List.fold (fun (tupleAcc, namedAcc) (name, ty) ->
                match System.Int32.TryParse(name) with
                | true, index when index >= 0 ->
                    ((index, ty) :: tupleAcc, namedAcc)
                | _ -> (tupleAcc, (name, ty) :: namedAcc)) ([], [])
        let orderedTuple = tupleFields |> List.sortBy fst
        let expectedIndices = [0 .. orderedTuple.Length - 1]
        let hasAllIndices =
            orderedTuple
            |> List.map fst
            |> fun indices -> indices = expectedIndices
        if hasAllIndices && orderedTuple.Length > 0 then
            let tupleItems = orderedTuple |> List.map snd
            Some tupleItems, namedFields
        else
            None, namedFields

    /// `ty` with the fewest brackets the grammar allows. `,` binds tighter than `->`, which groups to the right,
    /// and `|` binds tighter than both, so a function needs brackets only where it is an item of a comma list
    /// (`(int, (a -> b))`), the input of another function (`(a -> b) -> c`) or a member of a union; a union never
    /// does. At the top, `signature` writes a function's parameters bare, as a def's annotation would:
    /// `int, (a -> b) -> a`. An open record's rest is spread with `spread`: `...` as in source, or `…` for a reader.
    let rec private printTy (spread: string) (signature: bool) (ty: Ty) : string =
        // A function, bracketed; in brackets its parameters are not bare.
        let grouped ty =
            match ty with
            | TyFunc _ -> $"({printTy spread false ty})"
            | _ -> printTy spread false ty
        // The items of a tuple, each bracketed where a comma list needs it.
        let items tys = tys |> List.map grouped |> String.concat ", "
        let bareTuple ty =
            match ty with
            | TyRecord fields ->
                match tryTupleLikeRecord fields with
                | Some tupleItems, [] -> Some (items tupleItems)
                | _ -> None
            | _ -> None
        // The only argument of an application or alias. A record of named fields is written in the application's
        // own brackets, as a tag's payload is: `list(name: string, ...)`. A tuple is not, as its items would read as
        // more arguments.
        let soleArgument ty =
            match ty with
            | TyRecord fields
            | TyOpenRecord(fields, _) when not fields.IsEmpty && (fst (tryTupleLikeRecord fields)).IsNone ->
                let written = printTy spread false ty
                written.Substring(1, written.Length - 2)
            | _ -> printTy spread false ty
        match ty with
        // The unit type is written `()`; `unit` is only its internal name.
        | TyPrimitive "unit" -> "()"
        // What failed to check could be anything. It is written as the hole `???` is, the placeholder for code not
        // written yet: a type still to be filled in.
        | TyPrimitive "?error" -> "???"
        | TyPrimitive name -> name
        // A type variable is written bare, as in an annotation: `(t, a) -> t`.
        | TyVar v ->
            match v.Name with
            | Some name -> name
            | None -> $"t{v.Id}"
        | TyNominal(nominal, _, _) -> nominal.Name
        // An alias is written by name, as the user wrote it: `Customer`, `Validator(int)`.
        | TyAlias(name, [], _) -> name
        | TyAlias(name, [ arg ], _) -> $"{name}({soleArgument arg})"
        | TyAlias(name, args, _) -> $"{name}({items args})"
        | TyFunc(arg, ret, contexts) ->
            let argText =
                match bareTuple arg with
                | Some bare when signature -> bare
                | _ -> grouped arg
            // A result that requires contexts is bracketed, so that its contexts read as its own.
            let retText =
                match ret with
                | TyFunc(_, _, _ :: _) -> $"({printTy spread false ret})"
                | _ -> printTy spread false ret
            match writtenContexts contexts with
            | [] -> $"{argText} -> {retText}"
            | written ->
                $"""[{written |> List.map (printTy spread false) |> String.concat ", "}] {argText} -> {retText}"""
        | TyRecord fields ->
            match tryTupleLikeRecord fields with
            | Some tupleItems, namedFields ->
                let tupleText = items tupleItems
                let namedText =
                    namedFields
                    |> List.sortBy fst
                    |> List.map (fun (name, fieldTy) -> $"{name}: {grouped fieldTy}")
                    |> String.concat ", "
                if namedText = "" then
                    $"({tupleText})"
                elif tupleText = "" then
                    $"({namedText})"
                else
                    $"({tupleText}, {namedText})"
            | None, _ ->
                fields
                |> Map.toList
                |> List.map (fun (name, fieldTy) -> $"{name}: {grouped fieldTy}")
                |> String.concat ", "
                |> fun inner -> $"({inner})"
        // An open record's other fields are written as a spread of its row: `(name: string, ...ω)`. A row named
        // nothing (see `displayRenaming`) is written as a bare spread: `(name: string, ...)`.
        | TyOpenRecord(fields, row) ->
            let rowText = $"{spread}{printTy spread false (TyVar row)}"
            match printTy spread false (TyRecord fields) with
            | "()" -> $"({rowText})"
            | closed -> $"{closed.Substring(0, closed.Length - 1)}, {rowText})"
        // Tags are written with their `#`, as in source: `#some(int) | #notFound`. A record payload's fields are
        // in the tag's own brackets: `#pair(int, string)`, `#authenticated(username: string)`.
        | TyTag(name, payloadOpt) ->
            match payloadOpt with
            | Some (TyRecord fields as payload) when not fields.IsEmpty ->
                let written = printTy spread false payload
                $"#{name}{written}"
            // The unit value is written as no payload in the brackets, as in source: `#some()`.
            | Some (TyPrimitive "unit") -> $"#{name}()"
            | Some payload -> $"#{name}({bareTuple payload |> Option.defaultWith (fun () -> printTy spread false payload)})"
            | None -> $"#{name}"
        // A context with no arguments is written by its name: `Console`.
        | TyApply(name, []) -> name
        | TyApply(name, [ arg ]) -> $"{name}({soleArgument arg})"
        | TyApply(name, args) -> $"{name}({items args})"
        // A union of no tags has no values: the bottom type.
        | TyUnion [] -> "undefined"
        // A rest named nothing (see `displayRenaming`) is written as a bare spread: `#some(int) | ...`.
        | TyUnion members ->
            members
            |> List.map (function TyPrimitive "" -> spread | item -> grouped item)
            |> String.concat " | "

    /// `ty` as it would be written in a def's annotation: see `printTy`.
    let tyToString (ty: Ty) : string = printTy "..." true ty

    /// The type variables of `ty` in the order `tyToString` prints them, each as many times as it is printed. A
    /// nominal's or alias's underlying type is not printed, so its variables are not included (an alias's arguments'
    /// are).
    let private varOccurrences (ty: Ty) : TyVar list =
        let rec collect ty =
            match ty with
            | TyVar v -> [ v ]
            | TyPrimitive _
            | TyNominal _ -> []
            | TyAlias(_, args, _) -> args |> List.collect collect
            | TyFunc(arg, ret, contexts) -> (writtenContexts contexts |> List.collect collect) @ collect arg @ collect ret
            | TyRecord fields ->
                match tryTupleLikeRecord fields with
                | Some tupleItems, namedFields ->
                    (tupleItems |> List.collect collect) @ (namedFields |> List.sortBy fst |> List.collect (snd >> collect))
                | None, _ -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyOpenRecord(fields, row) -> collect (TyRecord fields) @ [ row ]
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
            | TyApply(_, args) -> args |> List.collect collect
            | TyUnion items -> items |> List.collect collect
        collect ty

    /// The type variables of `ty` in the order `tyToString` prints them, each once.
    let private varsInPrintOrder (ty: Ty) : TyVar list =
        varOccurrences ty |> List.distinctBy (fun v -> v.Id)

    /// The module `origin` is, as a reader knows it: its file's name, or the standard library's module's.
    let private moduleText (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> $"'{System.IO.Path.GetFileName fullPath}'"
        | StandardLibraryModule name -> $"the standard library's '{name}'"
        | InMemory -> "this module"

    /// Says that two types are not the same. Two nominal types of the same name from different modules are written
    /// alike, so it says where each is defined.
    let private mismatchMessage left right =
        let homes =
            match left, right with
            | TyNominal(l, _, _), TyNominal(r, _, _) when l.Name = r.Name && l.Home <> r.Home ->
                $" They are different types: the first is defined in {moduleText l.Home}, the second in {moduleText r.Home}."
            | _ -> ""
        $"Type mismatch: {tyToString left} vs {tyToString right}.{homes}"

    /// Says which contexts make two function types differ: `onlyLeft` are those the first requires that the second has
    /// no room for, and `onlyRight` the reverse.
    let private contextsMessage (onlyLeft: Ty list) (onlyRight: Ty list) left right =
        let onlyIn which contexts =
            match contexts with
            | [] -> None
            | _ -> Some $"""only the {which} requires {contexts |> List.map (fun c -> $"'{tyToString c}'") |> String.concat ", "}"""
        let differences = [ onlyIn "first" onlyLeft; onlyIn "second" onlyRight ] |> List.choose id |> String.concat "; "
        $"Context mismatch: {differences}. {mismatchMessage left right}"

    /// Says that a function of type `given`, which requires `extra`, is given where one of type `expected` is, which
    /// has no room for them.
    let private contextsNotAllowedMessage (extra: Ty list) (given: Ty) (expected: Ty) =
        let contexts = extra |> List.map (fun c -> $"'{tyToString c}'") |> String.concat ", "
        let what = if extra.Length = 1 then "the context" else "the contexts"
        $"A function that requires {what} {contexts} is given where '{tyToString expected}' is expected, which does not: '{tyToString given}'. Write the contexts it may require on the type expected, as a context's member does in `log: [Console] string -> ()`, or a context variable for any, as in `[c] a -> b`."

    /// Says which tags make two unions differ: `onlyLeft` are tags only the first has that the second cannot take,
    /// and `onlyRight` the reverse. `Tag union mismatch: only the first has #bad; only the second has #underflow.`
    let private tagUnionMessage (onlyLeft: Set<string>) (onlyRight: Set<string>) left right =
        let onlyIn which tags =
            if Set.isEmpty tags then None
            else Some $"""only the {which} has {tags |> Set.toList |> List.sort |> List.map (sprintf "#%s") |> String.concat ", "}"""
        let differences = [ onlyIn "first" onlyLeft; onlyIn "second" onlyRight ] |> List.choose id |> String.concat "; "
        $"Tag union mismatch: {differences}. {mismatchMessage left right}"

    /// Says which fields only one of two records has: `{a, x} vs {x, y}: only the first has a; only the second has y.`
    let private recordKeyMessage leftKeys rightKeys left right =
        let keyList keys = keys |> Set.toList |> List.sort |> String.concat ", "
        let onlyIn which keys =
            if Set.isEmpty keys then None else Some $"only the {which} has {keyList keys}"
        let differences =
            [ onlyIn "first" (Set.difference leftKeys rightKeys); onlyIn "second" (Set.difference rightKeys leftKeys) ]
            |> List.choose id
            |> String.concat "; "
        $"Record field mismatch: {{{keyList leftKeys}}} vs {{{keyList rightKeys}}}: {differences}. {mismatchMessage left right}"

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

    /// Whether `ty` is given where `expected` is, and one is a tag or union while the other is a variable: what the
    /// variable stands for has still to be found.
    let private isPendingUnion ty expected =
        let isTagUnion ty = match ty with TyUnion _ | TyTag _ -> true | _ -> false
        match ty, expected with
        | TyVar _, other
        | other, TyVar _ -> isTagUnion other
        | _ -> false

    let empty : Subst = Map.empty

    let rec apply (subst: Subst) (ty: Ty) : Ty =
        match ty with
        | TyVar v ->
            match subst |> Map.tryFind v.Id with
            | Some replacement when replacement = ty -> ty
            | Some replacement -> apply subst replacement
            | None -> ty
        | TyNominal(nominal, underlying, isPrivate) -> TyNominal(nominal, apply subst underlying, isPrivate)
        | TyAlias(name, args, underlying) -> TyAlias(name, args |> List.map (apply subst), apply subst underlying)
        | TyFunc(arg, ret, contexts) ->
            TyFunc(apply subst arg, apply subst ret, contexts |> List.map (apply subst) |> FunctionContexts.flatten)
        | TyRecord fields ->
            fields |> Map.map (fun _ v -> apply subst v) |> TyRecord
        // A solved row adds its fields: to a record when it was solved to one, still open when to an open one.
        | TyOpenRecord(fields, row) ->
            let applied = fields |> Map.map (fun _ v -> apply subst v)
            let withFields more = more |> Map.fold (fun acc key fieldTy -> acc |> Map.add key fieldTy) applied
            match unalias (apply subst (TyVar row)) with
            | TyVar solved -> TyOpenRecord(applied, solved)
            | TyRecord more -> TyRecord (withFields more)
            | TyOpenRecord(more, rest) -> TyOpenRecord(withFields more, rest)
            // Renamed for a reader (see `displayRenaming`): the row takes the name.
            | TyPrimitive name -> TyOpenRecord(applied, { row with Name = Some name })
            | _ -> TyOpenRecord(applied, row)
        | TyTag(name, payload) -> TyTag(name, payload |> Option.map (apply subst))
        | TyApply(name, args) -> TyApply(name, args |> List.map (apply subst))
        // A rest variable solved to a union (`r` := `#notFound`) is spliced into the union it is the rest of. One
        // solved to the empty union leaves the others; when that is just one, the union is that one. A member that
        // is an alias of a tag or union is spliced in as what it stands for, as a union's members are tags. A tag
        // whose payload is the empty union has no values, so it is left out (`#error(#notFound)` taken from
        // `#error(#notFound)` leaves `#error` of nothing), and a tag the rest adds again is there once.
        | TyUnion items ->
            let spliced =
                items
                |> List.collect (fun item ->
                    let applied = apply subst item
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
        | TyPrimitive _ -> ty

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

    /// `index` in subscript digits: `12` is `₁₂`.
    let private subscript (index: int) =
        string index |> String.map (fun digit -> char (int '₀' + int digit - int '0'))

    let private latinLetters = "abcdefghijklmnopqrstuvwxyz"

    /// The ids of the variables in `ty` that stand for the rest of a union or record: `r` in `#some(a) | r` and
    /// in `(name: a, ...r)`; with `ofUnions` false, only a record's.
    let private restVarIds (ofUnions: bool) (ty: Ty) : Set<int> =
        let rec collect ty =
            match ty with
            | TyVar _
            | TyPrimitive _
            | TyNominal _ -> Set.empty
            | TyAlias(_, args, _) -> args |> List.map collect |> Set.unionMany
            | TyFunc(arg, ret, contexts) -> (collect arg :: collect ret :: List.map collect contexts) |> Set.unionMany
            | TyRecord fields -> fields |> Map.toList |> List.map (snd >> collect) |> Set.unionMany
            | TyOpenRecord(fields, row) -> collect (TyRecord fields) |> Set.add row.Id
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue Set.empty
            | TyApply(_, args) -> args |> List.map collect |> Set.unionMany
            | TyUnion items ->
                let rests =
                    if ofUnions then items |> List.choose (function TyVar v -> Some v.Id | _ -> None) |> Set.ofList
                    else Set.empty
                items |> List.map collect |> Set.unionMany |> Set.union rests
        collect ty

    /// The ids of the made-up variables in `ty` that stand for the rest of an open record or union and appear nowhere
    /// else in it. Such a rest only says there may be more fields or tags, so it needs no name: `(name: α, …) -> α`,
    /// `#some(int) | … -> int`.
    let private loneRests (ty: Ty) : Set<int> =
        let occurrences = varOccurrences ty
        let once =
            occurrences
            |> List.countBy (fun v -> v.Id)
            |> List.choose (fun (id, count) -> if count = 1 then Some id else None)
            |> Set.ofList
        let unnamed = occurrences |> List.filter (fun v -> v.Name.IsNone) |> List.map (fun v -> v.Id) |> Set.ofList
        restVarIds true ty |> Set.intersect once |> Set.intersect unnamed

    /// Replaces the type variables the compiler made up in `ty` with primitives that print as their names for a
    /// reader. They are named in the order they appear, through the Greek (`α`, `β`, ...) or Latin (`a`, `b`, ...)
    /// alphabet, skipping names already taken; variables named in an annotation keep their names. A union's rest
    /// variable is named from the other end of the alphabet, as in `#some(α) | ω`.
    ///
    /// When the two ends run into each other, the alphabet is split where they met while giving out its letters
    /// the first time, and each end goes round its own part again with a suffix: `α₁`, `ω₁` (`a1`, `z1`).
    ///
    /// The rests in `unnamedRests` are given no name and no letter, so they print as a bare spread.
    let private displayRenaming (notation: TypeNotation) (unnamedRests: Set<int>) (ty: Ty) : Subst =
        let alphabet, suffix =
            match notation with
            | Unicode -> greekLetters, subscript
            | Ascii -> latinLetters, string
        let vars = varsInPrintOrder ty
        let taken = vars |> List.choose (fun v -> v.Name) |> Set.ofList
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
        |> Seq.map (fun (v, name) -> v.Id, TyPrimitive name)
        |> Seq.append (unnamedRests |> Seq.map (fun id -> id, TyPrimitive ""))
        |> Map.ofSeq

    /// `ty` as `tyToString` writes it, with a spread written in `notation`.
    let private printIn (notation: TypeNotation) (ty: Ty) : string =
        printTy (match notation with Unicode -> "…" | Ascii -> "...") true ty

    /// `ty` for a reader, in `notation`, with the variables the compiler made up named as `displayRenaming`
    /// describes, and a rest that appears nowhere else written as a bare spread: `(name: α, …)`, `#some(α) | …`.
    let tyToDisplayString (notation: TypeNotation) (ty: Ty) : string =
        printIn notation (apply (displayRenaming notation (loneRests ty) ty) ty)

    /// Each of `tys` for a reader, as `tyToDisplayString` writes it, with a variable they share given the same name
    /// in each. The first one's variables are named first, so it reads as it would alone unless the others use a
    /// name from an annotation that it would have been given. A rest is a bare spread only where it is lone in
    /// every one of `tys` it appears in, so it is not named in one and not in another.
    let tysToDisplayStrings (notation: TypeNotation) (tys: Ty list) : string list =
        let lone =
            let loneIn = tys |> List.map (fun ty -> loneRests ty, varOccurrences ty |> List.map (fun v -> v.Id) |> Set.ofList)
            loneIn
            |> List.map fst
            |> Set.unionMany
            |> Set.filter (fun id -> loneIn |> List.forall (fun (lone, all) -> lone.Contains id || not (all.Contains id)))
        let renamed = displayRenaming notation lone (TyApply("", tys))
        tys |> List.map (apply renamed >> printIn notation)

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
            | TyNominal(_, underlying, _) -> collect true underlying
            | TyAlias(_, _, underlying) -> collect input underlying
            | TyFunc(arg, ret, contexts) -> collect (not input) arg @ collect input ret @ List.collect (collect input) contexts
            | TyRecord fields -> fields |> Map.toList |> List.collect (snd >> collect input)
            // A record's row is not a union's rest: it is never solved to the empty union.
            | TyOpenRecord(fields, row) -> collect input (TyRecord fields) @ [ row, false, input ]
            | TyTag(_, payload) -> payload |> Option.map (collect input) |> Option.defaultValue []
            | TyApply(_, args) -> args |> List.collect (collect input)
            | TyUnion items ->
                items
                |> List.collect (function
                    | TyVar v -> [ v, true, input ]
                    | item -> collect input item)
        let occurrences = collect false ty
        let inInput = occurrences |> List.choose (fun (v, _, input) -> if input then Some v.Id else None) |> Set.ofList
        occurrences
        |> List.choose (fun (v, isRest, _) -> if isRest && not (inInput.Contains v.Id) then Some v else None)
        |> List.distinctBy (fun v -> v.Id)

    let rec occurs (varId: int) (ty: Ty) : bool =
        match ty with
        | TyVar v -> v.Id = varId
        | TyNominal(_, underlying, _) -> occurs varId underlying
        | TyAlias(_, _, underlying) -> occurs varId underlying
        | TyFunc(arg, ret, contexts) -> occurs varId arg || occurs varId ret || contexts |> List.exists (occurs varId)
        | TyRecord fields -> fields |> Map.exists (fun _ v -> occurs varId v)
        | TyOpenRecord(fields, row) -> row.Id = varId || fields |> Map.exists (fun _ v -> occurs varId v)
        | TyTag(_, payload) -> payload |> Option.exists (occurs varId)
        | TyApply(_, args) -> args |> List.exists (occurs varId)
        | TyUnion items -> items |> List.exists (occurs varId)
        | TyPrimitive _ -> false

    /// Solves `constraints`, or gives the first that cannot be satisfied (see `Mismatch`).
    let unifyDetailed (constraints: ConstraintSet) : Result<Subst, Mismatch> =
        // Widening a union (below) makes up rest variables. They are numbered past every variable in the
        // constraints, and far past where inference numbers its own, so that they are new to both.
        let mutable nextRestId =
            let rec maxId ty =
                match ty with
                | TyVar v -> v.Id
                | TyPrimitive _ -> 0
                | TyNominal(_, underlying, _) -> maxId underlying
                | TyAlias(_, args, underlying) -> List.fold (fun acc arg -> max acc (maxId arg)) (maxId underlying) args
                | TyFunc(arg, ret, contexts) -> List.fold (fun acc context -> max acc (maxId context)) (max (maxId arg) (maxId ret)) contexts
                | TyRecord fields -> fields |> Map.fold (fun acc _ fieldTy -> max acc (maxId fieldTy)) 0
                | TyOpenRecord(fields, row) -> fields |> Map.fold (fun acc _ fieldTy -> max acc (maxId fieldTy)) row.Id
                | TyTag(_, payload) -> payload |> Option.map maxId |> Option.defaultValue 0
                | TyApply(_, args)
                | TyUnion args -> List.fold (fun acc arg -> max acc (maxId arg)) 0 args
            constraints
            |> List.fold (fun acc (left, right, _, _) -> max acc (max (maxId left) (maxId right))) 0
            |> fun highest -> max 1_000_000_000 (highest + 1)
        let freshRest () =
            let var = TyVar { Id = nextRestId; Name = None }
            nextRestId <- nextRestId + 1
            var
        // Where to report that none of a union's members could be the type given for it, at the constraint from
        // `origin`, given how trying each one failed, in the order tried. A member is tried against the constraints
        // after it too, so one whose failure is elsewhere fitted here, and what failed is a constraint further on,
        // which may be in another def altogether: that is reported, not this constraint (`solve` would otherwise set
        // this one aside as wrong too). Only when every member failed here is the last one's failure reported.
        let unionFailure (origin: SourceSpan) (failures: Mismatch list) =
            failures
            |> List.tryFind (fun (_, span, _) -> span <> origin)
            |> Option.orElse (List.tryLast failures)
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
                    Error (recordKeyMessage lKeys rKeys left right, origin, TypesDiffer)
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
                | TyVar v, ty
                | ty, TyVar v ->
                    if occurs v.Id ty then
                        Error ($"Occurs check failed: {tyToString ty} contains {tyToString (TyVar v)}.", origin, TypesDiffer)
                    else
                        let newSubst = subst |> Map.add v.Id ty
                        loop newSubst rest
                // An alias is the type it stands for.
                | TyAlias(_, _, underlying), _ ->
                    loop subst ((underlying, r, Equal, origin) :: rest)
                | _, TyAlias(_, _, underlying) ->
                    loop subst ((l, underlying, Equal, origin) :: rest)
                | TyPrimitive lName, TyPrimitive rName when lName = rName ->
                    loop subst rest
                | TyApply(lName, lArgs), TyApply(rName, rArgs) when lName = rName && lArgs.Length = rArgs.Length ->
                    let pairs = List.zip lArgs rArgs
                    let pairConstraints = pairs |> List.map (fun (lTy, rTy) -> (lTy, rTy, Equal, origin))
                    loop subst (pairConstraints @ rest)
                | TyNominal(lNominal, lUnderlying, lPrivate), TyNominal(rNominal, rUnderlying, rPrivate) ->
                    if lNominal = rNominal then
                        if lPrivate || rPrivate then
                            loop subst rest
                        else
                            loop subst ((lUnderlying, rUnderlying, Equal, origin) :: rest)
                    else
                        Error (mismatchMessage l r, origin, TypesDiffer)
                // A nominal type is a different type from its shape. Only assignability, which is one-way, lets a
                // nominal value stand for its shape.
                | TyNominal _, _
                | _, TyNominal _ ->
                    Error (mismatchMessage l r, origin, TypesDiffer)
                | TyFunc(lArg, lRet, lContexts), TyFunc(rArg, rRet, rContexts) ->
                    match FunctionContexts.agree freshRest origin lContexts rContexts with
                    | Ok contexts -> loop subst ((lArg, rArg, Equal, origin) :: (lRet, rRet, Equal, origin) :: contexts @ rest)
                    | Error (onlyLeft, onlyRight) -> Error (contextsMessage onlyLeft onlyRight l r, origin, TypesDiffer)
                | TyRecord lFields, TyRecord rFields ->
                    unifyRecordFields origin lFields rFields rest l r
                // An open record is a record with at least its fields: the record must have each of them, and the
                // row is the fields it has besides.
                | TyOpenRecord(openFields, row), TyRecord fields
                | TyRecord fields, TyOpenRecord(openFields, row) ->
                    let openKeys = keySet openFields
                    if Set.isSubset openKeys (keySet fields) then
                        let pairs = openKeys |> Seq.map (fun key -> (openFields.[key], fields.[key], Equal, origin)) |> Seq.toList
                        let others = fields |> Map.filter (fun key _ -> not (openKeys.Contains key))
                        loop subst (pairs @ [ (TyVar row, TyRecord others, Equal, origin) ] @ rest)
                    else
                        Error (recordKeyMessage openKeys (keySet fields) l r, origin, TypesDiffer)
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
                    | _ -> Error ($"Tag payload mismatch: #{lName}. {mismatchMessage l r}", origin, TypesDiffer)
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
                    | Error message, _ -> Error (message, origin, TypesDiffer)
                    | _, Error message -> Error (message, origin, TypesDiffer)
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
                                match lRest with
                                | Some restVar when isTagLike ty && not (lMap.ContainsKey (tagUnionKey ty)) -> loop subst ((TyVar restVar, ty, Equal, origin) :: rest)
                                | _ -> Error (unionFailure origin failures |> Option.defaultValue (mismatchMessage l r, origin, TypesDiffer))
                            | item :: restItems ->
                                match loop subst ((item, ty, Equal, origin) :: emptyRest lRest @ rest) with
                                | Ok result -> Ok result
                                | Error inner -> tryItems restItems (failures @ [ inner ])
                        tryItems tags []
                    | Error message -> Error (message, origin, TypesDiffer)
                | ty, TyUnion rItems ->
                    match parseTagUnion rItems with
                    | Ok (rMap, rRest) ->
                        let tags = rMap |> Map.toList |> List.map snd
                        let rec tryItems items (failures: Mismatch list) =
                            match items with
                            | [] ->
                                match rRest with
                                | Some restVar when isTagLike ty && not (rMap.ContainsKey (tagUnionKey ty)) -> loop subst ((ty, TyVar restVar, Equal, origin) :: rest)
                                | _ -> Error (unionFailure origin failures |> Option.defaultValue (mismatchMessage l r, origin, TypesDiffer))
                            | item :: restItems ->
                                match loop subst ((ty, item, Equal, origin) :: emptyRest rRest @ rest) with
                                | Ok result -> Ok result
                                | Error inner -> tryItems restItems (failures @ [ inner ])
                        tryItems tags []
                    | Error message -> Error (message, origin, TypesDiffer)
                | _ -> Error (mismatchMessage l r, origin, TypesDiffer)
            match remaining with
            | [] -> Ok subst
            | (left, right, kind, origin) :: rest ->
                let l = apply subst left
                let r = apply subst right
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
                            loop subst (rest @ [ (left, right, AssignableLater, origin) ])
                        | TyVar _, _
                        | _, TyVar _ ->
                            unifyEqual false origin l r rest
                        | TyAlias(_, _, underlying), _ ->
                            loop subst ((underlying, r, Assignable, origin) :: rest)
                        | _, TyAlias(_, _, underlying) ->
                            loop subst ((l, underlying, Assignable, origin) :: rest)
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
                                    |> List.choose (fun (key, lTy) -> rMap |> Map.tryFind key |> Option.map (fun rTy -> (lTy, rTy, Equal, origin)))
                                let extraTags = lMap |> Map.toList |> List.filter (fun (key, _) -> not (rMap.ContainsKey key)) |> List.map snd
                                let restConstraints =
                                    if extraTags.IsEmpty then [] else [ (TyVar restVar, TyUnion(extraTags @ [ freshRest () ]), Equal, origin) ]
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
                                let pairs = lMap |> Map.toList |> List.map (fun (key, lTy) -> (lTy, rMap.[key], Equal, origin))
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
                        // Assignability reaches into what types are built from, so a nominal type stays one-way
                        // there too: `list($Person)` is assignable to `list((name: string))`, but not the reverse.
                        // Values are immutable, so a type's arguments are covariant, and so are a record's fields:
                        // the arguments of `f(a, b)` are one tuple, each item given where its parameter is
                        // expected. A function's argument is contravariant, since the function is given values of
                        // the type it expects.
                        | TyRecord lFields, TyRecord rFields when keySet lFields = keySet rFields ->
                            let pairs = keySet lFields |> Seq.map (fun key -> (lFields.[key], rFields.[key], Assignable, origin)) |> Seq.toList
                            loop subst (pairs @ rest)
                        | TyApply(lName, lArgs), TyApply(rName, rArgs) when lName = rName && lArgs.Length = rArgs.Length ->
                            loop subst ((List.map2 (fun lTy rTy -> (lTy, rTy, Assignable, origin)) lArgs rArgs) @ rest)
                        // A function that requires fewer contexts fits where one that requires more is expected.
                        | TyFunc(lArg, lRet, lContexts), TyFunc(rArg, rRet, rContexts) ->
                            match FunctionContexts.fit freshRest origin lContexts rContexts with
                            | Ok contexts -> loop subst ((rArg, lArg, Assignable, origin) :: (lRet, rRet, Assignable, origin) :: contexts @ rest)
                            | Error extra -> Error (contextsNotAllowedMessage extra l r, origin, ContextsNotAllowed)
                        | _ ->
                        match l, r with
                        | TyNominal(lNominal, _, _), TyNominal(rNominal, _, _) when lNominal = rNominal ->
                            loop subst rest
                        | TyNominal(_, _, true), _
                        | _, TyNominal(_, _, true) ->
                            Error (mismatchMessage l r, origin, TypesDiffer)
                        | TyNominal(_, underlying, false), other when not (match other with TyNominal _ -> true | _ -> false) ->
                            loop subst ((underlying, other, Equal, origin) :: rest)
                        | other, TyNominal(_, _, false) when not (match other with TyNominal _ -> true | _ -> false) ->
                            Error (mismatchMessage l r, origin, TypesDiffer)
                        | TyNominal _, TyNominal _ ->
                            Error (mismatchMessage l r, origin, TypesDiffer)
                        | _ ->
                            unifyEqual false origin l r rest
                    | Equal ->
                        unifyEqual true origin l r rest
        loop empty constraints

    /// Solves `constraints`, or gives the first that cannot be satisfied, with what was wrong and where.
    let unify (constraints: ConstraintSet) : Result<Subst, string * SourceSpan> =
        unifyDetailed constraints |> Result.mapError (fun (message, span, _) -> message, span)

    /// Solves `constraints` as `unify` does, carrying on past a failure: the constraints from where it failed are
    /// set aside and the rest solved again, until they agree. Gives the substitution the rest agree on, and each
    /// failure in the order it was found (see `Mismatch`). With no failure, the substitution is the one `unify` gives.
    let solveDetailed (constraints: ConstraintSet) : Subst * Mismatch list =
        let rec attempt (remaining: ConstraintSet) (failures: Mismatch list) =
            match unifyDetailed remaining with
            | Ok subst -> subst, List.rev failures
            | Error (_, span, _ as failure) ->
                let kept = remaining |> List.filter (fun (_, _, _, origin) -> origin <> span)
                // A failure from no constraint given (which `unify` does not make) cannot be set aside.
                if kept.Length = remaining.Length then empty, List.rev (failure :: failures)
                else attempt kept (failure :: failures)
        attempt constraints []

    /// Solves `constraints` as `solveDetailed` does, giving each failure's message and where it is.
    let solve (constraints: ConstraintSet) : Subst * (string * SourceSpan) list =
        let subst, failures = solveDetailed constraints
        subst, failures |> List.map (fun (message, span, _) -> message, span)
