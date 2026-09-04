#!/bin/bash
# Copyright 2026 The Drasi Authors.
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

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
echo "Starting the console app (embeds the Drasi engine)..."
echo "On first run it downloads plugins from ghcr.io — give it a moment."
echo

cd "$TUTORIAL_DIR"
exec dotnet run --configuration Release
