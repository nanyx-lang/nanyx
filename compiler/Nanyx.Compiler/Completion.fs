/// What an editor offers to complete. Positions are character offsets into the source as the parser saw it, with
/// line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Completion

open Parser.Program

/// The names that can be written unqualified at `offset` in the compiled root module, one symbol for each name,
/// ordered by name. Where several declarations of a name are in scope, the nearest is the one the name means: the one
/// with the narrowest scope (a parameter before a top-level def, a top-level def before an import), and of equal
/// scopes the one declared last (a def in a block shadows an earlier one of the same name). Names with a dot
/// (`inner.secret`, `String.shout`) are left out: they are reached through what comes before the dot. Empty when
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
let typedNamesInScope (names: TypeVarNames) (result: CompileResult) (offset: int) : NameInScope list =
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
    /// A definition written after the prefix: `foo` in `m.foo`, `uppercase` in `String.uppercase`.
    | Definition
    /// A further prefix: `String` in `t.String.uppercase`.
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
/// are the fields of its record type, followed through the rest of the path; a tuple's numbered items are left out,
/// and a value whose type is not yet known to be a record has none. Otherwise they are the module's names written
/// under the path as a prefix (see `TypedModule.ModuleNames`): an import's exports under its alias (`m.`), or
/// definitions attached to a type (`String.`). A dot on a value never reaches a definition attached to its type:
/// that is a pipe's (see `pipeTargetsAt`). Empty when the module did not type-check.
let membersAt (names: TypeVarNames) (result: CompileResult) (offset: int) (path: string list) : Member list =
    let typeOf ty = Some (Unifier.tyToDisplayString names ty)
    match result.Typed with
    | Some typed ->
        match valueType typed result offset path with
        | Some pathType ->
            pathType
            |> Option.bind recordFields
            |> Option.map (fun fields ->
                fields
                |> Map.toList
                |> List.filter (fun (name, _) -> not (System.Char.IsDigit name.[0]))
                |> List.map (fun (name, ty) -> { Name = name; Kind = Field; Type = typeOf ty; IsFunction = isFunction ty }))
            |> Option.defaultValue []
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

/// A function a pipe can call, with its type as hover writes it, if it has one. `IsAttached` when it is a definition
/// attached to the piped value's type, written without the type's name.
type PipeTarget = { Name: string; Type: string option; IsFunction: bool; IsAttached: bool }

/// What can follow `\` at `offset` in the compiled root module, where `path` names the value piped (`s`, or `p`,
/// `inner` for `p.inner \`; empty when what is piped is not a name). First the definitions attached to the value's
/// type, under each name a pipe resolves it by (see `Infer.attachedTypeNames`: `String` for a string, `List` for a
/// list), nearest name first, as a pipe resolves them; then the functions in scope, which a pipe calls when no
/// attached definition has the name. Only the functions in scope when the value's type is not known.
let pipeTargetsAt (names: TypeVarNames) (result: CompileResult) (offset: int) (path: string list) : PipeTarget list =
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
