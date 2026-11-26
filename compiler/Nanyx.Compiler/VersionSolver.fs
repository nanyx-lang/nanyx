namespace NyxCompiler

open System.Collections.Generic

/// Why a version the index lists can't be chosen for a new resolution.
type UnusableVersion =
    /// Its author unlisted it. A lockfile that already names it keeps it.
    | Unlisted
    /// Its release or asset is missing from GitHub, so it can't be downloaded.
    | Unavailable
    /// It needs another compiler: the compilers it builds with, and this one.
    | NeedsCompiler of requirement: CompilerRequirement * running: string
    /// The index writes one of its dependencies as something this compiler can't read.
    | UnreadableDependencies

/// A version of a package the solver can choose from, as the index lists it.
type SolverVersion =
    { Version: SemanticVersion
      /// The packages it depends on, each with the versions it accepts.
      Dependencies: (string * VersionRequirement) list
      /// Why it can't be chosen, if it can't.
      Unusable: UnusableVersion option }

/// A dependency of the package being resolved for, or of a package beside it on disk, which a resolution has to
/// satisfy: `Package` at the versions `Requirement` accepts, written in the manifest of the package `From`.
type RootRequirement =
    { From: string
      Package: string
      Requirement: VersionRequirement }

/// What the solver resolves: the dependencies of one package, the root.
type SolverInput =
    { /// The package being resolved for.
      Root: string
      /// The dependencies the root and the packages beside it on disk have.
      Requirements: RootRequirement list
      /// The versions of a package, in any order; none for a package resolution leaves alone, such as one the compiler
      /// ships; or why they can't be read.
      Versions: string -> Result<SolverVersion list option, string>
      /// The version a lockfile already locks of each package, which is chosen whenever it is allowed, so that
      /// resolving again never upgrades a package as a side effect.
      Locked: Map<string, SemanticVersion> }

/// What the solver finds.
type SolverOutcome =
    /// The version chosen of each package the build needs, sorted by name.
    | Solved of (string * SemanticVersion) list
    /// No choice of versions satisfies every dependency: the lines that explain why, in plain sentences.
    | NoSolution of string list
    /// The versions of a package can't be read.
    | IndexFailed of string

/// Choosing one version of each package a build needs, with [PubGrub](https://github.com/dart-lang/pub/blob/master/doc/solver.md),
/// as Dart and uv do: the newest version that satisfies every constraint (#859), one version of each package (#860), and,
/// when there is none, an explanation of why in plain sentences, built from the incompatibilities that led there.
///
/// The versions a term allows are a finite set: those the index lists of the package. A dependency whose requirement
/// matches none of them is an incompatibility of its own, which says so.
module VersionSolver =
    type internal Term =
        { Package: string
          Positive: bool
          Versions: Set<SemanticVersion> }

    type internal Cause =
        /// The root has to be chosen.
        | RootCause
        /// A dependency: the first term's versions depend on the second term's package at `Requirement`. `From` names the
        /// package on disk that writes it, for one of the root's.
        | DependsOn of requirement: VersionRequirement * from: string option
        /// A dependency on `package` that no version the index lists of it satisfies.
        | NoMatch of package: string * requirement: VersionRequirement * from: string option
        /// Versions that can't be chosen.
        | CantUse of UnusableVersion
        /// Derived from two others while resolving a conflict.
        | Derived of Incompatibility * Incompatibility

    /// A set of terms that can't all be true at once.
    and internal Incompatibility = { Id: int; Terms: Term list; Cause: Cause }

    type private Assignment =
        { Term: Term
          Level: int
          /// The incompatibility it was derived from; none for a decision.
          Cause: Incompatibility option }

    type private Relation =
        | Satisfied
        | Contradicted
        | Inconclusive

    let private negate (term: Term) = { term with Positive = not term.Positive }

    /// The versions both terms allow, as a term.
    let private intersect (a: Term) (b: Term) : Term =
        match a.Positive, b.Positive with
        | true, true -> { a with Versions = Set.intersect a.Versions b.Versions }
        | true, false -> { a with Versions = Set.difference a.Versions b.Versions }
        | false, true -> { b with Versions = Set.difference b.Versions a.Versions }
        | false, false -> { a with Versions = Set.union a.Versions b.Versions }

    /// A positive term that allows nothing.
    let private isEmpty (term: Term) = term.Positive && term.Versions.IsEmpty

    /// Whether everything `a` allows, `b` allows. A negative term also allows the package not being chosen at all.
    let private subset (a: Term) (b: Term) =
        match a.Positive, b.Positive with
        | true, true -> Set.isSubset a.Versions b.Versions
        | true, false -> Set.intersect a.Versions b.Versions |> Set.isEmpty
        | false, true -> false
        | false, false -> Set.isSubset b.Versions a.Versions

    let private disjoint (a: Term) (b: Term) =
        match a.Positive, b.Positive with
        | true, true -> Set.intersect a.Versions b.Versions |> Set.isEmpty
        | true, false -> Set.isSubset a.Versions b.Versions
        | false, true -> Set.isSubset b.Versions a.Versions
        | false, false -> false

    let private relation (assigned: Term option) (term: Term) =
        match assigned with
        | None -> Inconclusive
        | Some assigned when subset assigned term -> Satisfied
        | Some assigned when disjoint assigned term -> Contradicted
        | Some _ -> Inconclusive

    /// The solver's state for one resolution.
    type private State(input: SolverInput) =
        let universes = Dictionary<string, SemanticVersion list>()
        let listings = Dictionary<string, SolverVersion list>()
        let incompatibilities = Dictionary<string, ResizeArray<Incompatibility>>()
        let assignments = ResizeArray<Assignment>()
        let decisions = Dictionary<string, SemanticVersion>()
        let mutable nextId = 0
        let rootVersion = { Major = 0; Minor = 0; Patch = 0; PreRelease = [] }
        do universes.[input.Root] <- [ rootVersion ]

        member _.Root = input.Root
        member _.RootVersion = rootVersion

        /// The versions listed of `package`, oldest first.
        member _.Universe(package: string) =
            match universes.TryGetValue package with
            | true, versions -> versions
            | _ -> []

        member _.Listing(package: string) =
            match listings.TryGetValue package with
            | true, versions -> versions
            | _ -> []

        member _.IsKnown(package: string) = universes.ContainsKey package

        member _.Know(package: string, versions: SolverVersion list) =
            let sorted = versions |> List.sortBy _.Version
            listings.[package] <- sorted
            universes.[package] <- sorted |> List.map _.Version

        member _.Make(terms: Term list, cause: Cause) : Incompatibility =
            // Terms for one package are one term, the versions both allow; the root, once chosen, is always chosen, so a
            // derived incompatibility leaves it out.
            let merged =
                terms
                |> List.groupBy _.Package
                |> List.map (fun (_, same) -> List.reduce intersect same)
            let merged =
                match cause with
                | Derived _ when merged.Length <> 1 && merged |> List.exists (fun term -> term.Positive && term.Package = input.Root) ->
                    merged |> List.filter (fun term -> not (term.Positive && term.Package = input.Root))
                | _ -> merged
            nextId <- nextId + 1
            { Id = nextId; Terms = merged; Cause = cause }

        member this.Add(incompatibility: Incompatibility) =
            for term in incompatibility.Terms do
                match incompatibilities.TryGetValue term.Package with
                | true, list -> list.Add incompatibility
                | _ -> incompatibilities.[term.Package] <- ResizeArray [ incompatibility ]

        member _.IncompatibilitiesOf(package: string) =
            match incompatibilities.TryGetValue package with
            | true, list -> List.ofSeq list |> List.rev
            | _ -> []

        member _.DecisionLevel = decisions.Count - 1
        member _.Assignments = assignments
        member _.Decisions = decisions

        /// What the assignments allow of `package`, if any is about it.
        member _.Assigned(package: string) : Term option =
            let mutable found = None
            for assignment in assignments do
                if assignment.Term.Package = package then
                    found <- Some(match found with None -> assignment.Term | Some term -> intersect term assignment.Term)
            found

        member this.Derive(term: Term, cause: Incompatibility) =
            assignments.Add { Term = term; Level = this.DecisionLevel; Cause = Some cause }

        member this.Decide(package: string, version: SemanticVersion) =
            decisions.[package] <- version
            assignments.Add { Term = { Package = package; Positive = true; Versions = Set.singleton version }; Level = this.DecisionLevel; Cause = None }

        member _.Backtrack(level: int) =
            let kept = assignments |> Seq.filter (fun assignment -> assignment.Level <= level) |> List.ofSeq
            for assignment in assignments do
                if assignment.Level > level && assignment.Cause.IsNone then
                    decisions.Remove assignment.Term.Package |> ignore
            assignments.Clear()
            assignments.AddRange kept

        /// The index of the first assignment by which the assignments together satisfy `term`.
        member _.SatisfierOf(term: Term) : int =
            let mutable found = None
            let mutable index = 0
            let mutable accumulated = None
            while found.IsNone && index < assignments.Count do
                let assignment = assignments.[index]
                if assignment.Term.Package = term.Package then
                    let next = match accumulated with None -> assignment.Term | Some soFar -> intersect soFar assignment.Term
                    accumulated <- Some next
                    if subset next term then found <- Some index
                index <- index + 1
            match found with
            | Some index -> index
            | None -> failwith "Compiler bug: the version solver looked for what satisfies a term nothing satisfies"

    exception private IndexError of string

    /// The term that allows the versions of `package` that `requirement` accepts.
    let private accepted (state: State) (package: string) (requirement: VersionRequirement) =
        state.Universe package |> List.filter (VersionRequirement.accepts requirement) |> Set.ofList

    /// Reads the versions of `package`, the first time it is reached, and adds an incompatibility for each run of them that
    /// can't be chosen for the same reason. Gives whether resolution looks at it at all.
    let private learn (state: State) (input: SolverInput) (package: string) : bool =
        if state.IsKnown package then
            true
        else
            match input.Versions package with
            | Result.Error message -> raise (IndexError message)
            | Result.Ok None -> false
            | Result.Ok(Some versions) ->
                state.Know(package, versions)
                let runs =
                    state.Listing package
                    |> List.fold
                        (fun runs version ->
                            match runs with
                            | (reason, members) :: rest when reason = version.Unusable -> (reason, version.Version :: members) :: rest
                            | _ -> (version.Unusable, [ version.Version ]) :: runs)
                        []
                for reason, members in List.rev runs do
                    match reason with
                    | Some reason -> state.Add(state.Make([ { Package = package; Positive = true; Versions = Set.ofList members } ], CantUse reason))
                    | None -> ()
                true

    /// The incompatibility of `depender` with the versions of `package` that `requirement` doesn't accept; or one that says
    /// no version the index lists of it does, when none does. None for a package resolution leaves alone.
    let private dependency (state: State) (input: SolverInput) (depender: Term) (package: string) (requirement: VersionRequirement) (from: string option) =
        if not (learn state input package) then
            None
        else
            match accepted state package requirement with
            | allowed when allowed.IsEmpty -> Some(state.Make([ depender ], NoMatch(package, requirement, from)))
            | allowed -> Some(state.Make([ depender; { Package = package; Positive = false; Versions = allowed } ], DependsOn(requirement, from)))

    /// The incompatibilities for the dependencies of `package` at `version`, each covering the run of versions around it
    /// that depend on the same package with the same requirement, so that an explanation speaks of them together.
    let private dependenciesOf (state: State) (input: SolverInput) (package: string) (version: SemanticVersion) =
        let listing = state.Listing package |> Array.ofList
        let at = listing |> Array.findIndex (fun candidate -> candidate.Version = version)
        let same (name: string) (requirement: VersionRequirement) (candidate: SolverVersion) =
            candidate.Dependencies |> List.exists (fun (other, written) -> other = name && written.Minimum = requirement.Minimum && written.Exact = requirement.Exact)
        listing.[at].Dependencies
        |> List.filter (fun (name, _) -> name <> package)
        |> List.choose (fun (name, requirement) ->
            let mutable first = at
            while first > 0 && same name requirement listing.[first - 1] do
                first <- first - 1
            let mutable last = at
            while last < listing.Length - 1 && same name requirement listing.[last + 1] do
                last <- last + 1
            let versions = listing.[first..last] |> Array.map _.Version |> Set.ofArray
            dependency state input { Package = package; Positive = true; Versions = versions } name requirement None)

    /// Whether `incompatibility` means resolution failed: it has no terms, or says only that the root can't be chosen.
    let private isFailure (state: State) (incompatibility: Incompatibility) =
        match incompatibility.Terms with
        | [] -> true
        | [ term ] -> term.Positive && term.Package = state.Root
        | _ -> false

    /// Derives what `incompatibility` forces, if all its terms but one are satisfied: gives the package that changed, or
    /// none; or that every term is satisfied, a conflict.
    let private propagate (state: State) (incompatibility: Incompatibility) : Choice<string option, unit> =
        let rec scan (terms: Term list) (unsatisfied: Term option) =
            match terms with
            | [] ->
                match unsatisfied with
                | None -> Choice2Of2()
                | Some term ->
                    state.Derive(negate term, incompatibility)
                    Choice1Of2(Some term.Package)
            | term :: rest ->
                match relation (state.Assigned term.Package) term with
                | Contradicted -> Choice1Of2 None
                | Inconclusive when unsatisfied.IsSome -> Choice1Of2 None
                | Inconclusive -> scan rest (Some term)
                | Satisfied -> scan rest unsatisfied
        scan incompatibility.Terms None

    exception internal Unsolvable of Incompatibility

    /// Resolves a conflict: learns, from `incompatibility` and the causes of what satisfied it, the incompatibility that
    /// explains it, and backtracks to where that one forces something new. Raises `Unsolvable` when it is a failure.
    let private resolveConflict (state: State) (incompatibility: Incompatibility) : Incompatibility =
        let rec resolve (incompatibility: Incompatibility) (isNew: bool) =
            if isFailure state incompatibility then
                raise (Unsolvable incompatibility)
            let mutable latestTerm = None
            let mutable latest = -1
            let mutable difference = None
            let mutable previousLevel = 0
            for term in incompatibility.Terms do
                let satisfier = state.SatisfierOf term
                if latest = -1 then
                    latestTerm <- Some term
                    latest <- satisfier
                elif latest < satisfier then
                    previousLevel <- max previousLevel state.Assignments.[latest].Level
                    latestTerm <- Some term
                    latest <- satisfier
                    difference <- None
                else
                    previousLevel <- max previousLevel state.Assignments.[satisfier].Level
                if latestTerm = Some term then
                    let left = intersect state.Assignments.[latest].Term (negate term)
                    if isEmpty left then
                        difference <- None
                    else
                        difference <- Some left
                        previousLevel <- max previousLevel state.Assignments.[state.SatisfierOf(negate left)].Level
            let satisfier = state.Assignments.[latest]
            match satisfier.Cause with
            | Some cause when previousLevel >= satisfier.Level ->
                let term = latestTerm.Value
                let terms =
                    (incompatibility.Terms |> List.filter (fun other -> other <> term))
                    @ (cause.Terms |> List.filter (fun other -> other.Package <> satisfier.Term.Package))
                    @ (difference |> Option.map negate |> Option.toList)
                resolve (state.Make(terms, Derived(incompatibility, cause))) true
            | _ ->
                state.Backtrack previousLevel
                if isNew then state.Add incompatibility
                incompatibility
        resolve incompatibility false

    /// Unit propagation from `package`: derives everything the incompatibilities force, resolving each conflict it meets.
    let private unitPropagation (state: State) (package: string) =
        let changed = Queue<string>([ package ])
        while changed.Count > 0 do
            let next = changed.Dequeue()
            let mutable stop = false
            for incompatibility in state.IncompatibilitiesOf next do
                if not stop then
                    match propagate state incompatibility with
                    | Choice1Of2(Some package) -> changed.Enqueue package
                    | Choice1Of2 None -> ()
                    | Choice2Of2() ->
                        let rootCause = resolveConflict state incompatibility
                        changed.Clear()
                        match propagate state rootCause with
                        | Choice1Of2(Some package) -> changed.Enqueue package
                        | _ -> failwith "Compiler bug: the version solver learnt an incompatibility that forces nothing"
                        stop <- true

    /// Chooses a version of a package that some term requires and none is chosen of yet: the one the lockfile locks, if
    /// it is allowed, or else the newest allowed. Gives the package, or none when every package has its version.
    let private decide (state: State) (input: SolverInput) : string option =
        let undecided =
            state.Assignments
            |> Seq.map _.Term.Package
            |> Seq.distinct
            |> Seq.filter (fun package -> not (state.Decisions.ContainsKey package))
            |> Seq.choose (fun package ->
                match state.Assigned package with
                | Some term when term.Positive -> Some(package, term.Versions)
                | _ -> None)
            |> List.ofSeq
        match undecided |> List.sortBy (fun (package, allowed) -> allowed.Count, package) with
        | [] -> None
        | (package, allowed) :: _ ->
            let version =
                match input.Locked.TryFind package with
                | Some locked when allowed.Contains locked -> locked
                | _ -> Set.maxElement allowed
            let added =
                if package = state.Root then
                    let root = { Package = package; Positive = true; Versions = Set.singleton version }
                    input.Requirements
                    |> List.choose (fun requirement ->
                        let from = if requirement.From = input.Root then None else Some requirement.From
                        dependency state input root requirement.Package requirement.Requirement from)
                else
                    dependenciesOf state input package version
            let mutable conflict = false
            for incompatibility in added do
                state.Add incompatibility
                conflict <-
                    conflict
                    || incompatibility.Terms
                       |> List.forall (fun term -> term.Package = package || relation (state.Assigned term.Package) term = Satisfied)
            if not conflict then state.Decide(package, version)
            Some package

    // Explaining a failure, as pub does: each derived incompatibility is a sentence, "Because A and B, C.", and one used
    // more than once gets a number, to be referred to.

    /// `versions` of `package`, as a sentence says them.
    let private describeVersions (state: State) (package: string) (versions: Set<SemanticVersion>) =
        let universe = state.Universe package
        if package = state.Root then
            package
        elif versions.IsEmpty then
            $"no version of {package}"
        elif versions.Count = 1 then
            $"{package} {Set.minElement versions}"
        elif versions.Count = universe.Length then
            $"every version of {package}"
        else
            let inSet = universe |> List.map versions.Contains
            let first = inSet |> List.findIndex id
            let last = inSet |> List.findIndexBack id
            let contiguous = inSet.[first..last] |> List.forall id
            let low, high = Set.minElement versions, Set.maxElement versions
            if contiguous && last = universe.Length - 1 then $"{package} {low} or later"
            elif contiguous && first = 0 then $"{package} {high} or earlier"
            elif contiguous then $"{package} {low} to {high}"
            else
                $"{package} {versions |> Set.toList |> List.map string |> Diagnostics.listedOr}"

    let private describeTerm (state: State) (term: Term) = describeVersions state term.Package term.Versions

    /// What makes the versions `term` allows unusable, after them: "is unlisted".
    let private unusableText (state: State) (term: Term) (reason: UnusableVersion) =
        let one = term.Versions.Count = 1 || term.Versions.Count = (state.Universe term.Package).Length
        match reason with
        | Unlisted -> if one then "is unlisted" else "are unlisted"
        | Unavailable -> if one then "is missing from GitHub" else "are missing from GitHub"
        | NeedsCompiler(requirement, running) -> $"""{(if one then "needs" else "need")} {CompilerRequirement.describe requirement} (this is {running})"""
        | UnreadableDependencies -> if one then "has dependencies the index writes in a way this nanyx can't read" else "have dependencies the index writes in a way this nanyx can't read"

    /// Who depends on something, in a dependency: the package on disk that writes it, for one of the root's.
    let private subject (state: State) (term: Term) (from: string option) =
        match from with
        | Some from when term.Package = state.Root -> from
        | _ -> describeTerm state term

    /// How the explanation of a failure ends.
    let private failed = "the dependencies can't all be satisfied"

    /// What `incompatibility` says, as part of a sentence.
    let private describe (state: State) (incompatibility: Incompatibility) =
        let subject = subject state
        match incompatibility.Cause, incompatibility.Terms with
        | DependsOn(requirement, from), [ depender; dependee ] ->
            $"{subject depender from} depends on {dependee.Package} {requirement}"
        | NoMatch(package, requirement, from), [ depender ] ->
            let missing =
                if (state.Universe package).IsEmpty then "which isn't in the index"
                else $"which no version of {package} in the index satisfies"
            $"{subject depender from} depends on {package} {requirement}, {missing}"
        | CantUse reason, [ term ] -> $"{describeTerm state term} {unusableText state term reason}"
        | RootCause, _ -> $"{state.Root} is the package being resolved"
        | _, terms ->
            match terms with
            | [] -> failed
            | [ term ] when term.Positive && term.Package = state.Root -> failed
            | [ term ] when term.Positive && term.Versions.Count = (state.Universe term.Package).Length -> $"no version of {term.Package} can be used"
            | [ term ] when term.Positive -> $"{describeTerm state term} can't be used"
            | [ term ] -> $"{describeTerm state term} is required"
            | _ ->
                let positive = terms |> List.filter _.Positive
                let negative = terms |> List.filter (fun term -> not term.Positive)
                match positive, negative with
                | [ one ], [ other ] -> $"{describeTerm state one} requires {describeTerm state other}"
                | [ one; other ], [] -> $"{describeTerm state one} is incompatible with {describeTerm state other}"
                | positive, [] -> $"""{positive |> List.map (describeTerm state) |> Diagnostics.listed} can't all be used together"""
                | positive, negative ->
                    let verb = if positive.Length = 1 then "requires" else "require"
                    $"""{positive |> List.map (describeTerm state) |> Diagnostics.listed} {verb} {negative |> List.map (describeTerm state) |> Diagnostics.listedOr}"""

    /// The lines that explain why `failure` has no solution.
    let private explain (state: State) (failure: Incompatibility) : string list =
        let derivations = Dictionary<int, int>()
        let rec count (incompatibility: Incompatibility) =
            match incompatibility.Cause with
            | Derived(first, second) ->
                match derivations.TryGetValue incompatibility.Id with
                | true, n -> derivations.[incompatibility.Id] <- n + 1
                | _ ->
                    derivations.[incompatibility.Id] <- 1
                    count first
                    count second
            | _ -> ()
        count failure
        let lines = ResizeArray<string * int option>()
        let numbers = Dictionary<int, int>()
        let numberOf (incompatibility: Incompatibility) =
            match numbers.TryGetValue incompatibility.Id with
            | true, n -> Some n
            | _ -> None
        let write (incompatibility: Incompatibility) (message: string) (numbered: bool) =
            if numbered then
                let number = numbers.Count + 1
                numbers.[incompatibility.Id] <- number
                lines.Add(message, Some number)
            else
                lines.Add(message, None)
        let isDerived (incompatibility: Incompatibility) = incompatibility.Cause.IsDerived
        let causesOf (incompatibility: Incompatibility) =
            match incompatibility.Cause with
            | Derived(first, second) -> first, second
            | _ -> failwith "Compiler bug: the version solver explained an incompatibility that wasn't derived"
        let isSingleLine (incompatibility: Incompatibility) =
            let first, second = causesOf incompatibility
            not (isDerived first) && not (isDerived second)
        let isCollapsible (incompatibility: Incompatibility) =
            if derivations.GetValueOrDefault(incompatibility.Id, 0) > 1 then
                false
            else
                let first, second = causesOf incompatibility
                match isDerived first, isDerived second with
                | true, false -> (numberOf first).IsNone
                | false, true -> (numberOf second).IsNone
                | _ -> false
        let text = describe state
        /// Two incompatibilities, joined by "and": two dependencies of the same package as one.
        let both (first: Incompatibility) (second: Incompatibility) =
            match first.Cause, first.Terms, second.Cause, second.Terms with
            | DependsOn(a, fromA), [ dependerA; dependeeA ], DependsOn(b, fromB), [ dependerB; dependeeB ] when dependerA = dependerB && fromA = fromB ->
                $"{subject state dependerA fromA} depends on {dependeeA.Package} {a} and {dependeeB.Package} {b}"
            | _ -> $"{text first} and {text second}"
        let rec visit (incompatibility: Incompatibility) (conclusion: bool) =
            let numbered = conclusion || derivations.GetValueOrDefault(incompatibility.Id, 0) > 1
            let first, second = causesOf incompatibility
            let says = text incompatibility
            match isDerived first, isDerived second with
            | true, true ->
                match numberOf first, numberOf second with
                | Some a, Some b -> write incompatibility $"Because {text first} ({a}) and {text second} ({b}), {says}." numbered
                | Some a, None ->
                    visit second false
                    write incompatibility $"And because {text first} ({a}), {says}." numbered
                | None, Some b ->
                    visit first false
                    write incompatibility $"And because {text second} ({b}), {says}." numbered
                | None, None when isSingleLine first || isSingleLine second ->
                    let earlier, later = if isSingleLine second then first, second else second, first
                    visit earlier false
                    visit later false
                    write incompatibility $"Thus, {says}." numbered
                | None, None ->
                    visit first true
                    lines.Add("", None)
                    visit second false
                    write incompatibility $"And because {text first} ({(numberOf first).Value}), {says}." numbered
            | true, false
            | false, true ->
                let derived, external = if isDerived first then first, second else second, first
                match numberOf derived with
                | Some n -> write incompatibility $"Because {both external derived} ({n}), {says}." numbered
                | None when isCollapsible derived ->
                    let a, b = causesOf derived
                    let collapsedDerived, collapsedExternal = if isDerived a then a, b else b, a
                    visit collapsedDerived false
                    write incompatibility $"And because {both collapsedExternal external}, {says}." numbered
                | None ->
                    visit derived false
                    write incompatibility $"And because {text external}, {says}." numbered
            | false, false -> write incompatibility $"Because {both first second}, {says}." numbered
        match failure.Cause with
        | Derived _ -> visit failure false
        | _ -> lines.Add($"Because {text failure}, {failed}.", None)
        let width = if numbers.Count = 0 then 0 else (string numbers.Count).Length + 3
        lines
        |> Seq.map (fun (message, number) ->
            match number with
            | _ when message = "" -> ""
            | Some n -> ($"({n})").PadRight width + message
            | None -> String.replicate width " " + message)
        |> List.ofSeq

    /// Resolves `input`: the version of each package the build needs, or why there is none.
    let solve (input: SolverInput) : SolverOutcome =
        let state = State input
        state.Know(input.Root, [ { Version = state.RootVersion; Dependencies = []; Unusable = None } ])
        state.Add(state.Make([ { Package = input.Root; Positive = false; Versions = Set.singleton state.RootVersion } ], RootCause))
        try
            let mutable next = Some input.Root
            while next.IsSome do
                unitPropagation state next.Value
                next <- decide state input
            state.Decisions
            |> Seq.filter (fun decision -> decision.Key <> input.Root)
            |> Seq.map (fun decision -> decision.Key, decision.Value)
            |> Seq.sortBy fst
            |> List.ofSeq
            |> Solved
        with
        | Unsolvable failure -> NoSolution(explain state failure)
        | IndexError message -> IndexFailed message
