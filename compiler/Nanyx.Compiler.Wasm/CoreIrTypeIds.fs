/// The numbers `Type` values are at run time. Each type a `type X` names, or a description refers to as a field's or an
/// element's type, gets the next number when it is first met, and the module's `__describe` builds the description of
/// the type of each number (see `CoreIrLowerTypeMatch`), so the module holds the descriptions of those types only.
///
/// A type is numbered as seen from a module, by the file its code is in: a nominal type's description shows its
/// structure only in its home module (see `TypeValues.nominalTag`), so one type seen from two modules may have two
/// numbers. A part of a description is seen from where the description is.
module Transpiler.Wasm.CoreIrTypeIds

open NyxCompiler

/// The types numbered so far in the module being lowered, each with the file it is seen from.
type TypeIds() =
    let ids = System.Collections.Generic.Dictionary<Ty * string, int64>()
    let types = ResizeArray<Ty * string>()

    /// The number of `ty`, a type with nothing left to settle, seen from the file `view`, which it is given if it has
    /// none yet.
    member _.Of(ty: Ty, view: string) : int64 =
        match ids.TryGetValue((ty, view)) with
        | true, id -> id
        | _ ->
            let id = int64 types.Count
            ids.[(ty, view)] <- id
            types.Add((ty, view))
            id

    /// How many types are numbered so far.
    member _.Count = types.Count

    /// The type of the number `id`, with the file it is seen from.
    member _.At(id: int) = types.[id]
