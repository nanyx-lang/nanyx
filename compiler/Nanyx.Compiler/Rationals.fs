namespace NyxCompiler

open System.Numerics
open Parser.Program

/// An exact fraction, in lowest terms with a positive denominator, so that two equal fractions are equal records: how
/// many base units a unit of measure is (see `UnitInfo.Scale`).
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

    /// The float nearest `r`.
    let toFloat (r: Rational) = float r.Numerator / float r.Denominator

    /// Whether `r` is a whole number.
    let isWhole (r: Rational) = r.Denominator.IsOne

    /// `r` as it is written: `1000`, or `1/1000`.
    let text (r: Rational) = if isWhole r then string r.Numerator else $"{r.Numerator}/{r.Denominator}"
