namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Units of measure: `dimension Time`, `@baseUnit unit ns: Time`, `unit ms: Time = 1_000us` and `500ms` (see
/// `UnitSyntax`). A dimension is a type: a private nominal one, generic in the number that stores it, `Time(a)`, unless
/// it is constrained to one, as `dimension Bytes(int)` is, so it is exported, imported and written as a type is. A
/// unit is a name of a kind of its own, with its dimension and how many of the dimension's base units it is, exactly
/// (`InferState.Units`). The checker does not type a number with a unit yet, so it reports each where it is written.
module internal Units =
    /// The error for a number with a unit, `500ms`, of a unit the module knows, written at `span`.
    let literalNotChecked (span: SourceSpan) : Diagnostic =
        Diagnostics.at DiagnosticCodes.UnitsNotChecked span
            "A number with a unit is read but not checked yet, so a program can't use one: units of measure are being built"

    /// The error for `unitName`, written at `span` in a literal or a unit's definition, when the module knows no unit of
    /// that name.
    let unknownUnit (span: SourceSpan) (unitName: Identifier) (known: Map<string, UnitInfo>) : Diagnostic =
        let suggestion =
            match NameSuggestions.closest known.Keys unitName with
            | close :: _ -> $": did you mean '{close}'?"
            | [] -> ": declare it with `unit`, or import the module that exports it"
        Diagnostics.at DiagnosticCodes.UnknownUnit span $"There is no unit '{unitName}' here{suggestion}"
        |> Diagnostics.about unitName

    /// The parameter a dimension that is not constrained is generic in: the number that stores it.
    let private storageParameter = "a"

    /// `items` with each dimension's declaration as the type definition it stands for, `type Time(a) = private a` or
    /// `type Bytes = private int`, for the type definitions to be registered with (see `TypeResolution.registerTypeDef`).
    let withDimensionsAsTypes (items: TopLevelItem list) : TopLevelItem list =
        items
        |> List.map (fun item ->
            match item.Kind with
            | DimensionDecl(isExport, name, storage) ->
                let modifiers = (if isExport then [ Export ] else []) @ [ Private ]
                let parameters, body =
                    match storage with
                    | Some storage -> [], storage
                    | None -> [ storageParameter, None ], TypeExpr.at item.Span (TypeVar storageParameter)
                { item with Kind = Def (TypeDef(name, modifiers, parameters, body)) }
            | _ -> item)

    /// The dimensions `items` declare, each with its declaration.
    let private dimensionsOf (items: TopLevelItem list) =
        items |> List.choose (fun item -> match item.Kind with DimensionDecl(_, name, storage) -> Some (name, storage, item) | _ -> None)

    /// `state` with each dimension `items` declare marked as one among its type definitions.
    let markDimensions (items: TopLevelItem list) (state: InferState) : InferState =
        { state with
            TypeDefs =
                dimensionsOf items
                |> List.fold (fun typeDefs (name, _, _) ->
                    match typeDefs |> Map.tryFind name with
                    | Some info when info.Home = state.Home -> typeDefs |> Map.add name { info with IsDimension = true }
                    | _ -> typeDefs) state.TypeDefs }

    /// A unit's declaration as the module writes it: its item, name and dimension, whether `@baseUnit` marks it, and the
    /// multiple of another unit it is defined as, with where that is written.
    type private Declared =
        { Item: TopLevelItem
          Name: Identifier
          IsExport: bool
          DimensionName: Identifier
          IsBase: bool
          Multiple: (WrittenNumber * Identifier * SourceSpan) option }

    let private declaredUnits (items: TopLevelItem list) : Declared list =
        let previous = None :: (items |> List.map Some) |> List.take items.Length
        List.zip previous items
        |> List.choose (fun (before, item) ->
            match item.Kind with
            | UnitDecl(isExport, name, dimension, multiple) ->
                Some
                    { Item = item
                      Name = name
                      IsExport = isExport
                      DimensionName = dimension
                      IsBase = before |> Option.exists (fun before -> UnitSyntax.isBaseUnitDeclaration before item)
                      Multiple =
                        multiple
                        |> Option.bind (fun value ->
                            match value.Kind with
                            | LiteralExpr(UnitLit(number, target)) -> Some (number, target, value.Span)
                            | _ -> None) }
            | _ -> None)

    /// The dimension `name` names, written at `span`, or the error saying it names none.
    let private dimensionNamed (state: InferState) (name: Identifier) (span: SourceSpan) : Result<NominalId, Diagnostic> =
        match state.TypeDefs |> Map.tryFind name with
        | Some info when info.IsDimension -> Ok (TypeDefInfo.nominalId info)
        | Some _ ->
            Error (Diagnostics.at DiagnosticCodes.UnknownType span $"'{name}' is a type, not a dimension: a unit belongs to a dimension, declared as `dimension {Names.written name}`" |> Diagnostics.about name)
        | None ->
            Error (Diagnostics.at DiagnosticCodes.UnknownType span $"There is no dimension '{name}' here: declare it with `dimension {Names.written name}`, or import the module that exports it" |> Diagnostics.about name)

    /// The errors in the storage a constrained dimension `items` declares is: a type with no `Num` instance, as `hasNum`
    /// says, can't store a quantity.
    let private storageErrors (hasNum: Ty -> bool) (state: InferState) (items: TopLevelItem list) : Diagnostic list =
        dimensionsOf items
        |> List.choose (fun (name, storage, _) ->
            match storage, state.TypeDefs |> Map.tryFind name with
            | Some storage, Some info when info.IsDimension && not (Unifier.isError info.Underlying) && not (hasNum info.Underlying) ->
                Some (
                    Diagnostics.at DiagnosticCodes.DimensionStorageNotNumber storage.Span
                        $"A dimension is stored as a number, but '{Unifier.tyToString info.Underlying}' has no `Num` instance: store '{name}' as a number type, such as `int` or `float`"
                    |> Diagnostics.about name)
            | _ -> None)

    /// The errors in the base units of the units `declared`, by their dimensions, and the dimensions `items` declare:
    /// each of the module's own dimensions has one `@baseUnit`, a base unit is no multiple of another and is declared
    /// with its dimension, and any other unit is a multiple.
    let private baseUnitErrors (state: InferState) (items: TopLevelItem list) (declared: (Declared * NominalId) list) : Diagnostic list =
        let perUnit =
            declared
            |> List.choose (fun (unit, dimension) ->
                match unit.IsBase, unit.Multiple with
                | true, Some(_, _, span) ->
                    Some (
                        Diagnostics.at DiagnosticCodes.MisplacedBaseUnit span
                            $"'{unit.Name}' is the base unit of '{unit.DimensionName}', which the others are multiples of, so it is no multiple itself: remove `@baseUnit`, or the `=` and what follows it"
                        |> Diagnostics.about unit.Name)
                | true, None when dimension.Home <> state.Home ->
                    Some (
                        Diagnostics.at DiagnosticCodes.MisplacedBaseUnit unit.Item.Span
                            $"'{unit.DimensionName}' is declared in another module, which declares its base unit: a unit of it declared here is a multiple of another unit, as `unit ms: Time = 1_000us` is"
                        |> Diagnostics.about unit.Name)
                | false, None ->
                    Some (
                        Diagnostics.at DiagnosticCodes.DimensionWithoutBaseUnit unit.Item.Span
                            $"'{unit.Name}' is no multiple of another unit, so it would be the base unit of '{unit.DimensionName}': mark it `@baseUnit`, or define it as a multiple of another unit, as `unit ms: Time = 1_000us` is"
                        |> Diagnostics.about unit.Name)
                | _ -> None)
        let perDimension =
            dimensionsOf items
            |> List.collect (fun (name, _, item) ->
                match state.TypeDefs |> Map.tryFind name with
                | Some info when info.IsDimension && info.Home = state.Home ->
                    let id = TypeDefInfo.nominalId info
                    match declared |> List.filter (fun (unit, dimension) -> dimension = id && unit.IsBase) with
                    | [] ->
                        [ Diagnostics.at DiagnosticCodes.DimensionWithoutBaseUnit item.Span
                              $"'{name}' has no base unit, the unit its values are stored in: declare one, marked `@baseUnit`, as in `@baseUnit unit ns: Time`"
                          |> Diagnostics.about name ]
                    | _ :: others ->
                        others
                        |> List.map (fun (unit, _) ->
                            Diagnostics.at DiagnosticCodes.MisplacedBaseUnit unit.Item.Span
                                $"'{name}' has a base unit already: a dimension has one, so define '{unit.Name}' as a multiple of another unit"
                            |> Diagnostics.about unit.Name)
                | _ -> [])
        perUnit @ perDimension

    /// The scale of each unit `declared`, by name, which the units `known` beside them are known by, with the errors in
    /// their definitions: a unit defined as a multiple of one in another dimension, of one there is none of, or of
    /// itself, through others or not. A unit whose scale is not found is at the scale 1, so its literals are not also
    /// reported.
    let private scales (known: Map<string, UnitInfo>) (declared: (Declared * NominalId) list) : Map<string, Rational> * Diagnostic list =
        let own = declared |> List.map (fun (unit, dimension) -> unit.Name, (unit, dimension)) |> Map.ofList
        let mutable found: Map<string, Rational> = Map.empty
        let errors = ResizeArray<Diagnostic>()
        let rec scaleOf (visiting: string list) (name: string) : Rational option =
            match found |> Map.tryFind name, own |> Map.tryFind name with
            | Some scale, _ -> Some scale
            | None, Some (unit, dimension) ->
                if List.contains name visiting then
                    let through = visiting |> List.takeWhile ((<>) name) |> List.rev |> List.map (fun name -> $"'{name}'")
                    let circle = if through.IsEmpty then "" else $", through {Diagnostics.listed through}"
                    errors.Add(
                        Diagnostics.at DiagnosticCodes.UnitCycle unit.Item.Span
                            $"'{name}' is defined as a multiple of itself{circle}, so it is no multiple of the base unit: define it as a multiple of another unit of '{unit.DimensionName}'"
                        |> Diagnostics.about name)
                    None
                else
                    let scale =
                        match unit.IsBase, unit.Multiple with
                        | true, _
                        | false, None -> Some Rational.one
                        | false, Some(number, target, span) ->
                            let targetDimension =
                                match own |> Map.tryFind target, known |> Map.tryFind target with
                                | Some (_, targetDimension), _ -> Some targetDimension
                                | None, Some info -> Some info.Dimension
                                | None, None -> None
                            match targetDimension with
                            | None ->
                                errors.Add(unknownUnit span target known)
                                None
                            | Some targetDimension when targetDimension <> dimension ->
                                errors.Add(
                                    Diagnostics.at DiagnosticCodes.UnitOfAnotherDimension span
                                        $"'{unit.Name}' is a unit of '{unit.DimensionName}', but '{target}' is a unit of '{targetDimension.Name}': define it as a multiple of a unit of '{unit.DimensionName}'"
                                    |> Diagnostics.about unit.Name)
                                None
                            | Some _ ->
                                let targetScale =
                                    if own.ContainsKey target then scaleOf (name :: visiting) target
                                    else known |> Map.tryFind target |> Option.map _.Scale
                                targetScale |> Option.map (Rational.multiply (Rational.ofWritten number))
                    scale |> Option.iter (fun scale -> found <- found |> Map.add name scale)
                    scale
            | None, None -> None
        for name in own.Keys do
            scaleOf [] name |> ignore
        found, List.ofSeq errors

    /// `state` with the units `items` declare added to those its imports brought in, each with its exact scale, and the
    /// errors in `items`' dimensions and units. `hasNum` says whether a type has a `Num` instance.
    let declare (hasNum: Ty -> bool) (state: InferState) (items: TopLevelItem list) : InferState * Diagnostic list =
        let units = declaredUnits items
        let resolved, dimensionErrors =
            units
            |> List.fold (fun (resolved, errors) unit ->
                match dimensionNamed state unit.DimensionName unit.Item.Span with
                | Ok dimension -> resolved @ [ unit, dimension ], errors
                | Error error -> resolved, errors @ [ error ]) ([], [])
        let duplicates =
            units
            |> List.groupBy _.Name
            |> List.collect (fun (name, written) ->
                written
                |> List.tail
                |> List.map (fun unit ->
                    Diagnostics.at DiagnosticCodes.DuplicateDefinition unit.Item.Span $"The unit '{name}' is declared already: give one of them another name"
                    |> Diagnostics.about name))
        let found, scaleErrors = scales state.Units resolved
        let own =
            resolved
            |> List.map (fun (unit, dimension) ->
                unit.Name,
                { Name = unit.Name
                  Dimension = dimension
                  Scale = found |> Map.tryFind unit.Name |> Option.defaultValue Rational.one
                  Home = state.Home
                  IsExport = unit.IsExport })
        let units = own |> List.fold (fun acc (name, info) -> acc |> Map.add name info) state.Units
        let errors = storageErrors hasNum state items @ dimensionErrors @ duplicates @ baseUnitErrors state items resolved @ scaleErrors
        { state with Units = units }, errors |> List.sortBy (fun error -> error.Span |> Option.map _.Start |> Option.defaultValue 0)
