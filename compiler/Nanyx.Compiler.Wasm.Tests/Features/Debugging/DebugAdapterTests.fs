/// `nanyx run --debug`'s Debug Adapter Protocol server (#2041, see `DebugServer`), driven by a client in the test as an
/// editor would drive it: breakpoints, stepping in, over and out, pausing, the stack, a frame's variables expanded, a
/// crash, and what the program writes.
module TranspilerWasmTests.Features.Debugging.DebugAdapter

open System
open System.Collections.Concurrent
open System.IO
open System.IO.Pipes
open System.Text.Json.Nodes
open System.Threading
open Xunit
open Nanyx.Runtime
open Nanyx.Runtime.Debugging
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// A client of a session for one program, as an editor is: it sends requests and reads the responses and events.
type private Client(directory: string, file: string) =
    let toServer = new AnonymousPipeServerStream(PipeDirection.Out)
    let fromServer = new AnonymousPipeServerStream(PipeDirection.In)
    let messages = new BlockingCollection<JsonObject>()
    let mutable sequence = 0
    let mutable exitCode = -1
    let server =
        let wat =
            Nanyx.LargeStack.run (fun () ->
                transpileWatFileWith { WasmOptions.Default with Places = true; Probes = true; CrashToRuntime = true } (Path.Combine(directory, file)))
        let target =
            { Wasm = Transpiler.Wasm.WasmBinary.assemble wat
              Arguments = [ file ]
              Directories = [ directory ]
              FileOf = fun shown -> let path = Path.Combine(directory, shown) in if File.Exists path then Some path else None
              ShownOf = Path.GetFileName
              Report = fun error -> $"{error}\n", 1 }
        let serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle)
        let serverOut = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle)
        Thread(
            (fun () ->
                exitCode <- DebugServer.serve serverIn serverOut target
                serverOut.Dispose()),
            IsBackground = true)
    do
        server.Start()
        Thread(
            (fun () ->
                let mutable reading = true
                while reading do
                    match (try Dap.read fromServer with _ -> None) with
                    | Some message -> messages.Add message
                    | None ->
                        reading <- false
                        messages.CompleteAdding()),
            IsBackground = true)
            .Start()

    /// What the program has written, as `output` events, so far.
    member val Output = Text.StringBuilder()

    /// The next message the predicate takes, recording what the program writes on the way.
    member this.Wait(predicate: JsonObject -> bool) =
        let mutable found = None
        while found.IsNone do
            let mutable message = null
            match messages.TryTake(&message, TimeSpan.FromSeconds 30.0) with
            | true ->
                if Dap.text "type" message = Some "event" && Dap.text "event" message = Some "output" then
                    this.Output.Append(message.["body"].["output"].GetValue<string>()) |> ignore
                if predicate message then found <- Some message
            | _ -> failwith "The debugger said nothing for 30 seconds"
        found.Value

    /// Sends the request `command` with `arguments`, and gives its response's body.
    member this.Request(command: string, ?arguments: (string * JsonNode) list) : JsonNode =
        sequence <- sequence + 1
        let seq = sequence
        Dap.write
            toServer
            (Dap.object [ "seq", Dap.number seq; "type", Dap.string "request"; "command", Dap.string command; "arguments", Dap.object (defaultArg arguments []) ])
        let response = this.Wait(fun message -> Dap.text "type" message = Some "response" && Dap.integer "request_seq" message = Some seq)
        Assert.True(response.["success"].GetValue<bool>(), $"{command} failed: {response.ToJsonString()}")
        response.["body"]

    /// The next event named `name`'s body.
    member this.Event(name: string) : JsonNode =
        (this.Wait(fun message -> Dap.text "type" message = Some "event" && Dap.text "event" message = Some name)).["body"]

    /// Starts the program, with breakpoints at `lines`, and gives the lines they break at.
    member this.Launch(lines: int list, ?stopOnEntry: bool) =
        this.Request("initialize", [ "adapterID", Dap.string "nanyx" ]) |> ignore
        this.Event "initialized" |> ignore
        this.Request("launch", [ "stopOnEntry", Dap.boolean (defaultArg stopOnEntry false) ]) |> ignore
        let set =
            this.Request(
                "setBreakpoints",
                [ "source", Dap.object [ "path", Dap.string (Path.Combine(directory, file)) ]
                  "breakpoints", Dap.array (lines |> List.map (fun line -> Dap.object [ "line", Dap.number line ] :> JsonNode)) ])
        this.Request "configurationDone" |> ignore
        set.["breakpoints"].AsArray() |> Seq.map (fun breakpoint -> breakpoint.["line"].GetValue<int>()) |> List.ofSeq

    /// The reason the program stopped next, and the text that says why, if it says.
    member this.Stopped() =
        let body = this.Event "stopped"
        body.["reason"].GetValue<string>(), Dap.text "text" body

    /// The stack where the program stopped: each frame's name and line, innermost first.
    member this.Stack() =
        (this.Request("stackTrace", [ "threadId", Dap.number 1 ])).["stackFrames"].AsArray()
        |> Seq.map (fun frame -> frame.["name"].GetValue<string>(), frame.["line"].GetValue<int>())
        |> List.ofSeq

    /// The variables of `reference`, each with its value and the reference of its parts (0 for none).
    member this.Variables(reference: int) =
        (this.Request("variables", [ "variablesReference", Dap.number reference ])).["variables"].AsArray()
        |> Seq.map (fun variable -> variable.["name"].GetValue<string>(), variable.["value"].GetValue<string>(), variable.["variablesReference"].GetValue<int>())
        |> List.ofSeq

    /// The name of frame `frame`'s scope, and its locals.
    member this.Locals(frame: int) =
        let scope = (this.Request("scopes", [ "frameId", Dap.number frame ])).["scopes"].[0]
        let reference = scope.["variablesReference"].GetValue<int>()
        scope.["name"].GetValue<string>(), (if reference = 0 then [] else this.Variables reference |> List.map (fun (name, value, _) -> name, value))

    /// Goes on as `command` says, `continue`, `next`, `stepIn` or `stepOut`.
    member this.Resume(command: string) = this.Request(command, [ "threadId", Dap.number 1 ]) |> ignore

    /// Waits for the program to end, and gives the exit code it ended with.
    member this.Exited() =
        let body = this.Event "exited"
        this.Event "terminated" |> ignore
        this.Request "disconnect" |> ignore
        server.Join(TimeSpan.FromSeconds 30.0) |> ignore
        body.["exitCode"].GetValue<int>()

    interface IDisposable with
        member _.Dispose() =
            toServer.Dispose()
            fromServer.Dispose()

/// A program with a function that calls another, a loop and a record.
let private program =
    """def double = { n: int ->
  def twice = n * 2
  twice
}

def sum = { xs: list(int) ->
  def total = list.fold(xs, 0, { a, b -> a + b })
  double(total)
}

def ada = (name = "Ada", tags = [#a, #b])
println(sum([1, 2, 3]))
println(ada.name)
"""

/// A client of a session for `source`, saved as `main.nyx`.
let private session (source: string) =
    let directory = Directory.CreateTempSubdirectory("nanyx-dap-").FullName
    File.WriteAllText(Path.Combine(directory, "main.nyx"), source)
    new Client(directory, "main.nyx")

[<Fact>]
let ``The program stops at a breakpoint, moved to the next line with a statement, with the stack and the frame's locals`` () =
    use client = session program
    Assert.Equal<int list>([ 7 ], client.Launch [ 5 ])
    Assert.Equal(("breakpoint", None), client.Stopped())
    Assert.Equal<(string * int) list>([ "sum", 7; "<top level>", 12 ], client.Stack())
    Assert.Equal(("Locals", [ "xs", "[1, 2, 3]" ]), client.Locals 1)
    client.Resume "continue"
    Assert.Equal(0, client.Exited())
    Assert.Equal("12\nAda\n", client.Output.ToString())

[<Fact>]
let ``Stepping in goes into the function a statement calls, over runs through it, and out returns to the caller`` () =
    use client = session program
    client.Launch [ 8 ] |> ignore
    client.Stopped() |> ignore
    Assert.Equal<(string * int) list>([ "sum", 8; "<top level>", 12 ], client.Stack())
    client.Resume "stepIn"
    Assert.Equal("step", fst (client.Stopped()))
    Assert.Equal<(string * int) list>([ "double", 2; "<top level>", 12 ], client.Stack())
    Assert.Equal(("Locals", [ "n", "6" ]), client.Locals 1)
    client.Resume "next"
    client.Stopped() |> ignore
    Assert.Equal<(string * int) list>([ "double", 3; "<top level>", 12 ], client.Stack())
    Assert.Equal(("Locals", [ "twice", "12" ]), client.Locals 1)
    client.Resume "stepOut"
    client.Stopped() |> ignore
    Assert.Equal<(string * int) list>([ "<top level>", 13 ], client.Stack())
    client.Resume "continue"
    Assert.Equal(0, client.Exited())

[<Fact>]
let ``A frame further out shows its locals as of its call, though the program never stopped in it, a large value cut short`` () =
    use client =
        session "def inner = { n: int ->\n  n * 2\n}\n\ndef outer = { xs: list(int) ->\n  def count = list.length(xs)\n  inner(count) + list.length(xs)\n}\n\nprintln(outer(list.init(500, { i -> i })))\n"
    client.Launch [ 2 ] |> ignore
    client.Stopped() |> ignore
    Assert.Equal<string list>([ "inner"; "outer"; "<top level>" ], client.Stack() |> List.map fst)
    // The frame it stopped in is written whole.
    Assert.Equal(("Locals", [ "n", "500" ]), client.Locals 1)
    // `outer`'s are as of its last statement, the call: each written within a budget as the program went on.
    let name, locals = client.Locals 2
    Assert.Equal("Locals at line 7, values cut short: stop in this frame to see them whole", name)
    Assert.Contains(("count", "500"), locals)
    let xs = locals |> List.find (fun (local, _) -> local = "xs") |> snd
    Assert.StartsWith("[0, 1, 2", xs)
    Assert.EndsWith(", …]", xs)
    client.Resume "continue"
    client.Exited() |> ignore

[<Fact>]
let ``A record and a list in a frame's locals expand into their parts`` () =
    // Made of `n`, so that the record isn't a constant, which is a global, not a local.
    use client = session "def greet = { n: int ->\n  def ada = (name = \"Ada\", age = n, tags = [#a, #b])\n  println(ada.name)\n  n\n}\n\nprintln(greet(1))\n"
    client.Launch [ 3 ] |> ignore
    client.Stopped() |> ignore
    let scope = (client.Request("scopes", [ "frameId", Dap.number 1 ])).["scopes"].[0].["variablesReference"].GetValue<int>()
    let _, value, reference = client.Variables scope |> List.find (fun (name, _, _) -> name = "ada")
    Assert.Equal("(age = 1, name = \"Ada\", tags = [#a, #b])", value)
    let parts = client.Variables reference
    Assert.Equal<string list>([ "age"; "name"; "tags" ], parts |> List.map (fun (name, _, _) -> name))
    let _, _, tags = parts |> List.find (fun (name, _, _) -> name = "tags")
    Assert.Equal<(string * string * int) list>([ "[0]", "#a", 0; "[1]", "#b", 0 ], client.Variables tags)
    client.Resume "continue"
    client.Exited() |> ignore

[<Fact>]
let ``A program launched to stop on entry stops at its first statement`` () =
    use client = session program
    client.Launch([], stopOnEntry = true) |> ignore
    Assert.Equal("pause", fst (client.Stopped()))
    client.Resume "continue"
    Assert.Equal(0, client.Exited())

[<Fact>]
let ``A pause stops a program that is running, at the next statement`` () =
    use client = session "def spin: int -> int = { n ->\n  def next = if n > 0 then n - 1 else 1000\n  spin(next)\n}\n\nprintln(spin(10))\n"
    client.Launch [] |> ignore
    Thread.Sleep 200
    client.Request "pause" |> ignore
    Assert.Equal("pause", fst (client.Stopped()))
    Assert.Equal("spin", fst (List.head (client.Stack())))
    // The program ends before the request is answered, its `terminated` event first.
    client.Request "terminate" |> ignore

[<Fact>]
let ``A crash stops the program at the frame that crashed, with its message, and continuing ends it`` () =
    use client = session "def inner = { n: int ->\n  def limit = 2\n  if n > limit then crash(\"too big: {n}\") else n\n}\n\nprintln(inner(1))\nprintln(inner(5))\n"
    client.Launch [] |> ignore
    Assert.Equal(("exception", Some "too big: 5"), client.Stopped())
    Assert.Equal<string list>([ "crash"; "inner"; "<top level>" ], client.Stack() |> List.map fst)
    client.Resume "continue"
    Assert.Equal(1, client.Exited())
    Assert.StartsWith("1\n", client.Output.ToString())

[<Fact>]
let ``--debug with --profile, a memory check or --dbg-to is a usage error, and the program doesn't run`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "println(\"ran\")\n"
    for extra in [ [ "--profile"; "p.json" ]; [ "--check-memory" ]; [ "--dbg-to"; "hits.jsonl" ] ] do
        let outcome = nanyxIn directory.Path ([ "run"; "--debug" ] @ extra @ [ "main.nyx" ])
        Assert.Equal(2, outcome.ExitCode)
        Assert.StartsWith("error: --debug runs the program under the debugger", outcome.StandardError)
        Assert.Equal("", outcome.StandardOutput)
