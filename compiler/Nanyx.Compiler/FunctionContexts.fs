namespace NyxCompiler

open Parser.Program

/// The contexts a function type requires where it is called (see `TyFunc`), and what makes two lists of them agree.
/// Two function types that must be the same require the same contexts. A function given where another is expected may
/// require fewer: a pure function fits where one requiring `Console` is expected. A context variable takes the contexts
/// the other list has and its own does not, as a union's rest takes the tags it does not name.
module FunctionContexts =
    /// The scope `scope`, a type variable, as a scoped context holds it: the argument of the context, and of a cell's
    /// type (see `cellType`).
    let scopeType (scope: Ty) : Ty = TyScope scope

    /// The scope a scope type holds (see `scopeType`).
    let (|ScopeType|_|) (ty: Ty) : Ty option =
        match ty with
        | TyScope scope -> Some scope
        | _ -> None

    /// The context a cell's type is of (see `cellType`): `Memory`, as the prelude declares it.
    let cellContext = "Memory"

    /// The type of a cell that holds `held`, of the `memory` block of the scope `scope` (see `CellPassingSyntax`): a
    /// reader sees `mut T`, without the scope.
    let cellType (held: Ty) (scope: Ty) : Ty = TyApply(CellPassingSyntax.typeName, [ held; scopeType scope ])

    /// What a cell's type holds, and its scope (see `cellType`).
    let (|CellType|_|) (ty: Ty) : (Ty * Ty) option =
        match ty with
        | TyApply(name, [ held; ScopeType scope ]) when name = CellPassingSyntax.typeName -> Some (held, scope)
        | _ -> None

    /// Whether `scope` is the scope a call starts, rather than one that may become any (see `TyVarKind.CallScope`).
    let isCallScope (scope: Ty) =
        match scope with
        | TyVar { Kind = CallScope } -> true
        | _ -> false

    /// Whether `left` and `right` are scoped contexts of two scopes that calls start, which are never the same.
    let private ofTwoCalls (left: Ty) (right: Ty) =
        match left, right with
        | TyApply(_, [ ScopeType l ]), TyApply(_, [ ScopeType r ]) -> isCallScope l && isCallScope r && l <> r
        | _ -> false

    /// The scoped context `name` (`scoped context Memory = ()`) of the scope `scope`, a type variable: the call that
    /// supplies the context starts the scope, and every requirement of it inside belongs to it. A reader never sees the
    /// scope (see `withoutScope`), and the backend leaves it out.
    let scoped (name: string) (scope: Ty) : Ty = TyApply(name, [ scopeType scope ])

    /// A scoped context's name and its scope (see `scoped`).
    let (|ScopedContext|_|) (context: Ty) : (string * Ty) option =
        match context with
        | TyApply(name, [ ScopeType scope ]) -> Some (name, scope)
        | _ -> None

    /// `contexts` as a type generic in contexts carries them (`type Stream(a) = [k] protected (...)`, see
    /// `TypeDefInfo.ContextVariable`): the last argument of its nominal type, a `TyContexts`. Two such types' contexts
    /// agree as two function types' do, and one with fewer fits where one with more is expected. A reader writes them in
    /// front of the type, `[FileSystem] Stream(string)`.
    let carried (contexts: Ty list) : Ty = TyContexts contexts

    /// The contexts a type generic in contexts carries (see `carried`).
    let (|Carried|_|) (ty: Ty) : Ty list option =
        match ty with
        | TyContexts contexts -> Some contexts
        | _ -> None

    /// The arguments of a type generic in contexts (see `carried`): those of its own, and the contexts it carries, which
    /// are its last.
    let (|CarryingArguments|_|) (args: Ty list) : (Ty list * Ty list) option =
        match List.tryLast args with
        | Some (Carried contexts) -> Some (List.truncate (args.Length - 1) args, contexts)
        | _ -> None

    /// A type generic in contexts (see `carried`): its nominal type, the arguments of its own, and the contexts it carries.
    let (|CarryingNominal|_|) (ty: Ty) : (NominalId * Ty list * Ty list) option =
        match ty with
        | TyNominal(nominal, CarryingArguments(own, contexts), _, _) -> Some (nominal, own, contexts)
        | _ -> None

    /// The name of a context that takes no arguments a reader writes: `Console`, or a scoped context, whose scope is not
    /// one.
    let (|PlainContext|_|) (context: Ty) : string option =
        match context with
        | TyApply(name, []) -> Some name
        | ScopedContext(name, _) -> Some name
        | _ -> None

    /// `contexts`, those of a function type whose input is `input`, with `Memory` of the scope of each cell the function
    /// takes: a function that takes a cell requires it, whether its type says so or not, since `Memory` is nominal and
    /// only the cell's block has it. `mut int -> ()` is `[Memory] mut int -> ()`.
    let withCellMemory (input: Ty) (contexts: Ty list) : Ty list =
        let parameters =
            match input with
            | TyRecord(fields, _) -> List.ofSeq fields.Values
            | single -> [ single ]
        let memory = parameters |> List.choose (function CellType(_, scope) -> Some (scoped cellContext scope) | _ -> None)
        if memory.IsEmpty then contexts else contexts @ memory |> List.distinct

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

    /// The context variables of the function types in `ty`, and of the contexts its types generic in contexts carry, by
    /// id, each with its name if it has one.
    let rec variablesIn (ty: Ty) : Map<int, string option> =
        let all (tys: Ty list) = tys |> List.fold (fun acc ty -> Map.fold (fun acc id name -> Map.add id name acc) acc (variablesIn ty)) Map.empty
        let ownOf (contexts: Ty list) = contexts |> List.choose (function TyVar v -> Some (v.Id, v.Name) | _ -> None) |> Map.ofList
        match ty with
        | TyFunc(input, output, contexts) ->
            Map.fold (fun acc id name -> Map.add id name acc) (ownOf contexts) (all (input :: output :: contexts))
        | TyContexts contexts -> Map.fold (fun acc id name -> Map.add id name acc) (ownOf contexts) (all contexts)
        | TyOpenRecord(fields, _) -> all (List.ofSeq fields.Values)
        | _ -> all (Ty.children ty)

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
        // An outer block's `Memory` is not an inner block's: it goes into a variable instead.
        let fits l r = key l = key r && not (ofTwoCalls l r)
        let paired, leftExtra, rightExtra = go fits leftOver rightOver [] []
        // Any other scoped context of a name already paired is of the same scope: two calls in one block that require
        // `Memory` require the block's, not one each.
        let pairedScoped = same @ paired |> List.filter (fst >> function ScopedContext _ -> true | _ -> false)
        let absorb (partnerOf: Ty * Ty -> Ty) (extra: Ty list) =
            extra
            |> List.fold (fun (pairs, unmatched) context ->
                match context with
                | ScopedContext _ ->
                    match pairedScoped |> List.map partnerOf |> List.tryFind (fits context) with
                    | Some partner -> (context, partner) :: pairs, unmatched
                    | None -> pairs, unmatched @ [ context ]
                | _ -> pairs, unmatched @ [ context ]) ([], [])
        let leftAbsorbed, leftExtra = absorb snd leftExtra
        let rightAbsorbed, rightExtra = absorb fst rightExtra
        let paired = paired @ List.rev leftAbsorbed @ (List.rev rightAbsorbed |> List.map (fun (r, l) -> l, r))
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
        // Each variable of `given` may stand for any of the contexts `expected` has, as `c` and `d` of `seq.map`'s
        // `[c, d] seq(b)` may where a `[Console] seq(int)` is expected: the sequence or the function may be the one that
        // requires `Console` (#1738).
        | [] -> Ok (arguments @ (givenVars |> List.map (fun var -> (var, TyUnion expectedExtra, Equal, origin))))
        | _ ->
            // A variable of `given` takes the scoped contexts `expected` has besides: a block given to `memory` that
            // calls a function it is not told the type of, as `{ mk -> memory { mk() } }` does, gives it the block's
            // `Memory`. The variable of `expected` takes only what it has besides, so the call that supplies the
            // context does not require it too.
            let scoped = expectedExtra |> List.filter (function ScopedContext _ -> true | _ -> false)
            match givenVars with
            | _ :: _ when not scoped.IsEmpty ->
                let rest = fresh ()
                Ok (arguments @ solveTo origin givenVars (scoped @ [ rest ]) @ solveTo origin expectedVars (givenExtra @ [ rest; fresh () ]))
            // A function that requires nothing leaves alone a variable the type of the def it is in writes, which
            // would otherwise be solved to a made-up one, as where a constructor's field has taken it (#1701).
            | [] when givenExtra.IsEmpty && expectedVars |> List.exists (function TyVar v -> v.Name.IsSome | _ -> false) ->
                match expectedVars |> List.filter (function TyVar v -> v.Name.IsNone | _ -> true) with
                | [] -> Ok arguments
                | unwritten -> Ok (arguments @ solveTo origin unwritten [ fresh () ])
            // A variable made up to keep room for more takes what is given, and the variables the type of the def
            // writes are left alone: a `[d] seq(b)` given where `[c, e] seq(b)` is expected, `e` made up beside the
            // signature's `c`, goes into `e` (#1908).
            | _ when expectedVars |> List.exists (function TyVar v -> v.Name.IsSome | _ -> false)
                     && expectedVars |> List.exists (function TyVar v -> v.Name.IsNone | _ -> false) ->
                let unwritten = expectedVars |> List.filter (function TyVar v -> v.Name.IsNone | _ -> true)
                Ok (arguments @ solveTo origin unwritten (givenExtra @ givenVars @ [ fresh () ]))
            // The variable keeps room for more, so that a function given for it elsewhere can require others.
            | _ -> Ok (arguments @ solveTo origin expectedVars (givenExtra @ givenVars @ [ fresh () ]))
