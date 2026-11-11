module TranspilerWasmTests.Features.Matching.RunRecordPattern

open Xunit
open TranspilerWasmTestHelpers

// A record pattern by member name, `(id = 1, ...)`, tests the fields it names and leaves the others alone (#460).

[<Fact>]
let ``Run - a record pattern matches the fields it names`` () =
    let source =
        """
import nanyx/console

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
import nanyx/console

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
