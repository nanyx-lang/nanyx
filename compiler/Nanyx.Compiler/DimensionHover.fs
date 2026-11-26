/// What hovering a dimension or a unit shows (see `Hover.unitAt`): a dimension's declaration and the units that make
/// values of it, so a reader sees what to write, and a unit's declaration and its dimension.
module internal NyxCompiler.DimensionHover

open Parser.Program

/// The units of `dimension`, plain or derived, that the module `typed` can write, its own and those it imports, from
/// the smallest to the largest.
let unitsOf (typed: TypedModule) (dimension: Dimension) : UnitInfo list =
    typed.Units
    |> Map.values
    |> Seq.filter (fun info -> info.Dimension = dimension)
    |> List.ofSeq
    |> List.sortWith (fun a b -> compare (a.Scale.Numerator * b.Scale.Denominator) (b.Scale.Numerator * a.Scale.Denominator))

/// `number`, a number as text, with the digits before its point in groups of three, as a literal is written to read:
/// `1_000`, `1_000_000`, `0.001`. A number of fewer than four digits is as it is.
let private grouped (number: string) =
    let digitsEnd = number |> Seq.tryFindIndex (fun c -> not (System.Char.IsDigit c)) |> Option.defaultValue number.Length
    let whole, rest = number.Substring(0, digitsEnd), number.Substring digitsEnd
    if whole.Length < 4 then number
    else
        let groups = [ for stop in whole.Length .. -3 .. 1 -> whole.Substring(max 0 (stop - 3), min 3 stop) ] |> List.rev
        String.concat "_" groups + rest

/// What `info` is declared as, as a literal writes it: `1_000us`, or `1km / 1hr`.
let private multipleOf (info: UnitInfo) =
    info.Multiple |> Option.map (Units.writtenWith (WrittenNumber.text >> grouped))

/// A unit as the list of a dimension's units writes it: `us = 1_000ns`, or `ns (base)`.
let private listed (info: UnitInfo) =
    match multipleOf info with
    | Some multiple -> $"`{info.Name} = {multiple}`"
    | None -> $"`{info.Name}` (base)"

/// What a hover says of the units of `dimension` after its declaration: each unit the module can write, from the
/// smallest to the largest, with how it is declared, and a value written in one of them. None when it can write none.
let unitsNote (typed: TypedModule) (dimension: Dimension) : string option =
    match unitsOf typed dimension with
    | [] -> None
    | units ->
        // A unit from the smaller half, as `ms` is of `Time`'s, which a value is often written in.
        let example = units[max 0 ((units.Length - 1) / 2 - 1)]
        Some $"""Units: {units |> List.map listed |> String.concat ", "}

Write a value as `500{example.Name}`."""

/// What a hover says of a derived dimension the module can write no unit of.
let private noUnitsNote =
    "It has no units here: write a value of it from its parts' units, as `100m / 9.58s`, or declare one, as `unit kmh: Speed = 1km / 1hr`."

/// The declaration of the dimension `info` defines, as hover writes it, and what it says after it: `dimension Time` and
/// its units, `dimension Bytes(int)`, or `dimension Speed = Length / Time` and its units.
let ofDimension (typed: TypedModule) (info: TypeDefInfo) : string * string option =
    if info.IsNominal then
        let storage = if info.Parameters.IsEmpty then $"({Unifier.tyToString info.Underlying})" else ""
        $"{UnitSyntax.dimensionKeyword} {info.Name}{storage}", unitsNote typed (Dimension.ofBase (TypeDefInfo.nominalId info))
    else
        match Unifier.unalias info.Underlying with
        | TyMeasure(dimension, _) ->
            $"{UnitSyntax.dimensionKeyword} {info.Name} = {Dimension.text dimension}",
            unitsNote typed dimension |> Option.orElse (Some noUnitsNote)
        | _ -> $"{UnitSyntax.dimensionKeyword} {info.Name}", None

/// The declaration of the unit `info`, as hover writes it, `unit ms: Time = 1_000us` or `@baseUnit unit ns: Time`, and
/// what it says after it: how many of its dimension's base units it is, and the dimension's units.
let ofUnit (typed: TypedModule) (info: UnitInfo) : string * string option =
    let dimension = Dimension.text info.Dimension
    let declaration =
        match multipleOf info with
        | Some multiple -> $"{UnitSyntax.unitKeyword} {info.Name}: {dimension} = {multiple}"
        | None -> $"@{UnitSyntax.baseUnitAttribute} {UnitSyntax.unitKeyword} {info.Name}: {dimension}"
    let inBase =
        match unitsOf typed info.Dimension |> List.tryFind UnitInfo.isBase with
        | _ when UnitInfo.isBase info -> Some $"The base unit of `{dimension}`, which its values are stored in."
        | Some baseUnit -> Some $"A unit of `{dimension}`: `{grouped (Rational.text info.Scale)}{baseUnit.Name}`."
        | None -> Some $"A unit of `{dimension}`."
    let notes = Option.toList inBase @ Option.toList (unitsNote typed info.Dimension)
    declaration, Some (String.concat "\n\n" notes)
