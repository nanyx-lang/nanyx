/// What an editor offers to complete. Positions are character offsets into the source as the parser saw it, with
/// line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Completion

open Parser.Program

/// The names that can be written unqualified at `offset` in the compiled root module, one symbol for each name,
/// ordered by name. Where several declarations of a name are in scope, the nearest is the one the name means: the one
/// with the narrowest scope (a parameter before a top-level def, a top-level def before an import), and of equal
/// scopes the one declared last (a def in a block shadows an earlier one of the same name). Names with a dot
/// (`inner.secret`, `string.shout`) are left out: they are reached through what comes before the dot. Empty when
/// the module did not type-check.
let namesInScope (result: CompileResult) (offset: int) : Symbol list =
    match result.Syntax, result.Typed with
    | Some _, Some typed ->
        typed.Scopes
        |> Map.toSeq
        |> Seq.filter (fun (_, scope) -> scope.Start <= offset && offset < scope.End)
        |> Seq.map (fun (id, scope) -> typed.Symbols.[id], scope)
        |> Seq.filter (fun (symbol, _) -> not (symbol.Name.Contains '.'))
        |> Seq.groupBy (fun (symbol, _) -> symbol.Name)
        |> Seq.map (fun (_, candidates) ->
            candidates
            |> Seq.minBy (fun (symbol, scope) -> scope.End - scope.Start, -symbol.Declaration.Start)
            |> fst)
        |> Seq.sortBy (fun symbol -> symbol.Name)
        |> List.ofSeq
    | _ -> []

/// A name `namesInScope` finds, with its type as hover writes it (with made-up type variables named by `names`), if
/// it has one, and whether that type is a function's.
type NameInScope = { Symbol: Symbol; Type: string option; IsFunction: bool }

/// The type of a symbol: what inference recorded for a declaration of the module's own, or what its module gave a
/// definition an import brings in. An import's alias has none.
let private symbolType (typed: TypedModule) (symbol: Symbol) : Ty option =
    typed.SymbolTypes |> Map.tryFind symbol.Id |> Option.orElse (typed.ImportedTypes |> Map.tryFind symbol.Id)

let private isFunction (ty: Ty) =
    match Unifier.unalias ty with
    | TyFunc _ -> true
    | _ -> false

/// The names of `namesInScope`, with their types.
let typedNamesInScope (names: TypeNotation) (result: CompileResult) (offset: int) : NameInScope list =
    match result.Typed with
    | Some typed ->
        namesInScope result offset
        |> List.map (fun symbol ->
            let ty = symbolType typed symbol
            { Symbol = symbol
              Type = ty |> Option.map (Unifier.tyToDisplayString names)
              IsFunction = ty |> Option.exists isFunction })
    | None -> []

type MemberKind =
    /// A field of a record.
    | Field
    /// A property a built-in type has: a string's `length`.
    | Property
    /// A definition written after the prefix: `foo` in `m.foo`, `uppercase` in `string.uppercase`.
    | Definition
    /// A further prefix: `string` in `t.string.uppercase`.
    | Prefix

/// What can follow a dot, with its type as hover writes it, if it has one.
type Member = { Name: string; Kind: MemberKind; Type: string option; IsFunction: bool }

/// The fields of a record type, or of the record a named type stands for.
let rec private recordFields (ty: Ty) : Map<string, Ty> option =
    match Unifier.unalias ty with
    | TyRecord fields -> Some fields
    | TyNominal(_, underlying, _) -> recordFields underlying
    | _ -> None

/// The type of the value `path` names at `offset`: `None` if its first name is not a value in scope there (an
/// import's alias, a type's name), and `Some None` if it is but its type, or that of a field the rest of the path
/// reads, is not known.
let private valueType (typed: TypedModule) (result: CompileResult) (offset: int) (path: string list) : Ty option option =
    match path with
    | first :: rest ->
        namesInScope result offset
        |> List.tryFind (fun symbol -> symbol.Name = first && symbol.Kind <> ImportedModule)
        |> Option.map (fun symbol ->
            rest |> List.fold (fun ty field -> ty |> Option.bind recordFields |> Option.bind (Map.tryFind field)) (symbolType typed symbol))
    | [] -> None

/// What can follow `path` and a dot at `offset` in the compiled root module, ordered by name, where `path` is the
/// names written before the dot (`p`, or `p`, `inner` for `p.inner.`). If the first is a value in scope there, these
/// are the fields of its record type, followed through the rest of the path, or the built-in properties of its type
/// (a string's `length`); a tuple's numbered items are left out, and a value whose type is not yet known has none. Otherwise they are the module's names written
/// under the path as a prefix (see `TypedModule.ModuleNames`): an import's exports under its alias (`m.`), or
/// definitions attached to a type (`string.`). A dot on a value never reaches a definition attached to its type:
/// that is a pipe's (see `pipeTargetsAt`). Empty when the module did not type-check.
/// What can follow a dot on a value of type `valueType`, ordered by name: the fields of its record type, or the
/// built-in properties of its type. A tuple's numbered items are left out, and a value whose type is not yet known
/// has none.
let private valueMembers (names: TypeNotation) (valueType: Ty option) : Member list =
    let typeOf ty = Some (Unifier.tyToDisplayString names ty)
    let fields =
        valueType
        |> Option.bind recordFields
        |> Option.map (fun fields ->
            fields
            |> Map.toList
            |> List.filter (fun (name, _) -> not (System.Char.IsDigit name.[0]))
            |> List.map (fun (name, ty) -> { Name = name; Kind = Field; Type = typeOf ty; IsFunction = isFunction ty }))
        |> Option.defaultValue []
    let properties =
        match valueType with
        | Some ty ->
            Infer.builtInPropertyNames
            |> List.choose (fun name ->
                Infer.builtInProperty ty name
                |> Option.map (fun (_, propertyTy) -> { Name = name; Kind = Property; Type = typeOf propertyTy; IsFunction = false }))
        | None -> []
    fields @ properties |> List.sortBy (fun item -> item.Name)

let membersAt (names: TypeNotation) (result: CompileResult) (offset: int) (path: string list) : Member list =
    let typeOf ty = Some (Unifier.tyToDisplayString names ty)
    match result.Typed with
    | Some typed ->
        match valueType typed result offset path with
        | Some pathType -> valueMembers names pathType
        | None ->
            let prefix = String.concat "." path + "."
            typed.ModuleNames
            |> Map.toList
            |> List.filter (fun (name, _) -> name.StartsWith(prefix, System.StringComparison.Ordinal))
            |> List.map (fun (name, id) ->
                let after = name.Substring prefix.Length
                match after.IndexOf '.' with
                | -1 ->
                    let ty = symbolType typed typed.Symbols.[id]
                    { Name = after; Kind = Definition; Type = ty |> Option.bind typeOf; IsFunction = ty |> Option.exists isFunction }
                | dot -> { Name = after.Substring(0, dot); Kind = Prefix; Type = None; IsFunction = false })
            // A prefix shared by several names is offered once; a definition of the same name comes first.
            |> List.sortBy (fun item -> item.Name, item.Kind <> Definition)
            |> List.distinctBy (fun item -> item.Name)
    | None -> []

/// What can follow a dot that starts a shorthand lambda's body (`{ .name }`) at `offset` in the compiled root module:
/// the members of the innermost such lambda's argument there, as `membersAt` gives them for a value. Empty when there
/// is no such lambda at `offset`, or the module did not type-check.
let implicitArgumentMembersAt (names: TypeNotation) (result: CompileResult) (offset: int) : Member list =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        Hover.expressionsAt offset syntax
        |> List.tryFind (fun expr ->
            match expr.Kind with
            | Lambda([ (name, _) ], _) -> name = implicitArgument
            | _ -> false)
        |> Option.bind (fun lambda -> typed.ExprTypes |> Map.tryFind lambda.Id)
        |> Option.map (fun ty ->
            match Unifier.unalias ty with
            | TyFunc(argument, _) -> valueMembers names (Some argument)
            | _ -> [])
        |> Option.defaultValue []
    | _ -> []

/// A function a pipe can call, with its type as hover writes it, if it has one. `IsAttached` when it is a definition
/// attached to the piped value's type, written without the type's name.
type PipeTarget = { Name: string; Type: string option; IsFunction: bool; IsAttached: bool }

/// What can follow `\` at `offset` in the compiled root module, where `path` names the value piped (`s`, or `p`,
/// `inner` for `p.inner \`; empty when what is piped is not a name). First the definitions attached to the value's
/// type, under each name a pipe resolves it by (see `Infer.attachedTypeNames`: `string` for a string, `list` for a
/// list), nearest name first, as a pipe resolves them; then the functions in scope, which a pipe calls when no
/// attached definition has the name. Only the functions in scope when the value's type is not known.
let pipeTargetsAt (names: TypeNotation) (result: CompileResult) (offset: int) (path: string list) : PipeTarget list =
    match result.Typed with
    | Some typed ->
        let describe (ty: Ty option) = ty |> Option.map (Unifier.tyToDisplayString names), ty |> Option.exists isFunction
        let attached =
            valueType typed result offset path
            |> Option.flatten
            |> Option.map Infer.attachedTypeNames
            |> Option.defaultValue []
            |> List.collect (fun typeName ->
                let prefix = typeName + "."
                typed.ModuleNames
                |> Map.toList
                |> List.choose (fun (name, id) ->
                    let after = if name.StartsWith(prefix, System.StringComparison.Ordinal) then name.Substring prefix.Length else ""
                    if after.Length = 0 || after.Contains '.' then None
                    else
                        let ty, isFunction = describe (symbolType typed typed.Symbols.[id])
                        Some { Name = after; Type = ty; IsFunction = isFunction; IsAttached = true }))
            |> List.distinctBy (fun target -> target.Name)
        let functions =
            typedNamesInScope names result offset
            |> List.filter (fun name -> name.IsFunction)
            |> List.map (fun name -> { Name = name.Symbol.Name; Type = name.Type; IsFunction = true; IsAttached = false })
        (attached |> List.sortBy (fun target -> target.Name)) @ functions
    | None -> []
