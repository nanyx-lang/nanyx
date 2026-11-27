/// A debug build's probes (#2040, see `CoreIrProbes`): a test host listens to them, as the debugger will, and sees the
/// statements a program passes, its shadow stack, and a frame's locals as text.
module TranspilerWasmTests.Features.Debugging.Probes

open System
open System.IO
open Xunit
open Nanyx.Runtime
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

/// What a test host saw of a run: each probe it passed in `main.nyx`, as its kind and line, the shadow stack's frames in
/// `main.nyx` at each such probe, and the locals the program wrote at the probes `wanted` asked them at.
type private Heard =
    { Passed: string list
      Stacks: string list list
      Locals: (string * string) list }

/// Runs `source`, saved as `main.nyx`, compiled as `nanyx run` compiles it, with a test host listening to its probes,
/// which asks for the locals at each probe `wanted` says to.
let private listen (wanted: Probe -> bool) (source: string) =
    use directory = new TempDirectory()
    directory.Write "main.nyx" source
    // On a large stack, as `nanyx run` compiles: the backend recurses once per operand of a chain of operators (#2946).
    let wat =
        Nanyx.LargeStack.run (fun () ->
            transpileWatFileWith { WasmOptions.Default with Places = true; Probes = true } (Path.Combine(directory.Path, "main.nyx")))
    let stack = ShadowStack()
    let passed = ResizeArray<string>()
    let stacks = ResizeArray<string list>()
    let locals = ResizeArray<string * string>()
    let inMain (frames: Frame list) = frames |> List.filter (fun frame -> frame.Place |> Option.exists (fst >> (=) "main.nyx"))
    let listener =
        { new IProbeListener with
            member _.Probe probe =
                stack.Pass probe
                match probe.Place with
                | (_, "main.nyx", line, _) :: _ ->
                    let kind =
                        match probe.Kind with
                        | Entry name -> $"entry {name}"
                        | Statement -> "statement"
                        | Return -> "return"
                    passed.Add $"{kind} {line}"
                    stacks.Add(inMain stack.Frames |> List.map string)
                    wanted probe
                | _ -> false
            member _.Local(name, text) = locals.Add((name, text)) }
    use output = new MemoryStream()
    use error = new MemoryStream()
    let input = Path.Combine(directory.Path, "input.txt")
    File.WriteAllText(input, "")
    match WasmRunner.runListening (Redirected(input, output, error, [])) [ "main" ] [] (Transpiler.Wasm.WasmBinary.assemble wat) listener with
    | Ok () -> { Passed = List.ofSeq passed; Stacks = List.ofSeq stacks; Locals = List.ofSeq locals }
    | Error error -> failwith $"The program failed: %A{error}"

let private program =
    """def double = { n: int ->
  def twice = n * 2
  twice
}

def sum = { xs: list(int) ->
  def total = list.fold(xs, 0, { a, b -> a + b })
  double(total)
}

println(sum([1, 2, 3]))
"""

/// The probe of a statement on `line`.
let private statementAt (line: int) (probe: Probe) =
    probe.Kind = Statement && (match probe.Place with (_, _, at, _) :: _ -> at = line | [] -> false)

[<Fact>]
let ``A debug build passes each function's entry, each statement and each return, in order`` () =
    let heard = listen (fun _ -> false) program
    // A lambda is numbered by the compiler.
    let passed = heard.Passed |> List.map (fun probe -> if probe.StartsWith "entry __lambda" then "entry <lambda> 7" else probe)
    // The lambda adds each of the three elements, and `sum` ends before its tail call of `double`.
    Assert.Equal<string list>(
        [ "statement 11"
          "entry sum 6"
          "statement 7"
          yield! List.replicate 3 [ "entry <lambda> 7"; "return 7" ] |> List.concat
          "statement 8"
          "return 8"
          "entry double 1"
          "statement 2"
          "statement 3"
          "return 3" ],
        passed |> List.truncate 15)

[<Fact>]
let ``The shadow stack at a statement has a frame for each function the program is in, innermost first`` () =
    let heard = listen (fun _ -> false) program
    let at = heard.Passed |> List.findIndex ((=) "statement 2")
    // `sum` ended before its tail call of `double`, so `double` is called from the top level.
    Assert.Equal<string list>([ "double (main.nyx:2)"; "<top level> (main.nyx:11)" ], heard.Stacks.[at])
    let inFold = heard.Passed |> List.findIndex ((=) "statement 7")
    Assert.Equal<string list>([ "sum (main.nyx:7)"; "<top level> (main.nyx:11)" ], heard.Stacks.[inFold])

[<Fact>]
let ``A frame's locals are written as dbg writes them, those still alive where the program is`` () =
    Assert.Equal<(string * string) list>([ "xs", "[1, 2, 3]" ], (listen (statementAt 7) program).Locals)
    Assert.Equal<(string * string) list>([ "n", "6" ], (listen (statementAt 2) program).Locals)
    // `n` is no longer used once `twice` is defined, so it may have been given up.
    Assert.Equal<(string * string) list>([ "twice", "12" ], (listen (statementAt 3) program).Locals)

[<Fact>]
let ``A statement writes every local of a small function in scope there, by name`` () =
    let source =
        """def total = { n: int ->
  def a = n + 1
  def b = n + 2
  def c = a + b
  a + b + c + n
}

println(total(1))
"""
    Assert.Equal<(string * string) list>([ "a", "2"; "b", "3"; "c", "5"; "n", "1" ], (listen (statementAt 5) source).Locals)

[<Fact>]
let ``A statement writes the parameters and the 64 locals bound last of a function with more in scope`` () =
    let defs = [ for i in 1..70 -> $"  def x{i} = n + {i}" ] |> String.concat "\n"
    let sum = [ for i in 1..70 -> $"x{i}" ] |> String.concat " + "
    let source = $"def total = {{ n: int ->\n{defs}\n  {sum} + n\n}}\n\nprintln(total(0))\n"
    let written = (listen (statementAt 72) source).Locals |> List.map fst |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList ("n" :: [ for i in 7..70 -> $"x{i}" ]), written)

[<Fact>]
let ``A function's entry writes its parameters`` () =
    let entry (probe: Probe) = match probe.Kind with Entry "double" -> true | _ -> false
    Assert.Equal<(string * string) list>([ "n", "6" ], (listen entry program).Locals)

[<Fact>]
let ``A tail call in a loop of a million keeps the stack as it is`` () =
    let heard =
        listen
            (fun _ -> false)
            """def count: int, int -> int = { n, acc ->
  if n == 0 then acc else count(n - 1, acc + 1)
}

println(count(1000000, 0))
"""
    Assert.True(heard.Stacks |> List.forall (fun frames -> frames.Length <= 2))

[<Fact>]
let ``A release build has no probes, nor does a debug build written to a file`` () =
    let wat = transpileWatWith { WasmOptions.Default with Places = true } "println(1)"
    Assert.DoesNotContain("nanyx:debug", wat)
