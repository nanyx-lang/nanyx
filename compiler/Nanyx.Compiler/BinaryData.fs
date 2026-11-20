namespace NyxCompiler

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Reflection.Emit
open Microsoft.FSharp.Reflection

/// F# data written to bytes and read back, for the checked standard library the compiler's build writes beside it (see
/// `CheckedLibrary`). A value is written by the type it is declared as: a record, a union, a tuple, a list, a set, a map,
/// an option, a `Lazy`, a string, or a primitive. Anything else (a class, an interface, a function) is a compiler
/// invariant broken, and fails the build that writes it.
///
/// A value the graph holds more than once (an imported module, a definition inlined into each importer, a repeated
/// name) is written once, and every later place it is held refers back to it, so a value read back is shared where the
/// value written was.
///
/// A `Lazy` is written as its value, in a part of its own that a read skips: the value is read the first time the `Lazy`
/// read back is asked for it. What the part holds may refer back to what was written before it, but nothing after it
/// refers back into it, so a part that is never asked for is never read.
module internal BinaryData =
    let private flags = BindingFlags.Public ||| BindingFlags.NonPublic

    /// A collection type's elements as an array.
    type private Collection =
        abstract Items: obj -> obj[]

    type private ListCollection<'T>() =
        interface Collection with
            member _.Items value = (value :?> 'T list) |> List.map box |> Array.ofList

    type private SetCollection<'T when 'T: comparison>() =
        interface Collection with
            member _.Items value = (value :?> Set<'T>) |> Seq.map box |> Array.ofSeq

    /// A map's items are its keys and values in turn.
    type private MapCollection<'K, 'V when 'K: comparison>() =
        interface Collection with
            member _.Items value = (value :?> Map<'K, 'V>) |> Seq.collect (fun (KeyValue(k, v)) -> [ box k; box v ]) |> Array.ofSeq

    /// A `Lazy`'s value.
    type private Deferred =
        abstract Value: obj -> obj

    type private LazyValue<'T>() =
        interface Deferred with
            member _.Value value = box (value :?> Lazy<'T>).Value

    /// How a type is written: its parts, each with the type it is declared as. A record's, a tuple's and a union case's
    /// constructor is how `read` builds one again.
    type private Shape =
        | Primitive
        | Record of fields: Type[] * read: (obj -> obj[]) * make: ConstructorInfo
        | Union of tag: (obj -> int) * cases: (Type[] * (obj -> obj[]) * MethodInfo)[]
        | Tuple of items: Type[] * read: (obj -> obj[]) * make: ConstructorInfo
        /// A list, set or map (two element types).
        | Elements of elementTypes: Type[] * Collection
        /// A `Lazy`, with the type of its value.
        | Later of valueType: Type * Deferred
        | Enum of underlying: Type

    let private generic (definition: Type) (ty: Type) = ty.IsGenericType && ty.GetGenericTypeDefinition() = definition

    let private instance<'T> (definition: Type) (ty: Type) =
        Activator.CreateInstance(definition.MakeGenericType(ty.GetGenericArguments())) :?> 'T

    let private primitives =
        HashSet<Type> [ typeof<bool>; typeof<byte>; typeof<char>; typeof<int>; typeof<int64>; typeof<uint32>; typeof<uint64>; typeof<float>; typeof<string>; typeof<bigint>; typeof<unit> ]

    let private shapeOf (ty: Type) : Shape =
        if primitives.Contains ty then Primitive
        elif ty.IsEnum then Enum (Enum.GetUnderlyingType ty)
        elif generic typedefof<list<_>> ty then Elements (ty.GetGenericArguments(), instance typedefof<ListCollection<_>> ty)
        elif generic typedefof<Set<_>> ty then Elements (ty.GetGenericArguments(), instance typedefof<SetCollection<_>> ty)
        elif generic typedefof<Map<_, _>> ty then Elements (ty.GetGenericArguments(), instance typedefof<MapCollection<_, _>> ty)
        elif generic typedefof<Lazy<_>> ty then Later (ty.GetGenericArguments()[0], instance typedefof<LazyValue<_>> ty)
        elif FSharpType.IsTuple ty then
            match FSharpValue.PreComputeTupleConstructorInfo ty with
            | make, None -> Tuple (FSharpType.GetTupleElements ty, FSharpValue.PreComputeTupleReader ty, make)
            | _, Some _ -> failwith $"BinaryData cannot write a tuple of more than seven items: {ty.FullName}"
        elif FSharpType.IsRecord(ty, flags) then
            let fields = FSharpType.GetRecordFields(ty, flags) |> Array.map _.PropertyType
            Record (fields, FSharpValue.PreComputeRecordReader(ty, flags), FSharpValue.PreComputeRecordConstructorInfo(ty, flags))
        elif FSharpType.IsUnion(ty, flags) then
            let cases =
                FSharpType.GetUnionCases(ty, flags)
                |> Array.map (fun case ->
                    case.GetFields() |> Array.map _.PropertyType,
                    FSharpValue.PreComputeUnionReader(case, flags),
                    FSharpValue.PreComputeUnionConstructorInfo(case, flags))
            Union (FSharpValue.PreComputeUnionTagReader(ty, flags), cases)
        else
            failwith $"BinaryData cannot write a value of type {ty.FullName}: it is not F# data"

    let private shapes = System.Collections.Concurrent.ConcurrentDictionary<Type, Shape>()
    // One delegate for every lookup: passing `shapeOf` itself would make a new one for each value read.
    let private makeShape = Func<Type, Shape> shapeOf
    let private shape (ty: Type) = shapes.GetOrAdd(ty, makeShape)

    /// Whether a value of the type is written once and referred back to after: one that is not a value type.
    let private shared (ty: Type) = not ty.IsValueType && ty <> typeof<unit>

    let private writeCount (writer: BinaryWriter) (count: int) = writer.Write7BitEncodedInt count

    /// The values a write has numbered, so that a later place that holds one again refers back to it: those of the part
    /// it is writing, after those of the parts around it. Leaving a part forgets the values numbered in it.
    type private Numbering<'T>(comparer: IEqualityComparer<'T>) =
        let numbers = Dictionary<'T, int>(comparer)
        let order = ResizeArray<'T>()
        member _.TryGetValue(value: 'T, [<Runtime.InteropServices.Out>] number: byref<int>) = numbers.TryGetValue(value, &number)
        member _.Add(value: 'T) =
            numbers[value] <- order.Count
            order.Add value
        member _.Count = order.Count
        member _.ForgetFrom(count: int) =
            for i in count .. order.Count - 1 do
                numbers.Remove order[i] |> ignore
            order.RemoveRange(count, order.Count - count)

    /// Writes `value`, of type `ty`, to `writer`, each string as `text` gives it. The writer's stream must be seekable:
    /// the length of a `Lazy`'s part is written in front of it once the part is written.
    let write (writer: BinaryWriter) (text: string -> string) (ty: Type) (value: obj) =
        let objects = Numbering<obj>(HashIdentity.Reference)
        let strings = Numbering<string>(StringComparer.Ordinal)
        let rec item (ty: Type) (value: obj) =
            match value with
            | :? string as written ->
                match strings.TryGetValue written with
                | true, index -> writeCount writer (index + 1)
                | false, _ ->
                    writeCount writer 0
                    writer.Write(text written)
                    strings.Add written
            | null -> if shared ty then writeCount writer 0; content ty value
            | _ when shared ty ->
                match objects.TryGetValue value with
                | true, index -> writeCount writer (index + 1)
                | false, _ ->
                    writeCount writer 0
                    content ty value
                    objects.Add value
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
            | Later(valueType, deferred) ->
                let objectsBefore, stringsBefore = objects.Count, strings.Count
                let lengthAt = writer.BaseStream.Position
                writer.Write 0
                item valueType (deferred.Value value)
                let ended = writer.BaseStream.Position
                writer.BaseStream.Position <- lengthAt
                writer.Write(int (ended - lengthAt - 4L))
                writer.BaseStream.Position <- ended
                objects.ForgetFrom objectsBefore
                strings.ForgetFrom stringsBefore
        item ty value

    /// The values a read has seen, by number: the first `outerCount` of the part around the one it is reading, then its
    /// own.
    type private Seen<'T>(outer: Seen<'T> option, outerCount: int) =
        let own = ResizeArray<'T>()
        member _.Count = outerCount + own.Count
        member _.Add(value: 'T) = own.Add value
        member _.Item
            with get (index: int) : 'T = if index < outerCount then outer.Value[index] else own[index - outerCount]

    /// A read of one part, from the file's bytes at the position it has got to. The readers `build` makes call its
    /// methods: one for each primitive, and one for each kind of value that holds others, given their readers.
    [<Sealed>]
    type private ReadState(bytes: byte[], start: int, text: string -> string, objects: Seen<obj>, strings: Seen<string>) =
        let mutable position = start

        member _.Count() : int =
            let mutable result = 0
            let mutable shift = 0
            let mutable more = true
            while more do
                let b = int bytes[position]
                position <- position + 1
                result <- result ||| ((b &&& 0x7F) <<< shift)
                shift <- shift + 7
                more <- b &&& 0x80 <> 0
            result

        member _.Bool() : bool =
            position <- position + 1
            bytes[position - 1] <> 0uy

        member _.Byte() : byte =
            position <- position + 1
            bytes[position - 1]

        /// A `char`, which `BinaryWriter` writes in UTF-8.
        member _.Char() : char =
            let first = bytes[position]
            let length = if first < 0x80uy then 1 elif first < 0xE0uy then 2 elif first < 0xF0uy then 3 else 4
            let read = Text.Encoding.UTF8.GetString(bytes, position, length)
            position <- position + length
            read[0]

        member _.Int32() : int =
            position <- position + 4
            Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(bytes, position - 4, 4))

        member _.Int64() : int64 =
            position <- position + 8
            Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan(bytes, position - 8, 8))

        member _.UInt32() : uint32 =
            position <- position + 4
            Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan(bytes, position - 4, 4))

        member _.UInt64() : uint64 =
            position <- position + 8
            Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(bytes, position - 8, 8))

        member _.Float() : float =
            position <- position + 8
            Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(ReadOnlySpan(bytes, position - 8, 8))

        member this.BigInt() : bigint =
            let length = this.Count()
            position <- position + length
            bigint (ReadOnlySpan(bytes, position - length, length))

        /// A string written for the first time, which `BinaryWriter` writes as its length in bytes and its UTF-8, or the
        /// number of one read before.
        member this.String() : string =
            match this.Count() with
            | 0 ->
                let length = this.Count()
                let read = text (Text.Encoding.UTF8.GetString(bytes, position, length))
                position <- position + length
                strings.Add read
                read
            | index -> strings[index - 1]

        /// A value written for the first time, read by `content`, or the number of one read before.
        member this.Shared(content: Func<ReadState, 'T>) : 'T =
            match this.Count() with
            | 0 ->
                let value = content.Invoke this
                if not (isNull (box value)) then objects.Add(box value)
                value
            | index -> unbox<'T> objects[index - 1]

        member this.List(item: Func<ReadState, 'T>) : 'T list =
            let items = Array.zeroCreate (this.Count())
            for i in 0 .. items.Length - 1 do
                items[i] <- item.Invoke this
            List.ofArray items

        member this.Set(item: Func<ReadState, 'T>) : Set<'T> =
            let mutable set = Set.empty
            for _ in 1 .. this.Count() do
                set <- set.Add(item.Invoke this)
            set

        member this.Map(key: Func<ReadState, 'K>, value: Func<ReadState, 'V>) : Map<'K, 'V> =
            let mutable map = Map.empty
            for _ in 1 .. this.Count() do
                let k = key.Invoke this
                map <- map.Add(k, value.Invoke this)
            map

        /// A `Lazy` whose value is in a part of its own, which this read skips, and which is read the first time the
        /// `Lazy` is asked for.
        member this.Later(item: Func<ReadState, 'T>) : Lazy<'T> =
            let length = this.Int32()
            let partAt = position
            position <- position + length
            let outerObjects, outerStrings = Seen(Some objects, objects.Count), Seen(Some strings, strings.Count)
            Lazy<'T>(fun () -> item.Invoke(ReadState(bytes, partAt, text, outerObjects, outerStrings)))

    /// The reader of values of type `'T`, which `build` makes the first time a read meets the type.
    [<AbstractClass; Sealed>]
    type private ReaderOf<'T> =
        [<DefaultValue>]
        static val mutable private reader: Func<ReadState, 'T>

    let private instanceMember = flags ||| BindingFlags.Instance
    let private stateMethod (name: string) = typeof<ReadState>.GetMethod(name, instanceMember)
    // F# names the field as it likes (`reader@`), so it is found as the type's only one.
    let private readerField (ty: Type) = typedefof<ReaderOf<_>>.MakeGenericType(ty).GetFields(flags ||| BindingFlags.Static) |> Array.exactlyOne
    let private readerType (ty: Type) = typedefof<Func<_, _>>.MakeGenericType(typeof<ReadState>, ty)

    /// The `ReadState` method that reads a primitive of type `ty`.
    let private primitiveRead (ty: Type) =
        let name =
            if ty = typeof<bool> then "Bool"
            elif ty = typeof<byte> then "Byte"
            elif ty = typeof<char> then "Char"
            elif ty = typeof<int> then "Int32"
            elif ty = typeof<int64> then "Int64"
            elif ty = typeof<uint32> then "UInt32"
            elif ty = typeof<uint64> then "UInt64"
            elif ty = typeof<float> then "Float"
            elif ty = typeof<bigint> then "BigInt"
            else "String"
        stateMethod name

    /// Whether a value of the type is read where it is held, rather than by a reader of its own.
    let private readInPlace (ty: Type) = primitives.Contains ty || ty.IsEnum

    /// Emits the reading of a value of type `ty`, which leaves it on the stack, in a method whose only argument is the
    /// `ReadState`.
    let private emitValue (il: ILGenerator) (ty: Type) =
        if ty = typeof<unit> then
            il.Emit OpCodes.Ldnull
        elif primitives.Contains ty then
            il.Emit OpCodes.Ldarg_0
            il.Emit(OpCodes.Call, primitiveRead ty)
        elif ty.IsEnum then
            il.Emit OpCodes.Ldarg_0
            il.Emit(OpCodes.Call, stateMethod "Int64")
            let underlying = Enum.GetUnderlyingType ty
            if underlying = typeof<int> then il.Emit OpCodes.Conv_I4
            elif underlying = typeof<uint32> then il.Emit OpCodes.Conv_U4
            elif underlying = typeof<int16> then il.Emit OpCodes.Conv_I2
            elif underlying = typeof<uint16> then il.Emit OpCodes.Conv_U2
            elif underlying = typeof<byte> then il.Emit OpCodes.Conv_U1
            elif underlying = typeof<sbyte> then il.Emit OpCodes.Conv_I1
        else
            il.Emit(OpCodes.Ldsfld, readerField ty)
            il.Emit OpCodes.Ldarg_0
            il.Emit(OpCodes.Callvirt, (readerType ty).GetMethod "Invoke")

    /// The types whose readers the reader of `ty` calls.
    let private heldTypes (ty: Type) : Type[] =
        if readInPlace ty then
            [||]
        else
            match shape ty with
            | Primitive
            | Enum _ -> [||]
            | Record(fields, _, _) -> fields
            | Tuple(items, _, _) -> items
            | Union(_, cases) -> cases |> Array.collect (fun (fields, _, _) -> fields)
            | Elements(elementTypes, _) -> elementTypes
            | Later(valueType, _) -> [| valueType |]

    /// Emits the reading of what `write`'s `content` wrote of a value of type `ty`, and its return.
    let private emitContent (il: ILGenerator) (ty: Type) =
        let readers (types: Type[]) =
            for held in types do
                il.Emit(OpCodes.Ldsfld, readerField held)
        match shape ty with
        | _ when readInPlace ty -> emitValue il ty
        | Primitive
        | Enum _ -> emitValue il ty
        | Record(fields, _, make)
        | Tuple(fields, _, make) ->
            Array.iter (emitValue il) fields
            il.Emit(OpCodes.Newobj, make)
        | Union(_, cases) ->
            let labels = cases |> Array.map (fun _ -> il.DefineLabel())
            il.Emit OpCodes.Ldarg_0
            il.Emit(OpCodes.Call, stateMethod "Count")
            il.Emit(OpCodes.Switch, labels)
            il.Emit(OpCodes.Ldstr, $"BinaryData read a case of {ty.FullName} that it has no case for")
            il.Emit(OpCodes.Newobj, typeof<InvalidDataException>.GetConstructor [| typeof<string> |])
            il.Emit OpCodes.Throw
            for label, (fields, _, make) in Array.zip labels cases do
                il.MarkLabel label
                Array.iter (emitValue il) fields
                il.Emit(OpCodes.Call, make)
                il.Emit OpCodes.Ret
        | Elements(elementTypes, _) ->
            il.Emit OpCodes.Ldarg_0
            readers elementTypes
            let name = if generic typedefof<list<_>> ty then "List" elif generic typedefof<Set<_>> ty then "Set" else "Map"
            il.Emit(OpCodes.Call, (stateMethod name).MakeGenericMethod elementTypes)
        | Later(valueType, _) ->
            il.Emit OpCodes.Ldarg_0
            readers [| valueType |]
            il.Emit(OpCodes.Call, (stateMethod "Later").MakeGenericMethod [| valueType |])
        il.Emit OpCodes.Ret

    let private sharedMethod = stateMethod "Shared"

    /// A reader of values of type `'T` that reads one written for the first time with `content`, or one read before by
    /// its number.
    let private sharedReader<'T> (content: Func<ReadState, 'T>) = Func<ReadState, 'T>(fun state -> state.Shared content)

    let private sharedReaderMethod = typeof<ReadState>.DeclaringType.GetMethod("sharedReader", flags ||| BindingFlags.Static)

    let private built = HashSet<Type>()

    /// Builds the readers of `ty` and of every type it holds, which have not been built, once each: each a method of
    /// its own that reads straight from the bytes into the type's constructor, so that no value is boxed and no array
    /// of fields is made.
    let rec private build (ty: Type) =
        if built.Add ty then
            let content = DynamicMethod($"read {ty.Name}", ty, [| typeof<ReadState> |], typeof<ReadState>.Module, true)
            emitContent (content.GetILGenerator()) ty
            let reader =
                let contentReader = content.CreateDelegate(readerType ty)
                if shared ty && ty <> typeof<string> then sharedReaderMethod.MakeGenericMethod(ty).Invoke(null, [| contentReader |])
                else box contentReader
            (readerField ty).SetValue(null, reader)
            Array.iter build (heldTypes ty)

    /// Reads a value of type `ty` that `write` wrote, from `bytes` at `start`, each string as `text` gives the one read.
    /// A `Lazy` in it reads its value from `bytes` when it is first asked for, so `bytes` must not change after.
    let read (bytes: byte[]) (start: int) (text: string -> string) (ty: Type) : obj =
        lock built (fun () -> build ty)
        let reader = (readerField ty).GetValue null :?> Delegate
        reader.DynamicInvoke(ReadState(bytes, start, text, Seen(None, 0), Seen(None, 0)))
