namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The well-known contexts: the prelude's contexts the compiler knows by name (see "Equality and well-known contexts"
/// in the docs' work in progress). Each has operators that are read through it, and an instance of the compiler's
/// own for the types whose structure gives one: `Eq`, for `==` and `!=`, and `Ord`, for `<`, `<=`, `>` and `>=`. The
/// set is closed. What is particular to each context's operators is in `EqualityOperators` and `OrderingOperators`;
/// what they share is here: how a call that requires one finds it, and how an operator on a type with a variable in
/// it is checked once the type is known.
///
/// A requirement on a type with more to it than a variable is one on each variable it compares values of: `==` on a
/// `list(a)` needs `Eq(a)`, and `<` on one `Ord(a)`.
module internal WellKnownContexts =
    /// A well-known context.
    type WellKnownContext =
        { /// The prelude's context: `Eq`.
          Context: Identifier
          /// Its one member: `==`.
          Member: Identifier
          /// The operators read through it: `==` and `!=`.
          Operators: string list
          /// The prelude function that is the member of the compiler's own instance, which works by the structure of
          /// the type it is specialised at: `__equals`.
          BuiltInFunction: Identifier
          /// Why values of a type have no instance of the compiler's own, if they have none.
          Lacks: Ty -> string option
          /// The prelude function each operator calls on two lists whose elements have an instance other than the
          /// compiler's own, which it is passed: `__listEquals` for `==` (and `!=`, which is `not` of it).
          ListHelpers: Map<string, Identifier>
          /// What the context does with two values, as messages say it: `compares`, `Comparing` and `compared`.
          Verb: string
          Gerund: string
          Participle: string }

    /// Where in a value of the type `ty` the first part that `kindOf` names is, if there is one: the parts of `ty` it
    /// is in, outermost first, each with whether it names several values (a list's elements), and what `kindOf` calls
    /// it, singular and plural.
    let rec private offendingPart (kindOf: Ty -> (string * string) option) (ty: Ty) : ((string * bool) list * (string * string)) option =
        let inPart (part: string) (plural: bool) (partTy: Ty) =
            offendingPart kindOf partTy |> Option.map (fun (rest, kind) -> (part, plural) :: rest, kind)
        match kindOf ty with
        | Some kind -> Some ([], kind)
        | None ->
            match ty with
            | TyFunc _
            | TyVar _
            | TyPrimitive _ -> None
            | TyAlias(_, _, underlying)
            | TyNominal(_, underlying, _) -> offendingPart kindOf underlying
            | TyRecord fields
            | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.tryPick (fun (name, fieldTy) -> inPart $"its field '{name}'" false fieldTy)
            | TyTag(name, payload) -> payload |> Option.bind (inPart $"the payload of '#{name}'" false)
            | TyApply("list", [ element ]) -> inPart "its elements" true element
            | TyApply(_, args) -> args |> List.tryPick (inPart "a value it holds" false)
            | TyUnion items -> items |> List.tryPick (offendingPart kindOf)

    /// Why values of the type `ty` have no `property`, if they have none: they are, or hold, a part `kindOf` names.
    let private lacking (property: string) (kindOf: Ty -> (string * string) option) (ty: Ty) : string option =
        offendingPart kindOf ty
        |> Option.map (fun (path, (singular, plural)) ->
            match path with
            | [] -> $"{plural} have no {property}"
            | [ part, false ] -> $"{part} is {singular}, and {plural} have no {property}"
            | [ part, true ] -> $"{part} are {plural}, and {plural} have no {property}"
            | (part, false) :: _ -> $"{part} holds {singular}, and {plural} have no {property}"
            | (part, true) :: _ -> $"{part} hold {plural}, and {plural} have no {property}")

    /// Whether `fields` are a tuple's: its positions, `0`, `1`, ...
    let isTuple (fields: Map<string, Ty>) =
        Set.ofSeq fields.Keys = Set.ofList (List.init fields.Count string)

    /// Whether `ty` is a built-in type, whose instances of the well-known contexts are always the compiler's own,
    /// never ones built from definitions attached to it.
    let isBuiltIn (ty: Ty) =
        match Unifier.unalias ty with
        | TyPrimitive _
        | TyApply("list", _) -> true
        | _ -> false

    /// `Eq`: every type that holds no function has the compiler's own, which compares by structure.
    let equality =
        { Context = "Eq"
          Member = "=="
          Operators = [ "=="; "!=" ]
          BuiltInFunction = "__equals"
          Lacks = lacking "equality" (function TyFunc _ -> Some ("a function", "functions") | _ -> None)
          ListHelpers = Map.ofList [ "==", "__listEquals"; "!=", "__listEquals" ]
          Verb = "compares"
          Gerund = "Comparing"
          Participle = "compared" }

    /// `Ord`: numbers, bools, strings, and tuples and lists of them, have the compiler's own, which orders by
    /// structure. Records and tags have none: their fields and tags have no order of their own in their type, so any
    /// derived order would be alphabetical by accident.
    let ordering =
        { Context = "Ord"
          Member = "compare"
          Operators = [ "<"; "<="; ">"; ">=" ]
          BuiltInFunction = "__compare"
          Lacks =
            lacking "order" (function
                | TyFunc _ -> Some ("a function", "functions")
                | TyRecord fields when not (isTuple fields) -> Some ("a record", "records")
                | TyOpenRecord _ -> Some ("a record", "records")
                | TyTag _
                | TyUnion _ -> Some ("a tag", "tags")
                | _ -> None)
          ListHelpers =
            Map.ofList [ "<", "__listLess"; "<=", "__listLessOrEqual"; ">", "__listGreater"; ">=", "__listGreaterOrEqual" ]
          Verb = "orders"
          Gerund = "Ordering"
          Participle = "ordered" }

    /// The well-known context named `ctxName`, if it is one.
    let tryFind (ctxName: Identifier) =
        [ equality; ordering ] |> List.tryFind (fun wellKnown -> wellKnown.Context = ctxName)

    /// The members of the compiler's own instance of `wellKnown`, as `InstanceSynthesized` has them.
    let builtInMembers (wellKnown: WellKnownContext) = [ wellKnown.Member, wellKnown.BuiltInFunction ]

    /// Whether the prelude's `wellKnown` is in scope: a module checked without the prelude has no context to require.
    let isDeclared (wellKnown: WellKnownContext) (state: InferState) =
        state.TypeDefs |> Map.tryFind wellKnown.Context |> Option.exists (fun info -> info.IsContext && info.Parameters.Length = 1)

    /// `ty` as a diagnostic writes it, with the variables the compiler made up named `a`, `b`, ...
    let display (ty: Ty) = Unifier.tyToDisplayString Ascii ty

    let error (span: SourceSpan) (message: string) = [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]

    /// The error for using `op` on values of the type `ty`, which have no instance of `wellKnown` for `reason`.
    let cannotUse (wellKnown: WellKnownContext) (op: string) (ty: Ty) (reason: string) =
        $"'{display ty}' cannot be {wellKnown.Participle} with {op}: {reason}"

    /// The type variables comparing two values of the type `ty` compares values of, each once: `a` for `list(a)`. A
    /// union's rest and an open record's row are not among them.
    let comparedVars (ty: Ty) : TyVar list =
        let rec collect ty =
            match ty with
            | TyVar v -> [ v ]
            | TyPrimitive _
            | TyFunc _ -> []
            | TyAlias(_, _, underlying)
            | TyNominal(_, underlying, _) -> collect underlying
            | TyRecord fields
            | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
            | TyApply(_, args) -> args |> List.collect collect
            | TyUnion items -> items |> List.filter (function TyVar _ -> false | _ -> true) |> List.collect collect
        collect ty |> List.distinctBy _.Id

    /// The error for using `op` on values of the type variable `v` of the function `defName`, whose type does not
    /// require `wellKnown` of it.
    let private undeclared (wellKnown: WellKnownContext) (op: string) (v: TyVar) (defName: Identifier option) =
        let tyText = display (TyVar v)
        let context = wellKnown.Context
        match defName with
        | Some name -> $"'{name}' {wellKnown.Verb} values of type '{tyText}' with {op}, so its type must require {context}({tyText}): add [{context}({tyText})] to the type of '{name}'"
        | None -> $"{wellKnown.Gerund} values of type '{tyText}' with {op} requires the context '{context}({tyText})', which is not available here"

    /// The instance of a well-known context of a type, in an environment, other than the compiler's own, if it has
    /// one: one in scope, or one built from the member attached to it. Building one can fail, when the attached
    /// member has the wrong type.
    type InstanceOf = TypeEnv -> InferState -> Ty -> Result<ContextInstance option * InferState, Diagnostic list>

    /// `state` with `expr`, the operator `op` of `wellKnown` on two lists of the type `ty` (given as `input`, as a
    /// call's arguments are typed), calling the prelude's list helper for it (see `ListHelpers`) with the instance its
    /// elements have, when `instanceOf` finds one other than the compiler's own: `==` on a `list($Email)` compares the
    /// elements with `` $Email.`==` ``, and `<` on a `list(a)` in a function that requires `Ord(a)` orders them with
    /// the `compare` it is passed. `None` for any other type, whose parts are all the compiler's own.
    let throughElements
        (wellKnown: WellKnownContext)
        (instanceOf: InstanceOf)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (op: string)
        (input: Ty)
        (ty: Ty)
        : Result<InferState option, Diagnostic list> =
        match Unifier.unalias ty, wellKnown.ListHelpers |> Map.tryFind op with
        | TyApply("list", [ element ]), Some helper when isDeclared wellKnown state ->
            instanceOf env state (resolveSoFar state element)
            |> Result.map (function
                | Some instance, next ->
                    Some
                        { next with
                            CalleeTypes = next.CalleeTypes |> Map.add expr.Id (TyFunc(input, TyPrimitive "bool"))
                            OperatorCalls = next.OperatorCalls |> Map.add expr.Id helper
                            ContextInstances = next.ContextInstances |> Map.add expr.Id [ Some instance ] }
                | None, _ -> None)
        | _ -> Ok None

    /// Whether `pending` is an operator a well-known context's `require` left to be checked, rather than a call.
    let isOperatorPending (pending: PendingContextInstance) =
        tryFind pending.Context |> Option.exists (fun wellKnown -> wellKnown.Operators |> List.contains pending.Callee)

    /// `state` with `expr`, an operator `op` of `wellKnown` in `env` on two values of the type `ty`, which has
    /// variables in it, left to be checked once the type is known (see `checkPending`): in a def that does not write
    /// its type, that is once the def is inferred, when a variable that turns out to be one of the def's own is one
    /// the def requires `wellKnown` of (see `ContextInference`). Anywhere else, it is once the module is inferred.
    let pendingOperator (wellKnown: WellKnownContext) (env: TypeEnv) (state: InferState) (expr: Expression) (op: string) (ty: Ty) : InferState =
        let def, annotatedDef =
            match state.ContextScope with
            | DefScope(name, true) -> name, None
            | DefScope(name, false) -> InterpolationHoles.moduleEnd, Some name
            | AmbientScope -> InterpolationHoles.moduleEnd, None
        let pending =
            { Def = def; Call = expr.Id; Callee = op; Index = 0; Count = 1
              Context = wellKnown.Context; ArgTys = [ ty ]; Span = expr.Span
              Errors = error expr.Span (undeclared wellKnown op (List.head (comparedVars ty)) annotatedDef); ChecksOnly = false }
        { state with
            PendingContextInstances = pending :: state.PendingContextInstances
            HoleScopes = state.HoleScopes |> Map.add expr.Id (env, annotatedDef) }

    /// The environment the operator `pending` is written in, and the variables of the type the def around it writes,
    /// if it is in one (`defScheme` gives a def's type).
    let operatorScope (defScheme: Identifier -> TypeScheme option) (state: InferState) (pending: PendingContextInstance) =
        let env, annotatedDef = state.HoleScopes |> Map.tryFind pending.Call |> Option.defaultValue (TypeEnv.empty, None)
        let declaredVars = annotatedDef |> Option.bind defScheme |> Option.map _.Quantified |> Option.defaultValue Set.empty
        env, annotatedDef, declaredVars

    /// Checks the operator `pending` of `wellKnown` now that it is on values of the type `argTys`, when that is not
    /// one of the variables of the def it is in. A type `wellKnown` lacks for is an error. A variable of the type the
    /// def around it writes (`defScheme` gives a def's type) must be one that def requires `wellKnown` of
    /// (`hasInstance`, in the environment the operator is written in). Any other variable is one of a local function
    /// generic in it: local functions require no contexts, and the backend works at each type the function is
    /// specialised at. The operator is the compiler's own, so it finds no instance; one that fails failed to check,
    /// and the def around it carries on.
    let checkPending
        (wellKnown: WellKnownContext)
        (hasInstance: TypeEnv -> Ty -> bool)
        (defScheme: Identifier -> TypeScheme option)
        (state: InferState)
        (pending: PendingContextInstance)
        (argTys: Ty list)
        : Result<ContextInstance option * InferState, Diagnostic list> =
        let env, annotatedDef, declaredVars = operatorScope defScheme state pending
        let ty = List.head argTys
        let failed (message: string) =
            Ok (None, { state with FailedExprs = (pending.Call, error pending.Span message) :: state.FailedExprs })
        match wellKnown.Lacks ty with
        | Some reason -> failed (cannotUse wellKnown pending.Callee ty reason)
        | None ->
            match comparedVars ty |> List.tryFind (fun v -> declaredVars.Contains v.Id && not (hasInstance env (TyVar v))) with
            | Some v -> failed (undeclared wellKnown pending.Callee v annotatedDef)
            | None -> Ok (None, state)

    /// `state` with each operator among `pendings` that found an instance `calls` takes (see `ContextInference`)
    /// calling a function, as an operator whose name is bound does (see `OperatorCalls`): one that takes two values of
    /// its type (`inputOf` is the input type of such a function) and gives a bool, by the name `calleeOf` gives.
    /// `keepInstance` says whether the function needs the instance passed, or is the instance's own member.
    let callOperators
        (calls: ContextInstance -> bool)
        (keepInstance: bool)
        (calleeOf: PendingContextInstance -> Identifier)
        (inputOf: Ty -> Ty)
        (pendings: PendingContextInstance list)
        (state: InferState)
        : InferState =
        pendings
        |> List.fold (fun (current: InferState) pending ->
            match current.ContextInstances |> Map.tryFind pending.Call with
            | Some [ Some instance ] when calls instance ->
                let calleeTy = TyFunc(inputOf (List.head pending.ArgTys), TyPrimitive "bool")
                { current with
                    ContextInstances = if keepInstance then current.ContextInstances else current.ContextInstances |> Map.remove pending.Call
                    CalleeTypes = current.CalleeTypes |> Map.add pending.Call calleeTy
                    OperatorCalls = current.OperatorCalls |> Map.add pending.Call (calleeOf pending) }
            | _ -> current) state

    /// Where a call to a function that requires a well-known context of the type `ty` finds it, when no instance is in
    /// scope.
    type CallInstance =
        /// Nowhere: values of the type have no instance of the compiler's own, for the reason given.
        | NoInstance of string
        /// From the member attached to the type.
        | Attached
        /// The compiler's own: the instance whose member is the context's `BuiltInFunction`.
        | BuiltIn
        /// Not known yet: the type has this variable in it, which has no instance in scope.
        | Open of TyVar

    /// The instance of `wellKnown` a call that requires it at `ty` finds. `hasAttached` says whether the type has the
    /// member attached, which is found ahead of the compiler's own, and `hasInstance` whether an instance of a type
    /// variable is in scope.
    let atCall (wellKnown: WellKnownContext) (hasAttached: bool) (hasInstance: Ty -> bool) (ty: Ty) : CallInstance =
        match wellKnown.Lacks ty with
        | _ when hasAttached -> Attached
        | Some reason -> NoInstance reason
        | None ->
            match comparedVars ty |> List.tryFind (fun v -> not (hasInstance (TyVar v))) with
            | Some v -> Open v
            | None -> BuiltIn

    /// The error for a call to `calleeName`, which requires `wellKnown` of the type `ty`, which has none for `reason`.
    let noInstanceAtCall (wellKnown: WellKnownContext) (calleeName: Identifier) (ty: Ty) (reason: string) =
        $"'{calleeName}' {wellKnown.Verb} values of type '{display ty}', which cannot be {wellKnown.Participle}: {reason}"

    /// The error for a call to `calleeName`, which requires `wellKnown` of the type `ty`, in the function `defName`
    /// (if it is in one), whose type does not require it of the variable `v` of `ty`.
    let openAtCall (wellKnown: WellKnownContext) (calleeName: Identifier) (ty: Ty) (v: TyVar) (defName: Identifier option) =
        let tys = Unifier.tysToDisplayStrings Ascii [ ty; TyVar v ]
        let context = wellKnown.Context
        let advice =
            match defName with
            | Some name -> $": add [{context}({tys.[1]})] to the type of '{name}'"
            | None -> ""
        let needs = if tys.[0] = tys.[1] then "is" else $"needs '{context}({tys.[1]})',"
        $"'{calleeName}' requires the context '{context}({tys.[0]})', which {needs} not available here{advice}"

    /// What becomes of `call`, a call to `calleeName` at `span` that requires `wellKnown` of `ty` and finds it as
    /// `instance`: the instances `found` so far for the `count` contexts the callee requires, with this one. None for
    /// an instance found as any other context's is: one attached to the type, or one of a type variable in a def that
    /// does not write its type. A type with a variable in it, `Eq(list(a))`, needs the context of the variable,
    /// `Eq(a)`: in a def that does not write its type the call is left until the def is inferred, which then requires
    /// it (see `ContextInference`). Anywhere else it is left until the module is inferred, in `env`, when the variable
    /// is a type the rest of the module settled, or one of the variables of the type the def around the call writes,
    /// which is an error.
    let resolveCall
        (wellKnown: WellKnownContext)
        (env: TypeEnv)
        (state: InferState)
        (call: Expression)
        (calleeName: Identifier)
        (span: SourceSpan)
        (found: ContextInstance option list)
        (count: int)
        (ty: Ty)
        (instance: CallInstance)
        : Result<ContextInstance option list * InferState, Diagnostic list> option =
        let pendingIn (def: Identifier) (v: TyVar) (annotatedDef: Identifier option) =
            { Def = def; Call = call.Id; Callee = calleeName; Index = found.Length; Count = count
              Context = wellKnown.Context; ArgTys = [ ty ]; Span = span
              Errors = error span (openAtCall wellKnown calleeName ty v annotatedDef); ChecksOnly = false }
        let later (pending: PendingContextInstance) (state: InferState) =
            Some (Ok (found @ [ None ], { state with PendingContextInstances = pending :: state.PendingContextInstances }))
        match instance, ty, state.ContextScope with
        | BuiltIn, _, _ -> Some (Ok (found @ [ Some (InstanceSynthesized (builtInMembers wellKnown)) ], state))
        | NoInstance reason, _, _ -> Some (Error (error span (noInstanceAtCall wellKnown calleeName ty reason)))
        | Attached, _, _
        | Open _, TyVar _, DefScope(_, true) -> None
        | Open v, _, DefScope(defName, true) -> later (pendingIn defName v None) state
        | Open v, _, scope ->
            let annotatedDef = match scope with DefScope(defName, _) -> Some defName | AmbientScope -> None
            later (pendingIn InterpolationHoles.moduleEnd v annotatedDef) { state with HoleScopes = state.HoleScopes |> Map.add call.Id (env, annotatedDef) }

    // Local functions.
    //
    // A local function requires no contexts: the backend compiles it again at each type it is called with, so its
    // operators are the compiler's own at that type. A call to one that compares or orders values of a type variable
    // it is generic in still needs the well-known context of the type that variable is at the call, or the backend
    // would be left to compare functions, or order records.

    /// The type variables among `quantified`, those a local function was just generalised in, that the operators and
    /// calls requiring `wellKnown` in `state` work on. Each is still pending, as none of its types was known.
    let usedByLocal (wellKnown: WellKnownContext) (state: InferState) (quantified: Set<int>) : Set<int> =
        state.PendingContextInstances
        |> List.filter (fun pending -> pending.Context = wellKnown.Context)
        |> List.collect (fun pending -> pending.ArgTys |> List.collect (resolveSoFar state >> comparedVars))
        |> List.map _.Id
        |> List.filter quantified.Contains
        |> Set.ofList

    /// Checks `call`, at `span` in `env`, to the local function `calleeName`, which needs `wellKnown` of the type `ty`
    /// there, and finds it as `instance`. A type with none is an error at the call. A type with a variable left in it is
    /// checked as a call to a top-level function would be, once the def around it is inferred (see `resolveCall`), but
    /// the call is passed nothing (`ChecksOnly`).
    let checkLocalCall
        (wellKnown: WellKnownContext)
        (env: TypeEnv)
        (state: InferState)
        (call: Expression)
        (calleeName: Identifier)
        (span: SourceSpan)
        (ty: Ty)
        (instance: CallInstance)
        : Result<InferState, Diagnostic list> =
        let pendingIn (def: Identifier) (v: TyVar) (annotatedDef: Identifier option) (state: InferState) =
            let pending =
                { Def = def; Call = call.Id; Callee = calleeName; Index = 0; Count = 1
                  Context = wellKnown.Context; ArgTys = [ ty ]; Span = span
                  Errors = error span (openAtCall wellKnown calleeName ty v annotatedDef); ChecksOnly = true }
            Ok { state with PendingContextInstances = pending :: state.PendingContextInstances }
        match instance, state.ContextScope with
        | NoInstance reason, _ -> Error (error span (noInstanceAtCall wellKnown calleeName ty reason))
        | BuiltIn, _
        | Attached, _ -> Ok state
        | Open v, DefScope(defName, true) -> pendingIn defName v None state
        | Open v, scope ->
            let annotatedDef = match scope with DefScope(defName, _) -> Some defName | AmbientScope -> None
            pendingIn InterpolationHoles.moduleEnd v annotatedDef { state with HoleScopes = state.HoleScopes |> Map.add call.Id (env, annotatedDef) }
