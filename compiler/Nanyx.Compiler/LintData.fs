namespace NyxCompiler

open System
open System.IO
open System.Text

/// A value that crosses between the compiler and a linter written in Nanyx: the module it checks, the answers to its
/// questions, and its reports. The linter host turns data into `nanyx.analysis`'s types through their type descriptions,
/// matching a record's fields and a union's tags by name, so a record here has the fields of the Nanyx type it stands
/// for, a tag its tag, and a tuple the fields `0`, `1`, ... (see the Linters page in Work in progress).
type LintData =
    | DInt of int64
    | DFloat of float
    | DText of string
    | DBool of bool
    | DUnit
    | DList of LintData list
    | DRecord of (string * LintData) list
    /// A tag and its payload: `DUnit` for a tag without one, and a record for one of several values.
    | DTag of string * LintData

module LintData =
    /// The tag `name` with no payload.
    let tag (name: string) = DTag(name, DUnit)

    /// `#some(value)` for a value, and `noneTag` for none.
    let option (noneTag: string) (value: LintData option) =
        match value with
        | Some value -> DTag("some", value)
        | None -> tag noneTag

    /// A tuple of `items`: a record whose fields are their positions.
    let tuple (items: LintData list) = DRecord(items |> List.mapi (fun index item -> string index, item))

    // The binary form: a byte for the kind of value, then the value. An int is 8 bytes and a length 4, little-endian;
    // text is its length in bytes then its UTF-8; a float is the text that round-trips it, which Nanyx parses.
    [<Literal>]
    let private intKind = 0uy
    [<Literal>]
    let private floatKind = 1uy
    [<Literal>]
    let private textKind = 2uy
    [<Literal>]
    let private boolKind = 3uy
    [<Literal>]
    let private unitKind = 4uy
    [<Literal>]
    let private listKind = 5uy
    [<Literal>]
    let private recordKind = 6uy
    [<Literal>]
    let private tagKind = 7uy

    let private writeText (writer: BinaryWriter) (text: string) =
        let bytes = Encoding.UTF8.GetBytes text
        writer.Write(bytes.Length)
        writer.Write(bytes)

    let rec private write (writer: BinaryWriter) (data: LintData) =
        match data with
        | DInt value ->
            writer.Write(intKind)
            writer.Write(value)
        | DFloat value ->
            writer.Write(floatKind)
            writeText writer (value.ToString("R", Globalization.CultureInfo.InvariantCulture))
        | DText text ->
            writer.Write(textKind)
            writeText writer text
        | DBool value ->
            writer.Write(boolKind)
            writer.Write(if value then 1uy else 0uy)
        | DUnit -> writer.Write(unitKind)
        | DList items ->
            writer.Write(listKind)
            writer.Write(items.Length)
            items |> List.iter (write writer)
        | DRecord fields ->
            writer.Write(recordKind)
            writer.Write(fields.Length)
            for name, value in fields do
                writeText writer name
                write writer value
        | DTag(name, payload) ->
            writer.Write(tagKind)
            writeText writer name
            write writer payload

    /// `data` in its binary form.
    let toBytes (data: LintData) : byte array =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8)
        write writer data
        writer.Flush()
        stream.ToArray()

    let private readText (reader: BinaryReader) =
        let length = reader.ReadInt32()
        Encoding.UTF8.GetString(reader.ReadBytes length)

    let rec private read (reader: BinaryReader) : LintData =
        match reader.ReadByte() with
        | kind when kind = intKind -> DInt(reader.ReadInt64())
        | kind when kind = floatKind -> DFloat(Double.Parse(readText reader, Globalization.CultureInfo.InvariantCulture))
        | kind when kind = textKind -> DText(readText reader)
        | kind when kind = boolKind -> DBool(reader.ReadByte() <> 0uy)
        | kind when kind = unitKind -> DUnit
        | kind when kind = listKind -> DList(List.init (reader.ReadInt32()) (fun _ -> read reader))
        | kind when kind = recordKind ->
            DRecord(List.init (reader.ReadInt32()) (fun _ ->
                let name = readText reader
                name, read reader))
        | kind when kind = tagKind ->
            let name = readText reader
            DTag(name, read reader)
        | kind -> raise (InvalidDataException $"No value starts with the byte {kind}")

    /// The data `bytes` is the binary form of, or why it isn't any.
    let ofBytes (bytes: byte array) : Result<LintData, string> =
        try
            use reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8)
            let data = read reader
            if reader.BaseStream.Position = reader.BaseStream.Length then Ok data
            else Error $"{reader.BaseStream.Length - reader.BaseStream.Position} bytes are left after the value"
        with
        | :? EndOfStreamException -> Error "The value ends early"
        | :? InvalidDataException as ex -> Error ex.Message
        | :? FormatException as ex -> Error ex.Message

    /// The field `name` of the record `data`.
    let field (name: string) (data: LintData) : LintData option =
        match data with
        | DRecord fields -> fields |> List.tryFind (fst >> (=) name) |> Option.map snd
        | _ -> None
