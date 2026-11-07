namespace NyxCompiler

open System.IO
open System.Reflection
open Parser.Program

/// The compiler intrinsics: the few operations the standard library is written over because they cannot
/// be expressed in Nanyx itself. This is the one place that lists them and their types.
///
/// They are in scope only while a standard-library module is being checked, never in user code. The
/// WebAssembly backend lowers a call to one of them onto a helper function it emits with the module
/// (see `runtimeHelpers` in Transpiler.Wasm.CodeGen).
module Intrinsics =
    /// Every intrinsic name starts with this, which keeps them out of the way of ordinary identifiers.
    let prefix = "__nanyx_"

    let private stringTy = TyPrimitive "string"
    let private intTy = TyPrimitive "int"
    let private unitTy = TyPrimitive "unit"

    /// The input type of a function taking several arguments: a positional record, as inference builds it.
    let private tuple (items: Ty list) =
        items |> List.mapi (fun index ty -> string index, ty) |> Map.ofList |> TyRecord

    let private elementVar = { Id = -1; Name = Some "a" }
    let private element = TyVar elementVar
    let private listOf (ty: Ty) = TyApply("list", [ ty ])

    /// Writes a string to standard output, with nothing added.
    let writeStdout = prefix + "write_stdout"
    /// Writes a string to standard error, with nothing added.
    let writeStderr = prefix + "write_stderr"
    /// The next byte of standard input, or -1 at its end (or when it cannot be read).
    let readStdinByte = prefix + "read_stdin_byte"
    /// The number of bytes in a string.
    let stringLength = prefix + "string_length"
    /// The byte at an index of a string, or -1 when the index is out of range.
    let stringByteAt = prefix + "string_byte_at"
    /// A new string holding `count` bytes of a string from `start`; both are clamped to the string.
    let stringSlice = prefix + "string_slice"
    /// A new one-byte string.
    let stringFromByte = prefix + "string_from_byte"
    /// A list with an element added at the front.
    let listCons = prefix + "list_cons"

    /// Every intrinsic with its type scheme.
    let all : (string * TypeScheme) list =
        [ writeStdout, TypeEnv.mono (TyFunc(stringTy, unitTy))
          writeStderr, TypeEnv.mono (TyFunc(stringTy, unitTy))
          readStdinByte, TypeEnv.mono (TyFunc(unitTy, intTy))
          stringLength, TypeEnv.mono (TyFunc(stringTy, intTy))
          stringByteAt, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy ], intTy))
          stringSlice, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy; intTy ], stringTy))
          stringFromByte, TypeEnv.mono (TyFunc(intTy, stringTy))
          listCons, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; listOf element ], listOf element); Symbol = None } ]

    let names : Set<string> = all |> List.map fst |> Set.ofList

    let isIntrinsic (name: string) = names.Contains name

    /// The intrinsics that yield unit, so the backend knows to push a unit value after calling them.
    let returnsUnit (name: string) =
        all
        |> List.exists (fun (intrinsicName, scheme) ->
            intrinsicName = name
            && (match scheme.Type with
                | TyFunc(_, TyPrimitive "unit") -> true
                | _ -> false))

    /// The type environment a standard-library module is checked in.
    let typeEnv : TypeEnv =
        all |> List.fold (fun env (name, scheme) -> TypeEnv.extend name scheme env) TypeEnv.empty

/// The standard library: an ordinary package named `nanyx`, written in Nanyx (stdlib/nanyx/*.nyx at the
/// repository root) and embedded in the compiler assembly, so it is versioned with the compiler and the
/// published binary needs no files next to it. `import nanyx/<module>` reads the module's source from the
/// embedded resource "nanyx/<module>.nyx" and compiles it together with the importing program.
module StandardLibrary =
    let packageName = "nanyx"

    /// The prefix of an import path that refers to the standard library, e.g. `import nanyx/console`.
    let importPrefix = packageName + "/"

    /// The name of the standard-library module an import refers to (`console` for `nanyx/console`), if any.
    let tryModuleName (item: ImportItem) : string option =
        match item.Source with
        | ModuleImport name when name.StartsWith importPrefix -> Some (name.Substring importPrefix.Length)
        | _ -> None

    /// The module every other module imports without saying so (`nanyx/prelude`): what is in scope with no imports.
    let preludeModuleName = "prelude"

    let private resourceName (moduleName: string) = $"{packageName}/{moduleName}.nyx"

    let private assembly = Assembly.GetExecutingAssembly()

    /// The modules the library ships that a program imports by name, in alphabetical order. The prelude is left
    /// out, since it is imported automatically.
    let moduleNames : string list =
        assembly.GetManifestResourceNames()
        |> Array.choose (fun name ->
            if name.StartsWith importPrefix && name.EndsWith ".nyx" then
                Some (name.Substring(importPrefix.Length, name.Length - importPrefix.Length - ".nyx".Length))
            else
                None)
        |> Array.filter (fun name -> name <> preludeModuleName)
        |> Array.sort
        |> Array.toList

    /// The source of a standard-library module, or None when the library has no module of that name.
    let tryLoadSource (moduleName: string) : string option =
        use stream = assembly.GetManifestResourceStream(resourceName moduleName)
        if isNull stream then
            None
        else
            use reader = new StreamReader(stream)
            Some (reader.ReadToEnd())

    /// What a standard-library module is for, from the comment that opens it: `how a program talks to the terminal.`
    /// for `-- nanyx/console: how a program talks to the terminal.` None if it has no such comment.
    let trySummary (moduleName: string) : string option =
        let heading = $"-- {importPrefix}{moduleName}: "
        tryLoadSource moduleName
        |> Option.bind (fun source ->
            source.Split('\n')
            |> Array.map (fun line -> line.TrimEnd '\r')
            |> Array.tryFind (fun line -> line.StartsWith(heading, System.StringComparison.Ordinal))
            |> Option.map (fun line -> line.Substring heading.Length))
