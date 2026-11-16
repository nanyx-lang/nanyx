namespace NyxCompiler

/// The compiler's own instance of the prelude's `Default` of `#some(a) | #none`, which is `#none`. Every other built-in
/// type has its `default` attached (`int.default`, `list.default`), but a union has no name to attach one to, so the
/// instance is built from the prelude's `__noneDefault` instead.
module internal NoneDefault =
    /// The prelude's context of a fallback value.
    let context = "Default"

    /// The prelude function that is the instance's `default`.
    let definition = "__noneDefault"

    /// Whether `ty` is `#some(a) | #none`, for any `a`.
    let private isOption (ty: Ty) =
        match Unifier.unalias ty with
        | TyUnion items ->
            let tags =
                items
                |> List.choose (fun item ->
                    match Unifier.unalias item with
                    | TyTag(tag, payload) -> Some (tag, payload.IsSome)
                    | _ -> None)
            tags.Length = items.Length && Set.ofList tags = Set.ofList [ "some", true; "none", false ]
        | _ -> false

    /// The definition each member of `ctxName` at `argTys` is, when it is the `Default` of `#some(a) | #none`.
    let tryDefinitions (ctxName: string) (argTys: Ty list) : (string * string) list option =
        match argTys with
        | [ ty ] when ctxName = context && isOption ty -> Some [ "default", definition ]
        | _ -> None
