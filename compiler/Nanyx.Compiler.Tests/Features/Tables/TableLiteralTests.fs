module Nanyx.Compiler.Tests.Features.TableLiterals

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Table literals (#1255): `[| name | age | ... |]` is a list literal of records, so it is typed as one.

[<Fact>]
let ``A table literal is a list of records of its header's fields`` () =
    let source =
        """
def namesAndAges =
  [| name     | age |
   | "Alex"   | 22  |
   | "Taylor" | 30  |
   | "Sam"    | 28  |
   | "Morgan" | 45  |]
"""
    let typed = compileClean (normalized source)
    Assert.Equal("list(name: string, age: int)", Unifier.tyToDisplayString Ascii typed.Types.["namesAndAges"])

[<Fact>]
let ``A table literal takes the type it is declared as`` () =
    let source =
        """
type Stock = (ticker: string, price: float)

def stocks: list(Stock) =
  [| ticker | price  |
   | "GOOG" | 505.24 |
   | "YHOO" | 27.38  |]

def prices: Array((ticker: string, price: float)) = [| ticker | price |
 | "CNET" | 8.62  |]
"""
    let typed = compileClean (normalized source)
    Assert.Equal("list(Stock)", Unifier.tyToDisplayString Ascii typed.Types.["stocks"])
    Assert.Equal("array(ticker: string, price: float)", Unifier.tyToDisplayString Ascii typed.Types.["prices"])
