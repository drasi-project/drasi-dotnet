#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
profile="${1:-release}"

cargo build --manifest-path "$root/native/Cargo.toml" --"$profile"
dotnet run --project "$root/examples/Quickstart"
