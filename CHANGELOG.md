# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-10-10

### Added

- Configurable redb WAL provider with cross-process recovery and shutdown lock
  release.
- Public documentation site.
- .NET Getting Started, Building Comfort, and Curbside Pickup tutorials.
- Production-bar test suite: lifecycle, error-path, streaming, cancellation,
  concurrency, leak/soak, and public-API coverage, plus CI coverage gates
  (`cargo llvm-cov` on native marshalling, coverlet on the C# façade).

### Fixed

- Standard NuGet RID native asset paths.
- PostgreSQL source/bootstrap plugin ABI compatibility.

## [0.1.0] - 2026-09-04

Initial public API for embedding Drasi in .NET, at in-process parity with
Python and Node.

- Native `drasi_ffi` host around `drasi-lib` 0.8.9 (sources, queries,
  reactions, plugins/OCI/lockfiles/cosign, RocksDB, redb, identity, secrets).
- C# `Engine` with `IAsyncDisposable`, `IAsyncEnumerable`, typed errors and
  plugin/schema DTOs.
- Generic host: `AddDrasi`, `DrasiBuilder`, `AddDrasiCheck`, `ILogger` and
  `ActivitySource("Drasi")`.
- RID-specific native binaries in the nupkg (`win-x64`, `linux-x64`,
  `linux-arm64`, `osx-x64`, `osx-arm64`).

[Unreleased]: https://github.com/drasi-project/drasi-dotnet/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/drasi-project/drasi-dotnet/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/drasi-project/drasi-dotnet/releases/tag/v0.1.0
