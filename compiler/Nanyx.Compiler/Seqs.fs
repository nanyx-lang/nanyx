namespace NyxCompiler

/// The prelude's lazy sequence, `type Seq(a) = [k] protected (next: [k] () -> #next(a, Seq(a)) | #done)`, a type as
/// any user's is (see the lazy sequences page of the docs). Its alias `seq` is written as it, as `list` is the built-in
/// `List` (see `BuiltinTypeNames`), and it is shown by its alias, `seq(int)`. A range, `1..10` or `0..`, is one, made
/// by the prelude's `seq.range` or `seq.from`, but for a `for` loop's, which counts with its bounds (see `Loops`).
module Seqs =
    /// The type's name, as the prelude declares it and definitions are attached to it: `Seq.map`.
    let typeName = "Seq"

    /// The type's alias: a type or an attached definition may be written with it, `seq(int)` or `seq.map`, and the type
    /// is shown by it.
    let alias = "seq"

    /// The type's identity: the prelude's `Seq`, not a type of that name another module defines.
    let nominal: NominalId = { Home = StandardLibraryModule ModuleOrigin.preludeName; Name = typeName }

    /// The element type of a sequence type.
    let (|SeqType|_|) (ty: Ty) =
        match ty with
        | TyNominal(id, element :: _, _, _) when id = nominal -> Some element
        | _ -> None

    /// The prelude's function a range with an end, `first..last`, is a call to.
    let rangeFunction = $"{typeName}.range"

    /// The prelude's function a range with no end, `first..`, is a call to.
    let fromFunction = $"{typeName}.from"

    /// The prelude's functions a lambda with a `yield` in it is rewritten into calls to (see `WorkflowDesugaring`):
    /// `yield x` with the rest of the block is `seq.cons(x, { rest })`, the block's end `seq.empty()`,
    /// and a `for` loop whose body yields `seq.append(seq.concatMap(items, { item -> body }), seq.delay({ rest }))`.
    let consFunction = $"{typeName}.cons"
    let emptyFunction = $"{typeName}.empty"
    let appendFunction = $"{typeName}.append"
    let concatMapFunction = $"{typeName}.concatMap"
    let delayFunction = $"{typeName}.delay"

    /// `name`, a definition's, as it is shown: one attached to the type by its alias, `seq.map`.
    let shownName (name: string) =
        if name.StartsWith(typeName + ".") then alias + name.Substring(typeName.Length) else name
