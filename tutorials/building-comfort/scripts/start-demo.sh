#!/bin/bash
# Copyright 2026 The Drasi Authors.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TUTORIAL_DIR="$SCRIPT_DIR/.."
REPO_ROOT="$(cd "$TUTORIAL_DIR/../.." && pwd)"
bash "$SCRIPT_DIR/setup-database.sh"
if [[ ! -f "$REPO_ROOT/native/target/release/libdrasi_ffi.dylib" \
   && ! -f "$REPO_ROOT/native/target/release/libdrasi_ffi.so" \
   && ! -f "$REPO_ROOT/native/target/release/drasi_ffi.dll" ]]; then
    echo "Building the native Drasi library (once)..."
    cargo build --release --manifest-path "$REPO_ROOT/native/Cargo.toml"
fi
echo
echo "Starting Building Comfort..."
cd "$TUTORIAL_DIR"
exec dotnet run --configuration Release
