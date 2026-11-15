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
type TypeEnv =
    { Names: Map<string, TypeScheme>
      Instances: InstanceBinding list }

module TypeEnv =
    let empty : TypeEnv = { Names = Map.empty; Instances = [] }

    let extend (name: string) (scheme: TypeScheme) (env: TypeEnv) =
        { env with Names = env.Names |> Map.add name scheme }

    /// Binds `name` to `scheme` as a reference to the declaration `symbol`.
    let declare (name: string) (symbol: SymbolId option) (scheme: TypeScheme) (env: TypeEnv) =
        extend name { scheme with Symbol = symbol } env

    let tryFind (name: string) (env: TypeEnv) =
        env.Names |> Map.tryFind name

    /// `env` without `name` in scope.
    let remove (name: string) (env: TypeEnv) =
        { env with Names = env.Names |> Map.remove name }

    /// `env` with `instance` in scope, nearer than those already in it.
    let withInstance (instance: InstanceBinding) (env: TypeEnv) =
        { env with Instances = instance :: env.Instances }

    let mono (ty: Ty) : TypeScheme =
        { Quantified = Set.empty
          Type = ty
          Symbol = None
          ProvidedBy = None }

    let rec freeTyVars (ty: Ty) : Set<int> =
        match ty with
        | TyVar v -> Set.singleton v.Id
        | TyAlias(_, _, underlying) -> freeTyVars underlying
        | _ -> Ty.children ty |> List.map freeTyVars |> Set.unionMany

    let freeSchemeVars (scheme: TypeScheme) : Set<int> =
        Set.difference (freeTyVars scheme.Type) scheme.Quantified

    let freeEnvVars (env: TypeEnv) : Set<int> =
        env.Names
        |> Map.values
        |> Seq.map freeSchemeVars
        |> Seq.append (env.Instances |> Seq.map (_.Context >> freeTyVars))
        |> Seq.fold Set.union Set.empty

    let generalize (env: TypeEnv) (ty: Ty) : TypeScheme =
        let envVars = freeEnvVars env
        let tyVars = freeTyVars ty
        { Quantified = Set.difference tyVars envVars
          Type = ty
          Symbol = None
          ProvidedBy = None }
