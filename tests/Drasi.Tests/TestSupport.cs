// Copyright 2026 The Drasi Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Text.Json.Nodes;
using Drasi;
using Xunit;

namespace Drasi.Tests;

internal static class TestEngine
{
    internal const string OrdersQuery = "MATCH (o:Order) RETURN o.id AS id";
    internal const string OpenOrders =
        "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total";

    internal static string Id(string prefix = "test") => $"{prefix}-{Guid.NewGuid():N}";

    internal static SourceChange Order(string id, string? status = null, int? total = null)
    {
        var properties = new JsonObject { ["id"] = id };
        if (status is not null)
        {
            properties["status"] = status;
        }

        if (total is { } value)
        {
            properties["total"] = value;
        }

        return new SourceChange
        {
            Op = ChangeOp.Insert,
            Id = id,
            Labels = ["Order"],
            Properties = properties,
        };
    }

    internal static async Task<Engine> StartedAsync(string prefix = "test")
    {
        var engine = await Engine.CreateAsync(Id(prefix));
        await engine.StartAsync();
        return engine;
    }

    internal static async Task<IReadOnlyList<JsonObject>> WaitForRowsAsync(
        Engine engine,
        string queryId,
        TimeSpan? timeout = null)
    {
        IReadOnlyList<JsonObject> rows = [];
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            rows = await engine.GetQueryResultsAsync(queryId);
            if (rows.Count > 0)
            {
                return rows;
            }

            await Task.Delay(50);
        }

        return rows;
    }

    internal static async Task<T> WaitUntilAsync<T>(
        Func<Task<T>> poll,
        Func<T, bool> ready,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        var value = await poll();
        while (DateTime.UtcNow < deadline && !ready(value))
        {
            await Task.Delay(25);
            value = await poll();
        }

        return value;
    }

    internal static async Task WaitForReactionAsync(Engine engine, string id, TimeSpan? timeout = null)
    {
        var listed = await WaitUntilAsync(
            () => engine.ListReactionsAsync(),
            list => list.Any(item => item.Id == id),
            timeout);
        if (!listed.Any(item => item.Id == id))
        {
            throw new TimeoutException($"reaction '{id}' did not appear");
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Starts an async enumerable so native subscription runs before the caller
/// triggers the event it wants to observe.
/// </summary>
internal sealed class StartedStream<T> : IAsyncDisposable
{
    internal StartedStream(IAsyncEnumerable<T> stream, CancellationToken cancellationToken = default)
    {
        Enumerator = stream.GetAsyncEnumerator(cancellationToken);
        Pending = Enumerator.MoveNextAsync().AsTask();
    }

    internal IAsyncEnumerator<T> Enumerator { get; }

    internal Task<bool> Pending { get; private set; }

    internal T Current => Enumerator.Current;

    internal Task<bool> MoveNext()
    {
        Pending = Enumerator.MoveNextAsync().AsTask();
        return Pending;
    }

    internal async Task<T> WaitForAsync(Func<T, bool> match, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (await Pending.WaitAsync(cts.Token))
        {
            if (match(Current))
            {
                return Current;
            }

            _ = MoveNext();
        }

        throw new TimeoutException("stream ended before the expected item");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Enumerator.DisposeAsync();
        }
        catch (Exception ex) when (
            ex is NotSupportedException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}

/// <summary>Skipped unless <c>DRASI_SOAK_TESTS=1</c>, matching the Node analog.</summary>
internal sealed class SoakFactAttribute : FactAttribute
{
    public SoakFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DRASI_SOAK_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "set DRASI_SOAK_TESTS=1 to run soak tests";
        }
    }
}
