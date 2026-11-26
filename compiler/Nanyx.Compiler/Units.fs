namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Units of measure: `dimension Time`, `@baseUnit unit ns: Time`, `unit ms: Time = 1_000us` and `500ms` (see
/// `UnitSyntax`). A dimension is a type: a private nominal one, generic in the number that stores it, `Time(a)`, unless
/// it is constrained to one, as `dimension Bytes(int)` is, so it is exported, imported and written as a type is. A
/// unit is a name of a kind of its own, with its dimension and how many of the dimension's base units it is, exactly
/// (`InferState.Units`), which a number with the unit is scaled by (see `NumericLiterals.check`). A unit of a derived
/// dimension, `unit kmh: Speed = 1km / 1hr`, is defined from its parts' units, and is how many of their base units it
/// is, multiplied and divided as the dimension's parts are: a kilometre an hour is 1/3_600_000_000 of a metre a nanosecond.
module internal Units =
    /// The error for `unitName`, written at `span` in a literal or a unit's definition, when the module knows no unit of
    /// that name.
    let private unknownUnit (span: SourceSpan) (unitName: Identifier) (known: Map<string, UnitInfo>) : Diagnostic =
        let suggestion =
            match NameSuggestions.closest known.Keys unitName with
            | close :: _ -> $": did you mean '{close}'?"
            | [] -> ": declare it with `unit`, or import the module that exports it"
        Diagnostics.at DiagnosticCodes.UnknownUnit span $"There is no unit '{unitName}' here{suggestion}"
        |> Diagnostics.about unitName

    /// The error for `unitName`, written at `span`, when two of the module's imports bring in a unit of that name.
    let private ambiguousUnit (span: SourceSpan) (unitName: Identifier) (each: UnitInfo list) : Diagnostic =
        let dimensions = each |> List.map (fun info -> $"'{Dimension.text info.Dimension}'") |> List.distinct
        let ofWhat = if dimensions.Length > 1 then $" (of {Diagnostics.listed dimensions})" else ""
        Diagnostics.at DiagnosticCodes.AmbiguousName span
            $"Two imports bring in a unit '{unitName}'{ofWhat}: import it from only one of them, selecting the names the other's import brings in, as `import time as (Time, s)`"
        |> Diagnostics.about unitName

    /// The unit `unitName`, written at `span`, as `state` knows it, or the error saying it knows none, or more than one.
    let unitNamed (state: InferState) (span: SourceSpan) (unitName: Identifier) : Result<UnitInfo, Diagnostic> =
        match state.AmbiguousUnits |> Map.tryFind unitName, state.Units |> Map.tryFind unitName with
        | Some each, _ -> Error (ambiguousUnit span unitName each)
        | None, Some info -> Ok info
        | None, None -> Error (unknownUnit span unitName state.Units)

    /// The intrinsic that is the number a measured value is stored as, as it is: what a derived `toString` of one
    /// writes (see `StructuralInstances`). It has no scheme, as no type can name every dimension: `Infer` types a call
    /// of it, in any module, as the storage of its argument.
    let storageIntrinsic = "__nanyx_measured_storage"

    /// The units of the dimension `dimension`, plain or derived, that `state`'s module can write, its own and those it
    /// imports by a name no other unit it imports has, the largest first.
    let seenOf (state: InferState) (dimension: Dimension) : UnitInfo list =
        state.Units
        |> Map.toList
        |> List.filter (fun (name, info) -> info.Dimension = dimension && not (state.AmbiguousUnits.ContainsKey name))
        |> List.map snd
        |> List.sortWith (fun a b -> compare (b.Scale.Numerator * a.Scale.Denominator) (a.Scale.Numerator * b.Scale.Denominator))

    /// The storage the dimension `dimension` is constrained to, as `dimension Bytes(int)` is to `int`, if it is. A derived
    /// dimension is constrained by its parts' types, which a number with one of its units is checked against as a value
    /// of it.
    let constrainedStorage (state: InferState) (dimension: Dimension) : Ty option =
        match dimension with
        | [ id, 1 ] ->
            state.TypeDefs
            |> Map.values
            |> Seq.tryFind (fun info -> info.IsDimension && TypeDefInfo.nominalId info = id)
            |> Option.filter (fun info -> info.Parameters.IsEmpty)
            |> Option.map _.Underlying
        | _ -> None

    /// The prelude's contexts every dimension has an instance of: its storage's, since a measured value is its storage
    /// at run time. It has no `Num`: `Num`'s `*` takes two values of one type, and a `Time` times a `Time` is no `Time`.
    let contexts = set [ "Add"; "Sub" ]

    /// `argTys`, the arguments of the context `ctxName` that a call finds an instance of, with a measured value read as
    /// its storage where the context is one every dimension has (see `contexts`).
    let storageArguments (state: InferState) (ctxName: Identifier) (argTys: Ty list) : Ty list =
        match argTys with
        | [ ty ] when contexts.Contains ctxName ->
            match Unifier.unalias (resolveSoFar state ty) with
            | TyMeasure(_, storage) -> [ storage ]
            | _ -> argTys
        | _ -> argTys

    /// The parameter a dimension that is not constrained is generic in: the number that stores it.
    let private storageParameter = "a"

    /// `derived`, a derived dimension, with each dimension it names stored as `storage`: `Length(a) / Time(a)`.
    let rec private storedAs (storage: Identifier) (derived: TypeExpr) : TypeExpr =
        match derived.Kind with
        | TypeName name -> TypeExpr.at derived.Span (TypeApply(name, [ TypeExpr.at derived.Span (TypeVar storage) ]))
        | TypeProduct _
        | TypeQuotient _ -> TypeExpr.mapChildren (storedAs storage) derived
        | _ -> derived

    /// `items` with each dimension's declaration as the type definition it stands for, `type Time(a) = private a` or
    /// `type Bytes = private int`, for the type definitions to be registered with (see `TypeResolution.registerTypeDef`).
    /// A derived dimension, `dimension Speed = Length / Time`, is an alias of what it names, generic in its storage as
    /// its parts are: `type Speed(a) = Length(a) / Time(a)`, so it is the same dimension wherever it is written.
    let withDimensionsAsTypes (items: TopLevelItem list) : TopLevelItem list =
        items
        |> List.map (fun item ->
            match item.Kind with
            | DimensionDecl(isExport, name, _, Some derived) ->
                let modifiers = if isExport then [ Export ] else []
                { item with Kind = Def (TypeDef(name, modifiers, [ storageParameter, None ], storedAs storageParameter derived)) }
            | DimensionDecl(isExport, name, storage, None) ->
                let modifiers = (if isExport then [ Export ] else []) @ [ Private ]
                let parameters, body =
                    match storage with
                    | Some storage -> [], storage
                    | None -> [ storageParameter, None ], TypeExpr.at item.Span (TypeVar storageParameter)
                { item with Kind = Def (TypeDef(name, modifiers, parameters, body)) }
            | _ -> item)

    /// The dimensions `items` declare, each with its declaration.
    let private dimensionsOf (items: TopLevelItem list) =
        items |> List.choose (fun item -> match item.Kind with DimensionDecl(_, name, storage, _) -> Some (name, storage, item) | _ -> None)

    /// `state` with each dimension `items` declare marked as one among its type definitions.
    let markDimensions (items: TopLevelItem list) (state: InferState) : InferState =
        { state with
            TypeDefs =
                dimensionsOf items
                |> List.fold (fun typeDefs (name, _, _) ->
                    match typeDefs |> Map.tryFind name with
                    | Some info when info.Home = state.Home -> typeDefs |> Map.add name { info with IsDimension = true }
                    | _ -> typeDefs) state.TypeDefs }

    /// A unit's declaration as the module writes it: its item, name and dimension, whether `@baseUnit` marks it, and what
    /// it is defined as: a number of another unit, `1_000us`, or numbers of units multiplied and divided, `1km / 1hr`.
    type private Declared =
        { Item: TopLevelItem
          Name: Identifier
          IsExport: bool
          DimensionName: Identifier
          IsBase: bool
          Multiple: Expression option }

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
                      Multiple = multiple }
            | _ -> None)

    /// The dimension the type definition `info` declares: the dimension itself, or, for a derived dimension, what it
    /// names, `Length / Time` for `Speed`. None for any other type.
    let private dimensionOf (info: TypeDefInfo) : Dimension option =
        match info.IsDimension, info.IsNominal, Unifier.unalias info.Underlying with
        | true, true, _ -> Some (Dimension.ofBase (TypeDefInfo.nominalId info))
        | true, false, TyMeasure(dimension, _) -> Some dimension
        | _ -> None

    /// The dimension `name` names, written at `span`, plain or derived, or the error saying it names none.
    let private dimensionNamed (state: InferState) (name: Identifier) (span: SourceSpan) : Result<Dimension, Diagnostic> =
        match state.TypeDefs |> Map.tryFind name with
        | Some info when (dimensionOf info).IsSome -> Ok (dimensionOf info).Value
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
    /// with its dimension, and any other unit is a multiple. A derived dimension has no base unit of its own, as its
    /// values are stored in its parts' base units: each of its units is defined from its parts' units.
    let private baseUnitErrors (state: InferState) (items: TopLevelItem list) (declared: (Declared * Dimension) list) : Diagnostic list =
        let fromParts (unit: Declared) (why: string) =
            Diagnostics.at DiagnosticCodes.UnitOfDerivedDimension unit.Item.Span
                $"'{unit.DimensionName}' is a derived dimension, {why}: define '{unit.Name}' from its parts' units, as in `unit kmh: Speed = 1km / 1hr`"
            |> Diagnostics.about unit.Name
        let perUnit =
            declared
            |> List.choose (fun (unit, dimension) ->
                match unit.IsBase, unit.Multiple, dimension with
                | true, _, _ when Dimension.isDerived dimension ->
                    Some (fromParts unit "whose values are stored in its parts' base units, so it has no base unit of its own")
                | false, None, _ when Dimension.isDerived dimension ->
                    Some (fromParts unit "whose units are multiples of its parts' base units")
                | true, Some value, _ ->
                    Some (
                        Diagnostics.at DiagnosticCodes.MisplacedBaseUnit value.Span
                            $"'{unit.Name}' is the base unit of '{unit.DimensionName}', which the others are multiples of, so it is no multiple itself: remove `@baseUnit`, or the `=` and what follows it"
                        |> Diagnostics.about unit.Name)
                | true, None, [ id, 1 ] when id.Home <> state.Home ->
                    Some (
                        Diagnostics.at DiagnosticCodes.MisplacedBaseUnit unit.Item.Span
                            $"'{unit.DimensionName}' is declared in another module, which declares its base unit: a unit of it declared here is a multiple of another unit, as `unit ms: Time = 1_000us` is"
                        |> Diagnostics.about unit.Name)
                | false, None, _ ->
                    Some (
                        Diagnostics.at DiagnosticCodes.DimensionWithoutBaseUnit unit.Item.Span
                            $"'{unit.Name}' is no multiple of another unit, so it would be the base unit of '{unit.DimensionName}': mark it `@baseUnit`, or define it as a multiple of another unit, as `unit ms: Time = 1_000us` is"
                        |> Diagnostics.about unit.Name)
                | _ -> None)
        let perDimension =
            dimensionsOf items
            |> List.collect (fun (name, _, item) ->
                match state.TypeDefs |> Map.tryFind name with
                | Some info when info.IsDimension && info.IsNominal && info.Home = state.Home ->
                    let id = TypeDefInfo.nominalId info
                    match declared |> List.filter (fun (unit, dimension) -> dimension = Dimension.ofBase id && unit.IsBase) with
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

    /// `value`, what a unit is defined as, as written, with each number written by `number`: `1_000us`, `1km / 1hr`.
    let rec internal writtenWith (number: WrittenNumber -> string) (value: Expression) : string =
        match value.Kind with
        | LiteralExpr(UnitLit(written, unitName)) -> number written + unitName
        | BinaryOp(op, _, left, right) -> $"{writtenWith number left} {op} {writtenWith number right}"
        | _ -> "it"

    let private written = writtenWith WrittenNumber.text

    /// The error for `unit`, a unit of `dimension`, defined as `value`, a value of `found`, another dimension.
    let private ofAnotherDimension (unit: Declared) (dimension: Dimension) (value: Expression) (found: Dimension) : Diagnostic =
        let message =
            match value.Kind with
            | LiteralExpr(UnitLit(_, target)) when not (Dimension.isDerived dimension) ->
                $"'{unit.Name}' is a unit of '{unit.DimensionName}', but '{target}' is a unit of '{Dimension.text found}': define it as a multiple of a unit of '{unit.DimensionName}'"
            | _ ->
                let named = if Dimension.text dimension = unit.DimensionName then "" else $", which is '{Dimension.text dimension}'"
                $"'{unit.Name}' is a unit of '{unit.DimensionName}'{named}, but `{written value}` is a '{Dimension.text found}': define it from units of '{Dimension.text dimension}'"
        Diagnostics.at DiagnosticCodes.UnitOfAnotherDimension value.Span message |> Diagnostics.about unit.Name

    /// The error for `unit`, defined as `value`, whose part `nothing` is worth nothing: no multiple of it is a unit, and a
    /// unit divided by it is none either.
    let private worthNothing (unit: Declared) (nothing: Expression) : Diagnostic =
        Diagnostics.at DiagnosticCodes.UnitWorthNothing nothing.Span
            $"`{written nothing}` is nothing, so '{unit.Name}' would be no quantity at all: define it from a number of units that isn't zero"
        |> Diagnostics.about unit.Name

    /// The scale of each unit `declared`, by name, which the units `known` beside them are known by, with the errors in
    /// their definitions: a unit defined as a multiple of one in another dimension, of one there is none of, of
    /// itself, through others or not, or of nothing. A unit of a derived dimension is defined from its parts' units, and
    /// its scale is theirs, multiplied and divided as they are: `1km / 1hr` is 1_000 metres over 3_600_000_000_000
    /// nanoseconds. A unit whose scale is not found is at the scale 1, so its literals are not also reported.
    let private scales (state: InferState) (declared: (Declared * Dimension) list) : Map<string, Rational> * Diagnostic list =
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
                        | false, Some value ->
                            match measure (name :: visiting) unit value with
                            | Some (valueDimension, _) when valueDimension <> dimension ->
                                errors.Add(ofAnotherDimension unit dimension value valueDimension)
                                None
                            | Some (_, scale) when scale.Numerator.IsZero ->
                                errors.Add(worthNothing unit value)
                                None
                            | measured -> measured |> Option.map snd
                    scale |> Option.iter (fun scale -> found <- found |> Map.add name scale)
                    scale
            | None, None -> None
        // The dimension and scale of `value`, part of `unit`'s definition: a number of a unit, or a product or quotient
        // of two such.
        and measure (visiting: string list) (unit: Declared) (value: Expression) : (Dimension * Rational) option =
            match value.Kind with
            | LiteralExpr(UnitLit(number, target)) ->
                let targetDimension =
                    match own |> Map.tryFind target with
                    | Some (_, targetDimension) -> Ok targetDimension
                    | None -> unitNamed state value.Span target |> Result.map _.Dimension
                match targetDimension with
                | Error error ->
                    errors.Add error
                    None
                | Ok targetDimension ->
                    let targetScale =
                        if own.ContainsKey target then scaleOf visiting target
                        else state.Units |> Map.tryFind target |> Option.map _.Scale
                    targetScale |> Option.map (fun scale -> targetDimension, Rational.multiply (Rational.ofWritten number) scale)
            | BinaryOp(op, _, left, right) ->
                match measure visiting unit left, measure visiting unit right with
                | Some (leftDimension, leftScale), Some (rightDimension, rightScale) ->
                    if op = UnitSyntax.times then Some (Dimension.multiply leftDimension rightDimension, Rational.multiply leftScale rightScale)
                    elif rightScale.Numerator.IsZero then
                        errors.Add(worthNothing unit right)
                        None
                    else Some (Dimension.divide leftDimension rightDimension, Rational.divide leftScale rightScale)
                | _ -> None
            | _ -> None
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
        let found, scaleErrors = scales state resolved
        let own =
            resolved
            |> List.map (fun (unit, dimension) ->
                unit.Name,
                { Name = unit.Name
                  Dimension = dimension
                  Scale = found |> Map.tryFind unit.Name |> Option.defaultValue Rational.one
                  Home = state.Home
                  IsExport = unit.IsExport
                  Multiple = unit.Multiple })
        let units = own |> List.fold (fun acc (name, info) -> acc |> Map.add name info) state.Units
        let errors = storageErrors hasNum state items @ dimensionErrors @ duplicates @ baseUnitErrors state items resolved @ scaleErrors
        // A unit of the module's own is the one its name means, though two imports bring in others of the name.
        let ambiguous = own |> List.fold (fun acc (name, _) -> acc |> Map.remove name) state.AmbiguousUnits
        { state with Units = units; AmbiguousUnits = ambiguous }, errors |> List.sortBy (fun error -> error.Span |> Option.map _.Start |> Option.defaultValue 0)
