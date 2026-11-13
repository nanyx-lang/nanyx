/// Lambda lifting. A lambda that appears as a value, with parameters or none, (a call or pipe argument, the right-hand
/// side of a local `def`, ...) becomes a top-level function `__lambda_N`, and the expression becomes a reference
/// to that name, so the rest of the backend only ever meets lambdas as definitions. A lambda that captures
/// variables of its enclosing function is lifted too: what it captures travels in its closure block (see `lift`).
module internal Transpiler.Wasm.LambdaLifting

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers

/// Every name a pattern binds.
let rec private boundNamesPattern (pattern: Pattern) : string list =
    match pattern.Kind with
    | IdentifierPattern name -> [ name ]
    | TagPattern(_, Some payload) -> boundNamesPattern payload
    | TuplePattern patterns
    | AndPattern patterns -> patterns |> List.collect boundNamesPattern
    | ListPattern(patterns, rest) ->
        (patterns |> List.collect boundNamesPattern) @ (rest |> Option.map boundNamesPattern |> Option.defaultValue [])
    | ListSplatMiddle(before, after) -> (before @ after) |> List.collect boundNamesPattern
    | RecordPattern(_, patterns) -> patterns |> List.collect boundNamesPattern
    | RecordMemberPattern(members, _) -> members |> List.collect (fun (name, p) -> name :: boundNamesPattern p)
    // A guard's predicate is inlined where the arm is tested, so what it binds is bound there.
    | GuardPattern predicate -> boundNames predicate
    | TagPattern(_, None)
    | LiteralPattern _
    | RangePattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []

/// Every name bound anywhere inside an expression: local defs, lambda parameters and pattern variables.
/// (An over-approximation of what a nested lambda could capture, which errs on the side of not lifting.) What a `use`
/// loads is not looked inside: its lambdas are lifted in the scope around it.
and private boundNames (expr: Expression) : string list =
    match expr.Kind with
    | Lambda(args, body, _) -> (args |> List.map fst) @ boundNames body
    | Block statements ->
        statements |> List.map _.Kind
        |> List.collect (function
            | DefStatement(_, name, _, rhs) -> name :: boundNames rhs
            | ExprStatement value -> boundNames value
            | UseStatement _
            | ImportStatement _
            | TypeDefStatement _ -> [])
    | Except(value, arms) -> boundNames { expr with Kind = Match([ value ], arms) }
    | Match(values, arms) ->
        (values |> List.collect boundNames)
        @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect boundNamesPattern) @ boundNames body))
    | UseIn(_, body) -> boundNames body
    | WorkflowBindExpr(name, value) -> name :: boundNames value
    | _ -> SyntaxTree.childrenOf expr |> fst |> List.collect boundNames

/// `expr` with every reference to the local function `from` redirected to its lifted form `to'`: a call
/// becomes a direct call that passes the environment on, and a use as a value is the environment itself
/// (the closure block). It is given a body already lifted, so it meets no `except` and no `use ... in`, and the
/// `use`s it meets load only locals.
let rec private redirectSelfReferences (from: string) (to': string) (expr: Expression) : Expression =
    let recur = redirectSelfReferences from to'
    let rebuild kind = { expr with Kind = kind }
    match expr.Kind with
    | IdentifierExpr name when name = from -> rebuild (IdentifierExpr ClosureSyntax.envParameter)
    | FunctionCall(name, range, args) when name = from ->
        rebuild (FunctionCall(to', range, [ Expression.from expr (TupleExpr (Expression.from expr (IdentifierExpr ClosureSyntax.envParameter) :: (SyntaxTree.flattenCallArgs args |> List.map recur))) ]))
    | Pipe(value, name, range, args) when name = from ->
        recur (rebuild (FunctionCall(name, range, pipeCallArguments value args)))
    | _ -> SyntaxTree.mapChildren recur expr

/// Where a lifted function came from: the top-level def it was written in, its local name there if it
/// was a `rec` def, and the names of the variables it captures, in the order they sit in its environment.
/// A lambda lifted from a call's argument also keeps the lambda as written, to find its type in the owner's. `Lambda`
/// is the lambda as written for any lifted function, whose node the type checker typed.
type LiftedOrigin = { Owner: string; LocalName: string option; Captured: string list; Source: Expression option; Lambda: Expression }

/// `ty` with the type parameters of a context replaced by the arguments of one of its instances.
let rec private substituteTypeParameters (arguments: Map<string, TypeExpr>) (ty: TypeExpr) : TypeExpr =
    let recur = substituteTypeParameters arguments
    match ty.Kind with
    | TypeName name
    | TypeVar name when arguments.ContainsKey name -> arguments.[name]
    | TypeFunc(input, output) -> { ty with Kind = TypeFunc(recur input, recur output) }
    | TypeTuple items -> { ty with Kind = TypeTuple(items |> List.map recur) }
    | TypeApply(name, items) -> { ty with Kind = TypeApply(name, items |> List.map recur) }
    | _ -> ty

/// A lambda bound to a context member, with each parameter it leaves unannotated given the type the member
/// declares for it, so the lifted function knows its parameters' types (a string to interpolate, say).
let private annotateFromMemberType (memberType: TypeExpr option) (args: (string * TypeExpr option) list) =
    let declared =
        match memberType with
        | Some (Kind (TypeFunc(Kind (TypeTuple items), _))) when items.Length = args.Length -> items
        | Some (Kind (TypeFunc(input, _))) when args.Length = 1 -> [ input ]
        | _ -> []
    if declared.IsEmpty then args
    else List.map2 (fun (name, annotation) ty -> name, Some (annotation |> Option.defaultValue ty)) args declared

/// Closure conversion. Every function value is a closure block: a tuple of the function's table slot
/// followed by the values it captures. A lifted function takes that block as its first parameter,
/// `__env`, and starts by binding each captured name to the matching field; a call through a value
/// passes the block as the environment and calls the slot it holds. A top-level function used as a
/// value gets a wrapper that takes and ignores an environment. Lifts the lambdas of a module into
/// top-level definitions appended at the end, and returns where each came from (lifting runs after
/// type checking, so the backend uses that to find the types of parameters and captures) and the
/// wrapper made for each top-level function used as a value.
///
/// A context member is a function value too. Each binding of a `use` is evaluated into a local of its
/// own, `__use_<key>_N`, and the statement is left as `use (<key> = __use_<key>_N, ...)`, so a binding
/// can be any function-valued expression: a lambda (which may capture), a top-level function, a
/// parameter or local. The top-level functions that can stand in for a context member when nothing in
/// scope supplies one (the ambient instance, like the runtime's `Console`) get wrappers up front, as do
/// `synthesized`, the top-level functions an instance the compiler builds from attached definitions is made of.
///
/// `memberCaptures` gives, for each definition that requires contexts, the keys of its context members, which come in
/// parameters of their own. A lambda lifted out of it captures those it may call, as it captures a local, so that it
/// can call the members and pass them on to what it calls (see `withMembers`); `requiresContexts` says which functions
/// are passed members. It captures the locals the members of each `use` in scope are evaluated into as well, those it
/// may call, and loads them again with a `use` of its own. But it captures none of the members `passedMembers` gives
/// for it, by key: those of the contexts its type requires, which each call passes it instead.
///
/// `useContexts` is the instance of a parameterised context each `use` loads, by the node of its value (see
/// `TypedModule.UseContexts`): its members are keyed by it, and read from the value one by one. A `use` left behind
/// binds such a member by its key's text, and the key of each such name is returned too.
let lift (synthesized: Set<string>) (memberCaptures: Map<string, ContextMemberKey list>) (requiresContexts: string -> bool) (passedMembers: Expression -> Set<ContextMemberKey>) (useContexts: Map<NodeId, TypeExpr>) (module': Module) : Module * Map<string, LiftedOrigin> * Map<string, string> * Map<string, ContextMemberKey> =
    let lifted = System.Collections.Generic.List<TopLevelItem>()
    let origins = System.Collections.Generic.Dictionary<string, LiftedOrigin>()
    let wrappers = System.Collections.Generic.Dictionary<string, string>()
    let useKeys = System.Collections.Generic.Dictionary<string, ContextMemberKey>()
    let mutable owner = ""
    // The context members of `owner`: the name each is captured by, and its key.
    let mutable ownerMembers: (string * ContextMemberKey) list = []
    // The context members the `use`s in scope load: each local a binding is evaluated into, with the keys the
    // member is in scope by (its own, and its name), oldest first.
    let mutable useMembers: (string * ContextMemberKey list) list = []
    let mutable counter = 0
    // Whether some lambda is passed the members of contexts where it is called, which may be the ambient instance's.
    let mutable passesMembers = false

    // Every top-level function, with its number of parameters. One of none (`{ ... }`) is a function too: its name,
    // written on its own, is the function as a value, never a call (#182).
    let topLevelArities =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, Kind (Lambda(args, _, _))))) -> Some (name, args.Length)
            | _ -> None)
        |> Map.ofList
    let topLevelFunctions = topLevelArities |> Map.filter (fun _ arity -> arity > 0)

    // Every top-level record value, by name, for `use ctx` where `ctx` is one of them.
    let topLevelRecords =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, Kind (RecordExpr fields)))) -> Some (name, fields)
            | _ -> None)
        |> Map.ofList

    /// The type parameters and member types of every record type, by name: contexts are declared as
    /// records, with or without the `context` modifier.
    let recordTypes =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, _, parameters, Kind (TypeRecord fields)))) ->
                let members =
                    fields
                    |> List.choose (function
                        | TypeField(fieldName, _, typeOpt, _)
                        | TypeMember(fieldName, typeOpt) -> typeOpt |> Option.map (fun ty -> fieldName, ty))
                    |> Map.ofList
                Some (name, (parameters |> List.map fst, members))
            | _ -> None)
        |> Map.ofList

    /// The type a context declares for a member: of the named context (with its type parameters bound
    /// to the instance's arguments), or of any record type with such a member when the context is not named.
    let memberType (contextType: TypeExpr option) (memberName: string) : TypeExpr option =
        match contextType with
        | Some (Kind (TypeName name)) ->
            recordTypes |> Map.tryFind name |> Option.bind (fun (_, members) -> members |> Map.tryFind memberName)
        | Some (Kind (TypeApply(name, arguments))) ->
            recordTypes
            |> Map.tryFind name
            |> Option.bind (fun (parameters, members) ->
                members
                |> Map.tryFind memberName
                |> Option.map (fun ty ->
                    if parameters.Length = arguments.Length then
                        substituteTypeParameters (List.zip parameters arguments |> Map.ofList) ty
                    else ty))
        | _ -> recordTypes |> Map.toSeq |> Seq.tryPick (fun (_, (_, members)) -> members |> Map.tryFind memberName)

    /// The wrapper that lets the top-level function `name` be called as a function value.
    let wrapperOf (name: string) =
        match wrappers.TryGetValue name with
        | true, existing -> existing
        | _ ->
            let wrapperName = $"__fn_{sanitizeIdentifier name}"
            let parameters = [ for index in 1 .. topLevelArities.[name] -> $"__arg{index}" ]
            let arguments =
                match parameters with
                | [] -> []
                | _ -> [ Expression.synthetic (TupleExpr (parameters |> List.map (fun p -> Expression.synthetic (IdentifierExpr p)))) ]
            let call = Expression.synthetic (FunctionCall(name, None, arguments))
            lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, wrapperName, None, Expression.synthetic (Lambda((ClosureSyntax.envParameter, None) :: (parameters |> List.map (fun p -> p, None)), call, false))))))
            wrappers.[name] <- wrapperName
            wrapperName

    /// The name a `use` left behind binds the member of `key` by: a named member's name, or an instance's member's key's
    /// text, noted in `useKeys`.
    let fieldName (key: ContextMemberKey) =
        match key with
        | NamedMember name -> name
        | InstanceMember _ ->
            let name = ContextMemberKey.text key
            useKeys.[name] <- key
            name

    /// `captured` with the context members of `owner` and of the `use`s in scope that `body` may call after it, those it
    /// does not capture already. `body` may call a member when it names it (as a call, a value or an operator, by its
    /// key or its own name), names a function that requires contexts, or makes a closure that captures it. The names are
    /// read from `written`, the body as written, with the lambdas in it, so that a member one of them is passed where it
    /// is called is among them, and from `body`, the body lifted. The members of the keys in `passed`, which each call
    /// passes the lambda (see `passedMembers`), are not among them.
    let withMembers (passed: Set<ContextMemberKey>) (written: Expression) (body: Expression) (captured: string list) =
        let memberName (key: ContextMemberKey) =
            match key with
            | NamedMember name
            | InstanceMember(_, name) -> name
        let mentioned =
            SyntaxTree.expressionsWithin written @ SyntaxTree.expressionsWithin body
            |> List.choose (fun expr ->
                match expr.Kind with
                | IdentifierExpr name
                | FunctionCall(name, _, _)
                | Pipe(_, name, _, _)
                | BinaryOp(name, _, _, _)
                | ContextMemberCall(_, name, _)
                | ContextMemberAccess(_, name) -> Some name
                | _ -> None)
            |> Set.ofList
        let callsContexts = mentioned |> Set.exists requiresContexts
        let usedMembers =
            useMembers
            |> List.filter (fun (local, keys) ->
                callsContexts || mentioned.Contains local || keys |> List.exists (function NamedMember name -> mentioned.Contains name | InstanceMember _ -> false))
            |> List.filter (fun (_, keys) -> not (keys |> List.exists passed.Contains))
            |> List.map fst
        let ownerMembers =
            ownerMembers
            |> List.filter (fun (_, key) -> not (passed.Contains key) && (callsContexts || mentioned.Contains (memberName key)))
            |> List.map fst
        captured @ (ownerMembers @ usedMembers |> List.filter (fun name -> not (List.contains name captured)))

    /// The closure block for the lifted function `liftedName` over the given captured names, standing in for `origin`.
    let closureOf (origin: Expression) (liftedName: string) (captured: string list) =
        ClosureSyntax.make origin liftedName (captured |> List.map (fun name -> Expression.synthetic (IdentifierExpr name)))

    /// The lifted function's lambda: the environment parameter first, then the captured names bound
    /// from the environment's fields ahead of the original body, and the members of the `use`s in scope loaded
    /// again from their captured locals. It is reported where `lambda` was written.
    let liftedLambda (lambda: Expression) (args: (string * TypeExpr option) list) (captured: string list) (body: Expression) =
        let passed = passedMembers lambda
        let useBinding =
            useMembers
            |> List.filter (fun (local, _) -> List.contains local captured)
            |> List.collect (fun (local, keys) ->
                keys |> List.filter (passed.Contains >> not) |> List.map (fun key -> NamedField(fieldName key, Expression.synthetic (IdentifierExpr local))))
            |> function
                | [] -> []
                | fields -> [ Statement.synthetic (UseStatement(Expression.synthetic (RecordExpr fields))) ]
        let captureBindings =
            (captured
             |> List.mapi (fun index name ->
                 Statement.synthetic (DefStatement(false, name, None, Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr ClosureSyntax.envParameter), string (index + 1), None))))))
            @ useBinding
        let bodyWithCaptures =
            match captureBindings, body.Kind with
            | [], _ -> body
            | _, Block statements -> { body with Kind = Block (captureBindings @ statements) }
            | _, _ -> Expression.from body (Block (captureBindings @ [ Statement.from body (ExprStatement body) ]))
        Expression.from lambda (Lambda((ClosureSyntax.envParameter, None) :: args, bodyWithCaptures, false))

    // `scope` is every name the enclosing function binds; a lambda referring to one of them captures it.
    let rec liftExpr (scope: Set<string>) (expr: Expression) : Expression =
        let recur = liftExpr scope
        let rebuild kind = { expr with Kind = kind }
        match expr.Kind with
        // A `for` loop's lambda is inlined as the loop body by the backend, never lifted; lift inside it.
        | ForSyntax.Call(iterables, lambda, args, body) ->
            let innerScope = Set.union (args |> List.map fst |> Set.ofList) (boundNames body |> Set.ofList)
            rebuild (FunctionCall(ForSyntax.builtIn, None, List.map recur iterables @ [ { lambda with Kind = Lambda(args, liftExpr (Set.union scope innerScope) body, false) } ]))
        | Lambda(args, body, _) -> liftLambda scope expr args body
        // A top-level function used as a value: a closure over a wrapper that takes an environment.
        | IdentifierExpr name when topLevelArities.ContainsKey name && not (scope.Contains name) ->
            closureOf expr (wrapperOf name) []
        | Block statements ->
            // The records defined in this block, for `use ctx` where `ctx` is one of them.
            let localRecords =
                statements
                |> List.choose (function
                    | Kind (DefStatement(_, name, _, Kind (RecordExpr fields))) -> Some (name, fields)
                    | _ -> None)
                |> Map.ofList
            // Each of them a `use` below loads, with the instance it is if it is one of a parameterised context.
            let usedRecords =
                statements
                |> List.choose (function
                    | Kind (UseStatement(Kind (IdentifierExpr name) as value)) when localRecords.ContainsKey name ->
                        Some (name, useContexts |> Map.tryFind value.Id)
                    | _ -> None)
                |> Map.ofList
            // A lambda bound to a context member takes the types of its parameters from the member's declared type.
            let liftMember contextType memberName (value: Expression) =
                match value.Kind with
                | Lambda(args, body, _) -> liftLambda scope value (annotateFromMemberType (memberType contextType memberName) args) body
                | _ -> recur value
            // What a `use` in the block loads is in scope until the block ends.
            let outerUseMembers = useMembers
            let leavingScope statements =
                useMembers <- outerUseMembers
                statements
            statements
            |> List.collect (fun statement ->
                let keep kind = { statement with Kind = kind }
                match statement.Kind with
                // `rec name = { ... }`: a local function that calls itself. It is lifted under a fresh
                // name with its self-references redirected there (a self-call passes the environment on),
                // and the local is bound to its closure.
                | DefStatement(isExport, name, _, Kind (RecSyntax.Call (Kind (Lambda(args, body, _)) as lambda))) when not args.IsEmpty ->
                    let liftedName = $"__rec_{sanitizeIdentifier name}_{counter}"
                    counter <- counter + 1
                    let parameterNames = args |> List.map fst |> Set.ofList
                    let innerScope = Set.union parameterNames (boundNames body |> Set.ofList) |> Set.add name
                    let liftedBody = liftExpr (Set.union scope innerScope) body |> redirectSelfReferences name liftedName
                    let captured =
                        Set.difference (ClosureSyntax.references liftedBody |> List.map fst |> Set.ofList) (innerScope |> Set.add liftedName |> Set.add ClosureSyntax.envParameter)
                        |> Set.intersect scope
                        |> Set.toList
                        |> withMembers (passedMembers lambda) body liftedBody
                    lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, liftedName, None, liftedLambda lambda args captured liftedBody))))
                    origins.[liftedName] <- { Owner = owner; LocalName = Some name; Captured = captured; Source = None; Lambda = lambda }
                    [ keep (DefStatement(isExport, name, None, closureOf lambda liftedName captured)) ]
                | DefStatement(isExport, name, typeOpt, Kind (RecSyntax.Call rhs)) ->
                    [ keep (DefStatement(isExport, name, typeOpt, recur rhs)) ]
                // A record a `use` binds below: its fields are context members.
                | DefStatement(isExport, name, typeOpt, (Kind (RecordExpr fields) as record)) when usedRecords.ContainsKey name ->
                    let fields =
                        fields
                        |> List.map (function
                            | NamedField(memberName, value) -> NamedField(memberName, liftMember usedRecords.[name] memberName value)
                            | PositionalField value -> PositionalField(recur value))
                    [ keep (DefStatement(isExport, name, typeOpt, { record with Kind = RecordExpr fields })) ]
                | DefStatement(isExport, name, typeOpt, rhs) -> [ keep (DefStatement(isExport, name, typeOpt, recur rhs)) ]
                | ExprStatement value -> [ keep (ExprStatement(recur value)) ]
                | UseStatement value ->
                    let namedFields (fields: RecordField list) =
                        fields |> List.choose (function NamedField(name, value) -> Some (name, value) | PositionalField _ -> None)
                    let instance = useContexts |> Map.tryFind value.Id
                    // Each member of `instance`, read from `instanceValue`.
                    let membersOf (instanceValue: Expression) =
                        match instance with
                        | Some (Kind (TypeApply(contextName, _))) ->
                            recordTypes
                            |> Map.tryFind contextName
                            |> Option.map (fun (_, members) -> members.Keys |> List.ofSeq)
                            |> Option.defaultValue []
                            |> List.map (fun name -> name, Expression.from value (MemberAccess(instanceValue, name, None)))
                        | _ -> []
                    // Each of `fields`, read from `record`.
                    let fieldsOf (record: Expression) (fields: RecordField list) =
                        namedFields fields
                        |> List.map (fun (name, _) -> name, Expression.from value (MemberAccess(record, name, None)))
                    let evaluated, contextType, bindings =
                        match value.Kind with
                        | RecordExpr fields -> [], None, namedFields fields
                        | FunctionCall(contextName, _, [ Kind (RecordExpr fields) ]) -> [], Some (TypeExpr.synthetic (TypeName contextName)), namedFields fields
                        | IdentifierExpr recordName when localRecords.ContainsKey recordName ->
                            [], instance, fieldsOf (Expression.from value (IdentifierExpr recordName)) localRecords.[recordName]
                        // A top-level record of a plain context (one of a parameterised context is read below): read
                        // from `value` itself, which has the record's type, as a top-level value is evaluated by it.
                        | IdentifierExpr recordName when instance.IsNone && topLevelRecords.ContainsKey recordName && not (scope.Contains recordName) ->
                            [], None, fieldsOf value topLevelRecords.[recordName]
                        | IdentifierExpr _ -> [], instance, membersOf value
                        // Any other instance is evaluated once, into a local its members are read from.
                        | _ when instance.IsSome ->
                            let local = $"__use_instance_{counter}"
                            counter <- counter + 1
                            [ Statement.from value (DefStatement(false, local, None, recur value)) ], instance, membersOf (Expression.from value (IdentifierExpr local))
                        | _ -> [], None, []
                    if bindings.IsEmpty then [ statement ]
                    else
                        let locals =
                            bindings
                            |> List.map (fun (memberName, value) ->
                                let key = contextMemberKey contextType memberName
                                let local = $"__use_{sanitizeIdentifier (ContextMemberKey.text key)}_{counter}"
                                counter <- counter + 1
                                memberName, key, local, liftMember contextType memberName value)
                        // Each member by its key, and by its own name too, as the block can call it by that (`a == b`
                        // after `use ignoringCase` calls its `==`).
                        let names = locals |> List.map (fun (memberName, key, local, _) -> local, [ key; NamedMember memberName ] |> List.distinct)
                        let fields =
                            names
                            |> List.collect (fun (local, keys) -> keys |> List.map (fun key -> NamedField(fieldName key, Expression.synthetic (IdentifierExpr local))))
                        // A lambda below captures the members, to load them again (see `liftedLambda`).
                        useMembers <- useMembers @ names
                        [ yield! evaluated
                          yield! locals |> List.map (fun (_, _, local, value) -> Statement.from value (DefStatement(false, local, None, value)))
                          keep (UseStatement(Expression.synthetic (RecordExpr fields))) ]
                | ImportStatement _ | TypeDefStatement _ -> [ statement ])
            |> leavingScope
            |> Block
            |> rebuild
        | Except(value, arms) -> recur { expr with Kind = Match([ value ], arms) }
        | Match(values, arms) ->
            // A guard's predicate is inlined by the backend where its arm is tested, as a `for` loop's lambda is,
            // never lifted; lift inside it.
            let liftGuard (predicate: Expression) =
                match predicate.Kind with
                | Lambda(args, body, takesUnit) ->
                    let innerScope = Set.union (args |> List.map fst |> Set.ofList) (boundNames body |> Set.ofList)
                    { predicate with Kind = Lambda(args, liftExpr (Set.union scope innerScope) body, takesUnit) }
                | _ -> recur predicate
            let liftArm (patterns, body) = patterns |> List.map (Pattern.mapExpressions liftGuard), recur body
            rebuild (Match(values |> List.map recur, arms |> List.map liftArm))
        // An attached function used as a value, `int.toString`: the parser reads it as a member access.
        | MemberAccess(Kind (IdentifierExpr qualifier), field, _) when
            topLevelArities.ContainsKey $"{qualifier}.{field}" && not (scope.Contains qualifier) ->
            closureOf expr (wrapperOf $"{qualifier}.{field}") []
        // `use value in body` is the block `{ use value; body }`: what it loads is in scope for `body` only.
        | UseIn(value, body) ->
            recur { expr with Kind = Block [ Statement.from value (UseStatement value); Statement.from body (ExprStatement body) ] }
        | _ -> SyntaxTree.mapChildren recur expr

    /// Lifts `{ args -> body }` to a fresh top-level function and returns its closure.
    and liftLambda (scope: Set<string>) (lambda: Expression) (args: (string * TypeExpr option) list) (body: Expression) : Expression =
        let parameterNames = args |> List.map fst |> Set.ofList
        let innerScope = Set.union parameterNames (boundNames body |> Set.ofList)
        // A lambda nested inside may capture what this one binds and what encloses this one alike;
        // what it captures from outside is then referenced here, so this lambda captures it in turn.
        let liftedBody = liftExpr (Set.union scope innerScope) body
        let captured =
            Set.difference (ClosureSyntax.references liftedBody |> List.map fst |> Set.ofList) innerScope
            |> Set.intersect scope
            |> Set.toList
            |> withMembers (passedMembers lambda) body liftedBody
        let name = $"__lambda_{counter}"
        counter <- counter + 1
        if not (passedMembers lambda).IsEmpty then passesMembers <- true
        lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, name, None, liftedLambda lambda args captured liftedBody))))
        origins.[name] <- { Owner = owner; LocalName = None; Captured = captured; Source = Some lambda; Lambda = lambda }
        closureOf lambda name captured

    let liftItem (item: TopLevelItem) : TopLevelItem =
        match item.Kind with
        // A definition's own lambda is the function being defined; lift inside its body only.
        | Def (ValueDef(isExport, name, typeOpt, (Kind (Lambda(args, body, takesUnit)) as lambda))) ->
            owner <- name
            ownerMembers <- memberCaptures |> Map.tryFind name |> Option.defaultValue [] |> List.map (fun key -> contextMemberParameter key, key)
            let scope = Set.union (args |> List.map fst |> Set.ofList) (boundNames body |> Set.ofList) |> Set.union (ownerMembers |> List.map fst |> Set.ofList)
            { item with Kind = Def (ValueDef(isExport, name, typeOpt, { lambda with Kind = Lambda(args, liftExpr scope body, takesUnit) })) }
        | Def (ValueDef(isExport, name, typeOpt, rhs)) ->
            owner <- name
            ownerMembers <- []
            { item with Kind = Def (ValueDef(isExport, name, typeOpt, liftExpr (boundNames rhs |> Set.ofList) rhs)) }
        | Expr expr ->
            owner <- ""
            ownerMembers <- []
            { item with Kind = Expr (liftExpr (boundNames expr |> Set.ofList) expr) }
        | _ -> item

    let items = module' |> List.map liftItem

    // The ambient instance: a top-level function named like a member of a declared context, which takes
    // no contexts itself, stands in for that member wherever nothing else supplies it. Only worth a
    // wrapper when some function takes contexts, or some lambda is passed members where it is called.
    let takesContexts (typeOpt: TypeExpr option) =
        match typeOpt with
        | Some (Kind (TypeWithContext _))
        | Some (Kind (TypeContext _)) -> true
        | _ -> false
    let defTypes =
        module' |> List.choose (function Kind (Def (ValueDef(_, name, typeOpt, _))) -> Some (name, typeOpt) | _ -> None) |> Map.ofList
    if passesMembers || defTypes |> Map.exists (fun _ typeOpt -> takesContexts typeOpt) then
        AmbientMembers.ambientMemberDefinitions module'
        |> List.map snd
        |> List.filter (fun name -> topLevelFunctions.ContainsKey name && not (takesContexts defTypes.[name]))
        |> List.iter (wrapperOf >> ignore)
    synthesized |> Set.filter topLevelArities.ContainsKey |> Set.iter (wrapperOf >> ignore)

    items @ List.ofSeq lifted,
    origins |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq,
    wrappers |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq,
    useKeys |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
