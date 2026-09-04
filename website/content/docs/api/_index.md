---
title: "API reference"
linkTitle: "API reference"
weight: 40
description: >
  Every public method on Engine, grouped by area, with parameters and return types.
---

The public API is the `Engine` class in the `Drasi` package. Optional DI
helpers live in `Drasi.DependencyInjection`.

```csharp
using Drasi;
await using var drasi = await Engine.CreateAsync("app");
```

The v1 query API is a Cypher or GQL **string** passed to `AddQueryAsync`.
There is no LINQ or `IQueryable` provider.

## Surface at a glance

| Area | Methods |
| --- | --- |
| Construction | `CreateAsync`¹, `FromConfigAsync`¹ (`JsonNode` or `IConfiguration`) |
| Lifecycle | `StartAsync`, `StopAsync`, `ShutdownAsync`, `IsRunningAsync`, `Dispose` / `DisposeAsync` |
| Sources | `AddSourceAsync`², `PushChangeAsync`, `RemoveSourceAsync`, `StartSourceAsync`, `StopSourceAsync`, `GetSourceStatusAsync`, `ListSourcesAsync` |
| Queries | `AddQueryAsync`, `UpdateQueryAsync`, `RemoveQueryAsync`, `StartQueryAsync`, `StopQueryAsync`, `GetQueryResultsAsync`, `GetQueryStatusAsync`, `ListQueriesAsync`, `WaitForQueryAsync` |
| Reactions | `AddReactionAsync`², `AddDurableReactionAsync`, `RemoveReactionAsync`, `StartReactionAsync`, `StopReactionAsync`, `GetReactionStatusAsync`, `ListReactionsAsync` |
| Metrics and schema | `GetQueryMetricsAsync`, `GetReactionMetricsAsync`, `GetLifecycleMetricsAsync`, `GetSourceSchemaAsync`, `GetGraphSchemaAsync` |
| Streaming | `QueryResultsAsync`, `QueryEventsAsync`, `SourceEventsAsync`, `ReactionEventsAsync`, `AllEventsAsync`, `QueryLogsAsync`, `SourceLogsAsync`, `ReactionLogsAsync` |
| Plugins | `LoadPluginsAsync`, `WatchPluginsAsync`, `PluginKindsAsync`, `GetHostInfo`¹, `SearchPluginsAsync`¹, `ListPluginTagsAsync`¹, `ResolvePluginAsync`¹, `InstallPluginAsync`, `PullPluginAsync`¹, `WriteLockfileAsync`, `ReadLockfile`¹, `InstallFromLockfileAsync`, `UpdateSourceAsync`, `UpdateReactionAsync`, `UseSecretStoreAsync`, config schemas |

¹ Static. ² Overloaded: in-process versus plugin `kind`.

`DrasiVersion` exposes `Package`, `Core`, `Lib`, `Sdk`, and `FfiSdk`.

## Construction

### Engine.CreateAsync(id, options?) → Task&lt;Engine&gt; {#enginecreateasync}

*Static.* Create a new engine that is **not started**. Pass `LoggerFactory`
(or `Logger`) to send native `tracing` and `log` events through
`Microsoft.Extensions.Logging` (categories `Drasi` and `Drasi.{tracing-target}`).
Without a factory, native logs go to stderr and honour `RUST_LOG` (default
`warn`).

### Engine.FromConfigAsync(config, options?) → Task&lt;Engine&gt; {#enginefromconfigasync}

*Static.* Build from a JSON document or an `IConfiguration` section, load
`pluginsDir` if set, and **start** the engine. Missing `id` defaults to
`drasi`. `options` merge in when the document does not already set those keys.
See [Load a topology from configuration](../guides/configuration/).

### EngineOptions {#engineoptions}

| Property | Meaning |
| --- | --- |
| `Logger` / `LoggerFactory` | Managed and native logs |
| `Secrets` | In-memory map plugins resolve `ConfigValue::Secret` against |
| `StateStore` | Persistent plugin and reaction state. Kind `redb` with a `Path` |
| `IndexStore` | Persistent query index. Kind `rocksdb` with a `Path`. Process-exclusive lock |
| `Identity` | Built-in `password` or `token`, or a plugin kind |
| `PluginsDir` | Directory of plugin cdylibs loaded at create time |

## Lifecycle

| Method | Meaning |
| --- | --- |
| `StartAsync()` | Start the engine and every component configured to auto-start |
| `StopAsync()` | Stop the engine. Components stay registered |
| `ShutdownAsync()` | Permanently shut down and release native stores (including the RocksDB lock) |
| `IsRunningAsync()` | Whether the engine is running |
| `Dispose` / `DisposeAsync` | Destroy the native handle. Further calls throw `ObjectDisposedException` |

Prefer `ShutdownAsync` when the process is exiting. `await using` still
destroys the handle.

`Engine.Id` is the identifier supplied at creation.

## Sources

### AddSourceAsync(id, autoStart = true) {#addsourceasync-inprocess}

Register an in-process source. Push into it with `PushChangeAsync`.

### AddSourceAsync(kind, id, config?, autoStart = true, bootstrap?) {#addsourceasync-plugin}

Register a plugin source. The `kind` must already be loaded.

### PushChangeAsync(sourceId, change) {#pushchangeasync}

Emit a `SourceChange` or a `JsonNode` from an in-process source.

`SourceChange`:

| Property | Meaning |
| --- | --- |
| `Op` | `Insert`, `Update`, or `Delete` |
| `Id` | Graph key |
| `Labels` | Node or relation labels |
| `Properties` | Property map. Include `id` if a query selects it |
| `StartId`, `EndId` | Both required for a relation |
| `EffectiveFrom` | Optional timestamp |

### Other source methods

`RemoveSourceAsync(id, cleanup = false)`, `StartSourceAsync`, `StopSourceAsync`,
`GetSourceStatusAsync` → `ComponentStatus`, `ListSourcesAsync` →
`IReadOnlyList<ComponentInfo>`, `UpdateSourceAsync(kind, id, config?, autoStart)`.

## Queries

The v1 query API is a string. Language defaults to Cypher.
Set `QueryOptions.Language = QueryLanguage.Gql` for GQL.

### AddQueryAsync(id, query, sources, options?) {#addqueryasync}

Register a continuous query over one or more sources. Returns when the query
is provisioned. Startup finishes in the background. Call `WaitForQueryAsync`
before you push data or read results.

`QueryOptions`: `Language`, `AutoStart`, `EnableBootstrap`,
`BootstrapTimeoutSeconds`, queue capacities, `DispatchMode` (`channel` or
`broadcast`), `Joins`, `Middleware`, `Sources` (`QuerySource` with an optional
middleware `Pipeline`).

### WaitForQueryAsync(queryId, timeout = 30s) {#waitforqueryasync}

Block until the query is running.

### GetQueryResultsAsync(queryId) → Task&lt;IReadOnlyList&lt;JsonObject&gt;&gt; {#getqueryresultsasync}

Snapshot of the current result set.

### Other query methods

`UpdateQueryAsync` (same signature as add), `RemoveQueryAsync`,
`StartQueryAsync`, `StopQueryAsync`, `GetQueryStatusAsync`, `ListQueriesAsync`.

## Reactions

### AddReactionAsync(id, queryIds, callback, autoStart = true) {#addreactionasync-inprocess}

Register an in-process reaction. `callback` is `Action<QueryResultEvent>`.

`QueryResultEvent`: `QueryId`, `Sequence`, `Timestamp`, `Results`
(`IReadOnlyList<QueryDiff>`), `Metadata`.

`QueryDiff`: `Type` (`Add`, `Update`, `Delete`, `Aggregation`, `Noop`),
`Data`, `Before`, `After`, `GroupingKeys`.

### AddReactionAsync(kind, id, queryIds, config?, autoStart = true) {#addreactionasync-plugin}

Register a plugin reaction.

### AddDurableReactionAsync(id, queryIds, callback, recovery = Strict) {#adddurablereactionasync}

Checkpointed C# reaction. `callback` is `Func<QueryResultEvent, Task>`.
Requires `EngineOptions.StateStore`. `RecoveryPolicy`: `Strict`, `AutoReset`,
`SkipGap`.

### Other reaction methods

`RemoveReactionAsync(id, cleanup = false)`, `StartReactionAsync`,
`StopReactionAsync`, `GetReactionStatusAsync`, `ListReactionsAsync`,
`UpdateReactionAsync(kind, id, queryIds, config?, autoStart)`.

## Streaming

All of these honor `CancellationToken`. A slow consumer that fills the
256-item buffer throws `StreamLaggedException`. See
[Stream results with IAsyncEnumerable](../guides/streaming/).

| Method | Yields |
| --- | --- |
| `QueryResultsAsync(queryId, reactionId?)` | `QueryResultEvent` |
| `QueryEventsAsync(id)` | `ComponentEvent` |
| `SourceEventsAsync(id)` | `ComponentEvent` |
| `ReactionEventsAsync(id)` | `ComponentEvent` |
| `AllEventsAsync()` | `ComponentEvent` |
| `QueryLogsAsync(id)` | `LogMessage` |
| `SourceLogsAsync(id)` | `LogMessage` |
| `ReactionLogsAsync(id)` | `LogMessage` |

## Plugins

See [Work with plugins](../guides/plugins/).

| Method | Meaning |
| --- | --- |
| `LoadPluginsAsync(directory, verify?)` | Load cdylibs from disk. Optional `{ filename: sha256hex }` allowlist |
| `WatchPluginsAsync(directory, debounce?)` | Hot-reload (default debounce 1s) |
| `PluginKindsAsync()` | Loaded kinds by type |
| `GetHostInfo()`¹ | Platform triple and component versions |
| `SearchPluginsAsync(query?)`¹ | Published plugins |
| `ListPluginTagsAsync(repository)`¹ | Tags for one repository |
| `ResolvePluginAsync(reference)`¹ | Pick the artifact for this host without downloading |
| `InstallPluginAsync(reference, directory?, verify, requireSigned, trustedIdentities, load)` | Download and load. `verify` attaches cosign. `requireSigned` fails unless status is `verified` |
| `PullPluginAsync(...)`¹ | Download without an engine |
| `WriteLockfileAsync(directory)` | Pin loaded plugins |
| `ReadLockfile(directory)`¹ | Read a lockfile |
| `InstallFromLockfileAsync(directory, load)` | Reinstall from a lockfile |
| `UseSecretStoreAsync(kind, config?)` | Plugin secret store |
| `GetSourceConfigSchemaAsync(kind)` (and reaction, bootstrap, secret-store) | JSON Schema for a kind |

¹ Static.

## Metrics and schema

`GetQueryMetricsAsync`, `GetReactionMetricsAsync`, `GetLifecycleMetricsAsync`,
`GetSourceSchemaAsync`, `GetGraphSchemaAsync`.

## Dependency injection

`services.AddDrasi(engineId, drasi => { ... })` in
`Drasi.DependencyInjection` registers `Engine` as a singleton and a
`DrasiHostedService`. The host loads plugins, starts the engine, applies the
`DrasiBuilder` topology (sources, queries, reactions, optional `Seed`), and
shuts it down with the generic host.

- `AddDrasi(IConfiguration)` builds from appsettings.
- `AddDrasi(engineId, (builder, sp) => { ... })` gives the callback the
  `IServiceProvider`.
- `AddDrasiCheck()` registers an `IHealthCheck` that is healthy when the
  engine is running.
- `DrasiBuilder` can `LoadPlugins`, `InstallPlugin`, `WatchPlugins`,
  `UseSecretStore`, and `Configure(EngineOptions)` before start.

See [Use the generic host](../guides/hosting/).

## Errors

Catch `DrasiException` or a subclass. Branch on `Code` (`DrasiErrorCodes`).
See [Handle errors](../guides/error-handling/).

## Target and packaging

- Target floor: `net8.0`. AOT and trim friendly (`[LibraryImport]`).
- Package id: `Drasi`.
- RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
  Layout: `runtimes/<rid>/native/`.
