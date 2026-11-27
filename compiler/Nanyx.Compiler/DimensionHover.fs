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

/// Whether `info` is its dimension's default storage unit. A derived dimension has none of its own.
let private isStorage (info: UnitInfo) = UnitInfo.isStorage info && not (Dimension.isDerived info.Dimension)

/// A unit as the list of a dimension's units writes it: `us = 1_000ns`, `ns (storage)`, or `s = 1_000ms (display)`.
let private listed (info: UnitInfo) =
    let roles = [ if isStorage info then "storage"
                  if info.IsDisplay then "display" ]
    let roles = if roles.IsEmpty then "" else $""" ({String.concat ", " roles})"""
    match multipleOf info with
    | Some multiple -> $"`{info.Name} = {multiple}`{roles}"
    | None -> $"`{info.Name}`{roles}"

/// What a hover says of the units of `dimension` after its declaration: each unit the module can write, from the
/// smallest to the largest, with how it is declared, and a value written in one of them. None when it can write none.
let unitsNote (typed: TypedModule) (dimension: Dimension) : string option =
    match unitsOf typed dimension with
    | [] -> None
    | units ->
        // A unit from the smaller half, as `ms` is of `Time`'s, which a value is often written in. A number that is no
        // whole number of default storage units in it is written as a decimal, `500.0kmh`, which a literal of an
        // integer is not.
        let example = units[max 0 ((units.Length - 1) / 2 - 1)]
        let number = if Rational.isWhole (Rational.multiply (Rational.make 500I 1I) example.Scale) then "500" else "500.0"
        Some $"""Units: {units |> List.map listed |> String.concat ", "}

Write a value as `{number}{example.Name}`."""

/// What a hover says of a derived dimension the module can write no unit of.
let private noUnitsNote =
    "It has no units here: write a value of it from its parts' units, as `100m / 9.58s`, or declare one, as `unit kmh: Speed = 1km / 1hr`."

/// The declaration of the dimension `info` defines, as hover writes it, and what it says after it: `dimension Time` and
/// its units, `dimension Bytes(int)`, or `dimension Speed = Length / Time` and its units.
let ofDimension (typed: TypedModule) (info: TypeDefInfo) : string * string option =
    if info.IsNominal then
        let storage = if info.Parameters.IsEmpty then $"({Unifier.typeText Map.empty info.Underlying})" else ""
        $"{UnitSyntax.dimensionKeyword} {info.Name}{storage}", unitsNote typed (Dimension.ofBase (TypeDefInfo.nominalId info))
    else
        match Unifier.unalias info.Underlying with
        | TyMeasure(dimension, _) ->
            $"{UnitSyntax.dimensionKeyword} {info.Name} = {Dimension.text dimension}",
            unitsNote typed dimension |> Option.orElse (Some noUnitsNote)
        | _ -> $"{UnitSyntax.dimensionKeyword} {info.Name}", None

/// The declaration of the unit `info`, as hover writes it, `unit ms: Time = 1_000us` or `@storageUnit unit ns: Time`,
/// and what it says after it: whether it is its dimension's default storage or display unit, how many of its default
/// storage units it is, and the dimension's units.
let ofUnit (typed: TypedModule) (info: UnitInfo) : string * string option =
    let dimension = Dimension.text info.Dimension
    let declaration =
        let marks = info.Marks |> List.map (fun mark -> $"@{Units.attributeOf mark} ") |> String.concat ""
        match multipleOf info with
        | Some multiple -> $"{marks}{UnitSyntax.unitKeyword} {info.Name}: {dimension} = {multiple}"
        | None -> $"{marks}{UnitSyntax.unitKeyword} {info.Name}: {dimension}"
    let inStorage =
        match unitsOf typed info.Dimension |> List.tryFind isStorage with
        | _ when isStorage info && info.IsDisplay ->
            $"The default storage and display unit of `{dimension}`, which its values are stored as a number of."
        | _ when isStorage info -> $"The default storage unit of `{dimension}`, which its values are stored as a number of."
        | Some storage when info.IsDisplay ->
            $"The default display unit of `{dimension}`: `{grouped (Rational.text info.Scale)}{storage.Name}`."
        | Some storage -> $"A unit of `{dimension}`: `{grouped (Rational.text info.Scale)}{storage.Name}`."
        | None when info.IsDisplay -> $"The default display unit of `{dimension}`."
        | None -> $"A unit of `{dimension}`."
    let notes = inStorage :: Option.toList (unitsNote typed info.Dimension)
    declaration, Some (String.concat "\n\n" notes)
