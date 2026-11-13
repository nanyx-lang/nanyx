namespace NyxCompiler

open Parser.Program

/// The contexts a function type requires where it is called (see `TyFunc`), and what makes two lists of them agree.
/// Two function types that must be the same require the same contexts. A function given where another is expected may
/// require fewer: a pure function fits where one requiring `Console` is expected. A context variable takes the contexts
/// the other list has and its own does not, as a union's rest takes the tags it does not name.
module FunctionContexts =
    /// What a scoped context's scope is wrapped in (see `scoped`). No name written in source can be it.
    let private scopeTag = "@scope"

    /// The scoped context `name` (`scoped context $Memory = ()`) of the scope `scope`, a type variable: the call that
    /// supplies the context starts the scope, and every requirement of it inside belongs to it. A reader never sees the
    /// scope (see `withoutScope`), and the backend leaves it out.
    let scoped (name: string) (scope: Ty) : Ty = TyApply(name, [ TyApply(scopeTag, [ scope ]) ])

    /// A scoped context's name and its scope (see `scoped`).
    let (|ScopedContext|_|) (context: Ty) : (string * Ty) option =
        match context with
        | TyApply(name, [ TyApply(tag, [ scope ]) ]) when tag = scopeTag -> Some (name, scope)
        | _ -> None

    /// The name of a context that takes no arguments a reader writes: `Console`, or a scoped context, whose scope is not
    /// one.
    let (|PlainContext|_|) (context: Ty) : string option =
        match context with
        | TyApply(name, []) -> Some name
        | ScopedContext(name, _) -> Some name
        | _ -> None

    /// `context` as a reader writes it: a scoped context without its scope.
    let withoutScope (context: Ty) : Ty =
        match context with
        | ScopedContext(name, _) -> TyApply(name, [])
        | _ -> context

    /// A context's name and its arguments, or `None` for a context variable.
    let nameOf (context: Ty) : (string * Ty list) option =
        match context with
        | TyApply(name, args)
        | TyAlias(name, args, _) -> Some (name, args)
        | _ -> None

    /// Whether `context` is a context variable.
    let isVariable (context: Ty) =
        match context with
        | TyVar _ -> true
        | _ -> false

    /// The context variables of the function types in `ty`, by id, each with its name if it has one.
    let rec variablesIn (ty: Ty) : Map<int, string option> =
        let all (tys: Ty list) = tys |> List.fold (fun acc ty -> Map.fold (fun acc id name -> Map.add id name acc) acc (variablesIn ty)) Map.empty
        match ty with
        | TyFunc(input, output, contexts) ->
            let own = contexts |> List.choose (function TyVar v -> Some (v.Id, v.Name) | _ -> None) |> Map.ofList
            Map.fold (fun acc id name -> Map.add id name acc) own (all (input :: output :: contexts))
        | TyVar _
        | TyPrimitive _ -> Map.empty
        | TyNominal(_, underlying, _) -> variablesIn underlying
        | TyAlias(_, args, underlying) -> all (underlying :: args)
        | TyRecord fields
        | TyOpenRecord(fields, _) -> all (List.ofSeq fields.Values)
        | TyTag(_, payload) -> payload |> Option.map variablesIn |> Option.defaultValue Map.empty
        | TyApply(_, items)
        | TyUnion items -> all items

    /// `contexts` once a substitution is applied to them: a variable solved to a union of contexts is those contexts,
    /// and each context is there once.
    let flatten (contexts: Ty list) : Ty list =
        contexts
        |> List.collect (function
            | TyUnion items -> items
            | context -> [ context ])
        |> List.distinct

    /// The contexts of `left` and `right` paired by name and number of arguments, those of each that have no partner,
    /// and the variables of each that the other does not have too. A context both have exactly is paired first, so
    /// `Show(a)` meets `Show(a)` before it can meet `Show(b)`.
    let private pairUp (left: Ty list) (right: Ty list) =
        let leftVars, leftContexts = left |> List.partition isVariable
        let rightVars, rightContexts = right |> List.partition isVariable
        let key context = nameOf context |> Option.map (fun (name, args) -> name, args.Length)
        let rec go matches (contexts: Ty list) (remaining: Ty list) pairs unmatched =
            match contexts with
            | [] -> List.rev pairs, List.rev unmatched, remaining
            | context :: rest ->
                match remaining |> List.tryFindIndex (matches context) with
                | Some index -> go matches rest (List.removeAt index remaining) ((context, remaining.[index]) :: pairs) unmatched
                | None -> go matches rest remaining pairs (context :: unmatched)
        let same, leftOver, rightOver = go (=) leftContexts rightContexts [] []
        let paired, leftExtra, rightExtra = go (fun l r -> key l = key r) leftOver rightOver [] []
        let leftOnly = leftVars |> List.filter (fun v -> not (List.contains v rightVars))
        let rightOnly = rightVars |> List.filter (fun v -> not (List.contains v leftVars))
        let shared = leftVars |> List.filter (fun v -> List.contains v rightVars)
        same @ paired, leftExtra, rightExtra, leftOnly, rightOnly, shared

    /// The constraints that make the arguments of each pair of contexts the same.
    let private argumentConstraints (origin: SourceSpan) (pairs: (Ty * Ty) list) : Constraint list =
        pairs
        |> List.collect (fun (left, right) ->
            match nameOf left, nameOf right with
            | Some (_, leftArgs), Some (_, rightArgs) -> List.map2 (fun l r -> (l, r, Equal, origin)) leftArgs rightArgs
            | _ -> [])

    /// The constraints that solve the variables `vars` to `contexts`: the first takes them all, and any other none.
    let private solveTo (origin: SourceSpan) (vars: Ty list) (contexts: Ty list) : Constraint list =
        vars |> List.mapi (fun index var -> (var, TyUnion (if index = 0 then contexts else []), Equal, origin))

    /// The constraints that make `left` and `right` the same contexts, with `fresh` making a variable for the contexts
    /// two variables share; or, when they cannot be, the contexts of each that the other has no room for.
    let agree (fresh: unit -> Ty) (origin: SourceSpan) (left: Ty list) (right: Ty list) : Result<Constraint list, Ty list * Ty list> =
        let pairs, leftExtra, rightExtra, leftVars, rightVars, shared = pairUp left right
        let arguments = argumentConstraints origin pairs
        match leftVars, rightVars with
        // A variable both have, as a recursive function's own contexts are, can take what either has besides.
        | _ when not shared.IsEmpty && not (leftExtra.IsEmpty && rightExtra.IsEmpty) ->
            Ok (arguments @ solveTo origin (List.truncate 1 shared) (leftExtra @ rightExtra @ leftVars @ rightVars @ [ fresh () ]))
        | [], [] when leftExtra.IsEmpty && rightExtra.IsEmpty -> Ok arguments
        | [], [] -> Error (leftExtra, rightExtra)
        | [], _ when rightExtra.IsEmpty -> Ok (arguments @ solveTo origin rightVars leftExtra)
        | _, [] when leftExtra.IsEmpty -> Ok (arguments @ solveTo origin leftVars rightExtra)
        | [], _ -> Error ([], rightExtra)
        | _, [] -> Error (leftExtra, [])
        | _ ->
            let shared = fresh ()
            Ok (arguments @ solveTo origin leftVars (rightExtra @ [ shared ]) @ solveTo origin rightVars (leftExtra @ [ shared ]))

    /// The constraints that let a function requiring `given` be given where one requiring `expected` is expected: each
    /// context it requires is one the expected type has, or goes into its variable. A variable of `given` with no room
    /// in a variable of `expected` takes the contexts `expected` has that `given` does not. When it cannot fit, the
    /// contexts `given` requires that `expected` has no room for.
    let fit (fresh: unit -> Ty) (origin: SourceSpan) (given: Ty list) (expected: Ty list) : Result<Constraint list, Ty list> =
        let pairs, givenExtra, expectedExtra, givenVars, expectedVars, shared = pairUp given expected
        let arguments = argumentConstraints origin pairs
        match expectedVars with
        | [] when not givenExtra.IsEmpty && not shared.IsEmpty ->
            Ok (arguments @ solveTo origin (List.truncate 1 shared) (givenExtra @ [ fresh () ]) @ solveTo origin givenVars expectedExtra)
        | [] when not givenExtra.IsEmpty -> Error givenExtra
        | [] -> Ok (arguments @ solveTo origin givenVars expectedExtra)
        | _ ->
            // A variable of `given` takes the scoped contexts `expected` has besides: a block given to `memory` that
            // calls a function it is not told the type of, as `{ mk -> memory { mk() } }` does, gives it the block's
            // `$Memory`. The variable of `expected` takes only what it has besides, so the call that supplies the
            // context does not require it too.
            let scoped = expectedExtra |> List.filter (function ScopedContext _ -> true | _ -> false)
            match givenVars with
            | _ :: _ when not scoped.IsEmpty ->
                let rest = fresh ()
                Ok (arguments @ solveTo origin givenVars (scoped @ [ rest ]) @ solveTo origin expectedVars (givenExtra @ [ rest; fresh () ]))
            // The variable keeps room for more, so that a function given for it elsewhere can require others.
            | _ -> Ok (arguments @ solveTo origin expectedVars (givenExtra @ givenVars @ [ fresh () ]))
