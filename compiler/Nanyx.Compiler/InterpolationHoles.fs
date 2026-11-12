namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// String interpolation through the well-known context `ToString` (see "Printing values" in the docs' work in
/// progress). Desugaring makes each hole `"{x}"` a call `__interpolate(x)` to the prelude function of that name, which
/// requires `[ToString(a)]` at its argument's type. So a hole finds its instance as any call does: one loaded with
/// `use`, the one the enclosing function's type declares, or one synthesized from an attached `toString`. A function
/// that does not write its type and interpolates one of its own type variables requires `ToString` of it.
///
/// The built-in types have the compiler's own instance (`InstanceBuiltIn`), which the backend writes itself: int,
/// float, bool, string, and tags without a payload. The prelude's `int.toString` and the others are written with a
/// hole, which is how they avoid calling themselves. A function that requires `ToString` of a tag is passed the
/// prelude's `__toString` (`builtInFunction`), which writes it the same way. A tag with a payload, a tuple, a record
/// and a list have one the compiler derives from their parts (see `StructuralInstances`).
module InterpolationHoles =
    /// The context a hole requires of its value's type.
    let context = "ToString"

    /// The member of `context`.
    let memberName = "toString"

    /// The prelude function each hole calls.
    let holeFunction = "__interpolate"

    /// The prelude function the derived `toString` of a list calls, with the list and what writes an element.
    let listFunction = "__listToString"

    /// Whether a call to `calleeName` is a hole's.
    let isHole (calleeName: Identifier) = calleeName = holeFunction

    /// Whether `ty`, resolved as far as it can be, has the compiler's own `ToString`. A tag union open to more tags has
    /// it when the tags it names do; the backend prints the ones it holds at each type it is used at. A value that
    /// failed to check has it, so that the hole is not an error too.
    let hasBuiltInInstance (ty: Ty) =
        let isPlainTag (item: Ty) =
            match Unifier.unalias item with
            | TyTag(_, None)
            | TyVar _ -> true
            | _ -> false
        match Unifier.unalias ty with
        | TyPrimitive("int" | "float" | "bool" | "string" | "?error") -> true
        | TyTag(_, None) -> true
        | TyUnion items -> items |> List.forall isPlainTag
        | _ -> false

    /// The prelude function that writes a value with the compiler's own `ToString`, through a hole on its type
    /// variable: the member of the compiler's own instance, where one is passed to a function that requires
    /// `ToString` of a tag. The backend writes the hole at each type the function is specialised at.
    let builtInFunction = "__toString"

    /// The members of the compiler's own instance, as `InstanceSynthesized` has them.
    let builtInMembers = [ "toString", builtInFunction ]

    /// Whether `ty` is a tag, or a union of tags. Tags have no name a `toString` can be attached to.
    let private isTags (ty: Ty) =
        match Unifier.unalias ty with
        | TyTag _
        | TyUnion _ -> true
        | _ -> false

    /// Whether a call that requires `ctxName` at `argTys`, with no instance in scope, is passed the compiler's own
    /// `ToString`, whose member is `builtInFunction`: a tag without a payload, or a union of them, when `env` has the
    /// prelude. Every other type the compiler has its own for has an attached `toString` to synthesize one from.
    let passesBuiltIn (env: TypeEnv) (ctxName: Identifier) (argTys: Ty list) =
        match argTys with
        | [ ty ] -> ctxName = context && isTags ty && hasBuiltInInstance ty && (TypeEnv.tryFind builtInFunction env).IsSome
        | _ -> false

    /// `ty` as a diagnostic writes it, with the variables the compiler made up named `a`, `b`, ...
    let private display (ty: Ty) = Unifier.tyToDisplayString Ascii ty

    /// Who requires the context, as a diagnostic about a call to `calleeName` with the context's arguments `argTys`
    /// names it: the function, or the hole.
    let requirer (calleeName: Identifier) (argTys: Ty list) =
        match argTys with
        | [ ty ] when isHole calleeName -> $"Interpolating a value of type '{display ty}'"
        | _ -> $"'{calleeName}'"

    /// Why a value of the type `ty`, which has no `ToString`, cannot be interpolated, and what to do instead.
    let noInstance (ty: Ty) =
        let tyText = display ty
        match ty, Unifier.unalias ty with
        | (TyAlias(name, _, _) | TyNominal({ Name = name }, _, _)), _ ->
            $"'{tyText}' cannot be interpolated because it has no toString: attach '{name}.toString', or load an instance of `ToString({tyText})` with `use`"
        | _, TyFunc _ -> $"'{tyText}' cannot be interpolated: it is a function, and functions have no toString"
        | _ -> $"'{tyText}' cannot be interpolated because it has no toString: turn it into a string first, or print it with `dbg`"

    /// Why a call to `calleeName` that requires `ToString` at `argTys` has no instance: `held`, the value or a part of
    /// it, is a function. A hole says the value cannot be interpolated; another call, such as `println(f)`, names itself.
    let holdsFunction (calleeName: Identifier) (argTys: Ty list) (held: Ty) =
        match argTys with
        | [ ty ] when not (isHole calleeName) ->
            let heldText = display held
            let part = if Unifier.unalias ty = Unifier.unalias held then "" else " in it"
            $"'{calleeName}' requires the context 'ToString({display ty})', and '{heldText}'{part} is a function: functions have no toString"
        | _ -> noInstance held

    /// The error for a hole of the type `ty`, one of the type variables of the function `defName` (if it is in one),
    /// whose type does not require `ToString` of it.
    let undeclared (ty: Ty) (defName: Identifier option) =
        let tyText = display ty
        let advice =
            match defName with
            | Some name -> $": add [ToString({tyText})] to the type of '{name}'"
            | None -> ""
        $"Interpolating a value of type '{tyText}' requires the context 'ToString({tyText})', which is not available here{advice}"

    /// The error for a call to `calleeName` that requires `ToString` of `ty`, which holds a union open to tags the
    /// caller of `defName` (the function around the call, if it is in one) chooses.
    let openToCaller (calleeName: Identifier) (ty: Ty) (defName: Identifier option) =
        let tyText = display ty
        let caller =
            match defName with
            | Some name -> $"the caller of '{name}'"
            | None -> "the caller"
        let what =
            if isHole calleeName then $"'{tyText}' cannot be interpolated"
            else $"'{calleeName}' requires the context 'ToString({tyText})', which cannot be derived"
        $"{what}: {caller} chooses the other tags its union may hold, and a toString is derived only for a union whose tags are all known: name every tag in the type, or match on the tags it names"

    /// The error for a call to `calleeName` that requires `ToString` of the structure `ty`, whose variable `part` has
    /// no `ToString` where the call is, in the function `defName` (if it is in one).
    let undeclaredPart (calleeName: Identifier) (ty: Ty) (part: Ty) (defName: Identifier option) =
        let partText = display part
        let advice =
            match defName with
            | Some name -> $": add [ToString({partText})] to the type of '{name}'"
            | None -> ""
        $"'{calleeName}' requires the context 'ToString({display ty})', which is written through 'ToString({partText})', and that is not available here{advice}"

    /// `diagnostic` with the fix that adds `ToString` of `part` to the type of `defName`, if it is in a def.
    let addToStringFix (state: InferState) (part: Ty) (defName: Identifier option) (diagnostic: Diagnostic) =
        match defName with
        | Some name -> ContextFixes.withAddToType state name $"ToString({display part})" diagnostic
        | None -> diagnostic

    /// The def the holes found once the whole module is inferred are pending in (see `resolveLater`). No def has it.
    let moduleEnd: Identifier = ""

    /// What becomes of the hole `call`, at `span` in `env`, that needs `ctxName` at `argTys` when no instance is in
    /// scope, the compiler has none of its own and the type has no name to build one from: the instances `found` so
    /// far for the `count` contexts the hole function requires, with this one. A type with nothing left to settle has
    /// no instance, which is an error at the hole. Otherwise the hole is found once its type is known (see
    /// `resolvePending`): a lambda's parameter, say, is typed by the call it is passed to. In a def that does not write
    /// its type, that is once the def is inferred, and the type may turn out to be one of the def's own variables, which
    /// the def then requires `ToString` of (see `ContextInference`). Anywhere else, it is once the module is inferred.
    let resolveLater
        (env: TypeEnv)
        (state: InferState)
        (call: Expression)
        (ctxName: Identifier)
        (argTys: Ty list)
        (span: SourceSpan)
        (found: ContextInstance option list)
        (count: int)
        : Result<ContextInstance option list * InferState, Diagnostic list> =
        let ty = List.head argTys
        let error message = [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]
        if (TypeEnv.freeTyVars ty).IsEmpty then Error (error (noInstance ty))
        else
            let def, annotatedDef =
                match state.ContextScope with
                | DefScope(name, true) -> name, None
                | DefScope(name, false) -> moduleEnd, Some name
                | AmbientScope -> moduleEnd, None
            let pending =
                { Def = def; Call = call.Id; Callee = holeFunction; Index = found.Length; Count = count
                  Context = ctxName; ArgTys = argTys; Span = span; Errors = error (undeclared ty annotatedDef) |> List.map (addToStringFix state ty annotatedDef); ChecksOnly = false }
            Ok (found @ [ None ],
                { state with
                    PendingContextInstances = pending :: state.PendingContextInstances
                    HoleScopes = state.HoleScopes |> Map.add call.Id (env, annotatedDef) })

    /// The instance the hole `pending` finds now that its type is known to be `argTys`, when it is not one of the
    /// variables of the def it is in: as it would have found it at once, through `instanceInScope` in the environment
    /// it is written in, the compiler's own, or `synthesize` from a type's attached definitions or its parts. A hole
    /// that finds none failed to check, as it would have then, and the def around it carries on.
    ///
    /// A type variable left open is an error where it is one of the variables of the type the def around the hole
    /// writes (`defScheme` gives a def's type), which does not require `ToString` of it. Any other is a variable of a
    /// local function generic in it, `def show = { x -> "{x}" }` inside a def: local functions require no contexts, so
    /// the backend writes the value at each type the function is specialised at, as it does a built-in type's. A type
    /// with more to it than a variable, such as a function's, has no `ToString` whatever its variables turn out to be.
    let resolvePending
        (instanceInScope: TypeEnv -> Identifier -> Ty list -> TypeExpr option)
        (synthesize: TypeEnv -> Result<ContextInstance * InferState, Diagnostic list> option)
        (defScheme: Identifier -> TypeScheme option)
        (state: InferState)
        (pending: PendingContextInstance)
        (argTys: Ty list)
        : Result<ContextInstance option * InferState, Diagnostic list> =
        let env, annotatedDef = state.HoleScopes |> Map.tryFind pending.Call |> Option.defaultValue (TypeEnv.empty, None)
        let openVars = argTys |> List.map TypeEnv.freeTyVars |> Set.unionMany
        let declaredVars = annotatedDef |> Option.bind defScheme |> Option.map _.Quantified |> Option.defaultValue Set.empty
        let found =
            match instanceInScope env pending.Context argTys with
            | Some written -> Ok (InstanceInScope written, state)
            | None when argTys |> List.forall hasBuiltInInstance -> Ok (InstanceBuiltIn, state)
            | None ->
                match synthesize env with
                | Some synthesized -> synthesized
                // The prelude's `builtInFunction` writes a value of its type variable with the compiler's own.
                | None when not (Set.intersect openVars declaredVars).IsEmpty && annotatedDef <> Some builtInFunction -> Error pending.Errors
                | None when argTys |> List.forall (Unifier.unalias >> function TyVar _ -> true | _ -> false) -> Ok (InstanceBuiltIn, state)
                | None -> Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers pending.Span (noInstance (List.head argTys)) ]
        match found with
        | Ok (instance, next) -> Ok (Some instance, next)
        | Error errors -> Ok (None, { state with FailedExprs = (pending.Call, errors) :: state.FailedExprs })

