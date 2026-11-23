/// The locals a function the emitter writes (see `CoreIrEmit`) declares: the local each variable is kept in, and those
/// the function declares beyond its parameters.
module internal Transpiler.Wasm.CoreIrEmitLocals

open Transpiler.Wasm.CoreIr

/// The name of the local `var` is kept in: its name, with its value type after it (`_i64`, `_f64` or `_f32`) for a value
/// not held in an `i32`.
let localName (var: Var) =
    match CoreIrLayout.valueType var.Type with
    | "i32" -> var.Name
    | valueType -> $"{var.Name}_{valueType}"

/// The locals a function declares beyond its parameters, in this order: its own locals, the scrutinees', what patterns
/// bind, what interpolated strings need (their scratch locals, then their holes by depth), and its scratch locals.
let declaredLocals (body: Expr) : (string * string) list =
    let found = ResizeArray<Var>()
    // The holes of the interpolated strings, by depth and place.
    let holes = ResizeArray<int * int * Var>()
    let interpolates = ref false
    // The locals of the `for` loops: each loop's, then those of the loops in its body.
    let loops = ResizeArray<Var>()
    let rec pattern (p: Pattern) =
        match p with
        | PBind var -> found.Add var
        | PGuard(var, guard) ->
            found.Add var
            walk guard
        | _ -> Pattern.parts p |> List.iter pattern
    // What the expression declares itself, then the expressions inside it (see `Expr.children`), the last in tail
    // position, so that a chain as long as a function takes no stack to walk.
    and walk (expr: Expr) =
        match expr.Kind with
        // What a pattern binds is declared in its place among its guards' locals, so a match walks its arms' patterns
        // itself.
        | Match(_, arms) ->
            for arm in arms do
                arm.Patterns |> List.iter pattern
                walk arm.Body
        | _ ->
            match expr.Kind with
            | Let(var, _, _) ->
                match var.Kind with
                | Temp -> ()
                | InterpolationHole(depth, index) -> holes.Add((depth, index, var))
                | _ -> found.Add var
            | Release(released, _) -> released.Token |> Option.iter found.Add
            | Interpolate _ -> interpolates.Value <- true
            | ForEach loop -> ForLoop.locals loop |> List.iter loops.Add
            | _ -> ()
            match Expr.children expr with
            | [] -> ()
            | children ->
                for child in List.take (children.Length - 1) children do
                    walk child
                walk (List.last children)
    walk body
    let declared (var: Var) = localName var, CoreIrLayout.valueType var.Type
    let ofKind kind = found |> Seq.filter (fun var -> var.Kind = kind) |> Seq.map declared |> List.ofSeq
    // A hole is declared wherever it is bound: the string it was for may be gone, as one whose value nothing reads is.
    let interpolationLocals =
        (if interpolates.Value then Interpolation.scratchLocals |> List.map (fun name -> name, "i32") else [])
        @ (holes |> Seq.sortBy (fun (depth, index, var) -> depth, index, localName var) |> Seq.map (fun (_, _, var) -> declared var) |> List.ofSeq)
    [ yield! ofKind Local
      yield! ofKind MatchTemp
      yield! ofKind PatternBound
      yield! interpolationLocals
      yield! loops |> Seq.map declared
      yield! ofKind Scratch ]
    |> List.distinct

/// The declarations of `locals`, each a name and its value type.
let localDeclarations (locals: (string * string) list) =
    locals |> List.map (fun (name, valueType) -> $"(local ${name} {valueType})") |> String.concat " "
