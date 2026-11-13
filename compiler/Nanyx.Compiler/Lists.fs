namespace NyxCompiler

/// The built-in `list(a)` type: a list literal, `[a, b]`, and a list pattern have it, and the prelude's list functions
/// are attached to it.
module Lists =
    /// The type's name, as a type writes it, `list(int)`, and as the prelude's list functions are attached to it.
    let typeName = "list"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The list of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a list type.
    let (|ListType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None

/// The built-in `seq(a)` type: a range with an end, `1..n`, has it, and a `for` loop walks it as it walks a list.
module Seqs =
    /// The type's name, as a type writes it, `seq(int)`.
    let typeName = "seq"

    /// Whether `name` names the type.
    let isTypeName (name: string) = name = typeName

    /// The sequence of `element`s.
    let ofType (element: Ty) = TyApply(typeName, [ element ])

    /// The element type of a sequence type.
    let (|SeqType|_|) (ty: Ty) =
        match ty with
        | TyApply(name, [ element ]) when name = typeName -> Some element
        | _ -> None
