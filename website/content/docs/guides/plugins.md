---
title: "Work with plugins"
linkTitle: "Plugins"
weight: 30
description: >
  Discover, install, and load native Drasi source and reaction plugins from ghcr.io.
---

Plugins are self-contained native libraries (`.so`, `.dylib`, or `.dll`)
loaded through `drasi-host-sdk`, the same host path `drasi-server` uses.
Install them from `ghcr.io/drasi-project` or load files you already have on
disk.

This guide needs network access to `ghcr.io`.

## Inspect the host

```csharp
var info = Engine.GetHostInfo();
Console.WriteLine($"{info.TargetTriple}  core {info.CoreVersion}  lib {info.LibVersion}");
```

`GetHostInfo` reports the platform triple and the `drasi-core`, `drasi-lib`,
and plugin SDK versions this binary was built against. Plugins must match
those versions.

## Search the registry

```csharp
var available = await Engine.SearchPluginsAsync();
var sources = available.Where(item => item.PluginType == "source");
```

`SearchPluginsAsync` lists published plugins. Pass a query string to filter.
`ListPluginTagsAsync(repository)` lists tags for one repository.
`ResolvePluginAsync("source/mock")` picks the artifact for this host without
downloading it.

Bare references such as `"source/mock"` default to `ghcr.io/drasi-project`.

## Install and load

```csharp
var pluginsDir = "./plugins";
Directory.CreateDirectory(pluginsDir);

await using var drasi = await Engine.CreateAsync("plugin-demo");
await drasi.InstallPluginAsync("source/mock", directory: pluginsDir, verify: true);
await drasi.InstallPluginAsync("reaction/log", directory: pluginsDir, verify: true);

var kinds = await drasi.PluginKindsAsync();
Console.WriteLine(string.Join(", ", kinds.Sources));

await drasi.StartAsync();
await drasi.AddSourceAsync(
    "mock",
    "counters",
    new JsonObject
    {
        ["dataType"] = new JsonObject { ["type"] = "counter" },
        ["intervalMs"] = 200,
    });
```

`InstallPluginAsync` downloads the cdylib into `directory` and, by default,
loads it. Set `verify: true` to attach a cosign verifier to the registry
client. Set `requireSigned: true` to fail the install unless the signature
status is `verified` (`PLUGIN_SIGNATURE_INVALID` otherwise). The SHA-256
filename allowlist is the `verify` argument on `LoadPluginsAsync`, not this
flag.

To load files already on disk:

```csharp
await drasi.LoadPluginsAsync("./plugins");
```

`WatchPluginsAsync("./plugins")` hot-reloads as files appear or change
(1 second debounce by default).

Read a plugin's config JSON Schema with `GetSourceConfigSchemaAsync(kind)` or
`GetReactionConfigSchemaAsync(kind)` before you call `AddSourceAsync`.

## Pin versions with a lockfile

```csharp
await drasi.WriteLockfileAsync("./plugins");
var locked = Engine.ReadLockfile("./plugins");
await drasi.InstallFromLockfileAsync("./plugins");
```

`WriteLockfileAsync` records digest-pinned references for the plugins already
loaded. `InstallFromLockfileAsync` reinstalls that set on another machine.

## Hosted apps

On `DrasiBuilder`:

```csharp
builder.Services.AddDrasi("orders-app", drasi =>
{
    drasi.InstallPlugin("source/mock", directory: "./plugins", verify: true);
    drasi.AddSource("mock", "counters", config);
    drasi.AddQuery("counts", "MATCH (c:Counter) RETURN c.value AS value", ["counters"]);
});
```

The [Plugins](https://github.com/drasi-project/drasi-dotnet/tree/main/examples/Plugins)
sample installs `source/mock` and `reaction/log` and prints counter rows.
