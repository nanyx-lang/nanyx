namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The context an operator on a value of a type variable is read through (see "Operator contexts" in the docs' work
/// in progress). The compiler keeps no table of them: an operator reads a member, `+` the member `` `+` `` and prefix
/// `-` the member `negate`, and its context is the prelude's context with the fewest members that has that member.
/// So `+` is read through `Add`, though `Num` has a `` `+` `` too, and `*` through `Num`.
///
/// Several operators on values of one type need the contexts of each, less any whose members another of them has:
/// `+` and `*` need `Num` alone, and `+` and `==` need `Add` and `Eq`.
///
/// Only the prelude's contexts are looked at, so the prelude must leave no member with two smallest contexts (see
/// `ambiguities`).
module internal OperatorContexts =
    /// A context's name and the names of its members.
    type Declared = Identifier * Set<Identifier>

    /// The prelude's contexts of one parameter, each with its members.
    let declared (state: InferState) : Declared list =
        state.PreludeContexts
        |> Set.toList
        |> List.choose (fun name ->
            match state.TypeDefs |> Map.tryFind name with
            | Some info when info.IsContext && info.Parameters.Length = 1 ->
                match Unifier.unalias info.Underlying with
                | TyRecord fields -> Some (name, Set.ofSeq fields.Keys)
                | _ -> None
            | _ -> None)

    /// The names of the members of the context `context`, the prelude's or not; none if it is not a context.
    let membersOf (state: InferState) (context: Identifier) : Set<Identifier> =
        match state.TypeDefs |> Map.tryFind context with
        | Some info when info.IsContext ->
            match Unifier.unalias info.Underlying with
            | TyRecord fields -> Set.ofSeq fields.Keys
            | _ -> Set.empty
        | _ -> Set.empty

    /// Those of `contexts` with the fewest members among the ones that have the member `memberName`: one, unless
    /// none has it or the prelude is ambiguous.
    let private smallestOf (contexts: Declared list) (memberName: Identifier) : Identifier list =
        match contexts |> List.filter (fun (_, members) -> members.Contains memberName) with
        | [] -> []
        | having ->
            let fewest = having |> List.map (snd >> Set.count) |> List.min
            having |> List.filter (fun (_, members) -> members.Count = fewest) |> List.map fst

    /// The prelude's context the member `memberName` is read through, if exactly one is the smallest that has it.
    let smallest (state: InferState) (memberName: Identifier) : Identifier option =
        match smallestOf (declared state) memberName with
        | [ context ] -> Some context
        | _ -> None

    /// The prelude's contexts that reading `members` of one type needs: the smallest for each, less any whose members
    /// are all another's. `None` when one of `members` has no smallest context.
    let covering (state: InferState) (members: Identifier list) : Identifier list option =
        let contexts = declared state
        let membersOf (context: Identifier) = contexts |> List.find (fun (name, _) -> name = context) |> snd
        let chosen = members |> List.map (smallest state)
        if chosen |> List.forall Option.isSome then
            let chosen = chosen |> List.map Option.get |> List.distinct
            chosen
            |> List.filter (fun context ->
                not (chosen |> List.exists (fun other -> other <> context && Set.isProperSubset (membersOf context) (membersOf other))))
            |> Some
        else None

    /// The one of `contexts`, which `covering` gave, that has the member `memberName`.
    let having (state: InferState) (contexts: Identifier list) (memberName: Identifier) : Identifier option =
        let declared = declared state
        contexts
        |> List.tryFind (fun context -> declared |> List.exists (fun (name, members) -> name = context && members.Contains memberName))

    /// The names of the prelude's contexts in a module with the top-level items `items`, given those its imports
    /// brought in (`imported`). The prelude's own (`isPrelude`) are the contexts it declares. In any other module, a
    /// name the module gives to a type of its own is no longer the prelude's.
    let inModule (isPrelude: bool) (items: Module) (imported: Set<Identifier>) : Set<Identifier> =
        let own =
            items
            |> List.choose (fun item ->
                match item.Kind with
                | Def (TypeDef(name, modifiers, _, _)) -> Some (name, List.contains Context modifiers)
                | _ -> None)
        if isPrelude then own |> List.filter snd |> List.map fst |> Set.ofList
        else imported - Set.ofList (List.map fst own)

    /// Each member of the prelude's contexts that has no one smallest context, with the contexts tied for it: what the
    /// prelude must not have, since an operator that reads the member would have no context to be read through.
    let private ambiguities (state: InferState) : (Identifier * Identifier list) list =
        let contexts = declared state
        contexts
        |> List.collect (fun (_, members) -> Set.toList members)
        |> List.distinct
        |> List.choose (fun memberName ->
            match smallestOf contexts memberName with
            | _ :: _ :: _ as tied -> Some (memberName, List.sort tied)
            | _ -> None)

    /// The error for each member of the prelude's contexts that has no one smallest context (see `ambiguities`), at
    /// the first of the contexts tied for it. Only the prelude itself is checked for them.
    let ambiguityErrors (state: InferState) : Diagnostic list =
        ambiguities state
        |> List.map (fun (memberName, tied) ->
            let span =
                state.TypeDefs
                |> Map.tryFind tied.Head
                |> Option.bind _.Symbol
                |> Option.bind (fun symbol -> state.Symbols |> Map.tryFind symbol)
                |> Option.map _.Declaration
                |> Option.defaultValue SourceSpan.none
            let size = (declared state |> List.find (fun (name, _) -> name = tied.Head) |> snd).Count
            let contexts = Diagnostics.listed (tied |> List.map (fun name -> "'" + name + "'"))
            let written = Names.written memberName
            Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule span
                $"The prelude's contexts {contexts} each have '{written}' among {size} members, so an operator that reads it has no one context to be read through: give one of them fewer members, or move '{written}' to a smaller context of its own")
