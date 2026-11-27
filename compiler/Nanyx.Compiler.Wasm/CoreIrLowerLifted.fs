/// How the lowering (see `CoreIrLower`) lowers the functions lambda lifting and the closures it makes need: a lifted
/// lambda (`LiftedCapturing`), the wrapper that lets a top-level function be a value (`WrapperCalling`), and the adapter
/// that lets a function value be called as one of another type (`Adapting`).
module internal Transpiler.Wasm.CoreIrLowerLifted

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLowerScope

/// What lowering these functions needs from the function being lowered.
type LiftedTools =
    { Input: LowerInput
      /// A variable of this name, kind and type, named as it is.
      NewVar: string -> VarKind -> IrType -> Var
      /// A parameter's or local's name: as written, made unique within the function.
      UniqueName: string -> string
      /// A type at the function's type arguments.
      Substitute: Ty -> Ty
      ToIrType: Ty -> IrType
      /// The members of these contexts, in the order a function value takes them.
      MembersOf: Ty list -> (ContextMemberKey * IrType) list
      /// A scope with nothing in it.
      EmptyScope: Scope
      /// A value bound to a temporary, passed on.
      BindTemp: Expr -> (Atom -> Expr) -> Expr
      /// A value bound to a local of this name, made unique, passed on.
      BindLocal: string -> Expr -> (Atom -> Expr) -> Expr
      /// A value as an atom that can wait while code that stores runs before it is used.
      Settle: Expr -> (Atom -> Expr) -> Expr
      /// An atom adapted to a type (see `CoreIrLowerMembers.coerce`).
      Coerce: Scope -> Atom -> IrType -> (Atom -> Expr) -> Expr
      /// Atoms each adapted to its type (see `CoreIrLowerMembers.coerceAll`).
      CoerceAll: Scope -> Atom list -> IrType list -> (Atom list -> Expr) -> Expr
      /// The parameters a lifted function takes for its parameters (see `FunctionLowering.ValueParameters`).
      ValueParameters: string list -> IrType list -> Var list * (Expr -> Expr) * Var list
      /// The parameters a definition names.
      ParameterNames: string -> string list
      /// The types of a lifted lambda's parameters and result (see `FunctionLowering.LiftedParameterTypes`).
      LiftedParameterTypes: string -> Ty -> Ty list * Ty
      /// The name to call a definition by at an instance type.
      Callee: string -> Ty -> string
      /// What the type variables and context variables of a definition are where its type is this.
      BindingsAt: string -> Ty -> Map<int, Ty>
      /// The members of the contexts a definition's context variables stand for where these bindings bind them.
      VariableMembers: string -> Map<int, Ty> -> (ContextMemberKey * IrType) list
      /// Records the members the function being lowered takes after its other parameters, which a `rec` local
      /// function passes on when it calls itself.
      SetSelfMembers: (ContextMemberKey * IrType) list -> unit
      /// Lowers an expression in tail position.
      Tail: Scope -> Expression -> Expr }

/// The parameters a lifted function takes for its parameters `names`, of types `tys`, as a function value of type
/// `fnTy` (see `FunctionLowering.ValueParameters`). Where it takes its input whole (see `IrType.recordTakenWhole`), that
/// is one parameter, and each name is bound to the record's field of its name, or else, when the record has a field
/// for each name, the field in its place, at the type the name has.
let private valueParameters (tools: LiftedTools) (names: string list) (tys: IrType list) (fnTy: IrType) : Var list * (Expr -> Expr) * Var list =
    match IrType.recordTakenWhole ("" :: names) fnTy with
    | Some (IrRecord fields as recordTy) ->
        let record = tools.NewVar (tools.UniqueName "__input") Param recordTy
        let slotOf index name =
            match fields |> List.tryFindIndex (fst >> (=) name) with
            | Some slot -> slot
            | None when fields.Length = names.Length -> index
            | None -> cannot $"a function whose parameter {name} is not among its input's fields"
        let named = List.map2 (fun name ty -> tools.NewVar (tools.UniqueName name) Local ty) names tys
        let bind (body: Expr) =
            List.foldBack2 (fun (var: Var) slot (body: Expr) -> Expr.make (Let(var, Expr.make (Field(AVar record, slot)) var.Type, body)) body.Type)
                named (List.mapi slotOf names) body
        [ record ], bind, named
    | _ -> tools.ValueParameters names tys

/// The lifted lambda `name`, whose parameters are `parameterNames` (its environment first) and whose body is `body`,
/// as a function made by `make`, its closure capturing values of `capturedTys` and the local functions in
/// `capturedLocals`. `scopeOf` is the scope its parameters are in, and `lowerBody` lowers its body in that scope.
let liftedCapturing (tools: LiftedTools) (name: string) (parameterNames: string list) (body: Expression) (lowerBody: Scope -> Expression -> Expr) (scopeOf: Var list -> Scope) (make: Var list -> IrType -> Expr -> Function) (capturedTys: IrType list) (capturedLocals: Map<string, Ty * (IrType list * int) list>) : Function =
    let input = tools.Input
    let parameterOf (parameterName: string) (ty: IrType) = tools.NewVar (tools.UniqueName parameterName) Param ty
    // The environment first: the function's table slot, then what it captures. The rest are the lambda's
    // parameters, typed as the lambda was where it was written.
    let lambda, owner, written =
        match input.Lifted |> Map.tryFind name with
        | Some (LiftedLambda(lambda, _, owner, written)) -> lambda, owner, written
        | _ -> cannot $"'{name}' is not a lifted lambda"
    let lambdaTy =
        match input.ExprTypes |> Map.tryFind lambda.Id with
        | Some ty -> tools.Substitute ty
        | None -> cannot "a lambda without a type"
    let environment = IrRecord(("0", IrWord) :: (capturedTys |> List.mapi (fun index ty -> string (index + 1), ty)))
    let parameterTys, resultTy = tools.LiftedParameterTypes name lambdaTy
    let environmentParameter = parameterOf parameterNames.Head environment
    let valueParameters, bind, named = valueParameters tools parameterNames.Tail (List.map tools.ToIrType parameterTys) (tools.ToIrType lambdaTy)
    // The owner's context members are bound from the environment with what else it captures.
    let capturedMembers =
        let contextParameters, aliases = input.ContextParameters |> Map.tryFind owner |> Option.defaultValue ([], Map.empty)
        contextParameters
        |> List.map (fun parameter ->
            let otherNames = aliases |> Map.filter (fun _ key -> key = parameter.Key) |> Map.keys |> Seq.map NamedMember |> List.ofSeq
            parameter.Name, parameter.Key :: otherNames)
        |> Map.ofList
    // The members of the contexts the lambda requires where it is called follow its other parameters.
    // They stand for the owner's members of those keys, which it may still capture to make a closure that does
    // not take them.
    let members =
        tools.MembersOf (requiredContexts lambdaTy) |> List.map (fun (key, ty) -> key, tools.NewVar (tools.UniqueName (contextMemberParameter key)) Param ty)
    tools.SetSelfMembers (members |> List.map (fun (key, var) -> key, var.Type))
    // A parameterised context's members are in scope by the key its type writes too, which its body finds them by.
    let written = CoreIrLowerInstances.byWrittenKeys input.ContextLayouts (requiredContexts lambdaTy) written members
    // And by its own name, as a top-level function's are (see `CodeGen.contextMemberAliases`), where no other context
    // the lambda requires has a member of that name. A lambda passed where its parameter's type gives it an instance
    // (`withM { empty }` for `([Monoid(int)] () -> b) -> b`) writes none: its members are named by the instance's keys.
    let written = written @ CoreIrLowerInstances.aliases (List.map fst members) (if written.IsEmpty then members else written)
    let passed = members @ written |> List.map fst |> Set.ofList
    let capturedMembers = capturedMembers |> Map.map (fun _ keys -> keys |> List.filter (passed.Contains >> not))
    let scope = { scopeOf (environmentParameter :: named) with CapturedMembers = capturedMembers; CapturedLocals = capturedLocals }
    let scope =
        { scope with
            Members = members @ written |> List.fold (fun acc (key, var) -> acc |> Map.add key var) scope.Members
            // A lambda inside a local function captures its members by the keys the local writes (see
            // `CodeGen.passedMembers`), so they are named by those too (#2813).
            Vars = members @ written |> List.fold (fun acc (key, var) -> acc |> Map.add (contextMemberParameter key) var) scope.Vars }
    make (environmentParameter :: valueParameters @ List.map snd members) (tools.ToIrType resultTy) (bind (lowerBody scope body))

/// The wrapper, whose parameters are `parameterNames` (its environment first), that lets the top-level function
/// `wrapped` be a value of type `instanceTy`, as a function made by `make`, its closure capturing the context members
/// `captured`.
let wrapperCalling (tools: LiftedTools) (wrapped: string) (instanceTy: Ty) (captured: (ContextMemberKey * IrType) list) (parameterNames: string list) (make: Var list -> IrType -> Expr -> Function) : Function =
    let parameterOf (parameterName: string) (ty: IrType) = tools.NewVar (tools.UniqueName parameterName) Param ty
    // Takes an environment, which holds after its table slot the members of the contexts the function requires
    // that the closure captured, and after its other parameters those its value's type requires; and calls the
    // function with the rest and them all, in the order it takes them.
    // A wrapper that takes its input whole (see `IrType.recordTakenWhole`) passes the function each field under the
    // name of the parameter that takes it.
    let names =
        let wrappedNames = tools.ParameterNames wrapped
        match IrType.recordTakenWhole parameterNames (tools.ToIrType instanceTy) with
        | Some _ when wrappedNames.Length = parameterNames.Tail.Length -> wrappedNames
        | _ -> parameterNames.Tail
    let parameterTys, resultTy = parameterTypes names instanceTy true
    let environment = IrRecord(("0", IrWord) :: (captured |> List.mapi (fun index (_, ty) -> string (index + 1), ty)))
    let environmentParameter = parameterOf parameterNames.Head environment
    let valueParameters, bind, named = valueParameters tools names (List.map tools.ToIrType parameterTys) (tools.ToIrType instanceTy)
    let passed =
        match tools.ToIrType instanceTy with
        | IrFunction(_, _, members, _) -> members |> List.map (fun (key, ty) -> key, tools.NewVar (contextMemberParameter key) Param ty)
        | _ -> []
    let declared = tools.Input.ContextParameters |> Map.tryFind wrapped |> Option.map (fst >> List.map _.Key) |> Option.defaultValue []
    let order = declared @ (tools.VariableMembers wrapped (tools.BindingsAt wrapped instanceTy) |> List.map fst)
    let rec call (remaining: ContextMemberKey list) (members: Atom list) =
        match remaining, tools.Input.Externs |> Map.tryFind wrapped with
        // A foreign function takes no context members: the call is to the module's import of it.
        | [], Some declared -> Expr.make (CallImport(declared, named |> List.map AVar)) (tools.ToIrType resultTy)
        | [], None -> Expr.make (Call(tools.Callee wrapped instanceTy, (named |> List.map AVar) @ List.rev members)) (tools.ToIrType resultTy)
        | key :: rest, _ ->
            match captured |> List.tryFindIndex (fst >> (=) key), passed |> List.tryFind (fst >> (=) key) with
            | Some index, _ ->
                tools.BindTemp (Expr.make (Field(AVar environmentParameter, index + 1)) (snd captured.[index])) (fun atom -> call rest (atom :: members))
            | None, Some (_, var) -> call rest (AVar var :: members)
            | None, None -> internalError $"the context member '{ContextMemberKey.text key}' of '{wrapped}' is neither captured nor passed"
    make (environmentParameter :: valueParameters @ List.map snd passed) (tools.ToIrType resultTy) (bind (call order []))

/// The adapter `functionName` that lets a function value of type `givenTy` be called as one of `expectedTy` (see
/// `Coerce`): it takes an environment that holds the value, then the members `givenTy` requires that `expectedTy`
/// does not, and its arguments and the members `expectedTy` requires; and calls the value with the arguments and
/// the members it requires. An argument that is a function is adapted in turn to the type the value takes it at, as
/// a continuation that requires no contexts is passed to a function that calls it with its members. An argument or
/// a result boxed where the other type has the value, or the other way round, is converted (see
/// `CoreIrLowerBoxes`): a box of unit is passed where the value takes one and the adapter is given nothing, and none
/// where the adapter is given one and the value takes nothing.
let private adapting (tools: LiftedTools) (functionName: string) (fromMember: ContextMemberKey option) (givenTy: IrType) (expectedTy: IrType) : Function =
    let givenInput, givenOutput, given, result, wanted =
        match givenTy, expectedTy with
        | IrFunction(givenInput, givenOutput, given, _), IrFunction(_, result, wanted, _) -> givenInput, givenOutput, given, result, wanted
        | _ -> internalError $"an adapter from '{IrType.show givenTy}' to '{IrType.show expectedTy}'"
    let missing = given |> List.filter (fun (key, _) -> not (wanted |> List.exists (fst >> (=) key)))
    let held = if fromMember.IsSome then [] else [ "1", givenTy ]
    let environment = tools.NewVar ClosureSyntax.envParameter Param (IrRecord(("0", IrWord) :: held @ (missing |> List.mapi (fun index (_, ty) -> string (index + 2), ty))))
    let valueTys = adaptedValueTypes givenTy expectedTy
    let values = valueTys |> List.mapi (fun index ty -> tools.NewVar $"__arg{index + 1}" Param ty)
    let passed = wanted |> List.map (fun (key, ty) -> key, tools.NewVar (contextMemberParameter key) Param ty)
    let scope = { tools.EmptyScope with Members = passed |> Map.ofList }
    let adapted = tools.NewVar "__adapted" Local givenTy
    let givenValueTys = valuesOf givenInput
    let boxing = CoreIrLowerBoxes.needsConversion givenTy expectedTy
    let rec call (remaining: (ContextMemberKey * IrType) list) (members: Atom list) =
        match remaining with
        | [] ->
            let arguments = values |> List.map AVar
            let call arguments =
                let called = Expr.make (CallClosure(adapted, arguments @ List.rev members, Borrowing)) givenOutput
                if CoreIrLowerBoxes.needsConversion givenOutput result || CoreIrLowerMembers.differInContexts givenOutput result then
                    tools.BindTemp called (fun value -> tools.Coerce scope value result (fun atom -> Expr.make (Atom atom) result))
                else { called with Type = result }
            match givenValueTys, valueTys with
            | _ when givenValueTys.Length = values.Length -> tools.CoerceAll scope arguments givenValueTys call
            | [ IrBox ], [] -> tools.CoerceAll scope [ AUnit ] givenValueTys call
            | [], [ IrBox ] -> call []
            // A function that takes a tuple's elements, called as one that takes one boxed value, the tuple: as a
            // builder's `for` calls the body of a loop over a map, whose items are its entries (#2683).
            | _ :: _ :: _, [ IrBox ] ->
                let tupleTy = IrRecord(givenValueTys |> List.mapi (fun index ty -> string index, ty))
                tools.CoerceAll scope arguments [ tupleTy ] (fun converted ->
                    tools.BindLocal "__tuple" (Expr.make (Atom converted.Head) tupleTy) (fun tuple ->
                        let rec elements (index: int) (acc: Atom list) =
                            if index = givenValueTys.Length then call (List.rev acc)
                            else
                                tools.BindLocal "__element" (Expr.make (Field(tuple, index)) givenValueTys[index]) (fun element ->
                                    elements (index + 1) (element :: acc))
                        elements 0 []))
            | _ -> call arguments
        | (key, ty) :: rest ->
            // The members wait in locals while arguments are converted to or from boxes, which stores.
            let next (atom: Atom) =
                if boxing then tools.Settle (Expr.make (Atom atom) (atomType atom)) (fun atom -> call rest (atom :: members))
                else call rest (atom :: members)
            match missing |> List.tryFindIndex (fst >> (=) key) with
            | Some index -> (if boxing then tools.BindLocal "__member" else tools.BindTemp) (Expr.make (Field(AVar environment, index + 2)) ty) next
            | None -> tools.Coerce scope (AVar scope.Members.[key]) ty next
    let value =
        match fromMember with
        | Some key -> Expr.make (Atom(AVar scope.Members.[key])) givenTy
        | None -> Expr.make (Field(AVar environment, 1)) givenTy
    let body = Expr.make (Let(adapted, value, call given [])) result
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = environment :: values @ List.map snd passed
      Result = result
      Body = body
      Place = None }

/// The adapter `functionName` that lets a function value of type `givenTy` be called as one of `expectedTy` (see
/// `adapting`), holding the value.
let adapter (tools: LiftedTools) (functionName: string) (givenTy: IrType) (expectedTy: IrType) : Function =
    adapting tools functionName None givenTy expectedTy

/// The function `functionName` that a context's member of the key `key`, named as a value of type `expectedTy`, is a
/// closure over: it holds nothing, and calls the member it is passed with that key, as a call of the member where the
/// value is called would. What it calls is the instance where it is called, never one it was named under.
let memberCalling (tools: LiftedTools) (functionName: string) (key: ContextMemberKey) (expectedTy: IrType) : Function =
    match expectedTy with
    | IrFunction(_, _, wanted, _) ->
        match wanted |> List.tryFind (fst >> (=) key) with
        | Some (_, memberTy) -> adapting tools functionName (Some key) memberTy expectedTy
        | None -> internalError $"the member '{ContextMemberKey.text key}' named as a value of '{IrType.show expectedTy}', which doesn't take it"
    | _ -> internalError $"the member '{ContextMemberKey.text key}' named as a value of '{IrType.show expectedTy}'"

/// The function `functionName` that a call of a definition past its cap on specialisations calls (see
/// `CoreIrLowerModule.boxedInstead`): it takes what a specialisation of type `expectedTy` would, its values and the
/// members it requires, converts each to the type the specialisation `boxed`, of type `boxedTy`, with its type
/// arguments' values boxed, takes it at, calls it, and converts what it gives back.
let boxedCalling (tools: LiftedTools) (functionName: string) (boxed: string) (boxedTy: IrType) (expectedTy: IrType) : Function =
    match boxedTy, expectedTy with
    | IrFunction(boxedInput, boxedOutput, boxedMembers, _), IrFunction(input, result, members, _) ->
        let values = valuesOf input |> List.mapi (fun index ty -> tools.NewVar $"__arg{index + 1}" Param ty)
        let passed = members |> List.map (fun (key, ty) -> key, tools.NewVar (contextMemberParameter key) Param ty)
        let scope = { tools.EmptyScope with Members = passed |> Map.ofList }
        let call (arguments: Atom list) (memberAtoms: Atom list) =
            let called = Expr.make (Call(boxed, arguments @ memberAtoms)) boxedOutput
            if CoreIrLowerBoxes.needsConversion boxedOutput result || CoreIrLowerMembers.differInContexts boxedOutput result then
                tools.BindTemp called (fun value -> tools.Coerce scope value result (fun atom -> Expr.make (Atom atom) result))
            else
                { called with Type = result }
        let memberAtoms = boxedMembers |> List.map (fun (key, _) -> AVar scope.Members.[key])
        let body =
            tools.CoerceAll scope memberAtoms (List.map snd boxedMembers) (fun memberAtoms ->
                tools.CoerceAll scope (values |> List.map AVar) (valuesOf boxedInput) (fun arguments -> call arguments memberAtoms))
        { Name = functionName
          Source = functionName
          TypeArguments = []
          Params = values @ List.map snd passed
          Result = result
          Body = body
          Place = None }
    | _ -> internalError $"a call through a boxed specialisation from '{IrType.show expectedTy}' to '{IrType.show boxedTy}'"
