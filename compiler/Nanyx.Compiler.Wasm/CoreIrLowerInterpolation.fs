/// How the lowering (see `CoreIrLower`) lowers an interpolated string: each hole evaluated into its local, in order,
/// and then the string built from the text and them (`Interpolate` in `CoreIr.fs`).
module internal Transpiler.Wasm.CoreIrLowerInterpolation

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput
open Transpiler.Wasm.CoreIrLowerScope

/// What lowering an interpolated string needs from the function being lowered.
type InterpolationTools =
    { Input: LowerInput
      /// The function's type substitution, as it is now.
      Subst: unit -> Map<int, Ty>
      /// The IR type of a type.
      ToIrType: Ty -> IrType
      /// The type of an expression, at the function's type arguments.
      TypeOf: Expression -> IrType
      /// The type of an expression in a scope.
      TypeIn: Scope -> Expression -> IrType
      /// A variable of this name, kind and type, named as it is.
      NewVar: string -> VarKind -> IrType -> Var
      /// Lowers an expression to a value that is not a `Let`, and passes that on.
      Complex: Scope -> Expression -> (Expr -> Expr) -> Expr
      /// Lowers expressions to atoms, and passes them on.
      Atoms: Scope -> Expression list -> (Atom list -> Expr) -> Expr }

/// How the hole `hole` of an interpolated string is printed, from its type. A tag's cases are in the order its type
/// names them, which is the order they are tested in.
let holeType (tools: InterpolationTools) (scope: Scope) (hole: Expression) : HoleType =
    match tools.TypeIn scope hole with
    | IrString -> HoleString
    | IrInt -> HoleInt
    | IrSized integer -> HoleSized integer
    | IrBool -> HoleBool
    | IrFloat | IrF32 -> HoleFloat
    | IrTags tags ->
        // In the order the hole's type names them, where the type checker gave the hole one.
        let rec cases (ty: Ty) =
            match Unifier.unalias ty with
            | TyTag(name, payload) -> [ name, payload |> Option.map tools.ToIrType ]
            | TyUnion items -> items |> List.collect cases
            | TyVar _ -> []
            | _ -> cannot "interpolating a union with tags not known here"
        let ordered =
            match tools.Input.ExprTypes |> Map.tryFind hole.Id |> Option.map (substitute (tools.Subst ())) with
            | Some ty when (match Unifier.unalias ty with TyTag _ | TyUnion _ -> true | _ -> false) -> cases ty
            | _ -> tags
        ordered
        |> List.distinctBy fst
        |> List.map (fun (name, payloadTy) ->
            match payloadTy with
            | None
            | Some (IrString | IrInt | IrBool) -> name, payloadTy
            | Some other -> cannot $"interpolating `#{name}` with a payload of type '{IrType.show other}'")
        |> HoleTag
    | other -> cannot $"interpolating a value of type '{IrType.show other}'"

/// The interpolated string of `parts`, at `expr`.
let lowerInterpolation (tools: InterpolationTools) (scope: Scope) (expr: Expression) (parts: StringPart list) (k: Expr -> Expr) : Expr =
    // A hole the compiler writes itself holds its value, not a call to convert it.
    if parts |> List.exists (function StringExpr hole -> tools.Input.BuiltInHoles.Contains hole.Id | StringText _ -> false) then
        let written =
            parts
            |> List.map (function
                | StringExpr (Kind (FunctionCall(_, _, [ value ])) as hole) when tools.Input.BuiltInHoles.Contains hole.Id -> StringExpr value
                | part -> part)
        tools.Complex scope { expr with Kind = InterpolatedString written } k
    elif parts |> List.exists (function StringExpr hole -> tools.TypeOf hole = IrError | StringText _ -> false) then
        // A hole of no known type cannot be printed. The holes are evaluated in order up to it, for what they do (the
        // hole itself most likely stops the program, through the deferred error that left its type unknown), and if
        // the program is still running then, it stops there.
        let insideHole = { scope with InterpolationDepth = scope.InterpolationDepth + 1 }
        let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
        let before = holes |> List.takeWhile (fun hole -> tools.TypeOf hole <> IrError)
        let unknown = holes |> List.find (fun hole -> tools.TypeOf hole = IrError)
        let stop = Expr.make (Crash([ Interpolation.unknownHoleMessage (DbgBuild.sourceRoot tools.Input.Dbg) unknown.Span ], true)) IrString
        (before @ [ unknown ])
        |> List.rev
        |> List.fold (fun (rest: Expr) hole -> tools.Complex insideHole hole (fun value -> Expr.make (Do(value, rest)) rest.Type)) stop
        |> k
    else
        // Each hole is evaluated into its local, in order, and then the string is built from the text and them.
        // A string inside a hole is a level deeper, so that it keeps to its own hole locals.
        let depth = scope.InterpolationDepth
        let insideHole = { scope with InterpolationDepth = depth + 1 }
        let holes = parts |> List.choose (function StringExpr hole -> Some hole | StringText _ -> None)
        let rec evaluateHoles (index: int) (remaining: Expression list) (evaluated: (Var * HoleType) option list) =
            match remaining with
            | [] ->
                let mutable next = List.rev evaluated
                let irParts =
                    parts
                    |> List.map (function
                        | StringText text -> Text text
                        | StringExpr _ ->
                            let held = next.Head
                            next <- next.Tail
                            match held with
                            | Some (var, holeType) -> Hole(var, holeType)
                            | None -> Text "()")
                k (Expr.make (Interpolate irParts) IrString)
            // The unit value is written `()`, as a derived `toString` writes it. It is still evaluated, for what it does:
            // the instance passed for the elements of `[]` writes one (see `unconstrainedAsUnit`).
            | hole :: rest when tools.TypeOf hole = IrUnit ->
                let body = evaluateHoles (index + 1) rest (None :: evaluated)
                tools.Complex insideHole hole (fun value -> Expr.make (Do(value, body)) body.Type)
            | hole :: rest ->
                let holeType = holeType tools scope hole
                // A float or an `F32` is formatted to a string as its hole is evaluated, since that allocates.
                let local = tools.NewVar (Interpolation.holeLocalName depth index) (InterpolationHole(depth, index)) (if holeType = HoleFloat then IrString else tools.TypeOf hole)
                let store (value: Expr) =
                    let body = evaluateHoles (index + 1) rest (Some (local, holeType) :: evaluated)
                    Expr.make (Let(local, value, body)) body.Type
                if holeType = HoleFloat then
                    let format = if tools.TypeOf hole = IrF32 then Helpers.f32ToString else Helpers.floatToString
                    tools.Atoms insideHole [ hole ] (fun atoms -> store (Expr.make (CallRuntime(format, atoms, true)) IrString))
                else tools.Complex insideHole hole store
        evaluateHoles 0 holes []
