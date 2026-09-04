---
title: "Getting started"
linkTitle: "Getting started"
weight: 10
description: >
  Install Drasi and run your first continuous query in .NET.
---

This guide gets you from a clone to a running **continuous query**. You will
push a change from C# and watch the query result update live. No database, no
server, no Kubernetes.

## Prerequisites

- **.NET 8** or newer.
- **Rust stable** (see `rust-toolchain.toml`) only if you build the native
  library from this repository.
- A supported platform: **Windows (x64)**, **Linux (x64 or arm64)**, or
  **macOS (x64 or arm64)**.

Consumers of the NuGet package do not need a Rust toolchain. Prebuilt binaries
ship for those RIDs.

## Run the Quickstart

```bash
git clone https://github.com/drasi-project/drasi-dotnet.git
cd drasi-dotnet
cargo build --release --manifest-path native/Cargo.toml
dotnet run --project examples/Quickstart
```

Or `./scripts/run-quickstart.sh`.

Expected output:

```text
  Add {"id":"o1","total":42}
open orders: [{"id":"o1","total":42}]
```

The C# library resolves `libdrasi_ffi` from `native/target/{release,debug}`,
so `dotnet run` works after the cargo build.

## What that program does

The Quickstart creates an `Engine`, adds an in-process source, registers a
Cypher continuous query, prints each result-set diff, pushes one insert, then
prints the current result set. Core of `examples/Quickstart/Program.cs`:

```csharp
using System.Text.Json.Nodes;
using Drasi;

await using var drasi = await Engine.CreateAsync("dotnet-demo");
await drasi.StartAsync();
await drasi.AddSourceAsync("orders");
await drasi.AddQueryAsync(
    "open",
    "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
    ["orders"]);
await drasi.WaitForQueryAsync("open");

var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await drasi.AddReactionAsync("watch", ["open"], evt =>
{
    foreach (var diff in evt.Results)
    {
        Console.WriteLine($"  {diff.Type} {diff.Data}");
        seen.TrySetResult();
    }
});

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

await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
var rows = await drasi.GetQueryResultsAsync("open");
Console.WriteLine($"open orders: [{string.Join(",", rows.Select(row => row.ToJsonString()))}]");
```

Step by step:

1. `Engine.CreateAsync` builds an engine that is not started yet.
2. `StartAsync` starts it.
3. `AddSourceAsync("orders")` registers an in-process source you can push into.
4. `AddQueryAsync` registers a Cypher query. The v1 query API is this string.
   There is no LINQ provider. `WaitForQueryAsync` blocks until the query is
   running. `AddQueryAsync` returns when the query is provisioned. Startup
   finishes in the background.
5. `AddReactionAsync` prints each result-set diff.
6. `PushChangeAsync` inserts one open order. The query emits an `Add` diff.
7. `GetQueryResultsAsync` prints the current result set.
8. `await using` disposes the engine.

`SourceChange.Id` is the graph key. A query that selects `o.id` reads a
**property** of that name, so put `id` in `Properties` as well.

## Use it in your own app

Tagged releases publish `Drasi` to nuget.org with those RID binaries inside
the nupkg. In your project:

```bash
dotnet add package Drasi
```

Then copy the shape above into `Program.cs`. You do not need Rust, and you do
not need this repository.

Until a `v*` tag has been published, keep using the Quickstart in this
repository, or add a project reference to `src/Drasi/Drasi.csproj` after
`cargo build`.

## Next steps

- [Concepts](../concepts/) explains sources, queries, and reactions.
- [Use the generic host](../guides/hosting/) shows `AddDrasi` for worker
  services and ASP.NET Core.
- [API reference](../api/) lists every method on `Engine`.
- [Examples](../examples/) points at Quickstart, Hosted, and Plugins.
