/// The Nanyx source files under a folder, as `nanyx format` finds them and the language server indexes a workspace's.
module Nanyx.Formatter.SourceFiles

open System
open System.IO

/// The folders never searched for files: build output and packages. Hidden folders, such as `.git`, are skipped too.
let private skippedFolders = set [ "bin"; "obj"; "node_modules" ]

/// Whether the folder named `name` is never searched: build output, packages or a hidden folder. The names are compared
/// without case, as Windows and macOS find folders.
let isSkipped (name: string) =
    name.StartsWith "." || skippedFolders |> Set.exists (fun skipped -> String.Equals(skipped, name, StringComparison.OrdinalIgnoreCase))

/// Every `.nyx` file under `folder`, by full path, outside the skipped folders: each folder's files in order of name,
/// then its folders'. A folder that can't be read is passed over.
let rec under (folder: string) : string seq =
    seq {
        let entries =
            try
                Some (Directory.EnumerateFiles(folder, "*.nyx") |> Seq.sort |> List.ofSeq, Directory.EnumerateDirectories folder |> Seq.sort |> List.ofSeq)
            with :? IOException | :? UnauthorizedAccessException -> None
        match entries with
        | Some (files, folders) ->
            yield! files |> Seq.map Path.GetFullPath
            for child in folders do
                if not (isSkipped (Path.GetFileName child)) then yield! under child
        | None -> ()
    }

/// Whether `file` is under one of `folders`, outside the skipped folders.
let isUnder (folders: string seq) (file: string) =
    let rec inside (folder: string) (directory: string) =
        match directory with
        | null -> false
        | _ when Path.GetRelativePath(folder, directory) = "." -> true
        | _ -> not (isSkipped (Path.GetFileName directory)) && inside folder (Path.GetDirectoryName directory)
    let directory = Path.GetDirectoryName(Path.GetFullPath file)
    folders |> Seq.exists (fun folder -> inside (Path.GetFullPath folder) directory)
