/// A def's type written for a reader, after the contexts it requires, as its annotation would write it: what hover,
/// the type lenses and `nanyx check` show, and what the official linter's `unannotatedExports` fix writes.
module NyxCompiler.Signatures

/// The type of a def, written for a reader as `type` is, after the contexts it requires, as its annotation would write
/// them: `[Show(α) & Console] α -> string`. `written` are the contexts as written for a reader, named together with the
/// type.
let withContexts (written: string list) (typeWritten: string) =
    match written with
    | [] -> typeWritten
    | _ -> $"""[{String.concat " & " written}] {typeWritten}"""

/// `ty` written for a reader after the contexts `contexts` it requires, as `withContexts` writes them, and each of
/// `others` written for a reader, the type variables of them all named together and by `names`, so that a variable of
/// `ty` has the same name in `others`: a parameter's `α` is the `α` of its def's signature.
let typesWithContexts (names: TypeNotation) (ty: Ty) (contexts: Ty list) (others: Ty list) : string * string list =
    match contexts, others with
    | [], [] -> Unifier.tyToDisplayString names ty, []
    | _ ->
        let written = Unifier.tysToDisplayStrings names (ty :: contexts @ others)
        let contextsWritten, othersWritten = List.splitAt contexts.Length (List.tail written)
        withContexts contextsWritten (List.head written), othersWritten

/// `ty` written for a reader after the contexts `contexts` it requires, as `typesWithContexts` writes it.
let typeWithContexts (names: TypeNotation) (ty: Ty) (contexts: Ty list) =
    fst (typesWithContexts names ty contexts [])
