# Copyright 2026 The Drasi Authors.
$ErrorActionPreference = "Stop"
$compose = Join-Path $PSScriptRoot "../database/docker-compose.yml"
if ($args -contains "--volumes" -or $args -contains "-v") {
    docker compose -f $compose down -v
} else {
    docker compose -f $compose down
}
