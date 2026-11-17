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
    let private i32Ty = SizedInteger.ty SizedInteger.I32

    /// The input type of a function taking several arguments: a positional record, as inference builds it.
    let private tuple (items: Ty list) =
        TyRecord(items |> List.mapi (fun index ty -> string index, ty) |> Map.ofList, FieldOrder.none)

    let private elementVar = TyVar.ordinary (-1) (Some "a")
    let private element = TyVar elementVar
    let private listOf = Lists.ofType

    /// Writes a string to standard error, with nothing added: what `crash` writes.
    let writeStderr = prefix + "write_stderr"
    /// The number of bytes in a string.
    let stringLength = prefix + "string_length"
    /// The byte at an index of a string, or -1 when the index is out of range.
    let stringByteAt = prefix + "string_byte_at"
    /// A new string holding `count` bytes of a string from `start`; both are clamped to the string.
    let stringSlice = prefix + "string_slice"
    /// A new one-byte string.
    let stringFromByte = prefix + "string_from_byte"
    /// A new string holding a list's strings with a separator between each pair, each copied once.
    let stringJoin = prefix + "string_join"
    /// A new string holding a string's bytes, with `delta` added to each from `low` to `high`, in one allocation: what
    /// changes the case of the ASCII letters.
    let stringShiftBytes = prefix + "string_shift_bytes"
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
    /// The bitwise operations on ints: and, or, exclusive or and not.
    let bitAnd = prefix + "bit_and"
    let bitOr = prefix + "bit_or"
    let bitXor = prefix + "bit_xor"
    let bitNot = prefix + "bit_not"
    /// An int's bits moved by a count, taken modulo 64: left, right keeping the sign, and right filling with zeros.
    let shiftLeft = prefix + "shift_left"
    let shiftRight = prefix + "shift_right"
    let shiftRightUnsigned = prefix + "shift_right_unsigned"
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
    /// An array holding a list's elements, in order, each laid out flat in one block (see `Arrays`).
    let arrayOfList = prefix + "array_of_list"
    /// The elements an array holds, as a list, in order.
    let arrayToList = prefix + "array_to_list"
    /// How many elements an array holds.
    let arrayLength = prefix + "array_length"
    /// The element of an array at an index, which the standard library has checked is in range.
    let arrayGet = prefix + "array_get"
    /// A new array of a length (none for a length of 0 or less) whose elements are not yet written, and its element at an
    /// index written for the first time, which the array takes over: how the standard library makes an array in one
    /// block, writing each element once before anything reads the array. The standard library checks each index is in
    /// range.
    let arrayNew = prefix + "array_new"
    let arrayWrite = prefix + "array_write"
    /// A new box holding a value (see `Boxes`), the value a box holds, and a box changed to hold another value, which
    /// gives up the one it held.
    let boxNew = prefix + "box_new"
    let boxGet = prefix + "box_get"
    let boxSet = prefix + "box_set"
    /// A new mutable array of a list's elements (see `MutableArrays`), its elements as a list, its length and its
    /// element at an index, as for an array, and its element at an index changed, which gives up the one it held. The
    /// standard library checks each index is in range.
    let marrayOfList = prefix + "marray_of_list"
    let marrayToList = prefix + "marray_to_list"
    let marrayLength = prefix + "marray_length"
    let marrayGet = prefix + "marray_get"
    let marraySet = prefix + "marray_set"
    /// A new mutable array of a length whose elements are not yet written, and its element at an index written for the
    /// first time, as `arrayNew` and `arrayWrite` are for an array: how the standard library makes a mutable array in
    /// one block.
    let marrayNew = prefix + "marray_new"
    let marrayWrite = prefix + "marray_write"
    /// Whether a value is held exclusively: it takes over a reference to the value and gives it up, and says whether that
    /// was the only one, as Lean's `isExclusive` does. A caller that holds the only reference can hand over what the value
    /// holds instead of copying it, as `MList.toList` does. The value is one that is always on the heap, such as a box.
    let isExclusive = prefix + "is_exclusive"
    /// A list's first cell changed in place to have another list as its tail, giving up the tail it had: how the standard
    /// library builds a list front to back, in cells nothing outside it can see yet.
    let listSetTail = prefix + "list_set_tail"

    /// The array intrinsic a mutable array's is, since the backend holds the two the same way: a mutable array's
    /// elements are read as an array's are.
    let asArrayIntrinsic (name: string) =
        if name = marrayOfList then arrayOfList
        elif name = marrayToList then arrayToList
        elif name = marrayLength then arrayLength
        elif name = marrayGet then arrayGet
        elif name = marrayNew then arrayNew
        elif name = marrayWrite then arrayWrite
        else name

    /// Ends the program with an exit code. It never returns, so its result is `undefined`.
    let exit = prefix + "exit"

    /// A host's access to linear memory (see `nanyx/host.memory`), at addresses that are `I32`s. A new block of a number
    /// of bytes, from the allocator every object comes from, and the block given back.
    let memoryAllocate = prefix + "memory_allocate"
    let memoryFree = prefix + "memory_free"
    /// The byte (as an `I32`, 0 to 255), the `I32` and the `I64` at an address, and each written there.
    let memoryReadByte = prefix + "memory_read_byte"
    let memoryWriteByte = prefix + "memory_write_byte"
    let memoryReadI32 = prefix + "memory_read_i32"
    let memoryWriteI32 = prefix + "memory_write_i32"
    let memoryReadI64 = prefix + "memory_read_i64"
    let memoryWriteI64 = prefix + "memory_write_i64"
    /// Where a string's bytes are, and how many there are, as `I32`s.
    let stringAddress = prefix + "string_address"
    let stringByteLength = prefix + "string_byte_length"
    /// A new string holding a copy of the bytes at an address, as many as a length.
    let stringFromMemory = prefix + "string_from_memory"
    /// Does nothing but read a string, so that it is alive until then: its address may be in memory a foreign function
    /// reads, which reference counting does not follow.
    let keepAlive = prefix + "keep_alive"
    /// The address of the block the module sets aside for its host, all zero when the program starts.
    let hostState = prefix + "host_state"

    /// The intrinsics that give back their argument as a value of another type, which the backend holds the same way.
    let reinterpretations : Set<string> = set [ mapOfEntries; mapEntries; setOfElements; setElements ]

    let private keyVar = TyVar.ordinary (-2) (Some "k")
    let private valueVar = TyVar.ordinary (-3) (Some "v")
    let private entries = listOf (tuple [ TyVar keyVar; TyVar valueVar ])
    let private map = Maps.ofTypes (TyVar keyVar) (TyVar valueVar)
    let private overEntries (ty: Ty) = { Quantified = set [ keyVar.Id; valueVar.Id ]; Type = ty; Symbol = None; ProvidedBy = None }

    /// Every intrinsic with its type scheme.
    let all : (string * TypeScheme) list =
        [ writeStderr, TypeEnv.mono (TyFunc(stringTy, unitTy, []))
          exit, TypeEnv.mono (TyFunc(intTy, Unifier.bottomType, []))
          memoryAllocate, TypeEnv.mono (TyFunc(i32Ty, i32Ty, []))
          memoryFree, TypeEnv.mono (TyFunc(i32Ty, unitTy, []))
          memoryReadByte, TypeEnv.mono (TyFunc(i32Ty, i32Ty, []))
          memoryWriteByte, TypeEnv.mono (TyFunc(tuple [ i32Ty; i32Ty ], unitTy, []))
          memoryReadI32, TypeEnv.mono (TyFunc(i32Ty, i32Ty, []))
          memoryWriteI32, TypeEnv.mono (TyFunc(tuple [ i32Ty; i32Ty ], unitTy, []))
          memoryReadI64, TypeEnv.mono (TyFunc(i32Ty, intTy, []))
          memoryWriteI64, TypeEnv.mono (TyFunc(tuple [ i32Ty; intTy ], unitTy, []))
          stringAddress, TypeEnv.mono (TyFunc(stringTy, i32Ty, []))
          stringByteLength, TypeEnv.mono (TyFunc(stringTy, i32Ty, []))
          stringFromMemory, TypeEnv.mono (TyFunc(tuple [ i32Ty; i32Ty ], stringTy, []))
          keepAlive, TypeEnv.mono (TyFunc(stringTy, unitTy, []))
          hostState, TypeEnv.mono (TyFunc(unitTy, i32Ty, []))
          stringLength, TypeEnv.mono (TyFunc(stringTy, intTy, []))
          stringByteAt, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy ], intTy, []))
          stringSlice, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy; intTy ], stringTy, []))
          stringFromByte, TypeEnv.mono (TyFunc(intTy, stringTy, []))
          stringJoin, TypeEnv.mono (TyFunc(tuple [ listOf stringTy; stringTy ], stringTy, []))
          stringShiftBytes, TypeEnv.mono (TyFunc(tuple [ stringTy; intTy; intTy; intTy ], stringTy, []))
          floatSqrt, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatFloor, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatCeil, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatTrunc, TypeEnv.mono (TyFunc(floatTy, floatTy, []))
          floatToInt, TypeEnv.mono (TyFunc(floatTy, intTy, []))
          intToFloat, TypeEnv.mono (TyFunc(intTy, floatTy, []))
          fromInt, { Quantified = set [ elementVar.Id ]; Type = TyFunc(intTy, element, []); Symbol = None; ProvidedBy = None }
          wrappingAdd, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], element, []); Symbol = None; ProvidedBy = None }
          wrappingSub, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], element, []); Symbol = None; ProvidedBy = None }
          wrappingMul, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], element, []); Symbol = None; ProvidedBy = None }
          bitAnd, TypeEnv.mono (TyFunc(tuple [ intTy; intTy ], intTy, []))
          bitOr, TypeEnv.mono (TyFunc(tuple [ intTy; intTy ], intTy, []))
          bitXor, TypeEnv.mono (TyFunc(tuple [ intTy; intTy ], intTy, []))
          bitNot, TypeEnv.mono (TyFunc(intTy, intTy, []))
          shiftLeft, TypeEnv.mono (TyFunc(tuple [ intTy; intTy ], intTy, []))
          shiftRight, TypeEnv.mono (TyFunc(tuple [ intTy; intTy ], intTy, []))
          shiftRightUnsigned, TypeEnv.mono (TyFunc(tuple [ intTy; intTy ], intTy, []))
          intCast, { Quantified = set [ elementVar.Id; valueVar.Id ]; Type = TyFunc(element, TyVar valueVar, []); Symbol = None; ProvidedBy = None }
          toFloat, { Quantified = set [ elementVar.Id; valueVar.Id ]; Type = TyFunc(element, TyVar valueVar, []); Symbol = None; ProvidedBy = None }
          mapOfEntries, overEntries (TyFunc(entries, map, []))
          mapEntries, overEntries (TyFunc(map, entries, []))
          setOfElements, { Quantified = set [ elementVar.Id ]; Type = TyFunc(listOf element, Sets.ofType element, []); Symbol = None; ProvidedBy = None }
          setElements, { Quantified = set [ elementVar.Id ]; Type = TyFunc(Sets.ofType element, listOf element, []); Symbol = None; ProvidedBy = None }
          arrayOfList, { Quantified = set [ elementVar.Id ]; Type = TyFunc(listOf element, Arrays.ofType element, []); Symbol = None; ProvidedBy = None }
          arrayToList, { Quantified = set [ elementVar.Id ]; Type = TyFunc(Arrays.ofType element, listOf element, []); Symbol = None; ProvidedBy = None }
          arrayLength, { Quantified = set [ elementVar.Id ]; Type = TyFunc(Arrays.ofType element, intTy, []); Symbol = None; ProvidedBy = None }
          arrayGet, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ Arrays.ofType element; intTy ], element, []); Symbol = None; ProvidedBy = None }
          boxNew, { Quantified = set [ elementVar.Id ]; Type = TyFunc(element, Boxes.ofType element, []); Symbol = None; ProvidedBy = None }
          boxGet, { Quantified = set [ elementVar.Id ]; Type = TyFunc(Boxes.ofType element, element, []); Symbol = None; ProvidedBy = None }
          boxSet, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ Boxes.ofType element; element ], unitTy, []); Symbol = None; ProvidedBy = None }
          marrayOfList, { Quantified = set [ elementVar.Id ]; Type = TyFunc(listOf element, MutableArrays.ofType element, []); Symbol = None; ProvidedBy = None }
          marrayToList, { Quantified = set [ elementVar.Id ]; Type = TyFunc(MutableArrays.ofType element, listOf element, []); Symbol = None; ProvidedBy = None }
          marrayLength, { Quantified = set [ elementVar.Id ]; Type = TyFunc(MutableArrays.ofType element, intTy, []); Symbol = None; ProvidedBy = None }
          marrayGet, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ MutableArrays.ofType element; intTy ], element, []); Symbol = None; ProvidedBy = None }
          marraySet, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ MutableArrays.ofType element; intTy; element ], unitTy, []); Symbol = None; ProvidedBy = None }
          marrayNew, { Quantified = set [ elementVar.Id ]; Type = TyFunc(intTy, MutableArrays.ofType element, []); Symbol = None; ProvidedBy = None }
          marrayWrite, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ MutableArrays.ofType element; intTy; element ], unitTy, []); Symbol = None; ProvidedBy = None }
          arrayNew, { Quantified = set [ elementVar.Id ]; Type = TyFunc(intTy, Arrays.ofType element, []); Symbol = None; ProvidedBy = None }
          arrayWrite, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ Arrays.ofType element; intTy; element ], unitTy, []); Symbol = None; ProvidedBy = None }
          isExclusive, { Quantified = set [ elementVar.Id ]; Type = TyFunc(element, TyPrimitive "bool", []); Symbol = None; ProvidedBy = None }
          listSetTail, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ listOf element; listOf element ], unitTy, []); Symbol = None; ProvidedBy = None }
          listCons, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; listOf element ], listOf element, []); Symbol = None; ProvidedBy = None }
          compare, { Quantified = set [ elementVar.Id ]; Type = TyFunc(tuple [ element; element ], intTy, []); Symbol = None; ProvidedBy = None }
          hash, { Quantified = set [ elementVar.Id ]; Type = TyFunc(element, intTy, []); Symbol = None; ProvidedBy = None } ]

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

    /// The prefix of an import path that refers to the standard library, e.g. `import nanyx/text`.
    let importPrefix = packageName + "/"

    /// The name of the standard-library module an import refers to (`text` for `nanyx/text`), if any.
    let tryModuleName (item: ImportItem) : string option =
        match item.Source with
        | ModuleImport name when name.StartsWith importPrefix -> Some (name.Substring importPrefix.Length)
        | _ -> None

    /// The module every other module imports without saying so (`nanyx/prelude`): what is in scope with no imports.
    let preludeModuleName = ModuleOrigin.preludeName

    /// The name of a module's embedded resource, which is the file its declarations' spans name: `nanyx/list.nyx`.
    let resourceName (moduleName: string) = $"{packageName}/{moduleName}.nyx"

    let private assembly = Assembly.GetExecutingAssembly()

    /// The text of each of the library's files read so far, by resource name: the library is embedded, so it never
    /// changes while the process runs, and the editor asks for a file's text for every doc comment it shows.
    let private loaded = System.Collections.Concurrent.ConcurrentDictionary<string, string option>()

    /// The text of the library's file `name` (its resource name, as `nanyx/list.nyx`), if it has one. It is read once
    /// per process.
    let tryLoadFile (name: string) : string option =
        loaded.GetOrAdd(name, fun name ->
            use stream = assembly.GetManifestResourceStream name
            if isNull stream then
                None
            else
                use reader = new StreamReader(stream)
                Some (reader.ReadToEnd()))

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

    /// The module the library's file `name` (its resource name, as `nanyx/prelude-lists.nyx`) declares, if it has one.
    let tryModuleOfFile (name: string) : string option =
        files.Value |> List.tryFind (fun (file, _) -> file = name) |> Option.map snd

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
