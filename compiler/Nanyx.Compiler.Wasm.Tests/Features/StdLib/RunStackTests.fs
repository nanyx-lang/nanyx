module TranspilerWasmTests.Features.StdLib.RunStack

open Xunit
open TranspilerWasmTestHelpers

// #1642: the immutable `Stack(a)` in `nanyx/stack`.

[<Fact>]
let ``Run - a stack pushes and pops at the top`` () =
    runSource
        """
import nanyx/stack

def s = Stack.empty \push(1) \push(2) \push(3)

match Stack.pop(s)
  | #some(top, rest) -> println("{top} {Stack.toList(rest)} {Stack.peek(rest)}")
  | #none -> println("empty")
println("{Stack.length(s)} {Stack.isEmpty(s)} {Stack.toList(s)}")
println(Stack.toString(s))
"""
    |> assertRan "3 [2, 1] #some(2)\n3 false [3, 2, 1]\nStack[3, 2, 1]\n"

[<Fact>]
let ``Run - the empty stack has nothing to pop or peek at`` () =
    runSource
        """
import nanyx/stack

def s: Stack(int) = Stack.empty
def popped =
  match Stack.pop(s)
    | #some(_, _) -> "popped"
    | #none -> "none"

println("{popped} {Stack.peek(s)} {Stack.isEmpty(s)} {Stack.length(s)}")
println(Stack.toString(s))
"""
    |> assertRan "none #none true 0\nStack[]\n"

[<Fact>]
let ``Run - a stack literal and fromList put the first value on top`` () =
    runSource
        """
import nanyx/stack

def a = Stack[3, 2, 1]
def b: Stack(int) = [3, 2, 1]

println("{Stack.peek(a)} {Stack.peek(b)} {Stack.peek(Stack.fromList([9, 8]))}")
println("{a == b} {a == Stack[1, 2, 3]} {a != Stack.fromList([3, 2, 1])}")
"""
    |> assertRan "#some(3) #some(3) #some(9)\ntrue false false\n"

[<Fact>]
let ``Run - pushing and popping leave the stack they were given as it was`` () =
    runSource
        """
import nanyx/stack

def base = Stack[2, 1]
def pushed = base \push(3)
def popped =
  match Stack.pop(base)
    | #some(_, rest) -> rest
    | #none -> Stack.empty

println("{Stack.toList(base)} {Stack.toList(pushed)} {Stack.toList(popped)}")
"""
    |> assertRan "[2, 1] [3, 2, 1] [1]\n"

[<Fact>]
let ``Run - a stack can be a map's key`` () =
    runSource
        """
import nanyx/stack

def seen = [Stack[1, 2] => "a", Stack[2, 1] => "b"]

println("{seen(Stack.empty \push(2) \push(1))}")
"""
    |> assertRan "#some(a)\n"
