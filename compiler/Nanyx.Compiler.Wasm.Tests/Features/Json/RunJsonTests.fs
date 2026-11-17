module TranspilerWasmTests.Features.Json.RunJson

open Xunit
open TranspilerWasmTestHelpers

// The shipped package `nanyx.json`: the `Json` value, `json.parse`, `json.print` and `json.printPretty` (#1787), run as
// a script that names it in a `::dependency` line.

/// What running the script `body` printed, after the lines that make `nanyx.json` its dependency and import it, and
/// `show`, which prints what `json.parse` reads from a text: the value as `json.print` writes it, or the error's
/// position, line, column and message.
let private runJson (body: string) =
    let source =
        """::dependency nanyx.json

import nanyx.json/json as json
import nanyx.json/json as (Json, JsonError)

def show = { text ->
  match json.parse(text)
    | #some(value) -> println(json.print(value))
    | #error(e) -> println("{e.position} {e.line}:{e.column} {e.message}")
}

"""
    runSource ((source + body).ReplaceLineEndings "\n")

[<Fact>]
let ``Run - json.parse reads every kind of value, and json.print writes it back`` () =
    runJson
        """show("null")
show("true")
show("false")
show("42")
show('"text"')
show("[]")
show("\{\}")
show('[1, "a", null]')
show('{"a": 1, "b": false}')
"""
    |> assertRan
        ("""null
true
false
42
"text"
[]
{}
[1,"a",null]
{"a":1,"b":false}
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - json.parse reads nested values, with whitespace between tokens`` () =
    runJson
        """show(" \{ \"users\" : [ \{ \"name\": \"Ada\", \"tags\": [ ] \}, \{ \"name\": \"Grace\", \"tags\": [\"navy\", [1, [2]]] \} ],\n\t\"count\": 2 \} \r\n")
"""
    |> assertRan "{\"users\":[{\"name\":\"Ada\",\"tags\":[]},{\"name\":\"Grace\",\"tags\":[\"navy\",[1,[2]]]}],\"count\":2}\n"

[<Fact>]
let ``Run - json.parse reads the value of each kind out of the text`` () =
    runJson
        """match json.parse('{"name": "Ada", "born": 1815, "languages": ["English", "French"], "alive": false}')
  | #some(#object(person)) ->
    match person("name")
      | #some(#string(name)) -> println(name)
      | _ -> println("no name")
    match person("born")
      | #some(#number(born)) -> println(born + 1.0)
      | _ -> println("no year")
    match person("languages")
      | #some(#array(languages)) -> println(list.length(languages))
      | _ -> println("no languages")
  | _ -> println("not an object")
"""
    |> assertRan "Ada\n1816.0\n2\n"

[<Fact>]
let ``Run - json.parse reads a string's escapes, and json.print writes the ones JSON needs`` () =
    runJson
        """match json.parse('"q\" b\\ s\/ \b\f\n\r\t e\u00e9 E\u00C9 euro\u20ac smile\ud83d\ude00 nul\u0000"')
  | #some(#string(s)) ->
    println(s \codePoints)
    println(json.print(#string(s)))
  | _ -> println("not a string")
show('"héllo, wörld"')
"""
    |> assertRan
        ("""[113, 34, 32, 98, 92, 32, 115, 47, 32, 8, 12, 10, 13, 9, 32, 101, 233, 32, 69, 201, 32, 101, 117, 114, 111, 8364, 32, 115, 109, 105, 108, 101, 128512, 32, 110, 117, 108, 0]
"q\" b\\ s/ \b\f\n\r\t eé EÉ euro€ smile😀 nul\u0000"
"héllo, wörld"
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - json.parse reads an integer, a fraction, an exponent and a negative number`` () =
    runJson
        """def number = { text ->
  match json.parse(text)
    | #some(#number(x)) -> println(x)
    | _ -> println("not a number")
}

number("0")
number("1815")
number("2.5")
number("0.125")
number("1e3")
number("2.5E-2")
number("6e+2")
number("-7")
number("-0.5")
number("-1.5e2")
show("[1, 2.5, -3, 1e2, 1.5e-7, 12345678901234567890]")
"""
    |> assertRan
        ("""0.0
1815.0
2.5
0.125
1000.0
0.025
600.0
-7.0
-0.5
-150.0
[1,2.5,-3,100,1.5e-7,12345678901234570000.0]
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - json.print writes NaN and infinity as null`` () =
    runJson
        """println(json.print(#array([#number(0.0 / 0.0), #number(1.0 / 0.0), #number(-1.0 / 0.0)])))
"""
    |> assertRan "[null,null,null]\n"

[<Fact>]
let ``Run - json.parse says where text stops being JSON, and why`` () =
    runJson
        """show("")
show("[1, 2")
show("[1,]")
show("[1 2]")
show('{"a" 1}')
show('{"a": 1,}')
show("\{1: 2\}")
show('{"a": 1 "b": 2}')
show("tru")
show("true false")
show("01")
show("-")
show("1.")
show("1e")
show('"abc')
show('"a\qb"')
show('"\u12"')
show('"\ud800"')
show('"\ude00 x"')
show('"tab	inside"')
show("[\n  1,\n  x\n]")
show('["é", ü]')
"""
    |> assertRan
        ("""0 1:1 Expected a value, found the end of the text
5 1:6 Expected ',' or ']' after an element of an array, found the end of the text
3 1:4 Expected a value, found ']'
3 1:4 Expected ',' or ']' after an element of an array, found '2'
5 1:6 Expected ':' after the name of a member, found '1'
8 1:9 Expected a string to name a member of an object, found '}'
1 1:2 Expected a string to name a member of an object, found '1'
8 1:9 Expected ',' or '}' after a member of an object, found '"'
0 1:1 Expected a value, found 't'
5 1:6 Expected the end of the text after the value, found 'f'
0 1:1 A number can't start with a 0 followed by more digits
1 1:2 Expected a digit, found the end of the text
2 1:3 Expected a digit after '.', found the end of the text
2 1:3 Expected a digit in the exponent, found the end of the text
4 1:5 Expected a '"' to end the string, found the end of the text
2 1:3 Expected an escape after '\', found 'q': a string's escapes are \", \\, \/, \b, \f, \n, \r, \t and \uXXXX
1 1:2 Expected four hexadecimal digits after '\u'
1 1:2 A '\u' escape of a surrogate needs the other half of its pair: a character past U+FFFF is written as a high surrogate then a low one, such as '\uD83D\uDE00'
1 1:2 A '\u' escape of a surrogate needs the other half of its pair: a character past U+FFFF is written as a high surrogate then a low one, such as '\uD83D\uDE00'
4 1:5 A string can't hold a control character: write it as an escape, such as '\n'
9 3:3 Expected a value, found 'x'
7 1:7 Expected a value, found 'ü'
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - json.printPretty writes each element and member on a line of its own`` () =
    runJson
        """match json.parse('{"name": "Ada", "tags": ["a", [], {}], "nested": {"x": [1, {"y": null}]}}')
  | #some(value) ->
    println(json.printPretty(value, indent = 2))
    println(json.printPretty(value, indent = 4))
    println(json.printPretty(#number(1.0), indent = 2))
    println(json.printPretty(#array([]), indent = 2))
  | #error(e) -> println(e.message)
"""
    |> assertRan
        ("""{
  "name": "Ada",
  "tags": [
    "a",
    [],
    {}
  ],
  "nested": {
    "x": [
      1,
      {
        "y": null
      }
    ]
  }
}
{
    "name": "Ada",
    "tags": [
        "a",
        [],
        {}
    ],
    "nested": {
        "x": [
            1,
            {
                "y": null
            }
        ]
    }
}
1
[]
""".ReplaceLineEndings "\n")

[<Fact>]
let ``Run - What json.print and json.printPretty write, json.parse reads back as the same value`` () =
    runJson
        """-- Each array is named first, until a tag's payload is checked against the declared type (#1798).
def numbers: Json = #array([#number(0.0), #number(-2.5), #number(1.5e-7), #number(1.0e21), #number(123456.789)])
def flags: Json = #array([#bool(true), #bool(false), #null])

def value: Json = #object([
  "name" => #string("Ada \"the first\" \\ Lovelace\n\u{e9}\u{1F600}\u{1}")
  "numbers" => numbers
  "flags" => flags
  "empty" => #object(map.empty)
  "nested" => #object(["a" => #array([#array([]), #object(["b" => #string("")])])])
])

println(json.parse(json.print(value)) == #some(value))
println(json.parse(json.printPretty(value, indent = 2)) == #some(value))
println(json.parse(json.printPretty(value, indent = 0)) == #some(value))
println(json.print(value) == json.print(orCrash(json.parse(json.printPretty(value, indent = 3)), "JSON")))
"""
    |> assertRan "true\ntrue\ntrue\ntrue\n"

[<Fact>]
let ``Run - An object keeps its keys in the order they were written, and a key written twice holds its last value`` () =
    runJson
        """show('{"zebra": 1, "apple": 2, "mango": 3}')
show('{"b": 1, "a": 2, "b": 3}')
println(json.parse('{"a": 1, "b": 2}') == json.parse('{"b": 2, "a": 1}'))
"""
    |> assertRan "{\"zebra\":1,\"apple\":2,\"mango\":3}\n{\"b\":3,\"a\":2}\ntrue\n"

// A document of over 100,000 values, read and written in time in proportion to its size: an array of 20,000 objects,
// each six values (itself, a number, a string, and an array of two values).
[<Fact>]
let ``Run - json.parse and json.print handle a document of over 100,000 values`` () =
    runJson
        """def item = { i -> '{"id": ' ++ int.toString(i) ++ ', "name": "item \u00e9", "tags": [true, null]}' }
def text = "[" ++ string.join(list.init(20000, item), ",\n") ++ "]"

match json.parse(text)
  | #some(#array(items)) ->
    println(list.length(items))
    println(string.length(json.print(#array(items))))
    println(json.print(#array(items)) == json.print(orCrash(json.parse(json.print(#array(items))), "JSON")))
  | _ -> println("not an array")
"""
    |> assertRan "20000\n968891\ntrue\n"
