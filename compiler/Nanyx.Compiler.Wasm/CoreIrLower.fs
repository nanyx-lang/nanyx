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
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr

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
      /// The definitions that take context members as extra parameters, which the IR does not lower or call yet.
      RequiresContexts: Set<string> }

/// A function the IR lowered, and what it was finished into (see `lowerModule`).
type Lowered<'T> =
    { /// The definitions lowered as they are, by name.
      Roots: Map<string, Function * 'T>
      /// The specialisations of each generic definition, by the definition's name, in the order they were asked for.
      Specialisations: Map<string, (Function * 'T) list>
      /// Why each definition that is not among the roots was left to the old path.
      Fallbacks: Map<string, string> }

/// Something the IR cannot lower yet, and why.
exception CannotLower of string

let private cannot message = raise (CannotLower message)

/// A generic definition at some type arguments: what a specialisation is.
type private SpecKey = string * IrType list

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
    | TyOpenRecord(fields, rest) -> TyOpenRecord(fields |> Map.map (fun _ -> go), rest)
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
    | TyTag(name, payload), TyTag(name', payload') when name = name' ->
        match payload, payload' with
        | Some p, Some p' -> bindScheme bindings p p'
        | None, None -> bindings
        | _ -> cannot "a tag's payload differs between a definition and its use"
    | TyUnion items, TyUnion items' when items.Length = items'.Length ->
        // The tags of a union may come in another order: pair them by name.
        items
        |> List.fold (fun acc item ->
            match item with
            | TyTag(name, _) ->
                match items' |> List.tryFind (function TyTag(name', _) -> name' = name | _ -> false) with
                | Some item' -> bindScheme acc item item'
                | None -> cannot $"the tag #{name} is not in the type it is used at"
            | _ -> cannot "an open union in a generic definition") bindings
    | TyApply(name, args), TyApply(name', args') when name = name' && args.Length = args'.Length ->
        List.fold2 bindScheme bindings args args'
    | TyNominal(name, underlying, _), TyNominal(name', underlying', _) when name = name' ->
        bindScheme bindings underlying underlying'
    | _ -> cannot "a generic definition's type does not fit how it is used"

/// The IR type of `ty`, which must be concrete once `subst` is applied.
let rec private toIrType (ty: Ty) : IrType =
    match Unifier.unalias ty with
    | TyPrimitive "int" -> IrInt
    | TyPrimitive "bool" -> IrBool
    | TyPrimitive "unit" -> IrUnit
    | TyPrimitive "string" -> IrString
    | TyTag _
    | TyUnion _ as tags ->
        let rec members ty =
            match Unifier.unalias ty with
            | TyTag(name, payload) -> [ name, payload |> Option.map toIrType ]
            | TyUnion items -> items |> List.collect members
            | _ -> cannot "a union of something other than tags"
        IrTags(members tags |> List.distinct |> List.sortBy fst)
    | TyVar _ -> cannot "a type that is not known here"
    | TyPrimitive other -> cannot $"a value of type {other}"
    | TyFunc _ -> cannot "a function value"
    | TyRecord _
    | TyOpenRecord _ -> cannot "a record"
    | TyApply(name, _) -> cannot $"a value of type {name}"
    | TyNominal(name, _, _) -> cannot $"a value of the nominal type {name}"
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
        | _ -> IrType.show ty
    sanitizeIdentifier name + (typeArguments |> List.map (mangle >> sanitizeIdentifier >> sprintf "__%s") |> String.concat "")

/// Whether lowering `expr` stores to a local before its value is ready: a block with statements before its last
/// expression, or a `match` (which holds its scrutinees in locals). Operands evaluated before such an operand are
/// bound to temporaries, so that the emitter keeps them on the stack while the store happens (see `CoreIrEmit.fs`).
let rec private storesBeforeValue (expr: Expression) =
    match expr.Kind with
    | Block [ Kind (ExprStatement last) ] -> storesBeforeValue last
    | Block _
    | ExpressionKind.Match _ -> true
    | IfExpr(cond, _, _) -> storesBeforeValue cond
    | BinaryOp(_, left, right) -> storesBeforeValue left || storesBeforeValue right
    | FunctionCall(_, _, args) -> args |> List.exists storesBeforeValue
    | TupleExpr items -> items |> List.exists storesBeforeValue
    | TagExpr(_, Some payload) -> storesBeforeValue payload
    | _ -> false

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
      LivePayloads: Set<string> }

/// Lowers each function, asking for the specialisations its calls need through `request`.
type private FunctionLowering(input: LowerInput, definitions: Map<string, Expression>, subst: Map<int, Ty>, request: SpecKey -> Map<int, Ty> -> string) =
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

    let tyOf (expr: Expression) : IrType =
        match input.ExprTypes |> Map.tryFind expr.Id with
        | Some ty -> toIrType (substitute subst ty)
        | None -> cannot "an expression without a type"

    let isFunction (name: string) =
        match definitions.[name].Kind with
        | Lambda _ -> true
        | _ -> false

    let arityOf (name: string) =
        match definitions.[name].Kind with
        | Lambda(parameters, _) -> parameters.Length
        | _ -> 0

    /// The name to call `name` by at the instance type `instanceTy`: its own, or a specialisation's.
    let callee (name: string) (instanceTy: Ty) =
        if input.RequiresContexts.Contains name then cannot $"a call to '{name}', which requires contexts"
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
                    | None -> cannot $"a call to '{name}' that does not fix all of its type arguments")
            request (name, typeArguments) bindings

    let mk kind ty : Expr = { Kind = kind; Type = ty }

    let bindTemp (value: Expr) (k: Atom -> Expr) : Expr =
        let temp = newTemp value.Type
        let body = k (AVar temp)
        mk (Let(temp, value, body)) body.Type

    /// Lowers `expr` to a value that is not a `Let` (its own lets go around it), and passes that to `k`.
    member this.Complex (scope: Scope) (expr: Expression) (k: Expr -> Expr) : Expr =
        match expr.Kind with
        | UnitExpr -> k (mk (Atom AUnit) IrUnit)
        | LiteralExpr(IntLit value) -> k (mk (Atom(AInt(int64 value))) IrInt)
        | LiteralExpr(BoolLit value) -> k (mk (Atom(ABool value)) IrBool)
        | LiteralExpr(StringLit value) -> k (mk (Atom(AString value)) IrString)
        | LiteralExpr(FloatLit _) -> cannot "a float"
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
        | BinaryOp(op, left, right) ->
            if (wasmBinaryInstruction op).IsNone then cannot $"the operator {op}"
            for operand in [ left; right ] do
                match tyOf operand with
                | IrInt | IrBool | IrUnit -> ()
                | other -> cannot $"the operator {op} on a value of type {IrType.show other}"
            let ty = tyOf expr
            this.Atoms scope [ left; right ] (fun atoms -> k (mk (Prim(Binary op, atoms)) ty))
        | FunctionCall("not", _, [ operand ]) when not (scope.Vars.ContainsKey "not") && not (definitions.ContainsKey "not") ->
            this.Atoms scope [ operand ] (fun atoms -> k (mk (Prim(Not, atoms)) IrBool))
        | FunctionCall(name, _, args) ->
            if scope.Vars.ContainsKey name then cannot $"a call through the function value '{name}'"
            if not (definitions.ContainsKey name) then cannot $"a call to '{name}'"
            if not (isFunction name) then cannot $"a call to the value '{name}'"
            let flatArgs = flattenCallArgs args
            if flatArgs.Length <> arityOf name then cannot $"a call to '{name}' with its arguments in another shape"
            let instanceTy =
                match input.CalleeTypes |> Map.tryFind expr.Id with
                | Some ty -> ty
                | None -> cannot $"a call to '{name}' whose type is not known"
            let target = callee name instanceTy
            let ty = tyOf expr
            this.Atoms scope flatArgs (fun atoms -> k (mk (Call(target, atoms)) ty))
        | IfExpr(cond, ifTrue, ifFalse) ->
            let ty = tyOf expr
            this.Atoms scope [ cond ] (fun atoms ->
                k (mk (If(atoms.Head, this.Tail scope ifTrue, this.Tail scope ifFalse)) ty))
        | Block statements -> this.Block scope (statements |> List.map _.Kind) k
        | ExpressionKind.Match(values, arms) -> this.Match scope expr values arms k
        | Pipe _ -> cannot "a pipe"
        | InterpolatedString _ -> cannot "an interpolated string"
        | Lambda _ -> cannot "a lambda"
        | _ -> cannot "this kind of expression"

    /// Lowers `expr` in tail position: the whole of what is left to do.
    member this.Tail (scope: Scope) (expr: Expression) : Expr = this.Complex scope expr id

    /// Lowers `exprs` in order to atoms, binding each value that is not one to a temporary.
    member this.Atoms (scope: Scope) (exprs: Expression list) (k: Atom list -> Expr) : Expr =
        let rec go (acc: Atom list) (remaining: Expression list) =
            match remaining with
            | [] -> k (List.rev acc)
            | expr :: rest ->
                let storesLater = rest |> List.exists storesBeforeValue
                this.Complex scope expr (fun value ->
                    match value.Kind with
                    | Atom atom when not storesLater -> go (atom :: acc) rest
                    | _ -> bindTemp value (fun atom -> go (atom :: acc) rest))
        go [] exprs

    member this.Block (scope: Scope) (statements: StatementKind list) (k: Expr -> Expr) : Expr =
        match statements with
        | [] -> cannot "an empty block"
        | [ ExprStatement last ] -> this.Complex scope last k
        | DefStatement(_, _, _, Kind (Lambda _)) :: _ -> cannot "a local function"
        | DefStatement(_, name, _, rhs) :: rest ->
            // The local is named before its value is lowered, so that locals are declared in the order they are
            // written.
            let local = newVar (uniqueName name) Local (tyOf rhs)
            this.Complex scope rhs (fun value ->
                let body = this.Block { scope with Vars = scope.Vars |> Map.add name local } rest k
                mk (Let(local, value, body)) body.Type)
        | ExprStatement first :: rest ->
            this.Complex scope first (fun value ->
                let body = this.Block scope rest k
                mk (Do(value, body)) body.Type)
        | UseStatement _ :: _ -> cannot "a `use` statement"
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
                | LiteralPattern(BoolLit value) -> PBool value
                | TagPattern(name, payload) ->
                    let payloadTy =
                        match valueTy with
                        | IrTags tags ->
                            match tags |> List.tryFind (fst >> (=) name) with
                            | Some (_, payloadTy) -> payloadTy
                            | None -> cannot $"the pattern #{name}, which is not among the value's tags"
                        | _ -> cannot $"the pattern #{name} on a value that is not a tag"
                    match payload, payloadTy with
                    | Some inner, Some innerTy -> PTag(name, Some (lowerPattern false scrutinee innerTy inner))
                    | None, _ -> PTag(name, None)
                    | Some _, None -> cannot $"a payload pattern on #{name}, which has none"
                | _ -> cannot "this kind of pattern"
            let loweredPatterns = List.map2 (fun scrutinee pattern -> lowerPattern true scrutinee scrutinee.Type pattern) scrutinees patterns
            let bodyScope = { armScope with Vars = armVars; LivePayloads = Set.union armScope.LivePayloads payloads }
            { Patterns = loweredPatterns; Body = this.Tail bodyScope body }
        // The scrutinees are evaluated in order, each into its local, before any arm is tested.
        let rec bindScrutinees (remaining: (Var * Expression) list) =
            match remaining with
            | [] -> k (mk (Match(scrutinees, arms |> List.map lowerArm)) ty)
            | (scrutinee, value) :: rest ->
                this.Complex scope value (fun lowered ->
                    let body = bindScrutinees rest
                    mk (Let(scrutinee, lowered, body)) body.Type)
        bindScrutinees (List.zip scrutinees values)

    /// Lowers the definition `name` to a function called `functionName`.
    member this.Function (name: string) (functionName: string) (typeArguments: IrType list) : Function =
        let expr = definitions.[name]
        let parameterNames, body = parametersAndBody expr
        let defTy =
            match input.ExprTypes |> Map.tryFind expr.Id with
            | Some ty -> substitute subst ty
            | None -> cannot "a definition without a type"
        let parameterTys, resultTy =
            match parameterNames, Unifier.unalias defTy with
            | [], TyFunc(_, result) when (match expr.Kind with Lambda _ -> true | _ -> false) -> [], result
            | [], _ -> [], defTy
            | [ _ ], TyFunc(input, result) -> [ input ], result
            | several, TyFunc(input, result) ->
                match Unifier.unalias input with
                | TyRecord fields when fields.Count = several.Length ->
                    List.init several.Length (fun index ->
                        match fields |> Map.tryFind (string index) with
                        | Some ty -> ty
                        | None -> cannot "a function whose parameters are named fields"), result
                | _ -> cannot "a function whose parameters are not a tuple"
            | _ -> cannot "a definition whose type is not a function"
        let parameters = List.map2 (fun parameterName ty -> newVar (uniqueName parameterName) Param (toIrType ty)) parameterNames parameterTys
        let scope =
            { Vars = List.zip parameterNames parameters |> Map.ofList
              MatchDepth = 0
              LivePayloads = Set.empty }
        { Name = functionName
          Source = name
          TypeArguments = typeArguments
          Params = parameters
          Result = toIrType resultTy
          Body = this.Tail scope body }

/// Lowers every definition the IR can, with the specialisations they need, and runs `finish` over each function
/// (the emitter, which can still fail). A function is kept only when it and every specialisation it calls,
/// directly or not, lowered and finished: one that calls a specialisation that did not is left to the old path, as is
/// any generic definition (the old path still compiles it, unspecialised, for the callers it compiles).
let lowerModule (input: LowerInput) (finish: Function -> 'T) : Lowered<'T> =
    let definitions = input.Definitions |> Map.ofList
    let names = System.Collections.Generic.Dictionary<SpecKey, string>()
    let usedNames = System.Collections.Generic.HashSet<string>(input.Definitions |> Seq.map (fst >> sanitizeIdentifier))
    let pending = System.Collections.Generic.Queue<SpecKey * Map<int, Ty>>()
    let counts = System.Collections.Generic.Dictionary<string, int>()
    // Every function lowered, by its key, with the specialisations it calls; or why it could not be.
    let results = System.Collections.Generic.Dictionary<SpecKey, Result<Function * 'T * Set<SpecKey>, string>>()
    let order = ResizeArray<SpecKey>()

    let lowerOne (key: SpecKey) (subst: Map<int, Ty>) (functionName: string) =
        let calls = System.Collections.Generic.HashSet<SpecKey>()
        let request (specKey: SpecKey) (bindings: Map<int, Ty>) =
            calls.Add specKey |> ignore
            match names.TryGetValue specKey with
            | true, existing -> existing
            | _ ->
                let defName = fst specKey
                let count = match counts.TryGetValue defName with | true, n -> n | _ -> 0
                if count >= maxSpecialisations then cannot $"more than {maxSpecialisations} specialisations of '{defName}'"
                counts.[defName] <- count + 1
                let baseName = specialisationName defName (snd specKey)
                let mutable candidate = baseName
                let mutable index = 1
                while usedNames.Contains candidate do
                    candidate <- $"{baseName}_{index}"
                    index <- index + 1
                usedNames.Add candidate |> ignore
                names.[specKey] <- candidate
                pending.Enqueue((specKey, bindings))
                candidate
        order.Add key
        results.[key] <-
            try
                let lowering = FunctionLowering(input, definitions, subst, request)
                let func = lowering.Function (fst key) functionName (snd key)
                Ok (func, finish func, Set.ofSeq calls)
            with
            | CannotLower reason -> Error reason
            | Failure reason -> Error reason
            // A bug in the lowering or the emitter costs the function its trip through the IR, not the compile.
            | ex -> Error $"internal error: {ex.Message}"

    let isGeneric (name: string) =
        match input.DefinitionTypes |> Map.tryFind name with
        | Some ty -> not (freeVars ty).IsEmpty
        | None -> false

    for (name, _) in input.Definitions do
        if input.RequiresContexts.Contains name then
            results.[(name, [])] <- Error "it requires contexts"
            order.Add((name, []))
        elif isGeneric name then
            results.[(name, [])] <- Error "it is generic: each instantiation is lowered where it is called"
            order.Add((name, []))
        else
            lowerOne (name, []) Map.empty (sanitizeIdentifier name)
        while pending.Count > 0 do
            let key, bindings = pending.Dequeue()
            lowerOne key bindings names.[key]

    // Keep what lowered and finished and calls only specialisations that are kept, until nothing changes.
    let mutable kept =
        results
        |> Seq.choose (fun (KeyValue(key, result)) -> match result with Ok _ -> Some key | Error _ -> None)
        |> Set.ofSeq
    let mutable changed = true
    while changed do
        let next =
            kept
            |> Set.filter (fun key ->
                match results.[key] with
                | Ok (_, _, calls) -> calls |> Set.forall kept.Contains
                | Error _ -> false)
        changed <- next.Count <> kept.Count
        kept <- next

    let fallbackReason (key: SpecKey) =
        match results.[key] with
        | Error reason -> reason
        | Ok (_, _, calls) ->
            let missing = calls |> Seq.find (kept.Contains >> not)
            $"it calls '{fst missing}' at a type the IR could not lower it at"

    let keptFunctions =
        order
        |> Seq.distinct
        |> Seq.filter kept.Contains
        |> Seq.map (fun key -> match results.[key] with Ok (func, finished, _) -> key, (func, finished) | Error _ -> failwith "unreachable")
        |> List.ofSeq
    { Roots =
        keptFunctions
        |> List.filter (fun ((_, typeArguments), _) -> typeArguments.IsEmpty)
        |> List.map (fun ((name, _), lowered) -> name, lowered)
        |> Map.ofList
      Specialisations =
        keptFunctions
        |> List.filter (fun ((_, typeArguments), _) -> not typeArguments.IsEmpty)
        |> List.groupBy (fun ((name, _), _) -> name)
        |> List.map (fun (name, items) -> name, items |> List.map snd)
        |> Map.ofList
      Fallbacks =
        order
        |> Seq.distinct
        |> Seq.filter (fun ((_, typeArguments) as key) -> typeArguments.IsEmpty && not (kept.Contains key))
        |> Seq.map (fun ((name, _) as key) -> name, fallbackReason key)
        |> Map.ofSeq }
