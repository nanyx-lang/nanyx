namespace NyxCompiler

open Parser.Program

/// Types written as text: `tyToString` writes a type as a def's annotation would, and `Unifier`'s display functions
/// build on `printTy` to name the variables the compiler made up for a reader.
module TypePrinting =
    /// The number of the first rest variable `unify` makes up, far past where inference numbers its own.
    let internal firstMadeUpRest = 1_000_000_000

    /// Whether `var` is a rest variable `unify` made up when widening a union, or to keep room for more contexts in a
    /// context variable (see `FunctionContexts`). Its number depends on the constraints it was given, so a later
    /// `unify` of more constraints may give the same rest another, and a constraint naming it may name nothing then.
    let isMadeUpRest (var: TyVar) = var.Id >= firstMadeUpRest

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

    /// `index` in subscript digits: `12` is `₁₂`.
    let internal subscript (index: int) =
        string index |> String.map (fun digit -> char (int '₀' + int digit - int '0'))

    /// The scoped contexts the function types of `ty` require, in the order they are printed, each as often.
    let rec private scopedContextsInPrintOrder (ty: Ty) : (string * Ty) list =
        let all = List.collect scopedContextsInPrintOrder
        match ty with
        | TyFunc(arg, ret, contexts) ->
            (writtenContexts contexts |> List.choose (function FunctionContexts.ScopedContext(name, scope) -> Some (name, scope) | _ -> None))
            @ scopedContextsInPrintOrder arg
            @ scopedContextsInPrintOrder ret
        | TyContexts contexts -> writtenContexts contexts |> List.choose (function FunctionContexts.ScopedContext(name, scope) -> Some (name, scope) | _ -> None)
        | TyRecord(fields, _)
        | TyOpenRecord(fields, _) -> all (List.ofSeq fields.Values)
        | TyTag(_, Some payload) -> scopedContextsInPrintOrder payload
        | TyNominal(_, args, _, _)
        | TyAlias(_, args, _)
        | TyApply(_, args)
        | TyUnion args
        | TySelf args -> all args
        | TyRecursive(body, _) -> scopedContextsInPrintOrder body
        | _ -> []

    /// How `printTy` writes a type: an open record's or union's rest is spread with `Spread`, and each scoped context
    /// of a name `ty` requires at more than one scope is numbered by its scope's place in `Scopes`.
    type internal Printing = { Spread: string; Scopes: Map<string, Ty list> }

    /// `Printing` for `ty`: the scopes of each scoped context it requires at more than one, in order of first appearance.
    let internal printingFor (spread: string) (ty: Ty) : Printing =
        let scopes =
            scopedContextsInPrintOrder ty
            |> List.groupBy fst
            |> List.map (fun (name, found) -> name, found |> List.map snd |> List.distinct)
            |> List.filter (fun (_, scopes) -> scopes.Length > 1)
            |> Map.ofList
        { Spread = spread; Scopes = scopes }

    /// `context` as `p` writes it among a function type's contexts: a scoped one numbered when `p` numbers its name, as
    /// `Memory₁`, and without its scope.
    let private contextAsPrinted (p: Printing) (context: Ty) : Ty =
        match context with
        | FunctionContexts.ScopedContext(name, scope) when p.Scopes.ContainsKey name ->
            TyApply($"{name}{subscript (List.findIndex ((=) scope) p.Scopes.[name] + 1)}", [])
        | _ -> FunctionContexts.withoutScope context

    /// Whether `printTy` writes `ty` with contexts in front: a type generic in contexts that carries some, or the contexts
    /// it carries on their own.
    let rec private writtenWithContexts (ty: Ty) =
        match ty with
        | FunctionContexts.CarryingNominal(_, _, contexts) -> not (writtenContexts contexts).IsEmpty
        | TyContexts _ -> true
        | TyRecursive(body, _) -> writtenWithContexts body
        | _ -> false

    /// `ty` with the fewest brackets the grammar allows. `,` binds tighter than `->`, which groups to the right,
    /// and `|` binds tighter than both, so a function needs brackets only where it is an item of a comma list
    /// (`(int, (a -> b))`), the input of another function (`(a -> b) -> c`) or a member of a union; a union never
    /// does. At the top, `signature` writes a function's parameters bare, as a def's annotation would:
    /// `int, (a -> b) -> a`. An open record's rest is spread with `spread`: `...` as in source, or `…` for a reader.
    let rec internal printTy (p: Printing) (signature: bool) (ty: Ty) : string =
        // A function, bracketed; in brackets its parameters are not bare.
        let grouped ty =
            match ty with
            | TyFunc _ -> $"({printTy p false ty})"
            // A type generic in contexts written with them reads as a function type's would.
            | _ when writtenWithContexts ty -> $"({printTy p false ty})"
            | _ -> printTy p false ty
        // The items of a tuple, each bracketed where a comma list needs it.
        let items tys = tys |> List.map grouped |> String.concat ", "
        let bareTuple ty =
            match ty with
            | TyRecord(fields, _) ->
                match tryTupleLikeRecord fields with
                | Some tupleItems, [] -> Some (items tupleItems)
                | _ -> None
            | _ -> None
        // The only argument of an application or alias. A record of named fields is written in the application's
        // own brackets, as a tag's payload is: `list(name: string, ...)`. A tuple is not, as its items would read as
        // more arguments.
        let soleArgument ty =
            match ty with
            | TyRecord(fields, _)
            | TyOpenRecord(fields, _) when not fields.IsEmpty && (fst (tryTupleLikeRecord fields)).IsNone ->
                let written = printTy p false ty
                written.Substring(1, written.Length - 2)
            | _ -> printTy p false ty
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
        // A type in a scoped context's scope is written without the scope, as `mut T` is: `Cursor`, `MRef(int)`.
        // A type generic in contexts is written with them in front, as a function type is: `[FileSystem] Stream(string)`.
        | TyNominal(nominal, args, _, _) ->
            // The prelude's `Seq` is shown by its alias, as the built-in types are (see `Seqs`).
            let name = if nominal = Seqs.nominal then Seqs.alias else nominal.Name
            let named =
                match args |> List.filter (function FunctionContexts.ScopedContext _ | FunctionContexts.Carried _ -> false | _ -> true) with
                | [] -> name
                | [ arg ] -> $"{name}({soleArgument arg})"
                | args -> $"{name}({items args})"
            match ty with
            | FunctionContexts.CarryingNominal(_, _, contexts) ->
                match writtenContexts contexts |> List.map (contextAsPrinted p) |> List.distinct with
                | [] -> named
                | written -> $"""[{written |> List.map (printTy p false) |> String.concat ", "}] {named}"""
            | _ -> named
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
                | TyFunc(_, _, contexts) when not (writtenContexts contexts).IsEmpty -> $"({printTy p false ret})"
                | _ -> printTy p false ret
            match writtenContexts contexts |> List.map (contextAsPrinted p) |> List.distinct with
            | [] -> $"{argText} -> {retText}"
            | written ->
                $"""[{written |> List.map (printTy p false) |> String.concat ", "}] {argText} -> {retText}"""
        | TyRecord(fields, order) ->
            // Named fields in the order they are written, any the order doesn't name after them by name.
            let inOrder (named: (string * Ty) list) =
                let rank name = List.tryFindIndex ((=) name) order.Names |> Option.defaultValue order.Names.Length
                named |> List.sortBy (fun (name, _) -> rank name, name)
            match tryTupleLikeRecord fields with
            | Some tupleItems, namedFields ->
                // In a tuple's own brackets, contexts in front of the first item are its own (#1728).
                let tupleText =
                    match tupleItems with
                    | (TyNominal _ as first) :: rest when namedFields.IsEmpty && not rest.IsEmpty ->
                        String.concat ", " (printTy p false first :: List.map grouped rest)
                    | _ -> items tupleItems
                let namedText =
                    namedFields
                    |> inOrder
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
                |> inOrder
                |> List.map (fun (name, fieldTy) -> $"{name}: {grouped fieldTy}")
                |> String.concat ", "
                |> fun inner -> $"({inner})"
        // An open record's other fields are written as a spread of its row: `(name: string, ...ω)`. A row named
        // nothing (see `displayRenaming`) is written as a bare spread: `(name: string, ...)`.
        | TyOpenRecord(fields, row) ->
            let rowText = $"{p.Spread}{printTy p false (TyVar row)}"
            match printTy p false (TyRecord(fields, FieldOrder.none)) with
            | "()" -> $"({rowText})"
            | closed -> $"{closed.Substring(0, closed.Length - 1)}, {rowText})"
        // Tags are written with their `#`, as in source: `#some(int) | #notFound`. A record payload's fields are
        // in the tag's own brackets: `#pair(int, string)`, `#authenticated(username: string)`.
        | TyTag(name, payloadOpt) ->
            match payloadOpt with
            | Some (TyRecord(fields, _) as payload) when not fields.IsEmpty ->
                let written = printTy p false payload
                $"#{name}{written}"
            // The unit value is written as no payload in the brackets, as in source: `#some()`.
            | Some (TyPrimitive "unit") -> $"#{name}()"
            | Some payload -> $"#{name}({bareTuple payload |> Option.defaultWith (fun () -> printTy p false payload)})"
            | None -> $"#{name}"
        // A type the checker never learns, a field's in a type's description, is written by its name, without the
        // number that makes it its own: `b` for `b#12` (see `TypeDescriptions.skolem`).
        | TyApply(name, []) when name.Contains '#' -> name.Substring(0, name.IndexOf '#')
        // A context with no arguments is written by its name: `Console`, and `Memory` without its scope.
        | FunctionContexts.PlainContext name -> name
        // A cell's type is written as it is in an annotation, without its scope: `mut int`.
        | FunctionContexts.CellType(held, _) -> $"mut {grouped held}"
        | TyApply(name, [ arg ]) -> $"{name}({soleArgument arg})"
        | TyApply(name, args) -> $"{name}({items args})"
        // A union of no tags has no values: the bottom type.
        | TyUnion [] -> "undefined"
        // A rest named nothing (see `displayRenaming`) is written as a bare spread: `#some(int) | ...`.
        | TyUnion members ->
            members
            |> List.map (function TyPrimitive "" -> p.Spread | item -> grouped item)
            |> String.concat " | "
        // Only inside what a recursive alias stands for, which is written by the alias's name.
        | TyRecursive(body, _) -> printTy p signature body
        | TySelf [] -> "self"
        | TySelf args -> $"self({items args})"
        | TyParam index -> $"${index}"
        // Only where a scoped context's scope is printed on its own, which a reader never sees.
        | TyScope scope -> $"scope({soleArgument scope})"
        // Only where the contexts a type carries are printed on their own, which a reader never sees: they are written
        // in front of the type.
        | TyContexts contexts -> $"""[{writtenContexts contexts |> List.map (contextAsPrinted p >> printTy p false) |> String.concat ", "}]"""

    /// `ty` as it would be written in a def's annotation: see `printTy`.
    let tyToString (ty: Ty) : string = printTy (printingFor "..." ty) true ty

    /// The type variables of `ty` in the order `tyToString` prints them, each as many times as it is printed. A
    /// nominal's or alias's underlying type is not printed, so its variables are not included (its arguments' are).
    let internal varOccurrences (ty: Ty) : TyVar list =
        let rec collect ty =
            match ty with
            | TyVar v -> [ v ]
            | TyPrimitive _
            | TyParam _ -> []
            | TyNominal(_, args, _, _)
            | TyAlias(_, args, _)
            | TyRecursive(_, args)
            | TySelf args -> args |> List.collect collect
            | TyFunc(arg, ret, contexts) ->
                (writtenContexts contexts |> List.map FunctionContexts.withoutScope |> List.collect collect) @ collect arg @ collect ret
            | TyContexts contexts -> writtenContexts contexts |> List.map FunctionContexts.withoutScope |> List.collect collect
            | TyRecord(fields, _) ->
                match tryTupleLikeRecord fields with
                | Some tupleItems, namedFields ->
                    (tupleItems |> List.collect collect) @ (namedFields |> List.sortBy fst |> List.collect (snd >> collect))
                | None, _ -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyOpenRecord(fields, row) -> collect (TyRecord(fields, FieldOrder.none)) @ [ row ]
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
            | FunctionContexts.ScopedContext _ -> []
            | FunctionContexts.CellType(held, _) -> collect held
            | TyApply(_, args) -> args |> List.collect collect
            | TyUnion items -> items |> List.collect collect
            | TyScope scope -> collect scope
        collect ty

    /// The type variables of `ty` in the order `tyToString` prints them, each once.
    let internal varsInPrintOrder (ty: Ty) : TyVar list =
        varOccurrences ty |> List.distinctBy (fun v -> v.Id)
