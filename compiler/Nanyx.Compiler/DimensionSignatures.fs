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
            | Kind (DimensionDecl(_, name, storage, _)) -> Some (name, storage.IsNone)
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

    /// The types a derived dimension, `Length / Time`, multiplies and divides: those that are no product or quotient.
    let rec private partsOf (derived: TypeExpr) : TypeExpr list =
        match derived.Kind with
        | TypeProduct(left, right)
        | TypeQuotient(left, right) -> partsOf left @ partsOf right
        | _ -> [ derived ]

    /// A storage as it is written, to tell two apart: `a` and `float` are two, and `a` and `a` one.
    let rec private writtenStorage (storage: TypeExpr) : string =
        match storage.Kind with
        | TypeVar name
        | TypeName name -> name
        | TypeApply(name, args) -> $"""{name}({args |> List.map writtenStorage |> String.concat ", "})"""
        | _ -> $"#{storage.Id}"

    /// A derived dimension as a message writes it, its parts without their storages: `Length / (Time * Time)`.
    let rec private writtenDimension (derived: TypeExpr) : string =
        let right (part: TypeExpr) =
            match part.Kind with
            | TypeProduct _
            | TypeQuotient _ -> $"({writtenDimension part})"
            | _ -> writtenDimension part
        match derived.Kind with
        | TypeProduct(left, part) -> $"{writtenDimension left} {UnitSyntax.times} {right part}"
        | TypeQuotient(left, part) -> $"{writtenDimension left} {UnitSyntax.over} {right part}"
        | TypeName name
        | TypeApply(name, _) -> name
        | _ -> "?"

    /// The error for `part` of a derived dimension that is no dimension.
    let private notADimension (part: TypeExpr) (name: string) =
        Diagnostics.at DiagnosticCodes.NotADimension part.Span
            $"'{name}' is no dimension, and a derived dimension multiplies and divides dimensions, as `Length / Time` does"

    /// The errors in what the declaration of a derived dimension, `dimension Speed = Length / Time`, names: a part that
    /// is no dimension, and a storage written on one, since the dimension is generic in its storage as its parts are.
    let private declarationErrors (dimensions: Map<Identifier, bool>) (name: Identifier) (derived: TypeExpr) : Diagnostic list =
        partsOf derived
        |> List.choose (fun part ->
            match part.Kind with
            | TypeName partName when dimensions.ContainsKey partName -> None
            | TypeApply(partName, _) when dimensions.ContainsKey partName ->
                Some (
                    Diagnostics.at DiagnosticCodes.DerivedDimensionStorages part.Span
                        $"'{name}' is generic in its storage, as its parts are, so they are written without one: write `{Names.written partName}`, and `{Names.written name}(float)` where a value of it is"
                    |> Diagnostics.about name)
            | TypeName partName
            | TypeApply(partName, _) -> Some (notADimension part partName)
            | _ -> Some (notADimension part "this"))

    /// `typeExpr` with each dimension written without its storage given a storage variable of its own, the storage
    /// variables of the dimensions it names, and the errors in them: a storage written for a constrained dimension.
    /// The parts of a derived dimension share one storage: the one written on its parts, or one of its own.
    let private withStorages (dimensions: Map<Identifier, bool>) (typeExpr: TypeExpr) : TypeExpr * Storage list * Diagnostic list =
        let names = (freshNames (Set.ofList (TypeResolution.annotationVarNames typeExpr))).GetEnumerator()
        let storages = ResizeArray<Storage>()
        let errors = ResizeArray<Diagnostic>()
        let rec derived (t: TypeExpr) : TypeExpr =
            let parts = partsOf t
            let written =
                parts
                |> List.choose (fun part ->
                    match part.Kind with
                    | TypeApply(name, [ storage ]) when dimensions.TryFind name = Some true -> Some storage
                    | _ -> None)
            let notDimensions =
                parts
                |> List.choose (fun part ->
                    match part.Kind with
                    | TypeName name
                    | TypeApply(name, _) when dimensions.ContainsKey name -> None
                    | TypeName name
                    | TypeApply(name, _) -> Some (notADimension part name)
                    | _ -> Some (notADimension part "this"))
            errors.AddRange notDimensions
            // One that names something no dimension is that error alone: it has no storage to require `Num` of.
            if not notDimensions.IsEmpty then t else
            match written |> List.distinctBy writtenStorage with
            | first :: second :: _ ->
                errors.Add(
                    Diagnostics.at DiagnosticCodes.DerivedDimensionStorages t.Span
                        $"The parts of '{writtenDimension t}' are stored as '{writtenStorage first}' and as '{writtenStorage second}', and a derived dimension's parts share one storage: write it on every part, or on none")
            | _ -> ()
            let shared =
                match written with
                | storage :: _ -> storage
                | [] ->
                    names.MoveNext() |> ignore
                    TypeExpr.at t.Span (TypeVar names.Current)
            match shared.Kind with
            | TypeVar variable -> storages.Add { Variable = variable; Dimension = writtenDimension t; Span = t.Span }
            | _ -> ()
            let rec apply (part: TypeExpr) =
                match part.Kind with
                | TypeProduct _
                | TypeQuotient _ -> TypeExpr.mapChildren apply part
                | TypeName name when dimensions.TryFind name = Some true -> TypeExpr.at part.Span (TypeApply(name, [ shared ]))
                | _ -> part
            apply t
        and rewrite (t: TypeExpr) : TypeExpr =
            match t.Kind with
            | TypeProduct _
            | TypeQuotient _ -> derived t
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
        for item in module' do
            match item.Kind with
            | DimensionDecl(_, name, _, Some derived) -> errors.AddRange(declarationErrors dimensions name derived)
            | _ -> ()
        rewritten, List.ofSeq errors
