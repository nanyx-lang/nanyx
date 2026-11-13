/// The order the formatter sorts imports in: standard library modules (`nanyx/...`) first, then other packages'
/// modules, then imports by path. Each group is alphabetical by its source as written, ignoring case, with ties broken
/// by case. The alias doesn't affect the order. Imports in a row are merged into one before they are sorted.
module Nanyx.Formatter.ImportOrder

open System
open Parser.Program

let private group (source: ImportSource) =
    match source with
    | ModuleImport m when m = "nanyx" || m.StartsWith "nanyx/" -> 0
    | ModuleImport _ -> 1
    | PathImport _ -> 2

let private written (source: ImportSource) =
    match source with
    | ModuleImport m
    | PathImport m -> m

/// Compares two imports by the order above.
let compare (a: ImportItem) (b: ImportItem) =
    match Operators.compare (group a.Source) (group b.Source) with
    | 0 ->
        match String.Compare(written a.Source, written b.Source, StringComparison.OrdinalIgnoreCase) with
        | 0 -> String.CompareOrdinal(written a.Source, written b.Source)
        | byLetters -> byLetters
    | byGroup -> byGroup

/// The imports sorted. The sort is stable, so duplicates keep their order.
let sort (items: ImportItem list) = List.sortWith compare items

/// Imports in a row are written as one import of all their items: `import nanyx/math` and `import nanyx/text` become
/// `import (` / `nanyx/math` / `nanyx/text` / `)`. An item that `importsOf` gives imports for joins the run of the item
/// before it when that one is an import too and `joins previous next` allows it. `merge` makes one item of each run of
/// more than one, from the run and all its imports in order.
let mergeRuns (importsOf: 'a -> ImportItem list option) (joins: 'a -> 'a -> bool) (merge: 'a list -> ImportItem list -> 'a) (items: 'a list) : 'a list =
    items
    |> List.fold
        (fun (runs: 'a list list) next ->
            match runs with
            | (previous :: _ as run) :: rest when (importsOf next).IsSome && (importsOf previous).IsSome && joins previous next ->
                (next :: run) :: rest
            | _ -> [ next ] :: runs)
        []
    |> List.rev
    |> List.map (fun run ->
        match List.rev run with
        | [ single ] -> single
        | run -> merge run (run |> List.collect (importsOf >> Option.defaultValue [])))
