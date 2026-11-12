module Nanyx.Compiler.Tests.Features.LocalRecContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A local `rec` def's calls to itself require what it requires, and nothing besides: no context variable of its own,
// and none for the def around it.

let private hints (source: string) =
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    TypeHints.inlayHintsWithTopLevel Unicode "" result
    |> List.filter (fun hint -> hint.Kind = TopLevelValue || hint.Kind = LocalValue)
    |> List.map (fun hint -> hint.Name, hint.Type)

[<Fact>]
let ``A local rec def that requires nothing has no context`` () =
    Assert.Equal<(string * string) list>(
        [ "count", "list(α) -> int"; "loop", "list(β) -> int" ],
        hints
            """
def count = { xs ->
  rec loop = {
    | [] -> 0
    | [_, ...rest] -> 1 + loop(rest)
  }
  loop(xs)
}
""")

[<Fact>]
let ``A local rec def that compares values requires only the comparison`` () =
    Assert.Equal<(string * string) list>(
        [ "requiredArgument", "[Eq(α)] list(α), α -> #missingArgument(α) | #some(α)"
          "loop", "list(α) -> #missingArgument(α) | #some(α)" ],
        hints
            """
def requiredArgument = { args, name ->
  rec loop = {
    | [] | [_] -> #missingArgument(name)
    | [flag, value, ...rest] ->
      if flag == name then #some(value) else loop([value, ...rest])
  }

  loop(args)
}
""")

[<Fact>]
let ``A local rec def that calls a function it is given keeps that function's context variable`` () =
    Assert.Equal<(string * string) list>(
        [ "each", "[c] ([c] α -> β), list(α) -> ()"; "loop", "[c] list(α) -> ()" ],
        hints
            """
def each = { f, xs ->
  rec loop = {
    | [] -> ()
    | [x, ...rest] ->
      f(x)
      loop(rest)
  }
  loop(xs)
}
""")

[<Fact>]
let ``A function that prints can be given to a def whose local rec def calls it`` () =
    compileClean (
        """
import nanyx/console

def each = { f, xs ->
  rec loop = {
    | [] -> ()
    | [x, ...rest] ->
      f(x)
      loop(rest)
  }
  loop(xs)
}

def main: [Console] () -> () = { -> each({ n -> println("{n}") }, [7, 8]) }
"""
            .ReplaceLineEndings "\n"
    )
    |> ignore

[<Fact>]
let ``A local rec def that prints requires Console, and so does the def around it`` () =
    Assert.Equal<(string * string) list>(
        [ "tickAll", "[Console] list(α) -> ()"; "loop", "[Console] list(β) -> ()" ],
        hints
            """
import nanyx/console

def tickAll = { items ->
  rec loop = {
    | [] -> ()
    | [_, ...rest] ->
      println("tick")
      loop(rest)
  }
  loop(items)
}
""")

[<Fact>]
let ``A local rec def that calls itself through a function value has no context`` () =
    // `next(total + 1)` calls a function value whose type is not known yet, so its type has a context variable, which
    // no caller of `later` or `loop` can choose: it stands for none.
    Assert.Equal<(string * string) list>(
        [ "later", "int -> int -> int"; "loop", "int -> int -> int"; "next", "int -> int" ],
        hints
            """
def later = { n ->
  rec loop = { k ->
    { total ->
      def next = loop(k - 1)
      if k == 0 then total else next(total + 1)
    }
  }
  loop(n)
}
""")

[<Fact>]
let ``A lambda a local rec def returns that prints requires Console, and nothing besides`` () =
    // The def around it requires Console too, as a def that makes a lambda that prints does.
    Assert.Equal<(string * string) list>(
        [ "later", "[Console] int -> ([Console] int -> int)"
          "loop", "int -> ([Console] int -> int)"
          "next", "[Console] int -> int" ],
        hints
            """
import nanyx/console

def later = { n ->
  rec loop = { k ->
    { total ->
      println("tick")
      def next = loop(k - 1)
      if k == 0 then total else next(total + 1)
    }
  }
  loop(n)
}
""")
