---
title: "Use the generic host"
linkTitle: "Generic host"
weight: 10
description: >
  Register Engine with AddDrasi so the generic host starts, configures, and stops it.
---

Use `AddDrasi` when your app already has a generic host (ASP.NET Core, a
worker service, or `Host.CreateApplicationBuilder`). The core API does not
require DI. This guide is only for hosted apps.

## Register the engine

```csharp
using System.Text.Json.Nodes;
using Drasi;
using Drasi.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddDrasi("orders-app", drasi =>
{
    drasi.AddSource("orders");
    drasi.AddQuery(
        "open",
        "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
        ["orders"]);
    drasi.AddReaction("watch", ["open"], evt =>
    {
        foreach (var diff in evt.Results)
        {
            Console.WriteLine($"{diff.Type} {diff.Data}");
        }
    });
});
await builder.Build().RunAsync();
```

`AddDrasi` registers `Engine` as a singleton and a `DrasiHostedService`. On
start, the host:

1. Creates the engine (and picks up the host `ILoggerFactory`).
2. Loads any plugins you declared on `DrasiBuilder`.
3. Calls `StartAsync`.
4. Adds sources, queries, and reactions.
5. Waits for each auto-started query.
6. Pushes any `Seed` changes.

On stop, the host shuts the engine down. You do not add your own
`IHostedService` for Drasi.

Call `AddDrasi` once per `IServiceCollection`. A second call throws
`InvalidOperationException`.

## Resolve Engine from the container

Push changes from the rest of the app with the registered `Engine`:

```csharp
var engine = app.Services.GetRequiredService<Engine>();
await engine.PushChangeAsync("orders", change);
```

An overload `AddDrasi(engineId, (drasi, sp) => { ... })` gives the configure
callback the `IServiceProvider` so reactions can resolve other services.

## Seed data at startup

Call `Seed` for changes that should fire after the topology is up:

```csharp
drasi.Seed("orders", new SourceChange
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

## Health checks

```csharp
builder.Services.AddHealthChecks().AddDrasiCheck();
```

`AddDrasiCheck` reports healthy when `Engine.IsRunningAsync` is true.

## Configure from appsettings

```csharp
builder.Services.AddDrasi(builder.Configuration.GetSection("Drasi"), drasi =>
{
    drasi.AddReaction("watch", ["open"], evt => { /* C# callback */ });
});
```

`FromConfigAsync` starts the sources and queries declared in JSON (in-process
or plugin). Use the `DrasiBuilder` callback for in-process reactions and seeds.
See [Load a topology from configuration](configuration/).

The [Hosted](https://github.com/drasi-project/drasi-dotnet/tree/main/examples/Hosted)
sample is a complete worker that uses this pattern.
