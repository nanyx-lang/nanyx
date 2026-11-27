namespace NyxCompiler

/// A type scheme with generalized variables, and the declaration the name it is bound to refers to (none for a
/// built-in, or a name the compiler made up). `ProvidedBy` is, for a name a `use` or a def's signature binds as a
/// context's member, which of them binds it (see `TypedModule.ContextProviders`).
type TypeScheme =
    { Quantified: Set<int>
      Type: Ty
      Symbol: SymbolId option
      ProvidedBy: ContextProvider option }

/// An instance of a parameterised context in scope, put there by a `use` or a signature (see `ContextInstances`): the
/// context applied to its arguments (`Sum(Point)` is `TyApply("Sum", [Point])`), as written where it is put in scope,
/// and what puts it there.
type InstanceBinding =
    { Context: Ty
      Written: Parser.Program.TypeExpr
      Provider: ContextProvider }

/// The names in scope, each with its type, and the instances of parameterised contexts in scope, nearest first.
/// `Unquantified` holds the free type variables of each name whose scheme has any, by name: few of them do (a
/// parameter, a def not yet generalised), so `freeEnvVars` looks at those, not at every name in scope (#1266).
/// `UnquantifiedVars` holds each of those variables with how many names have it, so that what is in scope is looked
/// at once for each variable, however many locals of one type a block holds (#2407).
/// `BlockScope` is the scope of the `memory` block the code is in, whose cells a `mut` there makes (see `MemoryCells`),
/// and `BlockDepth` how many lambdas deep the block's own lambda is, while that scope is the block's, not one a `use`
/// loads.
/// `Supplied` holds the contexts with no members that are supplied here, which no member's name says, each with what
/// supplies it (see `Contexts.withSupplied`).
type TypeEnv =
    { Names: Map<string, TypeScheme>
      Instances: InstanceBinding list
      Unquantified: Map<string, Set<int>>
      UnquantifiedVars: Map<int, int>
      BlockScope: Ty option
      BlockDepth: int option
      Supplied: Map<NominalId, ContextProvider> }

module TypeEnv =
    let empty : TypeEnv = { Names = Map.empty; Instances = []; Unquantified = Map.empty; UnquantifiedVars = Map.empty; BlockScope = None; BlockDepth = None; Supplied = Map.empty }

    let rec freeTyVars (ty: Ty) : Set<int> =
        match ty with
        | TyVar v -> Set.singleton v.Id
        | TyAlias(_, _, underlying) -> freeTyVars underlying
        | _ -> Ty.children ty |> List.map freeTyVars |> Set.unionMany

    let freeSchemeVars (scheme: TypeScheme) : Set<int> =
        Set.difference (freeTyVars scheme.Type) scheme.Quantified

    /// `counts`, which says how many names have each variable, with `vars` counted once more, or once less (`by` -1).
    let private count (by: int) (vars: Set<int>) (counts: Map<int, int>) =
        vars
        |> Set.fold (fun counts id ->
            match (counts |> Map.tryFind id |> Option.defaultValue 0) + by with
            | 0 -> counts |> Map.remove id
            | n -> counts |> Map.add id n) counts

    /// `env` without `name`'s free variables in `Unquantified`.
    let private uncount (name: string) (env: TypeEnv) =
        match env.Unquantified |> Map.tryFind name with
        | Some vars -> { env with Unquantified = env.Unquantified |> Map.remove name; UnquantifiedVars = count -1 vars env.UnquantifiedVars }
        | None -> env

    let extend (name: string) (scheme: TypeScheme) (env: TypeEnv) =
        let free = freeSchemeVars scheme
        let env = uncount name env
        { env with
            Names = env.Names |> Map.add name scheme
            Unquantified = if free.IsEmpty then env.Unquantified else env.Unquantified |> Map.add name free
            UnquantifiedVars = count 1 free env.UnquantifiedVars }

    /// Binds `name` to `scheme` as a reference to the declaration `symbol`.
    let declare (name: string) (symbol: SymbolId option) (scheme: TypeScheme) (env: TypeEnv) =
        extend name { scheme with Symbol = symbol } env

    let tryFind (name: string) (env: TypeEnv) =
        env.Names |> Map.tryFind name

    /// `env` without `name` in scope.
    let remove (name: string) (env: TypeEnv) =
        { uncount name env with Names = env.Names |> Map.remove name }

    /// `env` with `instance` in scope, nearer than those already in it.
    let withInstance (instance: InstanceBinding) (env: TypeEnv) =
        { env with Instances = instance :: env.Instances }

    let mono (ty: Ty) : TypeScheme =
        { Quantified = Set.empty
          Type = ty
          Symbol = None
          ProvidedBy = None }

    let freeEnvVars (env: TypeEnv) : Set<int> =
        env.UnquantifiedVars
        |> Map.keys
        |> Set.ofSeq
        |> Seq.singleton
        |> Seq.append (env.Instances |> Seq.map (_.Context >> freeTyVars))
        |> Seq.append (env.BlockScope |> Option.map freeTyVars |> Option.toList)
        |> Seq.fold Set.union Set.empty

    let generalize (env: TypeEnv) (ty: Ty) : TypeScheme =
        let envVars = freeEnvVars env
        let tyVars = freeTyVars ty
        { Quantified = Set.difference tyVars envVars
          Type = ty
          Symbol = None
          ProvidedBy = None }
