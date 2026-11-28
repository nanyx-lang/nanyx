namespace Nanyx.Runtime

open System
open System.Reflection
open System.Runtime.InteropServices
open Wasmtime

/// Wasmtime's guest profiler, which samples the stack of a running module and writes the Firefox profiler's format,
/// with the functions named as the module's name section names them. wasmtime-dotnet doesn't wrap it, so it is called
/// through Wasmtime's C API, from the native library wasmtime-dotnet ships (`wasmtime/profiling.h`, in Wasmtime 48).
module private GuestProfilerApi =
    /// A `wasm_byte_vec_t`, and a `wasm_name_t`, which is one.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ByteVec =
        val mutable Size: unativeint
        val mutable Data: nativeint

    /// A `wasmtime_guestprofiler_modules_t`: a name, and a module that may appear in the stacks.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ProfiledModule =
        val mutable Name: nativeint
        val mutable Module: nativeint

    [<DllImport("wasmtime")>]
    extern void wasm_byte_vec_new(ByteVec& out, unativeint size, byte[] data)

    [<DllImport("wasmtime")>]
    extern void wasm_byte_vec_delete(ByteVec& vec)

    [<DllImport("wasmtime")>]
    extern nativeint wasmtime_guestprofiler_new(nativeint engine, nativeint moduleName, uint64 intervalNanos, ProfiledModule[] modules, unativeint modulesLength)

    [<DllImport("wasmtime")>]
    extern void wasmtime_guestprofiler_sample(nativeint profiler, nativeint store, uint64 deltaNanos)

    [<DllImport("wasmtime")>]
    extern nativeint wasmtime_guestprofiler_finish(nativeint profiler, ByteVec& out)

    [<DllImport("wasmtime")>]
    extern void wasmtime_error_message(nativeint error, ByteVec& message)

    [<DllImport("wasmtime")>]
    extern void wasmtime_error_delete(nativeint error)

    /// The native handle wasmtime-dotnet keeps for `owner`, which it doesn't make public.
    let handleOf (owner: obj) =
        let property = owner.GetType().GetProperty("NativeHandle", BindingFlags.NonPublic ||| BindingFlags.Instance)
        (property.GetValue owner :?> SafeHandle).DangerousGetHandle()

    /// `text` as a `wasm_name_t` the caller deletes.
    let name (text: string) =
        let mutable vec = ByteVec()
        let bytes = Text.Encoding.UTF8.GetBytes text
        wasm_byte_vec_new(&vec, unativeint bytes.Length, bytes)
        vec

/// A run of a module that Wasmtime's guest profiler samples (see `GuestProfilerApi`), every `interval`: on an engine with
/// epoch interruption, a thread moves the epoch on, and the store's deadline callback takes each sample.
type GuestProfiler(engine: Engine, ``module``: Module, interval: TimeSpan) =
    // The module's name in the profile, a `wasm_name_t` where the profiler can read it until it finishes.
    let moduleName =
        let pointer = Marshal.AllocHGlobal(Marshal.SizeOf<GuestProfilerApi.ByteVec>())
        Marshal.StructureToPtr(GuestProfilerApi.name "nanyx", pointer, false)
        pointer
    let profiler =
        let mutable profiled = GuestProfilerApi.ProfiledModule()
        profiled.Name <- moduleName
        profiled.Module <- GuestProfilerApi.handleOf ``module``
        GuestProfilerApi.wasmtime_guestprofiler_new(GuestProfilerApi.handleOf engine, moduleName, uint64 interval.Ticks * 100UL, [| profiled |], 1un)
    // A thread of its own moves the epoch on, rather than a timer, whose ticks wait for a thread of the pool.
    let stop = new Threading.ManualResetEventSlim(false)
    let ticker =
        let thread = Threading.Thread((fun () -> while not (stop.Wait interval) do engine.IncrementEpoch()), IsBackground = true)
        thread.Start()
        thread

    /// The configuration of an engine a profiled run needs.
    static member Configure(config: Config) = config.WithEpochInterruption true

    /// Samples `store`'s stack at each tick of the timer, from now on.
    member _.Watch(store: Store) =
        let handle = GuestProfilerApi.handleOf store
        store.SetEpochDeadline 1UL
        store.SetEpochDeadlineCallback(fun _ ->
            GuestProfilerApi.wasmtime_guestprofiler_sample(profiler, handle, uint64 interval.Ticks * 100UL)
            1UL)

    /// Stops sampling, and gives the profile, in the Firefox profiler's format, or why it couldn't be written.
    member _.Finish() : Result<byte array, string> =
        stop.Set()
        ticker.Join()
        stop.Dispose()
        let mutable out = GuestProfilerApi.ByteVec()
        let error = GuestProfilerApi.wasmtime_guestprofiler_finish(profiler, &out)
        let mutable name = Marshal.PtrToStructure<GuestProfilerApi.ByteVec> moduleName
        GuestProfilerApi.wasm_byte_vec_delete(&name)
        Marshal.FreeHGlobal moduleName
        if error = 0n then
            let bytes = Array.zeroCreate<byte> (int out.Size)
            Marshal.Copy(out.Data, bytes, 0, bytes.Length)
            GuestProfilerApi.wasm_byte_vec_delete(&out)
            Ok bytes
        else
            let mutable message = GuestProfilerApi.ByteVec()
            GuestProfilerApi.wasmtime_error_message(error, &message)
            let text = Marshal.PtrToStringUTF8(message.Data, int message.Size)
            GuestProfilerApi.wasm_byte_vec_delete(&message)
            GuestProfilerApi.wasmtime_error_delete error
            Error text

/// The places a profile gives its functions.
module ProfilePlaces =
    open System.Text.Json.Nodes

    /// `profile`, in the Firefox profiler's format, with the file and line of each function's definition that `table`
    /// has, by the function's name, in each thread's `funcTable` (its `fileName`, an index into the thread's
    /// `stringArray`, and its `lineNumber`), and its name as it is written in the source, where the table has that. Wasmtime's guest profiler gives a frame's address in its machine code, not
    /// in the module, so a profile places functions, not the lines they ran.
    let withDefinitions (table: SpanTable option) (profile: byte array) : byte array =
        match table with
        | None -> profile
        | Some table ->
            let root = JsonNode.Parse(profile: byte array)
            for thread in root.["threads"].AsArray() do
                let strings = thread.["stringArray"].AsArray()
                let functions = thread.["funcTable"]
                let names = functions.["name"].AsArray()
                let fileNames = functions.["fileName"].AsArray()
                let lineNumbers = functions.["lineNumber"].AsArray()
                // Each file's index in the thread's strings, which a file is added to the first time it is named.
                let fileIndex (file: string) =
                    match strings |> Seq.tryFindIndex (fun text -> text.GetValue<string>() = file) with
                    | Some index -> index
                    | None ->
                        strings.Add(JsonValue.Create file)
                        strings.Count - 1
                for index in 0 .. names.Count - 1 do
                    let functionName = strings.[names.[index].GetValue<int>()].GetValue<string>()
                    match SpanTable.definitionOf table functionName with
                    | Some(file, line) ->
                        fileNames.[index] <- JsonValue.Create(fileIndex file)
                        lineNumbers.[index] <- JsonValue.Create line
                    | None -> ()
                    match SpanTable.writtenName table functionName with
                    | Some written ->
                        strings.Add(JsonValue.Create written)
                        names.[index] <- JsonValue.Create(strings.Count - 1)
                    | None -> ()
            Text.Encoding.UTF8.GetBytes(root.ToJsonString())
