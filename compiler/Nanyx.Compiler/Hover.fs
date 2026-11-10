/// What an editor shows when the pointer rests on an expression: its inferred type. Positions are character
/// offsets into the source as the parser saw it, with line endings normalised to `\n`.
module NyxCompiler.Hover

open Parser.Program

/// The type shown for a hover and the span of the syntax it describes.
type HoverInfo = { Type: string; Span: SourceSpan }

let private contains (offset: int) (span: SourceSpan) =
    not (SourceSpan.isNone span) && span.Start <= offset && offset < span.End

/// The expressions directly inside `expr`, including those in its statements, match arms and patterns.
let rec private children (expr: Expression) : Expression list =
    match expr.Kind with
    | LiteralExpr _
    | UnitExpr
    | IdentifierExpr _
    | ContextMemberAccess _ -> []
    | FunctionCall(_, _, args)
    | ContextMemberCall(_, _, args) -> args
    | Lambda(_, body) -> [ body ]
    | BinaryOp(_, left, right) -> [ left; right ]
    | Pipe(value, _, _, args) -> value :: args
    | Block statements -> statements |> List.collect statementExpressions
    | Match(values, arms) ->
        values @ (arms |> List.collect (fun (patterns, armExpr) -> (patterns |> List.collect Pattern.expressions) @ [ armExpr ]))
    | Except(value, arms) -> value :: Pattern.armExpressions arms @ (arms |> List.map snd)
    | TupleExpr items
    | ListExpr items -> items
    | RecordExpr fields ->
        fields |> List.map (function NamedField(_, value) | PositionalField value -> value)
    | TagExpr(_, payload) -> Option.toList payload
    | IfExpr(cond, thenExpr, elseExpr) -> [ cond; thenExpr; elseExpr ]
    | MemberAccess(target, _, _) -> [ target ]
    | UseIn(value, body) -> [ value; body ]
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> [ value ]
    | InterpolatedString parts -> parts |> List.choose (function StringExpr value -> Some value | StringText _ -> None)

and private statementExpressions (statement: Statement) : Expression list =
    match statement.Kind with
    | DefStatement(_, _, _, value)
    | ExprStatement value
    | UseStatement value -> [ value ]
    | TypeDefStatement _
    | ImportStatement _ -> []

let rec private topLevelExpressions (item: TopLevelItem) : Expression list =
    match item.Kind with
    | Def (ValueDef(_, _, _, value))
    | Expr value -> [ value ]
    | ModuleBlock(_, items) -> items |> List.collect topLevelExpressions
    | Def (TypeDef _)
    | ModuleDecl _
    | Import _
    | Attribute _ -> []

/// The expressions whose spans contain `offset`, innermost first. Only expressions that contain the offset are
/// searched, and those without a span of their own (made-up parents), so a lookup is proportional to depth.
let expressionsAt (offset: int) (module': Module) : Expression list =
    // Each expression found is pushed before its children are searched, so they end up in front of it.
    let rec search (found: Expression list) (expr: Expression) : Expression list =
        if contains offset expr.Span then children expr |> List.fold search (expr :: found)
        elif SourceSpan.isNone expr.Span then children expr |> List.fold search found
        else found
    module' |> List.collect topLevelExpressions |> List.fold search []

/// Every expression in `module'`, each before those inside it.
let allExpressions (module': Module) : Expression list =
    let rec walk (expr: Expression) : Expression list = expr :: (children expr |> List.collect walk)
    module' |> List.collect topLevelExpressions |> List.collect walk

/// The types written in a definition: its annotation, or a type definition's parameters' constraints and body.
let private definitionTypes (definition: Definition) : TypeExpr list =
    match definition with
    | ValueDef(_, _, typeOpt, _) -> Option.toList typeOpt
    | TypeDef(_, _, parameters, body) -> (parameters |> List.choose snd) @ [ body ]

/// The types written directly in `expr` (not in the expressions inside it): its lambda's parameters' annotations,
/// its block's defs' annotations and type definitions, the context it names.
let expressionTypes (expr: Expression) : TypeExpr list =
    match expr.Kind with
    | Lambda(parameters, _) -> parameters |> List.choose snd
    | Block statements ->
        statements
        |> List.collect (fun statement ->
            match statement.Kind with
            | DefStatement(_, _, typeOpt, _) -> Option.toList typeOpt
            | TypeDefStatement(_, _, parameters, body) -> (parameters |> List.choose snd) @ [ body ]
            | UseStatement _
            | ExprStatement _
            | ImportStatement _ -> [])
    | ContextMemberAccess(context, _)
    | ContextMemberCall(context, _, _) -> [ context ]
    | _ -> []

let rec topLevelTypes (item: TopLevelItem) : TypeExpr list =
    match item.Kind with
    | Def definition -> definitionTypes definition
    | ModuleBlock(_, items) -> items |> List.collect topLevelTypes
    | Expr _
    | ModuleDecl _
    | Import _
    | Attribute _ -> []

/// The type expressions whose spans contain `offset`, innermost first.
let typeExprsAt (offset: int) (module': Module) : TypeExpr list =
    let rec search (found: TypeExpr list) (typeExpr: TypeExpr) : TypeExpr list =
        if contains offset typeExpr.Span then TypeExpr.children typeExpr |> List.fold search (typeExpr :: found)
        else found
    let written = (module' |> List.collect topLevelTypes) @ (expressionsAt offset module' |> List.collect expressionTypes)
    written |> List.fold search []

/// The type of a def, written for a reader as `type` is, after the contexts it requires, as its annotation would write
/// them: `[Show(α)] α -> string`. `written` are the contexts as written for a reader, named together with the type.
let withContexts (written: string list) (typeWritten: string) =
    match written with
    | [] -> typeWritten
    | _ -> $"""[{String.concat ", " written}] {typeWritten}"""

/// The span of a call's or pipe's function name, when it has one.
let private calleeSpan (expr: Expression) =
    match expr.Kind with
    | FunctionCall(_, nameSpan, _)
    | Pipe(_, _, nameSpan, _) -> nameSpan
    | _ -> None

/// The hover for the expression at `offset` in the compiled root module: the type of the narrowest expression
/// there that has one, or of the function itself on a call's function name, where it is instantiated at that
/// call, or of what a declaration's own name declares (a def, a parameter, a pattern variable), or of what a `_` in
/// a type was inferred to be, with made-up type variables named by `names`. `None` when the module did not type-check
/// or nothing typed is at `offset`.
///
/// The narrowest span wins rather than the deepest node, because a made-up node can sit inside the syntax it
/// stands for: a `for` loop's element is an identifier spanning all of the loop's arms. Of equal spans the
/// outermost wins, for the same reason: the parameters a shorthand match lambda makes up span the whole lambda.
let typeAt (names: TypeNotation) (result: CompileResult) (offset: int) : HoverInfo option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        // Only the module's own declarations have types recorded, so no imported one is found here. A def that does
        // not write its type shows the contexts it was inferred to require too.
        let declarations =
            typed.SymbolTypes
            |> Map.toList
            |> List.choose (fun (id, ty) ->
                let declaration = typed.Symbols.[id].Declaration
                let contexts = typed.InferredContextTypes |> Map.tryFind id |> Option.defaultValue []
                if contains offset declaration then Some (ty, contexts, declaration) else None)
        let candidates =
            // A declaration's name is written as it is, so it comes before any expression of the same span.
            (declarations |> List.map (fun (ty, contexts, span) -> ty, contexts, span, System.Int32.MaxValue, 0))
            @ (expressionsAt offset syntax
            |> List.mapi (fun depth expr ->
                let callee =
                    match calleeSpan expr with
                    | Some nameSpan when contains offset nameSpan ->
                        typed.CalleeTypes |> Map.tryFind expr.Id |> Option.map (fun ty -> ty, [], nameSpan, depth, 0)
                    | _ -> None
                let own = typed.ExprTypes |> Map.tryFind expr.Id |> Option.map (fun ty -> ty, [], expr.Span, depth, 1)
                Option.toList callee @ Option.toList own)
            |> List.concat)
            // A `_` in a type shows what it was inferred to be.
            @ (typeExprsAt offset syntax
            |> List.choose (fun typeExpr ->
                match typeExpr.Kind with
                | TypeHole -> typed.ExprTypes |> Map.tryFind typeExpr.Id |> Option.map (fun ty -> ty, [], typeExpr.Span, 0, 0)
                | _ -> None))
        match candidates with
        | [] -> None
        | _ ->
            // Depth counts outwards from the innermost; a callee's name is inside its call, so it comes first.
            let ty, contexts, span, _, _ =
                candidates |> List.minBy (fun (_, _, span, depth, rank) -> span.End - span.Start, -depth, rank)
            let written =
                match contexts, Unifier.tysToDisplayStrings names (ty :: contexts) with
                | _ :: _, typeWritten :: contextsWritten -> withContexts contextsWritten typeWritten
                | _ -> Unifier.tyToDisplayString names ty
            Some { Type = written; Span = span }
    | _ -> None
