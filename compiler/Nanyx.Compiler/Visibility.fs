/// Which names code can see where it is written: the `module name =` blocks of a file, the imports of them, and the
/// errors for a reference that reaches past what a block exports or imports.
module NyxCompiler.Visibility

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState

/// The imports in `module'` that name a `module name =` block of the same file, by its short name, rather than
/// another file. At the top level of the file, that is a block written at the top level; in a block, a block nested
/// directly in it, or else one written beside it.
let blockImports (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) : BlockImport list =
    let parentOf (block: SourceSpan) =
        moduleBlocks
        |> List.filter (fun (_, other) -> other <> block && SourceSpan.contains other block)
        |> List.sortBy (fun (_, other) -> other.End - other.Start)
        |> List.tryHead
        |> Option.map snd
    // Each block is a module declaration where the block starts once blocks are expanded, under its full name.
    let prefixOf (block: SourceSpan) =
        module' |> List.tryPick (function
            | Kind (ModuleDecl name) as item when item.Span.Start = block.Start -> Some name
            | _ -> None)
    module'
    |> List.collect (function
        | Kind (Import items) -> items
        | _ -> [])
    |> List.choose (fun item ->
        match item.Source with
        | ModuleImport name when not (name.Contains '/') && not (name.Contains '.') ->
            let site = blockAround moduleBlocks item.Span |> Option.map snd
            let named = moduleBlocks |> List.filter (fun (blockName, block) -> blockName = name && Some block <> site)
            let children = named |> List.filter (fun (_, block) -> parentOf block = site)
            let siblings =
                match site with
                | Some site -> named |> List.filter (fun (_, block) -> parentOf block = parentOf site)
                | None -> []
            List.tryHead (children @ siblings)
            |> Option.bind (fun (_, block) -> prefixOf block |> Option.map (fun prefix -> { Import = item; Prefix = prefix; Block = block }))
        | _ -> None)

/// The names `blockImport` brings in, each with the qualified name it stands for: the block's own exported
/// definitions and types, under the import's alias if it has one.
let internal blockAliases (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) (blockImport: BlockImport) : (string * string) list =
    let ownName (item: TopLevelItem) (name: string) =
        if name.StartsWith(blockImport.Prefix + ".") && blockAround moduleBlocks item.Span |> Option.map snd = Some blockImport.Block then
            Some (name.Substring(blockImport.Prefix.Length + 1), name)
        else
            None
    module'
    |> List.choose (fun item ->
        match item.Kind with
        | Def (ValueDef(true, name, _, _)) -> ownName item name
        | Def (TypeDef(name, modifiers, _, _)) when modifiers |> List.contains Export -> ownName item name
        | _ -> None)
    |> List.map (fun (short, qualified) ->
        match blockImport.Import.Alias with
        | Some alias -> $"{alias}.{short}", qualified
        | None -> short, qualified)

/// Whether code at `span` is in the home module of the nominal type `nominal`: the module that defines it, and within
/// that the `module name =` block it is defined in, if any. Only its home module can make one from its shape.
let internal inNominalHome (state: InferState) (nominal: NominalId) (span: SourceSpan) =
    nominal.Home = state.Home
    && state.LocalNominals.Contains nominal.Name
    && (match state.TypeDefs |> Map.tryFind nominal.Name |> Option.bind (fun info -> info.Symbol) |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
        | Some symbol when not (SourceSpan.isNone span) ->
            blockAround state.ModuleBlocks symbol.Declaration = blockAround state.ModuleBlocks span
        | _ -> true)

/// `state` with `actualTy`, the type of the value at `span`, made assignable to `declaredTy`, the type written for it.
/// In the home module of a nominal type, a value of its underlying type is one of it.
let internal addDeclaredConstraintAt (span: SourceSpan) (actualTy: Ty) (declaredTy: Ty) (state: InferState) =
    match Unifier.unalias declaredTy with
    | TyNominal(nominal, underlying, _) when inNominalHome state nominal span ->
        match Unifier.unalias actualTy with
        | TyNominal _ -> addAssignableConstraintAt span actualTy declaredTy state
        | _ -> addAssignableConstraintAt span actualTy underlying state
    | _ -> addAssignableConstraintAt span actualTy declaredTy state

/// The error for naming `name` at `span`, where it means the definition or type `symbolId`, if that reaches out of the
/// `module name =` block `span` is in, or into one: a block sees only its own definitions and types, the blocks nested
/// in it, the blocks it imports and what it imports itself, and its imports are its own.
let internal blockIsolationAt (state: InferState) (name: string) (symbolId: SymbolId option) (span: SourceSpan) : Diagnostic list =
    let exprBlock = blockAround state.ModuleBlocks span
    match symbolId |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
    | _ when SourceSpan.isNone span || state.ModuleBlocks.IsEmpty -> []
    | None -> []
    | Some symbol ->
        match state.ImportSites |> Map.tryFind symbol.Id with
        | Some sites ->
            let visible = sites |> List.exists (fun site -> SourceSpan.isNone site || blockAround state.ModuleBlocks site = exprBlock)
            match exprBlock with
            | _ when visible -> []
            | Some (blockName, _) ->
                [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is imported outside module '{blockName}', which sees only what it imports itself: import it in the block too" ]
            | None ->
                let blockName = sites |> List.tryPick (fun site -> blockAround state.ModuleBlocks site |> Option.map fst) |> Option.defaultValue ""
                [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is only imported in module '{blockName}', and a module block's imports are its own: import it here too" ]
        | None when (symbol.Kind = TopLevelValue || symbol.Kind = TypeDefinition) && symbol.Declaration.File = span.File ->
            match exprBlock with
            | Some (blockName, block) when not (SourceSpan.contains block symbol.Declaration) ->
                let imported =
                    symbol.IsExported
                    && state.BlockImports
                       |> List.exists (fun blockImport ->
                           SourceSpan.contains blockImport.Block symbol.Declaration
                           && blockAround state.ModuleBlocks blockImport.Import.Span = exprBlock)
                if imported then []
                else
                    [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is defined outside module '{blockName}', which sees only its own definitions and what it imports"
                      |> Diagnostics.withRelated symbol.Declaration $"'{name}' is declared here" ]
            | _ -> []
        | None -> []

/// `blockIsolationAt` for `expr`, a reference to `name` bound to `scheme`.
let internal blockIsolation (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    blockIsolationAt state name scheme.Symbol expr.Span

/// The error for `expr` naming what `scheme` is bound for, if that is a definition a `module name =` block does
/// not export and `expr` is outside the block.
let internal privateReference (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match scheme.Symbol |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
    | Some symbol when symbol.Kind = TopLevelValue && not symbol.IsExported ->
        let outside =
            state.ModuleBlocks
            |> List.tryFind (fun (_, block) ->
                SourceSpan.contains block symbol.Declaration && not (SourceSpan.isNone expr.Span) && not (SourceSpan.contains block expr.Span))
        match outside with
        | Some (blockName, _) ->
            [ Diagnostics.at DiagnosticCodes.NotExported expr.Span $"'{symbol.Name}' is not exported from module '{blockName}'"
              |> Diagnostics.withRelated symbol.Declaration $"'{symbol.Name}' is declared here" ]
        | None -> []
    | _ -> []

/// The error for `expr`, a reference to `name` bound to `scheme`, if `name` is brought in by more than one import
/// and nothing nearer binds it. Import order never picks one.
let internal ambiguousReference (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match state.AmbiguousImports |> Map.tryFind name with
    | Some (symbol, candidates) when scheme.Symbol = Some symbol ->
        let quoted = candidates |> List.map (fun candidate -> $"'{candidate}'")
        [ Diagnostics.at DiagnosticCodes.AmbiguousName expr.Span $"'{name}' is ambiguous between {Diagnostics.listed quoted}; import all but one of them with 'as'" ]
    | _ -> []

/// The errors for a reference to `name`, bound to `scheme`, that the name alone does not show: a definition a module
/// block keeps private, or a name more than one import brings in.
let internal referenceErrors (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match privateReference expr scheme state with
    | [] -> blockIsolation name expr scheme state @ ambiguousReference name expr scheme state
    | errors -> errors @ ambiguousReference name expr scheme state
