namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A lambda with `yield` in it is a lazy sequence: the `#yield` rewrite makes its value calls to the prelude's `seq`
/// functions before inference (see `WorkflowDesugaring`). The builders that take every value at once, such as
/// `list.select`, take a function that calls the prelude's `Emit`'s `emit` and gives back `()` instead (#2566). A lambda
/// of the first kind given where one of the second is expected would be a type mismatch between a sequence's record and
/// `()`, which says nothing of why; it is reported at its first `yield` instead, saying what to write (#2571).
module internal EmitLambdas =
    /// The prelude's context a builder's function pushes its values through.
    let emitContext = "Emit"

    /// The calls the rewrite makes a `yield` into: `seq.cons(x, { rest })`, the last one's `__seqLast(x)`, or a loop's
    /// that yields once each time round, `__seqMap(items, { item -> x })`, made at its first `yield`.
    let private yieldCalls = set [ Seqs.consFunction; Seqs.lastFunction; Seqs.mapFunction ]

    /// The calls the rewrite chains a sequence's parts with, whose lambdas are the rest of the same block.
    let private chainCalls = set [ Seqs.consFunction; Seqs.appendFunction; Seqs.flatMapFunction; Seqs.delayFunction ]

    /// The span of the first `yield` that made `body`, a lambda's, a sequence. A lambda written in the block is a
    /// function of its own, whose `yield`s are its own, so only those the rewrite made for the rest of the block are
    /// looked in.
    let private firstYield (body: Expression) : SourceSpan option =
        let children (expr: Expression) =
            let direct, statements = SyntaxTree.childrenOf expr
            let all = direct @ (statements |> List.collect SyntaxTree.ofStatement)
            match expr.Kind with
            | FunctionCall(name, _, _) when chainCalls.Contains name -> all
            | _ -> all |> List.filter (fun child -> match child.Kind with Lambda _ -> false | _ -> true)
        SyntaxTree.expressionsWithinBy children body
        |> List.filter (fun expr -> match expr.Kind with FunctionCall(name, _, _) -> yieldCalls.Contains name | _ -> false)
        |> List.map _.Span
        |> List.sortBy _.Start
        |> List.tryHead

    /// Whether `paramTy`, as far as `state` says, is a builder's function: one that gives back `()` and requires `Emit`.
    let private emits (state: InferState) (paramTy: Ty) =
        match Unifier.unalias (resolveSoFar state paramTy) with
        | TyFunc(_, result, contexts) ->
            resolveSoFar state result = TyPrimitive "unit"
            && contexts |> List.exists (fun ctx -> match resolveSoFar state ctx with TyApply(name, _) -> name = emitContext | _ -> false)
        | _ -> false

    /// The error for `lambdaBody`, the body of a lambda passed as a parameter of type `paramTy`, when the lambda yields
    /// and the parameter is a builder's function that emits: NYX0201 at its first `yield`.
    let yieldGivenToEmitter (state: InferState) (lambdaBody: Expression) (paramTy: Ty) : Diagnostic option =
        if emits state paramTy then
            firstYield lambdaBody
            |> Option.map (fun span ->
                Diagnostics.at DiagnosticCodes.TypeMismatch span
                    "This `yield` makes the lambda a lazy sequence, but it is given where a function that calls `emit` is expected, as a builder such as `list.select` or `list.build` takes: write `emit(x)` for `yield x`")
        else None
