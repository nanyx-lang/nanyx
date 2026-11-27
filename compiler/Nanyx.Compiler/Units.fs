namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Units of measure: `dimension Time`, `@storageUnit unit ns: Time`, `unit ms: Time = 1_000us` and `500ms` (see
/// `UnitSyntax`). A dimension is a type: a private nominal one, generic in the number that stores it, `Time(a)`, unless
/// it is constrained to one, as `dimension Bytes(int)` is, so it is exported, imported and written as a type is. A
/// unit is a name of a kind of its own, with its dimension and how many of the dimension's default storage units it
/// is, exactly (`InferState.Units`), which a number with the unit is scaled by (see `NumericLiterals.check`). The
/// default storage unit is worked out from the units declared with the dimension (see `storageUnits`), and `@baseUnit`
/// marks the default display unit. A unit of a derived dimension, `unit kmh: Speed = 1km / 1hr`, is defined from its
/// parts' units, and is how many of their default storage units it is, multiplied and divided as the dimension's parts
/// are: a kilometre an hour is 1/3_600_000_000 of a metre a nanosecond.
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
        // The dimension's name keeps its node, which a reference to the dimension is recorded at.
        | TypeName name -> { derived with Kind = TypeApply(name, [ TypeExpr.at derived.Span (TypeVar storage) ]) }
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

    /// The symbol of the name the `dimension` or `unit` line `item` declares, `kind` and exported if `isExport`, made
    /// the first time it is asked for. A dimension's, a type, and a unit's are named, not in scope, so they have no
    /// scope, as a type definition has none.
    let private declaredSymbol (item: TopLevelItem) (name: Identifier) (kind: SymbolKind) (isExport: bool) (state: InferState) =
        let symbol, declared = declareSymbol MeasureDeclaration item.Id name kind isExport None state
        symbol, { declared with Scopes = symbol |> Option.map (fun id -> declared.Scopes |> Map.remove id) |> Option.defaultValue declared.Scopes }

    /// `state` with each dimension `items` declare marked as one among its type definitions, with the symbol of its name,
    /// which a type that names it refers to.
    let markDimensions (items: TopLevelItem list) (state: InferState) : InferState =
        dimensionsOf items
        |> List.fold
            (fun (state: InferState) (name, _, item) ->
                match state.TypeDefs |> Map.tryFind name, item.Kind with
                | Some info, DimensionDecl(isExport, _, _, _) when info.Home = state.Home ->
                    let symbol, state = declaredSymbol item name TypeDefinition isExport state
                    { state with TypeDefs = state.TypeDefs |> Map.add name { info with IsDimension = true; Symbol = info.Symbol |> Option.orElse symbol } }
                | _ -> state)
            state

    /// A unit's declaration as the module writes it: its item, name and dimension, the attributes that mark it,
    /// `@storageUnit` and `@baseUnit`, and what it is defined as: a number of another unit, `1_000us`, or numbers of
    /// units multiplied and divided, `1km / 1hr`.
    type private Declared =
        { Item: TopLevelItem
          Name: Identifier
          IsExport: bool
          /// Its dimension, as the `unit` line writes it, a `TypeName`.
          Dimension: TypeExpr
          DimensionName: Identifier
          Marks: UnitMark list
          Multiple: Expression option }

    /// The attribute that `mark` is written as.
    let attributeOf (mark: UnitMark) =
        match mark with
        | StorageUnitMark -> UnitSyntax.storageUnitAttribute
        | DisplayUnitMark -> UnitSyntax.baseUnitAttribute

    let private declaredUnits (items: TopLevelItem list) : Declared list =
        UnitSyntax.unitsWithAttributes items
        |> List.choose (fun (item, attributes) ->
            match item.Kind with
            | UnitDecl(isExport, name, dimension, multiple) ->
                Some
                    { Item = item
                      Name = name
                      IsExport = isExport
                      Dimension = dimension
                      DimensionName = match dimension.Kind with TypeName name -> name | _ -> ""
                      Marks = attributes |> List.map (fun name -> if name = UnitSyntax.storageUnitAttribute then StorageUnitMark else DisplayUnitMark)
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
                        $"A dimension is stored as a number, but '{Unifier.typeText state.TypeDefs info.Underlying}' has no `Num` instance: store '{name}' as a number type, such as `int` or `float`"
                    |> Diagnostics.about name)
            | _ -> None)

    /// The module's own dimensions, each with its identity, name and declaration, that are not derived.
    let private ownDimensions (state: InferState) (items: TopLevelItem list) : (NominalId * Identifier * TopLevelItem) list =
        dimensionsOf items
        |> List.choose (fun (name, _, item) ->
            match state.TypeDefs |> Map.tryFind name with
            | Some info when info.IsDimension && info.IsNominal && info.Home = state.Home -> Some (TypeDefInfo.nominalId info, name, item)
            | _ -> None)

    /// The errors in how the units `declared`, by their dimensions, are defined and marked, and in the dimensions
    /// `items` declare: each of the module's own dimensions has one unit that is no multiple of another, which the
    /// others are defined from, and at most one unit marked `@storageUnit` and one marked `@baseUnit`. A unit of a
    /// dimension declared in another module is a multiple of a unit of it, and unmarked, as that module decides how the
    /// dimension is stored and written. A derived dimension has no default storage or display unit of its own, as its
    /// values are stored in its parts' default storage units: each of its units is defined from its parts' units.
    let private unitErrors (state: InferState) (items: TopLevelItem list) (declared: (Declared * Dimension) list) : Diagnostic list =
        let fromParts (unit: Declared) (why: string) =
            Diagnostics.at DiagnosticCodes.UnitOfDerivedDimension unit.Item.Span
                $"'{unit.DimensionName}' is a derived dimension, {why}: define '{unit.Name}' from its parts' units, as in `unit kmh: Speed = 1km / 1hr`"
            |> Diagnostics.about unit.Name
        let perUnit =
            declared
            |> List.collect (fun (unit, dimension) ->
                match unit.Multiple, dimension with
                | _ when Dimension.isDerived dimension && not unit.Marks.IsEmpty ->
                    [ fromParts unit "whose values are stored in its parts' default storage units and displayed in their default display units, so it has no default storage or display unit of its own" ]
                | None, _ when Dimension.isDerived dimension -> [ fromParts unit "whose units are multiples of its parts' default storage units" ]
                | multiple, [ id, 1 ] when id.Home <> state.Home ->
                    let marked =
                        unit.Marks
                        |> List.distinct
                        |> List.map (fun mark ->
                            Diagnostics.at DiagnosticCodes.MisplacedUnitAttribute unit.Item.Span
                                $"'{unit.DimensionName}' is declared in another module, which decides its default storage and display units: remove `@{attributeOf mark}`"
                            |> Diagnostics.about unit.Name)
                    let first =
                        match multiple with
                        | None ->
                            [ Diagnostics.at DiagnosticCodes.UnitsNotFromOne unit.Item.Span
                                  $"'{unit.DimensionName}' is declared in another module, which declares the unit its others are defined from: define '{unit.Name}' as a multiple of a unit of it, as `unit ms: Time = 1_000us` is"
                              |> Diagnostics.about unit.Name ]
                        | Some _ -> []
                    marked @ first
                | _ -> [])
        let perDimension =
            ownDimensions state items
            |> List.collect (fun (id, name, item) ->
                let units = declared |> List.filter (fun (_, dimension) -> dimension = Dimension.ofBase id) |> List.map fst
                let first =
                    match units |> List.filter (fun unit -> unit.Multiple.IsNone) with
                    | [] when units.IsEmpty ->
                        [ Diagnostics.at DiagnosticCodes.UnitsNotFromOne item.Span
                              $"'{name}' has no units: declare one, as in `unit ns: Time`, and define its others as multiples of it"
                          |> Diagnostics.about name ]
                    // Units that are all multiples are defined in a circle, which `scales` reports.
                    | [] -> []
                    | first :: others ->
                        others
                        |> List.map (fun unit ->
                            Diagnostics.at DiagnosticCodes.UnitsNotFromOne unit.Item.Span
                                $"'{unit.Name}' is no multiple of another unit, but '{first.Name}' is the one the units of '{name}' are defined from: define '{unit.Name}' as a multiple of a unit of '{name}', as `unit ms: Time = 1_000us` is"
                            |> Diagnostics.about unit.Name)
                let markedTwice (mark: UnitMark) (what: string) =
                    match units |> List.collect (fun unit -> unit.Marks |> List.filter ((=) mark) |> List.map (fun _ -> unit)) with
                    | [] -> []
                    | _ :: others ->
                        others
                        |> List.map (fun unit ->
                            Diagnostics.at DiagnosticCodes.MisplacedUnitAttribute unit.Item.Span
                                $"'{name}' has a unit marked `@{attributeOf mark}` already: a dimension has one {what}, so remove `@{attributeOf mark}` from '{unit.Name}'"
                            |> Diagnostics.about unit.Name)
                first @ markedTwice StorageUnitMark "default storage unit" @ markedTwice DisplayUnitMark "default display unit")
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

    /// The scale of each unit `declared`, by name, with the errors in their definitions: a unit defined as a multiple
    /// of one in another dimension, of one there is none of, of itself, through others or not, or of nothing. A unit of
    /// one of the module's own dimensions is scaled to the unit its others are defined from, the one that is no
    /// multiple, and any other unit to its dimension's default storage unit, as an imported unit is. A unit of a
    /// derived dimension is defined from its parts' units, and its scale is theirs, multiplied and divided as they are:
    /// `1km / 1hr` is 1_000 metres over 3_600_000_000_000 nanoseconds.
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
                            $"'{name}' is defined as a multiple of itself{circle}, so it is no multiple of the unit the others are defined from: define it as a multiple of another unit of '{unit.DimensionName}'"
                        |> Diagnostics.about name)
                    None
                else
                    let scale =
                        match unit.Multiple with
                        | None -> Some Rational.one
                        | Some value ->
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

    /// The default storage unit of each of the module's own dimensions, by the dimension's identity, as the name of the
    /// unit its values are displayed in if `@baseUnit` marks none and its scale in `found`, with the errors in them. It
    /// is the unit `@storageUnit` marks, or else the largest unit that every unit declared with the dimension is a whole
    /// number of. If no declared unit is that one it is made up, and its values are displayed in the smallest unit it is
    /// an exact decimal of: with `mm` and `inch = 25.4mm`, it is 0.2mm, displayed in `mm`. A dimension with a unit whose
    /// scale isn't found has none, as the unit's definition is reported.
    let private storageUnits (state: InferState) (items: TopLevelItem list) (declared: (Declared * Dimension) list) (found: Map<string, Rational>) =
        let chosen =
            ownDimensions state items
            |> List.choose (fun (dimensionId, name, item) ->
                let units = declared |> List.filter (fun (_, dimension) -> dimension = Dimension.ofBase dimensionId) |> List.map fst
                let scales = units |> List.map (fun unit -> found |> Map.tryFind unit.Name |> Option.map (fun scale -> unit, scale))
                if units.IsEmpty || scales |> List.exists Option.isNone then None
                else
                    let scales = scales |> List.choose id
                    match scales |> List.tryFind (fun (unit, _) -> List.contains StorageUnitMark unit.Marks) with
                    | Some (storage, storageScale) ->
                        let errors =
                            scales
                            |> List.filter (fun (_, scale) -> not (Rational.isWhole (Rational.divide scale storageScale)))
                            |> List.map (fun (unit, scale) ->
                                Diagnostics.at DiagnosticCodes.UnitNotWholeStorageUnits unit.Item.Span
                                    $"'{unit.Name}' is {Rational.text (Rational.divide scale storageScale)} of '{storage.Name}', the default storage unit of '{name}' that `@{UnitSyntax.storageUnitAttribute}` marks, but every unit of '{name}' declared with it is a whole number of its default storage unit: mark a unit that '{unit.Name}' is a whole number of `@{UnitSyntax.storageUnitAttribute}`, or mark none to have one worked out"
                                |> Diagnostics.about unit.Name)
                        Some (dimensionId, (storage.Name, storageScale), errors)
                    | None ->
                        let divisor = scales |> List.map snd |> List.reduce Rational.greatestCommonDivisor
                        match scales |> List.tryFind (fun (_, scale) -> scale = divisor) with
                        | Some (storage, _) -> Some (dimensionId, (storage.Name, divisor), [])
                        | None ->
                            // No unit is one, so the storage unit is made up, with no name. Each unit's scale is a
                            // decimal times another's, so it is a decimal of one of them, and its values are displayed
                            // in the smallest of those.
                            let display =
                                Rational.smallestDecimalOf divisor scales
                                |> Option.map fst
                                |> Option.defaultValue (fst scales.Head)
                            Some (dimensionId, (display.Name, divisor), []))
        let storage = chosen |> List.map (fun (id, storage, _) -> id, storage) |> Map.ofList
        storage, chosen |> List.collect (fun (_, _, errors) -> errors)

    /// The errors in the units `declared` of dimensions declared in other modules, scaled to their default storage
    /// units as `found` has them: each is a whole number of its dimension's default storage unit, so that a unit
    /// declared outside a dimension's module never changes what its values hold.
    let private importedDimensionErrors (state: InferState) (declared: (Declared * Dimension) list) (found: Map<string, Rational>) : Diagnostic list =
        declared
        |> List.choose (fun (unit, dimension) ->
            match dimension, found |> Map.tryFind unit.Name with
            | [ id, 1 ], Some scale when id.Home <> state.Home && not (Rational.isWhole scale) ->
                // A declared unit is named in quotes, and one made up is written as the decimal it is, 0.2mm.
                let units = state.Units |> Map.values |> Seq.filter (fun info -> info.Dimension = dimension) |> List.ofSeq
                let storage =
                    match units |> List.tryFind UnitInfo.isStorage with
                    | Some storage -> Some $"'{storage.Name}'"
                    | None -> UnitInfo.storageText units
                let what =
                    match storage with
                    | Some storage -> $"'{unit.Name}' is {Rational.text scale} of {storage}, the default storage unit of '{unit.DimensionName}'"
                    | None -> $"'{unit.Name}' is no whole number of the default storage unit of '{unit.DimensionName}'"
                Some (
                    Diagnostics.at DiagnosticCodes.UnitNotWholeStorageUnits unit.Item.Span
                        $"{what}, which is declared in another module: a unit of it declared here is a whole number of its default storage unit, so that it doesn't change what a '{unit.DimensionName}' holds"
                    |> Diagnostics.about unit.Name)
            | _ -> None)

    /// `scale`, a unit of `dimension`'s scale as `scales` finds it, scaled to the default storage units `storage` of
    /// the module's own dimensions, by their identities.
    let private inStorageUnits (storage: Map<NominalId, string * Rational>) (dimension: Dimension) (scale: Rational) : Rational =
        dimension
        |> List.fold (fun scale (id, power) ->
            match storage |> Map.tryFind id with
            | Some (_, storageScale) -> Rational.divide scale (Rational.power storageScale power)
            | None -> scale) scale

    /// Each number of a unit that the definitions of `units` write, with the unit it is written with: `1_000us` in
    /// `unit ms: Time = 1_000us`, and both of `1km / 1hr`.
    let private writtenUnits (units: Declared list) : (Expression * Identifier) list =
        let rec within (value: Expression) =
            match value.Kind with
            | LiteralExpr(UnitLit(_, unitName)) -> [ value, unitName ]
            | BinaryOp(_, _, left, right) -> within left @ within right
            | _ -> []
        units |> List.collect (fun unit -> unit.Multiple |> Option.toList |> List.collect within)

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
        let storage, storageUnitErrors = storageUnits state items resolved found
        // A dimension's default display unit is the unit `@baseUnit` marks, or else its default storage unit.
        let isDisplay (unit: Declared) (dimension: Dimension) =
            match dimension with
            | [ id, 1 ] when id.Home = state.Home ->
                let marked = resolved |> List.tryFind (fun (other, otherDimension) -> otherDimension = dimension && List.contains DisplayUnitMark other.Marks)
                match marked, storage |> Map.tryFind id with
                | Some (marked, _), _ -> obj.ReferenceEquals(marked, unit)
                | None, Some (storageName, _) -> unit.Name = storageName
                | None, None -> false
            | _ -> false
        // A unit whose scale is not found is at the scale 1, so its literals are not also reported.
        let own, state =
            resolved
            |> List.mapFold
                (fun state (unit, dimension) ->
                    let symbol, state = declaredSymbol unit.Item unit.Name UnitOfMeasure unit.IsExport state
                    (unit.Name,
                     { Name = unit.Name
                       Dimension = dimension
                       Scale = found |> Map.tryFind unit.Name |> Option.map (inStorageUnits storage dimension) |> Option.defaultValue Rational.one
                       Home = state.Home
                       IsExport = unit.IsExport
                       Multiple = unit.Multiple
                       Marks = unit.Marks
                       IsDisplay = isDisplay unit dimension
                       Symbol = symbol }),
                    state)
                state
        let units = own |> List.fold (fun acc (name, info) -> acc |> Map.add name info) state.Units
        let errors =
            storageErrors hasNum state items @ dimensionErrors @ duplicates @ unitErrors state items resolved @ scaleErrors
            @ storageUnitErrors @ importedDimensionErrors state resolved found
        // A unit of the module's own is the one its name means, though two imports bring in others of the name.
        let ambiguous = own |> List.fold (fun acc (name, _) -> acc |> Map.remove name) state.AmbiguousUnits
        let state = { state with Units = units; AmbiguousUnits = ambiguous }
        // Each `unit` line's dimension refers to it, as a type that names it does, and each number of a unit that a
        // unit's definition writes refers to that unit.
        let state =
            resolved
            |> List.fold (fun state (unit, _) ->
                match state.TypeDefs |> Map.tryFind unit.DimensionName with
                | Some info -> recordTypeReference unit.Dimension info state
                | None -> state) state
        let state =
            writtenUnits (resolved |> List.map fst)
            |> List.fold (fun state (literal: Expression, unitName) ->
                match unitNamed state literal.Span unitName with
                | Ok info -> recordUnitReference literal info state
                | Error _ -> state) state
        state, errors |> List.sortBy (fun error -> error.Span |> Option.map _.Start |> Option.defaultValue 0)
