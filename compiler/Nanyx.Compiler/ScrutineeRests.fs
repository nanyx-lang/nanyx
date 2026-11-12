namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The rest variables of the unions a match takes apart, and what they ask of its arms. A union whose rest can
/// still stand for tags needs a catch-all arm for them; one whose rest nothing can add a tag to has only the tags
/// written, and is checked against those.
module internal ScrutineeRests =
    let private hasRest (ty: Ty) =
        match ty with
        | TyUnion items -> items |> List.exists (function TyVar _ -> true | _ -> false)
        | _ -> false

    /// The types of a match's scrutinees as far as the constraints gathered so far determine them, for the
    /// exhaustiveness check. A scrutinee's type may still be a variable (a call's result, a local bound to one), so
    /// it is resolved, and before the arms add their constraints: a lone `#some(_)` arm would otherwise narrow the
    /// variable to that tag first and the check would pass silently (#61).
    ///
    /// A rest that nothing can add a tag to is left out, as it is from the type of a def bound to the same value
    /// (`generalizeDef`): one in no function's input, not named in an annotation, and not shared with `env`. So the
    /// room a call leaves when it widens a union, as `find(1) \bindSome(parseDigit)` does to give
    /// `#some(int) | #notFound | #invalidInt | r`, asks for no catch-all arm (#183).
    let resolve (env: TypeEnv) (state: InferState) (scrutineeTypes: Ty list) : Ty list =
        let subst = fst (Unifier.solve state.Constraints)
        let resolved = scrutineeTypes |> List.map (Unifier.apply subst >> Unifier.unalias)
        if not (resolved |> List.exists hasRest) then
            resolved
        else
            let envVars =
                TypeEnv.freeEnvVars env
                |> Seq.map (fun id -> TypeEnv.freeTyVars (Unifier.apply subst (TyVar { Id = id; Name = None })))
                |> Set.unionMany
            resolved
            |> List.map (fun ty ->
                let closing =
                    Unifier.outputOnlyRestVars ty
                    |> List.filter (fun v -> v.Name.IsNone && not (envVars.Contains v.Id))
                    |> List.map (fun v -> v.Id, TyUnion [])
                    |> Map.ofList
                if closing.IsEmpty then ty else Unifier.apply closing ty |> Unifier.unalias)

    /// The error for a match with no catch-all arm on a union that can still have tags it does not name, given the
    /// scrutinees' types from `resolve`, reported at `headSpan`.
    let missingCatchAll (resolved: Ty list) (arms: MatchArm list) (headSpan: SourceSpan) : Diagnostic option =
        let hasCatchAll =
            arms
            |> List.exists (fun (patterns, _) ->
                patterns
                |> List.exists (function
                    | Kind (WildcardPattern | ElsePattern | IdentifierPattern _) -> true
                    | _ -> false))
        if resolved |> List.exists hasRest && not hasCatchAll then
            Some (Diagnostics.at DiagnosticCodes.MissingCatchAllArm headSpan "Tag union rest parameters require a catch-all match arm")
        else
            None
