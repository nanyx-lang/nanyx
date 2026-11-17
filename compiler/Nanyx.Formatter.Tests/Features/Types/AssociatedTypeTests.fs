module Nanyx.Formatter.Tests.Features.Types.AssociatedTypes

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Associated types read as the docs write them`` () =
    unchanged "context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)\n"
    unchanged "def joinAll: [Iter(c, elem = string)] c -> string = f\n"
    unchanged "def firstPair: [Iter(c), Iter(d)] c, d -> (Iter(c).elem, Iter(d).elem) = f\n"
    unchanged "type Bag.elem = int\n"
