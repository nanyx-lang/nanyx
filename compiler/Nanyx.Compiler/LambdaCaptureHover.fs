/// What hovering a lambda's opening `{` or its parameters adds to the hover: what the lambda captures, the names of the
/// code around it that its closure block carries, or that it captures nothing and so allocates nothing (#1651). The
/// names are those lambda lifting puts in the closure (`LambdaCaptures.capturedFrom`), so the hover and the generated
/// code agree.
module NyxCompiler.LambdaCaptureHover

open Parser.Program

/// A lambda that is made as a value, and so has a closure: each with the names of the def it is in that it could
/// capture, which lambda lifting counts as its scope, the def's parameters and every name its body binds, and, for a
/// `rec` local, its own name. A def's own lambda is the function being defined, and a `for` loop's lambda and a guard's
/// predicate are inlined where they are used, so none of them is one.
let private closuresIn (module': Module) : (Expression * Set<string> * string option) list =
    let within (scope: Set<string>) (body: Expression) =
        let expressions = Hover.expressionsWithin body
        let inlined =
            expressions
            |> List.collect (fun expr ->
                match expr.Kind with
                | ForSyntax.Call(_, loop, _, _) -> [ loop.Id ]
                | Match(_, arms) ->
                    arms
                    |> List.collect (fst >> List.collect Pattern.expressions)
                    |> List.choose (fun predicate -> match predicate.Kind with Lambda _ -> Some predicate.Id | _ -> None)
                | _ -> [])
            |> Set.ofList
        let recNames =
            expressions
            |> List.collect (fun expr ->
                match expr.Kind with
                | Block statements ->
                    statements
                    |> List.choose (fun statement ->
                        match statement.Kind with
                        | DefStatement(_, name, _, Kind (RecSyntax.Call lambda)) -> Some(lambda.Id, name)
                        | _ -> None)
                | _ -> [])
            |> Map.ofList
        expressions
        |> List.choose (fun expr ->
            match expr.Kind with
            | Lambda _ when not (inlined.Contains expr.Id) -> Some(expr, scope, recNames |> Map.tryFind expr.Id)
            | _ -> None)
    let rec ofItem (item: TopLevelItem) =
        match item.Kind with
        | Def (ValueDef(_, _, _, Kind (Lambda(args, body, _)))) ->
            within (Set.union (args |> List.map fst |> Set.ofList) (LambdaCaptures.boundNames body |> Set.ofList)) body
        | Def (ValueDef(_, _, _, rhs)) -> within (LambdaCaptures.boundNames rhs |> Set.ofList) rhs
        | Expr expr -> within (LambdaCaptures.boundNames expr |> Set.ofList) expr
        | ModuleBlock(_, items) -> items |> List.collect ofItem
        | _ -> []
    module' |> List.collect ofItem

/// The names `lambda` captures, in the order its body first refers to them, given its scope (see `closuresIn`) and its
/// name if it is a `rec` local's. The names the compiler makes up, which start with `__`, are left out.
let private captured (lambda: Expression) (scope: Set<string>) (recName: string option) =
    match lambda.Kind with
    | Lambda(args, body, _) ->
        let parameters = args |> List.map fst |> Set.ofList
        let own = recName |> Option.toList |> Set.ofList
        let bound = Set.unionMany [ parameters; LambdaCaptures.boundNames body |> Set.ofList; own ]
        let names = LambdaCaptures.capturedFrom scope (Set.union parameters own) bound body |> Set.ofList
        LambdaCaptures.references body
        |> List.map fst
        |> List.filter (fun name -> names.Contains name && not (name.StartsWith "__"))
        |> List.distinct
    | _ -> []

/// The names of the local `mut` cells defined in `module'`. A lambda captures only locals (see `closuresIn`).
let private cells (module': Module) =
    Hover.allExpressions module'
    |> List.collect (fun expr ->
        match expr.Kind with
        | Block statements ->
            statements
            |> List.choose (fun statement ->
                match statement.Kind with
                | DefStatement(_, name, _, Kind (MutSyntax.Call _)) -> Some name
                | _ -> None)
        | _ -> [])
    |> Set.ofList

/// The line the hover at `offset` in `syntax` adds for the narrowest lambda whose opening `{` or parameters are there:
/// "Captures nothing: no allocation", or "Captures `x`, `total` (allocates a closure)", with a captured `mut` cell
/// marked as one. `None` elsewhere, and on a lambda that has no closure (see `closuresIn`).
let at (syntax: Module) (offset: int) : string option =
    closuresIn syntax
    |> List.filter (fun (lambda, _, _) ->
        match lambda.Kind with
        | Lambda(_, body, _) -> Hover.contains offset lambda.Span && (SourceSpan.isNone body.Span || offset < body.Span.Start)
        | _ -> false)
    |> List.sortBy (fun (lambda, _, _) -> lambda.Span.End - lambda.Span.Start)
    |> List.tryHead
    |> Option.map (fun (lambda, scope, recName) ->
        match captured lambda scope recName with
        | [] -> "Captures nothing: no allocation"
        | names ->
            let mutCells = cells syntax
            let written = names |> List.map (fun name -> if mutCells.Contains name then $"`{name}` (a `mut` cell)" else $"`{name}`")
            $"""Captures {String.concat ", " written} (allocates a closure)""")
