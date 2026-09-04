#!/bin/bash
set -euo pipefail
echo "Initializing Drasi .NET Getting Started tutorial environment..."
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TUTORIAL_DIR="$REPO_ROOT/tutorials/getting-started"
echo "Building the native library..."
cargo build --release --manifest-path "$REPO_ROOT/native/Cargo.toml"
echo "Restoring the tutorial project..."
dotnet restore "$TUTORIAL_DIR/GettingStarted.csproj"
echo
echo "Getting Started tutorial environment is ready."
echo "Next: run './scripts/start-demo.sh'"
