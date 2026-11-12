/// Lambda lifting. A lambda that appears as a value, with parameters or none, (a call or pipe argument, the right-hand
/// side of a local `def`, ...) becomes a top-level function `__lambda_N`, and the expression becomes a reference
/// to that name, so the rest of the backend only ever meets lambdas as definitions. A lambda that captures
/// variables of its enclosing function is lifted too: what it captures travels in its closure block (see `lift`).
module internal Transpiler.Wasm.LambdaLifting

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.Identifiers

/// Every name an expression refers to, with the node that refers to it: identifiers, callees and pipe targets.
let rec references (expr: Expression) : (string * Expression) list =
    match expr.Kind with
    | IdentifierExpr name -> [ name, expr ]
    | FunctionCall(name, _, args) -> (name, expr) :: (args |> List.collect references)
    | Pipe(value, name, _, args) -> (name, expr) :: references value @ (args |> List.collect references)
    | Lambda(_, body, _) -> references body
    | BinaryOp(_, left, right) -> references left @ references right
    | IfExpr(cond, thenExpr, elseExpr) -> references cond @ references thenExpr @ references elseExpr
    | Block statements -> statements |> List.collect referencesInStatement
    | Except(value, arms) -> references { expr with Kind = Match([ value ], arms) }
    | Match(values, arms) ->
        (values |> List.collect references)
        @ (arms |> List.collect (fun (patterns, body) -> (patterns |> List.collect referencesInPattern) @ references body))
    | TupleExpr items
    | ListExpr items -> items |> List.collect references
    | RecordExpr fields ->
        fields |> List.collect (function NamedField(_, value) | PositionalField value -> references value)
    | TagExpr(_, payload) -> payload |> Option.map references |> Option.defaultValue []
    | MemberAccess(inner, _, _) -> references inner
    | UseIn(value, body) -> references value @ references body
    | WorkflowBindExpr(_, value)
    | WorkflowReturnExpr value -> references value
    | InterpolatedString parts ->
        parts |> List.collect (function StringExpr value -> references value | StringText _ -> [])
    | ContextMemberCall(_, _, args) -> args |> List.collect references
    | ContextMemberAccess _
    | UnitExpr
    | LiteralExpr _ -> []

and private referencesInStatement (statement: Statement) : (string * Expression) list =
    match statement.Kind with
    | DefStatement(_, _, _, rhs) -> references rhs
    | ExprStatement value
    | UseStatement value -> references value
    | ImportStatement _
    | TypeDefStatement _ -> []

and private referencesInPattern (pattern: Pattern) : (string * Expression) list =
    match pattern.Kind with
    | GuardPattern predicate -> references predicate
    | RangePattern(startValue, endValue) -> references startValue @ references endValue
    | TagPattern(_, Some payload) -> referencesInPattern payload
    | TuplePattern patterns
    | AndPattern patterns
    | ListPattern(patterns, _) -> patterns |> List.collect referencesInPattern
    | ListSplatMiddle(before, after) -> (before @ after) |> List.collect referencesInPattern
    | RecordPattern(_, patterns) -> patterns |> List.collect referencesInPattern
    | RecordMemberPattern(members, _) -> members |> List.collect (snd >> referencesInPattern)
    | TagPattern(_, None)
    | LiteralPattern _
    | IdentifierPattern _
    | WildcardPattern
    | UnitPattern
    | ElsePattern -> []

/// The nodes in `expr`, after lifting, that use the local `name`: each reference to it, and, for a closure that captures
/// it, the uses in the lifted function the closure is over (its definition, which `definitionOf` finds), which binds it
/// under the same name. The capture itself is not a use.
let rec usesThroughClosures (definitionOf: string -> Expression option) (name: string) (expr: Expression) : Expression list =
    let found = references expr
    let closures =
        found
        |> List.choose (fun (_, node) ->
            match node.Kind with
            | FunctionCall("__closure", _, Kind (FunctionCall("__slot", _, [ Kind (IdentifierExpr target) ])) :: captured) ->
                let captures = captured |> List.filter (function Kind (IdentifierExpr captured) -> captured = name | _ -> false)
                if captures.IsEmpty then None else Some (target, captures)
            | _ -> None)
    let captures = closures |> List.collect snd
    let direct =
        found
        |> List.filter (fun (referenced, node) -> referenced = name && not (captures |> List.exists (fun capture -> obj.ReferenceEquals(capture, node))))
        |> List.map snd
    direct @ (closures |> List.collect (fun (target, _) -> definitionOf target |> Option.map (usesThroughClosures definitionOf name) |> Option.defaultValue []))

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
/// (An over-approximation of what a nested lambda could capture, which errs on the side of not lifting.)
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
    | FunctionCall(_, _, args)
    | ContextMemberCall(_, _, args) -> args |> List.collect boundNames
    | Pipe(value, _, _, args) -> boundNames value @ (args |> List.collect boundNames)
    | BinaryOp(_, left, right) -> boundNames left @ boundNames right
    | IfExpr(cond, thenExpr, elseExpr) -> boundNames cond @ boundNames thenExpr @ boundNames elseExpr
    | TupleExpr items
    | ListExpr items -> items |> List.collect boundNames
    | RecordExpr fields -> fields |> List.collect (function NamedField(_, value) | PositionalField value -> boundNames value)
    | TagExpr(_, payload) -> payload |> Option.map boundNames |> Option.defaultValue []
    | MemberAccess(inner, _, _) -> boundNames inner
    | UseIn(_, body) -> boundNames body
    | WorkflowBindExpr(name, value) -> name :: boundNames value
    | WorkflowReturnExpr value -> boundNames value
    | InterpolatedString parts -> parts |> List.collect (function StringExpr value -> boundNames value | StringText _ -> [])
    | ContextMemberAccess _
    | UnitExpr
    | LiteralExpr _
    | IdentifierExpr _ -> []

/// The name of the environment parameter every lifted function takes first: the closure block itself.
let envParameter = "__env"

/// `expr` with every reference to the local function `from` redirected to its lifted form `to'`: a call
/// becomes a direct call that passes the environment on, and a use as a value is the environment itself
/// (the closure block).
let rec private redirectSelfReferences (from: string) (to': string) (expr: Expression) : Expression =
    let recur = redirectSelfReferences from to'
    let rebuild kind = { expr with Kind = kind }
    let flatten (args: Expression list) =
        match args with
        | [ Kind (TupleExpr items) ] -> items
        | _ -> args
    match expr.Kind with
    | IdentifierExpr name when name = from -> rebuild (IdentifierExpr envParameter)
    | IdentifierExpr _ -> expr
    | FunctionCall(name, range, args) when name = from ->
        rebuild (FunctionCall(to', range, [ Expression.from expr (TupleExpr (Expression.from expr (IdentifierExpr envParameter) :: (flatten args |> List.map recur))) ]))
    | FunctionCall(name, range, args) -> rebuild (FunctionCall(name, range, args |> List.map recur))
    | Pipe(value, name, range, args) when name = from ->
        recur (rebuild (FunctionCall(name, range, pipeCallArguments value args)))
    | Pipe(value, name, range, args) -> rebuild (Pipe(recur value, name, range, args |> List.map recur))
    | Lambda(args, body, takesUnit) -> rebuild (Lambda(args, recur body, takesUnit))
    | BinaryOp(op, left, right) -> rebuild (BinaryOp(op, recur left, recur right))
    | IfExpr(cond, thenExpr, elseExpr) -> rebuild (IfExpr(recur cond, recur thenExpr, recur elseExpr))
    | Block statements ->
        statements
        |> List.map (fun statement ->
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, rhs) -> { statement with Kind = DefStatement(isExport, name, typeOpt, recur rhs) }
            | ExprStatement value -> { statement with Kind = ExprStatement(recur value) }
            | _ -> statement)
        |> Block
        |> rebuild
    | Except(value, arms) -> redirectSelfReferences from to' { expr with Kind = Match([ value ], arms) }
    | Match(values, arms) ->
        rebuild (Match(values |> List.map recur, arms |> List.map (fun (patterns, body) -> patterns |> List.map (Pattern.mapExpressions recur), recur body)))
    | TupleExpr items -> rebuild (TupleExpr(items |> List.map recur))
    | ListExpr items -> rebuild (ListExpr(items |> List.map recur))
    | RecordExpr fields ->
        fields
        |> List.map (function
            | NamedField(name, value) -> NamedField(name, recur value)
            | PositionalField value -> PositionalField(recur value))
        |> RecordExpr
        |> rebuild
    | TagExpr(name, payload) -> rebuild (TagExpr(name, payload |> Option.map recur))
    | MemberAccess(inner, field, range) -> rebuild (MemberAccess(recur inner, field, range))
    | UseIn(binding, body) -> rebuild (UseIn(binding, recur body))
    | WorkflowBindExpr(name, value) -> rebuild (WorkflowBindExpr(name, recur value))
    | WorkflowReturnExpr value -> rebuild (WorkflowReturnExpr(recur value))
    | InterpolatedString parts ->
        parts |> List.map (function StringExpr value -> StringExpr(recur value) | text -> text) |> InterpolatedString |> rebuild
    | ContextMemberCall(ctx, name, args) -> rebuild (ContextMemberCall(ctx, name, args |> List.map recur))
    | ContextMemberAccess _
    | UnitExpr
    | LiteralExpr _ -> expr

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

/// The top-level definitions that can stand in for the members of the contexts `module'` declares (the ambient
/// instance, like the runtime's `Console`), each with the member it stands in for, in the order the contexts declare
/// them: the definition of the member's own name, or else, for a context without parameters whose every member is
/// defined under one qualifier, the definition under it (`c.write`, where `nanyx/console` is imported as `c`). The type
/// checker finds an aliased import's ambient instance the same way.
let ambientMemberDefinitions (module': Module) : (string * string) list =
    let defined = module' |> List.choose (function Kind (Def (ValueDef(_, name, _, _))) -> Some name | _ -> None) |> Set.ofList
    module'
    |> List.collect (function
        | Kind (Def (TypeDef(_, modifiers, parameters, Kind (TypeRecord fields)))) when modifiers |> List.contains Context ->
            let members = fields |> List.map (function TypeField(fieldName, _, _, _, _) | TypeMember(fieldName, _) -> fieldName)
            let qualifiers =
                match members with
                | first :: _ when parameters.IsEmpty ->
                    defined
                    |> Seq.choose (fun name ->
                        match name.LastIndexOf '.' with
                        | dot when dot > 0 && name.Substring(dot + 1) = first -> Some (name.Substring(0, dot))
                        | _ -> None)
                    |> Seq.filter (fun qualifier -> members |> List.forall (fun memberName -> defined.Contains $"{qualifier}.{memberName}"))
                    |> List.ofSeq
                | _ -> []
            members
            |> List.choose (fun memberName ->
                if defined.Contains memberName then Some (memberName, memberName)
                else qualifiers |> List.tryHead |> Option.map (fun qualifier -> memberName, $"{qualifier}.{memberName}"))
        | _ -> [])
    |> List.distinctBy fst

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
/// `memberCaptures` names, for each definition that requires contexts, the parameters its context members come in. A
/// lambda lifted out of it captures them, as it captures a local, so that it can call the members and pass them on to
/// what it calls. It captures the locals the members of each `use` in scope are evaluated into as well, those it may
/// call (see `withMembers`), and loads them again with a `use` of its own. But it captures none of the members
/// `passedMembers` gives for it, by key: those of the contexts its type requires, which each call passes it instead.
///
/// `useContexts` is the instance of a parameterised context each `use` loads, by the node of its value (see
/// `TypedModule.UseContexts`): its members are keyed by it, and read from the value one by one.
let lift (synthesized: Set<string>) (memberCaptures: Map<string, string list>) (passedMembers: Expression -> Set<string>) (useContexts: Map<NodeId, TypeExpr>) (module': Module) : Module * Map<string, LiftedOrigin> * Map<string, string> =
    let lifted = System.Collections.Generic.List<TopLevelItem>()
    let origins = System.Collections.Generic.Dictionary<string, LiftedOrigin>()
    let wrappers = System.Collections.Generic.Dictionary<string, string>()
    let mutable owner = ""
    // The names the context members of `owner` are captured by.
    let mutable ownerMembers: string list = []
    // The context members the `use`s in scope load: each local a binding is evaluated into, with the names the
    // member is in scope by (its key and its own name), oldest first.
    let mutable useMembers: (string * string list) list = []
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
                        | TypeField(fieldName, _, _, typeOpt, _)
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
            lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, wrapperName, None, Expression.synthetic (Lambda((envParameter, None) :: (parameters |> List.map (fun p -> p, None)), call, false))))))
            wrappers.[name] <- wrapperName
            wrapperName

    /// `captured` with the context members of `owner` after it, and those of the `use`s in scope that `body` may call,
    /// those it does not capture already. `body` may call a member a `use` loads when it names it (as a call, a value
    /// or an operator), names a function that requires contexts, or makes a closure that captures it. The members of
    /// the keys in `passed`, which each call passes the lambda (see `passedMembers`), are not among them.
    let withMembers (passed: Set<string>) (body: Expression) (captured: string list) =
        let mentioned =
            SyntaxTree.expressionsWithin body
            |> List.choose (fun expr ->
                match expr.Kind with
                | IdentifierExpr name
                | FunctionCall(name, _, _)
                | Pipe(_, name, _, _)
                | BinaryOp(name, _, _)
                | ContextMemberCall(_, name, _)
                | ContextMemberAccess(_, name) -> Some name
                | _ -> None)
            |> Set.ofList
        let callsContexts = mentioned |> Set.exists memberCaptures.ContainsKey
        let usedMembers =
            useMembers
            |> List.filter (fun (local, names) -> callsContexts || mentioned.Contains local || names |> List.exists mentioned.Contains)
            |> List.filter (fun (_, names) -> not (names |> List.exists passed.Contains))
            |> List.map fst
        let ownerMembers = ownerMembers |> List.filter (fun name -> not (passed |> Set.exists (fun key -> contextMemberParameter key = name)))
        captured @ (ownerMembers @ usedMembers |> List.filter (fun name -> not (List.contains name captured)))

    /// The closure block for the lifted function `liftedName` over the given captured names, standing in for
    /// `origin` (the lambda or function reference it replaces), whose id and span it keeps. (A marker call
    /// rather than a tuple, so that `f(closure)` is not flattened into one argument per field.)
    let closureOf (origin: Expression) (liftedName: string) (captured: string list) =
        { origin with
            Kind =
                FunctionCall("__closure", None,
                    Expression.synthetic (FunctionCall("__slot", None, [ Expression.synthetic (IdentifierExpr liftedName) ]))
                    :: (captured |> List.map (fun name -> Expression.synthetic (IdentifierExpr name)))) }

    /// The lifted function's lambda: the environment parameter first, then the captured names bound
    /// from the environment's fields ahead of the original body, and the members of the `use`s in scope loaded
    /// again from their captured locals. It is reported where `lambda` was written.
    let liftedLambda (lambda: Expression) (args: (string * TypeExpr option) list) (captured: string list) (body: Expression) =
        let passed = passedMembers lambda
        let useBinding =
            useMembers
            |> List.filter (fun (local, _) -> List.contains local captured)
            |> List.collect (fun (local, names) ->
                names |> List.filter (passed.Contains >> not) |> List.map (fun name -> NamedField(name, Expression.synthetic (IdentifierExpr local))))
            |> function
                | [] -> []
                | fields -> [ Statement.synthetic (UseStatement(Expression.synthetic (RecordExpr fields))) ]
        let captureBindings =
            (captured
             |> List.mapi (fun index name ->
                 Statement.synthetic (DefStatement(false, name, None, Expression.synthetic (MemberAccess(Expression.synthetic (IdentifierExpr envParameter), string (index + 1), None))))))
            @ useBinding
        let bodyWithCaptures =
            match captureBindings, body.Kind with
            | [], _ -> body
            | _, Block statements -> { body with Kind = Block (captureBindings @ statements) }
            | _, _ -> Expression.from body (Block (captureBindings @ [ Statement.from body (ExprStatement body) ]))
        Expression.from lambda (Lambda((envParameter, None) :: args, bodyWithCaptures, false))

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
        | FunctionCall(name, range, args) -> rebuild (FunctionCall(name, range, args |> List.map recur))
        | Pipe(value, name, range, args) -> rebuild (Pipe(recur value, name, range, args |> List.map recur))
        | BinaryOp(op, left, right) -> rebuild (BinaryOp(op, recur left, recur right))
        | IfExpr(cond, thenExpr, elseExpr) -> rebuild (IfExpr(recur cond, recur thenExpr, recur elseExpr))
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
                | DefStatement(isExport, name, _, Kind (FunctionCall("__rec", _, [ Kind (Lambda(args, body, _)) as lambda ]))) when not args.IsEmpty ->
                    let liftedName = $"__rec_{sanitizeIdentifier name}_{counter}"
                    counter <- counter + 1
                    let parameterNames = args |> List.map fst |> Set.ofList
                    let innerScope = Set.union parameterNames (boundNames body |> Set.ofList) |> Set.add name
                    let liftedBody = liftExpr (Set.union scope innerScope) body |> redirectSelfReferences name liftedName
                    let captured =
                        Set.difference (references liftedBody |> List.map fst |> Set.ofList) (innerScope |> Set.add liftedName |> Set.add envParameter)
                        |> Set.intersect scope
                        |> Set.toList
                        |> withMembers (passedMembers lambda) liftedBody
                    lifted.Add(TopLevelItem.synthetic (Def (ValueDef(false, liftedName, None, liftedLambda lambda args captured liftedBody))))
                    origins.[liftedName] <- { Owner = owner; LocalName = Some name; Captured = captured; Source = None; Lambda = lambda }
                    [ keep (DefStatement(isExport, name, None, closureOf lambda liftedName captured)) ]
                | DefStatement(isExport, name, typeOpt, Kind (FunctionCall("__rec", _, [ rhs ]))) ->
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
                                let local = $"__use_{sanitizeIdentifier key}_{counter}"
                                counter <- counter + 1
                                memberName, key, local, liftMember contextType memberName value)
                        // Each member by its key, and by its own name too, as the block can call it by that (`a == b`
                        // after `use ignoringCase` calls its `==`).
                        let names = locals |> List.map (fun (memberName, key, local, _) -> local, [ key; memberName ] |> List.distinct)
                        let fields =
                            names
                            |> List.collect (fun (local, names) -> names |> List.map (fun name -> NamedField(name, Expression.synthetic (IdentifierExpr local))))
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
        | TupleExpr items -> rebuild (TupleExpr(items |> List.map recur))
        | ListExpr items -> rebuild (ListExpr(items |> List.map recur))
        | RecordExpr fields ->
            fields
            |> List.map (function
                | NamedField(name, value) -> NamedField(name, recur value)
                | PositionalField value -> PositionalField(recur value))
            |> RecordExpr
            |> rebuild
        | TagExpr(name, payload) -> rebuild (TagExpr(name, payload |> Option.map recur))
        // An attached function used as a value, `int.toString`: the parser reads it as a member access.
        | MemberAccess(Kind (IdentifierExpr qualifier), field, _) when
            topLevelArities.ContainsKey $"{qualifier}.{field}" && not (scope.Contains qualifier) ->
            closureOf expr (wrapperOf $"{qualifier}.{field}") []
        | MemberAccess(inner, field, range) -> rebuild (MemberAccess(recur inner, field, range))
        // `use value in body` is the block `{ use value; body }`: what it loads is in scope for `body` only.
        | UseIn(value, body) ->
            recur { expr with Kind = Block [ Statement.from value (UseStatement value); Statement.from body (ExprStatement body) ] }
        | WorkflowBindExpr(name, value) -> rebuild (WorkflowBindExpr(name, recur value))
        | WorkflowReturnExpr value -> rebuild (WorkflowReturnExpr(recur value))
        | InterpolatedString parts ->
            parts
            |> List.map (function
                | StringExpr value -> StringExpr(recur value)
                | text -> text)
            |> InterpolatedString
            |> rebuild
        | ContextMemberCall(ctx, name, args) -> rebuild (ContextMemberCall(ctx, name, args |> List.map recur))
        | ContextMemberAccess _
        | UnitExpr
        | LiteralExpr _
        | IdentifierExpr _ -> expr

    /// Lifts `{ args -> body }` to a fresh top-level function and returns its closure.
    and liftLambda (scope: Set<string>) (lambda: Expression) (args: (string * TypeExpr option) list) (body: Expression) : Expression =
        let parameterNames = args |> List.map fst |> Set.ofList
        let innerScope = Set.union parameterNames (boundNames body |> Set.ofList)
        // A lambda nested inside may capture what this one binds and what encloses this one alike;
        // what it captures from outside is then referenced here, so this lambda captures it in turn.
        let liftedBody = liftExpr (Set.union scope innerScope) body
        let captured =
            Set.difference (references liftedBody |> List.map fst |> Set.ofList) innerScope
            |> Set.intersect scope
            |> Set.toList
            |> withMembers (passedMembers lambda) liftedBody
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
            ownerMembers <- memberCaptures |> Map.tryFind name |> Option.defaultValue []
            let scope = Set.union (args |> List.map fst |> Set.ofList) (boundNames body |> Set.ofList) |> Set.union (Set.ofList ownerMembers)
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
        ambientMemberDefinitions module'
        |> List.map snd
        |> List.filter (fun name -> topLevelFunctions.ContainsKey name && not (takesContexts defTypes.[name]))
        |> List.iter (wrapperOf >> ignore)
    synthesized |> Set.filter topLevelArities.ContainsKey |> Set.iter (wrapperOf >> ignore)

    items @ List.ofSeq lifted,
    origins |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq,
    wrappers |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
