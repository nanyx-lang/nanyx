namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Which types may attach the members of the well-known contexts (see "Equality and well-known contexts" in the docs'
/// work in progress). A structural type always compares by its structure, so that `(x = 1, y = 2)` compares the same
/// whether or not the alias it is written with is known: only a nominal or protected type, `$Email`, may attach its
/// own `==`. `!=` is never attached by anything: it is always `not (a == b)`.
module internal AttachedOperators =
    /// The members only a nominal or protected type may attach, each with what it decides, as a message says it.
    let private nominalOnly = [ EqualityOperators.equals, "equality" ]

    /// What a type that a definition is attached to is, as far as these rules care.
    type private AttachedTo =
        | Nominal
        | BuiltIn of string
        | Structural

    let private kindOf (state: InferState) (typeName: string) =
        let lastSegment = typeName.Substring(typeName.LastIndexOf '.' + 1)
        match state.TypeDefs |> Map.tryFind typeName with
        | _ when lastSegment.StartsWith "$" -> Some Nominal
        | Some { Underlying = TyNominal _ } -> Some Nominal
        | Some info when info.IsContext -> None
        | Some _ -> Some Structural
        | None ->
            match typeName.ToLowerInvariant() with
            | "int" | "float" | "bool" | "string" | "unit" | "list" -> Some (BuiltIn (typeName.ToLowerInvariant()))
            | _ -> None

    /// The error for the definition `name`, attached to a type and written at `span`, if that type may not attach it.
    let check (state: InferState) (name: Identifier) (span: SourceSpan) : Result<unit, Diagnostic list> =
        let dot = name.LastIndexOf '.'
        let error message = Error [ Diagnostics.at DiagnosticCodes.UnattachableDefinition span message ]
        if dot <= 0 then Ok ()
        else
            let typeName, memberName = name.Substring(0, dot), name.Substring(dot + 1)
            if memberName = EqualityOperators.notEquals then
                error $"'{name}' cannot be defined: '!=' is always 'not (a == b)'. Attach '==' instead"
            else
                match nominalOnly |> List.tryFind (fst >> (=) memberName), kindOf state typeName with
                | Some (_, decides), Some Structural ->
                    error $"'{name}' cannot be defined: '{typeName}' is a structural type, so its {decides} is always that of its structure. Make it nominal ('${typeName}') to define its own {decides}"
                | Some (_, decides), Some (BuiltIn builtIn) ->
                    error $"'{name}' cannot be defined: '{builtIn}' is a built-in type, so its {decides} is the compiler's own. Make a nominal type of it to define its own {decides}"
                | _ -> Ok ()
