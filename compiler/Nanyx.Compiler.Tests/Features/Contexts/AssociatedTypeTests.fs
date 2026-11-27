module Nanyx.Compiler.Tests.Features.AssociatedTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// Associated types: a context's member of kind `type`, which a signature names, a constraint fixes by name, and an
// instance's members fix (see `AssociatedTypes`).

let private iter =
    """context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)

type Bag = protected (items: list(int))

def Bag.next: Bag -> #some(int, Bag) | #done = { bag ->
  match bag.items
    | [] -> #done
    | [first, ...rest] -> #some(first, Bag(items = rest))
}
"""

let private withIter (source: string) = normalized (iter + source)

[<Fact>]
let ``A context's associated type is its last parameter, which its members name`` () =
    let typed = compileClean (withIter "")
    let info = typed.TypeDefs |> Map.find "Iter"
    Assert.Equal<string list>([ "elem" ], info.AssociatedTypes)
    Assert.Equal<string option list>([ Some "c"; Some "elem" ], info.Parameters |> List.map _.Name)
    match Unifier.unalias info.Underlying with
    | TyRecord(members, _) -> Assert.Equal<string list>([ "next" ], members.Keys |> List.ofSeq)
    | other -> failwith $"Expected the context's members, got %A{other}"

[<Fact>]
let ``Hovering a context's name shows its associated types as it declares them`` () =
    let source = withIter "\ndef f: [Iter(c)] c -> () = { xs -> () }\n"
    let result = Compiler.compile source
    let hover = Definition.typeDefinitionAt Unicode "" result (offsetOf source "Iter(c)]" 0 1)
    Assert.Equal(Some "context Iter(c) = (elem: type, next: (c -> #some(elem, c) | #done))", hover |> Option.map _.Type)

[<Fact>]
let ``A signature names an associated type bare, and a call's instance fixes it`` () =
    let source =
        withIter
            """
def headOr: [Iter(c)] (c, elem) -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}

def first = headOr(Bag(items = [1]), 0)
"""
    let typed = compileClean source
    Assert.Equal("int", Unifier.tyToString typed.Types.["first"])

[<Fact>]
let ``The instance fixes an associated type the call's arguments don't`` () =
    let source =
        withIter
            """
def firstOf: [Iter(c)] c -> #some(elem) | #none = { items ->
  match next(items)
    | #some(value, _) -> #some(value)
    | #done -> #none
}

def first = firstOf(Bag(items = [1]))
"""
    let typed = compileClean source
    Assert.Equal("#some(int) | #none", Unifier.tyToString typed.Types.["first"])

[<Fact>]
let ``Iter(c).elem names the associated type of that context`` () =
    let source =
        withIter
            """
def firstPair: [Iter(c), Iter(d)] (c, d) -> #some((Iter(c).elem, Iter(d).elem)) | #none = { xs, ys ->
  match Iter(c).next(xs), Iter(d).next(ys)
    | #some(x, _), #some(y, _) -> #some((x, y))
    | _, _ -> #none
}

def pair = firstPair(Bag(items = [1]), Bag(items = [2]))
"""
    let typed = compileClean source
    Assert.Equal("#some(int, int) | #none", Unifier.tyToString typed.Types.["pair"])

[<Fact>]
let ``A constraint fixes an associated type by name`` () =
    let source =
        withIter
            """
def joinAll: [Iter(c, elem = string)] c -> string = { items ->
  match next(items)
    | #some(item, rest) -> item ++ joinAll(rest)
    | #done -> ""
}
"""
    compileClean source |> ignore

[<Fact>]
let ``A call whose instance fixes the associated type otherwise than the constraint is an error`` () =
    let source =
        withIter
            """
def joinAll: [Iter(c, elem = string)] c -> string = { items -> "" }

def joined = joinAll(Bag(items = [1]))
"""
    let result = Compiler.compile source
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Contains("'Iter(Bag, elem = string)'", diagnostic.Message)

[<Fact>]
let ``A bare name two contexts have is an error that shows the qualified names`` () =
    let source = withIter "\ndef both: [Iter(c), Iter(d)] (c, d) -> elem = { xs, ys -> ??? }\n"
    let result = Compiler.compile source
    let diagnostic = result.Diagnostics |> List.find (fun diagnostic -> diagnostic.Code = DiagnosticCodes.AmbiguousAssociatedType.Code)
    Assert.Equal(
        "'elem' is an associated type of 'Iter(c)' and 'Iter(d)': write which one, as `Iter(c).elem` or `Iter(d).elem`",
        diagnostic.Message)
    Assert.Equal("elem", textAt source (spanOf diagnostic))

[<Fact>]
let ``A bare name no context has is still a reserved name`` () =
    let code, text = singleError (withIter "\ndef f: [Iter(c)] c -> item = { xs -> ??? }\n")
    Assert.Equal(DiagnosticCodes.InvalidTypeName.Code, code)
    Assert.Equal("item", text)

[<Fact>]
let ``Fixing an associated type the context doesn't have is an error`` () =
    let code, text = singleError (withIter "\ndef f: [Iter(c, item = int)] c -> () = { xs -> () }\n")
    Assert.Equal(DiagnosticCodes.UnknownAssociatedType.Code, code)
    Assert.Equal("item = int", text)

[<Fact>]
let ``Fixing a member that is not a type is an error`` () =
    let result = Compiler.compile (withIter "\ndef f: [Iter(c, next = int)] c -> () = { xs -> () }\n")
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnknownAssociatedType.Code, diagnostic.Code)
    Assert.Equal(
        "It is a member of 'Iter', not one of its associated types: only an associated type is fixed by name in a context's arguments",
        diagnostic.Message)

[<Fact>]
let ``Naming the associated type of a context the signature doesn't require is an error`` () =
    let result = Compiler.compile (withIter "\ndef f: [Iter(c)] c -> Iter(d).elem = { xs -> ??? }\n")
    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Code = DiagnosticCodes.UnknownAssociatedType.Code)

[<Fact>]
let ``An instance whose members leave an associated type unfixed is an error`` () =
    let source =
        withIter
            """
context Sized(c) = (elem: type, size: c -> int)

def Bag.size: Bag -> int = { bag -> 0 }

def total: [Sized(c)] c -> int = { xs -> size(xs) }

def n = total(Bag(items = []))
"""
    let result = Compiler.compile source
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnfixedAssociatedType.Code, diagnostic.Code)
    Assert.Equal(
        "The instance 'Sized(Bag)' leaves its associated type 'elem' unfixed: no member's type says what it is, so declare it, as in `type Bag.elem = ...`",
        diagnostic.Message)

[<Fact>]
let ``An attached alias fixes an associated type no member mentions`` () =
    let source =
        withIter
            """
context Sized(c) = (elem: type, size: c -> int)

def Bag.size: Bag -> int = { bag -> 0 }

type Bag.elem = int

def total: [Sized(c)] c -> int = { xs -> size(xs) }

def n = total(Bag(items = []))
"""
    compileClean source |> ignore

[<Fact>]
let ``An instance whose members fix an associated type two ways is an error`` () =
    let source =
        withIter
            """
context Two(c) = (elem: type, first: c -> elem, second: c -> elem)

def Bag.first: Bag -> int = { bag -> 0 }

def Bag.second: Bag -> string = { bag -> "" }

def go: [Two(c)] c -> () = { xs -> () }

def ran = go(Bag(items = []))
"""
    let result = Compiler.compile source
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.AssociatedTypeFixedTwice.Code, diagnostic.Code)
    Assert.Equal(
        "The instance 'Two(Bag)' fixes its associated type 'elem' two ways: 'Bag.second' makes it 'string', where it is already 'int'",
        diagnostic.Message)

[<Fact>]
let ``An instance a use loads that leaves an associated type unfixed is an error`` () =
    let source =
        withIter
            """
context Sized(c) = (elem: type, size: c -> int)

def f: Bag -> int = { bag ->
  use Sized(Bag)(size = { x -> 1 })
  1
}
"""
    let result = Compiler.compile source
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnfixedAssociatedType.Code, diagnostic.Code)
    Assert.EndsWith("so fix it by name, as in `use Sized(Bag, elem = ...)(...)`", diagnostic.Message)

[<Fact>]
let ``A use fixes an associated type by name`` () =
    let source =
        withIter
            """
context Sized(c) = (elem: type, size: c -> int)

def total: [Sized(c)] c -> int = { xs -> size(xs) }

def f: Bag -> int = { bag ->
  use Sized(Bag, elem = int)(size = { x -> 1 })
  total(bag)
}
"""
    compileClean source |> ignore

[<Fact>]
let ``An associated type can't have a default`` () =
    let code, _ = singleError "context D(c) = (elem: type = int, get: c -> elem)\n"
    Assert.Equal(DiagnosticCodes.InvalidAssociatedType.Code, code)

[<Fact>]
let ``A built-in type's name can't be an associated type's`` () =
    let result = Compiler.compile "context D(c) = (list: type, get: c -> list)\n"
    let diagnostic = result.Diagnostics |> List.find (fun diagnostic -> diagnostic.Code = DiagnosticCodes.InvalidAssociatedType.Code)
    Assert.Equal("'list' is a built-in type, so it can't be the name of an associated type", diagnostic.Message)

[<Fact>]
let ``type is only an associated type's type`` () =
    let code, text = singleError "type R = (x: type)\n"
    Assert.Equal(DiagnosticCodes.InvalidAssociatedType.Code, code)
    Assert.Equal("type", text)

[<Fact>]
let ``An imported context's associated type is named and fixed as a local one's is`` () =
    let library =
        """module mod

export context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)

export def headOr: [Iter(c)] c, elem -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}
"""
    let main =
        """import mod

type Bag = protected (items: list(int))

def Bag.next: Bag -> #some(int, Bag) | #done = { bag -> #done }

def strings: [Iter(c, elem = string)] c -> string = { items -> headOr(items, "") }

def first = headOr(Bag(items = []), 0)
"""
    let result = compileImporting (normalized library) (normalized main)
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("int", Unifier.tyToString result.Typed.Value.Types.["first"])

[<Fact>]
let ``A signature's associated type is undetermined by its parameters, and that is fine`` () =
    let source =
        withIter
            """
def countAll: [Iter(c)] c -> int = { items ->
  match next(items)
    | #some(_, rest) -> 1 + countAll(rest)
    | #done -> 0
}
"""
    compileClean source |> ignore

[<Fact>]
let ``An associated type is a Type value, qualified or bare`` () =
    let source =
        withIter
            """
def elementType: [Iter(c)] c -> Type(elem) = { xs -> type Iter(c).elem }

def bareType: [Iter(c)] c -> Type(elem) = { xs -> type elem }

def t = elementType(Bag(items = []))
def u = bareType(Bag(items = []))
"""
    let typed = compileClean source
    Assert.Equal("Type(int)", Unifier.tyToString typed.Types.["t"])
    Assert.Equal("Type(int)", Unifier.tyToString typed.Types.["u"])

[<Fact>]
let ``A Type value of an associated type names a context the signature requires`` () =
    let code, text = singleError (withIter "\ndef f: [Iter(c)] c -> () = { xs ->\n  def t = type Iter(d).elem\n  ()\n}\n")
    Assert.Equal(DiagnosticCodes.UnknownAssociatedType.Code, code)
    Assert.Equal("Iter(d).elem", text)

/// What hover shows, in `notation`, on the def `name` of `source`, which must compile cleanly.
let private hoverOn (notation: TypeNotation) (source: string) (name: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Hover.typeAt notation result (offsetOf source $"def {name}" 0 4) |> Option.map _.Type

// A signature's variable named after an associated type was written bare, `[Iter(c)] c, elem -> elem`, which reads as a
// type's name. It is a variable bound in its context, with the next free Latin letter, whatever the notation (#3047).
[<Fact>]
let ``Hover binds a signature's associated type the type uses to a variable in its context`` () =
    let source =
        withIter
            """
def headOr: [Iter(c)] c, elem -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}
"""
    Assert.Equal(Some "def headOr: [Iter(c, elem = a)] c, a -> a", hoverOn Ascii source "headOr")
    Assert.Equal(Some "def headOr: [Iter(c, elem = a)] c, a -> a", hoverOn Unicode source "headOr")

[<Fact>]
let ``A message binds a signature's associated type as hover does`` () =
    let source =
        withIter
            """
def runPure: (Bag -> int) -> int = { g -> 1 }

def pass: ([Iter(c)] c -> elem) -> int = { f -> runPure(f) }
"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.StartsWith("'[Iter(c, elem = a)] c -> a' is given where 'Bag -> int' is expected", message)

[<Fact>]
let ``Hover leaves out an associated type the type doesn't use`` () =
    let source =
        withIter
            """
def countAll: [Iter(c)] c -> int = { items ->
  match next(items)
    | #some(_, rest) -> 1 + countAll(rest)
    | #done -> 0
}
"""
    Assert.Equal(Some "def countAll: [Iter(c)] c -> int", hoverOn Unicode source "countAll")

[<Fact>]
let ``Hover binds Num's fraction by name, declared with Latin letters and inferred with Greek`` () =
    let source =
        withFinalNewline
            """dimension Distance
@baseUnit unit m: Distance

def speed: Distance(a), Time(a) -> Distance(Num(a).fraction) / Time(Num(a).fraction) = { d, t -> d / t }

def ratio: [Num(a)] a, a -> fraction = { x, y -> x / y }

def divide = { x, y -> x / y }"""
    Assert.Equal(Some "def speed: [Num(a, fraction = b)] Distance(a), Time(a) -> Distance(b) / Time(b)", hoverOn Unicode source "speed")
    Assert.Equal(Some "def ratio: [Num(a, fraction = b)] a, a -> b", hoverOn Unicode source "ratio")
    Assert.Equal(Some "def divide: [Num(α, fraction = β)] α, α -> β", hoverOn Unicode source "divide")
    Assert.Equal(Some "def divide: [Num(a, fraction = b)] a, a -> b", hoverOn Ascii source "divide")

[<Fact>]
let ``Two contexts' associated types of one name are two variables`` () =
    let source =
        withIter
            """
def headOr: [Iter(c)] c, elem -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}

def both = { xs, ys, x, y -> (headOr(xs, x), headOr(ys, y)) }
"""
    Assert.Equal(Some "def both: [Iter(α, elem = γ), Iter(β, elem = δ)] α, β, γ, δ -> (γ, δ)", hoverOn Unicode source "both")

[<Fact>]
let ``A signature writes an associated type a constraint fixes, or inference leaves open, by name`` () =
    let source =
        withIter
            """
def joinAll: [Iter(c, elem = string)] c -> string = { items ->
  match next(items)
    | #some(item, rest) -> item ++ joinAll(rest)
    | #done -> ""
}

def headOr: [Iter(c)] c, elem -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}

def firstOr = { items, fallback -> headOr(items, fallback) }
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let types = TypeHints.topLevelTypes Ascii "" result |> List.map (fun hint -> hint.Name, hint.Type) |> Map.ofList
    Assert.Equal("[Iter(c, elem = string)] c -> string", types.["joinAll"])
    Assert.Equal("[Iter(a, elem = b)] a, b -> b", types.["firstOr"])

// Messages made while the constraints are solved wrote a context's associated types positionally, `Iter(c, elem)`,
// where hover writes them as a signature does (#2108).
[<Fact>]
let ``A message made while solving writes a context's associated types as a signature does`` () =
    let source =
        withIter
            """
def runPure: (Bag -> int) -> int = { g -> 1 }

def pass: ([Iter(c, elem = string)] c -> int) -> int = { f -> runPure(f) }
"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal(
        "'[Iter(c, elem = string)] c -> int' is given where 'Bag -> int' is expected, which does not require the context 'Iter(c, elem = string)': write the contexts it may require on the type expected, as a context's member does in `log: [Console] string -> ()`, or a context variable for any, as in `[c] a -> b`",
        message
    )
