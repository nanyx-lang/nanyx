namespace NyxCompiler

/// A type scheme with generalized variables, and the declaration the name it is bound to refers to (none for a
/// built-in, or a name the compiler made up).
type TypeScheme =
    { Quantified: Set<int>
      Type: Ty
      Symbol: SymbolId option }

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
          Symbol = None }

    let rec freeTyVars (ty: Ty) : Set<int> =
        match ty with
        | TyVar v -> Set.singleton v.Id
        | TyPrimitive _ -> Set.empty
        | TyNominal(_, underlying, _) -> freeTyVars underlying
        | TyAlias(_, _, underlying) -> freeTyVars underlying
        | TyFunc(arg, ret) ->
            Set.union (freeTyVars arg) (freeTyVars ret)
        | TyRecord fields ->
            fields
            |> Map.values
            |> Seq.map freeTyVars
            |> Seq.fold Set.union Set.empty
        | TyOpenRecord(fields, row) -> freeTyVars (TyRecord fields) |> Set.add row.Id
        | TyTag(_, payloadOpt) ->
            payloadOpt |> Option.map freeTyVars |> Option.defaultValue Set.empty
        | TyApply(_, args) ->
            args
            |> List.map freeTyVars
            |> List.fold Set.union Set.empty
        | TyUnion items ->
            items
            |> List.map freeTyVars
            |> List.fold Set.union Set.empty

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
          Symbol = None }
