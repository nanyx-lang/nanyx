namespace Nanyx.Runtime

open System

/// A frame of a program's stack where it stopped: the function, by the name its source gives it, and the file and line
/// of the code it was running, when the module's table of places has them, with the column there (0 for none), which a
/// crash's report names and a stack doesn't.
type Frame =
    { Name: string
      Place: (string * int) option
      Column: int }

    /// The frame as a line of a stack: `count (main.nyx:3)`, or the name alone without a place.
    override this.ToString() =
        match this.Place with
        | Some(file, line) -> $"{this.Name} ({file}:{line})"
        | None -> this.Name

/// The names a stack shows for the functions the backend names.
module FunctionNames =
    /// The name a function has in the source, from the name the backend gave it: `count`; `list_fold` for a library
    /// module's `fold`; `go` for a local `rec go` (`__rec_go_16`, see `LambdaLifting`); without the type arguments of a
    /// specialisation (`map__int`) or the suffix of a closure's owning entry (`__owning`, see `CoreIrClosureEntries`).
    /// A def a test build's root imports is named as its module names it (`__test0_perimeter` is `perimeter`, see
    /// `TestRootSyntax`). None for a lambda (`__lambda_3`), a runtime helper (`__nanyx_alloc`) and the other functions the
    /// backend makes, whose names all start with `__`.
    let rec source (name: string) =
        // The name without what follows its first `__`, the type arguments of a specialisation or `__owning`.
        let own (name: string) =
            match name.IndexOf "__" with
            | -1 -> name
            | index -> name.Substring(0, index)
        match name with
        | _ when String.IsNullOrEmpty name -> None
        | _ when name.StartsWith "__rec_" ->
            let numbered = own (name.Substring "__rec_".Length)
            match numbered.LastIndexOf '_' with
            | index when index > 0 -> Some(numbered.Substring(0, index))
            | _ -> None
        | _ when name.StartsWith "__fn_" -> Some(own (name.Substring "__fn_".Length))
        | Parser.Program.TestRootSyntax.Aliased name -> source name
        | _ when name.StartsWith "__" -> None
        | _ -> Some(own name)

    /// The name a stack shows for the function the backend named `name`: its name in the source (see `source`),
    /// `<lambda>` for a lambda and `<top level>` for a script's top-level code, which the compiler makes a def named
    /// `__script` (see `NyxCompiler.Scripts.definitionName`); none for another function the compiler made, such as a
    /// runtime helper or the start function, which runs the host, and which a stack leaves out.
    let shown (name: string) =
        match source name with
        | _ when isNull name -> None
        | _ when name = "__script" || name = "__fn___script" -> Some "<top level>"
        | Some name when name <> "" -> Some name
        | _ when name.StartsWith "__lambda" -> Some "<lambda>"
        | _ -> None

/// Where code was made from: for each call it was inlined through, innermost first, the function whose code it is, the
/// file, the line and the column (0 for none); the outermost has no function's name, since it is the function the code
/// is in.
type InlinedFrame = string option * string * int * int

/// Where a debug build's probe is (see `Transpiler.Wasm.CoreIrProbes`).
type ProbeKind =
    /// The start of the function the backend named so.
    | Entry of functionName: string
    /// A statement's start.
    | Statement
    /// A function's end.
    | Return

/// A debug build's probe: the number the program calls the runtime with, where it is, and its place.
type Probe =
    { Id: int
      Kind: ProbeKind
      Place: InlinedFrame list }

/// The table of places a module the compiler assembled carries in its custom section `nanyx.spans` (see
/// `Transpiler.Wasm.WasmBinary.spanTableName`, which says how it is laid out): for each stretch of code, by where it
/// starts in the module, the place of the source it was made from, the module's debug probes by their numbers, and
/// the file and line each function is defined at, by its name in the module's name section.
type SpanTable =
    private
        { Starts: int array
          Places: InlinedFrame list array
          Probes: Collections.Generic.Dictionary<int, Probe>
          Definitions: Collections.Generic.Dictionary<string, string * int>
          /// The files of the standard library's, as the frames name them.
          Library: Set<string>
          /// The name each function is written by in the source, by its name in the module's name section, where they
          /// differ: `perimeter` for `shapes_perimeter`, `list.fold` for `list_fold__int`.
          Written: Map<string, string> }

module SpanTable =
    /// The name of the custom section that holds the table.
    let sectionName = "nanyx.spans"

    /// Reads an unsigned LEB128 at `at` in `bytes`, giving it and where the next thing starts.
    let internal unsigned (bytes: byte array) (at: int) =
        let mutable value = 0UL
        let mutable shift = 0
        let mutable i = at
        let mutable going = true
        while going do
            let b = bytes.[i]
            value <- value ||| (uint64 (b &&& 0x7Fuy) <<< shift)
            shift <- shift + 7
            i <- i + 1
            going <- b &&& 0x80uy <> 0uy
        int value, i

    /// Reads a name, a length and that many bytes of UTF-8, at `at`.
    let internal name (bytes: byte array) (at: int) =
        let length, start = unsigned bytes at
        Text.Encoding.UTF8.GetString(bytes, start, length), start + length

    /// Where the contents of the custom section `named` of the module in the binary format `wasm` start, after its name,
    /// and where they end, if it has one.
    let internal customSection (named: string) (wasm: byte array) =
        // The sections follow the magic number and the version, each its id, its size and its contents.
        let rec find (at: int) =
            if at >= wasm.Length then None
            else
                let id = wasm.[at]
                let size, contents = unsigned wasm (at + 1)
                if id = 0uy && fst (name wasm contents) = named then Some(snd (name wasm contents), contents + size)
                else find (contents + size)
        find 8

    /// The table of the module in the binary format `wasm`, if it has one.
    let read (wasm: byte array) : SpanTable option =
        try
            customSection sectionName wasm
            |> Option.map (fun (start, finish) ->
                let mutable at = start
                let number () =
                    let value, next = unsigned wasm at
                    at <- next
                    value
                let strings =
                    Array.init (number ()) (fun _ ->
                        let text, next = name wasm at
                        at <- next
                        text)
                // Each frame's place: it, then the frames of the calls it was inlined through, which come before it.
                let frames = ResizeArray<InlinedFrame list>()
                for _ in 1 .. number () do
                    let functionName = number ()
                    let file = number ()
                    let line = number ()
                    let column = number ()
                    let outer = number ()
                    let frame = (if functionName = 0 then None else Some strings.[functionName - 1]), strings.[file - 1], line, column
                    frames.Add(frame :: (if outer = 0 then [] else frames.[outer - 1]))
                let mutable offset = 0
                let entries =
                    Array.init (number ()) (fun _ ->
                        offset <- offset + number ()
                        let place = number ()
                        offset, (if place = 0 then [] else frames.[place - 1]))
                    // Of two stretches that start at the same place, the second is the one that runs on.
                    |> Array.rev
                    |> Array.distinctBy fst
                    |> Array.rev
                let probes = Collections.Generic.Dictionary<int, Probe>()
                if at < finish then
                    for _ in 1 .. number () do
                        let id = number ()
                        let kind = number ()
                        let functionName = number ()
                        let place = number ()
                        let kind =
                            match kind with
                            | 1 -> Entry strings.[functionName - 1]
                            | 2 -> Statement
                            | _ -> Return
                        probes.[id] <- { Id = id; Kind = kind; Place = (if place = 0 then [] else frames.[place - 1]) }
                let definitions = Collections.Generic.Dictionary<string, string * int>()
                if at < finish then
                    for _ in 1 .. number () do
                        let functionName = strings.[number () - 1]
                        match frames.[number () - 1] with
                        | (_, file, line, _) :: _ -> definitions.[functionName] <- (file, line)
                        | [] -> ()
                let library =
                    if at < finish then Set.ofList [ for _ in 1 .. number () -> strings.[number () - 1] ] else Set.empty
                let written =
                    if at < finish then Map.ofList [ for _ in 1 .. number () -> let functionName = strings.[number () - 1] in functionName, strings.[number () - 1] ]
                    else Map.empty
                // A probe's entry names its function as it is written, as a stack does.
                let entryNamed (probe: Probe) =
                    match probe.Kind with
                    | Entry functionName when written.ContainsKey functionName -> { probe with Kind = Entry written.[functionName] }
                    | _ -> probe
                for id in List.ofSeq probes.Keys do
                    probes.[id] <- entryNamed probes.[id]
                { Starts = entries |> Array.map fst; Places = entries |> Array.map snd; Probes = probes; Definitions = definitions; Library = library; Written = written })
        with :? IndexOutOfRangeException -> None

    /// The name the function the module's name section calls `functionName` is written by in the source, if the table
    /// has it: it has none for one whose name is its name in the source, or one the compiler made up.
    let writtenName (table: SpanTable) (functionName: string) = table.Written |> Map.tryFind functionName

    /// `named`, the name a function's name gives (see `FunctionNames`), of the function the module's name section calls
    /// `functionName`, as the source writes it, where `table` says: by the function's name, or by `named`, which a
    /// function made from a definition, as a closure's entry (`__fn_main_ignore`), shares with it.
    let private written (table: SpanTable option) (functionName: string) (named: string option) =
        match table with
        | None -> named
        | Some table ->
            match table.Written |> Map.tryFind functionName with
            | Some found -> Some found
            | None -> named |> Option.map (fun name -> table.Written |> Map.tryFind name |> Option.defaultValue name)

    /// The name a stack shows for the function the module's name section calls `functionName`: as it is written in the
    /// source, if the table says (see `Written`), else as `FunctionNames.shown` gives it; none for one a stack leaves out.
    let shownName (table: SpanTable option) (functionName: string) =
        written table functionName (FunctionNames.shown functionName)

    /// The name the source gives the function the module's name section calls `functionName`, as `FunctionNames.source`
    /// does, but as it is written where the table says.
    let sourceName (table: SpanTable option) (functionName: string) =
        written table functionName (FunctionNames.source functionName)

    /// Whether `file`, as a frame names it, is a file of the standard library's.
    let isLibrary (table: SpanTable) (file: string) = table.Library.Contains file

    /// The lines of each file, as the frames name it, that a statement probe is on, by its innermost place: the lines a
    /// debugger can stop at.
    let statementLines (table: SpanTable) : Map<string, Set<int>> =
        table.Probes.Values
        |> Seq.choose (fun probe ->
            match probe.Kind, probe.Place with
            | Statement, (_, file, line, _) :: _ -> Some(file, line)
            | _ -> None)
        |> Seq.groupBy fst
        |> Seq.map (fun (file, lines) -> file, lines |> Seq.map snd |> Set.ofSeq)
        |> Map.ofSeq

    /// The probe of the number `id`, if the module has one.
    let probe (table: SpanTable) (id: int) =
        match table.Probes.TryGetValue id with
        | true, found -> Some found
        | _ -> None

    /// The file and line of the definition of the function the module's name section calls `functionName`, if the
    /// table has one: a function the compiler made up, such as a runtime helper, has none.
    let definitionOf (table: SpanTable) (functionName: string) =
        match table.Definitions.TryGetValue functionName with
        | true, found -> Some found
        | _ -> None

    /// The place of the code at `offset` in the module, the place of the stretch it is in: empty when it has none.
    let placeAt (table: SpanTable) (offset: int) : InlinedFrame list =
        match Array.BinarySearch(table.Starts, offset) with
        | found when found >= 0 -> table.Places.[found]
        // Not the start of a stretch: the complement of the index of the next one.
        | missing when ~~~missing > 0 -> table.Places.[~~~missing - 1]
        | _ -> []
