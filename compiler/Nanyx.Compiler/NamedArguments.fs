namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A call that passes by position an argument its callee takes by name, as `string.padStart(s, 2, "0")` does for
/// `(string, width: int, fill: string) -> string`. Unified as records, the two only differ in their fields, so
/// the call is checked here first, to say which parameters to name.
module internal NamedArguments =
    /// The arguments a call writes by position, and the names it passes by name. `f(a, b)` is one tuple argument
    /// and `f(a, n = b)` one record; a pipe's arguments are spliced into a list, or into that record. A lone
    /// argument is the callee's whole input, as `p` is in `shift(p)` for a record `p`, so it says nothing here.
    let private written (args: Expression list) : Expression list * string list =
        match args with
        | [ Kind (RecordExpr fields) ] ->
            fields |> List.choose (function PositionalField value -> Some value | NamedField _ -> None),
            fields |> List.choose (function NamedField(name, _) -> Some name | PositionalField _ -> None)
        | [ Kind (TupleExpr items) ] -> items, []
        | [ _ ] -> [], []
        | _ -> args, []

    /// The types of the arguments `args` writes by position, as far as `state` knows them.
    let private positionalTypes (state: InferState) (args: Expression list) (typedArgs: TypedExpr list) : Ty list =
        match args, typedArgs with
        | [ Kind (RecordExpr _ | TupleExpr _) as arg ], [ typedArg ] ->
            let fields = recordFieldsOf (resolveSoFar state typedArg.Type) |> Option.defaultValue Map.empty
            fst (written [ arg ]) |> List.mapi (fun index _ -> fields |> Map.tryFind (string index) |> Option.defaultValue Unifier.errorType)
        | _ -> typedArgs |> List.map (fun typedArg -> resolveSoFar state typedArg.Type)

    let private isPositional (key: string) = key |> Seq.forall System.Char.IsDigit

    /// Pairs each extra positional argument, in order, with the one named parameter left whose type is its type.
    /// None when an argument's type fits no parameter, or more than one.
    let rec private pair (extras: (Expression * Ty) list) (named: (string * Ty) list) : (Expression * string) list option =
        match extras with
        | [] -> Some []
        | (arg, argTy) :: rest ->
            match named |> List.filter (fun (_, paramTy) -> paramTy = argTy) with
            | [ (name, _) ] -> pair rest (named |> List.filter (fun (other, _) -> other <> name)) |> Option.map (fun paired -> (arg, name) :: paired)
            | _ -> None

    /// A diagnostic, at the call `expr`, when it passes by position arguments that `callee`, whose input is `input`,
    /// takes by name: more positional arguments than the callee has positional parameters, and no more than the
    /// named parameters it leaves out. When each of those arguments has the type of just one of them, the names are
    /// given in the call's order, with a fix that writes them in.
    let check (state: InferState) (expr: Expression) (callee: string) (input: Ty) (args: Expression list) (typedArgs: TypedExpr list) : Diagnostic option =
        match recordFieldsOf (resolveSoFar state input) with
        | Some fields ->
            let positionalCount = fields |> Map.filter (fun key _ -> isPositional key) |> Map.count
            let positional, passedByName = written args
            let unnamed =
                fields
                |> Map.toList
                |> List.filter (fun (key, _) -> not (isPositional key) && not (List.contains key passedByName))
                |> List.map (fun (key, ty) -> key, resolveSoFar state ty)
            let extraCount = positional.Length - positionalCount
            if extraCount > 0 && extraCount <= unnamed.Length then
                let extras = List.zip positional (positionalTypes state args typedArgs) |> List.skip positionalCount
                let paired = pair extras unnamed
                let names =
                    match paired with
                    | Some paired ->
                        let pairedNames = paired |> List.map snd
                        pairedNames @ (unnamed |> List.map fst |> List.filter (fun name -> not (List.contains name pairedNames)))
                    | None -> unnamed |> List.map fst
                let quoted = names |> List.map (fun name -> $"'{name}'")
                let asNamed = names |> List.map (fun name -> $"'{name} = ...'")
                let message = $"'{callee}' takes {Diagnostics.listed quoted} by name, not by position: write {Diagnostics.listed asNamed}"
                let diagnostic = Diagnostics.at DiagnosticCodes.PositionalForNamedArgument expr.Span message
                match paired with
                | Some paired when paired |> List.forall (fun (arg, _) -> not (SourceSpan.isNone arg.Span)) ->
                    let edits = paired |> List.map (fun (arg, name) -> { Span = { arg.Span with End = arg.Span.Start }; NewText = $"{name} = " })
                    Some { diagnostic with Fixes = [ { Title = "Name the arguments"; Edits = edits } ] }
                | _ -> Some diagnostic
            else None
        | None -> None
