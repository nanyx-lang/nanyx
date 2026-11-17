module TranspilerWasmTests.Features.Json.RunJsonDecode

open Xunit
open TranspilerWasmTestHelpers

// `nanyx.json`'s decoders and encoders (#1788): `json.decode` with `json.record` and the combinators, errors that name
// their path, and `json.encode` with the encoders of `nanyx.json/encode`.

/// What running the script `body` printed, after the lines that make `nanyx.json` its dependency and import its two
/// modules, the cookbook's `User` with its decoder and encoder, and `show`, which prints what a decode read, as the
/// encoder `encoder` writes it, or its error as `json.describe` writes it.
let private runDecoding (body: string) =
    let source =
        """::dependency nanyx.json

import (
  nanyx.json/encode as encode
  nanyx.json/json as json
)

type User = (name: string, age: int, nickname: #some(string) | #none)

def user: json.Decoder(User) = json.record {
  def name = try json.field("name", json.string)
  def age = try json.field("age", json.int)
  def nickname = try json.field("nickname", json.optional(json.string))
  return User(name = name, age = age, nickname = nickname)
}

def userEncoder: json.Encoder(User) = encode.object([
  encode.field("name", { u -> u.name }, encode.string)
  encode.field("age", { u -> u.age }, encode.int)
  encode.optionalField("nickname", { u -> u.nickname }, encode.string)
])

def show: [Console] (#some(a) | #error(json.DecodeError)), json.Encoder(a) -> () = {
  | #some(x), encoder -> println(json.encode(x, encoder))
  | #error(e), _ -> println(json.describe(e))
}

"""
    runSource ((source + body.Trim() + "\n").ReplaceLineEndings "\n")

[<Fact>]
let ``Run - the cookbook's User is decoded from an object, and encoded back`` () =
    runDecoding
        """
show(json.decode('{"name": "Ada", "age": 36, "nickname": "Countess"}', user), userEncoder)
show(json.decode('{"age": 25, "name": "Bob"}', user), userEncoder)
"""
    |> assertRan """{"name":"Ada","age":36,"nickname":"Countess"}
{"name":"Bob","age":25}
"""

[<Fact>]
let ``Run - an optional field reads a missing member and null as none, and anything else by its decoder`` () =
    runDecoding
        """
def nickname = json.field("nickname", json.optional(json.string))
def written = encode.optional(encode.string)

show(json.decode("\{}", nickname), written)
show(json.decode('{"nickname": null}', nickname), written)
show(json.decode('{"nickname": "Bo"}', nickname), written)
show(json.decode('{"nickname": 3}', nickname), written)
"""
    |> assertRan """null
null
"Bo"
nickname: Expected a string, found a number
"""

[<Fact>]
let ``Run - lists and nested records are decoded, and an error names the path to the value that didn't fit`` () =
    runDecoding
        """
type Team = (name: string, users: list(User))

def team: json.Decoder(Team) = json.record {
  def name = try json.field("name", json.string)
  def users = try json.field("users", json.list(user))
  return Team(name = name, users = users)
}

def teamEncoder: json.Encoder(Team) = encode.object([
  encode.field("name", { t -> t.name }, encode.string)
  encode.field("users", { t -> t.users }, encode.list(userEncoder))
])

show(json.decode('{"name": "core", "users": [{"name": "Ada", "age": 36}, {"name": "Bob", "age": 25}]}', team), teamEncoder)
show(json.decode('{"name": "core", "users": [{"name": "Ada", "age": 36}, {"name": "Bob", "age": 2}, {"name": "Cy", "age": 2.5}]}', team), teamEncoder)
show(json.decode('{"name": "core", "users": [{"name": "Ada", "age": 36}, {"age": 2}]}', team), teamEncoder)
show(json.decode('{"name": "core", "users": {}}', team), teamEncoder)
show(json.decode('[[1, 2], [3, "4"]]', json.list(json.list(json.int))), encode.list(encode.list(encode.int)))
"""
    |> assertRan """{"name":"core","users":[{"name":"Ada","age":36},{"name":"Bob","age":25}]}
users[2].age: Expected a whole number, found 2.5
users[1].name: Expected a member 'name', found none
users: Expected an array, found an object
[1][1]: Expected a whole number, found a string
"""

[<Fact>]
let ``Run - each kind of value is decoded by its own decoder, and anything else is an error that says what was found`` () =
    runDecoding
        """
show(json.decode("2.5", json.float), encode.float)
show(json.decode("-7", json.int), encode.int)
show(json.decode("true", json.bool), encode.bool)
show(json.decode('"hi"', json.string), encode.string)
show(json.decode('[1, {"a": null}]', json.value), { v -> v })
show(json.decode('{"en": "Hello", "fr": "Bonjour"}', json.dict(json.string)), encode.dict(encode.string))
show(json.decode("null", json.string), encode.string)
show(json.decode("1e30", json.int), encode.int)
show(json.decode("[]", json.bool), encode.bool)
show(json.decode("\{}", json.float), encode.float)
show(json.decode("[1, 2", json.int), encode.int)
"""
    |> assertRan """2.5
-7
true
"hi"
[1,{"a":null}]
{"en":"Hello","fr":"Bonjour"}
Expected a string, found null
Expected a whole number, found 1.0e30
Expected a boolean, found an array
Expected a number, found an object
Expected ',' or ']' after an element of an array, found the end of the text, at line 1, column 6
"""

[<Fact>]
let ``Run - a tag union is decoded by the decoder a discriminator field chooses, and encoded back`` () =
    runDecoding
        """
type Shape = #circle(float) | #rect(float, float)

def circle: json.Decoder(Shape) = json.convert(json.field("radius", json.float), { r -> #circle(r) })

def rect: json.Decoder(Shape) = json.record {
  def width = try json.field("width", json.float)
  def height = try json.field("height", json.float)
  return #rect(width, height)
}

def shape: json.Decoder(Shape) = json.record {
  def kind = try json.field("kind", json.string)

  def decoder =
    match kind
      | "circle" -> circle
      | "rect" -> rect
      | other -> json.fail("Expected the kind 'circle' or 'rect', found '{other}'")

  def s = try decoder
  return s
}

def shapeEncoder: json.Encoder(Shape) = {
  | #circle(r) -> #object(["kind" => #string("circle"), "radius" => #number(r)])
  | #rect(w, h) -> #object(["kind" => #string("rect"), "width" => #number(w), "height" => #number(h)])
}

show(json.decode('[{"kind": "circle", "radius": 1.5}, {"kind": "rect", "width": 2, "height": 3}]', json.list(shape)), encode.list(shapeEncoder))
show(json.decode('[{"kind": "circle", "radius": 1}, {"kind": "hexagon"}]', json.list(shape)), encode.list(shapeEncoder))
show(json.decode('[{"kind": "rect", "width": 2}]', json.list(shape)), encode.list(shapeEncoder))
"""
    |> assertRan """[{"kind":"circle","radius":1.5},{"kind":"rect","width":2,"height":3}]
[1]: Expected the kind 'circle' or 'rect', found 'hexagon'
[0].height: Expected a member 'height', found none
"""

[<Fact>]
let ``Run - oneOf reads by the first decoder that fits, and otherwise says what the one that got furthest found`` () =
    runDecoding
        """
type Id = #number(int) | #name(string)

def id: json.Decoder(Id) = json.oneOf([
  json.convert(json.int, { n -> #number(n) })
  json.convert(json.string, { s -> #name(s) })
  json.convert(json.field("name", json.string), { s -> #name(s) })
])

def idEncoder: json.Encoder(Id) = {
  | #number(n) -> encode.int(n)
  | #name(s) -> encode.string(s)
}

show(json.decode('[1, "two", {"name": "three"}]', json.list(id)), encode.list(idEncoder))
show(json.decode('[1, {"name": 3}]', json.list(id)), encode.list(idEncoder))
show(json.decode("true", json.oneOf([])), encode.bool)
"""
    |> assertRan """[1,"two","three"]
[1].name: Expected a string, found a number
Expected a value one of no decoders reads, found a boolean
"""

[<Fact>]
let ``Run - succeed, fail and andThen build decoders from others`` () =
    runDecoding
        """
def versioned: json.Decoder(string) = json.andThen(json.field("version", json.int), {
  | 1 -> json.field("name", json.string)
  | 2 -> json.field("fullName", json.string)
  | _ -> json.fail("Expected version 1 or 2")
})

show(json.decode('{"version": 1, "name": "Ada"}', versioned), encode.string)
show(json.decode('{"version": 2, "fullName": "Ada Lovelace"}', versioned), encode.string)
show(json.decode('{"version": 3}', versioned), encode.string)
show(json.decode("null", json.succeed("default")), encode.string)
"""
    |> assertRan """"Ada"
"Ada Lovelace"
Expected version 1 or 2
"default"
"""

[<Fact>]
let ``Run - an error's path is a list of steps, written by pathText`` () =
    runDecoding
        """
match json.decode('{"a": [{"b": [0, "x"]}]}', json.field("a", json.list(json.field("b", json.list(json.int)))))
  | #some(_) -> println("read")
  | #error(e) ->
    println(
      e.path \map {
        | #field(name) -> "field {name}"
        | #index(i) -> "index {i}"
      }
    )
    println(json.pathText(e.path))
    println(e.message)

println(json.pathText([]) == "")
"""
    |> assertRan """[field a, index 0, field b, index 1]
a[0].b[1]
Expected a whole number, found a string
true
"""

[<Fact>]
let ``Run - a list of 100,000 records is decoded and encoded in constant stack`` () =
    runDecoding
        """
def items = list.init(100000, { i -> '{"name": "u' ++ int.toString(i) ++ '", "age": ' ++ int.toString(i % 90) ++ '}' })

match json.decode("[" ++ string.join(items, ",") ++ "]", json.list(user))
  | #some(users) ->
    println(list.length(users))
    println(string.length(json.encode(users, encode.list(userEncoder))))
  | #error(e) -> println(json.describe(e))
"""
    |> assertRan "100000\n2677771\n"
