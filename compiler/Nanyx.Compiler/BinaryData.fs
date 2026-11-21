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
    /// constructor is how `read` builds one again. What takes a value apart, which only `write` needs, is made the first
    /// time it is asked for: F#'s reflection takes long to make it, and a read meets every type in the file.
    type private Shape =
        | Primitive
        | Record of fields: Type[] * read: Lazy<obj -> obj[]> * make: ConstructorInfo
        | Union of tag: Lazy<obj -> int> * cases: (Type[] * Lazy<obj -> obj[]> * MethodInfo)[]
        | Tuple of items: Type[] * read: Lazy<obj -> obj[]> * make: ConstructorInfo
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

    let private everyInstance = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance
    let private everyStatic = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static

    let private parameterTypes (method: MethodBase) = method.GetParameters() |> Array.map _.ParameterType

    /// The kind of F# type `ty` is (`RecordType`, `SumType`), from the attribute F# compiles onto it.
    let private kindOf (ty: Type) =
        match ty.GetCustomAttribute(typeof<CompilationMappingAttribute>) with
        | :? CompilationMappingAttribute as mapping -> mapping.SourceConstructFlags &&& SourceConstructFlags.KindMask
        | _ -> SourceConstructFlags.None

    /// A union's cases in the order of their tags, each with the types of its fields and the method that makes one, from
    /// what F# compiles a union to: an option's `None` and `Some`; otherwise a `Tags` class with each case's tag, and a
    /// static `New<Case>` method for each case with fields, or a static property for one without. `None` for a union
    /// not compiled so (one of a single case has no `Tags`), whose cases F#'s reflection gives. Plain reflection is
    /// used where it can be, since F#'s takes long the first time, and a read meets every type in the file.
    let private compiledCases (ty: Type) : (Type[] * MethodInfo)[] option =
        if generic typedefof<option<_>> ty then
            Some [| [||], ty.GetProperty("None", everyStatic).GetMethod; ty.GetGenericArguments(), ty.GetMethod("Some", everyStatic) |]
        else
            match ty.GetNestedType("Tags", BindingFlags.Public ||| BindingFlags.NonPublic) with
            | null -> None
            | tags ->
                let cases =
                    tags.GetFields everyStatic
                    |> Array.sortBy (fun tag -> tag.GetRawConstantValue() :?> int)
                    |> Array.map (fun tag ->
                        match ty.GetMethod("New" + tag.Name, everyStatic) with
                        | null ->
                            match ty.GetProperty(tag.Name, everyStatic) with
                            | null -> None
                            | property -> Some([||], property.GetMethod)
                        | make -> Some(parameterTypes make, make))
                if Array.forall Option.isSome cases then Some(Array.map Option.get cases) else None

    let private shapeOf (ty: Type) : Shape =
        if primitives.Contains ty then Primitive
        elif ty.IsEnum then Enum (Enum.GetUnderlyingType ty)
        elif generic typedefof<list<_>> ty then Elements (ty.GetGenericArguments(), instance typedefof<ListCollection<_>> ty)
        elif generic typedefof<Set<_>> ty then Elements (ty.GetGenericArguments(), instance typedefof<SetCollection<_>> ty)
        elif generic typedefof<Map<_, _>> ty then Elements (ty.GetGenericArguments(), instance typedefof<MapCollection<_, _>> ty)
        elif generic typedefof<Lazy<_>> ty then Later (ty.GetGenericArguments()[0], instance typedefof<LazyValue<_>> ty)
        elif ty.IsGenericType && ty.FullName.StartsWith "System.Tuple`" then
            if ty.GetGenericArguments().Length > 7 then failwith $"BinaryData cannot write a tuple of more than seven items: {ty.FullName}"
            let make = ty.GetConstructors() |> Array.exactlyOne
            Tuple (parameterTypes make, lazy (FSharpValue.PreComputeTupleReader ty), make)
        elif kindOf ty = SourceConstructFlags.RecordType then
            // A record's constructor takes each of its fields, in order (a `[<CLIMutable>]` one has an empty one too).
            let make = ty.GetConstructors everyInstance |> Array.maxBy _.GetParameters().Length
            Record (parameterTypes make, lazy (FSharpValue.PreComputeRecordReader(ty, flags)), make)
        elif kindOf ty = SourceConstructFlags.SumType then
            let cases =
                match compiledCases ty with
                | Some cases ->
                    cases
                    |> Array.mapi (fun tag (fields, make) ->
                        fields, lazy (FSharpValue.PreComputeUnionReader(FSharpType.GetUnionCases(ty, flags)[tag], flags)), make)
                | None ->
                    FSharpType.GetUnionCases(ty, flags)
                    |> Array.map (fun case ->
                        case.GetFields() |> Array.map _.PropertyType,
                        lazy (FSharpValue.PreComputeUnionReader(case, flags)),
                        FSharpValue.PreComputeUnionConstructorInfo(case, flags))
            Union (lazy (FSharpValue.PreComputeUnionTagReader(ty, flags)), cases)
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

    /// Writes `value`, of type `ty`, to `writer`, each string as `text` gives it and each `int64` as `ids` gives it: every
    /// `int64` a checked module holds is a syntax node's or a symbol's id (see `CheckedPackages`). The writer's stream must
    /// be seekable: the length of a `Lazy`'s part is written in front of it once the part is written.
    let writeWith (writer: BinaryWriter) (text: string -> string) (ids: int64 -> int64) (ty: Type) (value: obj) =
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
                | :? int64 as i -> writer.Write(ids i)
                | :? uint32 as i -> writer.Write i
                | :? uint64 as i -> writer.Write i
                | :? float as f -> writer.Write f
                | :? bigint as i ->
                    let bytes = i.ToByteArray()
                    writeCount writer bytes.Length
                    writer.Write bytes
                | _ -> ()
            | Enum _ -> writer.Write(Convert.ToInt64 value)
            | Record(fields, read, _) -> Array.iter2 item fields (read.Value value)
            | Tuple(items, read, _) -> Array.iter2 item items (read.Value value)
            | Union(tag, cases) ->
                let index = tag.Value value
                writeCount writer index
                let fields, read, _ = cases[index]
                Array.iter2 item fields (read.Value value)
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

    /// Writes `value` as `writeWith` does, each `int64` as it is.
    let write (writer: BinaryWriter) (text: string -> string) (ty: Type) (value: obj) = writeWith writer text id ty value

    /// The values a read has seen, by number: the first `outerCount` of the part around the one it is reading, then its
    /// own.
    ///
    /// Its own are kept in chunks of a fixed size, so that none is copied as they grow: a part of the checked library
    /// sees hundreds of thousands.
    type private Seen<'T>(outer: Seen<'T> option, outerCount: int) =
        static let chunkBits = 10
        static let chunkSize = 1 <<< chunkBits
        let chunks = ResizeArray<'T[]>()
        let mutable count = 0
        member _.Count = outerCount + count
        member _.Add(value: 'T) =
            if count &&& (chunkSize - 1) = 0 then chunks.Add(Array.zeroCreate chunkSize)
            chunks[count >>> chunkBits][count &&& (chunkSize - 1)] <- value
            count <- count + 1
        member _.Item
            with get (index: int) : 'T =
                if index < outerCount then
                    outer.Value[index]
                else
                    let own = index - outerCount
                    chunks[own >>> chunkBits][own &&& (chunkSize - 1)]

    /// A delegate of type `'D` that runs the IL `emit` writes, taking `parameters` and giving `result`. It may use what
    /// is internal to another assembly.
    let private emitted<'D when 'D :> Delegate> (name: string) (result: Type) (parameters: Type[]) (emit: ILGenerator -> unit) : 'D =
        let method = DynamicMethod(name, result, parameters, typeof<Shape>.Module, true)
        emit (method.GetILGenerator())
        method.CreateDelegate typeof<'D> :?> 'D

    /// One of FSharp.Core's types, which it keeps internal: a map's or set's tree. `None` if this FSharp.Core has none
    /// by that name.
    let private collectionType (name: string) (args: Type[]) =
        match typeof<Map<int, int>>.Assembly.GetType("Microsoft.FSharp.Collections." + name) with
        | null -> None
        | definition -> Some(definition.MakeGenericType args)

    /// The constructor of `ty` that takes `parameters`, if it has one.
    let private constructorOf (parameters: Type[]) (ty: Type) = Option.ofObj (ty.GetConstructor(everyInstance, parameters))

    /// The comparer a collection of type `ty` keeps (`Map.Comparer`, `Set.Comparer`, internal), read from `empty`.
    let private comparerOf<'T> (ty: Type) (empty: obj) =
        ty.GetProperty("Comparer", everyInstance) |> Option.ofObj |> Option.map (fun property -> property.GetValue empty :?> IComparer<'T>)

    /// Emits `new (args ...)` with the constructor `make`, each argument loaded and, where `casts` names a type, cast to
    /// it from `obj`.
    let private emitConstruction (il: ILGenerator) (casts: Type option[]) (make: ConstructorInfo) =
        casts
        |> Array.iteri (fun index cast ->
            il.Emit(OpCodes.Ldarg_S, byte index)
            cast |> Option.iter (fun ty -> il.Emit(OpCodes.Castclass, ty)))
        il.Emit(OpCodes.Newobj, make)
        il.Emit OpCodes.Ret

    /// The items `0 .. count - 1` as a balanced tree, built from the middle out, with `leaf` making a tree of one item
    /// and `node` one with subtrees (either may be `null`, the empty tree) of the height it is given.
    let private balancedTree (count: int) (leaf: int -> obj) (node: int -> obj -> obj -> int -> obj) : obj =
        let rec tree (low: int) (high: int) : struct (obj * int) =
            if low > high then
                struct (null, 0)
            else
                let middle = low + (high - low) / 2
                let struct (left, leftHeight) = tree low (middle - 1)
                let struct (right, rightHeight) = tree (middle + 1) high
                if leftHeight = 0 && rightHeight = 0 then struct (leaf middle, 1)
                else
                    let height = 1 + max leftHeight rightHeight
                    struct (node middle left right height, height)
        let struct (root, _) = tree 0 (count - 1)
        root

    /// Whether `items` are in the order `comparer` puts them in, each after the one before it.
    let private ascending (comparer: IComparer<'T>) (items: 'T[]) =
        let mutable inOrder = true
        let mutable i = 1
        while inOrder && i < items.Length do
            inOrder <- comparer.Compare(items[i - 1], items[i]) < 0
            i <- i + 1
        inOrder

    /// How `SortedMaps` builds a map from its sorted items: FSharp.Core's comparer for its keys, and a leaf of the tree,
    /// a node, and the map of a tree.
    type private MapBuilders<'K, 'V when 'K: comparison> =
        { Comparer: IComparer<'K>
          Leaf: Func<'K, 'V, obj>
          Node: Func<'K, 'V, obj, obj, int, obj>
          Map: Func<IComparer<'K>, obj, Map<'K, 'V>> }

    /// Maps built from their items in key order, as `write` writes them, straight into FSharp.Core's tree, which
    /// `Map.Add` would build by inserting each item, making a new path to it each time. The tree's types are internal to
    /// FSharp.Core, so if a version of it doesn't have them as this expects, a map is built by adding its items one by
    /// one, as it is when they are out of order (a key type compared differently than when it was written would give
    /// that).
    type private SortedMaps<'K, 'V when 'K: comparison>() =
        static let builders : MapBuilders<'K, 'V> option =
            let types = [| typeof<'K>; typeof<'V> |]
            match collectionType "MapTree`2" types, collectionType "MapTreeNode`2" types with
            | Some tree, Some node ->
                match
                    comparerOf<'K> typeof<Map<'K, 'V>> Map.empty<'K, 'V>,
                    constructorOf types tree,
                    constructorOf [| typeof<'K>; typeof<'V>; tree; tree; typeof<int> |] node,
                    constructorOf [| typeof<IComparer<'K>>; tree |] typeof<Map<'K, 'V>>
                with
                | Some comparer, Some leaf, Some node', Some map ->
                    Some
                        { Comparer = comparer
                          Leaf = emitted "map leaf" typeof<obj> types (fun il -> emitConstruction il [| None; None |] leaf)
                          Node =
                            emitted "map node" typeof<obj> [| typeof<'K>; typeof<'V>; typeof<obj>; typeof<obj>; typeof<int> |] (fun il ->
                                emitConstruction il [| None; None; Some tree; Some tree; None |] node')
                          Map =
                            emitted "map" typeof<Map<'K, 'V>> [| typeof<IComparer<'K>>; typeof<obj> |] (fun il ->
                                emitConstruction il [| None; Some tree |] map) }
                | _ -> None
            | _ -> None

        /// Whether maps of these types are built straight into FSharp.Core's tree.
        static member BuiltDirectly = builders.IsSome

        /// The map of `keys` to `values`.
        static member Of(keys: 'K[], values: 'V[]) : Map<'K, 'V> =
            match builders with
            | Some builders when ascending builders.Comparer keys ->
                let root =
                    balancedTree keys.Length
                        (fun i -> builders.Leaf.Invoke(keys[i], values[i]))
                        (fun i left right height -> builders.Node.Invoke(keys[i], values[i], left, right, height))
                builders.Map.Invoke(builders.Comparer, root)
            | _ -> Array.fold2 (fun (map: Map<'K, 'V>) k v -> map.Add(k, v)) Map.empty keys values

    /// How `SortedSets` builds a set from its sorted items, as `MapBuilders` does a map.
    type private SetBuilders<'T when 'T: comparison> =
        { Comparer: IComparer<'T>
          Leaf: Func<'T, obj>
          Node: Func<'T, obj, obj, int, obj>
          Set: Func<IComparer<'T>, obj, Set<'T>> }

    /// Sets built from their items in order, as `SortedMaps` builds maps, and by adding them one by one where it does.
    type private SortedSets<'T when 'T: comparison>() =
        static let builders : SetBuilders<'T> option =
            let types = [| typeof<'T> |]
            match collectionType "SetTree`1" types, collectionType "SetTreeNode`1" types with
            | Some tree, Some node ->
                match
                    comparerOf<'T> typeof<Set<'T>> Set.empty<'T>,
                    constructorOf types tree,
                    constructorOf [| typeof<'T>; tree; tree; typeof<int> |] node,
                    constructorOf [| typeof<IComparer<'T>>; tree |] typeof<Set<'T>>
                with
                | Some comparer, Some leaf, Some node', Some set ->
                    Some
                        { Comparer = comparer
                          Leaf = emitted "set leaf" typeof<obj> types (fun il -> emitConstruction il [| None |] leaf)
                          Node =
                            emitted "set node" typeof<obj> [| typeof<'T>; typeof<obj>; typeof<obj>; typeof<int> |] (fun il ->
                                emitConstruction il [| None; Some tree; Some tree; None |] node')
                          Set =
                            emitted "set" typeof<Set<'T>> [| typeof<IComparer<'T>>; typeof<obj> |] (fun il ->
                                emitConstruction il [| None; Some tree |] set) }
                | _ -> None
            | _ -> None

        /// Whether sets of this type are built straight into FSharp.Core's tree.
        static member BuiltDirectly = builders.IsSome

        /// The set of `items`.
        static member Of(items: 'T[]) : Set<'T> =
            match builders with
            | Some builders when ascending builders.Comparer items ->
                let root =
                    balancedTree items.Length
                        (fun i -> builders.Leaf.Invoke items[i])
                        (fun i left right height -> builders.Node.Invoke(items[i], left, right, height))
                builders.Set.Invoke(builders.Comparer, root)
            | _ -> Array.fold (fun (set: Set<'T>) item -> set.Add item) Set.empty items

    /// Whether this FSharp.Core has the internal tree types a read builds maps and sets with (see `SortedMaps`).
    /// Without them it still reads them, more slowly, by adding their items one by one.
    let treesBuiltDirectly () = SortedMaps<string, int>.BuiltDirectly && SortedSets<int64>.BuiltDirectly

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
            let items = Array.zeroCreate (this.Count())
            for i in 0 .. items.Length - 1 do
                items[i] <- item.Invoke this
            SortedSets<'T>.Of items

        member this.Map(key: Func<ReadState, 'K>, value: Func<ReadState, 'V>) : Map<'K, 'V> =
            let count = this.Count()
            let keys = Array.zeroCreate count
            let values = Array.zeroCreate count
            for i in 0 .. count - 1 do
                keys[i] <- key.Invoke this
                values[i] <- value.Invoke this
            SortedMaps<'K, 'V>.Of(keys, values)

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

    let private stateMethod (name: string) = typeof<ReadState>.GetMethod(name, everyInstance)
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
