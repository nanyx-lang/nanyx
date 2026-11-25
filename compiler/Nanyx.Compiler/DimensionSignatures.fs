namespace NyxCompiler

open Parser.Program

/// The signatures that name dimensions (see `Units`). A dimension written without its storage, `Time`, is of a storage
/// of its own, and `Time(a)` of the storage `a`, so `def overlap: Time, Time -> bool` is read as
/// `[Num(a), Num(b)] Time(a), Time(b) -> bool`: a signature that names a dimension requires `Num` of its storage
/// without writing it. A storage nothing a call passes fixes, one written only in the result, is an error, as is a
/// storage written for a dimension constrained to one, `Bytes(a)`.
module internal DimensionSignatures =
    /// A storage variable a signature has, for a dimension it names: the variable's name, the dimension's, and where
    /// the dimension is written.
    type private Storage =
        { Variable: string
          Dimension: Identifier
          Span: SourceSpan }

    /// The dimensions a module can name, its own and those its imports bring in (`typeDefs`), each with whether it is
    /// generic in its storage: not one constrained to a storage, as `dimension Bytes(int)` is.
    let dimensionsIn (typeDefs: Map<string, TypeDefInfo>) (module': TopLevelItem list) : Map<Identifier, bool> =
        let imported = typeDefs |> Map.filter (fun _ info -> info.IsDimension) |> Map.map (fun _ info -> not info.Parameters.IsEmpty)
        module'
        |> List.choose (function
            | Kind (DimensionDecl(_, name, storage)) -> Some (name, storage.IsNone)
            | _ -> None)
        |> List.fold (fun acc (name, generic) -> acc |> Map.add name generic) imported

    /// The names a signature can give the storages it doesn't write: single letters, then letters with a number, as
    /// inferred types are named, but for those `written`.
    let private freshNames (written: Set<string>) : string seq =
        Seq.initInfinite id
        |> Seq.map (fun index ->
            let letter = string (char (int 'a' + index % 26))
            if index < 26 then letter else $"{letter}{index / 26}")
        |> Seq.filter (written.Contains >> not)

    /// `typeExpr` with each dimension written without its storage given a storage variable of its own, the storage
    /// variables of the dimensions it names, and the errors in them: a storage written for a constrained dimension.
    let private withStorages (dimensions: Map<Identifier, bool>) (typeExpr: TypeExpr) : TypeExpr * Storage list * Diagnostic list =
        let names = (freshNames (Set.ofList (TypeResolution.annotationVarNames typeExpr))).GetEnumerator()
        let storages = ResizeArray<Storage>()
        let errors = ResizeArray<Diagnostic>()
        let rec rewrite (t: TypeExpr) : TypeExpr =
            match t.Kind with
            | TypeName name when dimensions.TryFind name = Some true ->
                names.MoveNext() |> ignore
                let variable = names.Current
                storages.Add { Variable = variable; Dimension = name; Span = t.Span }
                TypeExpr.at t.Span (TypeApply(name, [ TypeExpr.at t.Span (TypeVar variable) ]))
            | TypeApply(name, args) when dimensions.ContainsKey name ->
                match dimensions[name], args with
                | false, _ ->
                    errors.Add(
                        Diagnostics.at DiagnosticCodes.ConstrainedDimensionStorage t.Span
                            $"'{name}' is constrained to one storage, so it is written without one: write `{Names.written name}`"
                        |> Diagnostics.about name)
                    t
                | true, [ { Kind = TypeVar variable } ] ->
                    storages.Add { Variable = variable; Dimension = name; Span = t.Span }
                    t
                | _ -> TypeExpr.mapChildren rewrite t
            | _ -> TypeExpr.mapChildren rewrite t
        let rewritten = rewrite typeExpr
        rewritten, List.ofSeq storages, List.ofSeq errors

    /// Whether `context` is `Num` of the variable `variable`.
    let private isNumOf (variable: string) (context: TypeExpr) =
        match context.Kind with
        | TypeApply(name, [ { Kind = TypeVar written } ]) -> name = Fractions.context && written = variable
        | _ -> false

    /// The signature `typeExpr` as it is read (see the module's comment), and the errors in it.
    let signature (dimensions: Map<Identifier, bool>) (typeExpr: TypeExpr) : TypeExpr * Diagnostic list =
        let rewritten, storages, errors = withStorages dimensions typeExpr
        if storages.IsEmpty then
            rewritten, errors
        else
            let contexts, inner =
                match rewritten.Kind with
                | TypeWithContext(contexts, inner) -> contexts, inner
                | _ -> [], rewritten
            let onlyInResult (parameters: TypeExpr option) =
                let inParameters = parameters |> Option.map (TypeResolution.annotationVarNames >> Set.ofList) |> Option.defaultValue Set.empty
                storages
                |> List.filter (fun storage -> not (inParameters.Contains storage.Variable))
                |> List.distinctBy _.Variable
                |> List.map (fun storage ->
                    let fix =
                        match parameters with
                        | Some _ ->
                            $"write it as a parameter's, as in `list({storage.Dimension}(a)) -> {storage.Dimension}(a)`, or write the storage, as in `{storage.Dimension}(int)`"
                        | None -> $"write the storage, as in `{storage.Dimension}(int)`"
                    Diagnostics.at DiagnosticCodes.StorageOnlyInResult storage.Span
                        $"The number that stores '{storage.Dimension}' here is not the type of a parameter, so nothing a call passes says what it is: {fix}"
                    |> Diagnostics.about storage.Dimension)
            match inner.Kind with
            | TypeFunc(parameters, _) ->
                let required =
                    storages
                    |> List.map _.Variable
                    |> List.distinct
                    |> List.filter (fun variable -> not (contexts |> List.exists (isNumOf variable)))
                    |> List.map (fun variable ->
                        TypeExpr.at rewritten.Span (TypeApply(Fractions.context, [ TypeExpr.at rewritten.Span (TypeVar variable) ])))
                let read = if required.IsEmpty then rewritten else TypeExpr.at rewritten.Span (TypeWithContext(contexts @ required, inner))
                read, errors @ onlyInResult (Some parameters)
            | _ -> rewritten, errors @ onlyInResult None

    /// `module'` with each def's signature, top-level and local, as it is read (see `signature`), and the errors in them,
    /// given the types its imports bring in, `typeDefs`. It is read before a context's associated types are written as
    /// its last arguments (see `AssociatedTypes`), which the `Num` it requires has.
    let ofModule (typeDefs: Map<string, TypeDefInfo>) (module': TopLevelItem list) : TopLevelItem list * Diagnostic list =
        let dimensions = dimensionsIn typeDefs module'
        let errors = ResizeArray<Diagnostic>()
        let read (typeOpt: TypeExpr option) =
            typeOpt
            |> Option.map (fun typeExpr ->
                let read, found = signature dimensions typeExpr
                errors.AddRange found
                read)
        let rec inValue (expr: Expression) : Expression =
            let inner = SyntaxTree.mapChildren inValue expr
            match inner.Kind with
            | Block statements ->
                let statements =
                    statements
                    |> List.map (fun statement ->
                        match statement.Kind with
                        | DefStatement(isExport, name, typeOpt, value) -> { statement with Kind = DefStatement(isExport, name, read typeOpt, value) }
                        | _ -> statement)
                { inner with Kind = Block statements }
            | _ -> inner
        let rec inItem (item: TopLevelItem) : TopLevelItem =
            match item.Kind with
            | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, read typeOpt, inValue value)) }
            | Expr value -> { item with Kind = Expr (inValue value) }
            | ModuleBlock(name, items) -> { item with Kind = ModuleBlock(name, List.map inItem items) }
            | _ -> item
        let rewritten = List.map inItem module'
        rewritten, List.ofSeq errors
