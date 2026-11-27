namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Arithmetic on measured values (see `Units`): the table "Arithmetic" on the docs' Units of measure page. The
/// compiler types each operator on a measured value as the operator on its storage, which is what it is at run time,
/// and gives the result its dimension where the table says: `t + u` and `t * 2` are `Time(a)`, `t / 2` is
/// `Time(fraction)` and `t / u` a `fraction`. A product or quotient of two dimensions is of their derived dimension,
/// `d / t` a `Length / Time` (see `Dimension.multiply`), and a quotient's storage is the fraction. A sum of two
/// dimensions, a measured value and a number with no unit where both are measured, and a `//` that would round a
/// derived dimension are errors.
module internal MeasuredArithmetic =
    /// An operator on measured values as the operator on their storages: the types its operands are read at, and the
    /// type the storages' operator's result makes the result.
    type Reading =
        { Left: Ty
          Right: Ty
          Result: Ty -> Ty }

    /// The dimension and storage of `ty`, if it is a measured value.
    let private measured (state: InferState) (ty: Ty) =
        match Unifier.unalias (resolveSoFar state ty) with
        | TyMeasure(dimension, storage) -> Some (dimension, storage)
        | _ -> None

    /// Whether `ty` is a number literal's own type, which nothing has settled yet.
    let private isLiteral (state: InferState) (ty: Ty) =
        match resolveSoFar state ty with
        | TyVar v -> NumericLiterals.isUnsettledVar state v
        | _ -> false

    /// The dimension of `ty`, if it is a measured value.
    let dimensionOf (state: InferState) (ty: Ty) : Dimension option = measured state ty |> Option.map fst

    /// The error for a power, at `at`, of a value of the dimension `dimension`.
    let powerError (at: SourceSpan) (dimension: Dimension) : Diagnostic =
        Diagnostics.at DiagnosticCodes.TypeMismatch at
            $"A power of a '{Dimension.text dimension}' has no syntax yet: multiply it by itself, as `t * t` does, for a '{Dimension.text (Dimension.multiply dimension dimension)}'"

    let private error (expr: Expression) (message: string) =
        let at = match expr.Kind with BinaryOp(_, Some opSpan, _, _) -> opSpan | _ -> expr.Span
        Error (Diagnostics.at DiagnosticCodes.TypeMismatch at message)

    /// `op`, on operands of the types `leftTy` and `rightTy`, read as the operator on their storages, if one of them is
    /// a measured value, or the error saying it can't be one; none if neither is.
    let read (state: InferState) (expr: Expression) (op: string) (leftTy: Ty) (rightTy: Ty) : Result<Reading, Diagnostic> option =
        let measure dimension (result: Ty) = TyMeasure(dimension, result)
        let twoDimensions (l: Dimension) (r: Dimension) =
            error expr $"`{op}` takes two values of one dimension, and '{Dimension.text l}' and '{Dimension.text r}' are two"
        // The other operand is a number with no unit, which a unit would make one of the dimension, or a value of another
        // type, which nothing does.
        let noUnit (dimension: Dimension) (other: Ty) =
            let isNumber =
                isLiteral state other
                || (match Unifier.unalias (resolveSoFar state other) with
                    | TyPrimitive name -> name = "int" || name = "float" || SizedNumber.isName name
                    | _ -> false)
            if isNumber then
                error expr $"`{op}` takes two values of one dimension, and a number with no unit is of none, so it can't be beside a '{Dimension.text dimension}': give the number a unit of '{Dimension.text dimension}'"
            else
                error expr $"`{op}` takes two values of one dimension, and a value of '{Unifier.typeText state.TypeDefs (resolveSoFar state other)}' is not a '{Dimension.text dimension}'"
        match measured state leftTy, measured state rightTy with
        | None, None -> None
        | left, right ->
        Some (
            match op, left, right with
            // A sum, a difference and a remainder are of the operands' one dimension.
            | ("+" | "-" | "%"), Some (l, ls), Some (r, rs) ->
                if l = r then Ok { Left = ls; Right = rs; Result = measure l } else twoDimensions l r
            // An operand of a type not known yet is the other's measured value: a sum takes two.
            | ("+" | "-" | "%"), Some (dimension, storage), None when not (isLiteral state rightTy) && (match resolveSoFar state rightTy with TyVar _ -> true | _ -> false) ->
                Ok { Left = storage; Right = storage; Result = measure dimension }
            | ("+" | "-" | "%"), None, Some (dimension, storage) when not (isLiteral state leftTy) && (match resolveSoFar state leftTy with TyVar _ -> true | _ -> false) ->
                Ok { Left = storage; Right = storage; Result = measure dimension }
            | ("+" | "-" | "%"), Some (dimension, _), None -> noUnit dimension rightTy
            | ("+" | "-" | "%"), None, Some (dimension, _) -> noUnit dimension leftTy
            // A measured value scaled by a number of its storage, on either side.
            | "*", Some (dimension, storage), None -> Ok { Left = storage; Right = rightTy; Result = measure dimension }
            | "*", None, Some (dimension, storage) -> Ok { Left = leftTy; Right = storage; Result = measure dimension }
            // Divided by a number: `/` gives a measured value of the storage's fraction, `//` one of the storage.
            | ("/" | "//"), Some (dimension, storage), None -> Ok { Left = storage; Right = rightTy; Result = measure dimension }
            // Divided by one of its own dimension: how many of the one are in the other, a plain number.
            | "//", Some (l, ls), Some (r, rs) when l = r -> Ok { Left = ls; Right = rs; Result = id }
            // A product or quotient of two dimensions, or of a number by one, is of their derived dimension: `d / t` is a
            // `Length / Time`. Both operands share a storage, and a quotient is of its fraction, so that `50MB / 1s` is
            // not rounded to a whole number of bytes a nanosecond.
            | "*", Some (l, ls), Some (r, rs) -> Ok { Left = ls; Right = rs; Result = Dimension.measured (Dimension.multiply l r) }
            | "/", Some (l, ls), Some (r, rs) -> Ok { Left = ls; Right = rs; Result = Dimension.measured (Dimension.divide l r) }
            | "/", None, Some (dimension, storage) -> Ok { Left = storage; Right = storage; Result = Dimension.measured (Dimension.divide [] dimension) }
            | "//", Some (l, _), Some (r, _) ->
                error expr $"`//` rounds only between two values of one dimension, or by a number, and '{Dimension.text l}' // '{Dimension.text r}' would round a '{Dimension.text (Dimension.divide l r)}': divide with `/`"
            | "//", None, Some (dimension, _) ->
                error expr $"`//` rounds only between two values of one dimension, or by a number, and a number // '{Dimension.text dimension}' would round a '{Dimension.text (Dimension.divide [] dimension)}': divide with `/`"
            | _ ->
                let dimension = left |> Option.orElse right |> Option.map (fst >> Dimension.text) |> Option.defaultValue "dimension"
                error expr ($"'{op}' is not defined on a '{dimension}': a measured value has " + "`+`, `-`, `*`, `/`, `//` and `%`"))
