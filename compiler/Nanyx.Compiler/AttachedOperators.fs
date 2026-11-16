namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Which types may attach the members of the well-known contexts (see "Equality and well-known contexts" in the docs'
/// work in progress). A structural type always compares by its structure, so that `(x = 1, y = 2)` compares the same
/// whether or not the alias it is written with is known: only a nominal type, `type Email = protected string`, may attach its
/// own `==`, `compare` or `hash`. `!=` is never attached by anything: it is always `not (a == b)`. A built-in type's
/// order and hash are always the compiler's own, so a `compare` or `hash` attached to one, as the prelude's `int.compare`
/// and `int.hash` are, is an ordinary function that the operators, `Ord` and `Hash` do not read (see
/// `WellKnownContexts.isBuiltIn`).
module internal AttachedOperators =
    /// The members only a nominal or protected type may attach, each with what it decides and what a structural type's
    /// is, as a message says them, and whether a built-in type may attach it too.
    let private nominalOnly =
        [ EqualityOperators.equals, "equality", "its equality is always that of its structure", false
          WellKnownContexts.ordering.Member, "order", "it is only ordered by its structure, and records and tags have none", true
          WellKnownContexts.hashing.Member, "hash", "its hash is always that of its structure", true ]

    /// What a type that a definition is attached to is, as far as these rules care.
    type private AttachedTo =
        | Nominal
        | BuiltIn of string
        | Structural

    let private kindOf (state: InferState) (typeName: string) =
        match state.TypeDefs |> Map.tryFind typeName with
        | Some info when info.IsNominal -> Some Nominal
        | Some { Underlying = TyNominal _ } -> Some Nominal
        | Some info when info.IsContext -> None
        | Some _ -> Some Structural
        | None ->
            match typeName with
            | "int" | "float" | "bool" | "string" | "unit" -> Some (BuiltIn typeName)
            | _ when Lists.isTypeName typeName -> Some (BuiltIn typeName)
            | _ when SizedNumber.isName typeName -> Some (BuiltIn typeName)
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
                match kindOf state typeName with
                | Some (BuiltIn builtIn) when OrderingOperators.isOperator memberName ->
                    error $"'{name}' cannot be defined: '{builtIn}' is a built-in type, so its order is the compiler's own"
                | _ ->
                match nominalOnly |> List.tryFind (fun (attached, _, _, _) -> attached = memberName), kindOf state typeName with
                | Some (_, decides, structural, _), Some Structural ->
                    error $"'{name}' cannot be defined: '{typeName}' is a structural type, so {structural}. Declare it `protected` to define its own {decides}"
                | Some (_, decides, _, false), Some (BuiltIn builtIn) ->
                    error $"'{name}' cannot be defined: '{builtIn}' is a built-in type, so its {decides} is the compiler's own. Make a nominal type of it to define its own {decides}"
                | _ -> Ok ()

    /// The standard-library modules that own the attached definition `name` (`string.length`, owned by `nanyx/text`):
    /// those that export it, where it is attached to a built-in type. A built-in type's owned definitions are in scope
    /// wherever its values are, whether or not their module is imported (see "Extending a type" in the docs' work in
    /// progress). None for the module being checked, which may define its own.
    let private ownerModules (state: InferState) (name: Identifier) : string list =
        let dot = name.LastIndexOf '.'
        // The paths are import paths, `nanyx/text`, and a module of the library is named without its package: `text`.
        let isHome (path: string) =
            match state.Home with
            | StandardLibraryModule moduleName -> path.EndsWith("/" + moduleName)
            | _ -> false
        match kindOf state (name.Substring(0, max 0 dot)) with
        | Some (BuiltIn _) when dot > 0 ->
            state.StandardLibraryExports.Value
            |> Map.tryFind name
            |> Option.defaultValue []
            |> List.filter (isHome >> not)
        | _ -> []

    /// The error for each of the module's own top-level definitions in `module'` that is attached to a type and has a
    /// name already in scope for it in `imported`, the environment its imports make, or owned by the standard library:
    /// an attached definition extends a type, but never redefines what it already has. A `module name =` block's
    /// definitions, lifted to the top level as `name.f`, are not attached to anything.
    let redefinitions (imported: TypeEnv) (state: InferState) (module': Module) : Diagnostic list =
        let blocks = state.ModuleBlocks |> List.map fst |> Set.ofList
        let inBlock (name: Identifier) = blocks |> Set.exists (fun block -> name.StartsWith(block + "."))
        module'
        |> List.choose (fun item ->
            match item.Kind with
            | Def (ValueDef(_, name, _, value)) when name.LastIndexOf '.' > 0 && not (inBlock name) ->
                let span =
                    state.Declared |> Map.tryFind (value.Id, DefValue) |> Option.bind List.tryHead |> Option.map snd |> Option.defaultValue item.Span
                let typeName = name.Substring(0, name.LastIndexOf '.')
                let error (from: string) =
                    Diagnostics.at DiagnosticCodes.AttachedDefinitionInScope span
                        $"'{name}' is already in scope for '{typeName}'{from}: an attached definition can extend a type, but not redefine what it has. Give it another name"
                match ownerModules state name, TypeEnv.tryFind name imported |> Option.bind _.Symbol |> Option.bind (fun symbol -> state.Symbols |> Map.tryFind symbol) with
                | owner :: _, _ -> Some (error $", from {owner}")
                // The prelude is in files of its own, `nanyx/prelude-lists.nyx`, which every module imports.
                | [], Some symbol when symbol.Declaration.File.Replace('\\', '/').StartsWith "nanyx/prelude" -> Some (error ", from the prelude")
                | [], Some symbol when not (SourceSpan.isNone symbol.Declaration) ->
                    Some (error ", from an import" |> Diagnostics.withRelated symbol.Declaration $"'{name}' is defined here")
                | [], _ when (TypeEnv.tryFind name imported).IsSome -> Some (error ", from an import")
                | _ -> None
            | _ -> None)
