module NyxCompiler.Infer

open System
open Parser.Program
open NyxCompiler

type TypedExpr = NyxCompiler.TypedExpr
type TypedStatement = NyxCompiler.TypedStatement

/// Which contexts the code being inferred runs in.
type ContextScope =
    /// The top level of a module, which runs in the ambient instances (the runtime's `Console`).
    | AmbientScope
    /// The body of the def `name`. One that writes its type has only the contexts the type declares; one that does
    /// not (`inferring`) needs whichever ambient contexts its body uses, which are inferred.
    | DefScope of name: Identifier * inferring: bool

/// A use of the unannotated top-level def `Callee`, whose contexts are only known once every def has been inferred:
/// `Available` are the ambient contexts something nearer than the ambient instance supplies at `Span`.
type DeferredContextUse =
    { Caller: ContextScope
      Callee: Identifier
      Span: SourceSpan
      Available: Set<Identifier> }

type InferState =
        { NextId: int;
            Constraints: ConstraintSet;
            TypeVars: Map<string, TyVar>;
            TypeDefs: Map<string, TypeDefInfo>;
            /// The names in `TypeDefs` that cannot be written: the types only an aliased import brings in, which are
            /// written with the alias (`m.$Email`). They stay in `TypeDefs` for what the imported definitions' own
            /// signatures name.
            HiddenTypeNames: Set<string>;
            ContextDefs: Map<string, TypeExpr>;
            ContextRequirements: Map<string, TypeExpr list>;
            /// The imported definitions that are an ambient context instance's members, by name: the definition's
            /// symbol and the context's name. `println` is the runtime's `Console`'s, which `@entry` imports.
            AmbientMembers: Map<string, SymbolId * Identifier>;
            /// The ambient contexts an imported function requires that this module cannot name itself, by their
            /// names in `TypeDefs` (hidden ones): `Console`, required by a function of a module that imports
            /// `nanyx/console`, in a module that does not. Like any ambient context they are the top level's, and a
            /// def has them only if it declares them or infers them.
            CarriedAmbientContexts: Set<Identifier>;
            ContextScope: ContextScope;
            /// The ambient contexts each unannotated top-level def uses itself, by the def's name.
            InferredContexts: Map<Identifier, Set<Identifier>>;
            /// The module's unannotated top-level functions, by name, with their symbols.
            UnannotatedDefs: Map<Identifier, SymbolId>;
            DeferredContextUses: DeferredContextUse list;
            LocalNominals: Set<string>;
            LocalPrivateNominals: Set<string>;
            /// The type of every expression inferred so far, by node. These are the types as inference first saw
            /// them; the substitution unification finds turns them into resolved types.
            ExprTypes: Map<NodeId, Ty>;
            /// The callee's type at each call (and pipe), by the call's node: the function's type instantiated
            /// at that call site, which a call's own type (its result) does not show.
            CalleeTypes: Map<NodeId, Ty>;
            /// Where the module's declarations are written, as the parser found them.
            Declared: DeclaredNames;
            /// The text of each `module name =` block in the module, with the block's name: what a block does not
            /// export can only be named inside it.
            ModuleBlocks: (ModuleName * SourceSpan) list;
            /// The symbol made for each declaration, by its declaring node, how that node declares it and its
            /// name, so that a declaration inferred more than once keeps one symbol.
            DeclarationSymbols: Map<NodeId * Declarer * string, SymbolId>;
            Symbols: Map<SymbolId, Symbol>;
            /// Where each name the module declares, or an import aliases, can be written: see `Scope`.
            Scopes: Map<SymbolId, SourceSpan>;
            /// The type of each declaration the module makes, by its symbol, as inference first saw it.
            SymbolTypes: Map<SymbolId, Ty>;
            /// The declarations that write their own type: an annotated def or lambda parameter.
            AnnotatedSymbols: Set<SymbolId>;
            /// The symbol each name resolved to, by the node of the name.
            References: Map<NodeId, SymbolId>;
            /// The import that brought each imported definition into scope, by the definition's symbol.
            ImportedThrough: Map<SymbolId, SymbolId>;
            /// The import each name brought in by an import came through, by the node of the name.
            ImportReferences: Map<NodeId, SymbolId>;
            /// The names more than one import brings in unqualified: the symbol the environment holds for the name,
            /// and each candidate written as `module.name`. A reference that still resolves to that symbol is
            /// ambiguous; one to a definition of the module's own, or to a local, is not.
            AmbiguousImports: Map<string, SymbolId * string list>;
            /// The attached definition each pipe calls, by the pipe's node, where that is not the name written:
            /// `x \trim` on a string calls `String.trim`.
            PipeTargets: Map<NodeId, Identifier>;
            /// The built-in property each member access reads, by its node, where it reads one rather than a
            /// record's field: `s.length` on a string.
            BuiltInProperties: Map<NodeId, BuiltInProperty>;
            /// Where a constraint added now is reported if it cannot be satisfied: the expression being inferred.
            Origin: SourceSpan }

let emptyState =
        { NextId = 0;
            Constraints = [];
            TypeVars = Map.empty;
            TypeDefs = Map.empty;
            HiddenTypeNames = Set.empty;
            ContextDefs = Map.empty;
            ContextRequirements = Map.empty;
            AmbientMembers = Map.empty;
            CarriedAmbientContexts = Set.empty;
            ContextScope = AmbientScope;
            InferredContexts = Map.empty;
            UnannotatedDefs = Map.empty;
            DeferredContextUses = [];
            LocalNominals = Set.empty;
            LocalPrivateNominals = Set.empty;
            ExprTypes = Map.empty;
            CalleeTypes = Map.empty;
            Declared = Map.empty;
            ModuleBlocks = [];
            DeclarationSymbols = Map.empty;
            Symbols = Map.empty;
            Scopes = Map.empty;
            SymbolTypes = Map.empty;
            AnnotatedSymbols = Set.empty;
            References = Map.empty;
            ImportedThrough = Map.empty;
            ImportReferences = Map.empty;
            AmbiguousImports = Map.empty;
            PipeTargets = Map.empty;
            BuiltInProperties = Map.empty;
            Origin = SourceSpan.none }

let private freshVar (state: InferState) =
    let var = { Id = state.NextId; Name = None }
    TyVar var, { state with NextId = state.NextId + 1 }

/// The symbol for `name` as `holder` declares it, made the first time it is asked for. `None` when the parser
/// saw no such declaration: the compiler made the name up (a destructured tuple, a `for` loop's element).
/// `scopeEnd` is where the name stops being visible (see `Scope.until`), or `None` for the whole module.
let private declareSymbol (declarer: Declarer) (holder: NodeId) (name: string) (kind: SymbolKind) (isExported: bool) (scopeEnd: int option) (state: InferState) : SymbolId option * InferState =
    let key = holder, declarer, name
    match state.DeclarationSymbols |> Map.tryFind key with
    | Some symbol -> Some symbol, state
    | None ->
        let declared =
            state.Declared
            |> Map.tryFind (holder, declarer)
            |> Option.bind (fun names ->
                match declarer with
                // A def's value declares only that def, whose name a module block has qualified since.
                | DefValue
                | TypeDefBody -> List.tryHead names
                | LambdaParameters
                | ArmBody
                | RecordTypeFields -> names |> List.tryFind (fun (declaredName, _) -> declaredName = name))
        match declared with
        | None -> None, state
        | Some (_, span) ->
            let symbol = { Id = SymbolId.next (); Name = name; Kind = kind; Declaration = span; IsExported = isExported }
            let scope =
                match scopeEnd with
                | Some scopeEnd -> Scope.until scopeEnd span
                | None -> Scope.wholeModule span.File
            Some symbol.Id,
            { state with
                DeclarationSymbols = state.DeclarationSymbols |> Map.add key symbol.Id
                Symbols = state.Symbols |> Map.add symbol.Id symbol
                Scopes = state.Scopes |> Map.add symbol.Id scope }

/// Records `ty` as the type of the declaration `symbol`, if the parser saw one.
let private recordSymbolType (symbol: SymbolId option) (ty: Ty) (state: InferState) =
    match symbol with
    | Some symbol -> { state with SymbolTypes = state.SymbolTypes |> Map.add symbol ty }
    | None -> state

/// Records that the declaration `symbol` writes its own type, if `isAnnotated`.
let private recordAnnotated (symbol: SymbolId option) (isAnnotated: bool) (state: InferState) =
    match symbol with
    | Some symbol when isAnnotated -> { state with AnnotatedSymbols = state.AnnotatedSymbols |> Set.add symbol }
    | _ -> state

/// Records that the type expression `typeExpr` names the type definition `info`, if it declares a symbol.
let private recordTypeReference (typeExpr: TypeExpr) (info: TypeDefInfo) (state: InferState) =
    match info.Symbol with
    | Some symbol -> { state with References = state.References |> Map.add typeExpr.Id symbol }
    | None -> state

/// Records that `expr`, a qualified `Context.member`, names that member of the context `ctxType` names, and that
/// `ctxType` names the context.
let private recordContextMemberReference (expr: Expression) (ctxType: TypeExpr) (memberName: Identifier) (state: InferState) =
    match ctxType.Kind with
    | TypeName name
    | TypeApply(name, _) ->
        match state.TypeDefs |> Map.tryFind name with
        | Some info ->
            let state = recordTypeReference ctxType info state
            match info.Members |> Map.tryFind memberName with
            | Some symbol -> { state with References = state.References |> Map.add expr.Id symbol }
            | None -> state
        | None -> state
    | _ -> state

/// Records that `expr` names what `scheme` is bound for, and the import it came through if it was imported.
let private recordReference (expr: Expression) (scheme: TypeScheme) (state: InferState) =
    match scheme.Symbol with
    | None -> state
    | Some symbol ->
        let importReferences =
            match state.ImportedThrough |> Map.tryFind symbol with
            | Some import -> state.ImportReferences |> Map.add expr.Id import
            | None -> state.ImportReferences
        { state with References = state.References |> Map.add expr.Id symbol; ImportReferences = importReferences }

let private addConstraint left right (state: InferState) =
    { state with Constraints = (left, right, Equal, state.Origin) :: state.Constraints }

let private addAssignableConstraint left right (state: InferState) =
    { state with Constraints = (left, right, Assignable, state.Origin) :: state.Constraints }

/// `addConstraint`, reported at `span` rather than at the expression being inferred.
let private addConstraintAt (span: SourceSpan) left right (state: InferState) =
    let origin = if SourceSpan.isNone span then state.Origin else span
    { state with Constraints = (left, right, Equal, origin) :: state.Constraints }

/// `addAssignableConstraint`, reported at `span` rather than at the expression being inferred.
let private addAssignableConstraintAt (span: SourceSpan) left right (state: InferState) =
    let origin = if SourceSpan.isNone span then state.Origin else span
    { state with Constraints = (left, right, Assignable, origin) :: state.Constraints }

/// Where the declaration `holder` makes as `declarer` writes its name, or `holder`'s own span if the parser saw none.
let private declarationSpan (state: InferState) (declarer: Declarer) (holder: Expression) : SourceSpan =
    state.Declared
    |> Map.tryFind (holder.Id, declarer)
    |> Option.bind List.tryHead
    |> Option.map snd
    |> Option.defaultValue holder.Span

let private primitiveType name =
    TyPrimitive name

let private literalType literal =
    match literal with
    | StringLit _ -> primitiveType "string"
    | IntLit _ -> primitiveType "int"
    | FloatLit _ -> primitiveType "float"
    | BoolLit _ -> primitiveType "bool"

let private listType elementTy =
    TyApply("list", [ elementTy ])

let private numericType =
    TyPrimitive "int"

let private tupleFieldName index =
    string index

let private lookupPrimitive (name: string) =
    match name with
    | "int" | "Int" -> Some (primitiveType "int")
    | "float" | "Float" -> Some (primitiveType "float")
    | "bool" | "Bool" -> Some (primitiveType "bool")
    | "string" | "String" -> Some (primitiveType "string")
    | "unit" | "Unit" -> Some (primitiveType "unit")
    | _ -> None

/// Whether `name` is a type the compiler provides, such as `Int` or `List`, rather than one the program defines.
/// `Option` is one too, though only as a name to attach the helpers for option-like unions to: there is no type.
let private isBuiltInTypeName (name: string) =
    (lookupPrimitive name).IsSome
    || name.Equals("list", StringComparison.OrdinalIgnoreCase)
    || name.Equals("seq", StringComparison.OrdinalIgnoreCase)
    || name = "Option"

/// Whether the type `name` is attached to exists; `span` is where the definition's name is written.
let private ensureAttachedTypeExists (state: InferState) (currentModuleOpt: string option) (name: Identifier) (span: SourceSpan) : Result<unit, Diagnostic list> =
    let firstDot = name.IndexOf(".")
    if firstDot <= 0 then
        Ok ()
    else
        let lastDot = name.LastIndexOf(".")
        let candidateTypeName = name.Substring(0, lastDot)
        if state.TypeDefs.ContainsKey candidateTypeName || isBuiltInTypeName candidateTypeName then
            Ok ()
        else
            if firstDot = lastDot then
                match currentModuleOpt with
                | Some currentModule when currentModule = candidateTypeName -> Ok ()
                | _ -> Error [ Diagnostics.at DiagnosticCodes.UnknownAttachedType span $"Unknown attached type '{candidateTypeName}' for definition '{name}'" ]
            else
                Ok ()

/// Where the name of a call, pipe or member access is written (`spanOpt`), or the whole expression if not known.
let private nameSpan (spanOpt: SourceSpan option) (expr: Expression) : SourceSpan =
    spanOpt |> Option.defaultValue expr.Span

/// The error for `expr` naming what `scheme` is bound for, if that is a definition a `module name =` block does
/// not export and `expr` is outside the block.
let private privateReference (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
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
let private ambiguousReference (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    match state.AmbiguousImports |> Map.tryFind name with
    | Some (symbol, candidates) when scheme.Symbol = Some symbol ->
        let quoted = candidates |> List.map (fun candidate -> $"'{candidate}'")
        let listed = String.concat ", " (List.take (quoted.Length - 1) quoted) + " and " + List.last quoted
        [ Diagnostics.at DiagnosticCodes.AmbiguousName expr.Span $"'{name}' is ambiguous between {listed}; import all but one of them with 'as'" ]
    | _ -> []

/// The errors for a reference to `name`, bound to `scheme`, that the name alone does not show: a definition a module
/// block keeps private, or a name more than one import brings in.
let private referenceErrors (name: string) (expr: Expression) (scheme: TypeScheme) (state: InferState) : Diagnostic list =
    privateReference expr scheme state @ ambiguousReference name expr scheme state

/// The names definitions can be attached to a value of type `ty` under, nearest first: each alias it is written
/// with, then the nominal type or the language's name for a built-in type (`String`, `List`). Empty when the type
/// has no name, such as a union, a record written out in full, or a type that is not known yet.
let rec attachedTypeNames (ty: Ty) : string list =
    match ty with
    | TyAlias(name, _, underlying) -> name :: attachedTypeNames underlying
    | TyNominal(name, _, _) -> [ name ]
    | TyPrimitive name -> [ string (Char.ToUpperInvariant name.[0]) + name.Substring 1; name ]
    | TyApply("list", _) -> [ "List" ]
    | TyApply("seq", _) -> [ "Seq" ]
    | _ -> []

/// The attached definition `x \funcName` calls: `Name.funcName` for the first name of `x`'s type that has one in
/// scope. None when `funcName` is already qualified, or no name of the type attaches it, so the pipe calls a plain
/// `funcName` instead. Only names in `env` count, so a module imported with `as` is reached as `\t.String.trim`.
let private tryResolveAttachedFunction (env: TypeEnv) (lhsType: Ty) (funcName: string) : string option =
    if funcName.Contains(".") then
        None
    else
        attachedTypeNames lhsType
        |> List.map (fun typeName -> $"{typeName}.{funcName}")
        |> List.tryFind (fun name -> env.ContainsKey name)

/// The names of the built-in properties, whatever type has them.
let builtInPropertyNames = [ "length" ]

/// The built-in property `name` of a value of type `ty`, and the property's type, if the type has one of that name:
/// a string's or a list's `length`.
let builtInProperty (ty: Ty) (name: string) : (BuiltInProperty * Ty) option =
    match Unifier.unalias ty, name with
    | TyPrimitive "string", "length" -> Some (StringLength, TyPrimitive "int")
    | TyApply("list", _), "length" -> Some (ListLength, TyPrimitive "int")
    | _ -> None

/// The error for reading `memberName` with a dot from a value of type `ty` that has no such field or property.
/// `receiver` is the value as written, if it is a name. A definition attached to the type under that name is
/// reached with a pipe, not a dot, so the message says so when there is one.
let private unknownMember (env: TypeEnv) (span: SourceSpan) (receiver: string option) (ty: Ty) (memberName: string) : Diagnostic =
    let rec isRecord (ty: Ty) =
        match Unifier.unalias ty with
        | TyRecord _
        | TyOpenRecord _ -> true
        | TyNominal(_, underlying, _) -> isRecord underlying
        | _ -> false
    let missing =
        match receiver with
        | Some name when isRecord ty -> $"Record '{name}' has no member '{memberName}'"
        | None when isRecord ty -> $"Unknown member '{memberName}'"
        | _ -> $"'{Unifier.tyToString ty}' has no property '{memberName}'"
    let message =
        match tryResolveAttachedFunction env ty memberName with
        | Some attached ->
            let piped = receiver |> Option.defaultValue "x"
            $"{missing}. '{attached}' is attached to its type: call it with a pipe, as in `{piped} \\{memberName}`"
        | None -> missing
    Diagnostics.at DiagnosticCodes.UnknownMember span message

/// The alias `name`, defined by `info`, written without arguments: a generic one is applied to its own parameters.
let private aliasType (name: string) (info: TypeDefInfo) : Ty =
    TyAlias(name, info.Parameters |> List.map TyVar, info.Underlying)

/// Whether `info` defines a nominal type.
let private isNominal (info: TypeDefInfo) =
    info.Name.StartsWith("$")

/// The type the name `name`, defined by `info`, stands for written without arguments: the nominal type, or the alias.
let private namedType (name: string) (info: TypeDefInfo) : Ty =
    if isNominal info then TyNominal(info.Name, info.Underlying, info.IsPrivate) else aliasType name info

/// The type definition a type name written in the module means: none for a name only an aliased import brings in,
/// which has to be written with the alias.
let private tryFindTypeDef (name: string) (state: InferState) : TypeDefInfo option =
    if state.HiddenTypeNames.Contains name then None else state.TypeDefs |> Map.tryFind name

let private tryInferAttachedLambdaInput (state: InferState) (name: Identifier) (expr: Expression) : Ty option =
    match expr.Kind with
    | Lambda(args, _) when name.Contains(".") && args.Length = 1 ->
        let dotIndex = name.LastIndexOf(".")
        let typeName = name.Substring(0, dotIndex)
        match tryFindTypeDef typeName state with
        | Some info -> Some (namedType typeName info)
        | None -> lookupPrimitive typeName
    | _ -> None

let private isContextDef (modifiers: TypeDefModifier list) =
    modifiers |> List.contains Context

let private tupleRecordType (items: Ty list) =
    items
    |> List.mapi (fun index ty -> tupleFieldName index, ty)
    |> Map.ofList
    |> TyRecord

let private inputTypeFromArgs argTys =
    match argTys with
    | [] -> TyPrimitive "unit"
    | [ single ] -> single
    | _ -> tupleRecordType argTys

let private isPrivateType modifiers =
    modifiers |> List.exists (fun modifier -> modifier = TypeDefModifier.Private)


let rec private typeExprToTy (state: InferState) (typeExpr: TypeExpr) : Ty * InferState =
    match typeExpr.Kind with
    | TypeName name ->
        match lookupPrimitive name with
        | Some prim -> prim, state
        | None ->
            match tryFindTypeDef name state with
            | Some info -> namedType name info, recordTypeReference typeExpr info state
            | None -> TyPrimitive name, state
    | TypeVar name ->
        match state.TypeVars |> Map.tryFind name with
        | Some existing -> TyVar existing, state
        | None ->
            // The variable keeps the annotation's name, so types are shown with the names the user wrote.
            let var = { Id = state.NextId; Name = Some name }
            TyVar var, { state with NextId = state.NextId + 1; TypeVars = state.TypeVars |> Map.add name var }
    | TypeUnit -> TyPrimitive "unit", state
    | TypeTuple items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                let ty, next = typeExprToTy current item
                current <- next
                ty)
        tupleRecordType tys, current
    | TypeRecord fields ->
        let mutable current = state
        let fieldMap =
            fields
            |> List.choose (function
                | TypeField(name, _, _, typeOpt, _) -> Some (name, typeOpt)
                | TypeMember(name, typeOpt) -> Some (name, typeOpt))
            |> List.map (fun (name, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some t -> typeExprToTy current t
                    | None -> freshVar current
                current <- next
                name, ty)
            |> Map.ofList
        TyRecord fieldMap, current
    | TypeApply(name, args) ->
        let mutable current = state
        let argTys =
            args
            |> List.map (fun arg ->
                let ty, next = typeExprToTy current arg
                current <- next
                ty)
        if name.Equals("list", StringComparison.OrdinalIgnoreCase) then
            let elementTy =
                match argTys with
                | [] -> TyPrimitive "unit"
                | [single] -> single
                | multiple -> tupleRecordType multiple
            TyApply("list", [elementTy]), current
        else
            match tryFindTypeDef name current with
            | Some info when not (isNominal info) ->
                let current = recordTypeReference typeExpr info current
                if info.Parameters.Length = argTys.Length then
                    let substitutions =
                        (info.Parameters, argTys)
                        ||> List.zip
                        |> List.fold (fun acc (paramVar, argTy) -> acc |> Map.add paramVar.Id argTy) Map.empty
                    TyAlias(name, argTys, Unifier.apply substitutions info.Underlying), current
                else
                    TyApply(name, argTys), current
            | Some info -> TyApply(name, argTys), recordTypeReference typeExpr info current
            | None -> TyApply(name, argTys), current
    | TypeFunc(arg, ret) ->
        let argTy, next = typeExprToTy state arg
        let retTy, nextState = typeExprToTy next ret
        TyFunc(argTy, retTy), nextState
    | TypeTag(name, payloadOpt) ->
        match payloadOpt with
        | Some payload ->
            let payloadTy, next = typeExprToTy state payload
            TyTag(name, Some payloadTy), next
        | None -> TyTag(name, None), state
    | TypeUnion items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                let ty, next = typeExprToTy current item
                current <- next
                ty)
        // A union's members are tags: an alias of a tag or of a union is spliced in as what it stands for.
        let members =
            tys
            |> List.collect (fun ty ->
                match Unifier.unalias ty with
                | TyUnion inner -> inner
                | TyTag _ as tag -> [ tag ]
                | _ -> [ ty ])
        TyUnion members, current
    | TypeIntersection items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                let ty, next = typeExprToTy current item
                current <- next
                ty)
        TyUnion tys, current
    | TypeOptional inner ->
        let innerTy, next = typeExprToTy state inner
        TyUnion [ innerTy; TyTag("none", None) ], next
    | TypeContext _ ->
        let ty, next = freshVar state
        ty, next
    | TypeWithContext(_, inner) ->
        typeExprToTy state inner
    | TypeConstraint(_, baseType, _) ->
        typeExprToTy state baseType
    | TypeWhere(baseType, _) ->
        typeExprToTy state baseType
    // The value was inferred before the annotation was read (see `inferTypeOfValues`), and its type is the
    // one it was given there: a polymorphic value's type instantiated afresh. A `typeof` where there are no
    // values to name, such as in a type definition, is reported elsewhere.
    | TypeOf value ->
        match state.ExprTypes |> Map.tryFind value.Id with
        | Some ty -> ty, state
        | None -> freshVar state

/// The values named by each `typeof` in `typeExpr`, in the order they are written.
let rec private typeOfValues (typeExpr: TypeExpr) : Expression list =
    let ofList items = items |> List.collect typeOfValues
    match typeExpr.Kind with
    | TypeOf value -> [ value ]
    | TypeName _ | TypeVar _ | TypeUnit -> []
    | TypeTuple items | TypeUnion items | TypeIntersection items | TypeContext items -> ofList items
    | TypeRecord fields ->
        fields
        |> List.collect (function
            | TypeField(_, _, _, Some t, _) | TypeMember(_, Some t) -> typeOfValues t
            | _ -> [])
    | TypeApply(_, args) -> ofList args
    | TypeFunc(arg, ret) -> typeOfValues arg @ typeOfValues ret
    | TypeTag(_, payload) -> payload |> Option.map typeOfValues |> Option.defaultValue []
    | TypeWithContext(contexts, inner) -> ofList contexts @ typeOfValues inner
    | TypeOptional inner | TypeConstraint(_, inner, _) | TypeWhere(inner, _) -> typeOfValues inner

/// The error for each `typeof` in the type definition `name`: a type definition is not in the scope of any value.
let private typeOfInTypeDef (name: Identifier) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    body :: (parameters |> List.choose snd)
    |> List.collect typeOfValues
    |> List.map (fun value ->
        Diagnostics.at DiagnosticCodes.MisplacedTypeof value.Span $"'typeof' cannot be used in the definition of the type '{name}', only in the type of a value")

let private extractContextFromTypeExpr (typeExpr: TypeExpr option) : TypeExpr list option * TypeExpr option =
    match typeExpr with
    | Some (Kind (TypeWithContext(contexts, inner))) -> Some contexts, Some inner
    | Some (Kind (TypeContext contexts)) -> Some contexts, None
    | Some other -> None, Some other
    | None -> None, None

let private extendEnvWithRecordFields (env: TypeEnv) (ty: Ty) =
    let addFields fields =
        fields
        |> Map.fold (fun acc name fieldTy -> TypeEnv.extend name (TypeEnv.mono fieldTy) acc) env
    match Unifier.unalias ty with
    | TyRecord fields -> addFields fields
    | TyNominal(_, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord fields -> addFields fields
        | _ -> env
    | _ -> env

/// The members of the context `ctxExpr` names, each with its type and its symbol, if the context declares one.
let rec private collectContextMembers (state: InferState) (ctxExpr: TypeExpr) : (Identifier * Ty * SymbolId option) list * InferState =
    match ctxExpr.Kind with
    | TypeRecord fields ->
        let mutable current = state
        let members =
            fields
            |> List.choose (function
                | TypeField(name, _, _, typeOpt, _) -> Some (name, typeOpt)
                | TypeMember(name, typeOpt) -> Some (name, typeOpt))
            |> List.map (fun (name, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some t -> typeExprToTy current t
                    | None -> freshVar current
                current <- next
                name, ty, None)
        members, current
    | TypeContext contexts ->
        let mutable current = state
        let members =
            contexts
            |> List.collect (fun item ->
                let itemMembers, next = collectContextMembers current item
                current <- next
                itemMembers)
        members, current
    | TypeName name
    | TypeApply(name, _) ->
        let infoOpt = state.TypeDefs |> Map.tryFind name
        let state =
            match infoOpt with
            | Some info -> recordTypeReference ctxExpr info state
            | None -> state
        // Each member's symbol is the one the context's definition declared, in whichever module that is.
        let withSymbols (members: (Identifier * Ty * SymbolId option) list) =
            match infoOpt with
            | Some info -> members |> List.map (fun (memberName, ty, _) -> memberName, ty, info.Members |> Map.tryFind memberName)
            | None -> members
        match state.ContextDefs |> Map.tryFind name with
        | Some ctxDef ->
            let members, next = collectContextMembers state ctxDef
            withSymbols members, next
        | None ->
            // Also check TypeDefs for non-context types used as contexts
            match infoOpt with
            | Some info ->
                match Unifier.unalias info.Underlying with
                | TyRecord fields ->
                    fields |> Map.toList |> List.map (fun (memberName, ty) -> memberName, ty, None) |> withSymbols, state
                | _ -> [], state
            | None -> [], state
    | _ -> [], state

let private extendEnvWithContexts (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) =
    let mutable current = state
    let mutable env' = env
    for ctx in contexts do
        let members, next = collectContextMembers current ctx
        current <- next
        // A context named through an import's alias (`c.Console`) also supplies its members under the alias
        // (`c.println`), which would otherwise still be the ambient instance's.
        let qualifier =
            match ctx.Kind with
            | TypeName name
            | TypeApply(name, _) when name.Contains '.' -> Some (name.Substring(0, name.LastIndexOf '.'))
            | _ -> None
        env' <-
            members
            |> List.fold (fun acc (name, ty, symbol) ->
                let acc = TypeEnv.declare name symbol (TypeEnv.mono ty) acc
                match qualifier with
                | Some qualifier -> TypeEnv.declare $"{qualifier}.{name}" symbol (TypeEnv.mono ty) acc
                | None -> acc) env'
    env', current

let rec private unknownContextNames (state: InferState) (ctxExpr: TypeExpr) : Identifier list =
    match ctxExpr.Kind with
    | TypeContext contexts -> contexts |> List.collect (unknownContextNames state)
    | TypeName name
    | TypeApply(name, _) ->
        if state.ContextDefs.ContainsKey name || (tryFindTypeDef name state).IsSome then [] else [ name ]
    | _ -> []

/// The type names written in `typeExpr` that name no type here: `Strnig`, or `$Email` where only `m.$Email` is in
/// scope. `parameters` are the names of the parameters of the type definition `typeExpr` is the body of, if it is
/// one. Contexts are checked apart.
let rec private unknownTypeNames (state: InferState) (parameters: Set<Identifier>) (typeExpr: TypeExpr) : (Identifier * SourceSpan) list =
    let isUnknown name = not (isBuiltInTypeName name || parameters.Contains name) && (tryFindTypeDef name state).IsNone
    let ofType = unknownTypeNames state parameters
    match typeExpr.Kind with
    | TypeName name when isUnknown name -> [ name, typeExpr.Span ]
    | TypeApply(name, args) -> (if isUnknown name then [ name, typeExpr.Span ] else []) @ (args |> List.collect ofType)
    | TypeTuple items
    | TypeUnion items
    | TypeIntersection items -> items |> List.collect ofType
    | TypeRecord fields ->
        fields
        |> List.collect (function
            | TypeField(_, _, _, Some t, _)
            | TypeMember(_, Some t) -> ofType t
            | _ -> [])
    | TypeFunc(arg, ret) -> ofType arg @ ofType ret
    | TypeTag(_, Some payload)
    | TypeOptional payload
    | TypeWithContext(_, payload)
    | TypeConstraint(_, payload, _)
    | TypeWhere(payload, _) -> ofType payload
    | _ -> []

/// The error for each type name written in `typeExprs` that names no type, once per name.
let private unknownTypeErrors (state: InferState) (parameters: Set<Identifier>) (typeExprs: TypeExpr list) : Diagnostic list =
    typeExprs
    |> List.collect (unknownTypeNames state parameters)
    |> List.distinctBy fst
    |> List.map (fun (typeName, span) -> Diagnostics.at DiagnosticCodes.UnknownType span $"Unknown type '{typeName}'")

/// Whether every type an annotation, such as a def's or a lambda parameter's, names exists.
let private ensureTypesExist (state: InferState) (typeExprs: TypeExpr list) : Result<unit, Diagnostic list> =
    match unknownTypeErrors state Set.empty typeExprs with
    | [] -> Ok ()
    | errors -> Error errors

/// The error for each type name the definition of a type names that names no type: its body's, and its parameters'
/// constraints'. Its parameters are in scope in both.
let private unknownTypesInTypeDef (state: InferState) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
    unknownTypeErrors state (parameters |> List.map fst |> Set.ofList) (body :: (parameters |> List.choose snd))

/// Whether every context in the signature of `name` exists; `span` is where the definition's name is written.
let private ensureContextsExist (state: InferState) (name: Identifier) (span: SourceSpan) (contexts: TypeExpr list) : Result<unit, Diagnostic list> =
    match contexts |> List.collect (unknownContextNames state) |> List.distinct with
    | [] -> Ok ()
    | unknown ->
        unknown
        |> List.map (fun ctxName -> Diagnostics.at DiagnosticCodes.UnknownContext span $"Unknown context '{ctxName}' in the signature of '{name}'")
        |> Error

let private registerContextRequirement (state: InferState) (name: Identifier) (contexts: TypeExpr list) =
    { state with ContextRequirements = state.ContextRequirements |> Map.add name contexts }

/// Whether `name` is bound in `env` to the ambient instance's member, not to something nearer that shadows it: a
/// member of a context the def declares, or of an instance a `use` loads.
let private isAmbientMember (env: TypeEnv) (state: InferState) (name: Identifier) =
    match state.AmbientMembers |> Map.tryFind name, TypeEnv.tryFind name env with
    | Some (symbol, _), Some scheme -> scheme.Symbol = Some symbol
    | _ -> false

/// The ambient contexts that code in `env` runs in: every one at the top level, else those something nearer
/// supplies every member of.
let private availableAmbientContexts (env: TypeEnv) (state: InferState) : Set<Identifier> =
    let membersByContext =
        state.AmbientMembers
        |> Map.toList
        |> List.groupBy (fun (_, (_, ctxName)) -> ctxName)
    let fromMembers =
        membersByContext
        |> List.filter (fun (_, members) ->
            match state.ContextScope with
            | AmbientScope -> true
            | DefScope _ ->
                members |> List.forall (fun (memberName, _) -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName)))
        |> List.map fst
        |> Set.ofList
    match state.ContextScope with
    | AmbientScope -> Set.union fromMembers state.CarriedAmbientContexts
    | DefScope _ -> fromMembers

/// `what` (which ends "requires", say) the context `ctxName` at `span`, in the def `defName`, whose type does not
/// declare it.
let private undeclaredContext (defName: Identifier) (ctxName: Identifier) (what: string) (span: SourceSpan) =
    Diagnostics.at DiagnosticCodes.UndeclaredContext span $"{what} the context '{ctxName}', which '{defName}' does not declare: add [{ctxName}] to the type of '{defName}', or load an instance with `use`"

/// `what` needs the ambient context `ctxName` where nothing nearer supplies it: fine at the top level, inferred for
/// an unannotated def, and an error in a def whose type does not declare it.
let private needAmbientContext (state: InferState) (ctxName: Identifier) (what: string) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    match state.ContextScope with
    | AmbientScope -> Ok state
    | DefScope(defName, true) ->
        let contexts = state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty
        Ok { state with InferredContexts = state.InferredContexts |> Map.add defName (contexts.Add ctxName) }
    | DefScope(defName, false) -> Error [ undeclaredContext defName ctxName what span ]

/// Checks the context use of naming `name` at `span`: a member of the ambient instance needs its context, and a use
/// of an unannotated top-level def is kept to check once its contexts are known.
let private checkContextUse (env: TypeEnv) (state: InferState) (name: Identifier) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    if isAmbientMember env state name then
        let _, ctxName = state.AmbientMembers.[name]
        needAmbientContext state ctxName $"'{name}' is a member of" span
    else
        match state.UnannotatedDefs |> Map.tryFind name, TypeEnv.tryFind name env with
        | Some symbol, Some scheme when scheme.Symbol = Some symbol && not (state.AmbientMembers.IsEmpty && state.CarriedAmbientContexts.IsEmpty) ->
            let deferred = { Caller = state.ContextScope; Callee = name; Span = span; Available = availableAmbientContexts env state }
            Ok { state with DeferredContextUses = deferred :: state.DeferredContextUses }
        | _ -> Ok state

/// Checks that `contexts`, which `name` requires, are available at `span`. A context with a member not in scope at all
/// is an error only when `reportMissing`.
let private ensureContextAvailable (reportMissing: bool) (env: TypeEnv) (state: InferState) (name: Identifier) (contexts: TypeExpr list) (span: SourceSpan) : Result<InferState, Diagnostic list> =
    let requiredMembers =
        contexts
        |> List.map (fun ctx ->
            let members, _ = collectContextMembers state ctx
            ctx, members |> List.map (fun (memberName, _, _) -> memberName))
    let isCarried (ctx: TypeExpr) =
        match ctx.Kind with
        | TypeName ctxName
        | TypeApply(ctxName, _) -> state.CarriedAmbientContexts.Contains ctxName
        | _ -> false
    // A carried ambient context is supplied by something nearer only if its members are in scope and not the
    // instance's; otherwise it is needed from the ambient instance, as a context this module can name is.
    let suppliedNearer members =
        members |> List.forall (fun memberName -> TypeEnv.tryFind memberName env |> Option.isSome && not (isAmbientMember env state memberName))
    // Each context with a member not in scope at all, with those members.
    let missing =
        requiredMembers
        |> List.filter (fst >> isCarried >> not)
        |> List.choose (fun (ctx, members) ->
            match members |> List.filter (fun memberName -> TypeEnv.tryFind memberName env |> Option.isNone) with
            | [] -> None
            | absent -> Some (ctx, absent))
    if missing.IsEmpty || not reportMissing then
        // Every member is in scope, but one the ambient instance supplies is only available where its context is.
        requiredMembers
        |> List.fold (fun result (ctx, members) ->
            result
            |> Result.bind (fun current ->
                match ctx.Kind, members |> List.exists (isAmbientMember env current) with
                | (TypeName ctxName | TypeApply(ctxName, _)), _ when isCarried ctx && not (suppliedNearer members) ->
                    needAmbientContext current ctxName $"'{name}' requires" span
                | (TypeName ctxName | TypeApply(ctxName, _)), true -> needAmbientContext current ctxName $"'{name}' requires" span
                | _ -> Ok current)) (Ok state)
    else
        // A context is named as it is written, as a type alias is; only one written out as a record has nothing to
        // name it by but its members.
        let named, unnamed =
            missing
            |> List.partition (fun (ctx, _) -> match ctx.Kind with TypeName _ | TypeApply _ -> true | _ -> false)
        let quoted names = names |> List.distinct |> List.map (fun name -> $"'{name}'")
        let contextNames =
            named |> List.choose (fun (ctx, _) -> match ctx.Kind with TypeName ctxName | TypeApply(ctxName, _) -> Some ctxName | _ -> None) |> quoted
        let memberNames = unnamed |> List.collect snd |> quoted
        let listed (items: string list) =
            match items with
            | [] -> ""
            | [ only ] -> only
            | _ -> String.concat ", " (List.truncate (items.Length - 1) items) + " and " + List.last items
        let message =
            match contextNames, memberNames with
            | [ single ], [] -> $"'{name}' requires the context {single}, which is not available here"
            | _, [] -> $"'{name}' requires the contexts {listed contextNames}, which are not available here"
            | [], _ -> $"'{name}' requires the context members {listed memberNames}, which are not available here"
            | _ ->
                let contextsText = if contextNames.Length = 1 then "the context" else "the contexts"
                $"'{name}' requires {contextsText} {listed contextNames} and the context members {listed memberNames}, which are not available here"
        Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]

let private registerTypeDef (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
    let originalTypeVars = state.TypeVars
    let mutable current = state
    let paramEntries =
        parameters
        |> List.map (fun (paramName, _) ->
            let tyVar, next = freshVar current
            current <- next
            match tyVar with
            // The variable keeps the parameter's name, so the type is shown as it is written.
            | TyVar var -> paramName, { var with Name = Some paramName }
            | _ -> failwith "Expected TyVar")
    let updatedTypeVars =
        paramEntries
        |> List.fold (fun acc (paramName, var) -> acc |> Map.add paramName var) current.TypeVars
    let stateWithParams = { current with TypeVars = updatedTypeVars }
    let underlyingTy, next = typeExprToTy stateWithParams body
    let isPrivate = isPrivateType modifiers
    let paramVars = paramEntries |> List.map snd
    let nextNominals =
        if name.StartsWith("$") then next.LocalNominals |> Set.add name else next.LocalNominals
    let nextPrivate =
        if name.StartsWith("$") && isPrivate then next.LocalPrivateNominals |> Set.add name else next.LocalPrivateNominals
    let nextContexts =
        if isContextDef modifiers then next.ContextDefs |> Map.add name body else next.ContextDefs
    let isExport = modifiers |> List.contains Export
    // A type is named in types, and a context's member is in scope only where the context is required, so neither
    // has a scope of its own.
    let declareUnscoped declarer holder symbolName kind state =
        let symbol, declared = declareSymbol declarer holder symbolName kind isExport None state
        symbol, { declared with Scopes = symbol |> Option.map (fun id -> declared.Scopes |> Map.remove id) |> Option.defaultValue declared.Scopes }
    let symbol, next = declareUnscoped TypeDefBody body.Id name TypeDefinition next
    let mutable next = next
    let members =
        match body.Kind with
        | TypeRecord fields when isContextDef modifiers ->
            fields
            |> List.choose (function
                | TypeField(memberName, _, _, _, _) ->
                    let memberSymbol, declared = declareUnscoped RecordTypeFields body.Id memberName ContextMember next
                    next <- declared
                    memberSymbol |> Option.map (fun id -> memberName, id)
                | TypeMember _ -> None)
            |> Map.ofList
        | _ -> Map.empty
    { next with
        TypeVars = originalTypeVars
        TypeDefs =
            next.TypeDefs
            |> Map.add name
                { Name = name
                  Parameters = paramVars
                  Underlying = underlyingTy
                  IsPrivate = isPrivate
                  IsContext = isContextDef modifiers
                  Symbol = symbol
                  Members = members }
        HiddenTypeNames = next.HiddenTypeNames |> Set.remove name
        ContextDefs = nextContexts
        LocalNominals = nextNominals
        LocalPrivateNominals = nextPrivate }

let private instantiate (state: InferState) (scheme: TypeScheme) : Ty * InferState =
    if scheme.Quantified.IsEmpty then
        scheme.Type, state
    else
        let mutable current = state
        let substitutions =
            scheme.Quantified
            |> Seq.fold (fun subst id ->
                let tyVar, next = freshVar current
                current <- next
                subst |> Map.add id tyVar) Map.empty
        Unifier.apply substitutions scheme.Type, current

/// The type of an `if` or `match` whose branches have the given types.
///
/// When every branch is a tag (or a union of tags), the tags are collected into one union instead of
/// being forced to agree, so `if c then #some(x) else #none` is `#some(a) | #none` without an
/// annotation. Same-named tags unify their payloads, so `#some(1)` against `#some("a")` is still an
/// error. When every branch is a record (or tuple) with the same fields, the fields are joined one by
/// one, so `if c then (s, #some(x)) else (s, #none)` is `(string, #some(a) | #none)`. Otherwise the
/// branches have to agree as before: every later branch is constrained equal to the first.
///
/// `rowVars` are the rest variables of the tag unions being matched on. A branch of one of those types
/// (a catch-all arm returning what it caught) joins the union as its rest, so
/// `| #some(a), f -> #some(f(a)) | other, _ -> other` is `#some(b) | r`.
let rec private joinBranchTypes (rowVars: Set<int>) (state: InferState) (branchTypes: Ty list) : Ty * InferState =
    let isUnionItem ty =
        match Unifier.unalias ty with
        | TyTag _ -> true
        | TyVar v -> rowVars.Contains v.Id
        | _ -> false
    let tagsOf ty =
        match Unifier.unalias ty with
        | TyTag _ as tag -> Some [ tag ]
        | TyVar v as var when rowVars.Contains v.Id -> Some [ var ]
        | TyUnion items when items |> List.forall isUnionItem -> Some (items |> List.map Unifier.unalias)
        | _ -> None
    match branchTypes with
    | [] -> TyPrimitive "unit", state
    | head :: tail ->
        let branchTags = branchTypes |> List.map tagsOf
        if branchTags |> List.forall Option.isSome then
            // Merge in order of first appearance; an existing tag of the same name unifies with the new one.
            // A union has one rest, so a second rest variable is unified with the first.
            let items = branchTags |> List.choose id |> List.concat
            let tags = items |> List.filter (function TyTag _ -> true | _ -> false)
            let rests = items |> List.filter (function TyVar _ -> true | _ -> false) |> List.distinct
            let merged, nextState =
                tags
                |> List.fold (fun (acc: Ty list, st) tag ->
                    let name = match tag with TyTag(n, _) -> n | _ -> ""
                    match acc |> List.tryFind (function TyTag(n, _) -> n = name | _ -> false) with
                    | Some existing -> acc, addConstraint existing tag st
                    | None -> acc @ [ tag ], st) ([], state)
            let merged, nextState =
                match rests with
                | [] -> merged, nextState
                | rest :: others -> merged @ [ rest ], others |> List.fold (fun st other -> addConstraint rest other st) nextState
            match merged with
            | [ single ] -> single, nextState
            | items -> TyUnion items, nextState
        else
            let recordFields ty =
                match Unifier.unalias ty with
                | TyRecord fields -> Some fields
                | _ -> None
            let branchFields = branchTypes |> List.map recordFields
            match branchFields with
            | Some first :: _ when
                tail.Length > 0
                && branchFields |> List.forall (function Some fields -> List.ofSeq fields.Keys = List.ofSeq first.Keys | None -> false) ->
                let allFields = branchFields |> List.choose id
                let joined, nextState =
                    first
                    |> Map.fold (fun (acc: Map<string, Ty>, st) key _ ->
                        let fieldTy, next = joinBranchTypes rowVars st (allFields |> List.map (fun fields -> fields.[key]))
                        acc |> Map.add key fieldTy, next) (Map.empty, state)
                TyRecord joined, nextState
            | _ ->
                head, tail |> List.fold (fun st ty -> addConstraint head ty st) state

let private mkTypedExpr expr ty body statements matchArms : TypedExpr =
    { Expr = expr; Type = ty; Body = body; Statements = statements; MatchArms = matchArms; Arguments = None }

/// The fields of a record type, looking through aliases and a nominal wrapper.
let private recordFieldsOf (ty: Ty) : Map<string, Ty> option =
    match Unifier.unalias ty with
    | TyRecord fields -> Some fields
    | TyNominal(_, underlying, _) ->
        match Unifier.unalias underlying with
        | TyRecord fields -> Some fields
        | _ -> None
    | _ -> None

/// The fields of a record type, looking through a nominal wrapper. Member access and `with` need a record's
/// fields while inference is still running, so a type that is still a variable here (a call's result, or a
/// local bound to one) is first resolved against the constraints gathered so far.
let private recordFieldsSoFar (state: InferState) (ty: Ty) : Map<string, Ty> option =
    let resolved =
        match ty with
        | TyVar _ ->
            match Unifier.unify state.Constraints with
            | Ok subst -> Unifier.apply subst ty
            | Error _ -> ty
        | _ -> ty
    recordFieldsOf resolved
/// `ty` as far as the constraints gathered so far determine it; a type variable stays one when nothing does.
/// Variables inside it are resolved too: a call's `#some(b) | r` may already be `#some(string) | #notFound`.
let private resolveSoFar (state: InferState) (ty: Ty) : Ty =
    match ty with
    | TyPrimitive _ -> ty
    | _ ->
        match Unifier.unify state.Constraints with
        | Ok subst -> Unifier.apply subst ty
        | Error _ -> ty

/// The type of the field `fieldName` of a value of type `ty`, as far as the constraints gathered so far tell. A
/// value of no known type yet is taken to be a record with at least that field, and an open record without it
/// to have it among its others: reading `p.name` makes `p` a `(name: a, ...r)`. `None` when `ty` is known not to
/// have the field.
let private fieldTySoFar (state: InferState) (ty: Ty) (fieldName: string) : (Ty * InferState) option =
    let openWith (row: Ty) (next: InferState) =
        let fieldTy, next = freshVar next
        let rest, next = freshVar next
        match rest with
        | TyVar restVar -> Some (fieldTy, addConstraint row (TyOpenRecord(Map.ofList [ fieldName, fieldTy ], restVar)) next)
        | _ -> None
    let resolved = resolveSoFar state ty
    match recordFieldsOf resolved with
    | Some fields -> fields |> Map.tryFind fieldName |> Option.map (fun fieldTy -> fieldTy, state)
    | None ->
        match Unifier.unalias resolved with
        | TyVar _ -> openWith resolved state
        | TyOpenRecord(fields, row) ->
            match fields |> Map.tryFind fieldName with
            | Some fieldTy -> Some (fieldTy, state)
            | None -> openWith (TyVar row) state
        | _ -> None

/// A def's type `ty` generalised against `env`, both resolved against the constraints gathered so far. Unresolved,
/// a local bound to a call's result is a type variable, and generalising that would make every later use a fresh,
/// unrelated variable (and let a match on it skip the exhaustiveness check, #61); a variable `env` only has once
/// resolved, as a parameter's `#some(a) | r` does, would be generalised as if nothing else had it.
///
/// Every union rest variable that nothing can add tags to is solved to the empty union first:
/// `#some(5) \mapOption f` is a `#some(string)`, not a `#some(string) | r`. Such a variable is in no function's
/// input (see `Unifier.outputOnlyRestVars`), was not named in an annotation, and is not shared with `env`, where a
/// later constraint could still give it tags. The constraint solving it is added to the state, so the types
/// recorded for the def's expressions lose it too. A rest the unifier made up, as it does when a closed union is
/// given where an open one is expected, may be numbered differently the next time the constraints are solved, so
/// rather than it, each of inference's own variables solved to a type with it is constrained to that type without it.
let private generalizeDef (env: TypeEnv) (state: InferState) (ty: Ty) : TypeScheme * InferState =
    match Unifier.unify state.Constraints with
    | Error _ -> TypeEnv.generalize env ty, state
    | Ok subst ->
        let resolved = Unifier.apply subst ty
        let envVars =
            TypeEnv.freeEnvVars env
            |> Seq.map (fun id -> TypeEnv.freeTyVars (Unifier.apply subst (TyVar { Id = id; Name = None })))
            |> Set.unionMany
        let closed =
            Unifier.outputOnlyRestVars resolved
            |> List.filter (fun v -> v.Name.IsNone && not (envVars.Contains v.Id))
        let bottom = TyUnion []
        let closing = closed |> List.map (fun v -> v.Id, bottom) |> Map.ofList
        let closedTy = Unifier.apply closing resolved
        let madeUp, own = closed |> List.partition Unifier.isMadeUpRest
        let solvedWithMadeUp =
            if madeUp.IsEmpty then []
            else
                let madeUpIds = madeUp |> List.map (fun v -> v.Id) |> Set.ofList
                subst
                |> Map.toList
                |> List.choose (fun (id, _) ->
                    let var = { Id = id; Name = None }
                    let solved = Unifier.apply subst (TyVar var)
                    if Unifier.isMadeUpRest var || Set.isEmpty (Set.intersect madeUpIds (TypeEnv.freeTyVars solved)) then None
                    else Some (TyVar var, Unifier.apply closing solved))
        { Quantified = Set.difference (TypeEnv.freeTyVars closedTy) envVars
          Type = closedTy
          Symbol = None },
        (own |> List.map (fun v -> TyVar v, bottom)) @ solvedWithMadeUp
        |> List.fold (fun st (var, closedTy) -> addConstraint var closedTy st) state

/// Arithmetic and ordering work on `int` and `float`. Without numeric type classes yet, an operation is on
/// floats when either operand is already known to be one, and defaults to `int` otherwise.
let private numericOperandType (state: InferState) (leftTy: Ty) (rightTy: Ty) =
    let isFloat ty =
        match Unifier.unalias (resolveSoFar state ty) with
        | TyPrimitive "float" -> true
        | _ -> false
    if isFloat leftTy || isFloat rightTy then TyPrimitive "float" else numericType
let private argTypesFromExpected (state: InferState) (args: (Identifier * TypeExpr option) list) (expectedInput: Ty) =
    let mutable current = state
    let argTys =
        match Unifier.unalias expectedInput with
        | TyRecord _ when args.Length = 1 -> [ expectedInput ]
        | TyRecord fields ->
            args
            |> List.mapi (fun index (name, _) ->
                match fields |> Map.tryFind name with
                | Some ty -> ty
                | None ->
                    let tupleName = tupleFieldName index
                    match fields |> Map.tryFind tupleName with
                    | Some ty -> ty
                    | None ->
                        let ty, next = freshVar current
                        current <- next
                        ty)
        | _ when args.Length = 1 -> [ expectedInput ]
        | _ ->
            args
            |> List.map (fun _ ->
                let ty, next = freshVar current
                current <- next
                ty)
    argTys, current


/// Records the type inference gave an expression, so it can be looked up by node once unification has resolved it.
let private recordExprType (result: Result<TypedExpr * InferState, Diagnostic list>) =
    result
    |> Result.map (fun (typedExpr, state) ->
        typedExpr, { state with ExprTypes = state.ExprTypes |> Map.add typedExpr.Expr.Id typedExpr.Type })

/// The constraints inferring `expr` adds are reported at `expr` (or, for a node the compiler made up, wherever the
/// enclosing ones are); those its parent adds afterwards are reported at the parent again.
let rec private inferExpr (env: TypeEnv) (state: InferState) (expr: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    let outer = state.Origin
    let origin = if SourceSpan.isNone expr.Span then outer else expr.Span
    inferExprKind env { state with Origin = origin } expr
    |> recordExprType
    |> Result.map (fun (typedExpr, next) -> typedExpr, { next with Origin = outer })

and private inferExprKind (env: TypeEnv) (state: InferState) (expr: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    match expr.Kind with
    | LiteralExpr literal ->
        Ok (mkTypedExpr expr (literalType literal) None None None, state)
    | InterpolatedString parts ->
        let mutable current = state
        let mutable errors: Diagnostic list = []
        for part in parts do
            match part with
            | StringText _ -> ()
            | StringExpr inner ->
                match inferExpr env current inner with
                | Ok (_, next) -> current <- next
                | Error err -> errors <- errors @ err
        if errors.IsEmpty then
            Ok (mkTypedExpr expr (TyPrimitive "string") None None None, current)
        else
            Error errors
    | UnitExpr ->
        Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, state)
    | IdentifierExpr name ->
        match TypeEnv.tryFind name env with
        | Some scheme when not (referenceErrors name expr scheme state).IsEmpty -> Error (referenceErrors name expr scheme state)
        | Some scheme ->
            // A function that needs an ambient context can only be passed on where it runs in that context. One that
            // needs a context nothing here supplies can be passed to code that loads it.
            let contextCheck =
                match state.ContextRequirements |> Map.tryFind name with
                | Some contexts -> ensureContextAvailable false env state name contexts expr.Span
                | None -> Ok state
                |> Result.bind (fun current -> checkContextUse env current name expr.Span)
            match contextCheck with
            | Error err -> Error err
            | Ok state ->
                let ty, next = instantiate (recordReference expr scheme state) scheme
                Ok (mkTypedExpr expr ty None None None, next)
        | None -> Error [ Diagnostics.at DiagnosticCodes.UnknownIdentifier expr.Span $"Unknown identifier '{name}'" ]
    | FunctionCall(name, spanOpt, args) ->
        let calleeSpan = nameSpan spanOpt expr
        if name = "dbg" then
            match args with
            | [single] ->
                match inferExpr env state single with
                | Ok (typedArg, next) -> Ok (mkTypedExpr expr typedArg.Type None None None, next)
                | Error err -> Error err
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall calleeSpan "dbg expects a single argument" ]
        elif name = "not" then
            // The `not` keyword: the parser represents `not x` as a call to "not" with one argument.
            match args with
            | [single] ->
                match inferExpr env state single with
                | Ok (typedArg, next) ->
                    let constrained = addConstraint typedArg.Type (TyPrimitive "bool") next
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, constrained)
                | Error err -> Error err
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "not expects a single operand" ]
        elif name = "__with" then
            // `record with f = v, ...`: the parser represents a record update as a call to "__with" with the
            // record and a record of the updated fields. The result has the record's type; every updated
            // field must exist on it, at the field's type.
            match args with
            | [ baseExpr; Kind (RecordExpr updates) ] ->
                match inferExpr env state baseExpr with
                | Error err -> Error err
                | Ok (typedBase, afterBase) ->
                    match recordFieldsOf typedBase.Type with
                    | None -> Error [ Diagnostics.at DiagnosticCodes.InvalidRecordUpdate baseExpr.Span "`with` needs a record whose fields are known here" ]
                    | Some fields ->
                        let mutable current = afterBase
                        let mutable errors: Diagnostic list = []
                        // Field names have no span of their own (#96): a field is reported at its new value.
                        for update in updates do
                            match update with
                            | NamedField(fieldName, valueExpr) ->
                                match fields |> Map.tryFind fieldName with
                                | None -> errors <- errors @ [ Diagnostics.at DiagnosticCodes.UnknownMember valueExpr.Span $"Record has no member '{fieldName}' to update" ]
                                | Some fieldTy ->
                                    match inferExpr env current valueExpr with
                                    | Ok (typedValue, next) -> current <- addConstraintAt valueExpr.Span typedValue.Type fieldTy next
                                    | Error err -> errors <- errors @ err
                            | PositionalField valueExpr -> errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordUpdate valueExpr.Span "`with` updates fields by name" ]
                        if errors.IsEmpty then Ok (mkTypedExpr expr typedBase.Type None None None, current)
                        else Error errors
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`with` expects a record and the fields to update" ]

        elif name = "__range" then
            // `a..b`: a bounded range of ints, a `seq(int)` (see the ranges page).
            let mutable current = state
            let mutable errors: Diagnostic list = []
            for arg in args do
                match inferExpr env current arg with
                | Ok (typedArg, next) -> current <- addConstraint typedArg.Type (TyPrimitive "int") next
                | Error err -> errors <- errors @ err
            if errors.IsEmpty then Ok (mkTypedExpr expr (TyApply("seq", [ TyPrimitive "int" ])) None None None, current)
            else Error errors
        elif name = "__rec" then
            // `rec name = value`: the value of a self-referencing local def; the block already binds the name.
            match args with
            | [ inner ] -> inferExpr env state inner
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "rec expects a single definition" ]
        elif name = "__for" then
            // `for iterable | arms`: the parser passes the iterable and a one-parameter lambda. The lambda's
            // parameter is an element of the iterable (a `list(a)` or a `seq(a)`), and the loop is unit.
            match args with
            | [ iterable; Kind (Lambda(lambdaArgs, lambdaBody)) as lambdaExpr ] ->
                match inferExpr env state iterable with
                | Error err -> Error err
                | Ok (typedIterable, afterIterable) ->
                    let elementTy, constrained =
                        match Unifier.unalias (resolveSoFar afterIterable typedIterable.Type) with
                        | TyApply(("seq" | "list"), [ elementTy ]) -> elementTy, afterIterable
                        | _ ->
                            let elementTy, next = freshVar afterIterable
                            elementTy, addConstraint typedIterable.Type (listType elementTy) next
                    match inferLambdaWithExpected env constrained lambdaExpr lambdaArgs lambdaBody (Some elementTy) None with
                    | Ok (_, next) -> Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, next)
                    | Error err -> Error err
            | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "for expects an iterable and a loop body" ]
        else
        let contextCheck =
            match state.ContextRequirements |> Map.tryFind name with
            | Some contexts -> ensureContextAvailable true env state name contexts calleeSpan
            | None -> Ok state
            |> Result.bind (fun current -> checkContextUse env current name calleeSpan)
        match contextCheck with
        | Error err -> Error err
        | Ok state ->
            match TypeEnv.tryFind name env with
            | Some scheme when not (referenceErrors name expr scheme state).IsEmpty -> Error (referenceErrors name expr scheme state)
            | Some scheme ->
                let mutable current = state
                let funcTy, nextState = instantiate (recordReference expr scheme current) scheme
                current <- { nextState with CalleeTypes = nextState.CalleeTypes |> Map.add expr.Id funcTy }

                let recordFieldsOpt = recordFieldsOf funcTy

                let isWorkflowCall =
                    match args with
                    | [Kind (Lambda(lambdaArgs, _))] when lambdaArgs.IsEmpty -> recordFieldsOpt
                    | _ -> None

                match isWorkflowCall with
                | Some fields ->
                    let missingMembers =
                        ["await"; "pure"]
                        |> List.filter (fun memberName -> not (fields |> Map.containsKey memberName))
                    if not missingMembers.IsEmpty then
                        let missingText = String.concat ", " missingMembers
                        Error [ Diagnostics.at DiagnosticCodes.InvalidWorkflow calleeSpan $"Workflow '{name}' is missing context members: {missingText}" ]
                    else
                        let envWithCtx = extendEnvWithRecordFields env funcTy
                        match args with
                        | [Kind (Lambda(lambdaArgs, lambdaBody)) as lambdaExpr] ->
                            match inferLambdaWithExpected envWithCtx current lambdaExpr lambdaArgs lambdaBody (Some (TyPrimitive "unit")) None with
                            | Ok (typedLambda, next) ->
                                let returnTy =
                                    match Unifier.unalias typedLambda.Type with
                                    | TyFunc(_, ret) -> ret
                                    | other -> other
                                Ok (mkTypedExpr expr returnTy None None None, next)
                            | Error err -> Error err
                        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidWorkflow calleeSpan $"Workflow '{name}' must be called with a trailing lambda" ]
                | None ->
                    // With a known signature, each argument is checked against its parameter as it is inferred,
                    // so a lambda argument's parameters can take their types from the signature and from the
                    // arguments before it: in `customer \map { c -> c.name }`, `c` is already a record when
                    // `c.name` is checked.
                    let paramTys =
                        match Unifier.unalias funcTy, args with
                        | TyFunc(declaredInput, _), [ _ ] -> Some [ declaredInput ]
                        | TyFunc(declaredInput, _), _ ->
                            match Unifier.unalias declaredInput with
                            | TyRecord fields when fields.Count = args.Length ->
                                let tys = args |> List.mapi (fun index _ -> fields |> Map.tryFind (tupleFieldName index))
                                if tys |> List.forall Option.isSome then Some (tys |> List.choose id) else None
                            | _ -> None
                        | _ -> None
                    let expectedLambdaInput index lambdaArgs =
                        let unannotated = lambdaArgs |> List.forall (fun (_, typeOpt: TypeExpr option) -> typeOpt.IsNone)
                        match paramTys |> Option.bind (List.tryItem index) |> Option.map Unifier.unalias with
                        | Some (TyFunc(lambdaInput, _)) when unannotated && not (List.isEmpty lambdaArgs) ->
                            match Unifier.unify current.Constraints with
                            | Ok subst ->
                                // A lambda of several parameters needs the input's fields to split between them.
                                match Unifier.apply subst lambdaInput with
                                | resolved when (match Unifier.unalias resolved with TyRecord _ -> true | _ -> false) -> Some resolved
                                | resolved when lambdaArgs.Length = 1 -> Some resolved
                                | _ -> None
                            | Error _ -> None
                        | _ -> None
                    let argResults =
                        args
                        |> List.mapi (fun index arg ->
                            let inferred =
                                match arg with
                                | Kind (Lambda(lambdaArgs, lambdaBody)) ->
                                    match expectedLambdaInput index lambdaArgs with
                                    | Some expectedInput -> inferLambdaWithExpected env current arg lambdaArgs lambdaBody (Some expectedInput) None
                                    | None -> inferExpr env current arg
                                | _ -> inferExpr env current arg
                            match inferred with
                            | Ok (ty, next) ->
                                current <-
                                    match paramTys |> Option.bind (List.tryItem index) with
                                    | Some paramTy -> addAssignableConstraintAt arg.Span ty.Type paramTy next
                                    | None -> next
                                Ok ty
                            | Error err -> Error err)
                    let errors = argResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                    if errors.IsEmpty then
                        let typedArgs = argResults |> List.choose (function Ok typedExpr -> Some typedExpr | _ -> None)
                        let argTys = typedArgs |> List.map (fun typedExpr -> typedExpr.Type)
                        // When the callee's type is already a function (an annotated or already-inferred
                        // def), its return type is the call's type directly, so a member access or match on
                        // the result can see it without waiting for the constraint below to be solved.
                        match Unifier.unalias funcTy, paramTys with
                        // Each argument was checked against its parameter above, which is all a known signature
                        // asks; constraining several arguments together as well, as one record, would make each
                        // equal to its parameter, and undo an argument union's widening into its parameter's.
                        | TyFunc(_, declaredRet), Some _ when args.Length > 1 ->
                            Ok ({ mkTypedExpr expr declaredRet None None None with Arguments = Some typedArgs }, current)
                        | _ ->
                        let retTy, nextState =
                            match Unifier.unalias funcTy with
                            | TyFunc(_, declaredRet) -> declaredRet, current
                            | _ -> freshVar current
                        // A known input is what the argument must be assignable to. A fresh variable in its place
                        // would be bound to the argument's own type first (constraints are solved newest first),
                        // and a nominal argument would then have to equal its parameter's shape.
                        let expectedInput, nextState2 =
                            match Unifier.unalias funcTy with
                            | TyFunc(declaredInput, _) -> declaredInput, nextState
                            | _ -> freshVar nextState
                        let inputTy = inputTypeFromArgs argTys
                        // A single argument is the call's input: a mismatch is that argument's. So is one with the
                        // callee's type when that is known to be a function, whose result is the call's already.
                        let inputSpan = match args with [ single ] -> single.Span | _ -> SourceSpan.none
                        let funcSpan = if paramTys.IsSome then inputSpan else SourceSpan.none
                        let withFuncConstraint = addConstraintAt funcSpan funcTy (TyFunc(expectedInput, retTy)) nextState2
                        let withAssignable = addAssignableConstraintAt inputSpan inputTy expectedInput withFuncConstraint
                        Ok ({ mkTypedExpr expr retTy None None None with Arguments = Some typedArgs }, withAssignable)
                    else
                        Error errors
            | None ->
                match tryFindTypeDef name state with
                // Only a nominal type's home module can make one: anywhere else it comes from what that module exports.
                | Some info when isNominal info && not (state.LocalNominals.Contains info.Name) ->
                    Error [ Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule calleeSpan $"The nominal type '{name}' can only be constructed in the module that defines it; use a function that module exports instead" ]
                | Some info ->
                    let mutable current = state
                    let argResults =
                        args
                        |> List.map (fun arg ->
                            match inferExpr env current arg with
                            | Ok (ty, next) ->
                                current <- next
                                Ok ty
                            | Error err -> Error err)
                    let errors = argResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                    if errors.IsEmpty then
                        let argTys =
                            argResults
                            |> List.choose (function Ok typedExpr -> Some typedExpr.Type | _ -> None)
                        let inputTy = inputTypeFromArgs argTys
                        let constrained = addAssignableConstraint inputTy info.Underlying current
                        Ok (mkTypedExpr expr (namedType name info) None None None, constrained)
                    else
                        Error errors
                | None ->
                    // `s.contains("x")` is read as a call to a function named `s.contains`. When `s` is a value, the
                    // dot was meant to reach a definition attached to its type, which is a pipe's to do.
                    let dot = name.LastIndexOf '.'
                    let receiver = if dot > 0 then name.Substring(0, dot) else ""
                    match TypeEnv.tryFind receiver env with
                    | Some scheme when dot > 0 && not (receiver.Contains '.') ->
                        let memberName = name.Substring(dot + 1)
                        let receiverTy = resolveSoFar state scheme.Type
                        let message =
                            match tryResolveAttachedFunction env receiverTy memberName with
                            | Some attached ->
                                $"'{receiver}' is a value, so '{name}' is not a function's name. '{attached}' is attached to its type: call it with a pipe, as in `{receiver} \\{memberName}(...)`"
                            | None -> $"'{receiver}' is a value, so '{name}' is not a function's name, and '{Unifier.tyToString receiverTy}' has no '{memberName}' attached"
                        Error [ Diagnostics.at DiagnosticCodes.UnknownFunction calleeSpan message ]
                    | _ -> Error [ Diagnostics.at DiagnosticCodes.UnknownFunction calleeSpan $"Unknown function '{name}'" ]
    | Lambda(args, body) ->
        inferLambdaWithExpected env state expr args body None None
    | BinaryOp(_, left, right) ->
        match inferExpr env state left with
        | Error err -> Error err
        | Ok (leftExpr, next) ->
            match inferExpr env next right with
            | Error err -> Error err
            | Ok (rightExpr, nextState) ->
                let constrained = addConstraint leftExpr.Type rightExpr.Type nextState
                match expr.Kind with
                | BinaryOp("++", _, _) ->
                    // `++` concatenates two strings or two lists; both sides have the same type, which is the result.
                    // When neither side's type is known yet the result is simply that shared type.
                    let concatenable =
                        [ leftExpr.Type; rightExpr.Type ]
                        |> List.tryPick (fun ty ->
                            match Unifier.unalias ty with
                            | TyPrimitive "string" -> Some ty
                            | TyApply("list", _) -> Some ty
                            | _ -> None)
                    let resultTy = concatenable |> Option.defaultValue leftExpr.Type
                    Ok (mkTypedExpr expr resultTy None None None, constrained)
                | BinaryOp(op, _, _) when ["+"; "-"; "*"; "/"] |> List.contains op ->
                    let numericConstrained =
                        constrained
                        |> addConstraint leftExpr.Type (numericOperandType constrained leftExpr.Type rightExpr.Type)
                    Ok (mkTypedExpr expr leftExpr.Type None None None, numericConstrained)
                | BinaryOp(op, _, _) when ["<"; ">"; "<="; ">="] |> List.contains op ->
                    let numericConstrained =
                        constrained
                        |> addConstraint leftExpr.Type (numericOperandType constrained leftExpr.Type rightExpr.Type)
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, numericConstrained)
                | BinaryOp(op, _, _) when ["=="; "!="] |> List.contains op ->
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, constrained)
                | BinaryOp(op, _, _) when ["and"; "or"] |> List.contains op ->
                    // Both operands are already unified with each other; pin them to bool.
                    let boolConstrained = constrained |> addConstraint leftExpr.Type (TyPrimitive "bool")
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, boolConstrained)
                | _ -> Ok (mkTypedExpr expr leftExpr.Type None None None, constrained)
    | Pipe(value, name, spanOpt, args) ->
        match inferExpr env state value with
        | Error err -> Error err
        | Ok (typedValue, nextState) ->
            // A pipe resolves left to right: the value's type as the code before the pipe settles it picks the
            // attached function, even where that type is still a variable waiting on the module's constraints.
            let resolvedName =
                tryResolveAttachedFunction env (resolveSoFar nextState typedValue.Type) name
                |> Option.defaultValue name
            let nextState =
                if resolvedName = name then nextState
                else { nextState with PipeTargets = nextState.PipeTargets |> Map.add expr.Id resolvedName }
            let callArgs = pipeCallArguments value args
            match inferExpr env nextState { expr with Kind = FunctionCall(resolvedName, spanOpt, callArgs) } with
            | Ok (typedCall, finalState) ->
                Ok ({ typedCall with Expr = { expr with Kind = Pipe(value, resolvedName, spanOpt, args) } }, finalState)
            | Error err -> Error err
    | UseIn(binding, body) ->
        match inferUseBinding env state binding with
        | Error err -> Error err
        | Ok (typedBinding, nextState) ->
            let envWithUse = extendEnvWithRecordFields env typedBinding.Type
            match inferExpr envWithUse nextState body with
            | Ok (typedBody, finalState) -> Ok (mkTypedExpr expr typedBody.Type (Some typedBody) None None, finalState)
            | Error err -> Error err
    | WorkflowBindExpr _ ->
        Error [ Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow expr.Span "Workflow bind used outside of a workflow block" ]
    | WorkflowReturnExpr _ ->
        Error [ Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow expr.Span "Workflow return used outside of a workflow block" ]
    | Block statements ->
        inferBlock env state expr statements
    | TupleExpr items ->
        let mutable current = state
        let tys =
            items
            |> List.map (fun item ->
                match inferExpr env current item with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let errors = tys |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
        if errors.IsEmpty then
            let itemExprs = tys |> List.choose (function Ok ty -> Some ty | _ -> None)
            let itemTys = itemExprs |> List.map (fun typedExpr -> typedExpr.Type)
            Ok (mkTypedExpr expr (tupleRecordType itemTys) None None None, current)
        else
            Error errors
    | RecordExpr fields ->
        let positional, named =
            fields
            |> List.fold (fun (pos, named) field ->
                match field with
                | PositionalField expr -> (expr :: pos, named)
                | NamedField(name, expr) -> (pos, (name, expr) :: named)) ([], [])
        let mutable current = state
        let positionalResults =
            positional
            |> List.rev
            |> List.map (fun expr ->
                match inferExpr env current expr with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let namedResults =
            named
            |> List.rev
            |> List.map (fun (name, expr) ->
                match inferExpr env current expr with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok (name, typedExpr)
                | Error err -> Error err)
        let errors =
            positionalResults
            |> List.choose (function Error err -> Some err | _ -> None)
            |> List.concat
            |> fun errs ->
                let namedErrors = namedResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                errs @ namedErrors
        if errors.IsEmpty then
            let positionalExprs = positionalResults |> List.choose (function Ok ty -> Some ty | _ -> None)
            let positionalFieldsMap =
                positionalExprs
                |> List.mapi (fun index typedExpr -> tupleFieldName index, typedExpr.Type)
            let namedFieldsMap =
                namedResults
                |> List.choose (function Ok (name, typedExpr) -> Some (name, typedExpr.Type) | _ -> None)
            let fieldMap =
                positionalFieldsMap @ namedFieldsMap
                |> Map.ofList
            Ok (mkTypedExpr expr (TyRecord fieldMap) None None None, current)
        else
            Error errors
    | ListExpr items ->
        let mutable current = state
        let itemResults =
            items
            |> List.map (fun item ->
                match inferExpr env current item with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let errors = itemResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
        if errors.IsEmpty then
            match itemResults |> List.choose (function Ok ty -> Some ty | _ -> None) with
            | [] ->
                let elementTy, nextState = freshVar current
                Ok (mkTypedExpr expr (listType elementTy) None None None, nextState)
            | head :: tail ->
                let constrained = tail |> List.fold (fun st ty -> addConstraint head.Type ty.Type st) current
                Ok (mkTypedExpr expr (listType head.Type) None None None, constrained)
        else
            Error errors
    | TagExpr(name, payloadOpt) ->
        match payloadOpt with
        | None -> Ok (mkTypedExpr expr (TyTag(name, None)) None None None, state)
        | Some payload ->
            match inferExpr env state payload with
            | Ok (payloadExpr, next) -> Ok (mkTypedExpr expr (TyTag(name, Some payloadExpr.Type)) None None None, next)
            | Error err -> Error err
    | IfExpr(condition, thenExpr, elseExpr) ->
        match inferExpr env state condition with
        | Error err -> Error err
        | Ok (condExpr, next) ->
            let withConstraint = addConstraint condExpr.Type (primitiveType "bool") next
            match inferExpr env withConstraint thenExpr with
            | Error err -> Error err
            | Ok (thenExprTyped, nextState) ->
                match inferExpr env nextState elseExpr with
                | Error err -> Error err
                | Ok (elseExprTyped, finalState) ->
                    let resultTy, constrained = joinBranchTypes Set.empty finalState [ thenExprTyped.Type; elseExprTyped.Type ]
                    // The condition and branches are kept as statements so that walks over the typed tree (the
                    // backend's collection of local and pattern-bound types) reach what they bind, such as a
                    // match in the else branch of a multi-branch if.
                    let children = [ condExpr; thenExprTyped; elseExprTyped ] |> List.map TypedExprStatement
                    Ok (mkTypedExpr expr resultTy None (Some children) None, constrained)
    | Match(scrutinees, arms) ->
        let mutable current = state
        let scrutineeResults =
            scrutinees
            |> List.map (fun expr ->
                match inferExpr env current expr with
                | Ok (typedExpr, next) ->
                    current <- next
                    Ok typedExpr
                | Error err -> Error err)
        let scrutineeErrors = scrutineeResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
        if not scrutineeErrors.IsEmpty then
            Error scrutineeErrors
        else
            let scrutineeExprs = scrutineeResults |> List.choose (function Ok ty -> Some ty | _ -> None)
            let scrutineeTypes = scrutineeExprs |> List.map (fun typedExpr -> typedExpr.Type)
            let mkTypedPattern pattern ty = { Pattern = pattern; Type = ty }
            let rec tryRecordFields ty =
                match ty with
                | TyRecord fields -> Some fields
                | TyNominal(_, underlying, _)
                | TyAlias(_, _, underlying) -> tryRecordFields underlying
                | _ -> None
            // The variables a pattern binds are declared by the body of its arm, `armBody`, and are in scope until
            // its end. As with `inferExpr`, the constraints inferring `pattern` adds are reported at `pattern`.
            let rec inferPattern (armBody: Expression) (env': TypeEnv) (state': InferState) (scrutineeTy: Ty) (pattern: Pattern) =
                let outer = state'.Origin
                let origin = if SourceSpan.isNone pattern.Span then outer else pattern.Span
                inferPatternKind armBody env' { state' with Origin = origin } scrutineeTy pattern
                |> Result.map (fun (nextEnv, next, typedPattern) -> nextEnv, { next with Origin = outer }, typedPattern)

            and inferPatternKind (armBody: Expression) (env': TypeEnv) (state': InferState) (scrutineeTy: Ty) (pattern: Pattern) =
                match pattern.Kind with
                | WildcardPattern
                | ElsePattern -> Ok (env', state', mkTypedPattern pattern scrutineeTy)
                | IdentifierPattern name ->
                    let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) state'
                    Ok (TypeEnv.declare name symbol (TypeEnv.mono scrutineeTy) env', recordSymbolType symbol scrutineeTy declared, mkTypedPattern pattern scrutineeTy)
                | LiteralPattern literal ->
                    let constrained = addConstraint scrutineeTy (literalType literal) state'
                    Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                | GuardPattern(_, guardExpr) ->
                    match inferExpr env' state' guardExpr with
                    | Ok (guardTyped, nextState) ->
                        let constrained = addConstraint scrutineeTy guardTyped.Type nextState
                        Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                    | Error err -> Error err
                | RangePattern(startExpr, endExpr) ->
                    match inferExpr env' state' startExpr with
                    | Error err -> Error err
                    | Ok (startTyped, nextState) ->
                        match inferExpr env' nextState endExpr with
                        | Error err -> Error err
                        | Ok (endTyped, finalState) ->
                            let constrained =
                                finalState
                                |> addConstraint scrutineeTy startTyped.Type
                                |> addConstraint startTyped.Type endTyped.Type
                            Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                | TuplePattern patterns ->
                    let mutable currentState = state'
                    let itemTypes, nextState =
                        patterns
                        |> List.map (fun _ ->
                            let ty, next = freshVar currentState
                            currentState <- next
                            ty)
                        |> fun tys -> tys, currentState
                    let tupleRecord = tupleRecordType itemTypes
                    let constrained = addConstraint scrutineeTy tupleRecord nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    let mutable innerPatterns: TypedPattern list = []
                    for (pat, ty) in List.zip patterns itemTypes do
                        match inferPattern armBody innerEnv innerState ty pat with
                        | Ok (nextEnv, nextState, typedPattern) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                            innerPatterns <- innerPatterns @ [typedPattern]
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerState, mkTypedPattern pattern tupleRecord)
                    else
                        Error innerErrors
                | TagPattern(tagName, payloadOpt) ->
                    match payloadOpt with
                    | None ->
                        let constrained = addAssignableConstraint (TyTag(tagName, None)) scrutineeTy state'
                        Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                    | Some payload ->
                        let payloadTy, nextState = freshVar state'
                        let constrained = addAssignableConstraint (TyTag(tagName, Some payloadTy)) scrutineeTy nextState
                        match inferPattern armBody env' constrained payloadTy payload with
                        | Ok (nextEnv, nextState, _) ->
                            Ok (nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                        | Error err -> Error err
                | ListPattern(patterns, splatOpt) ->
                    let elementTy, nextState = freshVar state'
                    let constrained = addConstraint scrutineeTy (listType elementTy) nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    for pat in patterns do
                        match inferPattern armBody innerEnv innerState elementTy pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                        | Error err -> innerErrors <- err
                    let innerStateWithSplat =
                        match splatOpt with
                        | Some (Kind (IdentifierPattern name)) ->
                            let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) innerState
                            innerEnv <- TypeEnv.declare name symbol (TypeEnv.mono (listType elementTy)) innerEnv
                            recordSymbolType symbol (listType elementTy) declared
                        | _ -> innerState
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerStateWithSplat, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors
                | ListSplatMiddle(prefix, suffix) ->
                    let elementTy, nextState = freshVar state'
                    let constrained = addConstraint scrutineeTy (listType elementTy) nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    for pat in prefix @ suffix do
                        match inferPattern armBody innerEnv innerState elementTy pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors
                | RecordPattern(typeName, patterns) ->
                    let mutable currentState = state'
                    let itemTypes, nextState =
                        patterns
                        |> List.map (fun _ ->
                            let ty, next = freshVar currentState
                            currentState <- next
                            ty)
                        |> fun tys -> tys, currentState
                    let constrained = addConstraint scrutineeTy (TyRecord (Map.empty)) nextState
                    let mutable innerEnv = env'
                    let mutable innerState = constrained
                    let mutable innerErrors: Diagnostic list = []
                    for (pat, ty) in List.zip patterns itemTypes do
                        match inferPattern armBody innerEnv innerState ty pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        Ok (innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors
                | RecordMemberPattern members ->
                    let mutable innerEnv = env'
                    let mutable innerState = state'
                    let mutable innerErrors: Diagnostic list = []
                    let fieldHints = tryRecordFields scrutineeTy |> Option.defaultValue Map.empty
                    let mutable fieldMap = Map.empty
                    for (name, pat) in members do
                        let hintTy = fieldHints |> Map.tryFind name
                        let memberTy, nextState =
                            match hintTy with
                            | Some ty -> ty, innerState
                            | None -> freshVar innerState
                        innerState <- nextState
                        match inferPattern armBody innerEnv innerState memberTy pat with
                        | Ok (nextEnv, nextState, _) ->
                            innerEnv <- nextEnv
                            innerState <- nextState
                            if hintTy.IsNone then
                                fieldMap <- fieldMap.Add(name, memberTy)
                        | Error err -> innerErrors <- err
                    if innerErrors.IsEmpty then
                        let constrained =
                            if fieldMap.IsEmpty then innerState else addConstraint scrutineeTy (TyRecord fieldMap) innerState
                        Ok (innerEnv, constrained, mkTypedPattern pattern scrutineeTy)
                    else
                        Error innerErrors

            let mutable armExprs: TypedExpr list = []
            let mutable typedArms: TypedMatchArm list = []
            let mutable errors: Diagnostic list = []
            let rec isCatchAllPattern (pattern: Pattern) =
                match pattern.Kind with
                | WildcardPattern
                | ElsePattern
                | IdentifierPattern _ -> true
                // `(a, _)` matches every tuple: a tuple pattern is irrefutable when each element is.
                | TuplePattern elements -> elements |> List.forall isCatchAllPattern
                | _ -> false
            let tryGetTagName (pattern: Pattern) =
                match pattern.Kind with
                | TagPattern(tagName, _) -> Some tagName
                | _ -> None
            let tryGetTagUnion ty =
                let rec unwrap ty =
                    match ty with
                    | TyNominal(_, underlying, _)
                    | TyAlias(_, _, underlying) -> unwrap underlying
                    | TyUnion items ->
                        if items |> List.forall (function TyTag _ -> true | _ -> false) then
                            items
                            |> List.choose (function TyTag(name, _) -> Some name | _ -> None)
                            |> Set.ofList
                            |> Some
                        else
                            None
                    | _ -> None
                unwrap ty
            let isListType ty =
                let rec unwrap ty =
                    match ty with
                    | TyNominal(_, underlying, _)
                    | TyAlias(_, _, underlying) -> unwrap underlying
                    | TyApply("list", _) -> true
                    | _ -> false
                unwrap ty
            let listPatternsCoverAll patternsAtPos =
                let hasEmpty =
                    patternsAtPos
                    |> List.exists (function
                        | Kind (ListPattern(patterns, None)) when patterns.IsEmpty -> true
                        | _ -> false)
                let hasSplat =
                    patternsAtPos
                    |> List.exists (function
                        | Kind (ListPattern(_, Some _)) -> true
                        | _ -> false)
                hasEmpty && hasSplat
            let isRecordType ty =
                let rec unwrap ty =
                    match ty with
                    | TyNominal(_, underlying, _)
                    | TyAlias(_, _, underlying) -> unwrap underlying
                    | TyRecord _ -> true
                    | _ -> false
                unwrap ty
            let patternsForIndex index =
                arms
                |> List.choose (fun (patterns, _) ->
                    if index < patterns.Length then Some patterns.[index] else None)
            // A scrutinee's type may still be a variable here (a call's result, a local bound to one). For the
            // exhaustiveness check it is resolved against the constraints gathered so far, and before the arms
            // add theirs: a lone `#some(_)` arm would otherwise narrow the variable to that tag first and the
            // check would pass silently (#61).
            let resolvedScrutineeTypes = scrutineeTypes |> List.map (resolveSoFar current >> Unifier.unalias)
            let mutable scrutineeOverrides: Map<int, Ty> = Map.empty
            for (index, scrutineeTy) in scrutineeTypes |> List.indexed do
                let patternsAtPos = patternsForIndex index
                let hasCatchAll = patternsAtPos |> List.exists isCatchAllPattern
                let nonCatchAll = patternsAtPos |> List.filter (fun pat -> not (isCatchAllPattern pat))
                let allTagPatterns =
                    not nonCatchAll.IsEmpty
                    && (nonCatchAll |> List.forall (function Kind (TagPattern _) -> true | _ -> false))
                if allTagPatterns then
                    let tagInfos =
                        nonCatchAll
                        |> List.choose (function Kind (TagPattern(tagName, payloadOpt)) -> Some (tagName, payloadOpt) | _ -> None)
                        |> List.groupBy fst
                        |> List.sortBy fst
                    // The arms describe the scrutinee's union. With a catch-all arm they need not name every
                    // tag, so the union is left open with a rest variable for the tags the arms do not name
                    // (`if #some(v) = get(n) then ...` must accept a `#some(int) | #none`).
                    if tagInfos.Length > 1 || hasCatchAll then
                        let mutable stateForUnion = current
                        let unionItems =
                            tagInfos
                            |> List.map (fun (tagName, entries) ->
                                let hasPayload = entries |> List.exists (fun (_, payloadOpt) -> payloadOpt.IsSome)
                                if hasPayload then
                                    let payloadTy, nextState = freshVar stateForUnion
                                    stateForUnion <- nextState
                                    TyTag(tagName, Some payloadTy)
                                else
                                    TyTag(tagName, None))
                        let unionItems =
                            if hasCatchAll then
                                let restTy, nextState = freshVar stateForUnion
                                stateForUnion <- nextState
                                unionItems @ [ restTy ]
                            else
                                unionItems
                        let unionTy = TyUnion unionItems
                        scrutineeOverrides <- scrutineeOverrides |> Map.add index unionTy
                        current <- addConstraint scrutineeTy unionTy stateForUnion
            // A match on a value the compiler made up is reported at the whole match.
            let scrutineeSpan index =
                match List.tryItem index scrutinees with
                | Some scrutinee when not (SourceSpan.isNone scrutinee.Span) -> scrutinee.Span
                | _ -> expr.Span
            let exhaustivenessDiagnostics () =
                resolvedScrutineeTypes
                |> List.mapi (fun index scrutineeTy ->
                    let patternsAtPos = patternsForIndex index
                    let hasCatchAll = patternsAtPos |> List.exists isCatchAllPattern
                    if hasCatchAll then
                        None
                    else
                        match tryGetTagUnion scrutineeTy with
                        | Some tagUnion ->
                            let coveredTags =
                                patternsAtPos
                                |> List.choose tryGetTagName
                                |> Set.ofList
                            let missing = Set.difference tagUnion coveredTags |> Set.toList
                            if missing.IsEmpty then
                                None
                            else
                                let missingText = missing |> String.concat ", "
                                let suffix = if scrutineeTypes.Length > 1 then $" (scrutinee {index + 1})" else ""
                                Some (Diagnostics.at DiagnosticCodes.NonExhaustiveMatch (scrutineeSpan index) $"Non-exhaustive match{suffix}, missing tags: {missingText}")
                        | None ->
                            if isListType scrutineeTy then
                                if listPatternsCoverAll patternsAtPos then
                                    None
                                else
                                    let suffix = if scrutineeTypes.Length > 1 then $" (scrutinee {index + 1})" else ""
                                    Some (Diagnostics.at DiagnosticCodes.NonExhaustiveMatch (scrutineeSpan index) $"Non-exhaustive match{suffix}, missing list cases")
                            elif isRecordType scrutineeTy then
                                let suffix = if scrutineeTypes.Length > 1 then $" (scrutinee {index + 1})" else ""
                                Some (Diagnostics.at DiagnosticCodes.NonExhaustiveMatch (scrutineeSpan index) $"Non-exhaustive match{suffix}, missing record cases")
                            else
                                None)
                |> List.choose id
            // A tag that an earlier arm handles in full (its payload and every other position are catch-alls)
            // cannot reach a later arm, so a name bound by a catch-all in a later arm has the scrutinee's union
            // without those tags; with only the rest left, it has the rest's type. That is what lets
            // `| #some(a), f -> #some(f(a)) | other, _ -> other` be `(#some(a) | r), (a -> b) -> (#some(b) | r)`.
            let tagsHandledBefore armIndex index =
                arms
                |> List.take armIndex
                |> List.choose (fun (patterns, _) ->
                    let othersCatchAll =
                        patterns |> List.indexed |> List.forall (fun (i, pat) -> i = index || isCatchAllPattern pat)
                    match List.tryItem index patterns with
                    | Some (Kind (TagPattern(tagName, payloadOpt))) when othersCatchAll && payloadOpt |> Option.forall isCatchAllPattern ->
                        Some tagName
                    | _ -> None)
                |> Set.ofList
            let restRowTy index =
                match scrutineeOverrides |> Map.tryFind index with
                | Some (TyUnion items) -> items |> List.tryPick (function TyVar _ as rest -> Some rest | _ -> None)
                | _ -> None
            let catchAllBindingTy armIndex index =
                match scrutineeOverrides |> Map.tryFind index, restRowTy index with
                | Some (TyUnion items), Some _ ->
                    let handled = tagsHandledBefore armIndex index
                    if handled.IsEmpty then
                        None
                    else
                        match items |> List.filter (function TyTag(name, _) -> not (handled.Contains name) | _ -> true) with
                        | [ single ] -> Some single
                        | remaining -> Some (TyUnion remaining)
                | _ -> None
            let rowVars =
                scrutineeTypes
                |> List.indexed
                |> List.choose (fun (index, _) -> restRowTy index)
                |> List.choose (function TyVar v -> Some v.Id | _ -> None)
                |> Set.ofList
            for (armIndex, (patterns, expr)) in List.indexed arms do
                if errors.IsEmpty then
                    let mutable env' = env
                    let mutable state' = current
                    let effectiveScrutineeTypes =
                        scrutineeTypes
                        |> List.mapi (fun idx ty ->
                            match List.tryItem idx patterns with
                            | Some (Kind (IdentifierPattern _)) when (catchAllBindingTy armIndex idx).IsSome ->
                                (catchAllBindingTy armIndex idx).Value
                            | _ -> scrutineeOverrides |> Map.tryFind idx |> Option.defaultValue ty)
                    let patternPairs = List.zip patterns effectiveScrutineeTypes
                    let mutable typedPatterns: TypedPattern list = []
                    for (pattern, scrutineeTy) in patternPairs do
                        match inferPattern expr env' state' scrutineeTy pattern with
                        | Ok (nextEnv, nextState, typedPattern) ->
                            env' <- nextEnv
                            state' <- nextState
                            typedPatterns <- typedPatterns @ [typedPattern]
                        | Error err -> errors <- err
                    if errors.IsEmpty then
                        match inferExpr env' state' expr with
                        | Ok (typedExpr, next) ->
                            current <- next
                            armExprs <- typedExpr :: armExprs
                            typedArms <- typedArms @ [ (typedPatterns, typedExpr) ]
                        | Error err -> errors <- err
            if errors.IsEmpty then
                let hasRestUnion =
                    resolvedScrutineeTypes
                    |> List.exists (function
                        | TyUnion items -> items |> List.exists (function TyVar _ -> true | _ -> false)
                        | _ -> false)
                let restError =
                    if hasRestUnion then
                        let hasCatchAll =
                            arms
                            |> List.exists (fun (patterns, _) ->
                                patterns
                                |> List.exists (function
                                    | Kind (WildcardPattern | ElsePattern | IdentifierPattern _) -> true
                                    | _ -> false))
                        if hasCatchAll then
                            None
                        else
                            Some (Diagnostics.at DiagnosticCodes.MissingCatchAllArm expr.Span "Tag union rest parameters require a catch-all match arm")
                    else
                        None
                let exhaustivenessErrors =
                    if restError.IsSome then
                        []
                    else
                        exhaustivenessDiagnostics ()
                match restError with
                | Some diag -> Error [ diag ]
                | None ->
                    if not exhaustivenessErrors.IsEmpty then
                        Error exhaustivenessErrors
                    else
                    let armTypes = armExprs |> List.rev |> List.map (fun typedExpr -> typedExpr.Type)
                    let resultTy, constrained = joinBranchTypes rowVars current armTypes
                    Ok (mkTypedExpr expr resultTy None None (Some typedArms), constrained)
            else
                Error errors
    | MemberAccess(baseExpr, memberName, spanOpt) ->
        let memberSpan = nameSpan spanOpt expr
        match baseExpr.Kind with
        | IdentifierExpr baseName ->
            let qualifiedName = $"{baseName}.{memberName}"
            match TypeEnv.tryFind qualifiedName env with
            | Some scheme when not (referenceErrors qualifiedName expr scheme state).IsEmpty -> Error (referenceErrors qualifiedName expr scheme state)
            | Some scheme ->
                let ty, next = instantiate (recordReference expr scheme state) scheme
                Ok (mkTypedExpr expr ty None None None, next)
            | None -> readMember env state expr baseExpr (Some baseName) memberName memberSpan
        | _ -> readMember env state expr baseExpr None memberName memberSpan
    | ContextMemberAccess(ctxType, memberName) ->
        // TODO: Look up the context type and find the member's type
        // For now, return a fresh type variable
        let ty, next = freshVar (recordContextMemberReference expr ctxType memberName state)
        Ok (mkTypedExpr expr ty None None None, next)
    | ContextMemberCall(ctxType, memberName, args) ->
        // TODO: Look up the context, find the member function type, check args
        // For now, infer args and return a fresh type variable
        let mutable current = recordContextMemberReference expr ctxType memberName state
        let mutable errors: Diagnostic list = []
        for arg in args do
            match inferExpr env current arg with
            | Ok (_, next) -> current <- next
            | Error err -> errors <- errors @ err
        if errors.IsEmpty then
            let ty, next = freshVar current
            Ok (mkTypedExpr expr ty None None None, next)
        else
            Error errors

/// `baseExpr.memberName`, where `expr` is that member access and `receiver` the base as written if it is a name: a
/// record's field, or else a built-in property of the base's type as far as the code before it settles that type,
/// as for a pipe. A value of no known type yet is taken to be a record with that field (see `fieldTySoFar`), so
/// `{ s -> s.length }` reads a field, not a string's length.
and private readMember (env: TypeEnv) (state: InferState) (expr: Expression) (baseExpr: Expression) (receiver: string option) (memberName: string) (memberSpan: SourceSpan) =
    match inferExpr env state baseExpr with
    | Error err -> Error err
    | Ok (typedBase, next) ->
        match fieldTySoFar next typedBase.Type memberName with
        | Some (fieldTy, next) -> Ok (mkTypedExpr expr fieldTy None None None, next)
        | None ->
            let baseTy = resolveSoFar next typedBase.Type
            match builtInProperty baseTy memberName with
            | Some (property, propertyTy) ->
                Ok (mkTypedExpr expr propertyTy None None None, { next with BuiltInProperties = next.BuiltInProperties |> Map.add expr.Id property })
            | None -> Error [ unknownMember env memberSpan receiver baseTy memberName ]

and inferUseBinding (env: TypeEnv) (state: InferState) (binding: UseBinding) : Result<TypedExpr * InferState, Diagnostic list> =
    match binding with
    | UseValue expr -> inferExpr env state expr
    | UseContextInstance(ctxType, expr) -> 
        // TODO: Validate that expr satisfies the context type
        inferExpr env state expr

/// Infers the value named by each `typeof` in `typeExpr`, so that reading the annotation finds their types.
and private inferTypeOfValues (env: TypeEnv) (state: InferState) (typeExpr: TypeExpr) : Result<InferState, Diagnostic list> =
    typeOfValues typeExpr
    |> List.fold (fun result value -> result |> Result.bind (fun current -> inferExpr env current value |> Result.map snd)) (Ok state)

and inferLambdaWithExpected (env: TypeEnv) (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (body: Expression) (expectedInputOpt: Ty option) (expectedReturnOpt: Ty option) : Result<TypedExpr * InferState, Diagnostic list> =
    // The parameters' annotations are read only when nothing else gives their types, but each must name types.
    let annotated =
        match ensureTypesExist state (args |> List.choose snd), expectedInputOpt with
        | Error errors, _ -> Error errors
        | Ok (), Some _ -> Ok state
        | Ok (), None ->
            args
            |> List.choose snd
            |> List.fold (fun result typeExpr -> result |> Result.bind (fun current -> inferTypeOfValues env current typeExpr)) (Ok state)
    annotated |> Result.bind (fun state -> inferLambdaAfterTypeOfs env state lambda args body expectedInputOpt expectedReturnOpt)

and private inferLambdaAfterTypeOfs (env: TypeEnv) (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (body: Expression) (expectedInputOpt: Ty option) (expectedReturnOpt: Ty option) : Result<TypedExpr * InferState, Diagnostic list> =
    let mutable current = state
    let mutable env' = env
    let argTys =
        match expectedInputOpt with
        | Some expectedInput ->
            let tys, next = argTypesFromExpected current args expectedInput
            current <- next
            tys
        | None ->
            args
            |> List.map (fun (_, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some typeExpr -> typeExprToTy current typeExpr
                    | None -> freshVar current
                current <- next
                ty)
    for ((name, typeOpt), ty) in List.zip args argTys do
        let symbol, next = declareSymbol LambdaParameters lambda.Id name Parameter false (Some lambda.Span.End) current
        current <- next |> recordSymbolType symbol ty |> recordAnnotated symbol typeOpt.IsSome
        env' <- TypeEnv.declare name symbol (TypeEnv.mono ty) env'
    match inferExpr env' current body with
    | Ok (bodyExpr, next) ->
        let withReturnConstraint =
            match expectedReturnOpt with
            | Some expectedReturn ->
                match Unifier.unalias expectedReturn with
                | TyNominal(name, underlying, _) when state.LocalNominals.Contains name ->
                    match Unifier.unalias bodyExpr.Type with
                    | TyNominal _ -> addAssignableConstraintAt body.Span bodyExpr.Type expectedReturn next
                    | _ -> addAssignableConstraintAt body.Span bodyExpr.Type underlying next
                | _ -> addAssignableConstraintAt body.Span bodyExpr.Type expectedReturn next
            | None -> next
        let inputTy =
            match expectedInputOpt with
            | Some expectedInput -> expectedInput
            | None -> inputTypeFromArgs argTys
        Ok (mkTypedExpr lambda (TyFunc(inputTy, bodyExpr.Type)) (Some bodyExpr) None None, withReturnConstraint)
        // Called directly for a lambda whose input is known, not only through `inferExpr`.
        |> recordExprType
    | Error err -> Error err

and inferBlock (env: TypeEnv) (state: InferState) (block: Expression) (statements: Statement list) : Result<TypedExpr * InferState, Diagnostic list> =
    match statements with
    | [] -> Ok (mkTypedExpr block (TyPrimitive "unit") None (Some []) None, state)
    | _ ->
        let mutable current = state
        let mutable env' = env
        let mutable lastExpr = mkTypedExpr (Expression.synthetic (LiteralExpr (IntLit 0))) (TyPrimitive "unit") None None None
        let mutable typedStatements: TypedStatement list = []
        let mutable errors: Diagnostic list = []
        for statement in statements do
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, expr) ->
                if errors.IsEmpty then
                    let contextsExist =
                        match fst (extractContextFromTypeExpr typeOpt) with
                        | Some contexts -> ensureContextsExist current name (declarationSpan current DefValue expr) contexts
                        | None -> Ok ()
                    let typeOfsInferred =
                        match typeOpt with
                        | Some typeExpr -> inferTypeOfValues env' current typeExpr
                        | None -> Ok current
                    match ensureAttachedTypeExists current None name (declarationSpan current DefValue expr) |> Result.bind (fun () -> ensureTypesExist current (Option.toList typeOpt)) |> Result.bind (fun () -> contextsExist) |> Result.bind (fun () -> typeOfsInferred) with
                    | Error err -> errors <- err
                    | Ok inferredState ->
                        current <- inferredState
                        let contextOpt, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
                        let envWithCtx, nextState =
                            match contextOpt with
                            | Some contexts -> extendEnvWithContexts env' current contexts
                            | None -> env', current
                        let expectedInputOpt, expectedReturnOpt, expectedDeclaredOpt, nextState2 =
                            match declaredTypeExprOpt with
                            | Some declaredExpr ->
                                let ty, st = typeExprToTy nextState declaredExpr
                                match Unifier.unalias ty with
                                | TyFunc(inputTy, returnTy) -> Some inputTy, Some returnTy, Some ty, st
                                | _ -> None, None, Some ty, st
                            | None -> None, None, None, nextState
                        let effectiveExpectedInputOpt =
                            match expectedInputOpt with
                            | Some _ -> expectedInputOpt
                            | None when expectedDeclaredOpt.IsNone -> tryInferAttachedLambdaInput nextState2 name expr
                            | None -> None
                        let symbol, declaredState = declareSymbol DefValue expr.Id name LocalValue false (Some block.Span.End) nextState2
                        let preboundTy, preboundState, envWithRec, preboundFresh =
                            match expectedDeclaredOpt with
                            | Some declaredTy -> declaredTy, declaredState, TypeEnv.declare name symbol (TypeEnv.mono declaredTy) envWithCtx, false
                            | None ->
                                let ty, st = freshVar declaredState
                                ty, st, TypeEnv.declare name symbol (TypeEnv.mono ty) envWithCtx, true
                        let nextStateWithReq =
                            match contextOpt with
                            | Some contexts -> registerContextRequirement preboundState name contexts
                            | None -> preboundState
                        // A local function that writes its type runs in the contexts the type declares; one that does
                        // not runs in those of the def around it.
                        let outerScope = nextStateWithReq.ContextScope
                        let bodyState =
                            match typeOpt, expr.Kind with
                            | Some _, Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, false) }
                            | _ -> nextStateWithReq
                        let inferResult =
                            match expr.Kind with
                            | Lambda(args, body) when effectiveExpectedInputOpt.IsSome || expectedReturnOpt.IsSome ->
                                inferLambdaWithExpected envWithRec bodyState expr args body effectiveExpectedInputOpt expectedReturnOpt
                            | _ -> inferExpr envWithRec bodyState expr
                            |> Result.map (fun (typedExpr, next) -> typedExpr, { next with ContextScope = outerScope })
                        match inferResult with
                        | Ok (typedExpr, next) ->
                            let declaredTy, finalState =
                                match expectedDeclaredOpt with
                                | Some declaredTy ->
                                    match expr.Kind with
                                    | Lambda _ -> declaredTy, next
                                    | _ ->
                                        let updatedState =
                                            match Unifier.unalias declaredTy with
                                            | TyNominal(name, underlying, _) when current.LocalNominals.Contains name ->
                                                match Unifier.unalias typedExpr.Type with
                                                | TyNominal _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                                | _ -> addAssignableConstraintAt expr.Span typedExpr.Type underlying next
                                            | _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                        declaredTy, updatedState
                                | None ->
                                    let updatedState =
                                        if preboundFresh then addConstraintAt expr.Span preboundTy typedExpr.Type next else next
                                    typedExpr.Type, updatedState
                            current <-
                                finalState
                                |> recordSymbolType symbol declaredTy
                                |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                            let scheme, generalizedState = generalizeDef env' current declaredTy
                            current <- generalizedState
                            env' <- TypeEnv.declare name symbol scheme env'
                            typedStatements <- typedStatements @ [ TypedDefStatement(isExport, name, typeOpt, typedExpr) ]
                            lastExpr <- typedExpr
                        | Error err -> errors <- err
            | ImportStatement items ->
                typedStatements <- typedStatements @ [ TypedImportStatement items ]
            | TypeDefStatement(name, modifiers, parameters, body) ->
                if errors.IsEmpty then errors <- typeOfInTypeDef name body parameters
                current <- registerTypeDef current name modifiers parameters body
                // Checked once the type is registered, so it can name itself.
                if errors.IsEmpty then errors <- unknownTypesInTypeDef current body parameters
                typedStatements <- typedStatements @ [ TypedTypeDefStatement(name, modifiers, parameters, body) ]
            | UseStatement binding ->
                if errors.IsEmpty then
                    match inferUseBinding env' current binding with
                    | Ok (typedExpr, next) ->
                        current <- next
                        env' <- extendEnvWithRecordFields env' typedExpr.Type
                        typedStatements <- typedStatements @ [ TypedUseStatement(binding, Some typedExpr) ]
                    | Error err -> errors <- err
            | ExprStatement expr ->
                if errors.IsEmpty then
                    match inferExpr env' current expr with
                    | Ok (typedExpr, next) ->
                        current <- next
                        typedStatements <- typedStatements @ [ TypedExprStatement typedExpr ]
                        lastExpr <- typedExpr
                    | Error err -> errors <- err
        if errors.IsEmpty then
            let blockExpr = mkTypedExpr block lastExpr.Type (Some lastExpr) (Some typedStatements) None
            Ok (blockExpr, current)
        else
            Error errors

/// The names of the type variables written in an annotation, in order of first appearance.
let rec private annotationVarNames (typeExpr: TypeExpr) : string list =
    let ofList items = items |> List.collect annotationVarNames
    let names =
        match typeExpr.Kind with
        | TypeVar name -> [ name ]
        | TypeName _ | TypeUnit | TypeContext _ -> []
        | TypeTuple items | TypeUnion items | TypeIntersection items -> ofList items
        | TypeRecord fields ->
            fields
            |> List.collect (function
                | TypeField(_, _, _, Some t, _) | TypeMember(_, Some t) -> annotationVarNames t
                | _ -> [])
        | TypeApply(_, args) -> ofList args
        | TypeFunc(arg, ret) -> annotationVarNames arg @ annotationVarNames ret
        | TypeTag(_, payload) -> payload |> Option.map annotationVarNames |> Option.defaultValue []
        | TypeOptional inner | TypeWithContext(_, inner) | TypeConstraint(_, inner, _) | TypeWhere(inner, _) ->
            annotationVarNames inner
        // The type of a value is not a variable the annotation introduces.
        | TypeOf _ -> []
    names |> List.distinct

/// The type variables of a def's annotation are rigid: the body has to work for every type they stand for.
/// Once the constraints gathered so far are solved, each must still be a variable of its own. One bound to
/// a concrete type, or to the same type as another of the annotation's variables, means the annotation is
/// more general than the body, which would otherwise quietly narrow it.
let private checkAnnotationIsRigid (state: InferState) (name: Identifier) (span: SourceSpan) (typeExpr: TypeExpr) : Result<unit, Diagnostic list> =
    let vars =
        annotationVarNames typeExpr
        |> List.choose (fun varName -> state.TypeVars |> Map.tryFind varName |> Option.map (fun var -> varName, var))
    if vars.IsEmpty then
        Ok ()
    else
        match Unifier.unify state.Constraints with
        // A conflict is reported when the module's constraints are solved.
        | Error _ -> Ok ()
        | Ok subst ->
            let resolved = vars |> List.map (fun (varName, var) -> varName, Unifier.apply subst (TyVar var))
            // Show the variables an annotation variable resolved to by that annotation variable's name.
            let displayNames =
                resolved
                |> List.fold (fun acc (varName, ty) ->
                    match ty with
                    | TyVar image when not (acc |> Map.containsKey image.Id) -> acc |> Map.add image.Id (TyPrimitive varName)
                    | _ -> acc) Map.empty
            let problems =
                resolved
                |> List.mapi (fun index (varName, ty) ->
                    match ty with
                    | TyVar _ ->
                        resolved
                        |> List.take index
                        |> List.tryFind (fun (_, otherTy) -> otherTy = ty)
                        |> Option.map (fun (otherName, _) -> $"`{varName}` would have to be `{otherName}`")
                    | concrete -> Some $"`{varName}` would have to be `{Unifier.tyToString (Unifier.apply displayNames concrete)}`")
                |> List.choose id
            if problems.IsEmpty then
                Ok ()
            else
                let details = String.concat "; " problems
                Error [ Diagnostics.at DiagnosticCodes.AnnotationTooGeneral span $"Annotation of '{name}' is more general than the inferred type: {details}" ]

let inferModuleWithEnv (initialEnv: TypeEnv) (initialState: InferState) (module': Module) : Result<Map<string, Ty> * TypedTopLevelItem list * InferState, Diagnostic list> =
    let mutable env = initialEnv
    let mutable state = initialState
    let mutable currentModule: string option = None
    let mutable types = Map.empty
    let mutable items: TypedTopLevelItem list = []
    let mutable errors: Diagnostic list = []

    // An imported context whose every member is also an imported definition of that name is an ambient instance:
    // the one the runtime supplies, as `nanyx/console`'s definitions are its `Console`. The top level of the module
    // runs in it; a def runs in it only if it declares the context, or does not write its type and so infers it.
    state <-
        { state with
            AmbientMembers =
                state.TypeDefs
                |> Map.toList
                |> List.collect (fun (ctxName, info) ->
                    // An aliased import's context (`c.Console`) has its members under the alias too (`c.println`).
                    let qualify memberName =
                        if ctxName.Contains '.' then $"{ctxName.Substring(0, ctxName.LastIndexOf '.')}.{memberName}" else memberName
                    let bindings =
                        info.Members
                        |> Map.toList
                        |> List.map (fun (memberName, memberSymbol) ->
                            let memberName = qualify memberName
                            memberName,
                            TypeEnv.tryFind memberName env
                            |> Option.bind (fun scheme -> scheme.Symbol)
                            |> Option.filter (fun symbol -> symbol <> memberSymbol))
                    if bindings.IsEmpty || not info.Parameters.IsEmpty || bindings |> List.exists (snd >> Option.isNone) then []
                    else bindings |> List.map (fun (memberName, symbol) -> memberName, (symbol.Value, ctxName)))
                |> Map.ofList }

    // Pre-bind every top-level value before inferring any body, so a definition can refer to one
    // declared later in the module (forward references, mutual recursion). An annotated def is bound
    // at its declared type; an unannotated one at a fresh variable that its inferred type is unified
    // with once its body has been checked. Type definitions are registered in the same pass, so an
    // annotation can name a type declared further down.
    let mutable preboundFreshVars: Map<string, Ty> = Map.empty
    for item in module' do
        match item with
        | Kind (Def (TypeDef(name, modifiers, parameters, body))) ->
            state <- registerTypeDef state name modifiers parameters body
        | Kind (Def (ValueDef(isExport, name, typeOpt, value))) ->
            let symbol, declared = declareSymbol DefValue value.Id name TopLevelValue isExport None state
            // The module's own def wins over an imported one of the same name, and requires only its own contexts.
            state <- { declared with ContextRequirements = declared.ContextRequirements |> Map.remove name }
            match typeOpt, value.Kind, symbol with
            | None, Lambda _, Some symbol -> state <- { state with UnannotatedDefs = state.UnannotatedDefs |> Map.add name symbol }
            | _ -> ()
            let _, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
            match declaredTypeExprOpt with
            // An annotation with a `typeof` can only be read once the values it names are in scope, so the def is
            // pre-bound as an unannotated one is, and its annotation is unified with that once it has been read.
            | Some declaredExpr when (typeOfValues declaredExpr).IsEmpty ->
                let ty, st = typeExprToTy state declaredExpr
                state <- st
                env <- TypeEnv.declare name symbol (TypeEnv.generalize env ty) env
            | _ ->
                let ty, st = freshVar state
                state <- st
                preboundFreshVars <- preboundFreshVars |> Map.add name ty
                env <- TypeEnv.declare name symbol (TypeEnv.mono ty) env
        | _ -> ()

    for item in module' do
        match item.Kind with
        | Def (ValueDef(isExport, name, typeOpt, expr)) when errors.IsEmpty ->
            let contextsExist =
                match fst (extractContextFromTypeExpr typeOpt) with
                | Some contexts -> ensureContextsExist state name (declarationSpan state DefValue expr) contexts
                | None -> Ok ()
            // A def's own name is not in scope in its annotation: `def x: typeof x` says nothing.
            let typeOfsInferred =
                match typeOpt with
                | Some typeExpr -> inferTypeOfValues (env |> Map.remove name) state typeExpr
                | None -> Ok state
            match ensureAttachedTypeExists state currentModule name (declarationSpan state DefValue expr) |> Result.bind (fun () -> ensureTypesExist state (Option.toList typeOpt)) |> Result.bind (fun () -> contextsExist) |> Result.bind (fun () -> typeOfsInferred) with
            | Error err -> errors <- err
            | Ok inferredState ->
                state <- inferredState
                let contextOpt, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
                let envWithCtx, nextState =
                    match contextOpt with
                    | Some contexts -> extendEnvWithContexts env state contexts
                    | None -> env, state
                let expectedInputOpt, expectedReturnOpt, expectedDeclaredOpt, nextState2 =
                    match declaredTypeExprOpt with
                    | Some declaredExpr ->
                        let ty, st = typeExprToTy nextState declaredExpr
                        match Unifier.unalias ty with
                        | TyFunc(inputTy, returnTy) -> Some inputTy, Some returnTy, Some ty, st
                        | _ -> None, None, Some ty, st
                    | None -> None, None, None, nextState
                let effectiveExpectedInputOpt =
                    match expectedInputOpt with
                    | Some _ -> expectedInputOpt
                    | None when expectedDeclaredOpt.IsNone -> tryInferAttachedLambdaInput nextState2 name expr
                    | None -> None
                // The name is already in `env` from the pre-binding pass: at its declared type, or at the
                // fresh variable recorded in `preboundFreshVars`. Self-recursion sees the declared type as a
                // monomorphic binding while the body is checked, as before.
                // The symbol the pre-binding pass made.
                let symbol, _ = declareSymbol DefValue expr.Id name TopLevelValue isExport None state
                let envWithRec =
                    match expectedDeclaredOpt with
                    | Some declaredTy -> TypeEnv.declare name symbol (TypeEnv.mono declaredTy) envWithCtx
                    | None -> envWithCtx
                let nextStateWithReq =
                    match contextOpt with
                    | Some contexts -> registerContextRequirement nextState2 name contexts
                    | None -> nextState2
                // A function runs in the contexts its type declares, or infers them. A value is computed at the top
                // level, in the ambient ones.
                let bodyState =
                    match expr.Kind with
                    | Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, typeOpt.IsNone) }
                    | _ -> nextStateWithReq
                let inferResult =
                    match expr.Kind with
                    | Lambda(args, body) when effectiveExpectedInputOpt.IsSome || expectedReturnOpt.IsSome ->
                        inferLambdaWithExpected envWithRec bodyState expr args body effectiveExpectedInputOpt expectedReturnOpt
                    | _ -> inferExpr envWithRec bodyState expr
                    |> Result.map (fun (typedExpr, next) -> typedExpr, { next with ContextScope = AmbientScope })
                match inferResult with
                | Ok (typedExpr, next) ->
                    let declaredTy, finalState =
                        match expectedDeclaredOpt with
                        | Some declaredTy ->
                            match expr.Kind with
                            | Lambda _ -> declaredTy, next
                            | _ ->
                                let updatedState =
                                    match Unifier.unalias declaredTy with
                                    | TyNominal(name, underlying, _) when state.LocalNominals.Contains name ->
                                        match Unifier.unalias typedExpr.Type with
                                        | TyNominal _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                        | _ -> addAssignableConstraintAt expr.Span typedExpr.Type underlying next
                                    | _ -> addAssignableConstraintAt expr.Span typedExpr.Type declaredTy next
                                declaredTy, updatedState
                        | None ->
                            let updatedState =
                                match preboundFreshVars |> Map.tryFind name with
                                | Some preboundTy -> addConstraintAt expr.Span preboundTy typedExpr.Type next
                                | None -> next
                            typedExpr.Type, updatedState
                    // An annotated def pre-bound at a fresh variable (one with a `typeof`) is that annotation.
                    let finalState =
                        match expectedDeclaredOpt, preboundFreshVars |> Map.tryFind name with
                        | Some _, Some preboundTy -> addConstraintAt expr.Span preboundTy declaredTy finalState
                        | _ -> finalState
                    state <-
                        finalState
                        |> recordSymbolType symbol declaredTy
                        |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                    let rigidResult =
                        match declaredTypeExprOpt with
                        | Some declaredExpr -> checkAnnotationIsRigid finalState name (declarationSpan finalState DefValue expr) declaredExpr
                        | None -> Ok ()
                    match rigidResult with
                    | Error err -> errors <- err
                    | Ok () ->
                        // Generalize against an env without this def's own pre-binding, so the fresh
                        // variable it was pre-bound at does not stop its type being generalized. It is resolved
                        // first, as a local def's is: `{ x -> x + 1 }` is `α -> α` until its constraints are
                        // applied, and generalising that would make every use `α -> α` too.
                        let scheme, generalizedState = generalizeDef (env |> Map.remove name) state declaredTy
                        state <- generalizedState
                        env <- TypeEnv.declare name symbol scheme env
                        types <- types |> Map.add name declaredTy
                        items <- items @ [ TypedDef (TypedValueDef(isExport, name, typeOpt, typedExpr)) ]
                | Error err -> errors <- err
        | Expr expr when errors.IsEmpty ->
            match inferExpr env state expr with
            | Ok (typedExpr, next) ->
                state <- next
                items <- items @ [ TypedExprItem typedExpr ]
            | Error err -> errors <- err
        | Expr _ -> ()
        | Import itemsList -> items <- items @ [ TypedImport itemsList ]
        | ModuleDecl name ->
            currentModule <- Some name
            items <- items @ [ TypedModuleDecl name ]
        | ModuleBlock _ -> ()
        // Attributes are resolved by the compiler front end before inference (see Compiler.applyAttributes).
        | Attribute _ -> ()
        | Def (TypeDef(name, modifiers, parameters, body)) ->
            // Registered in the pre-binding pass above.
            if errors.IsEmpty then errors <- typeOfInTypeDef name body parameters
            if errors.IsEmpty then errors <- unknownTypesInTypeDef state body parameters
            items <- items @ [ TypedDef (TypedTypeDef(name, modifiers, parameters, body)) ]
        | Def (ValueDef _) -> ()

    if errors.IsEmpty then
        // The ambient contexts each unannotated function needs: those its body uses, and those the unannotated
        // functions it uses need that nothing nearer supplies, until no more are found.
        let uses = List.rev state.DeferredContextUses
        let mutable required =
            state.UnannotatedDefs |> Map.map (fun name _ -> state.InferredContexts |> Map.tryFind name |> Option.defaultValue Set.empty)
        let mutable changed = true
        while changed do
            changed <- false
            for used in uses do
                match used.Caller with
                | DefScope(caller, true) when required.ContainsKey caller ->
                    let needed = Set.difference required.[used.Callee] used.Available
                    if not (Set.isSubset needed required.[caller]) then
                        required <- required |> Map.add caller (Set.union required.[caller] needed)
                        changed <- true
                | _ -> ()
        for used in uses do
            match used.Caller with
            | DefScope(caller, false) ->
                for ctxName in Set.difference required.[used.Callee] used.Available do
                    errors <- errors @ [ undeclaredContext caller ctxName $"'{used.Callee}' requires" used.Span ]
            | _ -> ()
        // What an unannotated function needs is part of its type, as a context written in its type is.
        state <-
            { state with
                ContextRequirements =
                    required
                    |> Map.filter (fun _ contexts -> not contexts.IsEmpty)
                    |> Map.fold (fun acc name contexts ->
                        acc |> Map.add name (contexts |> Set.toList |> List.map (TypeName >> TypeExpr.synthetic))) state.ContextRequirements }

    if errors.IsEmpty then Ok (types, items, state) else Error errors

let inferModule (module': Module) : Result<Map<string, Ty> * TypedTopLevelItem list * InferState, Diagnostic list> =
    inferModuleWithEnv TypeEnv.empty emptyState module'
