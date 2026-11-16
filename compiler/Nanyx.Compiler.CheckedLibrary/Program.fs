/// Writes the checked standard library to the file the one argument names.
[<EntryPoint>]
let main argv =
    match argv with
    | [| path |] ->
        NyxCompiler.Compiler.writeCheckedLibrary path
        0
    | _ ->
        eprintfn "usage: Nanyx.Compiler.CheckedLibrary <path>"
        2
