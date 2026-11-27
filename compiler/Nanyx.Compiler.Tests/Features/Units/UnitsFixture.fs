/// The dimensions and units the units tests declare before their sources, and the types a source declares after them.
module Nanyx.Compiler.Tests.Features.UnitsFixture

open Nanyx.Compiler.Tests.TestSupport

/// `Time`, generic in its storage, with its units from `ns` to `s`.
let time =
    "dimension Time\n@baseUnit unit ns: Time\nunit us: Time = 1_000ns\nunit ms: Time = 1_000us\nunit s: Time = 1_000ms\n"

/// `time`, then `Length`, stored as a `float`, in `m` and `mm`, and `Bytes`, stored as an `int`, in `B` and `KB`.
let dimensions =
    time
    + "dimension Length(float)\n@baseUnit unit m: Length\nunit mm: Length = 0.001m\n"
    + "dimension Bytes(int)\n@baseUnit unit B: Bytes\nunit KB: Bytes = 1_000B\n"

/// The types of the top-level defs `names` that `source` declares after `preamble`, which compiles with no diagnostic,
/// as `typesOf` gives them.
let typesAfter (preamble: string) (source: string) (names: string list) =
    typesOf (withFinalNewline (preamble + source)) names
