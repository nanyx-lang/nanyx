namespace Nanyx.Runtime

open System

/// A frame of a program's stack where it stopped: the function, by the name its source gives it, and the file and line
/// of the code it was running, when the module's table of places has them.
type Frame =
    { Name: string
      Place: (string * int) option }

    /// The frame as a line of a stack: `count (main.nyx:3)`, or the name alone without a place.
    override this.ToString() =
        match this.Place with
        | Some(file, line) -> $"{this.Name} ({file}:{line})"
        | None -> this.Name

/// Where code was made from: for each call it was inlined through, innermost first, the function whose code it is, the
/// file and the line; the outermost has no function's name, since it is the function the code is in.
type InlinedFrame = string option * string * int

/// The table of places a module the compiler assembled carries in its custom section `nanyx.spans` (see
/// `Transpiler.Wasm.WasmBinary.spanTableName`, which says how it is laid out): for each stretch of code, by where it
/// starts in the module, the place of the source it was made from.
type SpanTable =
    private
        { Starts: int array
          Places: InlinedFrame list array }

module SpanTable =
    /// The name of the custom section that holds the table.
    let sectionName = "nanyx.spans"

    /// Reads an unsigned LEB128 at `at` in `bytes`, giving it and where the next thing starts.
    let private unsigned (bytes: byte array) (at: int) =
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
    let private name (bytes: byte array) (at: int) =
        let length, start = unsigned bytes at
        Text.Encoding.UTF8.GetString(bytes, start, length), start + length

    /// The table of the module in the binary format `wasm`, if it has one.
    let read (wasm: byte array) : SpanTable option =
        // The sections follow the magic number and the version, each its id, its size and its contents.
        let rec find (at: int) =
            if at >= wasm.Length then None
            else
                let id = wasm.[at]
                let size, contents = unsigned wasm (at + 1)
                if id = 0uy && fst (name wasm contents) = sectionName then Some(snd (name wasm contents))
                else find (contents + size)
        try
            find 8
            |> Option.map (fun start ->
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
                    let outer = number ()
                    let frame = (if functionName = 0 then None else Some strings.[functionName - 1]), strings.[file - 1], line
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
                { Starts = entries |> Array.map fst; Places = entries |> Array.map snd })
        with :? IndexOutOfRangeException -> None

    /// The place of the code at `offset` in the module, the place of the stretch it is in: empty when it has none.
    let placeAt (table: SpanTable) (offset: int) : InlinedFrame list =
        match Array.BinarySearch(table.Starts, offset) with
        | found when found >= 0 -> table.Places.[found]
        // Not the start of a stretch: the complement of the index of the next one.
        | missing when ~~~missing > 0 -> table.Places.[~~~missing - 1]
        | _ -> []
