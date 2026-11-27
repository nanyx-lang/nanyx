namespace NyxCompiler

open Parser.Program

/// What the editor's live mode needs to know of a file: whether it has an entry point, a script's or a package's
/// `@entry`, and the contexts that entry point requires. Live mode re-runs such a file on every save, on its own only
/// when re-running can't reach past the program, and only when the user turns it on otherwise (#2055).
module LiveMode =
    /// What an entry point is: a script's statements, or a def marked `@entry`.
    type EntryKind =
        | Statements
        | EntryDefinition

    /// A file's entry point: what it is, and the names of the contexts it requires, in the order its type has them.
    type Entry = { Kind: EntryKind; Contexts: string list }

    /// The contexts an entry point may require for live mode to be offered on its own: `Console`, whose output the editor
    /// shows and whose input is empty; `Async`; and `FileSystem`, which a live run keeps in memory over the real files.
    let offeredContexts = set [ "Async"; "Console"; "FileSystem" ]

    /// The def `module'` passes to its host's `run`, if it passes one: what `Attributes` and `Scripts` make of an
    /// `@entry`, and of a script's statements once they are typed (see `Scripts.asDefinition`).
    let private entryName (module': Module) =
        module'
        |> List.tryPick (fun item ->
            match item.Kind with
            | Expr { Kind = FunctionCall(run, _, [ { Kind = IdentifierExpr name } ]) } when run = $"{Hosts.importAlias}.{Hosts.runFunction}" -> Some name
            | _ -> None)

    /// The entry point of the module `typed` is, if it has one, with the contexts it requires, written or inferred.
    let entryOf (typed: TypedModule) : Entry option =
        entryName typed.Module
        |> Option.map (fun name ->
            // Those its type has, as a script's entry point has them once it is typed (see `Scripts.asDefinition`), and
            // those inferred for a def whose type doesn't write them.
            let written =
                match typed.Types |> Map.tryFind name |> Option.map Unifier.unalias with
                | Some (TyFunc(_, _, contexts)) -> contexts
                | _ -> []
            let inferred =
                typed.Symbols
                |> Map.values
                |> Seq.tryFind (fun symbol -> symbol.Kind = TopLevelValue && symbol.Name = name)
                |> Option.bind (fun symbol -> typed.ContextTypes |> Map.tryFind symbol.Id)
                |> Option.defaultValue []
            let contexts = written @ inferred
            { Kind = (if name = Scripts.definitionName then Statements else EntryDefinition)
              Contexts = contexts |> List.choose TestChecks.contextName |> List.distinct })

    /// Whether live mode is offered for `entry` on its own: whether it requires none but `offeredContexts`.
    let offered (entry: Entry) = entry.Contexts |> List.forall offeredContexts.Contains
