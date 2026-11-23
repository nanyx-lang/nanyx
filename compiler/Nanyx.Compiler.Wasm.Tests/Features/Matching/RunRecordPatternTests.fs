module TranspilerWasmTests.Features.Matching.RunRecordPattern

open Xunit
open TranspilerWasmTestHelpers

// A record pattern by member name, `(id = 1, ...)`, tests the fields it names and leaves the others alone (#460).

[<Fact>]
let ``Run - a record pattern matches the fields it names`` () =
    let source =
        """
type Request = (id: int, name: string)

def f: Request -> string = { r ->
  match r
    | (id = 1, ...) -> "one"
    | (name = n, id = _) -> "other {n}"
}

println(f((id = 1, name = "x")))
println(f((id = 2, name = "y")))
"""
    runSource source |> assertRan "one\nother y\n"

[<Fact>]
let ``Run - the nested patterns example takes requests apart by member name`` () =
    // The pattern-matching page's "Nesting patterns" example, whose second arm matches two elements' fields against
    // different tags (#461).
    let source =
        """
type Result =
  | #some(int)
  | #error(string)

type Request = (
  id: int
  status: Result
  tags: list(string)
)

def processRequests: list(Request) -> string = { requests ->
  match requests
    | [] ->
      "No requests"
    | [(id = 1, status = #some(value), tags = ["urgent", ...rest]), ...more] ->
      "Urgent request 1 with value {value}"
    | [(id = id, status = #error(msg), tags = []), (status = #some(val), ...), ...tail] ->
      "Error in request {id}: {msg}, followed by success"
    | [(status = #some(n), ...), ...rest] & requests & { .length > 5 } ->
      "Multiple requests with first one ok, total: {requests.length}"
    | _ ->
      "Other pattern"
}

def ok = { id -> (id = id, status = #some(id), tags = []) }

println(processRequests([]))
println(processRequests([(id = 1, status = #some(7), tags = ["urgent"])]))
println(processRequests([(id = 2, status = #error("bad"), tags = []), (id = 3, status = #some(4), tags = [])]))
println(processRequests([ok(1), ok(2), ok(3), ok(4), ok(5), ok(6)]))
println(processRequests([(id = 2, status = #error("bad"), tags = ["x"]), ok(3)]))
"""
    runSource source
    |> assertRan (
        """No requests
Urgent request 1 with value 7
Error in request 2: bad, followed by success
Multiple requests with first one ok, total: 6
Other pattern
"""
            .ReplaceLineEndings "\n"
    )

// Fields without `=` may end in `...`, which leaves the other fields out: a name alone takes the field it names, and
// any other pattern the field at its position.
[<Fact>]
let ``Run - a pattern of fields without = that ends in ... takes the fields it names or reaches by position`` () =
    let source =
        """
type Shape = #point | #rectangle(width: float, height: float, label: string)

pattern string -> #Email(localPart: string, domain: string) | _ = { s ->
  if s == "a@b" then #Email(localPart = "a", domain = "b") else _
}

def describe: Shape -> string = {
  | #point -> "point"
  | #rectangle(label, width, ...) -> "{label} {width}"
}

def greet: string -> string = {
  | #Email(localPart, ...) -> "hi {localPart}"
  | _ -> "no"
}

def sum: (int, int, int) -> int = {
  | (0, ...) -> 0
  | (a, b, ...) -> a + b
}

def second = { (_, b, ...) -> b }

println(describe(#rectangle(width = 2.0, height = 3.0, label = "box")))
println(describe(#point))
println(greet("a@b"))
println(greet("x"))
println(sum((0, 1, 2)))
println(sum((1, 2, 3)))
println(second((1, "two", 3.0)))
"""
    runSource source |> assertRan "box 2.0\npoint\nhi a\nno\n0\n3\ntwo\n"
