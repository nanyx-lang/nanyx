/// Which names code can see where it is written: the `module name =` blocks of a file, the imports of them, and the
/// errors for a reference that reaches past what a block exports or imports.
module NyxCompiler.Visibility

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState

/// The imports in `module'` that name a `module name =` block of the same file, by its short name, rather than
/// another file, and the re-exports that do, which import it too. At the top level of the file, that is a block written
/// at the top level; in a block, a block nested directly in it, or else one written beside it.
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
            | Kind (ModuleDecl(_, name)) as item when item.Span.Start = block.Start -> Some name
            | _ -> None)
    module'
    |> List.collect (function
        | Kind (Import items) -> items |> List.map (fun item -> item, false)
        | Kind (ReExport item) -> [ item, true ]
        | _ -> [])
    |> List.choose (fun (item, isReExport) ->
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
            |> Option.bind (fun (_, block) ->
                prefixOf block
                |> Option.map (fun prefix -> { Import = item; Prefix = prefix; Block = block; IsReExport = isReExport; Names = Map.empty }))
        | _ -> None)

/// Whether `name`, one of the names something gives, is one `selection` asks for: a name it selects, or a definition
/// attached to a type it selects, as `Money.new` with `Money`.
let private isSelected (selection: (Identifier * SourceSpan) list option) (name: string) =
    selection
    |> Option.forall (List.exists (fun (selected, _) -> name = selected || name.StartsWith(selected + ".")))

/// Everything the block `blockImport` names gives, whatever the import selects, each by its short name with the name of
/// the definition or type it stands for where the block is written: its own exported definitions and types, `area` for
/// `shapes.area`, and what it re-exports. A re-export of another block gives what that block gives; one of another
/// file's module gives the names the re-export brings in, which `importedBy` finds.
let private blockGives (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) (importedBy: ImportItem -> string list) (blockImport: BlockImport) : (string * string) list =
    let imports = blockImports moduleBlocks module'
    let rec gives (seen: Set<SourceSpan>) (blockImport: BlockImport) =
        let inBlock (item: TopLevelItem) = blockAround moduleBlocks item.Span |> Option.map snd = Some blockImport.Block
        let own (item: TopLevelItem) (name: string) =
            if name.StartsWith(blockImport.Prefix + ".") && inBlock item then [ name.Substring(blockImport.Prefix.Length + 1), name ] else []
        module'
        |> List.collect (fun item ->
            match item.Kind with
            | Def (ValueDef(true, name, _, _)) -> own item name
            | Def (TypeDef(name, modifiers, _, _)) when modifiers |> List.contains Export -> own item name
            | ReExport reExported when inBlock item ->
                match imports |> List.tryFind (fun other -> other.Import = reExported) with
                | Some other when seen.Contains other.Block -> []
                | Some other -> gives (seen.Add other.Block) other |> List.filter (fst >> isSelected reExported.Selection)
                | None -> importedBy reExported |> List.map (fun name -> name, name)
            | _ -> [])
    gives (Set.singleton blockImport.Block) blockImport |> List.distinct

/// What the block `blockImport` names gives the code that imports or re-exports it (see `blockGives`), limited to what
/// it selects.
let internal blockExports (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) (importedBy: ImportItem -> string list) (blockImport: BlockImport) : (string * string) list =
    blockGives moduleBlocks module' importedBy blockImport |> List.filter (fst >> isSelected blockImport.Import.Selection)

/// The re-exports of other files' modules written in the block `blockImport` names, or in a block it re-exports, in
/// turn: what the block gives that another file defines comes through them.
let internal moduleReExportsIn (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) (blockImport: BlockImport) : ImportItem list =
    let imports = blockImports moduleBlocks module'
    let rec within (seen: Set<SourceSpan>) (block: SourceSpan) =
        module'
        |> List.collect (fun item ->
            match item.Kind with
            | ReExport reExported when blockAround moduleBlocks item.Span |> Option.map snd = Some block ->
                match imports |> List.tryFind (fun other -> other.Import = reExported) with
                | Some other when seen.Contains other.Block -> []
                | Some other -> within (seen.Add other.Block) other.Block
                | None -> [ reExported ]
            | _ -> [])
    within (Set.singleton blockImport.Block) blockImport.Block

/// The names `blockImport` brings in, each with the name it stands for: what the block exports (see `blockExports`),
/// under the import's alias if it has one.
let internal blockAliases (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) (importedBy: ImportItem -> string list) (blockImport: BlockImport) : (string * string) list =
    blockExports moduleBlocks module' importedBy blockImport
    |> List.map (fun (short, qualified) ->
        match blockImport.Import.Alias with
        | Some alias -> $"{alias}.{short}", qualified
        | None -> short, qualified)

/// The error for each name `blockImport` selects that the block does not give, with the names closest to it.
let internal unexportedBlockSelections (moduleBlocks: (ModuleName * SourceSpan) list) (module': Module) (importedBy: ImportItem -> string list) (blockImport: BlockImport) : Diagnostic list =
    let given = blockGives moduleBlocks module' importedBy blockImport |> List.map fst |> Set.ofList
    blockImport.Import.Selection
    |> Option.defaultValue []
    |> List.filter (fun (name, _) -> not (given.Contains name))
    |> List.map (fun (name, span) ->
        let blockName = match blockImport.Import.Source with ModuleImport name -> name | PathImport path -> path
        NameSuggestions.unknown DiagnosticCodes.NotExported span $"'{blockName}' does not export '{name}'" given name)

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
/// In the home module of a nominal type, a value of its underlying type is one of it. A value whose type the constraints
/// so far don't settle is taken to be of the nominal type: the rest of a value of a type that names itself is one,
/// though only solving them shows it.
let internal addDeclaredConstraintAt (span: SourceSpan) (actualTy: Ty) (declaredTy: Ty) (state: InferState) =
    match Unifier.unalias declaredTy with
    | RecursiveNominals.Nominal(nominal, _, underlying, _) when inNominalHome state nominal span ->
        match Unifier.unalias (resolveSoFar state actualTy) with
        | TyNominal _
        | TyVar _ -> addAssignableConstraintAt span actualTy declaredTy state
        | _ -> addAssignableConstraintAt span actualTy underlying state
    | _ -> addAssignableConstraintAt span actualTy declaredTy state

/// The error for naming `name` at `span`, where it means the definition or type `symbolId`, if that reaches out of the
/// `module name =` block `span` is in, or into one: a block sees only its own definitions and types, the blocks nested
/// in it, the blocks it imports and what it imports itself, and its imports are its own.
let internal blockIsolationAt (state: InferState) (name: string) (symbolId: SymbolId option) (span: SourceSpan) : Diagnostic list =
    let exprBlock = blockAround state.ModuleBlocks span
    // A block import written where `span` is brings `name` in, perhaps from what the block re-exports.
    let broughtInHere () =
        state.BlockImports
        |> List.exists (fun blockImport -> blockImport.Names.ContainsKey name && blockAround state.ModuleBlocks blockImport.Import.Span = exprBlock)
    match symbolId |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
    | _ when SourceSpan.isNone span || state.ModuleBlocks.IsEmpty -> []
    | None -> []
    | Some symbol ->
        match state.ImportSites |> Map.tryFind symbol.Id with
        | Some sites ->
            let visible =
                sites |> List.exists (fun site -> SourceSpan.isNone site || blockAround state.ModuleBlocks site = exprBlock)
                || broughtInHere ()
            match exprBlock with
            | _ when visible -> []
            | Some (blockName, _) ->
                [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is imported outside module '{blockName}', which sees only what it imports itself: import it in the block too" |> Diagnostics.about name ]
            | None ->
                let blockName = sites |> List.tryPick (fun site -> blockAround state.ModuleBlocks site |> Option.map fst) |> Option.defaultValue ""
                [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is only imported in module '{blockName}', and a module block's imports are its own: import it here too" |> Diagnostics.about name ]
        | None when (symbol.Kind = TopLevelValue || symbol.Kind = TypeDefinition) && symbol.Declaration.File = span.File ->
            match exprBlock with
            | Some (blockName, block) when not (SourceSpan.contains block symbol.Declaration) ->
                let imported =
                    symbol.IsExported
                    && state.BlockImports
                       |> List.exists (fun blockImport ->
                           SourceSpan.contains blockImport.Block symbol.Declaration
                           && blockAround state.ModuleBlocks blockImport.Import.Span = exprBlock)
                    || broughtInHere ()
                if imported then []
                else
                    [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is defined outside module '{blockName}', which sees only its own definitions and what it imports"
                      |> Diagnostics.about name
                      |> Diagnostics.withRelated symbol.Declaration $"'{name}' is declared here" ]
            // At the top level, a block's name for what another block gives is one only an import there brings in.
            | None when name <> symbol.Name && state.BlockAliases |> Map.tryFind name = Some symbol.Name && not (broughtInHere ()) ->
                let blockName =
                    state.BlockImports
                    |> List.tryPick (fun blockImport ->
                        if blockImport.Names.ContainsKey name then blockAround state.ModuleBlocks blockImport.Import.Span |> Option.map fst else None)
                    |> Option.defaultValue ""
                [ Diagnostics.at DiagnosticCodes.OutsideModuleBlock span $"'{name}' is only imported in module '{blockName}', and a module block's imports are its own: import it here too" |> Diagnostics.about name ]
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
              |> Diagnostics.about symbol.Name
              |> Diagnostics.withRelated symbol.Declaration $"'{symbol.Name}' is declared here" ]
        | None -> []
    | _ -> []

/// The error for `expr`, a reference to `name` bound to `scheme`, if `name` is brought in by more than one import
/// and nothing nearer binds it. Import order never picks one.
let internal ambiguousReference (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match state.AmbiguousImports |> Map.tryFind name with
    | Some (symbol, candidates) when scheme.Symbol = Some symbol ->
        let quoted = candidates |> List.map (fun candidate -> $"'{candidate}'")
        [ Diagnostics.at DiagnosticCodes.AmbiguousName expr.Span $"It could be {Diagnostics.listedOr quoted}: import all but one of them with `as`" |> Diagnostics.about name ]
    | _ -> []

/// The error for `expr`, a reference to `name` bound to `scheme`, if `name` is what a lambda's `context name` binds:
/// an instance is never a value, so `use name` is all it can do, which never infers it as a reference.
let internal contextNameReference (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match scheme.Symbol with
    | Some symbol when state.ContextNames.ContainsKey symbol ->
        [ Diagnostics.at DiagnosticCodes.ContextNameAsValue expr.Span $"It names a context instance, which can only be loaded with `use {name}`" |> Diagnostics.about name ]
    | _ -> []

/// The errors for a reference to `name`, bound to `scheme`, that the name alone does not show: a definition a module
/// block keeps private, a name more than one import brings in, or a context instance a lambda names.
let internal referenceErrors (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match contextNameReference name expr scheme state, privateReference expr scheme state with
    | (_ :: _ as errors), _ -> errors
    | [], [] -> blockIsolation name expr scheme state @ ambiguousReference name expr scheme state
    | [], errors -> errors @ ambiguousReference name expr scheme state
