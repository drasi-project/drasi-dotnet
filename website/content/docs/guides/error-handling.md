---
title: "Handle errors"
linkTitle: "Error handling"
weight: 60
description: >
  Catch DrasiException and branch on stable error codes.
---

Engine and plugin failures are a `DrasiException` (or a more specific
subclass). Branch on `Code`, not on message text. Codes live in
`DrasiErrorCodes`. Bad arguments still throw `ArgumentException`. After
`Dispose` or `DisposeAsync`, further calls throw `ObjectDisposedException`.
After `ShutdownAsync` without dispose, native calls may throw `DrasiException`
with `ENGINE_CLOSED`.

## Catch by type or by code

```csharp
try
{
    await drasi.AddSourceAsync("postgres", "orders", config);
}
catch (UnknownKindException ex) when (ex.Code == DrasiErrorCodes.UnknownSourceKind)
{
    Console.WriteLine($"plugin kind not loaded: {ex.Message}");
}
catch (DrasiException ex)
{
    Console.WriteLine($"{ex.Code}: {ex.Message}");
}
```

## Exception types

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

| Type | When |
| --- | --- |
| `UnknownKindException` | Unknown source, reaction, bootstrap, store, identity, query language, or change op |
| `ConfigException` | Missing required config (paths, identity, durable store, JSON shape) |
| `SourceException` | Bad `SourceChange` (missing id, relation without both ends) |
| `StreamLaggedException` | An `IAsyncEnumerable` consumer overflowed its 256-item buffer |
| `PluginNotFoundException` | Registry or disk lookup failed |
| `PluginCompatibilityException` | Plugin SDK or core version does not match this host |
| `PluginSignatureException` | `requireSigned: true` and the artifact was not `verified` |

## Codes

`DrasiErrorCodes.All` lists every code the native host may report. Common ones:

| Code | Typical cause |
| --- | --- |
| `UNKNOWN_SOURCE_KIND` | Plugin not loaded before `AddSourceAsync(kind, ...)` |
| `NO_CSHARP_SOURCE` | `PushChangeAsync` on a plugin source, or a source that was never added |
| `CHANGE_ID_REQUIRED` | `SourceChange.Id` missing |
| `RELATION_REQUIRES_BOTH_ENDS` | Relation without both `StartId` and `EndId` |
| `DURABLE_REQUIRES_STATE_STORE` | `AddDurableReactionAsync` without a redb store |
| `CONFIG_INVALID` | `FromConfigAsync` document is not an object, or a field is wrong |
| `STREAM_LAGGED` | Slow `IAsyncEnumerable` consumer |
| `PLUGIN_NOT_FOUND` | `InstallPluginAsync` or `ResolvePluginAsync` missed the registry |
| `ENGINE_CLOSED` | Call after shutdown |
