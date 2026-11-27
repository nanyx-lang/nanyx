/// How the lowering (see `CoreIrLower`) lowers a local function generic in type variables of its own: a closure for
/// each type arguments it is used at (`Scope.LocalInstances`), and, in a lambda that captures it, a field of the
/// lambda's environment for each (`Scope.CapturedLocals`).
module internal Transpiler.Wasm.CoreIrLowerLocals

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLowerScope

/// What lowering a generic local function needs from the function being lowered.
type LocalTools =
    { Input: LowerInput
      /// The definitions being lowered, lifted lambdas among them, by name.
      Definitions: Map<string, Expression>
      /// A variable of this name, kind and type, named as it is.
      NewVar: string -> VarKind -> IrType -> Var
      /// A parameter's or local's name: as written, made unique within the function.
      UniqueName: string -> string
      /// A type at the function's type arguments.
      Substitute: Ty -> Ty
      ToIrType: Ty -> IrType
      /// The closure block, at an expression, of a lifted lambda over what it captures, at the type arguments the
      /// bindings give (see `FunctionLowering.Closure`).
      Closure: Scope -> Expression -> string -> Expression list -> Map<int, Ty> -> IrType list -> (Expr -> Expr) -> Expr
      /// Lowers a block, its value passed on.
      LowerBlock: Scope -> StatementKind list -> (Expr -> Expr) -> Expr }

/// The type arguments the local function of type `localTy`, generic in variables of its own, is used at where the
/// use has the type `instanceTy`, and what they bind; none where that is not known.
let private localTypeArguments (tools: LocalTools) (localTy: Ty) (instanceTy: Ty option) : (IrType list * Map<int, Ty>) option =
    instanceTy
    |> Option.bind (fun instanceTy ->
        try
            let bindings = bindScheme Map.empty localTy (tools.Substitute instanceTy)
            let typeArguments =
                freeVars localTy
                |> List.distinct
                |> List.sort
                |> List.map (fun id -> bindings |> Map.tryFind id |> Option.map tools.ToIrType |> Option.defaultValue IrUnit)
            Some (typeArguments, bindings)
        with CannotLower _ -> None)

/// The local functions generic in variables of their own that `expr` defines, by the lifted lambda each is a closure
/// over: the local's name and its type.
let private genericLocalsIn (tools: LocalTools) (bindings: Map<int, Ty>) (expr: Expression) : Map<string, string * Ty> =
    SyntaxTree.expressionsWithin expr
    |> List.collect (fun within ->
        match within.Kind with
        | Block statements ->
            statements
            |> List.choose (fun statement ->
                match statement.Kind with
                | DefStatement(_, local, _, (Kind (ClosureSyntax.Closure(target, _)) as value)) ->
                    tools.Input.ExprTypes
                    |> Map.tryFind value.Id
                    |> Option.map (substitute bindings >> tools.Substitute)
                    |> Option.filter (fun localTy -> not (freeVars localTy).IsEmpty)
                    |> Option.map (fun localTy -> target, (local, localTy))
                | _ -> None)
        | _ -> [])
    |> Map.ofList

/// Each use in `expr` of the local `name`, with what binds the type variables it is at: `bindings`, and, for a use in a
/// local function generic in variables of its own that `expr` defines, what those bind at each type that function is
/// used at in `expr`, so that a use at one of its variables is at the types it is used at (#2813). A use in a lambda that
/// captures `name` counts, as `ClosureSyntax.usesThroughClosures` counts it. `visited` are the lifted lambdas already
/// looked in, which a local function that calls itself would look in again.
let rec private usesWithBindings (tools: LocalTools) (visited: Set<string>) (bindings: Map<int, Ty>) (name: string) (expr: Expression) : (Expression * Map<int, Ty>) list =
    let found = ClosureSyntax.references expr
    let closures =
        found
        |> List.choose (fun (_, node) ->
            match node.Kind with
            | ClosureSyntax.Closure(target, captured) ->
                let captures = captured |> List.filter (function Kind (IdentifierExpr captured) -> captured = name | _ -> false)
                if captures.IsEmpty || visited.Contains target then None else Some (target, captures)
            | _ -> None)
    let captures = closures |> List.collect snd
    let direct =
        found
        |> List.filter (fun (referenced, node) -> referenced = name && not (captures |> List.exists (fun capture -> obj.ReferenceEquals(capture, node))))
        |> List.map (fun (_, node) -> node, bindings)
    let genericLocals = lazy (genericLocalsIn tools bindings expr)
    let inLambda (target: string) =
        match tools.Definitions |> Map.tryFind target with
        | None -> []
        | Some definition ->
            let visited = visited.Add target
            let atBindings =
                match genericLocals.Force() |> Map.tryFind target with
                | Some (local, localTy) ->
                    match localUsesFrom tools visited bindings localTy local expr with
                    | [] -> [ bindings ]
                    | used -> used |> List.map (fun (_, own) -> own |> Map.fold (fun acc id ty -> acc |> Map.add id ty) bindings)
                | None -> [ bindings ]
            atBindings |> List.collect (fun bindings -> usesWithBindings tools visited bindings name definition)
    direct @ (closures |> List.map fst |> List.distinct |> List.collect inLambda)

/// `localUses`, not looking again in the lifted lambdas `visited`.
and private localUsesFrom (tools: LocalTools) (visited: Set<string>) (bindings: Map<int, Ty>) (localTy: Ty) (name: string) (expr: Expression) : (IrType list * Map<int, Ty>) list =
    usesWithBindings tools visited bindings name expr
    |> List.map (fun (reference, bindings) ->
        let instanceTy =
            match reference.Kind with
            | IdentifierExpr _ -> tools.Input.ExprTypes |> Map.tryFind reference.Id
            | _ -> tools.Input.CalleeTypes |> Map.tryFind reference.Id
        localTypeArguments tools localTy (instanceTy |> Option.map (substitute bindings)) |> Option.defaultValue ([], Map.empty))
    |> List.distinctBy fst

/// The type arguments, and what they bind, of each use in `expr` of `name`, a local function of type `localTy`
/// generic in variables of its own, where `bindings` binds the variables of the function `expr` is in as it says:
/// none where a use's type is not known. A use in a lambda that captures it counts, and one in a local function generic
/// in variables of its own counts at each type that function is used at (see `usesWithBindings`).
let localUses (tools: LocalTools) (bindings: Map<int, Ty>) (localTy: Ty) (name: string) (expr: Expression) : (IrType list * Map<int, Ty>) list =
    localUsesFrom tools Set.empty bindings localTy name expr

/// The closure of `var`, the local function `name` generic in variables of its own, at each type arguments in `used`
/// (with what they bind): the one bound where it is defined, or else one made here, for a use in a lambda whose
/// own type arguments are only known where its closure is made.
let private localInstanceVars (tools: LocalTools) (scope: Scope) (name: string) (var: Var) (used: (IrType list * Map<int, Ty>) list) (k: Var list -> Expr) : Expr =
    let _, instances, make = scope.LocalInstances.[var.Id]
    let rec go remaining acc =
        match remaining with
        | [] -> k (List.rev acc)
        | (typeArguments, bindings) :: others ->
            match instances |> List.tryFind (fst >> (=) typeArguments), make with
            | Some (_, instance), _ -> go others (instance :: acc)
            | None, Some make ->
                let localName = tools.UniqueName name
                make bindings typeArguments (fun value ->
                    let local = tools.NewVar localName Local value.Type
                    let body = go others (local :: acc)
                    Expr.make (Let(local, value, body)) body.Type)
            | None, None -> go others (var :: acc)
    go used []

/// `atoms`, what a closure over the lifted lambda `target` captures (`captured`, lowered), with each local function
/// generic in variables of its own among them captured at each type arguments the lambda uses it at, where
/// `bindings` binds the variables of the closure's type: the first in its own field, the others after everything
/// else, as the lambda's `Scope.CapturedLocals` says.
let capturedInstances (tools: LocalTools) (scope: Scope) (target: string) (bindings: Map<int, Ty>) (captured: Expression list) (atoms: Atom list) (k: Atom list -> Map<string, Ty * (IrType list * int) list> -> Expr) : Expr =
    let generic =
        List.zip captured atoms
        |> List.indexed
        |> List.choose (fun (index, (capture, atom)) ->
            match capture.Kind, atom with
            | IdentifierExpr name, AVar var when scope.LocalInstances.ContainsKey var.Id ->
                let localTy, _, _ = scope.LocalInstances.[var.Id]
                match localUses tools bindings localTy name tools.Definitions.[target] with
                | [] -> None
                | used -> Some (index, name, var, localTy, used)
            | _ -> None)
    let rec instancesOf remaining acc =
        match remaining with
        | [] ->
            let generic = List.rev acc
            let capturedLocals, others =
                generic
                |> List.fold (fun (locals: Map<string, _>, others: Var list) (index, name, localTy, instances) ->
                    let fields = instances |> List.mapi (fun i (typeArguments, _) -> typeArguments, (if i = 0 then index + 1 else atoms.Length + others.Length + i))
                    locals |> Map.add name (localTy, fields), others @ (List.tail instances |> List.map snd)) (Map.empty, [])
            let firsts = generic |> List.map (fun (index, _, _, instances) -> index, snd (List.head instances)) |> Map.ofList
            let atoms = (atoms |> List.mapi (fun index atom -> firsts |> Map.tryFind index |> Option.map AVar |> Option.defaultValue atom)) @ (others |> List.map AVar)
            k atoms capturedLocals
        | (index, name, var, localTy, used) :: others ->
            localInstanceVars tools scope name var used (fun vars ->
                instancesOf others ((index, name, localTy, List.zip (List.map fst used) vars) :: acc))
    instancesOf generic []

/// The variable to read for `var` where it is used at the type `instanceTy`: `var` itself, unless it holds a local
/// function generic in variables of its own, which has a closure for each type arguments it is used at.
let localAt (tools: LocalTools) (scope: Scope) (var: Var) (instanceTy: Ty option) : Var =
    match scope.LocalInstances |> Map.tryFind var.Id with
    | Some (localTy, instances, _) ->
        let typeArguments = localTypeArguments tools localTy instanceTy |> Option.map fst |> Option.defaultValue []
        instances |> List.tryFind (fst >> (=) typeArguments) |> Option.map snd |> Option.defaultValue var
    | None -> var

/// Binds `name`, a local function of type `localTy` generic in variables of its own, around `rest`, the statements
/// after it, to its closure at each type arguments it is used at there, which `closures` lowers; `make` lowers it at
/// others, where it can be.
let localInstancesAround (tools: LocalTools) (scope: Scope) (name: string) (localTy: Ty) (closures: (IrType list * ((Expr -> Expr) -> Expr)) list) (make: LocalClosure option) (rest: StatementKind list) (k: Expr -> Expr) : Expr =
    let rec bind (remaining: (IrType list * ((Expr -> Expr) -> Expr)) list) (bound: (IrType list * Var) list) =
        match remaining with
        | [] ->
            let var = bound |> List.tryFind (fst >> List.isEmpty) |> Option.defaultValue (List.head bound) |> snd
            tools.LowerBlock { scope with Vars = scope.Vars |> Map.add name var; LocalInstances = scope.LocalInstances |> Map.add var.Id (localTy, bound, make) } rest k
        | (typeArguments, closure) :: others ->
            let localName = tools.UniqueName name
            closure (fun value ->
                let local = tools.NewVar localName Local value.Type
                let body = bind others ((typeArguments, local) :: bound)
                Expr.make (Let(local, value, body)) body.Type)
    bind closures []

/// Binds `name`, a local function of type `localTy` generic in variables of its own, around `rest`, the statements
/// after it: a closure for each type arguments the function is used at in `rest`, the closure `rhs` over the lifted
/// lambda `target` lowered at them, so that what it does with a value of one of those variables (compares it, drops
/// it) is what the value's type does. A use in a lambda that captures it counts, and the lambda captures the closure
/// at each type arguments it uses it at. A use whose type is not known reads the closure lowered at none, with each
/// of those variables unit.
let genericLocal (tools: LocalTools) (scope: Scope) (name: string) (rhs: Expression) (target: string) (captured: Expression list) (localTy: Ty) (rest: StatementKind list) (k: Expr -> Expr) : Expr =
    // A use at a variable of another local function generic in variables of its own, as `eq(v, v)` in `def twice = { v
    // -> eq(v, v) }`, isn't at types known here: that function's closure captures this one at each type arguments it is
    // lowered at (see `capturedInstances`). Lowered here, at its variables, a context this one requires would have no
    // members (#2813).
    // The closure bound here then is at each of its variables unit, whose contexts have members.
    let atKnownTypes (_, bindings: Map<int, Ty>) = bindings |> Map.forall (fun _ ty -> (freeVars ty).IsEmpty)
    let used = localUses tools Map.empty localTy name (Expression.synthetic (Block(rest |> List.map Statement.synthetic)))
    let instances =
        match used |> List.filter atKnownTypes, used with
        | [], [] -> [ [], Map.empty ]
        | [], _ ->
            let vars = freeVars localTy |> List.distinct |> List.sort
            [ vars |> List.map (fun _ -> IrUnit), vars |> List.map (fun id -> id, TyPrimitive "unit") |> Map.ofList ]
        | known, _ -> known
    let make bindings typeArguments = tools.Closure scope rhs target captured bindings typeArguments
    let closures = instances |> List.map (fun (typeArguments, bindings) -> typeArguments, make bindings typeArguments)
    localInstancesAround tools scope name localTy closures (Some make) rest k
