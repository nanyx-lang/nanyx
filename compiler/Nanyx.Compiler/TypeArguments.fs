/// Type arguments: a function's parameter that takes a type, written as one of type `Type(a)`, positional
/// `(Type(a), string) -> a` or named `(string, into: Type(a)) -> a`, and the value `type Point` passed for it, of type
/// `Type(Point)` (see `TypeValues`). A named parameter `into: Type(into)`, whose type names the field's own name, makes
/// that name a type variable of the whole signature, so `[Decode(into)]` and `-> into` name it as they would `a`.
/// Inference then needs nothing new: unifying `Type(Point)` with `Type(a)` fixes `a`. A `Type` value is known at compile
/// time, so it is never chosen with `if` or `match`, nor held in a list with another type's.
///
/// The old forms, `type a` as a positional parameter and `into: type` as a named one, still compile for one release,
/// each an error (NYX0282) whose fix writes it as `Type(a)` or `into: Type(into)`.
module internal NyxCompiler.TypeArguments

open Parser.Program
open NyxCompiler.InferState

let private isTypeKeyword (t: TypeExpr) =
    match t.Kind with
    | TypeKeyword -> true
    | _ -> false

/// Whether `t`, the type of the field `name`, is `Type(name)`: the field is a named type parameter whose name is its
/// type variable.
let private isTypeOfOwnName (name: Identifier) (t: TypeExpr) =
    match t.Kind with
    | TypeApply(typeName, [ { Kind = TypeName inner | TypeVar inner } ]) -> TypeValues.isTypeName typeName && inner = name
    | _ -> false

/// The fields of a function's parameters, `input`, when they are a record.
let private parameterFields (input: TypeExpr) =
    match input.Kind with
    | TypeRecord fields
    | TypeOpenRecord(fields, _) -> fields
    | _ -> []

/// The named type parameters whose name is their type variable, `into: Type(into)` or the old `into: type`, of the
/// functions `annotation` writes, each with the field's type and default.
let private namedParameters (annotation: TypeExpr) : (Identifier * bool * TypeExpr * Expression option) list =
    TypeExpr.within annotation
    |> List.collect (fun t ->
        match t.Kind with
        | TypeFunc(input, _) ->
            parameterFields input
            |> List.choose (function
                | TypeField(name, isMutable, Some written, defaultOpt) when isTypeKeyword written || isTypeOfOwnName name written ->
                    Some (name, isMutable, written, defaultOpt)
                | _ -> None)
        | _ -> [])

/// The error for a type parameter written the old way, `written`, which is `now`, with the fix that writes `newText` at
/// `span`. The parameter is still read as `now`, so that nothing else is reported for it.
let private oldForm (span: SourceSpan) (written: string) (now: string) (newText: string) : Diagnostic =
    { (Diagnostics.at DiagnosticCodes.OldFormTypeParameter span
           $"A parameter that takes a type is written with its type, `Type(a)`: write `{now}`, not `{written}`"
       |> Diagnostics.about written) with
        Fixes = [ { Title = $"Write `{now}`"; Edits = [ { Span = span; NewText = newText } ] } ] }

/// The errors for the type parameters written the old way in `module'`: each `type a` as a positional parameter, and
/// each `into: type` as a function's named one.
let private oldForms (module': Module) : Diagnostic list =
    SyntaxTree.writtenTypes module'
    |> List.collect (fun written -> TypeExpr.within written.Type)
    |> List.collect (fun t ->
        match t.Kind with
        | TypeArgument({ Kind = TypeVar variable }) -> [ oldForm t.Span $"type {variable}" $"Type({variable})" $"Type({variable})" ]
        | TypeFunc(input, _) ->
            parameterFields input
            |> List.choose (function
                // A built-in type's name is an error already (see `parameterErrors`), which `Type(list)` wouldn't fix.
                | TypeField(name, _, Some keyword, _) when isTypeKeyword keyword && not (TypeResolution.isBuiltInTypeName name) ->
                    Some (oldForm keyword.Span $"{name}: type" $"{name}: Type({name})" $"Type({name})")
                | _ -> None)
        | _ -> [])

/// The errors for the named type parameters `annotation` declares that are declared wrongly.
let private parameterErrors (annotation: TypeExpr) : Diagnostic list =
    namedParameters annotation
    |> List.collect (fun (name, isMutable, written, defaultOpt) ->
        [ if TypeResolution.isBuiltInTypeName name then
              Diagnostics.at DiagnosticCodes.InvalidTypeParameter written.Span
                  $"'{name}' is a built-in type, so it can't be the name of a type parameter" |> Diagnostics.about name
          if isMutable then
              Diagnostics.at DiagnosticCodes.InvalidTypeParameter written.Span "A type parameter can't be `mut`" |> Diagnostics.about name
          match defaultOpt with
          | Some value ->
              Diagnostics.at DiagnosticCodes.InvalidTypeParameter value.Span
                  $"A type parameter can't have a default: a type argument is always passed, as `{name} = type Point`" |> Diagnostics.about name
          | None -> () ])

/// `t` with each name in `names`, the named type parameters in scope, written as the type variable it is, and each
/// named type parameter of a function it writes as a parameter of type `Type(name)`.
let rec private withParameters (names: Set<Identifier>) (t: TypeExpr) : TypeExpr =
    match t.Kind with
    | TypeName name when names.Contains name -> { t with Kind = TypeVar name }
    | TypeFunc(input, result) ->
        let field (field: TypeRecordField) =
            match field with
            | TypeField(name, _, Some keyword, _) when isTypeKeyword keyword ->
                TypeField(name, false, Some (TypeExpr.at keyword.Span (TypeArgument(TypeExpr.at keyword.Span (TypeVar name)))), None)
            | TypeField(name, isMutable, typeOpt, defaultOpt) -> TypeField(name, isMutable, Option.map (withParameters names) typeOpt, defaultOpt)
            | TypeFieldAttribute _ -> field
        let input =
            match input.Kind with
            | TypeRecord fields -> { input with Kind = TypeRecord(List.map field fields) }
            | TypeOpenRecord(fields, row) -> { input with Kind = TypeOpenRecord(List.map field fields, row) }
            | _ -> withParameters names input
        { t with Kind = TypeFunc(input, withParameters names result) }
    | _ -> TypeExpr.mapChildren (withParameters names) t

/// What a block, or any other expression, gives: a block's last expression, through the blocks it ends in.
let rec private result (expr: Expression) : Expression =
    match expr.Kind with
    | Block statements ->
        match SyntaxTree.blockValue statements with
        | Some value -> result value
        | None -> expr
    | _ -> expr

/// The type `expr` passes, when it is written `type Point`, or a block that ends in one.
let private passedType (expr: Expression) : TypeExpr option =
    match (result expr).Kind with
    | TypeValue written -> Some written
    | _ -> None

/// `expr` passing `written` instead of the type it passes, if it passes one (see `passedType`).
let rec private passing (written: TypeExpr) (expr: Expression) : Expression =
    match expr.Kind with
    | TypeValue _ -> { expr with Kind = TypeValue written }
    | Block statements ->
        match List.rev statements with
        | ({ Kind = ExprStatement value } as last) :: earlier ->
            { expr with Kind = Block(List.rev ({ last with Kind = ExprStatement(passing written value) } :: earlier)) }
        | _ -> expr
    | _ -> expr

/// `expr` with no `Type` value chosen at run time, and the errors for those that were: an `if`'s or a `match`'s branch
/// written `type X`, and a list that holds two different ones. Each `if`, `match` or list is reported once, and each of
/// its `Type` values is then read as its first, so that inference finds no conflict among them to report again.
let rec private unchosen (errors: ResizeArray<Diagnostic>) (expr: Expression) : Expression =
    let expr = SyntaxTree.mapChildren (unchosen errors) expr
    let report (message: string) (branches: Expression list) =
        match branches |> List.choose (fun branch -> passedType branch |> Option.map (fun written -> branch, written)) with
        | [] -> None
        | (first, written) :: others ->
            let at = match others |> List.tryFind (fun (_, other) -> other <> written) with Some (other, _) -> other | None -> first
            errors.Add(Diagnostics.at DiagnosticCodes.TypeChosenAtRunTime (result at).Span message)
            Some (passing written)
    let chosenWith (construct: string) =
        $"A type argument can't be chosen with {construct}: a `Type` value is known at compile time, so pass `type X` where it is used"
    match expr.Kind with
    | IfExpr(condition, ifTrue, ifFalse) ->
        match report (chosenWith "`if`") [ ifTrue; ifFalse ] with
        | Some first -> { expr with Kind = IfExpr(condition, first ifTrue, first ifFalse) }
        | None -> expr
    | Match(scrutinees, arms) ->
        match report (chosenWith "`match`") (List.map snd arms) with
        | Some first -> { expr with Kind = Match(scrutinees, arms |> List.map (fun (patterns, body) -> patterns, first body)) }
        | None -> expr
    | ListExpr items when (items |> List.choose passedType |> List.distinct).Length > 1 ->
        match report "A list can't hold `Type` values of different types: a `Type` value is known at compile time, so pass each where it is used" items with
        | Some first -> { expr with Kind = ListExpr(List.map first items) }
        | None -> expr
    | _ -> expr

/// `value`, the value of a def whose signature declares the named type parameters `names`, with them written as the
/// type variables they are in the types written in it, `type into` among them, and the errors for the type parameters
/// its local defs declare wrongly.
let rec private inValue (names: Set<Identifier>) (errors: ResizeArray<Diagnostic>) (value: Expression) : Expression =
    let rewritten = SyntaxTree.mapChildren (inValue names errors) value
    let typeIn = withParameters names
    match rewritten.Kind with
    | TypeValue written -> { rewritten with Kind = TypeValue(typeIn written) }
    | Lambda(parameters, body, takesUnit) -> { rewritten with Kind = Lambda(parameters |> List.map (fun (name, typeOpt) -> name, Option.map typeIn typeOpt), body, takesUnit) }
    | Block statements ->
        let statement (statement: Statement) =
            match statement.Kind with
            | DefStatement(isExport, name, Some annotation, local) ->
                errors.AddRange(parameterErrors annotation)
                let names = names + (namedParameters annotation |> List.map (fun (name, _, _, _) -> name) |> Set.ofList)
                { statement with Kind = DefStatement(isExport, name, Some (withParameters names annotation), inValue names errors local) }
            | _ -> statement
        { rewritten with Kind = Block(List.map statement statements) }
    | _ -> rewritten

/// `module'` with each named type parameter written as a parameter of type `Type(name)`, and `name` as a type variable
/// wherever its def writes it, and the diagnostics: an error for each type parameter written the old way, and the
/// errors for type parameters declared wrongly and `Type` values chosen at run time.
let rewrite (module': Module) : Module * Diagnostic list =
    let errors = ResizeArray<Diagnostic>(oldForms module')
    let def (annotationOpt: TypeExpr option) (value: Expression) =
        let value = unchosen errors value
        match annotationOpt with
        | Some annotation ->
            errors.AddRange(parameterErrors annotation)
            let names = namedParameters annotation |> List.map (fun (name, _, _, _) -> name) |> Set.ofList
            Some (withParameters names annotation), inValue names errors value
        | None -> None, inValue Set.empty errors value
    let rec item (topLevel: TopLevelItem) : TopLevelItem =
        match topLevel.Kind with
        | Def (ValueDef(isExport, name, annotationOpt, value)) ->
            let annotationOpt, value = def annotationOpt value
            { topLevel with Kind = Def (ValueDef(isExport, name, annotationOpt, value)) }
        | Expr value -> { topLevel with Kind = Expr (snd (def None value)) }
        | ModuleBlock(name, items) -> { topLevel with Kind = ModuleBlock(name, List.map item items) }
        | Def (TypeDef _)
        | ModuleDecl _
        | Import _
        | ReExport _
        | Attribute _
        | DimensionDecl _
        | UnitDecl _ -> topLevel
    let rewritten = List.map item module'
    rewritten, List.ofSeq errors

/// The type argument the call of a callee that takes `input` leaves out, with `args`: the named parameter, or None for a
/// positional one, and the type it would stand for. `deserialise(text)` leaves out the `type a` of
/// `(type a, string) -> a`; a call that passes its callee's whole input as one record value leaves out nothing.
let private leftOut (state: InferState) (input: Ty) (args: Expression list) (typedArgs: TypedExpr list) : (Identifier option * Ty) option =
    let standsFor (ty: Ty) =
        match resolveSoFar state ty with
        | TypeValues.TypeValueType standsFor -> Some standsFor
        | _ -> None
    let isPositional (key: string) = key |> Seq.forall System.Char.IsDigit
    // The types of the arguments the call passes by position, and the names it passes by name.
    let passed =
        match args, typedArgs with
        | [], _ -> Some ([], [])
        | [ Kind (RecordExpr _ | TupleExpr _) ], [ typedArg ] ->
            let positional, byName = NamedArguments.written args
            let fields = recordFieldsOf (resolveSoFar state typedArg.Type) |> Option.defaultValue Map.empty
            Some (positional |> List.mapi (fun index _ -> fields |> Map.tryFind (string index)), byName)
        | [ _ ], [ typedArg ] when (recordFieldsOf (resolveSoFar state typedArg.Type)).IsSome -> None
        | _ -> Some (typedArgs |> List.map (fun typedArg -> Some typedArg.Type), [])
    match recordFieldsOf (resolveSoFar state input), passed with
    | Some fields, Some (passedTypes, byName) ->
        let positional = fields |> Map.toList |> List.filter (fst >> isPositional) |> List.sortBy (fst >> int)
        let typeParameters = positional |> List.choose (snd >> standsFor)
        let typeArguments = passedTypes |> List.filter (Option.bind standsFor >> Option.isSome) |> List.length
        let positionalLeftOut =
            if passedTypes.Length < positional.Length then typeParameters |> List.tryItem typeArguments |> Option.map (fun ty -> None, ty)
            else None
        let namedLeftOut () =
            fields
            |> Map.toList
            |> List.tryPick (fun (key, ty) ->
                if isPositional key || List.contains key byName then None else standsFor ty |> Option.map (fun ty -> Some key, ty))
        positionalLeftOut |> Option.orElseWith namedLeftOut
    | None, Some ([], _) -> standsFor input |> Option.map (fun ty -> None, ty)
    | _ -> None

/// A callee's type, `funcTy`, when the call `expr` of it, named `callee`, with `args`, leaves out a type argument: what
/// the call gives back, and the call left out.
let (|LeavesOut|_|) (state: InferState) (expr: Expression) (callee: string) (args: Expression list) (typedArgs: TypedExpr list) (funcTy: Ty) =
    match Unifier.unalias (resolveSoFar state funcTy) with
    | TyFunc(input, output, _) ->
        leftOut state input args typedArgs
        |> Option.map (fun (parameter, standsFor) ->
            output, { Call = expr.Id; At = expr.Span; Callee = callee; Parameter = parameter; StandsFor = standsFor })
    | _ -> None

/// The call `expr`, of a callee that gives back `output`, leaving out a type argument (see `LeavesOut`): it fails, and
/// is reported once the constraints say which type it should pass, but gives back `output` all the same, so the code
/// around it settles that type, as `Point` does in `def p: Point = deserialise(text)`.
let leaveOut (expr: Expression) (output: Ty) (call: LeftOutTypeArgument) (state: InferState) : TypedExpr * InferState =
    mkTypedExpr expr output None None None, { state with LeftOutTypeArguments = call :: state.LeftOutTypeArguments }

/// `state` with each call that leaves out a type argument failed, with an error that says which type to pass.
let report (state: InferState) : InferState =
    let subst = fst (Solving.solve state.Constraints)
    state.LeftOutTypeArguments
    |> List.rev
    |> List.fold (fun (current: InferState) call ->
        let written =
            match Unifier.apply subst call.StandsFor with
            | TyVar _ -> "type T"
            | known -> $"type {Unifier.tyToStringIn state.TypeDefs known}"
        let parameter, pass =
            match call.Parameter with
            | Some name -> $"the type argument '{name}'", $"'{name} = {written}'"
            | None -> "a type argument", $"`{written}`"
        let error =
            Diagnostics.at DiagnosticCodes.MissingTypeArgument call.At
                $"'{call.Callee}' takes {parameter}, which this call leaves out: a type argument is never left out, so pass {pass}"
        { current with FailedExprs = (call.Call, [ error ]) :: current.FailedExprs }) state

/// `type Point`, at `expr`: a value of type `Type(Point)`, once each type it names is known to exist.
let infer (state: InferState) (expr: Expression) (written: TypeExpr) : Result<TypedExpr * InferState, Diagnostic list> =
    TypeResolution.ensureTypesExist state [ written ]
    |> Result.map (fun () ->
        let standsFor, next = TypeResolution.typeExprToTy state written
        mkTypedExpr expr (TypeValues.ofType standsFor) None None None, next)
