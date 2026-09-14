#!/bin/bash
# Copyright 2026 The Drasi Authors.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TUTORIAL_DIR="$SCRIPT_DIR/.."
bash "$SCRIPT_DIR/setup-database.sh"
echo
echo "Starting the console app (embeds the Drasi engine)..."
echo "On first run it downloads plugins from ghcr.io — give it a moment."
echo
cd "$TUTORIAL_DIR"
exec dotnet run --configuration Release
