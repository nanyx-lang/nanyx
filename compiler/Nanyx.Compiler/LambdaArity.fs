namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A lambda of several parameters checked against a function type of one parameter that is no record, as
/// `{ xs, f -> ... }` against `(seq(a), a -> ()) -> ()`, whose parentheses make `seq(a), a -> ()` its one parameter
/// (#2659). The lambda's parameters would be a tuple, which the parameter is not.
module internal LambdaArity =
    /// The error for the parameters `args` of `lambda`, checked against a function whose input is `expectedInput`, if
    /// there are several and the input is one value that is no record. When the input is a function of as many
    /// parameters as the lambda's, the parentheses most likely went round the parameters, not round the last one, a
    /// function, and the message says how to write that.
    let check (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (expectedInput: Ty) (expectedReturn: Ty option) : Diagnostic list =
        let resolved = Unifier.unalias (resolveSoFar state expectedInput)
        match args, resolved with
        | _ :: _ :: _, (TyFunc _ | TyPrimitive _ | TyApply _ | TyTag _ | TyUnion _) ->
            let written (ty: Ty) = Unifier.typeText state.TypeDefs (resolveSoFar state ty)
            let spans = state.Declared |> Map.tryFind (lambda.Id, LambdaParameters) |> Option.defaultValue [] |> List.map snd
            let span =
                match spans with
                | first :: _ -> SourceSpan.between first (List.last spans)
                | [] -> lambda.Span
            // A function is written in the parentheses that make it one parameter.
            let parameter =
                match resolved with
                | TyFunc _ -> $"({written expectedInput})"
                | _ -> written expectedInput
            let counts = $"This lambda takes {args.Length} parameters, but its type gives it one, of type '{parameter}'"
            let hint =
                match resolved, expectedReturn with
                | TyFunc(input, result, contexts), Some outerResult ->
                    match Unifier.unalias (resolveSoFar state input) with
                    | TyRecord(fields, _) when WellKnownContexts.isTuple fields && fields.Count = args.Length ->
                        let items = WellKnownContexts.tupleItems fields
                        let last = TyFunc(List.last items, result, contexts)
                        let meant = TyFunc(TypeResolution.tupleRecordType (List.take (items.Length - 1) items @ [ last ]), outerResult, [])
                        $": for {args.Length} parameters, the last of them a function, put the parentheses round that function, '{written meant}'"
                    | _ -> ""
                | _ -> ""
            [ Diagnostics.at DiagnosticCodes.TypeMismatch span (counts + hint) ]
        | _ -> []
