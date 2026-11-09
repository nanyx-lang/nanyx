/// Lowers the typed AST to the core IR (`CoreIr.fs`), specialising generic functions as it goes.
///
/// Each definition whose type is concrete is lowered as a root. A call to a generic definition asks for its
/// specialisation at the call's type arguments, which is lowered in turn (and may ask for more). A definition the IR
/// cannot lower yet is left to `CodeGen.fs`, which compiles it as it always has: see "Core IR" in
/// `compiler/design/memory.md` for this fallback.
module Transpiler.Wasm.CoreIrLower

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr

/// A function lambda lifting made (see `LambdaLifting.fs`), which the IR lowers where a closure over it is made.
type LiftedFunction =
    /// A lambda, or a `rec` local function, lifted out of a definition: the lambda as written (which the type checker
    /// typed), and the names it captures, in the order its environment holds them.
    | LiftedLambda of Expression * string list
    /// What lets the top-level function of this name be passed as a value: it takes an environment and ignores it.
    | FunctionWrapper of string

/// A member of a context a function requires, which it takes as a parameter of its own after its others.
type ContextParameter =
    { /// The member's key (see `Identifiers.contextMemberKey`): its name, or `<context key>_<name>` for a member of a
      /// parameterised context.
      Key: string
      /// The parameter's name in WebAssembly text.
      Name: string
      /// The member's type, in the function's own type variables; none when it cannot be worked out from the
      /// annotation.
      Type: Ty option }

/// Where a call finds a member of a context its callee requires.
type ContextArgument =
    /// The member in scope under this key: a parameter of the function, or a `use` binding.
    | MemberInScope of string
    /// A closure over this wrapper of a top-level function: a member of the ambient instance (the runtime's
    /// `Console`), or of an instance the compiler built from attached definitions.
    | ClosureOverWrapper of string
    /// The value of this top-level definition.
    | DefinitionValue of string

/// What the lowering reads from the typed module.
type LowerInput =
    { /// The module's top-level definitions as the backend compiles them, in order: a function is a lambda.
      Definitions: (string * Expression) list
      /// The type of each expression, and of the function at each call, by node (`TypedModule.ExprTypes` and
      /// `CalleeTypes`). Type variables in them are the generalised variables of the definition they are in.
      ExprTypes: Map<NodeId, Ty>
      CalleeTypes: Map<NodeId, Ty>
      /// Each definition's type, generalised (`TypedModule.Types`).
      DefinitionTypes: Map<string, Ty>
      /// The context members each definition that requires contexts takes, in order, and the other names it can call
      /// them by (a member of a parameterised context, by its own name, when no other context it requires has one of
      /// that name).
      ContextParameters: Map<string, ContextParameter list * Map<string, string>>
      /// Where a call to the definition of this name, at this node, finds each context member it requires, given the
      /// keys of the members in scope there; or why it cannot.
      ContextArguments: string -> NodeId -> Set<string> -> Result<ContextArgument list, string>
      /// The record types the module defines, whose names construct a record: `Person(name = "Ada", age = 36)`.
      RecordConstructors: Set<string>
      /// The nominal types whose shape is not a record (`type $Money = private int`). A value of one is its shape's
      /// value, so its constructor and the pattern that takes it apart are no-ops.
      NominalWrappers: Set<string>
      /// The functions lambda lifting made, by name.
      Lifted: Map<string, LiftedFunction>
      /// The module's top-level expressions, which its start function evaluates in order when the module is loaded.
      TopLevel: Expression list
      /// The types the module defines (records and nominal types, not contexts), by name: what an annotation that
      /// names one means.
      NamedTypes: Map<string, Ty> }

/// The name of the start function, which evaluates the module's top-level expressions.
let startFunctionName = "__nanyx_start"

/// A function the IR lowered, and what it was finished into (see `lowerModule`).
type Lowered<'T> =
    { /// The definitions lowered as they are, by name.
      Roots: Map<string, Function * 'T>
      /// The specialisations of each generic definition, by the definition's name, in the order they were asked for.
      Specialisations: Map<string, (Function * 'T) list>
      /// What could not be lowered, in the order it was met: the function (by its definition's name), why, and where.
      Failures: (string * string * SourceSpan option) list }

/// Something the IR cannot lower yet, and why.
exception CannotLower of string

let private cannot message = raise (CannotLower message)

/// A pattern the value it is matched against can never match.
exception private NeverMatches

/// A generic definition at some type arguments: what a specialisation is.
type private SpecKey = string * IrType list

/// How a function is lowered.
type private Lowering =
    /// As its definition says.
    | AsDefined
    /// As a lifted lambda whose closure captures values of these types.
    | LiftedCapturing of IrType list
    /// As the wrapper of a top-level function, which it calls at this type.
    | WrapperCalling of string * Ty
    /// As the start function, from the module's top-level expressions.
    | AsStart

/// The most specialisations one definition may have: a definition that calls itself at ever bigger types
/// (polymorphic recursion) would otherwise have no end of them.
let private maxSpecialisations = 32

let rec private freeVars (ty: Ty) : int list =
    match ty with
    | TyVar v -> [ v.Id ]
    | TyPrimitive _ -> []
    | TyNominal(_, underlying, _) -> freeVars underlying
    | TyFunc(input, output) -> freeVars input @ freeVars output
    | TyRecord fields -> fields |> Map.toList |> List.collect (snd >> freeVars)
    | TyOpenRecord(fields, rest) -> (fields |> Map.toList |> List.collect (snd >> freeVars)) @ [ rest.Id ]
    | TyTag(_, payload) -> payload |> Option.map freeVars |> Option.defaultValue []
    | TyApply(_, args) -> args |> List.collect freeVars
    | TyUnion items -> items |> List.collect freeVars
    | TyAlias(_, args, underlying) -> (args |> List.collect freeVars) @ freeVars underlying

let rec private substitute (subst: Map<int, Ty>) (ty: Ty) : Ty =
    let go = substitute subst
    match ty with
    | TyVar v -> subst |> Map.tryFind v.Id |> Option.defaultValue ty
    | TyPrimitive _ -> ty
    | TyNominal(name, underlying, isPrivate) -> TyNominal(name, go underlying, isPrivate)
    | TyFunc(input, output) -> TyFunc(go input, go output)
    | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> go))
    | TyOpenRecord(fields, rest) ->
        let fields = fields |> Map.map (fun _ -> go)
        // A record whose other fields are now known is closed.
        match subst |> Map.tryFind rest.Id |> Option.map Unifier.unalias with
        | Some (TyRecord others) -> TyRecord(Map.fold (fun acc name fieldTy -> acc |> Map.add name fieldTy) others fields)
        | Some (TyOpenRecord(others, rest')) -> TyOpenRecord(Map.fold (fun acc name fieldTy -> acc |> Map.add name fieldTy) others fields, rest')
        | _ -> TyOpenRecord(fields, rest)
    | TyTag(name, payload) -> TyTag(name, payload |> Option.map go)
    | TyApply(name, args) -> TyApply(name, args |> List.map go)
    | TyUnion items -> TyUnion(items |> List.map go)
    | TyAlias(name, args, underlying) -> TyAlias(name, args |> List.map go, go underlying)

/// Binds the type variables of `scheme` so that it is `instance`, adding to `bindings`.
let rec private bindScheme (bindings: Map<int, Ty>) (scheme: Ty) (instance: Ty) : Map<int, Ty> =
    match Unifier.unalias scheme, Unifier.unalias instance with
    | TyVar v, _ ->
        match bindings |> Map.tryFind v.Id with
        | Some bound when bound <> instance -> cannot "a type variable stands for two types in one call"
        | _ -> bindings |> Map.add v.Id instance
    | TyPrimitive a, TyPrimitive b when a = b -> bindings
    | TyFunc(input, output), TyFunc(input', output') -> bindScheme (bindScheme bindings input input') output output'
    | TyRecord fields, TyRecord fields' when Set.ofSeq fields.Keys = Set.ofSeq fields'.Keys ->
        fields |> Map.fold (fun acc key fieldTy -> bindScheme acc fieldTy fields'.[key]) bindings
    // A function that reads fields of any record that has them, used at a record: its other fields are the rest.
    | TyOpenRecord(fields, rest), TyRecord fields' when fields.Keys |> Seq.forall fields'.ContainsKey ->
        let bound = fields |> Map.fold (fun acc key fieldTy -> bindScheme acc fieldTy fields'.[key]) bindings
        let others = fields' |> Map.filter (fun key _ -> not (fields.ContainsKey key))
        bindScheme bound (TyVar rest) (TyRecord others)
    | TyTag(name, payload), TyTag(name', payload') when name = name' ->
        match payload, payload' with
        | Some p, Some p' -> bindScheme bindings p p'
        | None, None -> bindings
        | _ -> cannot "a tag's payload differs between a definition and its use"
    // A tag alone is a union of one.
    | TyUnion _, (TyTag _ as tag) -> bindScheme bindings scheme (TyUnion [ tag ])
    | (TyTag _ as tag), TyUnion [ _ ] -> bindScheme bindings (TyUnion [ tag ]) instance
    | TyUnion items, TyUnion items' ->
        // The tags of a union may come in another order: pair them by name. An open union's rest (a type variable among
        // its tags) is the tags of the instance that the union does not name.
        let tagName = function TyTag(name, _) -> Some name | _ -> None
        let named = items |> List.choose tagName |> Set.ofList
        let bound =
            items
            |> List.fold (fun acc item ->
                match item with
                | TyTag(name, _) ->
                    match items' |> List.tryFind (fun item' -> tagName item' = Some name) with
                    | Some item' -> bindScheme acc item item'
                    | None -> cannot $"the tag #{name} is not in the type it is used at"
                | _ -> acc) bindings
        let rest = items' |> List.filter (fun item' -> match tagName item' with Some name -> not (named.Contains name) | None -> true)
        match items |> List.filter (fun item -> (tagName item).IsNone) with
        | [] when rest.IsEmpty -> bound
        | [] -> cannot "a union used at a type with more tags than it has"
        | [ restVar ] -> bindScheme bound restVar (TyUnion rest)
        | _ -> cannot "a union with more than one rest"
    | TyApply(name, args), TyApply(name', args') when name = name' && args.Length = args'.Length ->
        List.fold2 bindScheme bindings args args'
    | TyNominal(name, underlying, _), TyNominal(name', underlying', _) when name = name' ->
        bindScheme bindings underlying underlying'
    | _ -> cannot "a generic definition's type does not fit how it is used"

/// The order a record's fields are stored in: by position for a tuple, by name for anything else.
let private storageOrder (names: string list) =
    let positions = names |> List.map (fun name -> match System.Int32.TryParse name with | true, index -> Some index | _ -> None)
    if positions |> List.forall Option.isSome then List.zip positions names |> List.sortBy fst |> List.map snd
    else names |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

/// `fields` (name and whatever goes with it) in the order they are stored.
let private sortFields (fields: (string * 'a) list) =
    let byName = Map.ofList fields
    storageOrder (List.map fst fields) |> List.map (fun name -> name, byName.[name])

/// The IR type of `ty`, once `subst` is applied. A type variable nothing constrains (the element of `[]`, the rest of
/// an open union that no use closes) has no value of its own at run time, since nothing can make or inspect one, so it
/// is unit, and the rest of a union adds no tags.
let rec private toIrType (ty: Ty) : IrType =
    match Unifier.unalias ty with
    | TyPrimitive "int" -> IrInt
    | TyPrimitive "bool" -> IrBool
    | TyPrimitive "unit" -> IrUnit
    | TyPrimitive "string" -> IrString
    | TyPrimitive "float" -> IrFloat
    | TyPrimitive "?error" -> IrError
    | TyTag _
    | TyUnion _ as tags ->
        let rec members ty =
            match Unifier.unalias ty with
            | TyTag(name, payload) -> [ name, payload |> Option.map toIrType ]
            | TyUnion items -> items |> List.collect members
            | TyVar _ -> []
            | _ -> cannot "a union of something other than tags"
        IrTags(members tags |> List.distinct |> List.sortBy fst)
    // A `Map`'s keys are in the order a record's fields are stored in.
    | TyRecord fields -> IrRecord(fields |> Map.toList |> sortFields |> List.map (fun (name, fieldTy) -> name, toIrType fieldTy))
    | TyNominal(_, underlying, _) -> toIrType underlying
    | TyApply("list", [ element ]) -> IrList(toIrType element)
    | TyVar _ -> IrUnit
    | TyPrimitive other -> cannot $"a value of type {other}"
    | TyFunc(input, output) -> IrFunction(toIrType input, toIrType output)
    | TyOpenRecord _ -> cannot "a record whose other fields are not known here"
    | TyApply(name, _) -> cannot $"a value of type {name}"
    | TyAlias _ -> cannot "an alias"

/// A WebAssembly name for `name` specialised at `typeArguments`, such as `id__int`.
let private specialisationName (name: string) (typeArguments: IrType list) =
    let rec mangle ty =
        match ty with
        | IrTags tags ->
            tags
            |> List.map (fun (tag, payload) ->
                match payload with
                | Some payloadTy -> $"{tag}_{mangle payloadTy}"
                | None -> tag)
            |> String.concat "_"
            |> sprintf "tags_%s"
        | IrRecord fields ->
            fields
            |> List.map (fun (field, fieldTy) -> $"{field}_{mangle fieldTy}")
            |> String.concat "_"
            |> sprintf "rec_%s"
        | IrList element -> $"list_{mangle element}"
        | IrFunction(input, output) -> $"fn_{mangle input}_to_{mangle output}"
        | _ -> IrType.show ty
    sanitizeIdentifier name + (typeArguments |> List.map (mangle >> sanitizeIdentifier >> sprintf "__%s") |> String.concat "")

/// Whether lowering `expr` stores to a local before its value is ready: a block with statements before its last
/// expression, a `match` (which holds its scrutinees in locals), an interpolated string (which holds its holes), and
/// so on. Such an operand is lowered whole into one temporary (see `Atoms`).
let rec private storesBeforeValue (expr: Expression) =
    match expr.Kind with
    | Block [ Kind (ExprStatement last) ] -> storesBeforeValue last
    | Block _
    | ExpressionKind.Match _ -> true
    | IfExpr(cond, _, _) -> storesBeforeValue cond
    | BinaryOp(_, left, right) -> storesBeforeValue left || storesBeforeValue right
    // `dbg` of a tag value stores it in a hole local, as an interpolated string does, and a loop stores its elements.
    | FunctionCall("dbg", _, _)
    | FunctionCall("__for", _, _) -> true
    | FunctionCall(_, _, args) -> args |> List.exists storesBeforeValue
    | TupleExpr items
    | ListExpr items -> items |> List.exists storesBeforeValue
    | RecordExpr fields ->
        fields |> List.exists (function NamedField(_, value) | PositionalField value -> storesBeforeValue value)
    | MemberAccess(inner, _, _) -> storesBeforeValue inner
    | TagExpr(_, Some payload) -> storesBeforeValue payload
    // Each hole is stored in its local before the string is built.
    | InterpolatedString parts -> parts |> List.exists (function StringExpr _ -> true | StringText _ -> false)
    | _ -> false

/// The fields of a record expression by name, a positional field named by its position.
let private recordExprFields (fields: RecordField list) =
    fields
    |> List.mapi (fun index field ->
        match field with
        | NamedField(name, value) -> name, value
        | PositionalField value -> string index, value)

/// The messages of the errors deferred to run time inside `expr` (the `__panic` calls that stand for what failed to
/// check), in the order they are written.
let rec private deferredMessages (expr: Expression) : string list =
    let all (exprs: Expression list) = exprs |> List.collect deferredMessages
    match expr.Kind with
    | FunctionCall("__panic", _, [ Kind (LiteralExpr(StringLit message)) ]) -> [ message ]
    | InterpolatedString parts -> parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None) |> all
    | Block statements ->
        statements
        |> List.choose (fun statement ->
            match statement.Kind with
            | DefStatement(_, _, _, value)
            | ExprStatement value -> Some value
            | _ -> None)
        |> all
    | BinaryOp(_, left, right) -> all [ left; right ]
    | IfExpr(cond, ifTrue, ifFalse) -> all [ cond; ifTrue; ifFalse ]
    | FunctionCall(_, _, args)
    | ContextMemberCall(_, _, args) -> all args
    | Lambda(_, body)
    | UseIn(_, body)
    | MemberAccess(body, _, _)
    | WorkflowBindExpr(_, body)
    | WorkflowReturnExpr body -> deferredMessages body
    | Pipe(value, _, _, args) -> all (value :: args)
    | TupleExpr items
    | ListExpr items -> all items
    | RecordExpr fields -> fields |> List.map (function NamedField(_, value) | PositionalField value -> value) |> all
    | ExpressionKind.Match(values, arms) -> all (values @ (arms |> List.map snd))
    | TagExpr(_, payload) -> payload |> Option.toList |> all
    | ContextMemberAccess _
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// The type a type annotation names, if it is a primitive or a type the module defines: `string`, `(int)`, `Point`.
let rec private typeOfAnnotation (namedTypes: Map<string, Ty>) (typeExpr: TypeExpr) : Ty option =
    match typeExpr.Kind with
    | TypeName ("int" | "string" | "bool" | "float" | "unit" as name) -> Some (TyPrimitive name)
    | TypeName name -> namedTypes |> Map.tryFind name
    | TypeUnit -> Some (TyPrimitive "unit")
    | TypeTuple [ single ] -> typeOfAnnotation namedTypes single
    | _ -> None

let private flattenCallArgs (args: Expression list) =
    match args with
    | [ Kind (TupleExpr items) ] -> items
    | _ -> args

let private parametersAndBody (expr: Expression) =
    match expr.Kind with
    | Lambda(parameters, body) -> parameters |> List.map fst, body
    | _ -> [], expr

/// What names refer to in the expression being lowered.
type private Scope =
    { Vars: Map<string, Var>
      /// How many scrutinees enclose the expression: the next `match` holds its own from this number on.
      MatchDepth: int
      /// The names of the pattern-bound locals of the arms that enclose the expression, which a nested arm must not
      /// reuse.
      LivePayloads: Set<string>
      /// How many interpolated strings' holes enclose the expression (see `Interpolation.holeLocalName`).
      InterpolationDepth: int
      /// The context members in scope, by key (and by the other names they can be called by): each a variable holding
      /// the member's closure.
      Members: Map<string, Var>
      /// How many `for` loops enclose the expression, which names a loop's locals.
      ForDepth: int }

/// Lowers each function, asking for the specialisations its calls need through `request`.
type private FunctionLowering(input: LowerInput, definitions: Map<string, Expression>, subst: Map<int, Ty>, self: SpecKey, request: SpecKey -> Map<int, Ty> -> Lowering -> string) =
    let mutable nextId = 0
    let usedNames = System.Collections.Generic.HashSet<string>()

    let newVar (name: string) (kind: VarKind) (ty: IrType) =
        nextId <- nextId + 1
        { Id = nextId; Name = name; Kind = kind; Type = ty }

    /// A parameter's or local's name: as written, made unique within the function.
    let uniqueName (name: string) =
        let baseName = sanitizeIdentifier name
        let mutable candidate = baseName
        let mutable index = 1
        while usedNames.Contains candidate do
            candidate <- $"{baseName}_{index}"
            index <- index + 1
        usedNames.Add candidate |> ignore
        candidate

    let newTemp (ty: IrType) =
        let var = newVar "" Temp ty
        { var with Name = $"__t{var.Id}" }

    /// The local every `dbg` in the function holds its value in, made when the first needs it.
    let mutable dbgScratch: Var option = None
    let dbgLocal () =
        match dbgScratch with
        | Some var -> var
        | None ->
            let var = newVar (uniqueName "__dbg_tmp") Scratch IrInt
            dbgScratch <- Some var
            var

    let tyOf (expr: Expression) : IrType =
        match input.ExprTypes |> Map.tryFind expr.Id with
        | Some ty -> toIrType (substitute subst ty)
        | None -> cannot "an expression without a type"

    let isFunction (name: string) =
        match definitions.[name].Kind with
        | Lambda _ -> true
        | _ -> false

    let parameterNames (name: string) =
        match definitions.[name].Kind with
        | Lambda(parameters, _) -> parameters |> List.map fst
        | _ -> []

    /// The name to call `name` by at the instance type `instanceTy`: its own, or a specialisation's.
    let callee (name: string) (instanceTy: Ty) =
        let scheme =
            match input.DefinitionTypes |> Map.tryFind name with
            | Some scheme -> scheme
            | None -> cannot $"a call to '{name}', whose type is not known"
        match freeVars scheme |> List.distinct |> List.sort with
        | [] -> sanitizeIdentifier name
        | typeVars ->
            let bindings = bindScheme Map.empty scheme (substitute subst instanceTy)
            let typeArguments =
                typeVars
                |> List.map (fun id ->
                    match bindings |> Map.tryFind id with
                    | Some ty -> toIrType ty
                    // A type argument the call does not fix is a type variable nothing constrains (see `toIrType`).
                    | None -> IrUnit)
            request (name, typeArguments) bindings AsDefined

    let mk kind ty : Expr = { Kind = kind; Type = ty }

    /// The place in the source of the expression being lowered, where a failure to lower is reported.
    let mutable current: SourceSpan option = None

    /// The name of the function being lowered, in WebAssembly text.
    let mutable selfName = ""

    let atomType (atom: Atom) =
        match atom with
        | AVar var -> var.Type
        | AInt _ -> IrInt
        | ABool _ -> IrBool
        | AUnit -> IrUnit
        | AString _ -> IrString
        | ATag name -> IrTags [ name, None ]

    let bindTemp (value: Expr) (k: Atom -> Expr) : Expr =
        let temp = newTemp value.Type
        let body = k (AVar temp)
        mk (Let(temp, value, body)) body.Type

    /// Where the expression being lowered is written, for reporting what cannot be lowered.
    member _.CurrentSpan = current

    /// Lowers `expr` to a value that is not a `Let` (its own lets go around it), and passes that to `k`.
    member this.Complex (scope: Scope) (expr: Expression) (k: Expr -> Expr) : Expr =
        // What fails once `expr` is lowered, in the expression around it, is reported there.
        let around = current
        if not (SourceSpan.isNone expr.Span) then current <- Some expr.Span
        let k (value: Expr) =
            current <- around
            k value
        match expr.Kind with
        | UnitExpr -> k (mk (Atom AUnit) IrUnit)
        | LiteralExpr(IntLit value) -> k (mk (Atom(AInt(int64 value))) IrInt)
        | LiteralExpr(BoolLit value) -> k (mk (Atom(ABool value)) IrBool)
        | LiteralExpr(StringLit value) -> k (mk (Atom(AString value)) IrString)
        | LiteralExpr(FloatLit value) -> k (mk (BoxFloat value) IrFloat)
        | IdentifierExpr name ->
            match scope.Vars |> Map.tryFind name with
            | Some var -> k (mk (Atom(AVar var)) var.Type)
            | None when definitions.ContainsKey name && not (isFunction name) ->
                // A top-level value is compiled to a function of no parameters: evaluate it.
                let ty = tyOf expr
                k (mk (Call(callee name input.ExprTypes.[expr.Id], [])) ty)
            | None when definitions.ContainsKey name -> cannot $"the function '{name}' used as a value"
            | None -> cannot $"a reference to '{name}'"
        | TagExpr(name, None) -> k (mk (Atom(ATag name)) (tyOf expr))
        | TagExpr(name, Some payload) ->
            let ty = tyOf expr
            this.Atoms scope [ payload ] (fun atoms -> k (mk (MakeTag(name, atoms.Head)) ty))
        | BinaryOp("++", left, right) ->
            // Joining strings or lists: a runtime helper for each.
            let helper =
                match tyOf left with
                | IrString -> Helpers.stringConcat
                | IrList _ -> Helpers.listConcat
                | other -> cannot $"the operator ++ on a value of type {IrType.show other}"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (CallRuntime(helper, atoms, true)) ty))
        | BinaryOp(("==" | "!=") as op, left, right) when (match tyOf left, tyOf right with
                                                           | (IrString | IrTags _), _ | _, (IrString | IrTags _) -> true
                                                           | _ -> false) ->
            // A string is a pointer to its bytes, and a tag with a payload to its cell: equality compares what they
            // point to.
            let helper =
                match tyOf left, tyOf right with
                | IrString, _ | _, IrString -> Helpers.stringEquals
                | _ -> Helpers.tagEquals
            this.Atoms scope [ left; right ] (fun atoms ->
                let equal = mk (CallRuntime(helper, atoms, true)) IrBool
                if op = "==" then k equal
                else bindTemp equal (fun result -> k (mk (Prim(Not, [ result ])) IrBool)))
        | BinaryOp(op, left, right) when tyOf left = IrFloat || tyOf right = IrFloat ->
            // Floats are boxed: the operands are unboxed, and an arithmetic result boxed.
            if (wasmFloatInstruction op).IsNone then cannot $"the operator {op} on floats"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (Prim(FloatBinary op, atoms)) ty))
        | BinaryOp(op, left, right) ->
            if (wasmBinaryInstruction op).IsNone then cannot $"the operator {op}"
            for operand in [ left; right ] do
                match tyOf operand with
                | IrInt | IrBool | IrUnit | IrError -> ()
                | other -> cannot $"the operator {op} on a value of type {IrType.show other}"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (Prim(Binary op, atoms)) ty))
        | FunctionCall("not", _, [ operand ]) when not (scope.Vars.ContainsKey "not") && not (definitions.ContainsKey "not") ->
            this.Atoms scope [ operand ] (fun atoms -> k (mk (Prim(Not, atoms)) IrBool))
        | FunctionCall("__for", _, [ iterable; Kind (Lambda([ (parameter, _) ], body)) ]) ->
            // `for iterable | arms`: the body runs for each element, in a local the body names by the parameter. A
            // range counts from its start to its end; a list walks its cells.
            let depth = scope.ForDepth
            let cursor = newVar $"__for_cursor_{depth}" ForLoop IrInt
            let finish = newVar $"__for_end_{depth}" ForLoop IrInt
            let loop (source: ForSource) (elementTy: IrType) =
                let element = newVar $"__for_{sanitizeIdentifier parameter}_{depth}" ForLoop elementTy
                let bodyScope = { scope with Vars = scope.Vars |> Map.add parameter element; ForDepth = depth + 1 }
                let forLoop: ForLoop = { Source = source; Depth = depth; Cursor = cursor; End = finish; Element = element; Body = this.Tail bodyScope body }
                k (mk (ForEach forLoop) IrUnit)
            match iterable.Kind with
            | FunctionCall("__range", _, [ first; last ]) ->
                this.Atoms scope [ first; last ] (fun atoms -> loop (OverRange(atoms.[0], atoms.[1])) IrInt)
            | _ ->
                match tyOf iterable with
                | IrList elementTy -> this.Atoms scope [ iterable ] (fun atoms -> loop (OverList atoms.Head) elementTy)
                | other -> cannot $"a `for` loop over a value of type {IrType.show other}"
        | FunctionCall("__panic", _, [ Kind (LiteralExpr(StringLit message)) ]) ->
            // One the type checker added (the last arm of a match that does not handle every case) has no type.
            let ty = if input.ExprTypes.ContainsKey expr.Id then tyOf expr else IrError
            k (mk (Panic [ message ]) ty)
        | FunctionCall("dbg", _, args) when not (scope.Vars.ContainsKey "dbg") && not (definitions.ContainsKey "dbg") ->
            // Prints its value and leaves it, by how the value is printed.
            let value =
                match flattenCallArgs args with
                | [ single ] -> single
                | _ -> cannot "`dbg` of more than one value"
            let ty = tyOf expr
            let scratch = dbgLocal ()
            match value.Kind with
            | TagExpr(name, None) -> k (mk (Dbg(ATag name, DbgTag(name, scratch))) ty)
            | TagExpr(name, Some payload) ->
                this.Atoms scope [ payload ] (fun atoms -> k (mk (Dbg(atoms.Head, DbgTagPayload(name, scratch))) ty))
            | _ ->
                // For any value but a literal, the old path declares the locals it would print a tag with.
                let holeDepth =
                    match value.Kind with
                    | LiteralExpr _
                    | InterpolatedString _ -> None
                    | _ -> Some scope.InterpolationDepth
                match this.TypeIn scope value with
                | IrString -> this.Atoms scope [ value ] (fun atoms -> k (mk (Dbg(atoms.Head, DbgString(scratch, holeDepth))) ty))
                | IrFloat -> this.Atoms scope [ value ] (fun atoms -> k (mk (Dbg(atoms.Head, DbgFloat(scratch, holeDepth))) ty))
                | IrInt
                | IrBool
                | IrUnit -> this.Atoms scope [ value ] (fun atoms -> k (mk (Dbg(atoms.Head, DbgNumber(scratch, holeDepth))) ty))
                | IrTags _ ->
                    // A tag value prints as `"{value}"` would: evaluated into a hole, a level deeper.
                    let holeType = this.HoleType scope value
                    let hole = newVar (Interpolation.holeLocalName scope.InterpolationDepth 0) InterpolationHole (tyOf value)
                    this.Complex { scope with InterpolationDepth = scope.InterpolationDepth + 1 } value (fun lowered ->
                        let body = k (mk (Dbg(AVar hole, DbgTagValue(hole, holeType, scratch))) ty)
                        mk (Let(hole, lowered, body)) body.Type)
                | other -> cannot $"`dbg` of a value of type {IrType.show other}"
        | FunctionCall("__with", _, [ record; Kind (RecordExpr updates) ]) ->
            // `record with f = v, ...`: a copy of the record with those fields replaced.
            let ty = tyOf expr
            let fields =
                match tyOf record with
                | IrRecord fields -> fields
                | other -> cannot $"`with` on a value of type {IrType.show other}"
            let slotsAndValues =
                updates
                |> List.map (function
                    | NamedField(name, value) ->
                        match fields |> List.tryFindIndex (fst >> (=) name) with
                        | Some slot -> slot, value
                        | None -> cannot $"an update of '{name}', which the record does not have"
                    | PositionalField _ -> cannot "a positional field in a record update")
            this.Atoms scope (record :: List.map snd slotsAndValues) (fun atoms ->
                let updated = List.zip (List.map fst slotsAndValues) atoms.Tail
                k (mk (UpdateRecord(atoms.Head, fields.Length, updated)) ty))
        | FunctionCall(name, _, args) when input.NominalWrappers.Contains name && not (scope.Vars.ContainsKey name) ->
            // `$Money(pence)`: a nominal value is its shape's value.
            match flattenCallArgs args with
            | [ single ] -> this.Complex scope single k
            | several -> this.Record scope (several |> List.mapi (fun index value -> string index, value)) (tyOf expr) k
        | FunctionCall(name, _, args) when input.RecordConstructors.Contains name && not (scope.Vars.ContainsKey name) ->
            match flattenCallArgs args with
            | [ Kind (RecordExpr fields) ] -> this.Record scope (recordExprFields fields |> sortFields) (tyOf expr) k
            | _ -> cannot $"the type constructor {name} without a record of its fields"
        | FunctionCall(name, _, args) when scope.Members.ContainsKey name ->
            // A context member in scope: a function value like any other.
            let ty = tyOf expr
            this.Atoms scope (flattenCallArgs args) (fun atoms -> k (mk (CallClosure(ClosureVar scope.Members.[name], atoms)) ty))
        | FunctionCall(name, _, args) when Intrinsics.isIntrinsic name && not (scope.Vars.ContainsKey name) ->
            // A compiler intrinsic (only the standard library can call these, and a built-in property such as
            // `s.length` is one): the runtime helper of the same name.
            let ty = tyOf expr
            this.Atoms scope (flattenCallArgs args) (fun atoms ->
                k (mk (CallRuntime(name, atoms, not (Intrinsics.returnsUnit name))) ty))
        | FunctionCall("__closure", _, Kind (FunctionCall("__slot", _, [ Kind (IdentifierExpr target) ])) :: captured) ->
            // A function value: the closure block of a lifted function over what it captures. The closure keeps the
            // node of the lambda or function it stands for, so its type is that function's, with the parameters lambda
            // lifting annotated where the type checker left them open.
            let ty =
                match input.Lifted |> Map.tryFind target, input.ExprTypes |> Map.tryFind expr.Id with
                | Some (LiftedLambda _), Some lambdaTy ->
                    let parameterTys, resultTy = this.LiftedParameterTypes target (substitute subst lambdaTy)
                    let inputTy =
                        match parameterTys with
                        | [] -> TyPrimitive "unit"
                        | [ single ] -> single
                        | several -> TyRecord(several |> List.mapi (fun index ty -> string index, ty) |> Map.ofList)
                    toIrType (substitute subst (TyFunc(inputTy, resultTy)))
                | _ -> tyOf expr
            let arity = parameterNames target |> List.length
            match input.Lifted |> Map.tryFind target with
            | Some (LiftedLambda _) ->
                this.Atoms scope captured (fun atoms ->
                    let capturedTys = atoms |> List.map atomType
                    // A lambda in a specialisation is specialised with it.
                    let name = request (target, snd self) subst (LiftedCapturing capturedTys)
                    k (mk (MakeClosure(name, arity, atoms)) ty))
            | Some (FunctionWrapper wrapped) ->
                // A top-level function passed as a value, through a wrapper for each type it is passed at.
                let instanceTy = substitute subst input.ExprTypes.[expr.Id]
                let generic = input.DefinitionTypes |> Map.tryFind wrapped |> Option.exists (freeVars >> List.isEmpty >> not)
                let key = target, (if generic then [ toIrType instanceTy ] else [])
                let name = request key subst (WrapperCalling(wrapped, instanceTy))
                k (mk (MakeClosure(name, arity, [])) ty)
            | None -> cannot $"a closure over '{target}', which lambda lifting did not make"
        | FunctionCall(name, _, args) when scope.Vars.ContainsKey name ->
            // A call through a function value in a parameter or local.
            let ty = tyOf expr
            match scope.Vars.[name] with
            | { Type = IrFunction _ } as var -> this.Atoms scope (flattenCallArgs args) (fun atoms -> k (mk (CallClosure(ClosureVar var, atoms)) ty))
            | var -> cannot $"a call to '{name}', a value of type {IrType.show var.Type}"
        | FunctionCall(name, _, args) when definitions.ContainsKey name && not (isFunction name) && not (flattenCallArgs args).IsEmpty ->
            // A call through a function value a top-level value holds.
            let ty = tyOf expr
            let instanceTy =
                match input.CalleeTypes |> Map.tryFind expr.Id with
                | Some ty -> ty
                | None -> cannot $"a call to '{name}' whose type is not known"
            let target = callee name instanceTy
            this.Atoms scope (flattenCallArgs args) (fun atoms -> k (mk (CallClosure(ClosureValue target, atoms)) ty))
        | FunctionCall(name, _, args) when input.Lifted.ContainsKey name ->
            // A `rec` local function calling itself, directly, with its environment passed on.
            if name <> fst self then cannot $"a call to the lifted function '{name}' from another function"
            let ty = tyOf expr
            this.Atoms scope (flattenCallArgs args) (fun atoms -> k (mk (Call(selfName, atoms)) ty))
        | FunctionCall(name, _, args) ->
            if not (definitions.ContainsKey name) then cannot $"a call to '{name}'"
            if not (isFunction name) then cannot $"a call to the value '{name}'"
            let instanceTy =
                match input.CalleeTypes |> Map.tryFind expr.Id with
                | Some ty -> ty
                | None ->
                    // A call the type checker did not record as one (a built-in property, such as `xs.length`, is
                    // written as a call to the function that reads it): its type is its arguments' and its own.
                    let argumentTys =
                        flattenCallArgs args
                        |> List.map (fun arg ->
                            match input.ExprTypes |> Map.tryFind arg.Id with
                            | Some ty -> ty
                            | None -> cannot $"a call to '{name}' whose type is not known")
                    let input' =
                        match argumentTys with
                        | [ single ] -> single
                        | several -> TyRecord(several |> List.mapi (fun index ty -> string index, ty) |> Map.ofList)
                    match input.ExprTypes |> Map.tryFind expr.Id with
                    | Some output -> TyFunc(input', output)
                    | None -> cannot $"a call to '{name}' whose type is not known"
            let target = callee name instanceTy
            let ty = tyOf expr
            // The context members the callee requires follow its arguments, each found where the type checker found it.
            let call atoms =
                match input.ContextParameters |> Map.tryFind name with
                | None -> k (mk (Call(target, atoms)) ty)
                | Some _ ->
                    match input.ContextArguments name expr.Id (scope.Members |> Map.keys |> Set.ofSeq) with
                    | Error reason -> cannot reason
                    | Ok arguments ->
                        let rec pass (remaining: ContextArgument list) (acc: Atom list) =
                            match remaining with
                            | [] -> k (mk (Call(target, atoms @ List.rev acc)) ty)
                            | MemberInScope key :: rest -> pass rest (AVar scope.Members.[key] :: acc)
                            | ClosureOverWrapper wrapper :: rest ->
                                let closure = this.WrapperClosure wrapper
                                bindTemp closure (fun atom -> pass rest (atom :: acc))
                            | DefinitionValue definition :: _ -> cannot $"the context member '{definition}' passed by its value"
                        pass arguments []
            let parameters = parameterNames name
            // The arguments as the callee takes them, in the shapes the old path accepts.
            match flattenCallArgs args with
            | [ Kind (IdentifierExpr record) ] when parameters.Length > 1 && scope.Vars.ContainsKey record ->
                // A record passed to a function of several parameters: each parameter takes a field.
                let var = scope.Vars.[record]
                match var.Type with
                | IrRecord fields when fields.Length = parameters.Length ->
                    let rec loadFields (slot: int) (acc: Atom list) =
                        if slot = fields.Length then call (List.rev acc)
                        else bindTemp (mk (Field(AVar var, slot)) (snd fields.[slot])) (fun atom -> loadFields (slot + 1) (atom :: acc))
                    loadFields 0 []
                | _ -> cannot $"a call to '{name}' with one argument for its {parameters.Length} parameters"
            | [ Kind (RecordExpr fields) ] when parameters.Length > 1 && fields.Length = parameters.Length ->
                // `f(a, step = 2)`: each parameter takes the field of its name, or else the positional field in its
                // place.
                let positional = fields |> List.choose (function PositionalField value -> Some value | NamedField _ -> None)
                let named = fields |> List.choose (function NamedField(n, value) -> Some (n, value) | PositionalField _ -> None) |> Map.ofList
                let ordered =
                    parameters
                    |> List.mapi (fun index parameter ->
                        match named |> Map.tryFind parameter, positional |> List.tryItem index with
                        | Some value, _
                        | None, Some value -> value
                        | None, None -> cannot $"a call to '{name}' with no argument for {parameter}")
                this.Atoms scope ordered call
            | several when parameters.Length = 1 && several.Length > 1 ->
                // `f(a, b)` and `f((a, b))` parse alike; a function of one parameter takes the whole tuple.
                let tupleTy = IrRecord(several |> List.mapi (fun index value -> string index, tyOf value))
                this.Record scope (several |> List.mapi (fun index value -> string index, value)) tupleTy (fun tuple ->
                    bindTemp tuple (fun atom -> call [ atom ]))
            | [] when parameters.Length = 1 ->
                // `f()` on a function written `{ _ -> ... }`: its one parameter is the unit.
                call [ AUnit ]
            | flatArgs when flatArgs.Length = parameters.Length -> this.Atoms scope flatArgs call
            | _ -> cannot $"a call to '{name}' with its arguments in another shape"
        | TupleExpr items -> this.Record scope (items |> List.mapi (fun index item -> string index, item)) (tyOf expr) k
        | ListExpr items ->
            let ty = tyOf expr
            this.Atoms scope items (fun atoms -> k (mk (MakeList atoms) ty))
        | RecordExpr fields -> this.Record scope (recordExprFields fields |> sortFields) (tyOf expr) k
        | MemberAccess(Kind (IdentifierExpr qualifier), fieldName, _) when
            not (scope.Vars.ContainsKey qualifier) && definitions.ContainsKey $"{qualifier}.{fieldName}" ->
            // A value of another module, named through it.
            this.Complex scope { expr with Kind = IdentifierExpr $"{qualifier}.{fieldName}" } k
        | MemberAccess(record, fieldName, _) ->
            // The field's type is the record's for it: a lifted function reads what it captures from its environment
            // with reads the type checker never saw.
            let recordTy =
                match record.Kind with
                | IdentifierExpr name when scope.Vars.ContainsKey name -> scope.Vars.[name].Type
                | _ -> tyOf record
            let slot, ty =
                match recordTy with
                | IrRecord fields ->
                    match fields |> List.tryFindIndex (fst >> (=) fieldName) with
                    | Some slot -> slot, snd fields.[slot]
                    | None -> cannot $"the field '{fieldName}', which the record does not have"
                | other -> cannot $"the field '{fieldName}' of a value of type {IrType.show other}"
            this.Atoms scope [ record ] (fun atoms -> k (mk (Field(atoms.Head, slot)) ty))
        | IfExpr(cond, ifTrue, ifFalse) ->
            let ty = tyOf expr
            this.Atoms scope [ cond ] (fun atoms ->
                k (mk (If(atoms.Head, this.Tail scope ifTrue, this.Tail scope ifFalse)) ty))
        | Block statements -> this.Block scope (statements |> List.map _.Kind) k
        | ExpressionKind.Match(values, arms) -> this.Match scope expr values arms k
        | ContextMemberCall(contextType, memberName, args) ->
            // `C(T).f(args)`: the member of that instance of the context, which must be in scope.
            let key = contextMemberKey (Some contextType) memberName
            let ty = tyOf expr
            match scope.Members |> Map.tryFind key with
            | Some memberVar -> this.Atoms scope (flattenCallArgs args) (fun atoms -> k (mk (CallClosure(ClosureVar memberVar, atoms)) ty))
            | None -> cannot $"the context member {key}, which is not in scope"
        // A lambda of no parameters that is not a function's own runs where it is written, as the old path runs it.
        | Lambda([], body) -> this.Complex scope body k
        // `x \f(a, b)` is the call `f(x, a, b)`, typed at the pipe's node.
        | Pipe(value, name, range, args) -> this.Complex scope { expr with Kind = FunctionCall(name, range, pipeCallArguments value args) } k
        | InterpolatedString parts when parts |> List.exists (function StringExpr hole -> tyOf hole = IrError | StringText _ -> false) ->
            // A hole of no known type cannot be printed. The holes are evaluated in order up to it, for what they do (the
            // hole itself most likely stops the program, through the deferred error that left its type unknown), and if
            // the program is still running then, it stops there.
            let insideHole = { scope with InterpolationDepth = scope.InterpolationDepth + 1 }
            let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
            let before = holes |> List.takeWhile (fun hole -> tyOf hole <> IrError)
            let unknown = holes |> List.find (fun hole -> tyOf hole = IrError)
            let stop = mk (Panic [ Interpolation.unknownHoleMessage unknown.Span ]) IrString
            (before @ [ unknown ])
            |> List.rev
            |> List.fold (fun (rest: Expr) hole -> this.Complex insideHole hole (fun value -> mk (Do(value, rest)) rest.Type)) stop
            |> k
        | InterpolatedString parts ->
            // Each hole is evaluated into its local, in order, and then the string is built from the text and them.
            // A string inside a hole is a level deeper, so that it keeps to its own hole locals.
            let depth = scope.InterpolationDepth
            let insideHole = { scope with InterpolationDepth = depth + 1 }
            let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
            let rec evaluateHoles (index: int) (remaining: Expression list) (evaluated: (Var * HoleType) list) =
                match remaining with
                | [] ->
                    let mutable next = List.rev evaluated
                    let irParts =
                        parts
                        |> List.map (function
                            | StringText text -> Text text
                            | StringExpr _ ->
                                let var, holeType = next.Head
                                next <- next.Tail
                                Hole(var, holeType))
                    k (mk (Interpolate irParts) IrString)
                | hole :: rest ->
                    let holeType = this.HoleType scope hole
                    // A float is formatted to a string as its hole is evaluated, since that allocates.
                    let local = newVar (Interpolation.holeLocalName depth index) InterpolationHole (if holeType = HoleFloat then IrString else tyOf hole)
                    let store (value: Expr) =
                        let body = evaluateHoles (index + 1) rest ((local, holeType) :: evaluated)
                        mk (Let(local, value, body)) body.Type
                    if holeType = HoleFloat then
                        this.Atoms insideHole [ hole ] (fun atoms -> store (mk (CallRuntime(Helpers.floatToString, atoms, true)) IrString))
                    else this.Complex insideHole hole store
            evaluateHoles 0 holes []
        | Lambda _ -> cannot "a lambda"
        | UseIn _ -> cannot "`use ... in`"
        | _ -> cannot "this kind of expression"

    /// A closure over the wrapper `wrapper` of a top-level function, at the function's own type.
    member this.WrapperClosure (wrapper: string) : Expr =
        let wrapped =
            match input.Lifted |> Map.tryFind wrapper with
            | Some (FunctionWrapper wrapped) -> wrapped
            | _ -> cannot $"'{wrapper}' is not a function's wrapper"
        let wrappedTy =
            match input.DefinitionTypes |> Map.tryFind wrapped with
            | Some ty when (freeVars ty).IsEmpty -> ty
            | _ -> cannot $"passing the generic function '{wrapped}' as a context member"
        let name = request (wrapper, []) subst (WrapperCalling(wrapped, wrappedTy))
        mk (MakeClosure(name, parameterNames wrapper |> List.length, [])) (toIrType wrappedTy)

    /// The type of `expr` where it is: a name in scope has its variable's type, and a field of one the field's, which
    /// may be more than the type checker recorded at the node (for a parameter lambda lifting annotated, say).
    member this.TypeIn (scope: Scope) (expr: Expression) : IrType =
        match expr.Kind with
        | IdentifierExpr name when scope.Vars.ContainsKey name -> scope.Vars.[name].Type
        | MemberAccess(record, fieldName, _) when (match record.Kind with IdentifierExpr name -> scope.Vars.ContainsKey name | _ -> true) ->
            match this.TypeIn scope record with
            | IrRecord fields when fields |> List.exists (fst >> (=) fieldName) -> fields |> List.find (fst >> (=) fieldName) |> snd
            | _ -> tyOf expr
        | _ -> tyOf expr

    /// How the hole `hole` of an interpolated string is printed, from its type. A tag's cases are in the order its type
    /// names them, which is the order they are tested in.
    member this.HoleType (scope: Scope) (hole: Expression) : HoleType =
        match this.TypeIn scope hole with
        | IrString -> HoleString
        | IrInt -> HoleInt
        | IrBool -> HoleBool
        | IrFloat -> HoleFloat
        | IrTags tags ->
            // In the order the hole's type names them, where the type checker gave the hole one.
            let rec cases (ty: Ty) =
                match Unifier.unalias ty with
                | TyTag(name, payload) -> [ name, payload |> Option.map toIrType ]
                | TyUnion items -> items |> List.collect cases
                | TyVar _ -> []
                | _ -> cannot "interpolating a union with tags not known here"
            let ordered =
                match input.ExprTypes |> Map.tryFind hole.Id |> Option.map (substitute subst) with
                | Some ty when (match Unifier.unalias ty with TyTag _ | TyUnion _ -> true | _ -> false) -> cases ty
                | _ -> tags
            ordered
            |> List.distinctBy fst
            |> List.map (fun (name, payloadTy) ->
                match payloadTy with
                | None
                | Some (IrString | IrInt | IrBool) -> name, payloadTy
                | Some other -> cannot $"interpolating #{name} with a payload of type {IrType.show other}")
            |> HoleTag
        | other -> cannot $"interpolating a value of type {IrType.show other}"

    /// Allocates a record of `fields` (name and value, in the order they are stored), of type `ty`.
    member this.Record (scope: Scope) (fields: (string * Expression) list) (ty: IrType) (k: Expr -> Expr) : Expr =
        this.Atoms scope (List.map snd fields) (fun atoms -> k (mk (MakeRecord(List.zip (List.map fst fields) atoms)) ty))

    /// Lowers `expr` in tail position: the whole of what is left to do.
    member this.Tail (scope: Scope) (expr: Expression) : Expr = this.Complex scope expr id

    /// Lowers `exprs` in order to atoms, binding each value that is not one to a temporary. An operand that stores to a
    /// local on the way to its value (see `storesBeforeValue`) is lowered whole, its stores with it, into one
    /// temporary: it is computed where it is used, as the old path computes it, so nothing computed before it waits on
    /// the stack across its stores.
    member this.Atoms (scope: Scope) (exprs: Expression list) (k: Atom list -> Expr) : Expr =
        let rec go (acc: Atom list) (remaining: Expression list) =
            match remaining with
            | [] -> k (List.rev acc)
            | expr :: rest when storesBeforeValue expr -> bindTemp (this.Tail scope expr) (fun atom -> go (atom :: acc) rest)
            | expr :: rest ->
                this.Complex scope expr (fun value ->
                    match value.Kind with
                    | Atom atom -> go (atom :: acc) rest
                    | _ -> bindTemp value (fun atom -> go (atom :: acc) rest))
        go [] exprs

    member this.Block (scope: Scope) (statements: StatementKind list) (k: Expr -> Expr) : Expr =
        match statements with
        | [] -> cannot "an empty block"
        | [ ExprStatement last ] -> this.Complex scope last k
        | DefStatement(_, _, _, Kind (Lambda _)) :: _ -> cannot "a local function"
        | DefStatement(_, name, _, rhs) :: rest ->
            // The local is named before its value is lowered, so that locals are declared in the order they are
            // written. Its type is its value's, which a local lambda lifting made has as well.
            let localName = uniqueName name
            this.Complex scope rhs (fun value ->
                let local = newVar localName Local value.Type
                let body = this.Block { scope with Vars = scope.Vars |> Map.add name local } rest k
                mk (Let(local, value, body)) body.Type)
        | ExprStatement first :: rest ->
            this.Complex scope first (fun value ->
                let body = this.Block scope rest k
                mk (Do(value, body)) body.Type)
        | UseStatement(UseValue(Kind (RecordExpr fields))) :: rest ->
            // Lambda lifting has evaluated each binding into a local; the members are those locals.
            let members =
                fields
                |> List.fold (fun (acc: Map<string, Var>) field ->
                    match field with
                    | NamedField(key, Kind (IdentifierExpr local)) when scope.Vars.ContainsKey local -> acc |> Map.add key scope.Vars.[local]
                    | NamedField(key, _) -> cannot $"the `use` binding of '{key}'"
                    | PositionalField _ -> cannot "a `use` binding without a name") scope.Members
            this.Block { scope with Members = members } rest k
        | UseStatement _ :: _ -> cannot "this kind of `use`"
        | _ -> cannot "this kind of statement"

    member this.Match (scope: Scope) (expr: Expression) (values: Expression list) (arms: (Parser.Program.Pattern list * Expression) list) (k: Expr -> Expr) : Expr =
        let ty = tyOf expr
        let scrutinees =
            values |> List.mapi (fun index value -> newVar $"__match_tmp_{scope.MatchDepth + index}" MatchTemp (tyOf value))
        let armScope = { scope with MatchDepth = scope.MatchDepth + values.Length }
        let lowerArm (patterns: Parser.Program.Pattern list, body: Expression) =
            if patterns.Length <> values.Length then cannot "a match arm with a different number of patterns"
            let mutable armVars = armScope.Vars
            let mutable payloads = Set.empty
            let rec lowerPattern (isTop: bool) (scrutinee: Var) (valueTy: IrType) (pattern: Parser.Program.Pattern) =
                match pattern.Kind with
                | WildcardPattern
                | UnitPattern
                | ElsePattern -> PWildcard
                // A name for the whole scrutinee is the scrutinee's local.
                | IdentifierPattern name when isTop ->
                    armVars <- armVars |> Map.add name scrutinee
                    PWildcard
                | IdentifierPattern name ->
                    let baseName = $"__payload_{sanitizeIdentifier name}"
                    let mutable candidate = baseName
                    let mutable index = 1
                    while armScope.LivePayloads.Contains candidate || payloads.Contains candidate do
                        candidate <- $"{baseName}_{index}"
                        index <- index + 1
                    payloads <- payloads.Add candidate
                    let var = newVar candidate PatternBound valueTy
                    armVars <- armVars |> Map.add name var
                    PBind var
                | LiteralPattern(IntLit value) -> PInt(int64 value)
                | LiteralPattern(StringLit value) -> PString value
                | GuardPattern(op, guard) ->
                    // `| > 5 ->`, `| { >= limit * 2 } ->`: compared, as the old path compares it, as ints, to what the
                    // expression computes each time the arm is tested.
                    if (wasmBinaryInstruction op).IsNone then cannot $"the guard operator {op}"
                    PGuard(op, this.Tail armScope guard)
                | ListPattern(elements, rest) ->
                    let elementTy =
                        match valueTy with
                        | IrList elementTy -> elementTy
                        | other -> cannot $"a list pattern on a value of type {IrType.show other}"
                    PList(elements |> List.map (lowerPattern false scrutinee elementTy), rest |> Option.map (lowerPattern false scrutinee valueTy))
                | LiteralPattern(BoolLit value) -> PBool value
                | TagPattern(name, payload) ->
                    let payloadTy =
                        match valueTy with
                        | IrTags tags ->
                            match tags |> List.tryFind (fst >> (=) name) with
                            | Some (_, payloadTy) -> payloadTy
                            | None -> raise NeverMatches
                        | _ -> cannot $"the pattern #{name} on a value that is not a tag"
                    match payload, payloadTy with
                    | Some inner, Some innerTy -> PTag(name, Some (lowerPattern false scrutinee innerTy inner))
                    | None, _ -> PTag(name, None)
                    | Some _, None -> cannot $"a payload pattern on #{name}, which has none"
                | TuplePattern elements ->
                    let fields =
                        match valueTy with
                        | IrRecord fields when fields.Length = elements.Length -> fields
                        | other -> cannot $"a tuple pattern on a value of type {IrType.show other}"
                    let slotOf index = fields |> List.findIndex (fst >> (=) (string index))
                    PFields(elements |> List.mapi (fun index element ->
                        let slot = slotOf index
                        slot, lowerPattern false scrutinee (snd fields.[slot]) element))
                // `$Money(p)`: a nominal value is its shape's value, which the inner pattern matches. Its name gets a
                // local even at the top of an arm, as the old path gives it one.
                | RecordPattern(typeName, [ inner ]) when (typeName.Split('.') |> Array.last).StartsWith "$" ->
                    lowerPattern false scrutinee valueTy inner
                | _ -> cannot "this kind of pattern"
            // An arm for a tag the value cannot have never matches, so it is left out.
            match (try Some (List.map2 (fun scrutinee pattern -> lowerPattern true scrutinee scrutinee.Type pattern) scrutinees patterns) with NeverMatches -> None) with
            | Some loweredPatterns ->
                let bodyScope = { armScope with Vars = armVars; LivePayloads = Set.union armScope.LivePayloads payloads }
                Some { Patterns = loweredPatterns; Body = this.Tail bodyScope body }
            | None -> None
        // The scrutinees are evaluated in order, each into its local, before any arm is tested.
        let rec bindScrutinees (remaining: (Var * Expression) list) =
            match remaining with
            | [] -> k (mk (Match(scrutinees, arms |> List.choose lowerArm)) ty)
            | (scrutinee, value) :: rest ->
                this.Complex scope value (fun lowered ->
                    let body = bindScrutinees rest
                    mk (Let(scrutinee, lowered, body)) body.Type)
        match values |> List.tryFindIndex (fun value -> tyOf value = IrError) with
        | Some unknown ->
            // A value of no known type cannot be matched. The values are evaluated in order up to it, for what they do
            // (it most likely stops the program itself, through the deferred error that left its type unknown), and
            // if the program is still running then, it stops there.
            let stop = mk (Panic [ Interpolation.unknownScrutineeMessage values.[unknown].Span ]) ty
            values
            |> List.take (unknown + 1)
            |> List.rev
            |> List.fold (fun (rest: Expr) value -> this.Complex scope value (fun lowered -> mk (Do(lowered, rest)) rest.Type)) stop
            |> k
        | None -> bindScrutinees (List.zip scrutinees values)

    /// The start function: each top-level expression in turn, its value dropped. The start function returns nothing,
    /// so nothing in it is a tail call; its body ends in unit, which the emitter leaves out (see `emitStart`).
    member this.StartFunction () : Function =
        let scope =
            { Vars = Map.empty
              MatchDepth = 0
              LivePayloads = Set.empty
              InterpolationDepth = 0
              Members = Map.empty
              ForDepth = 0 }
        // An expression that holds errors deferred to run time, and cannot be lowered because of them, reports them
        // when it is reached instead.
        let lowerExpression (expr: Expression) =
            try this.Tail scope expr
            with CannotLower _ when not (deferredMessages expr).IsEmpty -> mk (Panic(deferredMessages expr)) IrError
        let body =
            List.foldBack (fun (expr: Expression) (rest: Expr) -> mk (Do(lowerExpression expr, rest)) IrUnit) input.TopLevel (mk (Atom AUnit) IrUnit)
        { Name = startFunctionName
          Source = startFunctionName
          TypeArguments = []
          Params = []
          Result = IrUnit
          Body = body }

    /// The definition `name` as a function called `functionName` that reports `messages`, errors deferred to run time,
    /// as soon as it is called: what a definition that cannot be lowered because of them is instead. It takes the
    /// parameters the definition does, context members included, as values of no known type.
    member this.Trap (name: string) (functionName: string) (typeArguments: IrType list) (messages: string list) : Function =
        let parameterNames, _ = parametersAndBody definitions.[name]
        let contextParameters = input.ContextParameters |> Map.tryFind name |> Option.map fst |> Option.defaultValue []
        let parameters =
            (parameterNames |> List.map (fun parameterName -> newVar (uniqueName parameterName) Param IrError))
            @ (contextParameters |> List.map (fun parameter -> newVar parameter.Name Param IrError))
        { Name = functionName
          Source = name
          TypeArguments = typeArguments
          Params = parameters
          Result = IrError
          Body = mk (Panic messages) IrError }

    /// The types of the parameters of the lifted lambda `name` (its environment aside), whose type where it was written
    /// is `lambdaTy`, and of its result. A lambda bound to a context member in a `use` may have parameters the type
    /// checker left open; lambda lifting annotates them with the types the member declares.
    member this.LiftedParameterTypes (name: string) (lambdaTy: Ty) : Ty list * Ty =
        let parameters =
            match definitions.[name].Kind with
            | Lambda(parameters, _) -> parameters.Tail
            | _ -> []
        let parameterTys, resultTy = this.ParameterTypes (List.map fst parameters) lambdaTy true
        let annotated =
            List.map2 (fun (ty: Ty) (_, annotation) ->
                match annotation |> Option.bind (typeOfAnnotation input.NamedTypes) with
                | Some annotatedTy when not (freeVars (substitute subst ty)).IsEmpty -> annotatedTy
                | _ -> ty) parameterTys parameters
        annotated, resultTy

    /// The types of the parameters `names` of a function of type `fnTy`, and of its result. Several parameters are the
    /// fields of the input, each named by its position or by its own name when the type names it
    /// (`(int, step: int) -> int`).
    member private this.ParameterTypes (names: string list) (fnTy: Ty) (isLambda: bool) : Ty list * Ty =
        match names, Unifier.unalias fnTy with
        | [], TyFunc(_, result) when isLambda -> [], result
        | [], _ -> [], fnTy
        | [ _ ], TyFunc(input, result) -> [ input ], result
        | several, TyFunc(input, result) ->
            match Unifier.unalias input with
            | TyRecord fields when fields.Count = several.Length ->
                several
                |> List.mapi (fun index parameterName ->
                    match fields |> Map.tryFind (string index), fields |> Map.tryFind parameterName with
                    | Some ty, _
                    | None, Some ty -> ty
                    | None, None -> cannot $"a function whose parameter {parameterName} is not among its input's fields"), result
            | _ -> cannot "a function whose parameters are not a tuple"
        | _ -> cannot "a definition whose type is not a function"

    /// Lowers the definition `name` to a function called `functionName`, as `lowering` says.
    member this.Function (lowering: Lowering) (name: string) (functionName: string) (typeArguments: IrType list) : Function =
        selfName <- functionName
        match lowering with
        | AsStart -> this.StartFunction ()
        | _ ->
        let expr = definitions.[name]
        let parameterNames, body = parametersAndBody expr
        let isLambda = match expr.Kind with Lambda _ -> true | _ -> false
        let parameterOf (parameterName: string) (ty: IrType) = newVar (uniqueName parameterName) Param ty
        let scopeOf (parameters: Var list) =
            { Vars = List.zip parameterNames (parameters |> List.truncate parameterNames.Length) |> Map.ofList
              MatchDepth = 0
              LivePayloads = Set.empty
              InterpolationDepth = 0
              Members = Map.empty
              ForDepth = 0 }
        let make (parameters: Var list) (result: IrType) (body: Expr) =
            { Name = functionName
              Source = name
              TypeArguments = typeArguments
              Params = parameters
              Result = result
              Body = body }
        match lowering with
        | AsStart -> this.StartFunction ()
        | AsDefined ->
            let defTy =
                match input.ExprTypes |> Map.tryFind expr.Id with
                | Some ty -> substitute subst ty
                | None -> cannot "a definition without a type"
            let parameterTys, resultTy = this.ParameterTypes parameterNames defTy isLambda
            let parameters = List.map2 (fun parameterName ty -> parameterOf parameterName (toIrType ty)) parameterNames parameterTys
            // The members of the contexts it requires follow, each a closure in a parameter of its own.
            let contextParameters, aliases = input.ContextParameters |> Map.tryFind name |> Option.defaultValue ([], Map.empty)
            let members =
                contextParameters
                |> List.map (fun parameter ->
                    match parameter.Type with
                    | Some ty -> parameter.Key, newVar parameter.Name Param (toIrType (substitute subst ty))
                    | None -> cannot $"the context member {parameter.Key}, whose type is not known")
            let memberScope =
                aliases
                |> Map.fold (fun acc alias key -> acc |> Map.add alias (Map.ofList members).[key]) (Map.ofList members)
            let scope = { scopeOf parameters with Members = memberScope }
            make (parameters @ List.map snd members) (toIrType resultTy) (this.Tail scope body)
        | LiftedCapturing capturedTys ->
            // The environment first: the function's table slot, then what it captures. The rest are the lambda's
            // parameters, typed as the lambda was where it was written.
            let lambda =
                match input.Lifted |> Map.tryFind name with
                | Some (LiftedLambda(lambda, _)) -> lambda
                | _ -> cannot $"'{name}' is not a lifted lambda"
            let lambdaTy =
                match input.ExprTypes |> Map.tryFind lambda.Id with
                | Some ty -> substitute subst ty
                | None -> cannot "a lambda without a type"
            let environment = IrRecord(("0", IrInt) :: (capturedTys |> List.mapi (fun index ty -> string (index + 1), ty)))
            let parameterTys, resultTy = this.LiftedParameterTypes name lambdaTy
            let parameters =
                parameterOf parameterNames.Head environment
                :: List.map2 (fun parameterName ty -> parameterOf parameterName (toIrType ty)) parameterNames.Tail parameterTys
            make parameters (toIrType resultTy) (this.Tail (scopeOf parameters) body)
        | WrapperCalling(wrapped, instanceTy) ->
            // Takes an environment, ignores it, and calls the function with the rest.
            if input.ContextParameters.ContainsKey wrapped then cannot $"a wrapper of '{wrapped}', which requires contexts"
            let parameterTys, resultTy = this.ParameterTypes parameterNames.Tail instanceTy true
            let environment = IrRecord [ "0", IrInt ]
            let parameters =
                parameterOf parameterNames.Head environment
                :: List.map2 (fun parameterName ty -> parameterOf parameterName (toIrType ty)) parameterNames.Tail parameterTys
            let call = mk (Call(callee wrapped instanceTy, parameters.Tail |> List.map AVar)) (toIrType resultTy)
            make parameters (toIrType resultTy) call
/// Lowers every definition that is not generic, the start function, and the specialisations and lifted functions they
/// need, and runs `finish` over each function (the emitter, which can still fail). A generic definition exists only as
/// its specialisations. What cannot be lowered or finished is in `Failures`, where it is written as far as is known.
let private lowerModuleOnThisThread (input: LowerInput) (finish: Function -> 'T) : Lowered<'T> =
    let definitions = input.Definitions |> Map.ofList
    let names = System.Collections.Generic.Dictionary<SpecKey, string>()
    let usedNames = System.Collections.Generic.HashSet<string>(input.Definitions |> Seq.map (fst >> sanitizeIdentifier))
    let pending = System.Collections.Generic.Queue<SpecKey * Map<int, Ty> * Lowering>()
    let counts = System.Collections.Generic.Dictionary<string, int>()
    // Every function lowered, by its key, with the specialisations it calls; or why it could not be.
    let results = System.Collections.Generic.Dictionary<SpecKey, Result<Function * 'T, string * SourceSpan option>>()
    let order = ResizeArray<SpecKey>()

    let lowerOne (key: SpecKey) (subst: Map<int, Ty>) (how: Lowering) (functionName: string) =
        let request (specKey: SpecKey) (bindings: Map<int, Ty>) (lowering: Lowering) =
            match names.TryGetValue specKey with
            | true, existing -> existing
            | _ ->
                let defName, typeArguments = specKey
                let count = match counts.TryGetValue defName with | true, n -> n | _ -> 0
                if count >= maxSpecialisations then cannot $"more than {maxSpecialisations} specialisations of '{defName}'"
                counts.[defName] <- count + 1
                // A lifted function of a definition that is not generic keeps its own name.
                let candidate =
                    if typeArguments.IsEmpty then sanitizeIdentifier defName
                    else
                        let baseName = specialisationName defName typeArguments
                        let mutable candidate = baseName
                        let mutable index = 1
                        while usedNames.Contains candidate do
                            candidate <- $"{baseName}_{index}"
                            index <- index + 1
                        usedNames.Add candidate |> ignore
                        candidate
                names.[specKey] <- candidate
                pending.Enqueue((specKey, bindings, lowering))
                candidate
        order.Add key
        let lowering = FunctionLowering(input, definitions, subst, key, request)
        let attempt () =
            try
                let func = lowering.Function how (fst key) functionName (snd key)
                Ok (func, finish func)
            with
            | CannotLower reason -> Error (reason, lowering.CurrentSpan)
            | Failure reason -> Error ($"internal error: {reason}", lowering.CurrentSpan)
            | ex -> Error ($"internal error: {ex.Message}", lowering.CurrentSpan)
        // A definition that holds errors deferred to run time may be what cannot be lowered because of them (a value of
        // no known type to interpolate, say): it reports them as soon as it is called instead.
        let messages =
            match definitions |> Map.tryFind (fst key) with
            | Some expr when how <> AsStart -> deferredMessages expr
            | _ -> []
        results.[key] <-
            match attempt () with
            | Error _ when not messages.IsEmpty ->
                let trap = FunctionLowering(input, definitions, subst, key, request)
                try
                    let func = trap.Trap (fst key) functionName (snd key) messages
                    Ok (func, finish func)
                with
                | CannotLower reason
                | Failure reason -> Error (reason, None)
            | result -> result

    let isGeneric (name: string) =
        match input.DefinitionTypes |> Map.tryFind name with
        | Some ty -> not (freeVars ty).IsEmpty
        | None -> false

    if not input.TopLevel.IsEmpty then
        lowerOne (startFunctionName, []) Map.empty AsStart startFunctionName
        while pending.Count > 0 do
            let key, bindings, how = pending.Dequeue()
            lowerOne key bindings how names.[key]

    // A generic definition is only lowered at the type arguments it is used at, and a lifted function where a closure
    // over it is made: what it captures is known there.
    for (name, _) in input.Definitions do
        if not (input.Lifted.ContainsKey name) && not (isGeneric name) then
            lowerOne (name, []) Map.empty AsDefined (sanitizeIdentifier name)
        while pending.Count > 0 do
            let key, bindings, how = pending.Dequeue()
            lowerOne key bindings how names.[key]

    // A definition may be generic only because an error left its type open. Unless it was specialised, it is lowered
    // with its type variables defaulted, so that it still exists, and reports its errors when it is called.
    for (name, expr) in input.Definitions do
        let specialised = names.Keys |> Seq.exists (fun (defName, _) -> defName = name)
        if not (input.Lifted.ContainsKey name) && isGeneric name && not specialised && not (deferredMessages expr).IsEmpty then
            lowerOne (name, []) Map.empty AsDefined (sanitizeIdentifier name)
            while pending.Count > 0 do
                let key, bindings, how = pending.Dequeue()
                lowerOne key bindings how names.[key]

    let lowered =
        order
        |> Seq.distinct
        |> Seq.choose (fun key -> match results.[key] with Ok lowered -> Some (key, lowered) | Error _ -> None)
        |> List.ofSeq
    { Roots =
        lowered
        |> List.filter (fun ((_, typeArguments), _) -> typeArguments.IsEmpty)
        |> List.map (fun ((name, _), lowered) -> name, lowered)
        |> Map.ofList
      Specialisations =
        lowered
        |> List.filter (fun ((_, typeArguments), _) -> not typeArguments.IsEmpty)
        |> List.groupBy (fun ((name, _), _) -> name)
        |> List.map (fun (name, items) -> name, items |> List.map snd)
        |> Map.ofList
      Failures =
        order
        |> Seq.distinct
        |> Seq.choose (fun ((name, _) as key) ->
            match results.[key] with
            | Error (reason, span) -> Some (name, reason, span |> Option.orElse (definitions |> Map.tryFind name |> Option.map _.Span))
            | Ok _ -> None)
        |> List.ofSeq }
/// Runs `action` on a thread with a stack large enough for the lowering, which recurses once per statement of a block
/// (each statement's lowering continues into the next) and so goes deep in a long function.
let private withLargeStack (action: unit -> 'T) : 'T =
    let mutable result = Unchecked.defaultof<'T>
    let mutable error: exn option = None
    let thread =
        System.Threading.Thread(
            (fun () ->
                try result <- action ()
                with ex -> error <- Some ex),
            256 * 1024 * 1024)
    thread.Start()
    thread.Join()
    match error with
    | Some ex -> raise ex
    | None -> result

let lowerModule (input: LowerInput) (finish: Function -> 'T) : Lowered<'T> =
    withLargeStack (fun () -> lowerModuleOnThisThread input finish)
