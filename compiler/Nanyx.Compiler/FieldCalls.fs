namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `statement.setInt(position, value)`, where `statement` is a value and its field `setInt` holds a function: a call of
/// the function in the field (#1513). A dotted name is read as a definition's name first (see `Infer`), so this is what
/// is left when the receiver is a record value with a field of that name, as a map's field is looked up (see `Maps`).
/// The call is read as a block that keeps the field's value in a local and calls that, which the backend calls as it
/// calls any function value in a local.
module internal FieldCalls =
    /// The local the field's value is kept in.
    let private local = "__field"

    /// Whether a value of the type `receiverTy` has a field `field` that holds a function, as far as `state` knows yet.
    let holdsFunction (state: InferState) (receiverTy: Ty) (field: Identifier) =
        match recordFieldsOf (resolveSoFar state receiverTy) |> Option.bind (Map.tryFind field) with
        | Some fieldTy ->
            match Unifier.unalias (resolveSoFar state fieldTy) with
            | TyFunc _ -> true
            | _ -> false
        | None -> false

    /// The block that `expr`, a call of the field `field` of `receiver` with `args`, its name at `spanOpt`, is read as.
    let private readAs (expr: Expression) (receiver: Identifier) (field: Identifier) (spanOpt: SourceSpan option) (args: Expression list) : Expression =
        let value = Expression.from expr (MemberAccess(Expression.from expr (IdentifierExpr receiver), field, spanOpt))
        let call = Expression.from expr (FunctionCall(local, spanOpt, args))
        { expr with Kind = Block [ Statement.from expr (DefStatement(false, local, None, value)); Statement.from expr (ExprStatement call) ] }

    /// `expr`, a call of the field `field` of `receiver` with `args`, its name at `spanOpt`, typed by `infer` as the block
    /// it is read as, which `state` records in `ReadAs`.
    let infer (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>) (state: InferState) (expr: Expression) (receiver: Identifier) (field: Identifier) (spanOpt: SourceSpan option) (args: Expression list) =
        let read = readAs expr receiver field spanOpt args
        infer state read
        |> Result.map (fun (typed, next) -> { typed with Expr = expr }, { next with ReadAs = next.ReadAs |> Map.add expr.Id read })

    /// The block a field's call is read as, with `args` for the arguments: those the call is written with, as they are
    /// rewritten (see `ExpressionRewrites.writeAsRead`).
    let (|WithArguments|_|) (args: Expression list) (read: Expression) : ExpressionKind option =
        match read.Kind with
        | Block [ field; { Kind = ExprStatement ({ Kind = FunctionCall(name, span, readArgs) } as call) } as last ] when name = local && readArgs.Length = args.Length ->
            Some (Block [ field; { last with Kind = ExprStatement { call with Kind = FunctionCall(name, span, args) } } ])
        | _ -> None
