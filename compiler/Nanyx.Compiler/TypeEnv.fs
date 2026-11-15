namespace NyxCompiler

/// A type scheme with generalized variables, and the declaration the name it is bound to refers to (none for a
/// built-in, or a name the compiler made up). `ProvidedBy` is, for a name a `use` or a def's signature binds as a
/// context's member, which of them binds it (see `TypedModule.ContextProviders`).
type TypeScheme =
    { Quantified: Set<int>
      Type: Ty
      Symbol: SymbolId option
      ProvidedBy: ContextProvider option }

type TypeEnv = Map<string, TypeScheme>

module TypeEnv =
    let empty : TypeEnv = Map.empty

    let extend (name: string) (scheme: TypeScheme) (env: TypeEnv) =
        env |> Map.add name scheme

    /// Binds `name` to `scheme` as a reference to the declaration `symbol`.
    let declare (name: string) (symbol: SymbolId option) (scheme: TypeScheme) (env: TypeEnv) =
        env |> Map.add name { scheme with Symbol = symbol }

    let tryFind (name: string) (env: TypeEnv) =
        env |> Map.tryFind name

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
        env
        |> Map.values
        |> Seq.map freeSchemeVars
        |> Seq.fold Set.union Set.empty

    let generalize (env: TypeEnv) (ty: Ty) : TypeScheme =
        let envVars = freeEnvVars env
        let tyVars = freeTyVars ty
        { Quantified = Set.difference tyVars envVars
          Type = ty
          Symbol = None
          ProvidedBy = None }
