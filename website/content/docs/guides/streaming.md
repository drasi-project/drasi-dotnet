---
title: "Stream results with IAsyncEnumerable"
linkTitle: "Streaming"
weight: 40
description: >
  Subscribe to query diffs, lifecycle events, and logs as IAsyncEnumerable sequences.
---

Every streaming method on `Engine` returns `IAsyncEnumerable<T>`. Consume with
`await foreach`. Disposing the enumerator (or the engine) completes the
sequence.

## Query result diffs

```csharp
await foreach (var evt in drasi.QueryResultsAsync("open-orders"))
{
    foreach (var diff in evt.Results)
    {
        Console.WriteLine($"{diff.Type} {diff.Data}");
    }
}
```

`QueryResultsAsync` registers a hidden C# reaction named
`__stream_{queryId}_{guid}` and removes it when the enumerator is disposed.
Pass `reactionId` if you need a stable name.

This is the same payload a callback reaction receives (`QueryResultEvent`).
Prefer a reaction when the handler should outlive a single loop. Prefer
`QueryResultsAsync` when the consumer is a loop, a channel, or ASP.NET
endpoint code.

## Lifecycle events

```csharp
await foreach (var evt in drasi.QueryEventsAsync("open-orders"))
{
    Console.WriteLine($"{evt.ComponentId} -> {evt.Status}");
}
```

| Method | Emits |
| --- | --- |
| `QueryEventsAsync(id)` | Status changes for one query |
| `SourceEventsAsync(id)` | Status changes for one source |
| `ReactionEventsAsync(id)` | Status changes for one reaction |
| `AllEventsAsync()` | Status changes for every component |

`ComponentStatus` values include `Added`, `Starting`, `Running`, `Stopping`,
`Stopped`, `Removed`, `Reconfiguring`, and `Error`.

## Logs

```csharp
await foreach (var line in drasi.QueryLogsAsync("open-orders"))
{
    Console.WriteLine($"{line.Level} {line.Message}");
}
```

`SourceLogsAsync` and `ReactionLogsAsync` do the same for those components.
Plugin log lines show up here too.

## Backpressure

Each subscription uses a 256-item buffer. If the consumer is slower than the
producer and the buffer fills, the stream throws `StreamLaggedException`
(`STREAM_LAGGED`). Native callbacks never block on the consumer.

Honor `CancellationToken` on the enumerable. Every `*Async` method also takes
a token. Cancellation is cooperative: the native call runs on `Task.Run`.
