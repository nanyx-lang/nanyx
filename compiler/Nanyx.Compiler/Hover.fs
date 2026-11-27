/// What an editor shows when the pointer rests on an expression: its inferred type, after its name on a name.
/// Positions are character offsets into the source as the parser saw it, with line endings normalised to `\n`.
module NyxCompiler.Hover

open Parser.Program

/// What a hover shows, a type or a declaration written with its type (`def name: type`, `type Name = ...`), and the
/// span of the syntax it describes. `Note` is a sentence about it to show after it, such as what kind of nominal type
/// it is and where it is declared.
type HoverInfo = { Type: string; Span: SourceSpan; Note: string option }

/// Whether `span`, a real one, covers the character at `offset`.
let internal contains (offset: int) (span: SourceSpan) =
    not (SourceSpan.isNone span) && span.Start <= offset && offset < span.End

/// The expressions directly inside `expr`, including those in its statements, match arms and patterns.
let rec private children (expr: Expression) : Expression list =
    match expr.Kind with
    | LiteralExpr _
    | UnitExpr
    | IdentifierExpr _
    | ContextMemberAccess _
    | TypeValue _ -> []
    | FunctionCall(_, _, args)
    | ContextMemberCall(_, _, args) -> args
    | Lambda(_, body, _)
    | ContextNames(_, body) -> [ body ]
    | BinaryOp(_, _, left, right) -> [ left; right ]
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
    | ImportStatement _
    | AttributeStatement _ -> []

/// The expressions whose spans contain `offset`, innermost first. Only expressions that contain the offset are
/// searched, and those without a span of their own (made-up parents), so a lookup is proportional to depth.
let expressionsAt (offset: int) (module': Module) : Expression list =
    // Each expression found is pushed before its children are searched, so they end up in front of it.
    let rec search (found: Expression list) (expr: Expression) : Expression list =
        if contains offset expr.Span then children expr |> List.fold search (expr :: found)
        elif SourceSpan.isNone expr.Span then children expr |> List.fold search found
        else found
    module' |> List.collect SyntaxTree.ofItem |> List.fold search []

/// `expr` and every expression inside it, each before those inside it, and each arm of a match with the expressions in
/// its patterns before its body (see `SyntaxTree.expressionsWithinBy`).
let expressionsWithin (expr: Expression) : Expression list = SyntaxTree.expressionsWithinBy children expr

/// Every expression in `module'`, each before those inside it.
let allExpressions (module': Module) : Expression list =
    module' |> List.collect SyntaxTree.ofItem |> List.collect expressionsWithin

/// The type expressions whose spans contain `offset`, innermost first.
let typeExprsAt (offset: int) (module': Module) : TypeExpr list =
    let rec search (found: TypeExpr list) (typeExpr: TypeExpr) : TypeExpr list =
        if contains offset typeExpr.Span then TypeExpr.children typeExpr |> List.fold search (typeExpr :: found)
        else found
    let written =
        (module' |> List.collect SyntaxTree.topLevelTypes) @ (expressionsAt offset module' |> List.collect SyntaxTree.expressionTypes)
    written |> List.fold search []

/// The span of a call's or pipe's function name, or of an operator, when it has one.
let private calleeSpan (expr: Expression) =
    match expr.Kind with
    | FunctionCall(_, nameSpan, _)
    | BinaryOp(_, nameSpan, _, _)
    | Pipe(_, _, nameSpan, _) -> nameSpan
    | _ -> None

/// The type `symbol` is declared with, generic, and the contexts it requires: for one of the module's own, its type and
/// `TypedModule.ContextTypes`'; for one an import brings in, `ImportedContextTypes`' or else its `ImportedTypes`' type.
let internal declaredType (typed: TypedModule) (symbol: Symbol) : (Ty * Ty list) option =
    match typed.SymbolTypes |> Map.tryFind symbol.Id with
    | Some general -> Some (general, typed.ContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue [])
    | None ->
        typed.ImportedContextTypes
        |> Map.tryFind symbol.Id
        |> Option.orElse (typed.ImportedTypes |> Map.tryFind symbol.Id |> Option.map (fun ty -> ty, []))

/// The contexts the def `symbol` requires, a top-level or local one of the module's own or one an import brings in (see
/// `declaredType`), and the type `ty` it has where it is hovered, with the contexts instantiated as `ty` is: at a call,
/// `[Show(int)]` for the `int` it is called with.
let private withContextsAt (typed: TypedModule) (symbol: Symbol) (ty: Ty) : Ty * Ty list =
    match declaredType typed symbol with
    | Some (general, (_ :: _ as contexts)) ->
        match Solving.unify [ general, ty, Equal, SourceSpan.none ] with
        // A context variable stands for the contexts it is bound to here, if any.
        | Ok subst -> Unifier.apply subst ty, contexts |> List.map (Unifier.apply subst) |> FunctionContexts.flatten |> Unifier.writtenContexts
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
    | TypeDefinition
    | UnitOfMeasure -> typeWritten

/// What hover describes at `offset` in the compiled root module, as `typeAt` finds it: its type there, the symbol of
/// the name it is, if it is one, and its span.
let internal hoveredAt (syntax: Module) (typed: TypedModule) (offset: int) : (Ty * Symbol option * SourceSpan) option =
    let referenced (expr: Expression) = WrittenReferences.symbolOf typed expr.Id
    // Only the module's own declarations have types recorded, so no imported one is found here.
    let declarations =
        typed.SymbolTypes
        |> Seq.choose (fun entry ->
            let symbol = typed.Symbols.[entry.Key]
            if contains offset symbol.Declaration then Some (entry.Value, Some symbol, symbol.Declaration) else None)
        |> List.ofSeq
    let candidates =
        // A declaration's name is written as it is, so it comes before any expression of the same span.
        (declarations |> List.map (fun (ty, symbol, span) -> ty, symbol, span, System.Int32.MaxValue, 0))
        @ (expressionsAt offset syntax
        |> List.mapi (fun depth expr ->
            let callee =
                match calleeSpan expr with
                // What syntax is desugared to (`__pow` for `**`) is not a name the user wrote: the expression is hovered.
                | Some nameSpan when contains offset nameSpan && not (referenced expr |> Option.exists ReservedNames.isHidden) ->
                    typed.CalleeTypes
                    |> Map.tryFind expr.Id
                    |> Option.map (fun ty -> WrittenReferences.calleeType typed expr.Id ty, referenced expr, nameSpan, depth, 0)
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
        Some (ty, symbol, span)

/// `ty`, the type `symbol` has where it is hovered, as hover writes it after the name: after the contexts it requires
/// there (see `withContextsAt`), with the types and contexts it names named by `rename` (see `Signatures.relativeAt`).
let internal typeWrittenAt (names: TypeNotation) (rename: Ty -> Ty) (typed: TypedModule) (symbol: Symbol option) (ty: Ty) =
    let ty, contexts =
        match symbol with
        | Some symbol -> withContextsAt typed symbol ty
        | None -> ty, []
    Signatures.typeWithContexts typed.TypeDefs names (rename ty) (List.map rename contexts)

/// `ty`, the type `symbol` has where it is hovered, as hover writes it: after the contexts it requires there (see
/// `withContextsAt`) and the name (see `declared`).
let private writtenAt (names: TypeNotation) (typed: TypedModule) (symbol: Symbol option) (ty: Ty) =
    let typeWritten = typeWrittenAt names id typed symbol ty
    symbol |> Option.map (fun symbol -> declared symbol typeWritten) |> Option.defaultValue typeWritten

/// The hover for what `hoveredAt` found (see `typeAt`). A measured value's says what units make values of its
/// dimension (see `DimensionHover`).
let private hoverOf (names: TypeNotation) (typed: TypedModule) (ty: Ty, symbol: Symbol option, span: SourceSpan) : HoverInfo =
    let note =
        match Unifier.unalias ty with
        | TyMeasure(dimension, _) -> DimensionHover.unitsNote typed dimension
        | _ -> None
    { Type = writtenAt names typed symbol ty; Span = span; Note = note }

/// The hover for the expression at `offset` in the compiled root module: the type of the narrowest expression
/// there that has one, or of the function itself on a call's function name or an operator, where it is
/// instantiated at that call, or of what a declaration's own name declares (a def, a parameter, a pattern variable),
/// or of what a `_` in a type was inferred to be, with made-up type variables named by `names`. On a name, the
/// declaration's own or one that refers to it, the type comes after the name (see `declared`), and a def shows the
/// contexts it requires too: a top-level def, the module's own or an imported one, and a local def that writes them.
/// `None` when the module did not type-check or nothing typed is at `offset`.
///
/// The narrowest span wins rather than the deepest node, because a made-up node can sit inside the syntax it
/// stands for: a `for` loop's element is an identifier spanning all of the loop's arms. Of equal spans the
/// outermost wins, for the same reason: the parameters a shorthand match lambda makes up span the whole lambda.
let typeAt (names: TypeNotation) (result: CompileResult) (offset: int) : HoverInfo option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed -> hoveredAt syntax typed offset |> Option.map (hoverOf names typed)
    | _ -> None

/// The hover on a unit or a dimension at `offset` in the compiled root module (see `DimensionHover`): the unit of a number
/// with a unit, `ms` in `500ms`, a dimension's name in a type, `Time` in `Time(int)`, a `unit` declaration, or a
/// `dimension` declaration. `None` when none is there.
let unitAt (result: CompileResult) (offset: int) : HoverInfo option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let hover (span: SourceSpan) (written: string, note: string option) = { Type = written; Span = span; Note = note }
        let dimensionNamed (name: Identifier) = typed.TypeDefs |> Map.tryFind name |> Option.filter _.IsDimension
        let literal =
            expressionsAt offset syntax
            |> List.tryPick (fun expr ->
                match expr.Kind with
                | LiteralExpr(UnitLit(_, unitName)) when offset >= expr.Span.End - unitName.Length ->
                    typed.Units
                    |> Map.tryFind unitName
                    |> Option.map (fun info -> hover { expr.Span with Start = expr.Span.End - unitName.Length } (DimensionHover.ofUnit typed info))
                | _ -> None)
        let rec items (module': Module) = module' |> List.collect (fun item -> match item.Kind with ModuleBlock(_, inner) -> items inner | _ -> [ item ])
        let declaration () =
            items syntax
            |> List.filter (fun item -> contains offset item.Span)
            |> List.tryPick (fun item ->
                match item.Kind with
                | UnitDecl(_, name, _, _) -> typed.Units |> Map.tryFind name |> Option.map (DimensionHover.ofUnit typed >> hover item.Span)
                | DimensionDecl(_, name, _, _) -> dimensionNamed name |> Option.map (DimensionHover.ofDimension typed >> hover item.Span)
                | _ -> None)
        let inType () =
            typeExprsAt offset syntax
            |> List.tryPick (fun typeExpr ->
                match typeExpr.Kind with
                | TypeName name
                | TypeApply(name, _) when contains offset { typeExpr.Span with End = typeExpr.Span.Start + name.Length } ->
                    dimensionNamed name
                    |> Option.map (DimensionHover.ofDimension typed >> hover { typeExpr.Span with End = typeExpr.Span.Start + name.Length })
                | _ -> None)
        literal |> Option.orElseWith inType |> Option.orElseWith declaration
    | _ -> None

/// The hover on a built-in type's name, such as `int`, `I64` or `List`, written in a type in the compiled root module at
/// `offset`: what the type is (see `BuiltInTypeHover`). `None` when none is there, or when the module defines a type of
/// that name, which is that type.
let builtInTypeAt (result: CompileResult) (offset: int) : HoverInfo option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        typeExprsAt offset syntax
        |> List.tryPick (fun typeExpr ->
            let named (name: Identifier) (span: SourceSpan) =
                if contains offset span && not (typed.TypeDefs.ContainsKey name) then
                    BuiltInTypeHover.describe name |> Option.map (fun (written, note) -> { Type = written; Span = span; Note = Some note })
                else
                    None
            match typeExpr.Kind with
            | TypeName name -> named name typeExpr.Span
            | TypeApply(name, _) -> named name { typeExpr.Span with End = typeExpr.Span.Start + name.Length }
            | _ -> None)
    | _ -> None

/// A module a hover is on: the file it is read from, as its spans name it (see `ImportedFile`), the name it is shown
/// by, and the span of the syntax that names it.
type ModuleHover = { File: string; Name: string; Span: SourceSpan }

/// The names around `offset` that an import's alias can qualify, each with its node and its span, which starts where
/// the name does: an identifier, a call's or a pipe's function name, or a type's name.
let private writtenNames (syntax: Module) (offset: int) : (NodeId * Identifier * SourceSpan) list =
    let ofExpr (expr: Expression) =
        match expr.Kind with
        | IdentifierExpr name -> Some (expr.Id, name, expr.Span)
        | FunctionCall(name, nameSpan, _) -> Some (expr.Id, name, nameSpan |> Option.defaultValue expr.Span)
        | Pipe(_, name, Some nameSpan, _) -> Some (expr.Id, name, nameSpan)
        | _ -> None
    let ofTypeExpr (typeExpr: TypeExpr) =
        match typeExpr.Kind with
        | TypeName name
        | TypeApply(name, _) -> Some (typeExpr.Id, name, typeExpr.Span)
        | _ -> None
    (expressionsAt offset syntax |> List.choose ofExpr) @ (typeExprsAt offset syntax |> List.choose ofTypeExpr)

/// The module named at `offset` in the compiled root module, compiled as `file` (empty for source compiled directly):
/// by the file's own `module` line, by an import (its path or its alias), or by the alias that qualifies a name an
/// import brought in (`m` in `m.foo`). An import is shown by the name its module declares. `None` anywhere else, and
/// when the module did not type-check.
let moduleAt (file: string) (result: CompileResult) (offset: int) : ModuleHover option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        let imported (import: SymbolId) (span: SourceSpan) =
            typed.ImportedFiles
            |> Map.tryFind import
            |> Option.map (fun found -> { File = found.File; Name = found.Name |> Option.defaultValue typed.Symbols[import].Name; Span = span })
        let ownModule () =
            syntax
            |> List.tryPick (fun item ->
                match item.Kind with
                | ModuleDecl(_, name) when contains offset item.Span -> Some { File = file; Name = name; Span = item.Span }
                | _ -> None)
        let import () =
            typed.ImportedFiles
            |> Map.tryPick (fun import _ ->
                let declaration = typed.Symbols[import].Declaration
                if declaration.File = file && contains offset declaration then imported import declaration else None)
        let qualifier () =
            writtenNames syntax offset
            |> List.tryPick (fun (node, name, span) ->
                typed.ImportReferences
                |> Map.tryFind node
                |> Option.bind (fun import ->
                    let alias = typed.Symbols[import].Name
                    let aliasSpan = { span with End = span.Start + alias.Length }
                    if name.StartsWith(alias + ".", System.StringComparison.Ordinal) && contains offset aliasSpan then imported import aliasSpan
                    else None))
        ownModule () |> Option.orElseWith import |> Option.orElseWith qualifier
    | _ -> None
