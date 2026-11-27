/// Division in the core IR (see "Num(a)" on the docs' Contexts page): `/` on integers, which divides with a fraction,
/// and the conversion of a `fraction` to the type it divides that the prelude's `__pow` makes for a negative power. `//`
/// and `%` are operators of their own, which call the helpers of `DivisionCode`.
module internal Transpiler.Wasm.CoreIrLowerDivision

open NyxCompiler
open Transpiler.Wasm.CoreIr

/// `/` on the integers `atoms`, of the type `ty` it gives (`float`): both converted to floats, which are then divided.
let fractional (bindTemp: Expr -> (Atom -> Expr) -> Expr) (atoms: Atom list) (ty: IrType) (k: Expr -> Expr) : Expr =
    let asFloat (atom: Atom) (k: Atom -> Expr) = bindTemp (Expr.make (CallRuntime(Intrinsics.toFloat, [ atom ], true)) ty) k
    asFloat atoms[0] (fun left -> asFloat atoms[1] (fun right -> k (Expr.make (Prim(FloatBinary "/", [ left; right ])) ty)))

/// What a call of the intrinsic `name` with arguments of the types `argTys`, which gives a value of `ty`, is in place of
/// a call, if it is `__pow`'s conversion of the `fraction` that `1 / x ** n` gives to the type `x` has, for `x ** -n`,
/// that has no such value: on an integer type, which never takes that branch, as `__pow` stops the program first, or on
/// a type whose `fraction` is another, whose negative power stops the program where it is reached. The type checker
/// reports such a power where the exponent is a literal (NYX0292).
let negativePower (name: string) (argTys: IrType list) (ty: IrType) : Expr option =
    match argTys with
    | [ _ ] when name = Intrinsics.toFloat && IrType.isNumber ty && not (IrType.isFloat ty) ->
        Some (Expr.make (Crash([ $"crash: a negative power of a '{IrType.show ty}', which is an integer type\n" ], false)) ty)
    | [ fraction ] when name = Intrinsics.toFloat && fraction <> ty && not (List.forall IrType.isNumber [ fraction; ty ]) ->
        Some (Expr.make (Crash([ $"crash: a negative power of a '{IrType.show ty}', whose `/` gives a '{IrType.show fraction}'\n" ], false)) ty)
    | _ -> None

/// Whether the call of the intrinsic `name` with `atoms`, which gives a value of `ty`, is `__pow`'s conversion of a
/// `fraction` that is the type itself, as on `float` and `F32`: the value as it is.
let isSameType (name: string) (atoms: Atom list) (ty: IrType) : bool =
    name = Intrinsics.toFloat && (match atoms with [ atom ] -> atomType atom = ty | _ -> false)
