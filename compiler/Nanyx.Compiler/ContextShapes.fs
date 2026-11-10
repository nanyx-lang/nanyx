namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Contexts are structural: an instance of one context is also an instance of any other whose members it has, each at
/// the same type. So a function that requires `[Num(a)]` can call one that requires `[Add(a)]`, and pass it its `Num`
/// (see "Numeric contexts" in the docs' work in progress).
module internal ContextShapes =
    /// The contexts other than `ctxName` that have every member `ctxName` has, each at the same type, when their
    /// parameters are the same types: those whose instances are also instances of `ctxName`. `membersOf` gives a
    /// context's members at those types, each with its type, if it is a context with that many parameters.
    let wider (state: InferState) (membersOf: Identifier -> (Identifier * Ty) list option) (ctxName: Identifier) : Identifier list =
        match membersOf ctxName with
        | None -> []
        | Some members ->
            [ for KeyValue(other, info) in state.TypeDefs do
                if other <> ctxName && info.IsContext then
                    match membersOf other with
                    | Some otherMembers when members |> List.forall (fun m -> List.contains m otherMembers) -> yield other
                    | _ -> () ]
