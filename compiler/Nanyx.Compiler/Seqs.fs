namespace NyxCompiler

/// The prelude's lazy sequence, `type Sequence(a) = [k] protected (next: [k] () -> #next(a, Sequence(a)) | #done)`, a type as
/// any user's is (see the lazy sequences page of the docs). Its alias `seq` is written as it, as `list` is the built-in
/// `List` (see `BuiltinTypeNames`), and it is shown by its alias, `seq(int)`. A range, `1..10` or `0..`, is one, made
/// by the prelude's `seq.range` or `seq.from` from its bounds' `Enumerable`, but for a range of `int`s a `for` loop
/// walks, which counts with its bounds (see `Loops`).
module Seqs =
    /// The type's name, as the prelude declares it and definitions are attached to it: `Sequence.map`.
    let typeName = "Sequence"

    /// The type's alias: a type or an attached definition may be written with it, `seq(int)` or `seq.map`, and the type
    /// is shown by it.
    let alias = "seq"

    /// The type's identity: the prelude's `Sequence`, not a type of that name another module defines.
    let nominal: NominalId = { Home = StandardLibraryModule ModuleOrigin.preludeName; Name = typeName }

    /// The tag a sequence's `next` gives with its first element and the rest, `#next(a, Sequence(a))`.
    let nextTag = "next"

    /// The tag a sequence's `next` gives when it has no more elements, `#done`.
    let doneTag = "done"

    /// The prelude's context of what can be iterated, `Iterable(c)`, which a type gets by attaching `toSeq`.
    let iterable = "Iterable"

    /// The element type of a sequence type.
    let (|SeqType|_|) (ty: Ty) =
        match ty with
        | TyNominal(id, element :: _, _, _) when id = nominal -> Some element
        | _ -> None

    /// The prelude's function a range with an end, `first..last`, is a call to.
    let rangeFunction = $"{typeName}.range"

    /// The prelude's function a range with no end, `first..`, is a call to.
    let fromFunction = $"{typeName}.from"

    /// The prelude's functions a range of `int`s is a call to instead, `first..last` and `first..`: they count, and
    /// need no `Enumerable` passed.
    let intRangeFunction = "__intRange"
    let intFromFunction = "__intFrom"

    /// The prelude's functions a lambda with a `yield` in it is rewritten into calls to (see `WorkflowDesugaring`):
    /// `yield x` with the rest of the block is `seq.cons(x, { rest })`, the block's end `seq.empty()`,
    /// and a `for` loop whose body yields
    /// `seq.append(seq.flatMap(__toSeq(items), { item -> body }), seq.delay({ rest }))`, or `__seqMap` for one that
    /// yields once each time round.
    let consFunction = $"{typeName}.cons"
    let emptyFunction = $"{typeName}.empty"
    let appendFunction = $"{typeName}.append"
    let flatMapFunction = $"{typeName}.flatMap"
    let delayFunction = $"{typeName}.delay"
    /// What a `for` loop whose body yields one value each time round is instead: the prelude's `seq.map`, under a name
    /// of its own, `__seqMap(__toSeq(items), { item -> e })`, which makes one sequence of the items' rather than one
    /// for each, and which `EmitLambdas` reads as the `yield` it was (#2208).
    let mapFunction = "__seqMap"
    /// What a `yield x` the sequence ends after is: the prelude's sequence of `x` alone, which makes no rest.
    let lastFunction = "__seqLast"

    /// `name`, a definition's, as it is shown: one attached to the type by its alias, `seq.map`.
    let shownName (name: string) =
        if name.StartsWith(typeName + ".") then alias + name.Substring(typeName.Length) else name
