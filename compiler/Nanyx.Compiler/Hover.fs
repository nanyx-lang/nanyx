/// What an editor shows when the pointer rests on an expression: its inferred type, after its name on a name.
/// Positions are character offsets into the source as the parser saw it, with line endings normalised to `\n`.
module NyxCompiler.Hover

open Parser.Program

/// What a hover shows, a type or a declaration written with its type (`def name: type`, `type Name = ...`), and the
/// span of the syntax it describes.
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

/// The contexts the top-level def `symbol` requires, the module's own or one an import brings in (see
/// `TypedModule.ContextTypes` and `ImportedContextTypes`), and the type `ty` it has where it is hovered, with the
/// contexts instantiated as `ty` is: at a call, `[Show(int)]` for the `int` it is called with.
let private withContextsAt (typed: TypedModule) (symbol: Symbol) (ty: Ty) : Ty * Ty list =
    let required =
        match typed.ContextTypes |> Map.tryFind symbol.Id, typed.SymbolTypes |> Map.tryFind symbol.Id with
        | Some contexts, Some general -> Some (general, contexts)
        | _ -> typed.ImportedContextTypes |> Map.tryFind symbol.Id
    match required with
    | Some (general, (_ :: _ as contexts)) ->
        match Unifier.unify [ general, ty, Equal, SourceSpan.none ] with
        | Ok subst -> Unifier.apply subst ty, contexts |> List.map (Unifier.apply subst)
        | Error _ -> ty, []
    | _ -> ty, []

/// What a hover on a name shows, `typeWritten` being its type as written for a reader: a top-level def as its
/// declaration would write it, `def name: type`, any other value as `name: type`.
let private declared (symbol: Symbol) (typeWritten: string) =
    match symbol.Kind with
    | TopLevelValue -> $"def {symbol.Name}: {typeWritten}"
    | LocalValue
    | Parameter
    | PatternVariable
    | ContextMember -> $"{symbol.Name}: {typeWritten}"
    | ImportedModule
    | TypeDefinition -> typeWritten

/// The hover for the expression at `offset` in the compiled root module: the type of the narrowest expression
/// there that has one, or of the function itself on a call's function name, where it is instantiated at that
/// call, or of what a declaration's own name declares (a def, a parameter, a pattern variable), or of what a `_` in
/// a type was inferred to be, with made-up type variables named by `names`. On a name, the declaration's own or one
/// that refers to it, the type comes after the name (see `declared`), and a top-level def, the module's own or an
/// imported one, shows the contexts it requires too. `None` when the module did not type-check or nothing typed is at `offset`.
///
/// The narrowest span wins rather than the deepest node, because a made-up node can sit inside the syntax it
/// stands for: a `for` loop's element is an identifier spanning all of the loop's arms. Of equal spans the
/// outermost wins, for the same reason: the parameters a shorthand match lambda makes up span the whole lambda.
let typeAt (names: TypeNotation) (result: CompileResult) (offset: int) : HoverInfo option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let referenced (expr: Expression) = typed.References |> Map.tryFind expr.Id |> Option.bind typed.Symbols.TryFind
        // Only the module's own declarations have types recorded, so no imported one is found here.
        let declarations =
            typed.SymbolTypes
            |> Map.toList
            |> List.choose (fun (id, ty) ->
                let symbol = typed.Symbols.[id]
                if contains offset symbol.Declaration then Some (ty, Some symbol, symbol.Declaration) else None)
        let candidates =
            // A declaration's name is written as it is, so it comes before any expression of the same span.
            (declarations |> List.map (fun (ty, symbol, span) -> ty, symbol, span, System.Int32.MaxValue, 0))
            @ (expressionsAt offset syntax
            |> List.mapi (fun depth expr ->
                let callee =
                    match calleeSpan expr with
                    | Some nameSpan when contains offset nameSpan ->
                        typed.CalleeTypes |> Map.tryFind expr.Id |> Option.map (fun ty -> ty, referenced expr, nameSpan, depth, 0)
                    | _ -> None
                let name =
                    match expr.Kind with
                    | IdentifierExpr _ -> referenced expr
                    | _ -> None
                let own = typed.ExprTypes |> Map.tryFind expr.Id |> Option.map (fun ty -> ty, name, expr.Span, depth, 1)
                Option.toList callee @ Option.toList own)
            |> List.concat)
            // A `_` in a type shows what it was inferred to be.
            @ (typeExprsAt offset syntax
            |> List.choose (fun typeExpr ->
                match typeExpr.Kind with
                | TypeHole -> typed.ExprTypes |> Map.tryFind typeExpr.Id |> Option.map (fun ty -> ty, None, typeExpr.Span, 0, 0)
                | _ -> None))
        match candidates with
        | [] -> None
        | _ ->
            // Depth counts outwards from the innermost; a callee's name is inside its call, so it comes first.
            let ty, symbol, span, _, _ =
                candidates |> List.minBy (fun (_, _, span, depth, rank) -> span.End - span.Start, -depth, rank)
            let ty, contexts =
                match symbol with
                | Some symbol when symbol.Kind = TopLevelValue -> withContextsAt typed symbol ty
                | _ -> ty, []
            let typeWritten =
                match contexts, Unifier.tysToDisplayStrings names (ty :: contexts) with
                | _ :: _, typeWritten :: contextsWritten -> withContexts contextsWritten typeWritten
                | _ -> Unifier.tyToDisplayString names ty
            Some { Type = symbol |> Option.map (fun symbol -> declared symbol typeWritten) |> Option.defaultValue typeWritten; Span = span }
    | _ -> None
