#!/usr/bin/env bash
# Prints the SHA-256 of the componentizer's sources: Cargo.toml, Cargo.lock and every file under src/, in path order.
# compiler/Nanyx.Componentizer/nanyx-componentizer.wasm.sources holds this hash for the sources the committed .wasm was
# built from; the Compiler workflow fails when they differ, so a change to the crate comes with its rebuilt .wasm.
#
#   componentizer-sources.sh          print the hash
#   componentizer-sources.sh --write  write it to the .sources file (after rebuilding the .wasm)
set -euo pipefail
dir="$(cd "$(dirname "$0")/../../compiler/Nanyx.Componentizer" && pwd)"
hash=$(cd "$dir" && { echo Cargo.toml; echo Cargo.lock; find src -type f; } | LC_ALL=C sort | while read -r f; do
  printf '%s\n' "$f"; sha256sum < "$f"; done | sha256sum | cut -d' ' -f1)
if [ "${1:-}" = "--write" ]; then echo "$hash" > "$dir/nanyx-componentizer.wasm.sources"; else echo "$hash"; fi
