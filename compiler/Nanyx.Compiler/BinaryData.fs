namespace NyxCompiler

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open Microsoft.FSharp.Reflection

/// F# data written to bytes and read back, for the checked standard library the compiler's build writes beside it (see
/// `CheckedLibrary`). A value is written by the type it is declared as: a record, a union, a tuple, a list, a set, a map,
/// an option, a `Lazy` (written as its value), a string, or a primitive. Anything else (a class, an interface, a
/// function) is a compiler invariant broken, and fails the build that writes it.
///
/// A value the graph holds more than once (an imported module, a definition inlined into each importer, a repeated
/// name) is written once, and every later place it is held refers back to it, so a value read back is shared where the
/// value written was.
module internal BinaryData =
    let private flags = BindingFlags.Public ||| BindingFlags.NonPublic

    /// A collection type's elements as an array, and back.
    type private Collection =
        abstract Items: obj -> obj[]
        abstract Make: obj[] -> obj

    type private ListCollection<'T>() =
        interface Collection with
            member _.Items value = (value :?> 'T list) |> List.map box |> Array.ofList
            member _.Make items = items |> Array.map unbox<'T> |> List.ofArray |> box

    type private SetCollection<'T when 'T: comparison>() =
        interface Collection with
            member _.Items value = (value :?> Set<'T>) |> Seq.map box |> Array.ofSeq
            member _.Make items = items |> Array.map unbox<'T> |> Set.ofArray |> box

    /// A map's items are its keys and values in turn.
    type private MapCollection<'K, 'V when 'K: comparison>() =
        interface Collection with
            member _.Items value = (value :?> Map<'K, 'V>) |> Seq.collect (fun (KeyValue(k, v)) -> [ box k; box v ]) |> Array.ofSeq
            member _.Make items = Array.init (items.Length / 2) (fun i -> unbox<'K> items[2 * i], unbox<'V> items[2 * i + 1]) |> Map.ofArray |> box

    type private LazyValue<'T>() =
        interface Collection with
            member _.Items value = [| box (value :?> Lazy<'T>).Value |]
            member _.Make items = Lazy<'T>.CreateFromValue(unbox<'T> items[0]) |> box

    /// How a type is written: its parts, each with the type it is declared as.
    type private Shape =
        | Primitive
        | Record of fields: Type[] * read: (obj -> obj[]) * make: (obj[] -> obj)
        | Union of tag: (obj -> int) * cases: (Type[] * (obj -> obj[]) * (obj[] -> obj))[]
        | Tuple of items: Type[] * read: (obj -> obj[]) * make: (obj[] -> obj)
        /// A list, set or map (two element types), or a `Lazy` (one element, its value).
        | Elements of elementTypes: Type[] * Collection
        | Enum of underlying: Type

    let private generic (definition: Type) (ty: Type) = ty.IsGenericType && ty.GetGenericTypeDefinition() = definition

    let private collection (definition: Type) (ty: Type) =
        Activator.CreateInstance(definition.MakeGenericType(ty.GetGenericArguments())) :?> Collection

    let private primitives =
        HashSet<Type> [ typeof<bool>; typeof<byte>; typeof<char>; typeof<int>; typeof<int64>; typeof<uint32>; typeof<uint64>; typeof<float>; typeof<string>; typeof<bigint>; typeof<unit> ]

    let private shapeOf (ty: Type) : Shape =
        if primitives.Contains ty then Primitive
        elif ty.IsEnum then Enum (Enum.GetUnderlyingType ty)
        elif generic typedefof<list<_>> ty then Elements (ty.GetGenericArguments(), collection typedefof<ListCollection<_>> ty)
        elif generic typedefof<Set<_>> ty then Elements (ty.GetGenericArguments(), collection typedefof<SetCollection<_>> ty)
        elif generic typedefof<Map<_, _>> ty then Elements (ty.GetGenericArguments(), collection typedefof<MapCollection<_, _>> ty)
        elif generic typedefof<Lazy<_>> ty then Elements (ty.GetGenericArguments(), collection typedefof<LazyValue<_>> ty)
        elif FSharpType.IsTuple ty then Tuple (FSharpType.GetTupleElements ty, FSharpValue.PreComputeTupleReader ty, FSharpValue.PreComputeTupleConstructor ty)
        elif FSharpType.IsRecord(ty, flags) then
            let fields = FSharpType.GetRecordFields(ty, flags) |> Array.map _.PropertyType
            Record (fields, FSharpValue.PreComputeRecordReader(ty, flags), FSharpValue.PreComputeRecordConstructor(ty, flags))
        elif FSharpType.IsUnion(ty, flags) then
            let cases =
                FSharpType.GetUnionCases(ty, flags)
                |> Array.map (fun case ->
                    case.GetFields() |> Array.map _.PropertyType,
                    FSharpValue.PreComputeUnionReader(case, flags),
                    FSharpValue.PreComputeUnionConstructor(case, flags))
            Union (FSharpValue.PreComputeUnionTagReader(ty, flags), cases)
        else
            failwith $"BinaryData cannot write a value of type {ty.FullName}: it is not F# data"

    let private shapes = System.Collections.Concurrent.ConcurrentDictionary<Type, Shape>()
    let private shape (ty: Type) = shapes.GetOrAdd(ty, shapeOf)

    /// Whether a value of the type is written once and referred back to after: one that is not a value type.
    let private shared (ty: Type) = not ty.IsValueType && ty <> typeof<unit>

    let private writeCount (writer: BinaryWriter) (count: int) = writer.Write7BitEncodedInt count

    /// Writes `value`, of type `ty`, to `writer`, each string as `text` gives it.
    let write (writer: BinaryWriter) (text: string -> string) (ty: Type) (value: obj) =
        let objects = Dictionary<obj, int>(HashIdentity.Reference)
        let strings = Dictionary<string, int>(StringComparer.Ordinal)
        let rec item (ty: Type) (value: obj) =
            match value with
            | :? string as written ->
                match strings.TryGetValue written with
                | true, index -> writeCount writer (index + 1)
                | false, _ ->
                    writeCount writer 0
                    writer.Write(text written)
                    strings[written] <- strings.Count
            | null -> if shared ty then writeCount writer 0; content ty value
            | _ when shared ty ->
                match objects.TryGetValue value with
                | true, index -> writeCount writer (index + 1)
                | false, _ ->
                    writeCount writer 0
                    content ty value
                    objects[value] <- objects.Count
            | _ -> content ty value
        and content (ty: Type) (value: obj) =
            match shape ty with
            | Primitive ->
                match value with
                | :? bool as b -> writer.Write b
                | :? byte as b -> writer.Write b
                | :? char as c -> writer.Write c
                | :? int as i -> writer.Write i
                | :? int64 as i -> writer.Write i
                | :? uint32 as i -> writer.Write i
                | :? uint64 as i -> writer.Write i
                | :? float as f -> writer.Write f
                | :? bigint as i ->
                    let bytes = i.ToByteArray()
                    writeCount writer bytes.Length
                    writer.Write bytes
                | _ -> ()
            | Enum _ -> writer.Write(Convert.ToInt64 value)
            | Record(fields, read, _) -> Array.iter2 item fields (read value)
            | Tuple(items, read, _) -> Array.iter2 item items (read value)
            | Union(tag, cases) ->
                let index = tag value
                writeCount writer index
                let fields, read, _ = cases[index]
                Array.iter2 item fields (read value)
            | Elements(elementTypes, collection) ->
                let items = collection.Items value
                writeCount writer (items.Length / elementTypes.Length)
                items |> Array.iteri (fun i element -> item elementTypes[i % elementTypes.Length] element)
        item ty value

    /// Reads a value of type `ty` that `write` wrote, each string as `text` gives the one read.
    let read (reader: BinaryReader) (text: string -> string) (ty: Type) : obj =
        let objects = ResizeArray<obj>()
        let strings = ResizeArray<string>()
        let rec item (ty: Type) : obj =
            if ty = typeof<string> then
                match reader.Read7BitEncodedInt() with
                | 0 ->
                    let read = text (reader.ReadString())
                    strings.Add read
                    box read
                | index -> box strings[index - 1]
            elif shared ty then
                match reader.Read7BitEncodedInt() with
                | 0 ->
                    let value = content ty
                    if not (isNull value) then objects.Add value
                    value
                | index -> objects[index - 1]
            else
                content ty
        and content (ty: Type) : obj =
            match shape ty with
            | Primitive ->
                if ty = typeof<bool> then box (reader.ReadBoolean())
                elif ty = typeof<byte> then box (reader.ReadByte())
                elif ty = typeof<char> then box (reader.ReadChar())
                elif ty = typeof<int> then box (reader.ReadInt32())
                elif ty = typeof<int64> then box (reader.ReadInt64())
                elif ty = typeof<uint32> then box (reader.ReadUInt32())
                elif ty = typeof<uint64> then box (reader.ReadUInt64())
                elif ty = typeof<float> then box (reader.ReadDouble())
                elif ty = typeof<bigint> then box (bigint (reader.ReadBytes(reader.Read7BitEncodedInt())))
                else null
            | Enum underlying -> Enum.ToObject(ty, Convert.ChangeType(reader.ReadInt64(), underlying))
            | Record(fields, _, make) -> make (Array.map item fields)
            | Tuple(items, _, make) -> make (Array.map item items)
            | Union(_, cases) ->
                let fields, _, make = cases[reader.Read7BitEncodedInt()]
                make (Array.map item fields)
            | Elements(elementTypes, collection) ->
                let count = reader.Read7BitEncodedInt() * elementTypes.Length
                collection.Make (Array.init count (fun i -> item elementTypes[i % elementTypes.Length]))
        item ty

    /// A description of how values of `ty` are written, which changes when any type it is made of changes how it is:
    /// a file written by one build of the compiler is read by another only if the two agree.
    let schema (ty: Type) : string =
        let seen = HashSet<Type>()
        let text = Text.StringBuilder()
        let rec describe (ty: Type) =
            if seen.Add ty then
                text.Append(ty.FullName).Append('{') |> ignore
                match shape ty with
                | Primitive
                | Enum _ -> ()
                | Record(fields, _, _) ->
                    for field in FSharpType.GetRecordFields(ty, flags) do
                        text.Append(field.Name).Append(':').Append(field.PropertyType.FullName).Append(';') |> ignore
                    Array.iter describe fields
                | Union(_, cases) ->
                    for case in FSharpType.GetUnionCases(ty, flags) do
                        text.Append(case.Name).Append('(') |> ignore
                        for field in case.GetFields() do
                            text.Append(field.PropertyType.FullName).Append(';') |> ignore
                        text.Append(')') |> ignore
                    cases |> Array.iter (fun (fields, _, _) -> Array.iter describe fields)
                | Tuple(items, _, _) -> Array.iter describe items
                | Elements(elementTypes, _) -> Array.iter describe elementTypes
                text.Append('}') |> ignore
        describe ty
        Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(text.ToString())))
