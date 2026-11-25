/// Which function a call calls (see `CoreIrLower`): a definition's own, or its specialisation at the call's type
/// arguments, on the members of the contexts its context variables stand for and on the members the call knows.
module internal Transpiler.Wasm.CoreIrLowerCallee

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLowerKnown
open Transpiler.Wasm.CoreIrLowerScope

/// The callees of one function's calls, asking for the specialisations they need through `request`. Each member that
/// reads types takes `subst`, the function's type arguments as they are where it is called (see `FunctionLowering`).
type internal Callees(input: LowerInput, definitions: Map<string, Expression>, request: SpecKey -> Map<int, Ty> -> Lowering -> string) =
    let toIrType = toIrTypeIn input.ContextLayouts

    /// The members of the contexts in `contexts`, in the order a function value takes them.
    let membersOf = contextMembersOf input.ContextLayouts

    /// The context variables of the definition `name`'s type (`[c]` in `[c] ([c] a -> b), a -> b`), by id.
    let contextVariables (name: string) =
        input.DefinitionTypes |> Map.tryFind name |> Option.map (FunctionContexts.variablesIn >> Map.keys >> List.ofSeq) |> Option.defaultValue []

    /// The context variables of the definition `name`'s type that only the function it returns requires (`[c]` in
    /// `([c] a -> b), ([c] b -> d) -> [c] a -> d`): that function takes their members when it is called, so a call to
    /// the definition does not pass them, though it is specialised on them. So do those the value it returns carries,
    /// `[c, d] Stream(b)` (see `FunctionContexts.carried`).
    let returnedVariables (name: string) =
        let ofContexts (contexts: Ty list) = contexts |> List.choose (function TyVar v -> Some v.Id | _ -> None) |> Set.ofList
        let returned (result: Ty) =
            match Unifier.unalias result with
            | FunctionContexts.CarryingNominal(_, _, contexts) -> contexts
            | TyNominal _ -> []
            | result -> requiredContexts result
        match input.DefinitionTypes |> Map.tryFind name |> Option.map Unifier.unalias with
        | Some (TyFunc(_, result, own)) -> Set.difference (ofContexts (returned result)) (ofContexts own)
        | _ -> Set.empty

    /// The members of the contexts the context variables of the definition `name` stand for where `bindings` binds them,
    /// which the definition takes after those of the contexts it declares, as each call passes them.
    let variableMembers (name: string) (bindings: Map<int, Ty>) =
        let declared =
            input.ContextParameters |> Map.tryFind name |> Option.map (fst >> List.map _.Key) |> Option.defaultValue [] |> Set.ofList
        let returned = returnedVariables name
        contextVariables name
        |> List.filter (returned.Contains >> not)
        |> List.collect (fun id -> bindings |> Map.tryFind id |> Option.map List.singleton |> Option.defaultValue [])
        |> membersOf
        |> List.filter (fst >> declared.Contains >> not)

    /// What the type variables and context variables of the definition `name` are where its type is `instanceTy`.
    let bindingsAt (subst: Map<int, Ty>) (name: string) (instanceTy: Ty) =
        match input.DefinitionTypes |> Map.tryFind name with
        | Some scheme -> bindScheme Map.empty scheme (substitute subst instanceTy)
        | None -> Map.empty

    /// The type variables the types of the definition `name`'s nodes name, and the types of the members of the contexts
    /// it requires, by name.
    let namedVariables =
        let cache = System.Collections.Generic.Dictionary<string, Map<string, int list>>()
        fun (name: string) ->
            match cache.TryGetValue name with
            | true, found -> found
            | _ ->
                let rec named (ty: Ty) =
                    match ty with
                    | TyVar { Id = id; Name = Some varName } -> [ varName, id ]
                    | _ -> Ty.children ty |> List.collect named
                let memberTys =
                    input.ContextParameters |> Map.tryFind name |> Option.map fst |> Option.defaultValue [] |> List.choose _.Type
                let found =
                    SyntaxTree.expressionsWithin definitions.[name]
                    |> List.choose (fun expr -> input.ExprTypes |> Map.tryFind expr.Id)
                    |> List.append memberTys
                    |> List.collect named
                    |> List.distinct
                    |> List.groupBy fst
                    |> List.map (fun (varName, ids) -> varName, List.map snd ids)
                    |> Map.ofList
                cache[name] <- found
                found

    /// `bindings`, the type variables of `name` where `call` calls it, with each associated type of a context it requires
    /// as the call has it (see `LowerInput.AssociatedArguments`): its type need not write them, as `elem` in
    /// `[Iter(c)] c -> int`, though its body has values of them.
    let withAssociatedArguments (subst: Map<int, Ty>) (call: NodeId option) (name: string) (bindings: Map<int, Ty>) =
        match call |> Option.bind (fun call -> input.AssociatedArguments |> Map.tryFind call) with
        | Some named when definitions.ContainsKey name ->
            let variables = namedVariables name
            named
            |> Map.fold (fun acc varName ty ->
                variables
                |> Map.tryFind varName
                |> Option.defaultValue []
                |> List.fold (fun acc id -> if acc |> Map.containsKey id then acc else acc |> Map.add id (substitute subst ty)) acc) bindings
        | _ -> bindings

    /// The name to call `name` by at the instance type `instanceTy`, given the members known where it is called: its
    /// own, or a specialisation's. A definition with context variables is specialised on the members of the contexts
    /// they stand for, unless they stand for none. A definition that declares contexts is lowered in its own form only
    /// where a call asks for it (see `CoreIrLowerModule.lowerModule`).
    let knowing (subst: Map<int, Ty>) (call: NodeId option) (name: string) (instanceTy: Ty) (knownMembers: (ContextMemberKey * KnownMember) list) =
        let scheme =
            match input.DefinitionTypes |> Map.tryFind name with
            | Some scheme -> scheme
            | None -> cannot $"a call to '{name}', whose type is not known"
        let typeVars = freeVars scheme |> List.distinct |> List.sort
        let contextVars = contextVariables name
        let own () =
            if input.ContextParameters.ContainsKey name then request (SpecKey.plain name []) Map.empty AsDefined
            else sanitizeIdentifier name
        if typeVars.IsEmpty && contextVars.IsEmpty && knownMembers.IsEmpty then own ()
        else
            let bindings = bindingsAt subst name instanceTy |> withAssociatedArguments subst call name
            let typeArguments =
                typeVars
                |> List.map (fun id ->
                    match bindings |> Map.tryFind id with
                    | Some ty -> toIrType ty
                    // A type argument the call does not fix is a type variable nothing constrains (see `toIrType`).
                    | None -> IrUnit)
            // What each context variable stands for, as the function type that requires it: its members, and whether it
            // can wait.
            let contextArguments =
                contextVars
                |> List.map (fun id -> toIrType (TyFunc(TyPrimitive "unit", TyPrimitive "unit", bindings |> Map.tryFind id |> Option.toList)))
            let typeArguments =
                if contextArguments |> List.forall ((=) (IrFunction(IrUnit, IrUnit, [], false))) then typeArguments
                else typeArguments @ contextArguments
            let described =
                let variables = SpecKey.describedVariables scheme
                typeVars |> List.filter variables.Contains |> List.map (fun id -> bindings |> Map.tryFind id |> Option.defaultValue (TyPrimitive "unit"))
            if typeArguments.IsEmpty && knownMembers.IsEmpty then own ()
            else request { Definition = name; TypeArguments = typeArguments; Known = knownMembers; Described = described } bindings AsDefined

    /// The members of the contexts in `contexts`, in the order a function value takes them.
    member _.MembersOf = membersOf

    /// The context variables of the definition `name`'s type, by id.
    member _.ContextVariables (name: string) = contextVariables name

    /// The members of the contexts the context variables of the definition `name` stand for where `bindings` binds them.
    member _.VariableMembers (name: string) (bindings: Map<int, Ty>) = variableMembers name bindings

    /// What the type variables and context variables of the definition `name` are where its type is `instanceTy`.
    member _.BindingsAt (subst: Map<int, Ty>) (name: string) (instanceTy: Ty) = bindingsAt subst name instanceTy

    /// `bindings`, with each associated type of a context `name` requires as `call` has it.
    member _.WithAssociatedArguments (subst: Map<int, Ty>) (call: NodeId option) (name: string) (bindings: Map<int, Ty>) =
        withAssociatedArguments subst call name bindings

    /// The name to call `name` by at the instance type `instanceTy`, given the members known where it is called.
    member _.Knowing (subst: Map<int, Ty>) (call: NodeId option) (name: string) (instanceTy: Ty) (knownMembers: (ContextMemberKey * KnownMember) list) =
        knowing subst call name instanceTy knownMembers
