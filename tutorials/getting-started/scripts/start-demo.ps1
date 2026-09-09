
# Copyright 2026 The Drasi Authors.
$ErrorActionPreference = "Stop"
$TutorialDir = Split-Path -Parent $PSScriptRoot
docker compose -f (Join-Path $TutorialDir "database/docker-compose.yml") up -d --wait
Set-Location $TutorialDir
dotnet run --configuration Release

