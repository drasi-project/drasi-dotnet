#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
profile="${1:-release}"

case "$profile" in
  release) cargo_profile_args=(--release) ;;
  debug|dev) cargo_profile_args=() ;;
  *) echo "unknown profile: $profile (use release or debug)" >&2; exit 1 ;;
esac

cargo build --manifest-path "$root/native/Cargo.toml" "${cargo_profile_args[@]}"
dotnet run --project "$root/examples/Quickstart"
