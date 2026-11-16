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

/// The lifted lambda `name`, whose parameters are `parameterNames` (its environment first) and whose body is `body`,
/// as a function made by `make`, its closure capturing values of `capturedTys` and the local functions in
/// `capturedLocals`. `scopeOf` is the scope its parameters are in.
let liftedCapturing (tools: LiftedTools) (name: string) (parameterNames: string list) (body: Expression) (scopeOf: Var list -> Scope) (make: Var list -> IrType -> Expr -> Function) (capturedTys: IrType list) (capturedLocals: Map<string, Ty * (IrType list * int) list>) : Function =
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
    let valueParameters, bind, named = tools.ValueParameters parameterNames.Tail (List.map tools.ToIrType parameterTys)
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
            Vars = members |> List.fold (fun acc (key, var) -> acc |> Map.add (contextMemberParameter key) var) scope.Vars }
    make (environmentParameter :: valueParameters @ List.map snd members) (tools.ToIrType resultTy) (bind (tools.Tail scope body))

/// The wrapper, whose parameters are `parameterNames` (its environment first), that lets the top-level function
/// `wrapped` be a value of type `instanceTy`, as a function made by `make`, its closure capturing the context members
/// `captured`.
let wrapperCalling (tools: LiftedTools) (wrapped: string) (instanceTy: Ty) (captured: (ContextMemberKey * IrType) list) (parameterNames: string list) (make: Var list -> IrType -> Expr -> Function) : Function =
    let parameterOf (parameterName: string) (ty: IrType) = tools.NewVar (tools.UniqueName parameterName) Param ty
    // Takes an environment, which holds after its table slot the members of the contexts the function requires
    // that the closure captured, and after its other parameters those its value's type requires; and calls the
    // function with the rest and them all, in the order it takes them.
    let parameterTys, resultTy = parameterTypes parameterNames.Tail instanceTy true
    let environment = IrRecord(("0", IrWord) :: (captured |> List.mapi (fun index (_, ty) -> string (index + 1), ty)))
    let environmentParameter = parameterOf parameterNames.Head environment
    let valueParameters, bind, named = tools.ValueParameters parameterNames.Tail (List.map tools.ToIrType parameterTys)
    let passed =
        match tools.ToIrType instanceTy with
        | IrFunction(_, _, members) -> members |> List.map (fun (key, ty) -> key, tools.NewVar (contextMemberParameter key) Param ty)
        | _ -> []
    let declared = tools.Input.ContextParameters |> Map.tryFind wrapped |> Option.map (fst >> List.map _.Key) |> Option.defaultValue []
    let order = declared @ (tools.VariableMembers wrapped (tools.BindingsAt wrapped instanceTy) |> List.map fst)
    let rec call (remaining: ContextMemberKey list) (members: Atom list) =
        match remaining with
        | [] -> Expr.make (Call(tools.Callee wrapped instanceTy, (named |> List.map AVar) @ List.rev members)) (tools.ToIrType resultTy)
        | key :: rest ->
            match captured |> List.tryFindIndex (fst >> (=) key), passed |> List.tryFind (fst >> (=) key) with
            | Some index, _ ->
                tools.BindTemp (Expr.make (Field(AVar environmentParameter, index + 1)) (snd captured.[index])) (fun atom -> call rest (atom :: members))
            | None, Some (_, var) -> call rest (AVar var :: members)
            | None, None -> internalError $"the context member {ContextMemberKey.text key} of '{wrapped}' is neither captured nor passed"
    make (environmentParameter :: valueParameters @ List.map snd passed) (tools.ToIrType resultTy) (bind (call order []))

/// The adapter `functionName` that lets a function value of type `givenTy` be called as one of `expectedTy` (see
/// `Coerce`): it takes an environment that holds the value, then the members `givenTy` requires that `expectedTy`
/// does not, and its arguments and the members `expectedTy` requires; and calls the value with the arguments and
/// the members it requires. An argument that is a function is adapted in turn to the type the value takes it at, as
/// a continuation that requires no contexts is passed to a function that calls it with its members. An argument or
/// a result boxed where the other type has the value, or the other way round, is converted (see
/// `CoreIrLowerBoxes`): a box of unit is passed where the value takes one and the adapter is given nothing, and none
/// where the adapter is given one and the value takes nothing.
let adapter (tools: LiftedTools) (functionName: string) (givenTy: IrType) (expectedTy: IrType) : Function =
    let givenInput, givenOutput, given, result, wanted =
        match givenTy, expectedTy with
        | IrFunction(givenInput, givenOutput, given), IrFunction(_, result, wanted) -> givenInput, givenOutput, given, result, wanted
        | _ -> internalError $"an adapter from {IrType.show givenTy} to {IrType.show expectedTy}"
    let missing = given |> List.filter (fun (key, _) -> not (wanted |> List.exists (fst >> (=) key)))
    let environment = tools.NewVar ClosureSyntax.envParameter Param (IrRecord(("0", IrWord) :: ("1", givenTy) :: (missing |> List.mapi (fun index (_, ty) -> string (index + 2), ty))))
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
            | _ -> call arguments
        | (key, ty) :: rest ->
            // The members wait in locals while arguments are converted to or from boxes, which stores.
            let next (atom: Atom) =
                if boxing then tools.Settle (Expr.make (Atom atom) (atomType atom)) (fun atom -> call rest (atom :: members))
                else call rest (atom :: members)
            match missing |> List.tryFindIndex (fst >> (=) key) with
            | Some index -> (if boxing then tools.BindLocal "__member" else tools.BindTemp) (Expr.make (Field(AVar environment, index + 2)) ty) next
            | None -> tools.Coerce scope (AVar scope.Members.[key]) ty next
    let body = Expr.make (Let(adapted, Expr.make (Field(AVar environment, 1)) givenTy, call given [])) result
    { Name = functionName
      Source = functionName
      TypeArguments = []
      Params = environment :: values @ List.map snd passed
      Result = result
      Body = body }
