# Copyright 2026 The Drasi Authors.
$ErrorActionPreference = "Stop"
$TutorialDir = Split-Path -Parent $PSScriptRoot
$RepoRoot = Resolve-Path (Join-Path $TutorialDir "../..")
docker compose -f (Join-Path $TutorialDir "database/docker-compose.yml") up -d --wait
$native = @(
    (Join-Path $RepoRoot "native/target/release/libdrasi_ffi.dylib"),
    (Join-Path $RepoRoot "native/target/release/libdrasi_ffi.so"),
    (Join-Path $RepoRoot "native/target/release/drasi_ffi.dll")
)
if (-not ($native | Where-Object { Test-Path $_ })) {
    cargo build --release --manifest-path (Join-Path $RepoRoot "native/Cargo.toml")
}
Set-Location $TutorialDir
dotnet run --configuration Release
