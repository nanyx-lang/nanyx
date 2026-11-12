/// What an editor shows when the pointer rests on an expression: its inferred type, after its name on a name.
/// Positions are character offsets into the source as the parser saw it, with line endings normalised to `\n`.
module NyxCompiler.Hover

open Parser.Program

/// What a hover shows, a type or a declaration written with its type (`def name: type`, `type Name = ...`), and the
/// span of the syntax it describes.
type HoverInfo = { Type: string; Span: SourceSpan }

/// Whether `span`, a real one, covers the character at `offset`.
let internal contains (offset: int) (span: SourceSpan) =
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
    | Lambda(_, body, _) -> [ body ]
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
        match Unifier.unify [ general, ty, Equal, SourceSpan.none ] with
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
    | TypeDefinition -> typeWritten

/// What hover describes at `offset` in the compiled root module, as `typeAt` finds it: its type there, the symbol of
/// the name it is, if it is one, and its span.
let private hoveredAt (syntax: Module) (typed: TypedModule) (offset: int) : (Ty * Symbol option * SourceSpan) option =
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
                // What syntax is desugared to (`__pow` for `**`) is not a name the user wrote: the expression is hovered.
                | Some nameSpan when contains offset nameSpan && not (referenced expr |> Option.exists ReservedNames.isHidden) ->
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
        Some (ty, symbol, span)

/// `ty`, the type `symbol` has where it is hovered, as hover writes it: after the contexts it requires there (see
/// `withContextsAt`) and the name (see `declared`), and with each of `others` written with it, its variables named as
/// they are there.
let private writtenAt (names: TypeNotation) (typed: TypedModule) (symbol: Symbol option) (ty: Ty) (others: Ty list) =
    let ty, contexts =
        match symbol with
        | Some symbol -> withContextsAt typed symbol ty
        | None -> ty, []
    let typeWritten, othersWritten = Signatures.typesWithContexts names ty contexts others
    (symbol |> Option.map (fun symbol -> declared symbol typeWritten) |> Option.defaultValue typeWritten), othersWritten

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
    | Some syntax, Some typed ->
        hoveredAt syntax typed offset
        |> Option.map (fun (ty, symbol, span) -> { Type = fst (writtenAt names typed symbol ty []); Span = span })
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
                | ModuleDecl name when contains offset item.Span -> Some { File = file; Name = name; Span = item.Span }
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

/// A def's own signature, beside the type a name that refers to it has where it is used. `Declared` is the signature
/// as hover writes the def's declaration: its annotation, or else the generic type it was inferred to have. `Bindings`
/// are the type variables of the signature that are something else at the use, in the order the signature writes
/// them, each with what it is there, named as the type at the use names its variables: `a`, `string`.
type Instantiation = { Declared: string; Bindings: (string * string) list; Definition: SourceSpan }

/// The signature of the def that the name at `offset` in the compiled root module refers to, or declares, when it is not
/// what `typeAt` shows there, as an `Instantiation`: on a use of a generic def, where its type variables are filled in.
/// The def is a top-level or local one, the module's own or one an import brings in. `None` when no such name is at
/// `offset`, or its type there is its signature: a def that is not generic, or the def's own name.
let instantiationAt (names: TypeNotation) (result: CompileResult) (offset: int) : Instantiation option =
    match result.Syntax, result.Typed with
    | Some syntax, Some typed ->
        match hoveredAt syntax typed offset with
        | Some (ty, Some symbol, _) when symbol.Kind = TopLevelValue || symbol.Kind = LocalValue ->
            declaredType typed symbol
            |> Option.bind (fun (general, contexts) ->
                let contexts = contexts |> FunctionContexts.flatten |> Unifier.writtenContexts
                let declaredWritten = declared symbol (Signatures.typeWithContexts names general contexts)
                let contextVariables = FunctionContexts.variablesIn general
                // Each variable with what it is here: a type, or the contexts a context variable stands for, if any.
                let bound =
                    match Unifier.unify [ general, ty, Equal, SourceSpan.none ] with
                    | Ok subst ->
                        Unifier.displayedVars names (general :: contexts)
                        |> List.choose (fun (var, name) ->
                            match Unifier.apply subst (TyVar var) with
                            | TyVar same when same.Id = var.Id -> None
                            | boundTo when contextVariables.ContainsKey var.Id ->
                                match FunctionContexts.flatten [ boundTo ] |> Unifier.writtenContexts with
                                | [] -> None
                                | boundContexts -> Some (name, boundContexts)
                            | boundTo -> Some (name, [ boundTo ]))
                    | Error _ -> []
                let typeWritten, boundWritten = writtenAt names typed (Some symbol) ty (List.collect snd bound)
                let bindings =
                    bound
                    |> List.mapFold (fun written (name, tys) ->
                        let here, rest = List.splitAt tys.Length written
                        (name, String.concat " & " here), rest) boundWritten
                    |> fst
                if typeWritten = declaredWritten then None
                else Some { Declared = declaredWritten; Bindings = bindings; Definition = symbol.Declaration })
        | _ -> None
    | _ -> None
