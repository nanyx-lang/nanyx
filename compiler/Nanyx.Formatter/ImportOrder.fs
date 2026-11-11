/// The order the formatter sorts imports in: standard library modules (`nanyx/...`) first, then other packages'
/// modules, then imports by path. Each group is alphabetical by its source as written, ignoring case, with ties broken
/// by case. The alias doesn't affect the order.
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
