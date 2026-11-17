module Nanyx.Compiler.Tests.Features.UseLoads

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// A `use` loads a context's constructor, `use Log(...)` or `use C(T)(...)`, or a name a lambda binds with `context`.
// Anything else, a record, a def's name, a call or a field, is NYX0249, reported at the use's value (#1448).

let private log = """context Log = (log: string -> ())

def logged: [Log] () -> () = { log("x") }
"""

/// The code of the one diagnostic `log` followed by `source` compiles to, and the text it is reported at.
let private reported (source: string) = singleError ((log + source).ReplaceLineEndings "\n")

let private useOfNonInstance (text: string) = DiagnosticCodes.UseOfNonInstance.Code, text

[<Fact>]
let ``A use of a record is reported at the record`` () =
    Assert.Equal(
        useOfNonInstance "(log = { m: string -> () })",
        reported """def main = {
  use (log = { m: string -> () })
  ()
}
""")

[<Fact>]
let ``A use of a def's name is reported at the name`` () =
    Assert.Equal(
        useOfNonInstance "addInt",
        reported """def addInt = (`+` = { x: int, y: int -> x * 10 + y })

def main = {
  use addInt
  ()
}
""")

[<Fact>]
let ``A use of a call is reported at the call`` () =
    Assert.Equal(
        useOfNonInstance "makeLog()",
        reported """def makeLog = { (log = { m: string -> () }) }

def main = {
  use makeLog()
  ()
}
""")

[<Fact>]
let ``A use of a field is reported at the field access`` () =
    Assert.Equal(
        useOfNonInstance "holder.log",
        reported """def holder = (log = (log = { m: string -> () }))

def main = {
  use holder.log
  ()
}
""")

[<Fact>]
let ``A use ... in of something other than an instance is reported at its value`` () =
    Assert.Equal(useOfNonInstance "(log = { m: string -> () })", reported "def main = use (log = { m: string -> () }) in ()\n")
    Assert.Equal(
        useOfNonInstance "addInt",
        reported """def addInt = (`+` = { x: int, y: int -> x * 10 + y })

def main = use addInt in ()
""")

[<Fact>]
let ``A use of a name a lambda's parameter binds is reported, as only a context name can be loaded`` () =
    Assert.Equal(useOfNonInstance "l", reported "def run: int -> () = { l -> use l in () }\n")

[<Fact>]
let ``A use of a constructor, a parameterised context's instance, or a context name compiles`` () =
    compileClean (
        (log
         + """context Show(a) = (show: a -> string)

def describe: [Show(a)] a -> string = { value -> Show(a).show(value) }

def statement = {
  use Log(log = { m -> () })
  logged()
}

def expression = use Log(log = { m -> () }) in logged()

def shown = use Show(int)(show = { n -> "{n}" }) in describe(1)

def named: [Log] () -> () = { context l ->
  use l
  logged()
}
""")
            .ReplaceLineEndings "\n")
    |> ignore

[<Fact>]
let ``A context is still served by a loaded instance of a larger one that has its members`` () =
    // `add` requires `Add(string)`, and is passed the `Num(string)` the use loads, which has every member an
    // `Add(string)` has. `string` has no `Add` of its own for the use to replace.
    let typed =
        compileClean (
            """def add: [Add(a)] a, a -> a = { x, y -> x + y }

def main = {
  use Num(string)(
    zero = ""
    `+` = { x, y -> x ++ y }
    `-` = { x, y -> x }
    negate = { x -> x }
    `*` = { x, y -> x }
    `/` = { x, y -> x }
    `%` = { x, y -> x }
    fromInt = { n -> "n" }
    isInteger = false
  )
  add("1", "2")
}
"""
                .ReplaceLineEndings "\n")
    Assert.Equal("() -> string", Unifier.tyToString typed.Types.["main"])
    match instancesOf typed with
    | [ InstanceInScope(Kind (TypeApply ("Num", [ Kind (TypeName "string") ])), FromUse _) ] -> ()
    | other -> failwith $"Expected the Num(string) the use loads, got %A{other}"
