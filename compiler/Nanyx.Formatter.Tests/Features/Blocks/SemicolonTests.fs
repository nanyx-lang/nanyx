/// How the formatter lays out statements separated by `;` on one line.
module Nanyx.Formatter.Tests.SemicolonTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Statements separated by a semicolon stay on one line, spaced`` () =
    formatsTo
        """def f =
  def x = 1; def y = 2; x + y"""
        """def f =
  def x = 1 ;def y = 2;   x + y"""

[<Fact>]
let ``A line of statements among others keeps its line`` () =
    formatsTo
        """def f =
  log(1); log(2)
  3"""
        """def f =
  log(1); log(2)
  3"""

[<Fact>]
let ``Statements that do not fit on one line go on lines of their own`` () =
    let source =
        normalized """def f =
  log("the first message, which is long"); log("the second message, which is long too"); 3"""
    let expected =
        normalized """def f =
  log("the first message, which is long")
  log("the second message, which is long too")
  3"""
    Assert.Equal(expected, formatWith 60 source)

[<Fact>]
let ``A match arm keeps its statements on its line`` () =
    formatsTo
        """def f = { x ->
  match x
    | 0 -> log(x); x
    | _ -> x
}"""
        """def f = { x ->
  match x
    | 0 -> log(x);x
    | _ -> x
}"""

[<Fact>]
let ``A match arm whose statements do not fit puts them in a block`` () =
    let source =
        normalized """def f = { x ->
  match x
    | 0 -> log("a message that is long"); log("another message"); x
    | _ -> x
}"""
    let expected =
        normalized """def f = { x ->
  match x
    | 0 ->
      log("a message that is long")
      log("another message")
      x
    | _ -> x
}"""
    Assert.Equal(expected, formatWith 50 source)

[<Fact>]
let ``A one line lambda keeps its statements on its line`` () =
    formatsTo "def f = { x -> log(x); x + 1 }" "def f = { x -> log(x);  x + 1 }"

[<Fact>]
let ``A lambda whose statements do not fit puts them on lines of their own`` () =
    let expected =
        normalized """def f = { x ->
  log("a message that is long")
  x + 1
}"""
    Assert.Equal(expected, formatWith 40 (normalized """def f = { x -> log("a message that is long"); x + 1 }"""))

[<Fact>]
let ``An if keeps the statements of its branches on its line`` () =
    formatsTo
        """def f =
  if c then a; b else d; e"""
        """def f =
  if c then a;b else d; e"""

[<Fact>]
let ``An if whose branches do not fit makes them blocks`` () =
    let source =
        normalized """def f =
  if ready then log("starting the work now"); start() else stop()"""
    let expected =
        normalized """def f =
  if ready then
    log("starting the work now")
    start()
  else stop()"""
    Assert.Equal(expected, formatWith 40 source)
