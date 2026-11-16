namespace NyxCompiler

/// The built-in `box(a)` type: a cell holding one value that can be changed in place, which the backend holds as the box
/// a captured cell is (see "Mutation" in the docs' work in progress). The standard library's `MRef(a)` is declared over
/// it, and makes, reads and changes one through three intrinsics (see `Intrinsics`). It has no equality, order or hash.
module Boxes =
    /// The type's name, as a type writes it, `box(int)`.
    let typeName = "box"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The box of an `element`.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The type of what a box type holds.
    let (|BoxType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None

    /// The type variables in what a value of `ty` holds in a box, through the types its nominal types stand for, as an
    /// `MRef(list(a))` holds `a`: not those of a box a function it holds makes. A def's type is not generalised over
    /// them, since the box holds values of one type, not one for each use: `def r = MRef.new([])`, generalised, could be
    /// set to a list of ints and read as a list of strings.
    let rec heldVariables (ty: Ty) : Set<int> =
        let rec variables (ty: Ty) =
            match ty with
            | TyVar var -> Set.singleton var.Id
            | _ -> Ty.children ty |> List.map variables |> Set.unionMany
        match ty with
        | BoxType element -> variables element
        | TyFunc _ -> Set.empty
        | _ -> Ty.children ty |> List.map heldVariables |> Set.unionMany
