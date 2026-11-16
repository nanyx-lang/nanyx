module TranspilerWasmTests.Features.StdLib.RunQueue

open Xunit
open TranspilerWasmTestHelpers

// #1642: the immutable two-list `Queue(a)` in `nanyx/queue`.

let private run (source: string) = runSource (source.ReplaceLineEndings "\n")

[<Fact>]
let ``Run - a queue gives its values out in the order they went in`` () =
    run
        """
import nanyx/queue

def drain: Queue(int), list(int) -> list(int) = { q, taken ->
  match Queue.dequeue(q)
    | #some(first, rest) -> drain(rest, [first, ...taken])
    | #none -> list.reverse(taken)
}

def q = Queue.empty \enqueue(1) \enqueue(2) \enqueue(3)

println("{drain(q, [])} {Queue.peek(q)} {Queue.count(q)} {Queue.isEmpty(q)}")
println(Queue.toString(q))
"""
    |> assertRan "[1, 2, 3] #some(1) 3 false\nQueue[1, 2, 3]\n"

[<Fact>]
let ``Run - the empty queue has nothing to dequeue or peek at`` () =
    run
        """
import nanyx/queue

def q: Queue(string) = Queue.empty
def taken =
  match Queue.dequeue(q)
    | #some(_, _) -> "dequeued"
    | #none -> "none"

println("{taken} {Queue.peek(q)} {Queue.isEmpty(q)} {Queue.count(q)}")
println(Queue.toString(q))
"""
    |> assertRan "none #none true 0\nQueue[]\n"

[<Fact>]
let ``Run - dequeuing past the front list reverses the back list into it`` () =
    run
        """
import nanyx/queue

def rest: Queue(int) -> Queue(int) = { q ->
  match Queue.dequeue(q)
    | #some(_, after) -> after
    | #none -> q
}

-- 1 is the front list; 2, 3 and 4 wait reversed on the back list.
def q = Queue.empty \enqueue(1) \enqueue(2) \enqueue(3) \enqueue(4)
def afterOne = rest(q)
def afterTwo = rest(afterOne) \enqueue(5)

println("{Queue.peek(afterOne)} {Queue.toList(afterOne)} {Queue.count(afterOne)}")
println("{Queue.peek(afterTwo)} {Queue.toList(afterTwo)} {Queue.count(afterTwo)}")
"""
    |> assertRan "#some(2) [2, 3, 4] 3\n#some(3) [3, 4, 5] 3\n"

[<Fact>]
let ``Run - an old queue reads the same after a new one is made from it`` () =
    run
        """
import nanyx/queue

def q = Queue[1, 2]
def longer = q \enqueue(3)
def shorter =
  match Queue.dequeue(q)
    | #some(_, after) -> after
    | #none -> q

println("{Queue.toList(q)} {Queue.toList(longer)} {Queue.toList(shorter)}")
"""
    |> assertRan "[1, 2] [1, 2, 3] [2]\n"

[<Fact>]
let ``Run - queues holding the same values are equal and hash alike, however they split them`` () =
    run
        """
import nanyx/queue

def split =
  match Queue.dequeue(Queue.empty \enqueue(0) \enqueue(1) \enqueue(2))
    | #some(_, after) -> after \enqueue(3)
    | #none -> Queue.empty
def whole: Queue(int) = [1, 2, 3]

println("{split == Queue.fromList([1, 2, 3])} {split == whole} {split != whole}")
println("{Queue.hash(split) == Queue.hash(Queue.fromList([1, 2, 3]))} {split == Queue[3, 2, 1]}")
"""
    |> assertRan "true true false\ntrue false\n"
