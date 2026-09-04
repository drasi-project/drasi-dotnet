---
title: "Push changes and handle diffs"
linkTitle: "Sources and reactions"
weight: 20
description: >
  Drive an in-process source with PushChangeAsync and handle query diffs in C#.
---

Use an in-process source when the data already lives in your application.
Use an in-process reaction when you want to handle result diffs in C# instead
of a plugin.

## Add a source you can push into

```csharp
await drasi.AddSourceAsync("orders");
```

The one-argument overload registers a C# source. There is no plugin `kind`.
Emit graph changes with `PushChangeAsync`:

```csharp
await drasi.PushChangeAsync("orders", new SourceChange
{
    Op = ChangeOp.Insert,
    Id = "o1",
    Labels = ["Order"],
    Properties = new JsonObject
    {
        ["id"] = "o1",
        ["status"] = "open",
        ["total"] = 42,
    },
});
```

`Id` is the graph key. Put the same value in `Properties["id"]` when a query
selects `o.id`.

For a relation, set both `StartId` and `EndId`. Omitting one throws
`SourceException` with code `RELATION_REQUIRES_BOTH_ENDS`.

`PushChangeAsync` also accepts a `JsonNode` if you already have JSON.

## Add a callback reaction

```csharp
await drasi.AddReactionAsync("watch", ["open-orders"], evt =>
{
    foreach (var diff in evt.Results)
    {
        switch (diff.Type)
        {
            case DiffType.Add:
                Console.WriteLine($"added {diff.Data}");
                break;
            case DiffType.Update:
                Console.WriteLine($"before {diff.Before} after {diff.After}");
                break;
            case DiffType.Delete:
                Console.WriteLine($"removed {diff.Data}");
                break;
        }
    }
});
```

Each `QueryResultEvent` has `QueryId`, `Sequence`, `Timestamp`, and `Results`.
A `QueryDiff` is one added, updated, or deleted row.

The native host invokes the callback on its own thread. Do not block it. For
async work that must survive a restart, use a durable reaction.

## Durable reactions

```csharp
await using var drasi = await Engine.CreateAsync("orders-app", new EngineOptions
{
    StateStore = new StateStoreOptions { Path = "./state.redb" },
});

await drasi.AddDurableReactionAsync("watch", ["open-orders"], async evt =>
{
    await HandleAsync(evt);
});
```

The checkpoint advances only after the callback succeeds. A redb
`StateStore` is required. Without one, the call throws `ConfigException`
(`DURABLE_REQUIRES_STATE_STORE`).

`RecoveryPolicy` is passed to the host as `strict` (default), `auto_reset`, or
`skip_gap`. Use `Strict` unless you have a reason to skip or reset a
checkpoint gap.

## Plugin sources and reactions

When the data lives outside your process, [install a plugin](plugins/) and
pass a `kind`:

```csharp
await drasi.AddSourceAsync("mock", "counters", new JsonObject
{
    ["dataType"] = new JsonObject { ["type"] = "counter" },
    ["intervalMs"] = 200,
});
await drasi.AddReactionAsync("log", "printer", ["counts"]);
```
