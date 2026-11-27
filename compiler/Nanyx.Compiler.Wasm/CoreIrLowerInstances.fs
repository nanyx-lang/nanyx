/// The members of the parameterised contexts a function value's type requires (see `IrFunction`): how a call through
/// the value finds them, and the keys the function finds them by in its body.
module internal Transpiler.Wasm.CoreIrLowerInstances

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput

/// The type of the member `memberName` of `context`, a parameterised context applied to types, as its declaration
/// writes it at those types. None for a context with no such member.
let private memberTypeOf (layouts: ContextLayouts) (context: Ty) (memberName: string) : Ty option =
    match context with
    | TyApply(name, args) ->
        layouts.Parameterised
        |> Map.tryFind name
        |> Option.bind (fun (parameters, members) ->
            members
            |> List.tryFind (fst >> (=) memberName)
            |> Option.map (fun (_, declared) ->
                let bindings = List.zip (List.truncate args.Length parameters) (List.truncate parameters.Length args) |> List.map (fun (v, ty) -> v.Id, ty) |> Map.ofList
                substitute bindings declared))
    | _ -> None

/// Where the call at `node` through a function value finds its member of the key `key`, given the keys of the members
/// in scope there, with the type the member is at: as the type checker found the context at the call, by the context's
/// place among those the callee's type writes, at the call's types (`subst` applied, see `LowerInput.LocalCallees`).
/// The type is needed to pass a generic function as the member, as the compiler's own `==` of `Eq(int)` is. None for
/// the member of a context without parameters, or of one the type checker found nothing for there, or found in scope
/// by a key not in scope.
let foundAt (input: LowerInput) (subst: Map<int, Ty>) (node: NodeId) (scopeKeys: Set<ContextMemberKey>) (key: ContextMemberKey) : (ContextArgument * Ty option) option =
    match key, input.LocalCallees |> Map.tryFind node, input.CalleeTypes |> Map.tryFind node with
    | InstanceMember(instance, memberName), Some (contexts, lambdaTy), Some calleeTy ->
        let bindings = try bindScheme Map.empty lambdaTy (substitute subst calleeTy) with CannotLower _ -> Map.empty
        contexts
        |> List.map (substitute bindings)
        |> List.indexed
        |> List.tryFind (fun (_, context) -> typeKeyOfType context = Some instance)
        |> Option.bind (fun (index, context) ->
            input.FoundMember node index memberName scopeKeys
            |> Option.bind (function
                | Choice1Of2 inScope when scopeKeys.Contains inScope -> Some (MemberInScope inScope)
                | Choice1Of2 _ -> None
                | Choice2Of2 argument -> Some argument)
            |> Option.map (fun argument -> argument, memberTypeOf input.ContextLayouts context memberName))
    | _ -> None

/// The keys of the members of the parameterised contexts `written`, as a local function's type writes them (see
/// `LiftedLambda`): `Show_b_show` for `[Show(b)]`, the keys `byWrittenKeys` puts its members in scope by too.
let writtenKeys (layouts: ContextLayouts) (written: TypeExpr list) : ContextMemberKey list =
    written
    |> List.collect (fun context ->
        match context.Kind with
        | TypeApply(name, _ :: _) ->
            layouts.Parameterised
            |> Map.tryFind name
            |> Option.map snd
            |> Option.defaultValue []
            |> List.map (fun (memberName, _) -> contextMemberKey (Some context) memberName)
        | _ -> [])

/// Each of `members`, those a lifted local function takes by key, of the parameterised contexts it requires, `contexts`
/// as its closure's type has them, by the other key its body finds it by: the key of the context as the function's type
/// writes it (`written`, see `LiftedLambda`), `Show_b_show` for the member keyed `Show_Point_show` of `[Show(b)]`. None
/// where the contexts are not the ones written, in order.
let byWrittenKeys (layouts: ContextLayouts) (contexts: Ty list) (written: TypeExpr list) (members: (ContextMemberKey * 'a) list) : (ContextMemberKey * 'a) list =
    let instances =
        Unifier.writtenContexts contexts
        |> List.choose (fun context ->
            match FunctionContexts.withoutScope context with
            | TyApply(name, _ :: _) as applied -> typeKeyOfType applied |> Option.map (fun key -> name, key)
            | _ -> None)
    let writtenInstances =
        written
        |> List.choose (fun context ->
            match context.Kind with
            | TypeApply(name, _ :: _) -> Some (name, context)
            | _ -> None)
    if List.map fst instances <> List.map fst writtenInstances then []
    else
        List.zip instances writtenInstances
        |> List.collect (fun ((name, key), (_, writtenContext)) ->
            layouts.Parameterised
            |> Map.tryFind name
            |> Option.map snd
            |> Option.defaultValue []
            |> List.choose (fun (memberName, _) ->
                members
                |> List.tryFind (fst >> (=) (InstanceMember(key, memberName)))
                |> Option.map (fun (_, value) -> contextMemberKey (Some writtenContext) memberName, value)))

/// Each of `written` (see `byWrittenKeys`) by the member's own name too, `show` for `Show_b_show`, where no other of
/// them and none of `keys` has a member of that name.
let aliases (keys: ContextMemberKey list) (written: (ContextMemberKey * 'a) list) : (ContextMemberKey * 'a) list =
    written
    |> List.choose (function
        | InstanceMember(_, memberName), value -> Some (memberName, value)
        | NamedMember _, _ -> None)
    |> List.groupBy fst
    |> List.choose (function
        | memberName, [ (_, value) ] when not (List.contains (NamedMember memberName) keys) -> Some (NamedMember memberName, value)
        | _ -> None)

/// Whether a function of the type `fnTy` (`subst` applied) is never called: a parameter of it is of a type variable
/// nothing constrains, and no value of such a type is ever made (see `toIrType`). Named as a value there, as in
/// `dbg(twice)` for `twice: [Show(a)] a -> string`, it needs no instance of a context at that variable: none of its
/// members ever runs, so a stand-in is passed for each (see `standInFunction`).
let neverCalled (fnTy: Ty) : bool =
    match Unifier.unalias fnTy with
    | TyFunc(input, _, _) ->
        let parameters =
            match Unifier.unalias input with
            | TyRecord(fields, _) when fields.Keys |> Seq.forall (fun name -> fst (System.Int32.TryParse name)) -> List.ofSeq fields.Values
            | single -> [ single ]
        parameters |> List.exists (function TyVar _ -> true | _ -> false)
    | _ -> false

/// How many parameters the function a stand-in for a context member of the type `ty` is a closure over takes (see
/// `standInFunction`).
let standInArity (ty: IrType) =
    match ty with
    | IrFunction(input, _, members, _) -> 1 + (valuesOf input).Length + members.Length
    | _ -> 1

/// The function `functionName` a stand-in for a context member of the type `ty` is a closure over (see `neverCalled`):
/// it takes what the member takes, and crashes.
let standInFunction (newVar: string -> VarKind -> IrType -> Var) (functionName: string) (ty: IrType) : Function =
    let input, result, members =
        match ty with
        | IrFunction(input, result, members, _) -> input, result, members
        | _ -> internalError $"a stand-in for a context member of type '{IrType.show ty}'"
    let environment = newVar ClosureSyntax.envParameter Param (IrRecord [ "0", IrWord ])
    let values = valuesOf input |> List.mapi (fun index ty -> newVar $"__arg{index + 1}" Param ty)
    let passed = members |> List.map (fun (key, ty) -> newVar (contextMemberParameter key) Param ty)
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = environment :: values @ passed
      Result = result
      Body = Expr.make (Crash [ "a context member at a type nothing constrains was called" ]) result
      Place = None }
