# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/drasi-project/drasi-dotnet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/drasi-project/drasi-dotnet/releases/tag/v0.1.0
