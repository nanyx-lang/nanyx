#!/usr/bin/env bash
# Components in Nanyx and in Rust call each other (#2241). A Rust command (app) calls the Nanyx component (shapes)'s
# exports, `acme:shapes/shapes`, which calls the Rust component (units)'s `acme:units/units` in turn. Each is built, the
# three composed with wac, and the result run under wasmtime run, which must print expected.txt.
#
# The Rust components' WIT is the WIT nanyx build writes for the Nanyx component: this checks the copies of its
# interfaces in app/wit and units/wit are what it wrote.
#
# Needs the compiler built (CONFIGURATION, Release by default), Cargo with the wasm32-wasip2 target, wac and wasmtime.
# Usage: run.sh [<directory to build in>]
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/out}
mkdir -p "$out"

nanyx() { dotnet run --project "$here/../Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj" --no-build --configuration "${CONFIGURATION:-Release}" -- "$@"; }

nanyx build "$here/shapes" --host nanyx.console --out "$out/shapes.wasm"

# The interface blocks of two WIT files are the same.
same_interfaces() {
  if ! diff <(sed -n '/^interface /,/^}/p' "$1") <(sed -n '/^interface /,/^}/p' "$2"); then
    echo "::error::$2 isn't the WIT nanyx build wrote in $1"
    exit 1
  fi
}
same_interfaces "$out/wit/component.wit" "$here/app/wit/deps/shapes/shapes.wit"
same_interfaces "$out/wit/deps/acme-units-0.1.0/package.wit" "$here/units/wit/units.wit"

cargo build --manifest-path "$here/units/Cargo.toml" --target wasm32-wasip2 --release --locked
cargo build --manifest-path "$here/app/Cargo.toml" --target wasm32-wasip2 --release --locked

wac plug "$out/shapes.wasm" --plug "$here/units/target/wasm32-wasip2/release/units.wasm" -o "$out/shapes-with-units.wasm"
wac plug "$here/app/target/wasm32-wasip2/release/app.wasm" --plug "$out/shapes-with-units.wasm" -o "$out/app.wasm"

actual=$(wasmtime run "$out/app.wasm" < /dev/null)
if ! diff <(echo "$actual") "$here/expected.txt"; then
  echo "::error::The composed components print something else than ComponentInterop/expected.txt under wasmtime run"
  exit 1
fi
echo "The Nanyx and Rust components called each other."
