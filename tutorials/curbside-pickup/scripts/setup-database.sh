#!/bin/bash
# Copyright 2026 The Drasi Authors.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
docker compose -f "$SCRIPT_DIR/../database/docker-compose.yml" up -d --wait
echo "PostgreSQL is ready on localhost:${POSTGRES_HOST_PORT:-5842}"
echo "MySQL is ready on localhost:${MYSQL_HOST_PORT:-3319}"
