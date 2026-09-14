#!/bin/bash
# Copyright 2026 The Drasi Authors.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ "${1:-}" = "--volumes" ] || [ "${1:-}" = "-v" ]; then
    docker compose -f "$SCRIPT_DIR/../database/docker-compose.yml" down -v
else
    docker compose -f "$SCRIPT_DIR/../database/docker-compose.yml" down
fi
