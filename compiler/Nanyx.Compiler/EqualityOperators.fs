namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `==` and `!=` through the well-known context `Eq` (see "Equality and well-known contexts" in the docs' work in
/// progress). Comparing two values of the type `T` needs an `Eq(T)`. Every type that holds no function has the
/// compiler's own, which compares by structure, so only two things are checked: a function is never compared, and a
/// function that compares values of one of its own type variables requires `Eq` of it. A type with an `==` attached
/// is compared by calling it, and a structure with a part that has an `==` of its own by a definition derived from
/// its parts (see `StructuralInstances`). One that does not write its
/// type has the requirement inferred, as it is for a call to a function that requires a context (see
/// `ContextInference`), and its `==` then calls the member of the instance it is passed.
///
/// A requirement on a type with more to it than a variable is one on each variable it compares values of: `==` on a
/// `list(a)` needs `Eq(a)`.
module internal EqualityOperators =
    /// The prelude's context.
    let context = "Eq"

    /// The context's one member, and the operator that reads it. `!=` is not a member: it is always `not (a == b)`.
    let equals = "=="

    let notEquals = "!="

    /// The prelude function that compares two values by their structure: the member of the compiler's own instance,
    /// where one is passed to a function that requires `Eq`.
    let builtInFunction = "__equals"

    /// The prelude function the derived `==` of a list calls, with the two lists and what compares two elements.
    let listFunction = "__listEquals"

    /// The members of the compiler's own instance, as `InstanceSynthesized` has them.
    let builtInMembers = [ equals, builtInFunction ]

    /// Whether `op` is `==` or `!=`.
    let isOperator (op: string) = op = equals || op = notEquals

    /// Whether comparing values of the types `left` and `right` (resolved as far as they can be) joins them into one
    /// union rather than making them equal: when either is a tag or a union of tags, so that `o == #none` compares
    /// at `#none | r` and leaves `o` open to other tags.
    let joinsOperands (left: Ty) (right: Ty) =
        let isTags (ty: Ty) =
            match Unifier.unalias ty with
            | TyTag _
            | TyUnion _ -> true
            | _ -> false
        isTags left || isTags right

    /// Whether the prelude's `Eq` is in scope: a module checked without the prelude has no context to require.
    let private isDeclared (state: InferState) =
        state.TypeDefs |> Map.tryFind context |> Option.exists (fun info -> info.IsContext && info.Parameters.Length = 1)

    /// `ty` as a diagnostic writes it, with the variables the compiler made up named `a`, `b`, ...
    let private display (ty: Ty) = Unifier.tyToDisplayString Ascii ty

    /// The part of a value of type `ty` that is a function, if it has one: nothing for a function itself, else the
    /// part of `ty` it is, or is in, and whether that names several values (a list's elements).
    let rec private functionIn (ty: Ty) : (string * bool) list option =
        let inPart (part: string) (plural: bool) (partTy: Ty) = functionIn partTy |> Option.map (fun rest -> (part, plural) :: rest)
        match ty with
        | TyFunc _ -> Some []
        | TyVar _
        | TyPrimitive _ -> None
        | TyAlias(_, _, underlying)
        | TyNominal(_, underlying, _) -> functionIn underlying
        | TyRecord fields
        | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.tryPick (fun (name, fieldTy) -> inPart $"its field '{name}'" false fieldTy)
        | TyTag(name, payload) -> payload |> Option.bind (inPart $"the payload of '#{name}'" false)
        | TyApply("list", [ element ]) -> inPart "its elements" true element
        | TyApply(_, args) -> args |> List.tryPick (inPart "a value it holds" false)
        | TyUnion items -> items |> List.tryPick functionIn

    /// Why values of the type `ty` have no equality, if they have none: a function has none, and so neither has
    /// anything that holds one.
    let private noEquality (ty: Ty) : string option =
        functionIn ty
        |> Option.map (function
            | [] -> "functions have no equality"
            | [ part, false ] -> $"{part} is a function, and functions have no equality"
            | [ part, true ] -> $"{part} are functions, and functions have no equality"
            | (part, false) :: _ -> $"{part} holds a function, and functions have no equality"
            | (part, true) :: _ -> $"{part} hold functions, and functions have no equality")

    /// The error for comparing values of the type `ty`, which have no equality for `reason`, with `op`.
    let private notComparable (op: string) (ty: Ty) (reason: string) = $"'{display ty}' cannot be compared with {op}: {reason}"

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

    /// The error for comparing, with `op`, values of the type variable `v` of the function `defName`, whose type does
    /// not require `Eq` of it.
    let private undeclared (op: string) (v: TyVar) (defName: Identifier option) =
        let tyText = display (TyVar v)
        match defName with
        | Some name -> $"'{name}' compares values of type '{tyText}' with {op}, so its type must require {context}({tyText}): add [{context}({tyText})] to the type of '{name}'"
        | None -> $"Comparing values of type '{tyText}' with {op} requires the context '{context}({tyText})', which is not available here"

    let private error (span: SourceSpan) (message: string) = [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]

    /// Checks `expr`, a `==` or `!=` (`op`) in `env` that compares two values of the type `operandTy` and calls no
    /// definition. A function is an error there. A type with a variable left in it is checked once it is known (see
    /// `resolvePending`): in a def that does not write its type, that is once the def is inferred, when a variable
    /// that turns out to be one of the def's own is one the def requires `Eq` of (see `ContextInference`). Anywhere
    /// else, it is once the module is inferred. The prelude's `builtInFunction` is the comparison itself, of any type.
    ///
    /// A structure with a part that has an `==` of its own (`comparesParts`) is compared by a derived definition
    /// (see `StructuralInstances`), which `derive` makes the comparison call once nothing of the type is left open.
    let require
        (comparesParts: Ty -> bool)
        (derive: Ty -> InferState -> InferState option)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (op: string)
        (operandTy: Ty)
        : Result<InferState, Diagnostic list> =
        let ty = resolveSoFar state operandTy
        match noEquality ty, state.ContextScope with
        | Some reason, _ -> Error (error expr.Span (notComparable op ty reason))
        | None, DefScope(name, false) when name = builtInFunction -> Ok state
        | None, _ when not (isDeclared state) -> Ok state
        | None, _ when (TypeEnv.freeTyVars ty).IsEmpty -> Ok (derive ty state |> Option.defaultValue state)
        | None, _ when (comparedVars ty).IsEmpty && not (comparesParts ty) -> Ok state
        | None, scope ->
            let def, annotatedDef =
                match scope with
                | DefScope(name, true) -> name, None
                | DefScope(name, false) -> InterpolationHoles.moduleEnd, Some name
                | AmbientScope -> InterpolationHoles.moduleEnd, None
            let pending =
                { Def = def; Call = expr.Id; Callee = op; Index = 0; Count = 1
                  Context = context; ArgTys = [ ty ]; Span = expr.Span
                  Errors = comparedVars ty |> List.tryHead |> Option.map (fun v -> error expr.Span (undeclared op v annotatedDef)) |> Option.defaultValue []; ChecksOnly = false }
            Ok { state with
                    PendingContextInstances = pending :: state.PendingContextInstances
                    HoleScopes = state.HoleScopes |> Map.add expr.Id (env, annotatedDef) }

    /// Whether `pending` is a comparison `require` left to be checked, rather than a call.
    let isPending (pending: PendingContextInstance) = pending.Context = context && isOperator pending.Callee

    /// Checks the comparison `pending` now that it compares values of the type `argTys`, when that is not one of the
    /// variables of the def it is in. A type that holds a function has no equality. A variable of the type the def
    /// around it writes (`defScheme` gives a def's type) must be one that def requires `Eq` of (`hasInstance`, in the
    /// environment the comparison is written in). Any other variable is one of a local function generic in it: local
    /// functions require no contexts, and the backend compares at each type the function is specialised at. The
    /// comparison is the compiler's own, so it finds no instance, unless `derive` makes it call a derived definition
    /// (see `require`); one that fails failed to check, and the def around it carries on.
    let resolvePending
        (hasInstance: TypeEnv -> Ty -> bool)
        (derive: Ty -> InferState option)
        (defScheme: Identifier -> TypeScheme option)
        (state: InferState)
        (pending: PendingContextInstance)
        (argTys: Ty list)
        : Result<ContextInstance option * InferState, Diagnostic list> =
        let env, annotatedDef = state.HoleScopes |> Map.tryFind pending.Call |> Option.defaultValue (TypeEnv.empty, None)
        let declaredVars = annotatedDef |> Option.bind defScheme |> Option.map _.Quantified |> Option.defaultValue Set.empty
        let ty = List.head argTys
        let failed (message: string) =
            Ok (None, { state with FailedExprs = (pending.Call, error pending.Span message) :: state.FailedExprs })
        match noEquality ty with
        | Some reason -> failed (notComparable pending.Callee ty reason)
        | None ->
            match comparedVars ty |> List.tryFind (fun v -> declaredVars.Contains v.Id && not (hasInstance env (TyVar v))) with
            | Some v -> failed (undeclared pending.Callee v annotatedDef)
            | None -> Ok (None, derive ty |> Option.defaultValue state)

    /// `state` with each comparison among `pendings` that found the instance its def requires (see `ContextInference`)
    /// calling that instance's `==`, as an operator whose name is bound does (see `OperatorCalls`). `inputOf` is the
    /// input type of a function that takes two values of a type.
    let callMembers (inputOf: Ty -> Ty) (pendings: PendingContextInstance list) (state: InferState) : InferState =
        pendings
        |> List.fold (fun (current: InferState) pending ->
            match current.ContextInstances |> Map.tryFind pending.Call with
            | Some [ Some (InstanceInScope _) ] ->
                let memberTy = TyFunc(inputOf (List.head pending.ArgTys), TyPrimitive "bool")
                { current with
                    ContextInstances = current.ContextInstances |> Map.remove pending.Call
                    CalleeTypes = current.CalleeTypes |> Map.add pending.Call memberTy
                    OperatorCalls = current.OperatorCalls |> Set.add pending.Call }
            | _ -> current) state

    /// Where a call to a function that requires `Eq` of the type `ty` finds it, when no instance is in scope.
    type CallInstance =
        /// Nowhere: values of the type have no equality, for the reason given.
        | NoInstance of string
        /// From the `==` attached to the type.
        | Attached
        /// The compiler's own: the instance whose `==` is `builtInFunction`.
        | BuiltIn
        /// Not known yet: the type compares values of this variable, which has no instance in scope.
        | Open of TyVar

    /// The instance a call that requires `Eq(ty)` finds. `hasAttached` says whether the type has a `==` attached, and
    /// `hasInstance` whether an `Eq` of a type variable is in scope.
    let atCall (hasAttached: bool) (hasInstance: Ty -> bool) (ty: Ty) : CallInstance =
        match noEquality ty with
        | Some reason -> NoInstance reason
        | None when hasAttached -> Attached
        | None ->
            match comparedVars ty |> List.tryFind (fun v -> not (hasInstance (TyVar v))) with
            | Some v -> Open v
            | None -> BuiltIn

    /// The error for a call to `calleeName`, which requires `Eq` of the type `ty`, which has none for `reason`.
    let noInstanceAtCall (calleeName: Identifier) (ty: Ty) (reason: string) =
        $"'{calleeName}' compares values of type '{display ty}', which cannot be compared: {reason}"

    /// The error for a call to `calleeName`, which requires `Eq` of the type `ty`, in the function `defName` (if it is
    /// in one), whose type does not require `Eq` of the variable `v` of `ty`.
    let private openAtCall (calleeName: Identifier) (ty: Ty) (v: TyVar) (defName: Identifier option) =
        let tys = Unifier.tysToDisplayStrings Ascii [ ty; TyVar v ]
        let advice =
            match defName with
            | Some name -> $": add [{context}({tys.[1]})] to the type of '{name}'"
            | None -> ""
        let needs = if tys.[0] = tys.[1] then "is" else $"needs '{context}({tys.[1]})',"
        $"'{calleeName}' requires the context '{context}({tys.[0]})', which {needs} not available here{advice}"

    /// What becomes of `call`, a call to `calleeName` at `span` that requires `Eq(ty)` and finds it as `instance`: the
    /// instances `found` so far for the `count` contexts the callee requires, with this one. None for an instance
    /// found as any other context's is: one attached to the type, or one of a type variable in a def that does not
    /// write its type. `Eq` of a type with a variable in it, `Eq(list(a))`, needs `Eq(a)`: in a def that does not
    /// write its type the call is left until the def is inferred, which then requires it (see `ContextInference`).
    /// Anywhere else it is left until the module is inferred, in `env`, when the variable is a type the rest of the
    /// module settled, or one of the variables of the type the def around the call writes, which is an error.
    let resolveCall
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
              Context = context; ArgTys = [ ty ]; Span = span; Errors = error span (openAtCall calleeName ty v annotatedDef)
              ChecksOnly = false }
        let later (pending: PendingContextInstance) (state: InferState) =
            Some (Ok (found @ [ None ], { state with PendingContextInstances = pending :: state.PendingContextInstances }))
        match instance, ty, state.ContextScope with
        | BuiltIn, _, _ -> Some (Ok (found @ [ Some (InstanceSynthesized builtInMembers) ], state))
        | NoInstance reason, _, _ -> Some (Error (error span (noInstanceAtCall calleeName ty reason)))
        | Attached, _, _
        | Open _, TyVar _, DefScope(_, true) -> None
        | Open v, _, DefScope(defName, true) -> later (pendingIn defName v None) state
        | Open v, _, scope ->
            let annotatedDef = match scope with DefScope(defName, _) -> Some defName | AmbientScope -> None
            later (pendingIn InterpolationHoles.moduleEnd v annotatedDef) { state with HoleScopes = state.HoleScopes |> Map.add call.Id (env, annotatedDef) }

    // Local functions.
    //
    // A local function requires no contexts: the backend compiles it again at each type it is called with, so its
    // comparisons are the compiler's own at that type. A call to one that compares values of a type variable it is
    // generic in still needs an `Eq` of the type that variable is at the call, or the backend would be left to compare
    // functions.

    /// The type variables among `quantified`, those a local function was just generalised in, that the comparisons and
    /// calls requiring `Eq` in `state` compare values of. Each is still pending, as none of its types was known.
    let comparedByLocal (state: InferState) (quantified: Set<int>) : Set<int> =
        state.PendingContextInstances
        |> List.filter (fun pending -> pending.Context = context)
        |> List.collect (fun pending -> pending.ArgTys |> List.collect (resolveSoFar state >> comparedVars))
        |> List.map _.Id
        |> List.filter quantified.Contains
        |> Set.ofList

    /// Checks `call`, at `span` in `env`, to the local function `calleeName`, which compares values of the type `ty`
    /// there, and finds `Eq(ty)` as `instance`. A type with no equality is an error at the call. A type with a
    /// variable left in it is checked as a call to a top-level function would be, once the def around it is inferred
    /// (see `resolveCall`), but the call is passed nothing (`ChecksOnly`).
    let checkLocalCall
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
                  Context = context; ArgTys = [ ty ]; Span = span; Errors = error span (openAtCall calleeName ty v annotatedDef)
                  ChecksOnly = true }
            Ok { state with PendingContextInstances = pending :: state.PendingContextInstances }
        match instance, state.ContextScope with
        | NoInstance reason, _ -> Error (error span (noInstanceAtCall calleeName ty reason))
        | BuiltIn, _
        | Attached, _ -> Ok state
        | Open v, DefScope(defName, true) -> pendingIn defName v None state
        | Open v, scope ->
            let annotatedDef = match scope with DefScope(defName, _) -> Some defName | AmbientScope -> None
            pendingIn InterpolationHoles.moduleEnd v annotatedDef { state with HoleScopes = state.HoleScopes |> Map.add call.Id (env, annotatedDef) }

