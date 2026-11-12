namespace NyxCompiler

open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Parser.Program

/// The compiler intrinsics: the few operations the standard library is written over because they cannot
/// be expressed in Nanyx itself. This is the one place that lists them and their types.
///
/// They are in scope only while a standard-library module is being checked, never in user code. The
/// WebAssembly backend lowers a call to one of them onto a helper function it emits with the module
/// (see `runtimeHelpers` in Transpiler.Wasm.WasmRuntime).
module Intrinsics =
    /// Every intrinsic name starts with this, which keeps them out of the way of ordinary identifiers.
    let prefix = "__nanyx_"

    let private stringTy = TyPrimitive "string"
    let private intTy = TyPrimitive "int"
    let private floatTy = TyPrimitive "float"
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
    /// The square root of a float (NaN for a negative one).
    let floatSqrt = prefix + "float_sqrt"
    /// The largest whole float not above a float.
    let floatFloor = prefix + "float_floor"
    /// The smallest whole float not below a float.
    let floatCeil = prefix + "float_ceil"
    /// A float with its fraction dropped, towards zero.
    let floatTrunc = prefix + "float_trunc"
    /// A whole float as an int. A float that is NaN, infinite or out of the range of `int` stops the program.
    let floatToInt = prefix + "float_to_int"
    /// An int as a float: the nearest one, which is exact up to 2^53. It never fails.
    let intToFloat = prefix + "int_to_float"
    /// An int as a value of the sized integer type it is taken as (see `SizedInteger`), which stops the program when it
    /// does not fit that type, or as the nearest `F32`.
    let fromInt = prefix + "from_int"
    /// `+`, `-` and `*` on two integers of the type they are taken at that keep the low bits of the result: they wrap
    /// around past the type's range rather than stop the program.
    let wrappingAdd = prefix + "wrapping_add"
    let wrappingSub = prefix + "wrapping_sub"
    let wrappingMul = prefix + "wrapping_mul"
    /// An integer as one of the integer type it is taken as: its bits, sign- or zero-extended or cut to the target's.
    /// The prelude's conversions check first that the value fits.
    let intCast = prefix + "int_cast"
    /// A number as the floating-point type it is taken as, `float` or `F32`: the nearest one.
    let toFloat = prefix + "to_float"
    /// Two values of a type the compiler orders (see `WellKnownContexts.ordering`) put in order by their structure:
    /// negative when the first comes first, 0 when they are equal, positive when it comes after. Floats are in a
    /// total order, with NaN after every other float.
    let compare = prefix + "compare"
    /// The hash of a value of a type the compiler hashes (see `WellKnownContexts.hashing`), by its structure: values
    /// that `==` compares equal by their structure have the same hash.
    let hash = prefix + "hash"
    /// A map holding a list's entries, each a key and its value. The list is what the map holds, as it is: the prelude
    /// keeps each key in it once.
    let mapOfEntries = prefix + "map_of_entries"
    /// The entries a map holds, as the list it was made of.
    let mapEntries = prefix + "map_entries"
    /// A set holding a list's elements. The list is what the set holds, as it is: the prelude keeps each element in
    /// it once.
    let setOfElements = prefix + "set_of_elements"
    /// The elements a set holds, as the list it was made of.
    let setElements = prefix + "set_elements"

    /// Ends the program with an exit code. It never returns, so its result is `undefined`.
    let exit = prefix + "exit"

    /// The intrinsics that give back their argument as a value of another type, which the backend holds the same way.
    let reinterpretations : Set<string> = set [ mapOfEntries; mapEntries; setOfElements; setElements ]

    let private keyVar = { Id = -2; Name = Some "k" }
    let private valueVar = { Id = -3; Name = Some "v" }
    let private entries = listOf (tuple [ TyVar keyVar; TyVar valueVar ])
    let private map = Maps.ofTypes (TyVar keyVar) (TyVar valueVar)
    let private overEntries (ty: Ty) = { Quantified = set [ keyVar.Id; valueVar.Id ]; Type = ty; Symbol = None }

    /// Every intrinsic with its type scheme.
    let all : (string * TypeScheme) list =
        [ writeStdout, TypeEnv.mono (TyFunc(stringTy, unitTy, []))
          writeStderr, TypeEnv.mono (TyFunc(stringTy, unitTy, []))
          readStdinByte, TypeEnv.mono (TyFunc(unitTy, intTy, []))
          exit, TypeEnv.mono (TyFunc(intTy, Unifier.bottomType, []))
          stringLength, TypeEnv.mono (TyFunc(stringTy, intTy, []))
          stringByteAt, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy ], intTy, []))
          stringSlice, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy; intTy ], stringTy, []))
          stringFromByte, TypeEnv.mono (TyFunc(intTy, stringTy, []))
          floatSqrt, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatFloor, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatCeil, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatTrunc, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatToInt, TypeEnv.mono (TyFunc(floatTy, intTy, []))
          intToFloat, TypeEnv.mono (TyFunc(intTy, floatTy, []))
          fromInt, { Quantified = set [ elementVar.Id ]; Type = TyFunc(intTy, element, []); Symbol = None }
          wrappingAdd, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], element, []); Symbol = None }
          wrappingSub, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], element, []); Symbol = None }
          wrappingMul, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], element, []); Symbol = None }
          intCast, { Quantified = set [ elementVar.Id; valueVar.Id ]; Type = TyFunc(element, TyVar valueVar, []); Symbol = None }
          toFloat, { Quantified = set [ elementVar.Id; valueVar.Id ]; Type = TyFunc(element, TyVar valueVar, []); Symbol = None }
          mapOfEntries, overEntries (TyFunc(entries, map, []))
          mapEntries, overEntries (TyFunc(map, entries, []))
          setOfElements, { Quantified = set [ elementVar.Id ]; Type = TyFunc(listOf element, Sets.ofType element, []); Symbol = None }
          setElements, { Quantified = set [ elementVar.Id ]; Type = TyFunc(Sets.ofType element, listOf element, []); Symbol = None }
          listCons, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; listOf element ], listOf element, []); Symbol = None }
          compare, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], intTy, []); Symbol = None }
          hash, { Quantified = set [ elementVar.Id ]; Type = TyFunc(element, intTy, []); Symbol = None } ]

    let names : Set<string> = all |> List.map fst |> Set.ofList

    let isIntrinsic (name: string) = names.Contains name

    /// The intrinsics that yield unit, so the backend knows to push a unit value after calling them.
    let returnsUnit (name: string) =
        all
        |> List.exists (fun (intrinsicName, scheme) ->
            intrinsicName = name
            && (match scheme.Type with
                | TyFunc(_, TyPrimitive "unit", _) -> true
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

    /// The name of a module's embedded resource, which is the file its declarations' spans name: `nanyx/list.nyx`.
    let resourceName (moduleName: string) = $"{packageName}/{moduleName}.nyx"

    let private assembly = Assembly.GetExecutingAssembly()

    /// The text of the library's file `name` (its resource name, as `nanyx/list.nyx`), if it has one.
    let tryLoadFile (name: string) : string option =
        use stream = assembly.GetManifestResourceStream name
        if isNull stream then
            None
        else
            use reader = new StreamReader(stream)
            Some (reader.ReadToEnd())

    /// A file's module declaration, `module list` or `@partial module list`: the first line that is one.
    let private declaration = Regex(@"^(@partial\s+)?module\s+([A-Za-z_][A-Za-z0-9_.]*)\s*$", RegexOptions.Multiline)

    /// The library's files, each as its resource name with the module it declares. A module is in the file named
    /// for it (`nanyx/list.nyx`), and may have more files, each starting `@partial module list`.
    let private files : Lazy<(string * string) list> =
        lazy
            (assembly.GetManifestResourceNames()
             |> Array.filter (fun name -> name.StartsWith importPrefix && name.EndsWith ".nyx")
             |> Array.sort
             |> Array.toList
             |> List.map (fun name ->
                 let fileName = name.Substring(importPrefix.Length, name.Length - importPrefix.Length - ".nyx".Length)
                 let declared = tryLoadFile name |> Option.map declaration.Match |> Option.filter _.Success
                 name, declared |> Option.map (fun m -> m.Groups[2].Value) |> Option.defaultValue fileName))

    /// The modules the library ships that a program imports by name, in alphabetical order. The prelude is left
    /// out, since it is imported automatically.
    let moduleNames : string list =
        files.Value |> List.map snd |> List.distinct |> List.filter (fun name -> name <> preludeModuleName) |> List.sort

    /// Whether `file` holds one of the library's own modules: the file an imported one is checked as
    /// (`nanyx/list.nyx`), or a file of its source on disk (`stdlib/nanyx/list.nyx`), as a checkout or the editor has it.
    let isModuleFile (file: string) =
        not (System.String.IsNullOrEmpty file)
        && Path.GetExtension file = ".nyx"
        && Path.GetFileName(Path.GetDirectoryName file) = packageName
        && files.Value |> List.exists (fun (name, _) -> name = importPrefix + Path.GetFileName file)

    /// The source of a standard-library module, or None when the library has no module of that name.
    let tryLoadSource (moduleName: string) : string option =
        tryLoadFile (resourceName moduleName)

    /// The resource names of every file of the module `moduleName`: `nanyx/prelude.nyx`, `nanyx/prelude-lists.nyx`, ...
    let moduleFiles (moduleName: string) : string list =
        files.Value |> List.filter (fun (_, declared) -> declared = moduleName) |> List.map fst

    /// The module's files other than the one named for it, each as its resource name and its source.
    let otherFragments (moduleName: string) : (string * string) list =
        files.Value
        |> List.filter (fun (name, declared) -> declared = moduleName && name <> resourceName moduleName)
        |> List.choose (fun (name, _) -> tryLoadFile name |> Option.map (fun source -> name, source))

    /// Every file of a standard-library module, the one named for it first, each as its resource name and its source.
    let moduleSources (moduleName: string) : (string * string) list =
        (tryLoadSource moduleName |> Option.map (fun source -> resourceName moduleName, source) |> Option.toList)
        @ otherFragments moduleName

/// The names the standard library keeps for itself: those that start with `__`, as the helpers syntax is desugared to
/// (`__equals` for `==`, `__pow` for `**`, `__interpolate` for a hole in a string). The intrinsics (see `Intrinsics`)
/// start with it too, but are never in scope outside the standard library to begin with.
module ReservedNames =
    let prefix = "__"

    /// Whether the editor keeps `name`, declared at `declaration`, out of sight in the module being edited: a reserved
    /// name the standard library declares, which completion never offers, the info view never lists, and hover, go to
    /// definition and doc comment links never lead to. A module's own declarations are never hidden from it, so a
    /// standard-library file being edited still sees its own.
    let isHiddenName (name: string) (declaration: SourceSpan) =
        name.StartsWith prefix && declaration.File.StartsWith StandardLibrary.importPrefix

    /// `isHiddenName` for a symbol's name and declaration.
    let isHidden (symbol: Symbol) = isHiddenName symbol.Name symbol.Declaration
