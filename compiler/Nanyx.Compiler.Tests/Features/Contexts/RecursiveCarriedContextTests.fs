module Nanyx.Compiler.Tests.Features.RecursiveCarriedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A type generic in contexts that names itself, as a stream does: the rest of a `[c, d] Stream(a)` is one too, and a
// rest that carries fewer contexts fits there. Each of a signature's context variables stays its own (#1908).

let private stream = """type Stream(a) = [k] protected (next: [k] () -> #next(a, Stream(a)) | #done)
"""

[<Fact>]
let ``A rest that carries fewer contexts than the stream fits as its rest`` () =
    let source =
        stream
        + """
def delay: ([c] () -> [d] Stream(a)) -> [c, d] Stream(a) = { f ->
  Stream(
    next = {
      def s = f()
      s.next()
    }
  )
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("([c] () -> [d] Stream(a)) -> [c, d] Stream(a)", Unifier.tyToDisplayString Ascii typed.Types.["delay"])

[<Fact>]
let ``Branches giving streams that carry different contexts join as one that carries them all`` () =
    let source =
        stream
        + """
def append: ([c] Stream(a)), ([d] Stream(a)) -> [c, d] Stream(a) = { s, t ->
  Stream(
    next = {
      match s.next()
        | #next(x, rest) -> #next(x, append(rest, t))
        | #done -> t.next()
    }
  )
}
"""
    let typed = compileClean (normalized source)
    Assert.Equal("([c] Stream(a)), ([d] Stream(a)) -> [c, d] Stream(a)", Unifier.tyToDisplayString Ascii typed.Types.["append"])

[<Fact>]
let ``A context variable of a signature stays when its function is passed on to seq.concatMap`` () =
    let source =
        """
def flat: [c] list(a), ([c] a -> [c] seq(b)) -> list(b) = { xs, f ->
  xs \list.toSeq \seq.concatMap(f) \seq.toList
}

def loud: [Console] () -> list(int) = {
  flat([1, 2], { x ->
    println("at {x}")
    list.toSeq([x])
  })
}
"""
    compileClean (normalized source) |> ignore
    Assert.Equal(Some "def flat: [c] list(a), ([c] a -> [c] seq(b)) -> list(b)", hoverType (normalized source) "flat")
