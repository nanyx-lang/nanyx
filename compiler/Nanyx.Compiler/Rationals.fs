namespace NyxCompiler

open System.Numerics
open Parser.Program

/// An exact fraction, in lowest terms with a positive denominator, so that two equal fractions are equal records: how
/// many default storage units a unit of measure is (see `UnitInfo.Scale`).
type Rational = { Numerator: bigint; Denominator: bigint }

module Rational =
    /// `numerator / denominator`, in lowest terms. The denominator is not zero.
    let make (numerator: bigint) (denominator: bigint) : Rational =
        let divisor = BigInteger.GreatestCommonDivisor(numerator, denominator)
        let divisor = if denominator.Sign < 0 then -divisor else divisor
        if divisor.IsZero then { Numerator = numerator; Denominator = denominator }
        else { Numerator = numerator / divisor; Denominator = denominator / divisor }

    let one = make 1I 1I

    /// The number `number` is exactly: `0.001` is 1/1000.
    let ofWritten (number: WrittenNumber) : Rational =
        if number.Exponent >= 0 then make (number.Digits * BigInteger.Pow(10I, number.Exponent)) 1I
        else make number.Digits (BigInteger.Pow(10I, -number.Exponent))

    let multiply (a: Rational) (b: Rational) : Rational = make (a.Numerator * b.Numerator) (a.Denominator * b.Denominator)

    /// `a` divided by `b`, which is not zero.
    let divide (a: Rational) (b: Rational) : Rational = make (a.Numerator * b.Denominator) (a.Denominator * b.Numerator)

    /// The float nearest `r`.
    let toFloat (r: Rational) = float r.Numerator / float r.Denominator

    /// Whether `r` is a whole number.
    let isWhole (r: Rational) = r.Denominator.IsOne

    /// `r` raised to the power `power`, which may be negative if `r` is not zero.
    let power (r: Rational) (power: int) : Rational =
        let raised = make (BigInteger.Pow(r.Numerator, abs power)) (BigInteger.Pow(r.Denominator, abs power))
        if power < 0 then divide one raised else raised

    /// The largest number that `a` and `b`, both above zero, are each a whole number of: the greatest common divisor of
    /// `1/2` and `3/4` is `1/4`.
    let greatestCommonDivisor (a: Rational) (b: Rational) : Rational =
        make (BigInteger.GreatestCommonDivisor(a.Numerator * b.Denominator, b.Numerator * a.Denominator)) (a.Denominator * b.Denominator)

    /// `r` as it is written: `1000`, or `1/1000`.
    let text (r: Rational) = if isWhole r then string r.Numerator else $"{r.Numerator}/{r.Denominator}"

    /// `r` written as a decimal, `0.2`, if it is one with an end; none for `1/3`.
    let decimalText (r: Rational) : string option =
        let rec digits (places: int) (scaled: bigint) =
            if (scaled % r.Denominator).IsZero then Some (places, scaled / r.Denominator)
            elif places > 64 then None
            else digits (places + 1) (scaled * 10I)
        digits 0 r.Numerator
        |> Option.map (fun (places, number) ->
            if places = 0 then string number
            else
                let sign = if number.Sign < 0 then "-" else ""
                let written = (BigInteger.Abs number).ToString().PadLeft(places + 1, '0')
                $"{sign}{written.Substring(0, written.Length - places)}.{written.Substring(written.Length - places)}")

    /// The smallest of `scales` that `r` is an exact decimal of, with that decimal: of `mm` at 5 and `inch` at 127,
    /// 1 is `0.2` of `mm`. None if it is a decimal of none of them.
    let smallestDecimalOf (r: Rational) (scales: ('a * Rational) list) : ('a * string) option =
        scales
        |> List.sortWith (fun (_, a) (_, b) -> compare (a.Numerator * b.Denominator) (b.Numerator * a.Denominator))
        |> List.tryPick (fun (unit, scale) -> decimalText (divide r scale) |> Option.map (fun number -> unit, number))
