module Nanyx.Compiler.Tests.Features.Types.Unions

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck treats union order as assignable`` () =
    let source =
        "def f: (#some(int) | #notFound) -> int = { x -> 1 }\n" +
        "def g: (#notFound | #some(int)) -> int = f\n" +
        "def result = g(#some(1))"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck allows wider unions in assignment`` () =
    let source =
        "def f: (#some(int) | #notFound) -> int = { x -> 1 }\n" +
        "def g: (#some(int) | #notFound | #error(string)) -> int = f\n" +
        "def result = g(#notFound)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck supports tag union rest parameter`` () =
    let source =
        "def mapOption: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->\n" +
        "  match o\n" +
        "  | #some(v) -> #some(f(v))\n" +
        "  | other -> other\n" +
        "}\n" +
        "def result = mapOption(#some(1), { x -> x })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    // `#some(1)` has no other tags, so the rest is empty.
    Assert.Equal(TyTag("some", Some (TyPrimitive "int")), typed.Types.["result"])

[<Fact>]
let ``Typecheck requires catch-all for tag union rest`` () =
    let source =
        "def mapOption: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->\n" +
        "  match o\n" +
        "  | #some(v) -> #some(f(v))\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("catch-all", message)

[<Fact>]
let ``Typecheck tag union width and rest regression`` () =
    let source =
        "def u: #some(string) | #nil = #nil\n" +
        "def g: (#some(string) | #nil | #notFound) -> string = { o -> \"\" }\n" +
        "def _ = g(u)\n" +
        "def mapOption: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->\n" +
        "  match o\n" +
        "  | #some(v) -> #some(f(v))\n" +
        "  | other -> other\n" +
        "}\n" +
        "def l: #some(string) | #nil = #some(\"\")\n" +
        "def mapper: string -> int = { s -> 1 }\n" +
        "def l2 = l \\mapOption(mapper)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["l2"] with
    | TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("nil", None) ] -> Assert.True(true)
    | other -> Assert.True(false, $"Unexpected l2 type: %A{other}")

[<Fact>]
let ``A piped lambda can match only the tags left of a union's rest`` () =
    // `parsed` widens into `#some(α) | ω`, which leaves `ω` as `#invalidInt` and room for more. The lambda handles
    // `#invalidInt` alone, as it may when the same call is written with its arguments in parentheses (#452).
    let source =
        "def mapError: #some(α) | ω, (ω -> #some(α) | b) -> #some(α) | b = {\n" +
        "  | #some(x), _ -> #some(x)\n" +
        "  | other, f -> f(other)\n" +
        "}\n" +
        "def parsed: #some(int) | #invalidInt = #invalidInt\n" +
        "def direct = mapError(parsed, { #invalidInt -> #bad(\"y\") })\n" +
        "def piped = parsed \\mapError({ #invalidInt -> #bad(\"y\") })"
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    let typed = result.Typed.Value
    let expected = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("bad", Some (TyPrimitive "string")) ]
    Assert.Equal(expected, typed.Types.["direct"])
    Assert.Equal(expected, typed.Types.["piped"])

[<Fact>]
let ``Typecheck binds a catch-all after a tag arm at the union's rest`` () =
    // `other` cannot be a `#some`, so it is the rest `r`: returning it does not force `a` and `b` together.
    let source =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def x: #some(int) | #notFound | #error = #some(1)\n" +
        "def y = mapOption(x, { n -> \"{n}\" })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typed = result.Typed.Value
    match typed.Types.["mapOption"] with
    | TyFunc(TyRecord fields, TyUnion [ TyTag("some", Some (TyVar b)); TyVar resultRest ]) ->
        match fields.["0"], fields.["1"] with
        | TyUnion [ TyTag("some", Some (TyVar a)); TyVar inputRest ], TyFunc(TyVar fa, TyVar fb) ->
            Assert.NotEqual(a.Id, b.Id)
            Assert.Equal(a.Id, fa.Id)
            Assert.Equal(b.Id, fb.Id)
            Assert.Equal(inputRest.Id, resultRest.Id)
        | other -> Assert.True(false, $"Unexpected mapOption parameters: %A{other}")
    | other -> Assert.True(false, $"Unexpected mapOption type: %A{other}")
    match typed.Types.["y"] with
    | TyUnion [ TyTag("some", Some (TyPrimitive "string")); TyTag("error", None); TyTag("notFound", None) ] -> ()
    | other -> Assert.True(false, $"Unexpected y type: %A{other}")

[<Fact>]
let ``Typecheck matches a call's result whose union rest is already solved`` () =
    // `mapOption`'s result is `#some(b) | r`, but here `r` is `#notFound`, so naming both tags is exhaustive.
    let mapOption =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def x: #some(int) | #notFound = #notFound\n"
    let exhaustive =
        mapOption +
        "def y = {\n" +
        "  def a = x \\mapOption { n -> n + 1 }\n" +
        "  match a\n" +
        "    | #some(n) -> n\n" +
        "    | #notFound -> 0\n" +
        "}\n"
    let result = Compiler.compile exhaustive
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let missing =
        mapOption +
        "def y = {\n" +
        "  def a = x \\mapOption { n -> n + 1 }\n" +
        "  match a\n" +
        "    | #some(n) -> n\n" +
        "}\n"
    let result = Compiler.compile missing
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("missing tags: notFound", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck rejects a tag union with a rest where a string is expected`` () =
    // The rest `r` stands for more tags, so it cannot make `#some(string) | r` a `string`.
    let source =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def takesString: string -> string = { s -> s }\n" +
        "def x: #some(int) | #notFound = #notFound\n" +
        "def y = takesString(x \\mapOption { n -> \"{n}\" })"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("mismatch", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck joins the tags of if branches into a union`` () =
    let source =
        "def getMessage = {\n" +
        "  def message = \"Hello\"\n" +
        "  if false then #some(message)\n" +
        "  else #noMessage\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expected = TyFunc(TyPrimitive "unit", TyUnion [ TyTag("some", Some (TyPrimitive "string")); TyTag("noMessage", None) ])
    Assert.Equal(expected, typed.Types.["getMessage"])

[<Fact>]
let ``Typecheck joins the tags of match arms into a union`` () =
    let source =
        "def classify = { n ->\n" +
        "  match n\n" +
        "    | 0 -> #zero\n" +
        "    | 1 -> #one(n)\n" +
        "    | _ -> #many(n)\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expected =
        TyFunc(TyPrimitive "int", TyUnion [ TyTag("zero", None); TyTag("one", Some (TyPrimitive "int")); TyTag("many", Some (TyPrimitive "int")) ])
    Assert.Equal(expected, typed.Types.["classify"])

[<Fact>]
let ``Typecheck joins nested if branches and repeated tags once`` () =
    let source =
        "def pick = { n ->\n" +
        "  if n == 0 then #zero\n" +
        "  else if n == 1 then #small(n)\n" +
        "  else if n == 2 then #small(n)\n" +
        "  else #big\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expected = TyFunc(TyPrimitive "int", TyUnion [ TyTag("zero", None); TyTag("small", Some (TyPrimitive "int")); TyTag("big", None) ])
    Assert.Equal(expected, typed.Types.["pick"])

[<Fact>]
let ``Typecheck joins the tags inside tuple branches field by field`` () =
    let source =
        "def splitOnce: (string, string) -> (string, #some(string) | #none) = { s, separator ->\n" +
        "  match s == separator\n" +
        "    | true -> (\"\", #some(s))\n" +
        "    | false -> (s, #none)\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck still rejects tuple branches whose fields disagree`` () =
    let source = "def f = { c -> if c then (1, #a) else (\"one\", #b) }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck still rejects same-named tags with different payloads across branches`` () =
    let source = "def f = { c -> if c then #some(1) else #some(\"one\") }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("mismatch", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck still rejects branches of different non-tag types`` () =
    let source = "def f = { c -> if c then 1 else \"one\" }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("Type mismatch", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck leaves the scrutinee union open when a match has a catch-all arm`` () =
    // `| #some(v) -> ... | _ -> ...` names only one tag; the scrutinee may still be a wider union.
    let result =
        Compiler.compile (
            "def get: int -> #some(int) | #none = { n -> #some(n) }\n" +
            "def d = { n -> match get(n)\n" +
            "  | #some(v) -> v\n" +
            "  | _ -> 0\n" +
            "}\n" +
            "def e = if get(1) is #some(v) then v else 0\n")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A call to a def that matches on a tag union keeps the argument's payload type`` () =
    // Regression: the def's type was generalized before its constraints were solved, so its input came out
    // as a fresh variable unrelated to its result, and `y` had a payload of no particular type.
    let source =
        "def mapId = { | #some(a) -> #some(a) | other -> other }\n" +
        "def y = mapId(#some(5))"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    // `#some(5)` has no other tags, so neither has `y`.
    Assert.Equal(TyTag("some", Some (TyPrimitive "int")), result.Typed.Value.Types.["y"])
