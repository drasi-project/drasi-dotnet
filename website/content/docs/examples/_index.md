---
title: "Examples"
linkTitle: "Examples"
weight: 50
description: >
  Runnable samples in this repository, from a console quickstart to plugins and the generic host.
---

The repository ships three runnable examples. Each is a small console app that
demonstrates a different slice of `Drasi`.

Build the native library once, then run any sample:

```bash
git clone https://github.com/drasi-project/drasi-dotnet.git
cd drasi-dotnet
cargo build --release --manifest-path native/Cargo.toml
```

The C# library resolves `libdrasi_ffi` from `native/target/{release,debug}`,
so a plain `dotnet run` works after that cargo build.

## Quickstart: push a change and print the diff

[`examples/Quickstart`](https://github.com/drasi-project/drasi-dotnet/tree/main/examples/Quickstart)
is the end-to-end change-driven scenario.

It creates an `Engine`, adds an in-process `orders` source, registers this
Cypher query:

```cypher
MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total
```

then attaches a callback reaction, waits for the query to run, and pushes one
insert:

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

Run it:

```bash
dotnet run --project examples/Quickstart
```

or `./scripts/run-quickstart.sh`.

Expected output:

```text
  Add {"id":"o1","total":42}
open orders: [{"id":"o1","total":42}]
```

The reaction prints the `Add` diff as soon as the query incorporates the
insert. `GetQueryResultsAsync` then prints the current result set. That is
the whole loop: application change in, continuous query, result diff out.

## Hosted: generic host and AddDrasi

[`examples/Hosted`](https://github.com/drasi-project/drasi-dotnet/tree/main/examples/Hosted)
declares the same topology in `AddDrasi`. The host starts the engine, applies
sources, queries, and reactions, seeds one order, and shuts down with the
process.

```bash
dotnet run --project examples/Hosted
```

See [Use the generic host](../guides/hosting/).

## Plugins: install from ghcr.io

[`examples/Plugins`](https://github.com/drasi-project/drasi-dotnet/tree/main/examples/Plugins)
searches the registry, installs `source/mock` and `reaction/log` from
`ghcr.io`, and runs a counter query. Needs network.

```bash
dotnet run --project examples/Plugins
```

See [Work with plugins](../guides/plugins/).
