/// How the lowering (see `CoreIrLower`) finds the context members a call passes, and adapts a value to the type it is
/// wanted at (`coerce`): a function value that requires other members, or has values boxed where the type does not.
module internal Transpiler.Wasm.CoreIrLowerMembers

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLowerKnown
open Transpiler.Wasm.CoreIrLowerScope

/// What finding and adapting members needs from the function being lowered.
type MemberTools =
    { Input: LowerInput
      /// The type arguments of the specialisation being lowered, as they are now.
      Subst: unit -> Map<int, Ty>
      ToIrType: Ty -> IrType
      /// Asks for a function, by its key, lowered as the `Lowering` says, and gives its name in WebAssembly text.
      Request: SpecKey -> Map<int, Ty> -> Lowering -> string
      /// A variable of this name, kind and type, named as it is.
      NewVar: string -> VarKind -> IrType -> Var
      /// A parameter's or local's name: as written, made unique within the function.
      UniqueName: string -> string
      /// A value bound to a temporary, passed on.
      BindTemp: Expr -> (Atom -> Expr) -> Expr
      /// A value as an atom that can wait while code that stores runs before it is used.
      Settle: Expr -> (Atom -> Expr) -> Expr
      /// The member a local is known to be, if it is.
      KnownOf: Var -> KnownMember option
      /// The members of the contexts a definition's context variables stand for where these bindings bind them.
      VariableMembers: string -> Map<int, Ty> -> (ContextMemberKey * IrType) list
      /// What the type variables and context variables of a definition are where the call at this node has it at this
      /// type, with the associated types of the contexts it requires as the call has them.
      BindingsAt: NodeId -> string -> Ty -> Map<int, Ty>
      /// The names of a definition's parameters.
      ParameterNames: string -> string list
      /// The name to call a definition by at an instance type.
      Callee: string -> Ty -> string
      /// The function a wrapper wraps.
      WrappedOf: string -> string }

/// The closure over the wrapper `wrapper` of a top-level function, as a context member of type `memberTy` (see
/// `wrappedAt`): static data, since it captures nothing.
let wrapperClosure (tools: MemberTools) (wrapper: string) (memberTy: Ty option) : Atom =
    let wrapped, at = wrappedAt tools.Input wrapper memberTy
    let name = tools.Request (SpecKey.plain wrapper (wrapperTypeArguments tools.Input tools.ToIrType wrapped at)) (tools.Subst ()) (WrapperCalling(wrapped, at, []))
    AStaticClosure(name, IrType.closureArity (tools.ParameterNames wrapper) (tools.ToIrType at), tools.ToIrType at)

/// The value of a known member, passed to `k`: the closure over a function's wrapper or a lambda, or a top-level value,
/// evaluated.
let valueOfKnown (tools: MemberTools) (knownMember: KnownMember) (k: Atom -> Expr) : Expr =
    match knownMember with
    | KnownFunction(wrapper, _, at) -> k (wrapperClosure tools wrapper (Some at))
    | KnownLambda(_, closure) -> k closure
    | KnownLambdaOver lifted -> internalError $"the value of a closure over '{lifted}', which is passed, not known"
    | KnownValue(definition, ty) -> tools.BindTemp (Expr.make (Call(tools.Callee definition ty, [])) (tools.ToIrType ty)) k
    | KnownConstant(constant, _) -> k constant

/// The members of the contexts `name` declares, and the type of each where the call at `node` calls it at `instanceTy`,
/// where the function's type gives it.
let private memberTypes (tools: MemberTools) (name: string) (node: NodeId) (instanceTy: Ty) : ContextParameter list * Ty option list =
    let contextParameters = tools.Input.ContextParameters |> Map.tryFind name |> Option.map fst |> Option.defaultValue []
    let bindings = tools.BindingsAt node name instanceTy
    contextParameters, contextParameters |> List.map (fun parameter -> parameter.Type |> Option.map (substitute bindings >> substitute (tools.Subst ())))

/// The members of the contexts `name` declares that a call to it at the node `node` knows (see `CoreIrLowerKnown`).
/// What cannot be found is left to `contextMembers` to report.
let knownMembers (tools: MemberTools) (scope: Scope) (name: string) (node: NodeId) (instanceTy: Ty) =
    let contextParameters, memberTys = memberTypes tools name node instanceTy
    match tools.Input.ContextArguments name node (scope.Members |> Map.keys |> Set.ofSeq) Set.empty with
    | Ok arguments when arguments.Length = contextParameters.Length ->
        let inScope key = scope.Members |> Map.tryFind key |> Option.bind tools.KnownOf
        knownAmong tools.Input contextParameters arguments memberTys inScope
    | _ -> []

/// Whether a value of `given` must be adapted to be a value of `wanted` because, somewhere in it, a function requires
/// other contexts' members: in a record's fields, a tag's payload, a function's input or result, or the body of a
/// recursive type, as a value of a type generic in contexts given where one that carries more is expected
/// (`FunctionContexts.carried`) has.
let rec differInContexts (given: IrType) (wanted: IrType) : bool =
    match given, wanted with
    | _ when given = wanted -> false
    | IrFunction(input, output, members, _), IrFunction(input', output', members', _) ->
        members <> members' || differInContexts input' input || differInContexts output output'
    | IrRecord givenFields, IrRecord wantedFields when List.map fst givenFields = List.map fst wantedFields ->
        List.exists2 (fun (_, givenTy) (_, wantedTy) -> differInContexts givenTy wantedTy) givenFields wantedFields
    | IrTags givenTags, IrTags wantedTags ->
        givenTags
        |> List.exists (fun (name, payload) ->
            match payload, wantedTags |> List.tryFind (fst >> (=) name) |> Option.bind snd with
            | Some givenPayload, Some wantedPayload -> differInContexts givenPayload wantedPayload
            | _ -> false)
    | IrRecursive(_, givenBodies), IrRecursive(_, wantedBodies) ->
        givenBodies.Length = wantedBodies.Length && List.exists2 differInContexts givenBodies wantedBodies
    | _ -> false

/// `atom` as a value of type `expected`. A function value where it requires other contexts' members where it is
/// called, takes or returns values boxed where `expected` does not or the other way round, or takes or returns values
/// that need adapting, is a closure over an adapter that holds it and calls it with the members it requires, those
/// `expected` requires passed on, and the others captured here, converting its arguments and its result. Any other
/// value with values boxed where `expected` does not, or the other way round, is converted (see `CoreIrLowerBoxes`).
/// A value with a function in it that needs adapting (see `differInContexts`) is made again around its parts, each
/// adapted (see `adaptParts`): one of a recursive type through a function of its own (`ValueAdapting`), which calls
/// itself for the parts that are of the type again. A value of `expected`'s layout is left as it is.
let rec coerce (tools: MemberTools) (scope: Scope) (atom: Atom) (expected: IrType) (k: Atom -> Expr) : Expr =
    match atomType atom, expected with
    | IrFunction(_, _, given, _) as givenTy, IrFunction(_, _, wanted, _) when differInContexts givenTy expected || CoreIrLowerBoxes.needsConversion givenTy expected ->
        let missing = given |> List.filter (fun (key, _) -> not (wanted |> List.exists (fst >> (=) key)))
        memberAtoms tools scope None missing (fun captured ->
            let name = tools.Request (SpecKey.plain $"__adapt_{IrType.mangle givenTy}_to_{IrType.mangle expected}" []) (tools.Subst ()) (Adapting(givenTy, expected))
            let arity = 1 + (adaptedValueTypes givenTy expected).Length + wanted.Length
            tools.BindTemp (Expr.make (MakeClosure(name, arity, atom :: captured)) expected) k)
    | givenTy, _ when CoreIrLowerBoxes.needsConversion givenTy expected -> CoreIrLowerBoxes.convert (boxTools tools scope) atom expected k
    | IrRecursive _ as givenTy, IrRecursive _ when differInContexts givenTy expected ->
        let name = tools.Request (SpecKey.plain $"__adapt_{IrType.mangle givenTy}_to_{IrType.mangle expected}" []) (tools.Subst ()) (ValueAdapting(givenTy, expected))
        tools.BindTemp (Expr.make (Call(name, [ atom ])) expected) k
    | givenTy, _ when differInContexts givenTy expected -> adaptParts tools scope atom givenTy expected k
    | _ -> k atom

/// `atom`, a record or a tag union of type `given` (the body of its type, if that is recursive), as a value of
/// `expected`: a new record of its fields, or the tag made again around its payload, each adapted (see `coerce`).
/// A tag whose payload needs nothing is the value as it is.
and adaptParts (tools: MemberTools) (scope: Scope) (atom: Atom) (given: IrType) (expected: IrType) (k: Atom -> Expr) : Expr =
    match given, expected with
    | IrRecord givenFields, IrRecord wantedFields ->
        // The record is read once for each field, so it waits in a local.
        tools.Settle (Expr.make (Atom atom) (atomType atom)) (fun record ->
            let rec fields (index: int) (adapted: Atom list) =
                if index = givenFields.Length then tools.BindTemp (Expr.make (MakeRecord(List.zip (List.map fst wantedFields) (List.rev adapted))) expected) k
                else
                    tools.BindTemp (Expr.make (Field(record, index)) (snd givenFields.[index])) (fun field ->
                        coerce tools scope field (snd wantedFields.[index]) (fun field -> fields (index + 1) (field :: adapted)))
            fields 0 [])
    | IrTags givenTags, IrTags wantedTags ->
        let value = tools.NewVar (tools.UniqueName "__adapting") Local (atomType atom)
        let arms =
            givenTags
            |> List.choose (fun (name, payload) ->
                match payload, wantedTags |> List.tryFind (fst >> (=) name) |> Option.bind snd with
                | Some givenPayload, Some wantedPayload when differInContexts givenPayload wantedPayload ->
                    let bound = tools.NewVar (tools.UniqueName "__payload") Local givenPayload
                    let body = coerce tools scope (AVar bound) wantedPayload (fun payload -> Expr.make (MakeTag(name, payload)) expected)
                    Some { Patterns = [ PTag(name, Some (PBind bound)) ]; Body = body }
                | _ -> None)
        let others = { Patterns = [ PWildcard ]; Body = Expr.make (Atom(AVar value)) expected }
        let adapted = Expr.make (Match([ value ], arms @ [ others ])) expected
        let body = tools.BindTemp adapted k
        Expr.make (Let(value, Expr.make (Atom atom) value.Type, body)) body.Type
    | _ -> internalError $"an adaptation from '{IrType.show given}' to '{IrType.show expected}'"

/// The function `functionName` that makes a value of the recursive type `given` a value of `expected` (see `coerce`):
/// its parts, each adapted.
and valueAdapter (tools: MemberTools) (scope: Scope) (functionName: string) (given: IrType) (expected: IrType) : Function =
    let value = tools.NewVar "__value" Param given
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = [ value ]
      Result = expected
      Body = adaptParts tools scope (AVar value) (IrType.unfold given) (IrType.unfold expected) (fun atom -> Expr.make (Atom atom) expected)
      Place = None }

/// What converting a value to or from boxes needs, where `scope` is in scope (see `CoreIrLowerBoxes`).
and boxTools (tools: MemberTools) (scope: Scope) : CoreIrLowerBoxes.BoxTools =
    { NewLocal = fun name ty -> tools.NewVar (tools.UniqueName name) Local ty
      Reader = fun ty -> tools.Request (SpecKey.plain $"__box_{IrType.mangle ty}" []) (tools.Subst ()) (BoxReading ty)
      ListConverter =
        fun given expected ->
            tools.Request (SpecKey.plain $"__convert_list_{IrType.mangle given}_to_{IrType.mangle expected}" []) (tools.Subst ()) (ListConverting(given, expected))
      RecursiveConverter =
        fun given expected ->
            tools.Request (SpecKey.plain $"__convert_rec_{IrType.mangle given}_to_{IrType.mangle expected}" []) (tools.Subst ()) (RecursiveConverting(given, expected))
      Convert = fun atom ty k -> coerce tools scope atom ty (fun atom -> tools.Settle (Expr.make (Atom atom) (atomType atom)) k) }

/// The member of each of `members` (a key and the type the member is wanted at): where the type checker found its
/// parameterised context at the call through a function value at `node` (see `CoreIrLowerInstances.foundAt`), or
/// else as the scope has it, or else the ambient instance's, each adapted to its type (see `coerce`).
and memberAtoms (tools: MemberTools) (scope: Scope) (node: NodeId option) (members: (ContextMemberKey * IrType) list) (k: Atom list -> Expr) : Expr =
    let rec go (remaining: (ContextMemberKey * IrType) list) (acc: Atom list) =
        match remaining with
        | [] -> k (List.rev acc)
        | (key, ty) :: rest ->
            let found (atom: Atom) = coerce tools scope atom ty (fun atom -> go rest (atom :: acc))
            let foundAtCall = node |> Option.bind (fun node -> CoreIrLowerInstances.foundAt tools.Input (tools.Subst ()) node (scope.Members |> Map.keys |> Set.ofSeq) key)
            // The one member in scope that is the same member at other type arguments, as a specialisation of a
            // generic function has `Yield_b_yield` from its `use Yield(b)` where `Yield_int_yield` is wanted.
            let compatible () =
                match scope.Members |> Map.toList |> List.filter (fst >> ContextMemberKey.sameMember key) with
                | [ _, var ] -> Some var
                | _ -> None
            match foundAtCall, scope.Members |> Map.tryFind key |> Option.orElseWith compatible, tools.Input.AmbientMembers |> Map.tryFind key with
            | Some (argument, memberTy), _, _ -> argumentAtom tools scope argument memberTy found
            | None, Some var, _ -> found (AVar var)
            | None, None, Some wrapper -> found (wrapperClosure tools wrapper None)
            | None, None, None -> cannot $"the context member '{ContextMemberKey.text key}', which is not in scope"
    go members []

/// The member a call finds where `argument` says (see `ContextArgument`), as the callee takes it at `memberTy` where
/// that is known.
and private argumentAtom (tools: MemberTools) (scope: Scope) (argument: ContextArgument) (memberTy: Ty option) (k: Atom -> Expr) : Expr =
    match argument with
    | MemberInScope inScope -> k (AVar scope.Members.[inScope])
    | ClosureOverWrapper wrapper -> k (wrapperClosure tools wrapper memberTy)
    | DerivedClosure(wrapper, at) -> derivedClosure tools scope wrapper at memberTy k
    // A member that is a value (`Point.`0``): the top-level value, evaluated.
    | DefinitionValue definition -> valueOfKnown tools (knownValue tools.Input definition memberTy) k
    | PassedByCaller -> internalError "a member its callers pass, found where it is called"
    // The type checker found every context a call requires, so one missing here is the backend's bug.
    | Unfound key ->
        let available = (scope.Members |> Map.keys |> List.ofSeq) @ (tools.Input.AmbientMembers |> Map.keys |> List.ofSeq) |> List.distinct |> List.map ContextMemberKey.text |> String.concat ", "
        internalError $"the context member '{ContextMemberKey.text key}' is not in scope; those in scope are [{available}]"

/// A closure over the wrapper `wrapper` of a definition derived from the parts of a type (see `DerivedClosure`), at
/// `memberTy`, the type of the context member it is passed as: it captures the members of the contexts the
/// definition requires, found as for a call to it at the node `at`.
and private derivedClosure (tools: MemberTools) (scope: Scope) (wrapper: string) (at: NodeId) (memberTy: Ty option) (k: Atom -> Expr) : Expr =
    let wrapped = tools.WrappedOf wrapper
    // A member whose type the context's annotation does not give (`ToString(#x(a) | r)`) is at the definition's own.
    let memberTy =
        match memberTy, tools.Input.DefinitionTypes |> Map.tryFind wrapped with
        | Some memberTy, _ -> unconstrainedAsUnit memberTy
        | None, Some (TyFunc(input, output, _)) -> unconstrainedAsUnit (TyFunc(input, output, []))
        | None, _ -> internalError $"the derived definition '{wrapped}' has no function type"
    let ty = tools.ToIrType memberTy
    contextMembers tools scope wrapped at memberTy Set.empty [] (fun captured ->
        let name = tools.Request (SpecKey.plain wrapper [ ty ]) (tools.Subst ()) (WrapperCalling(wrapped, memberTy, captured |> List.map (fun (key, atom) -> key, atomType atom)))
        match closureOver name (IrType.closureArity (tools.ParameterNames wrapper) ty) (List.map snd captured) ty with
        | { Kind = Atom atom } -> k atom
        | closure -> tools.BindTemp closure k)

/// The members of the contexts `name` requires, where it is called, or named as a value, at the node `node` and the
/// type `instanceTy`, each with its key: those of the contexts it declares, each found where the type checker found
/// it (see `LowerInput.ContextArguments`), then those of the contexts its context variables stand for, found in
/// scope. They are passed on to `k` in the order the function takes them, but for those of the keys in `passed`,
/// which the callers of the function's value pass instead, and those in `knownMembers`, which the callee is
/// specialised on, but for a lambda's environment (`KnownMember.isPassed`).
and contextMembers (tools: MemberTools) (scope: Scope) (name: string) (node: NodeId) (instanceTy: Ty) (passed: Set<ContextMemberKey>) (knownMembers: (ContextMemberKey * KnownMember) list) (k: (ContextMemberKey * Atom) list -> Expr) : Expr =
    let contextParameters, memberTys = memberTypes tools name node instanceTy
    let variables = tools.VariableMembers name (tools.BindingsAt node name instanceTy) |> List.filter (fst >> passed.Contains >> not)
    match tools.Input.ContextArguments name node (scope.Members |> Map.keys |> Set.ofSeq) passed with
    | Error reason -> cannot reason
    | Ok arguments ->
        let rec pass (remaining: (ContextMemberKey * ContextArgument * Ty option) list) (acc: (ContextMemberKey * Atom) list) =
            let next key (atom: Atom) (memberTy: Ty option) rest =
                match memberTy with
                | Some ty -> coerce tools scope atom (tools.ToIrType ty) (fun atom -> pass rest ((key, atom) :: acc))
                | None -> pass rest ((key, atom) :: acc)
            match remaining with
            | [] -> memberAtoms tools scope None variables (fun atoms -> k (List.rev acc @ List.zip (List.map fst variables) atoms))
            | (_, PassedByCaller, _) :: rest -> pass rest acc
            | (key, _, _) :: rest when knownMembers |> List.exists (fun (known, knownMember) -> known = key && not (KnownMember.isPassed knownMember)) ->
                pass rest acc
            // A member that is a value (`Point.`0``) is passed as it is, at the member's type if it is generic.
            | (key, (DefinitionValue _ as argument), memberTy) :: rest -> argumentAtom tools scope argument memberTy (fun atom -> pass rest ((key, atom) :: acc))
            | (key, Unfound _, Some memberTy) :: rest when CoreIrLowerInstances.neverCalled (substitute (tools.Subst ()) instanceTy) ->
                let ty = tools.ToIrType (unconstrainedAsUnit memberTy)
                let standIn = tools.Request (SpecKey.plain $"__stand_in_{IrType.mangle ty}" []) (tools.Subst ()) (StandingIn ty)
                next key (AStaticClosure(standIn, CoreIrLowerInstances.standInArity ty, ty)) (Some memberTy) rest
            | (key, argument, memberTy) :: rest -> argumentAtom tools scope argument memberTy (fun atom -> next key atom memberTy rest)
        if arguments.Length <> memberTys.Length then
            internalError $"{arguments.Length} context members found for the {memberTys.Length} '{name}' requires"
        pass (List.zip3 (contextParameters |> List.map _.Key) arguments memberTys) []

/// Whether making a value of `given` a value of `expected` (see `coerce`) stores to a local: converting it to or from
/// boxes does (see `CoreIrLowerBoxes`), and so does making a record or a tag again around its adapted parts.
let coercionStores (given: IrType) (expected: IrType) : bool =
    CoreIrLowerBoxes.needsConversion given expected
    || differInContexts given expected
       && (match given, expected with
           | IrFunction _, IrFunction _
           | IrRecursive _, IrRecursive _ -> false
           | _ -> true)

/// `atoms`, each adapted to the type in `tys` in its place (see `coerce`); as they are when `tys` is empty.
/// Where adapting one of them stores (see `coercionStores`), each adapted value waits in a local.
let coerceAll (tools: MemberTools) (scope: Scope) (atoms: Atom list) (tys: IrType list) (k: Atom list -> Expr) : Expr =
    let converting = tys.Length = atoms.Length && List.exists2 (fun atom ty -> coercionStores (atomType atom) ty) atoms tys
    let rec go (remaining: (Atom * IrType) list) (acc: Atom list) =
        match remaining with
        | [] -> k (List.rev acc)
        | (atom, ty) :: rest ->
            coerce tools scope atom ty (fun atom ->
                if converting then tools.Settle (Expr.make (Atom atom) (atomType atom)) (fun atom -> go rest (atom :: acc))
                else go rest (atom :: acc))
    if tys.IsEmpty then k atoms else go (List.zip atoms tys) []
