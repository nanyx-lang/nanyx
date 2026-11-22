namespace NyxCompiler

/// What a module is known by, given where its source came from.
module ModuleOrigins =
    let internal originKey (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> fullPath
        | StandardLibraryModule name -> StandardLibrary.importPrefix + name
        | InMemory -> "<source>"

    /// The file a module's syntax nodes are reported in, which their spans name: how the backend tells whether code is in
    /// a nominal type's home module (see `CoreIrLowerTypeMatch`).
    let sourceFileName (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> fullPath
        | StandardLibraryModule name -> StandardLibrary.importPrefix + name + ".nyx"
        | InMemory -> ""
