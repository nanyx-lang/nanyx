#!/usr/bin/env python3
"""Writes the timing tests' results, from the .trx files `dotnet test --logger trx` wrote under the directory given, as
a Markdown table to standard output and as CSV to the file given: each test, whether it passed, and how many
milliseconds it took, slowest first. Used by the Timing workflow (.github/workflows/timing.yml)."""

import csv
import pathlib
import sys
import xml.etree.ElementTree as ElementTree

NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def milliseconds(duration: str) -> float:
    """A .trx duration, `hh:mm:ss.fffffff`, in milliseconds."""
    hours, minutes, seconds = duration.split(":")
    return (int(hours) * 3600 + int(minutes) * 60 + float(seconds)) * 1000


def results(directory: pathlib.Path):
    for trx in sorted(directory.rglob("*.trx")):
        root = ElementTree.parse(trx).getroot()
        for result in root.iterfind(".//t:UnitTestResult", NAMESPACE):
            yield result.get("testName"), result.get("outcome"), milliseconds(result.get("duration", "0:0:0"))


def main() -> None:
    directory, csv_path = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    rows = sorted(results(directory), key=lambda row: -row[2])
    with csv_path.open("w", newline="") as out:
        writer = csv.writer(out)
        writer.writerow(["test", "outcome", "milliseconds"])
        writer.writerows((name, outcome, f"{ms:.0f}") for name, outcome, ms in rows)
    print("### Timing tests\n")
    print("| Test | Outcome | ms |")
    print("|---|---|---:|")
    for name, outcome, ms in rows:
        escaped = name.replace("|", "\\|")
        print(f"| {escaped} | {outcome} | {ms:.0f} |")


if __name__ == "__main__":
    main()
