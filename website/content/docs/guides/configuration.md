---
title: "Load a topology from configuration"
linkTitle: "Configuration"
weight: 50
description: >
  Build and start an engine from JSON or IConfiguration.
---

`Engine.FromConfigAsync` builds an engine from a document of sources, queries,
and reactions, then **starts** it. Missing `id` defaults to `drasi`.

This matches Python and Node `fromConfig`. Use `CreateAsync` when you want to
add components in code after construction.

## JSON document

```csharp
var config = JsonNode.Parse("""
{
  "id": "demo",
  "sources": [{ "id": "orders", "autoStart": true }],
  "queries": [{
    "id": "open",
    "query": "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
    "sources": ["orders"],
    "language": "cypher"
  }]
}
""")!;

await using var drasi = await Engine.FromConfigAsync(config);
```

Document keys:

| Key | Meaning |
| --- | --- |
| `id` | Engine id (default `drasi`) |
| `secrets`, `stateStore`, `indexStore`, `identity` | Same as `EngineOptions`. Merged from the `options` argument when the document omits them. |
| `pluginsDir` | Directory passed to `LoadPluginsAsync` before start |
| `sources` | In-process (`id` only) or plugin (`kind`, `id`, `config`, `autoStart`, `bootstrap`) |
| `queries` | `id`, `query`, `sources`, optional `language`, `joins` |
| `reactions` | Plugin reactions (`kind`, `id`, `queries`, `config`) |

C# callback reactions cannot be expressed in JSON. Add them after
`FromConfigAsync`, or use `AddDrasi` with a `DrasiBuilder` callback.

## appsettings.json

```json
{
  "Drasi": {
    "id": "orders-app",
    "sources": [{ "id": "orders" }],
    "queries": [{
      "id": "open",
      "query": "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
      "sources": ["orders"]
    }]
  }
}
```

```csharp
await using var drasi = await Engine.FromConfigAsync(
    configuration.GetSection("Drasi"));
```

In a hosted app:

```csharp
builder.Services.AddDrasi(builder.Configuration.GetSection("Drasi"), drasi =>
{
    drasi.AddReaction("watch", ["open"], evt =>
    {
        foreach (var diff in evt.Results)
        {
            Console.WriteLine(diff.Type);
        }
    });
});
```

The configuration section supplies sources and queries (in-process or plugin).
The `DrasiBuilder` callback supplies in-process reactions and seeds.

## Engine options

Pass `EngineOptions` to `CreateAsync` or `FromConfigAsync`:

```csharp
await Engine.CreateAsync("orders-app", new EngineOptions
{
    LoggerFactory = logs,
    PluginsDir = "./plugins",
    Secrets = new Dictionary<string, string> { ["db"] = password },
    StateStore = new StateStoreOptions { Path = "./state.redb" },
    IndexStore = new IndexStoreOptions { Path = "./index" },
    Identity = new IdentityOptions { Kind = "token", Token = token },
});
```

RocksDB holds a process-exclusive lock on `IndexStore.Path`. Only one engine
may use a given path at a time. `ShutdownAsync` releases that lock.
