namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A call that gives its callee another number of arguments than it takes (#2075). A function's parameters are one
/// tuple, so such a call would otherwise be a mismatch of a type and a tuple, which says nothing of the count, and for a
/// callee whose type is inferred names the checker's own type variables.
module internal ArgumentCount =
    /// Whether `ty` is one value that is no tuple, of a type no record is: a number, a string, a function, a list, a tag.
    let private isOneValue (ty: Ty) =
        match Unifier.unalias ty with
        | TyPrimitive "unit" -> false
        | TyPrimitive _
        | TyFunc _
        | TyApply _
        | TyTag _
        | TyUnion _
        | TyMeasure _ -> true
        | _ -> false

    /// How many parameters a function whose input is `input` takes, where that is plain: as many as a tuple input has
    /// items, or one, for an input that is one value (see `isOneValue`). None for any other input, such as a type
    /// variable, a record with named fields or `()`, and for a function that takes a type argument, which a call that
    /// leaves out is reported for that (see `TypeArguments`).
    let parametersOf (input: Ty) =
        let isTypeParameter (ty: Ty) = match Unifier.unalias ty with TypeValues.TypeValueType _ -> true | _ -> false
        match Unifier.unalias input with
        | TyRecord(fields, _) when fields.Count >= 2 && WellKnownContexts.isTuple fields ->
            if fields |> Map.exists (fun _ ty -> isTypeParameter ty) then None else Some fields.Count
        | ty when isTypeParameter ty -> None
        | ty when isOneValue ty -> Some 1
        | _ -> None

    let private arguments (count: int) = if count = 1 then "1 argument" else $"{count} arguments"

    /// The arguments `args` of a call as it was written: `f(a, b)` passes its arguments as one tuple, which starts
    /// where its first item does, but `f((a, b))` passes a tuple, whose span takes in its brackets.
    let private written (args: Expression list) =
        match args with
        | [ Kind (TupleExpr (first :: _ as items)) as tuple ] when first.Span.Start = tuple.Span.Start -> items
        | _ -> args

    /// Whether `arg` is written as one value that is no tuple: a literal or a lambda, whose type may not be settled yet.
    let private writtenAsOneValue (arg: Expression) =
        match arg.Kind with
        | LiteralExpr _
        | Lambda _ -> true
        | _ -> false

    /// The error for `call`, a call of `name`, a function of the type `calleeTy` written at `calleeSpan`, with `args`,
    /// when it gives another number of arguments than the function takes. A call that gives one argument to a function
    /// of several is one only when that argument is one value, by its type as `typeOf` gives it, as it may be a tuple
    /// of them. A pipe's value is its call's first argument, written before the function's name, and is counted. The
    /// error is at the call's arguments, or at the whole of a pipe.
    let check (state: InferState) (call: Expression) (name: Identifier) (calleeTy: Ty) (calleeSpan: SourceSpan) (args: Expression list) (typeOf: Expression -> Ty option) : Diagnostic option =
        let args = written args
        let report (taken: int) =
            let piped = not (SourceSpan.isNone calleeSpan) && (List.head args).Span.Start < calleeSpan.Start
            let span = if piped then call.Span else SourceSpan.between (List.head args).Span (List.last args).Span
            let given = if piped then $"this pipe gives it {args.Length}, counting the piped value" else $"this call gives {args.Length}"
            Some (Diagnostics.at DiagnosticCodes.ArgumentCount span $"'{name}' takes {arguments taken}, but {given}" |> Diagnostics.about name)
        match Unifier.unalias (resolveSoFar state calleeTy) with
        // A function the compiler made, as a custom pattern's, is called as the syntax it stands for says.
        | _ when MadeUpNames.isMadeUp name -> None
        | TyFunc(input, _, _) ->
            match parametersOf (resolveSoFar state input), args with
            | Some taken, [ single ] when taken >= 2 ->
                if writtenAsOneValue single || typeOf single |> Option.exists (resolveSoFar state >> isOneValue) then report taken
                else None
            | Some taken, _ :: _ :: _ when taken <> args.Length -> report taken
            | _ -> None
        | _ -> None
