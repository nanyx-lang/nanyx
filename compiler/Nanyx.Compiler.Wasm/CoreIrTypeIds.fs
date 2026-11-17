/// The numbers `Type` values are at run time. Each type a `type X` names, or a description refers to as a field's or an
/// element's type, gets the next number when it is first met, and the module's `__describe` builds the description of
/// the type of each number (see `CoreIrLowerTypeMatch`), so the module holds the descriptions of those types only.
module Transpiler.Wasm.CoreIrTypeIds

open NyxCompiler

/// The types numbered so far in the module being lowered.
type TypeIds() =
    let ids = System.Collections.Generic.Dictionary<Ty, int64>()
    let types = ResizeArray<Ty>()

    /// The number of `ty`, a type with nothing left to settle, which it is given if it has none yet.
    member _.Of(ty: Ty) : int64 =
        match ids.TryGetValue ty with
        | true, id -> id
        | _ ->
            let id = int64 types.Count
            ids.[ty] <- id
            types.Add ty
            id

    /// How many types are numbered so far.
    member _.Count = types.Count

    /// The type of the number `id`.
    member _.At(id: int) = types.[id]
