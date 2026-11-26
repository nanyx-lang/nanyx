#!/usr/bin/env python3
"""The shards the Compiler workflow splits the Wasm tests into (.github/workflows/compiler.yml, the `wasm-test` job).

Each shard runs the tests of some of `Nanyx.Compiler.Wasm.Tests`' areas. An area is a namespace
`TranspilerWasmTests.Features.<Area>`, which is a directory's under `Features/` (but for `OperatorContexts`, in
`Features/Contexts/`), or `E2E`, the module `TranspilerWasmTests.E2E`. The shards are grouped so that each takes about
the same time, from a run's per-test times (`dotnet test --logger trx`).

    wasm-shards.py filter <shard>   writes the `dotnet test --filter` that runs the shard's tests
    wasm-shards.py check            reads `dotnet test --list-tests` from standard input, and fails if a test is in no
                                    shard, or in more than one

To add an area, add its name to one of the shards below: the one with the least time, if you can't tell."""

import sys

# Each shard's tests took about 334 s of test time, summed over its tests, in one local run (26 November 2026). Docs
# alone took 312 s.
SHARDS = {
    1: [
        "Console", "Docs", "Equality", "Except", "Hashing", "Names", "Nominals", "Profiling"
    ],
    2: [
        "Asserts", "Chars", "Components", "Contexts", "ControlFlow", "Debugging", "Embedding", "Examples",
        "Functions", "Hosts", "Lists", "Loops", "Numbers", "Operators", "Ordering", "Regex", "Sets", "Strings",
        "Tests", "Types", "Workflows"
    ],
    3: [
        "Arrays", "Async", "Binary", "Collections", "Commands", "Core", "Dbg", "E2E", "Errors", "Floats", "Maps",
        "Matching", "Memory", "OperatorContexts", "Packages", "Records", "StdLib", "Tags", "TailCalls", "Units"
    ],
}


def prefix(area: str) -> str:
    """What every test's full name in the area contains, and no other test's does."""
    return "TranspilerWasmTests.E2E." if area == "E2E" else f"TranspilerWasmTests.Features.{area}."


def test_filter(shard: int) -> str:
    """The shard's `dotnet test --filter`: its areas' tests, but for the timing tests, which the Timing workflow runs."""
    areas = "|".join(f"FullyQualifiedName~{prefix(area)}" for area in SHARDS[shard])
    return f"({areas})&Category!=Timing"


def check(listed) -> int:
    """Checks that each test `dotnet test --list-tests` listed is in exactly one shard; gives the exit code."""
    # The tests are the indented lines after this one.
    lines = iter(listed)
    for line in lines:
        if line.strip() == "The following Tests are available:":
            break
    tests = [line.strip() for line in lines if line.startswith(" ") and line.strip()]
    if not tests:
        print("No tests were listed.")
        return 1
    problems = []
    for test in tests:
        shards = sorted({shard for shard, areas in SHARDS.items() for area in areas if prefix(area) in test})
        if len(shards) != 1:
            where = "no shard" if not shards else "shards " + ", ".join(map(str, shards))
            problems.append(f"{test}: in {where}")
    for problem in problems:
        print(problem)
    if problems:
        print(f"{len(problems)} of {len(tests)} tests are not in exactly one shard. Add each area to one shard in .github/scripts/wasm-shards.py.")
        return 1
    print(f"Each of the {len(tests)} tests is in exactly one shard.")
    return 0


if __name__ == "__main__":
    match sys.argv[1:]:
        case ["filter", shard]:
            print(test_filter(int(shard)))
        case ["check"]:
            sys.stdin.reconfigure(encoding="utf-8", errors="replace")
            sys.exit(check(sys.stdin))
        case _:
            sys.exit(__doc__)
