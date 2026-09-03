# `Drasi` — API reference & prototype audit

> Deliverable for [team#137](https://github.com/drasi-project/team/issues/137)
> ("Audit prototype .NET bindings; document API surface and gaps"), a subtask
> of the [team#126](https://github.com/drasi-project/team/issues/126) epic.
> Also the record of closing [team#141](https://github.com/drasi-project/team/issues/141)
> (API gaps), [team#130](https://github.com/drasi-project/team/issues/130)
> (idiomatic types), and [team#133](https://github.com/drasi-project/team/issues/133)
> (RID-specific native binaries).

This document is the inventory of the public API exposed by the `Drasi`
package, plus every known gap versus Python (`drasi-python`) and Node
(`@drasi/lib`). Every gap is either **closed** or an explicit deferral with
rationale.

- **Package:** `Drasi` (currently `0.1.0`, unpublished)
- **Native library:** `drasi_ffi` (`cdylib` around `drasi-lib` 0.8.9)
- **Managed façade:** `Engine` in `src/Drasi/`
- **Target floor:** `net8.0` LTS, AOT/trim-friendly (`[LibraryImport]`, no required reflection)

---

## Surface at a glance

| Area | Methods |
| --- | --- |
| Construction | `CreateAsync`¹, `FromConfigAsync`¹ (`JsonNode` or `IConfiguration`) |
| Lifecycle | `StartAsync`, `StopAsync`, `ShutdownAsync`, `IsRunningAsync`, `Dispose` / `DisposeAsync` |
| Sources | `AddSourceAsync`¹, `PushChangeAsync`, `RemoveSourceAsync`, `StartSourceAsync`, `StopSourceAsync`, `GetSourceStatusAsync`, `ListSourcesAsync` |
| Queries | `AddQueryAsync`, `UpdateQueryAsync`, `RemoveQueryAsync`, `StartQueryAsync`, `StopQueryAsync`, `GetQueryResultsAsync`, `GetQueryStatusAsync`, `ListQueriesAsync`, `WaitForQueryAsync` |
| Reactions | `AddReactionAsync`¹, `AddDurableReactionAsync`, `RemoveReactionAsync`, `StartReactionAsync`, `StopReactionAsync`, `GetReactionStatusAsync`, `ListReactionsAsync` |
| Metrics / schema | `GetQueryMetricsAsync`, `GetReactionMetricsAsync`, `GetLifecycleMetricsAsync`, `GetSourceSchemaAsync`, `GetGraphSchemaAsync` |
| Streaming | `QueryResultsAsync`, `QueryEventsAsync`, `SourceEventsAsync`, `ReactionEventsAsync`, `AllEventsAsync`, `QueryLogsAsync`, `SourceLogsAsync`, `ReactionLogsAsync` |
| Plugins | `LoadPluginsAsync`, `WatchPluginsAsync`, `PluginKindsAsync`, `GetHostInfo`, `SearchPluginsAsync`, `ListPluginTagsAsync`, `ResolvePluginAsync`, `InstallPluginAsync`, `PullPluginAsync`, `WriteLockfileAsync`, `ReadLockfile`, `InstallFromLockfileAsync`, `UpdateSourceAsync`, `UpdateReactionAsync`, `UseSecretStoreAsync`, config schemas |
| Construction options | `secrets`, `stateStore` (redb), `indexStore` (rocksdb), `identity`, `pluginsDir` |

¹ **static factory**. `AddSourceAsync` / `AddReactionAsync` are overloaded: in-process push/callback vs plugin `kind`.

Optional DI: `services.AddDrasi(engineId, drasi => { … })` in
`Drasi.DependencyInjection` registers `Engine` as a singleton and a
`DrasiHostedService` (`IHostedService`) that loads plugins, starts the engine,
applies the `DrasiBuilder` topology (sources, queries, reactions, optional
`Seed`), and shuts it down with the generic host. `AddDrasi(IConfiguration)`
builds from appsettings. `AddDrasiCheck()` registers an `IHealthCheck`.
`DrasiBuilder` can `LoadPlugins` / `InstallPlugin` / `UseSecretStore` before
start. The host `ILoggerFactory` is used automatically.

---

## Construction

### `Engine.CreateAsync(id, options?)` → `Task<Engine>`

Create a new, **not-yet-started** engine. Pass `LoggerFactory` (or `Logger`)
to send native `tracing` / `log` events through
`Microsoft.Extensions.Logging` (categories `Drasi` and `Drasi.{rust-target}`).
Without a factory, native logs go to stderr and honour `RUST_LOG` (default
`warn`). `AddDrasi(engineId)` picks up the host `ILoggerFactory`.

### `Engine.FromConfigAsync(config, options?)` → `Task<Engine>`

JSON document of C# or plugin sources, Cypher/GQL queries, and reactions.
Missing `id` defaults to `drasi`. The engine is **started** after plugins load
(Python/Node parity). `EngineOptions` (secrets, stores, identity, plugins dir)
are merged when the document does not already set those keys.

```json
{
  "id": "demo",
  "sources": [{ "id": "orders", "autoStart": true }],
  "queries": [{
    "id": "open",
    "query": "MATCH (o:Order) RETURN o.id",
    "sources": ["orders"],
    "language": "cypher"
  }]
}
```

---

## Queries

`AddQueryAsync` / `UpdateQueryAsync` take Cypher (default) or GQL via
`QueryOptions.Language`. Sources may be bare ids or `QuerySource` entries with a
middleware `Pipeline`. Joins, middleware declarations, auto-start, bootstrap,
and queue capacities match the Python/Node host.

**v1 query API (team#129):** string Cypher/GQL only. A LINQ / `IQueryable`
provider is **deferred** — the research subtask is still open, and Python/Node
ship string queries. A fluent builder is not in this package.

---

## Streaming

Query diffs are `IAsyncEnumerable<QueryResultEvent>` (`QueryResultsAsync`).
That registers a hidden C# reaction (`__stream_{queryId}_{guid}`) and removes
it when the enumerator is disposed.

Lifecycle events and logs are `IAsyncEnumerable<ComponentEvent>` /
`IAsyncEnumerable<LogMessage>`. A slow consumer that overflows the 256-item
buffer raises `StreamLaggedException` (`STREAM_LAGGED`). Native callbacks
never block on the consumer. Disposing the engine completes open streams.

Cancellation tokens are honoured on the enumerable and on every `*Async`
method (cooperative: the native call is `Task.Run` around `block_on`).

---

## Errors

Hierarchy rooted at `DrasiException`, every instance carrying a stable
`.Code` drawn from `DrasiErrorCodes.All` (27 values, same set as Python
except `NO_CSHARP_SOURCE` in place of `NO_PY_SOURCE`):

```
DrasiException
├── ConfigException
│   └── UnknownKindException
├── SourceException
├── StreamLaggedException
└── PluginException
    ├── PluginNotFoundException
    ├── PluginCompatibilityException
    └── PluginSignatureException
```

Plugin exception types exist so callers can catch them once plugin loading
lands; they are not thrown by the current host.

---

## Disposal

`Engine` implements `IDisposable` and `IAsyncDisposable`. Dispose shuts the
native engine down. Further calls throw `ObjectDisposedException`.

---

## Packaging (team#133)

Declared RIDs:

| RID | Rust target | Artifact |
| --- | --- | --- |
| `win-x64` | `x86_64-pc-windows-msvc` | `drasi_ffi.dll` |
| `linux-x64` | `x86_64-unknown-linux-gnu` | `libdrasi_ffi.so` |
| `linux-arm64` | `aarch64-unknown-linux-gnu` | `libdrasi_ffi.so` |
| `osx-x64` | `x86_64-apple-darwin` | `libdrasi_ffi.dylib` |
| `osx-arm64` | `aarch64-apple-darwin` | `libdrasi_ffi.dylib` |

Layout: `runtimes/<rid>/native/`. CI (`.github/workflows/native-binaries.yml`)
builds each RID on a matching runner, packs the nupkg, and verifies
`Engine.CreateAsync` loads **without a Rust toolchain**. nuget.org publish is
team#134.

---

## Cross-check vs Python / Node / `drasi-lib`

In-process lifecycle, C# sources/reactions, full query CRUD, metrics, schema,
and streaming match Python/Node. The remaining delta is **plugin-hosted**
surface (`drasi-host-sdk` / OCI), which the prototype explicitly left out.

---

## Gap analysis

| ID | Gap | Status | Rationale |
| --- | --- | --- | --- |
| G1 | Query CRUD (update/start/stop/remove/list/status) | ✅ closed | Parity with Python/Node; needed for any non-trivial host. |
| G2 | Source/reaction lifecycle (start/stop/remove/list/status) | ✅ closed | Same. |
| G3 | Typed errors | ✅ closed | team#130. 27 stable codes, typed exception hierarchy. |
| G4 | Strongly typed results / config | ✅ closed | team#130. Records for changes, diffs, metrics, options. No `dynamic`. |
| G5 | `IAsyncEnumerable` streaming + `CancellationToken` | ✅ closed | team#130. Query results, component events, logs. |
| G6 | `IDisposable` / `IAsyncDisposable` | ✅ closed | Engine lifetime. |
| G7 | Query language Cypher **and** GQL | ✅ closed | `QueryOptions.Language`. |
| G8 | Joins, middleware, bootstrap, queue tuning | ✅ closed | Same JSON shape as Python/Node. |
| G9 | Metrics + graph/source schema | ✅ closed | `drasi-lib` inspection APIs. |
| G10 | `FromConfig` | ✅ closed | JSON or `IConfiguration`; plugin kinds load from `pluginsDir`. |
| G11 | Optional DI / `ILogger` | ✅ closed | `AddDrasi`; logger is optional. Not mandatory. |
| G12 | RID-specific native binaries | ✅ closed | team#133. Five RIDs, nupkg `runtimes/` layout, CI verify-load. |
| G13 | AOT / trimming | ✅ closed where practical | `net8.0`, `[LibraryImport]`, `IsAotCompatible`. JSON via `JsonNode`. |
| G14 | Native plugin loading / OCI / lockfiles / cosign | ✅ closed | `drasi-host-sdk` 0.10. `LoadPlugins`, `InstallPlugin`, `PullPlugin`, lockfiles, optional cosign. |
| G15 | Plugin-backed `AddSource` / `AddReaction` | ✅ closed | Plus update, bootstrap, config schemas. |
| G16 | RocksDB index store / redb state store | ✅ closed | `EngineOptions.IndexStore` / `StateStore`. RocksDB is compiled into the host (Node parity). |
| G17 | Identity providers / secret stores | ✅ closed | Built-in password/token plus plugin kinds; `UseSecretStoreAsync`. |
| G18 | Config-schema validation | ✅ closed | `Get*ConfigSchemaAsync` for source/reaction/bootstrap/secret-store kinds. |
| G19 | Durable (checkpointed) C# reactions | ✅ closed | `AddDurableReactionAsync`; requires a redb `StateStore`. |
| G20 | LINQ / `IQueryable` query API | ⏸ deferred | team#129 is still open. v1 is string Cypher/GQL, matching Python/Node. |
| G21 | nuget.org publish / versioning / changelog | ⏸ deferred | team#134. This repo packs locally and in CI; it does not push. |

---

## Follow-ups already tracked

- Plugin host + OCI: new work after G14, not a silent hole.
- LINQ: [team#129](https://github.com/drasi-project/team/issues/129).
- NuGet release pipeline: [team#134](https://github.com/drasi-project/team/issues/134).
- Automated tests covering the public API (this tree adds `tests/Drasi.Tests`; broader coverage is [team#139](https://github.com/drasi-project/team/issues/139)).
