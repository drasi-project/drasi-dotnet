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

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Drasi;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Drasi.Tests;

public sealed class EngineTests
{
    private const string OpenOrders =
        "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total";

    [Fact]
    public async Task LoggerFactoryIsAccepted()
    {
        var factory = new CollectingLoggerFactory();
        await using var engine = await Engine.CreateAsync(
            $"test-{Guid.NewGuid():N}",
            new EngineOptions { LoggerFactory = factory });
        await engine.StartAsync();
        Assert.True(await engine.IsRunningAsync());
    }

    [Fact]
    public void HostInfoReportsRocksDb()
    {
        var info = Engine.GetHostInfo();
        Assert.Contains("rocksdb", info.IndexBackends);
        Assert.Equal(info.CoreVersion, DrasiVersion.Core);
        Assert.Equal(info.LibVersion, DrasiVersion.Lib);
    }

    [Fact]
    public async Task DurableReactionRequiresStateStore()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            engine.AddDurableReactionAsync("watch", ["open"], _ => Task.CompletedTask));
        Assert.Equal(DrasiErrorCodes.DurableRequiresStateStore, ex.Code);
    }

    [Fact]
    public async Task UnknownPluginSourceKindIsTyped()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        var ex = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.AddSourceAsync("postgres", "orders"));
        Assert.Equal(DrasiErrorCodes.UnknownSourceKind, ex.Code);
    }

    [Fact]
    public async Task StateStoreRequiresPath()
    {
        var config = JsonNode.Parse("""
            { "id": "x", "stateStore": { "kind": "redb" } }
            """)!;
        var ex = await Assert.ThrowsAsync<ConfigException>(() => Engine.FromConfigAsync(config));
        Assert.Equal(DrasiErrorCodes.StateStorePathRequired, ex.Code);
    }

    [Fact]
    public async Task PasswordIdentityCreates()
    {
        await using var engine = await Engine.CreateAsync(
            $"test-{Guid.NewGuid():N}",
            new EngineOptions
            {
                Identity = new IdentityOptions
                {
                    Kind = "password",
                    Username = "u",
                    Password = "p",
                },
                Secrets = new Dictionary<string, string> { ["k"] = "v" },
            });
        Assert.False(await engine.IsRunningAsync());
        var kinds = await engine.PluginKindsAsync();
        Assert.Empty(kinds.Sources);
    }

    [Fact]
    public void ErrorCodesAreUniqueScreamingSnake()
    {
        Assert.Equal(DrasiErrorCodes.All.Count, DrasiErrorCodes.All.Distinct().Count());
        foreach (var code in DrasiErrorCodes.All)
        {
            Assert.Matches("^[A-Z0-9_]+$", code);
        }
    }

    [Fact]
    public async Task InsertIsVisibleInSnapshotAndReaction()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("open", OpenOrders, ["orders"]);
        await engine.WaitForQueryAsync("open");

        var seen = new TaskCompletionSource<QueryResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await engine.AddReactionAsync("watch", ["open"], evt => seen.TrySetResult(evt));

        await engine.PushChangeAsync("orders", Order("o1", "open", 42));
        var evt = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(evt.Results, diff => diff.Type == DiffType.Add);

        var rows = await engine.GetQueryResultsAsync("open");
        Assert.Single(rows);
        Assert.Equal("o1", rows[0]["id"]?.ToString());
    }

    [Fact]
    public async Task UnknownChangeOpIsTyped()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");

        var ex = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.PushChangeAsync("orders", new JsonObject { ["op"] = "merge", ["id"] = "o1" }));
        Assert.Equal(DrasiErrorCodes.UnknownChangeOp, ex.Code);
    }

    [Fact]
    public async Task MissingCsharpSourceIsTyped()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        var ex = await Assert.ThrowsAsync<SourceException>(() =>
            engine.PushChangeAsync("nope", Order("o1", "open", 1)));
        Assert.Equal(DrasiErrorCodes.NoCsharpSource, ex.Code);
    }

    [Fact]
    public async Task InvalidDispatchModeIsTyped()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            engine.AddQueryAsync(
                "q",
                OpenOrders,
                ["orders"],
                new QueryOptions { DispatchMode = "nope" }));
        Assert.Equal(DrasiErrorCodes.ConfigInvalid, ex.Code);
    }

    [Fact]
    public async Task ListAndStatusCoverSourcesQueriesReactions()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("open", OpenOrders, ["orders"]);
        await engine.WaitForQueryAsync("open");
        await engine.AddReactionAsync("watch", ["open"], _ => { });

        var sources = await engine.ListSourcesAsync();
        Assert.Contains(sources, s => s.Id == "orders" && s.Status == ComponentStatus.Running);

        var queries = await engine.ListQueriesAsync();
        Assert.Contains(queries, q => q.Id == "open" && q.Status == ComponentStatus.Running);

        Assert.Equal(ComponentStatus.Running, await engine.GetQueryStatusAsync("open"));
        Assert.True(await engine.IsRunningAsync());

        var metrics = await engine.GetQueryMetricsAsync("open");
        Assert.True(metrics.LiveResultsCount == 0 || metrics.LiveResultsCount > 0);
    }

    [Fact]
    public async Task QueryResultsStreamEmitsAdd()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("open", OpenOrders, ["orders"]);
        await engine.WaitForQueryAsync("open");

        var seen = new TaskCompletionSource<QueryResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in engine.QueryResultsAsync("open"))
            {
                if (evt.Results.Any(diff => diff.Type == DiffType.Add))
                {
                    seen.TrySetResult(evt);
                    break;
                }
            }
        });

        await engine.PushChangeAsync("orders", Order("o2", "open", 7));
        var evt = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("open", evt.QueryId);
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposePreventsFurtherCalls()
    {
        var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartAsync());
    }

    [Fact]
    public async Task FromConfigCreatesSourceAndQuery()
    {
        var config = JsonNode.Parse("""
            {
              "id": "from-config",
              "sources": [{ "id": "orders" }],
              "queries": [{
                "id": "open",
                "query": "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
                "sources": ["orders"]
              }]
            }
            """)!;
        await using var engine = await Engine.FromConfigAsync(config);
        Assert.True(await engine.IsRunningAsync());
        await engine.WaitForQueryAsync("open");
        await engine.PushChangeAsync("orders", Order("o3", "open", 3));
        IReadOnlyList<JsonObject> rows = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            rows = await engine.GetQueryResultsAsync("open");
            if (rows.Count > 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Single(rows);
    }

    [Fact]
    public async Task FromConfigDefaultsIdAndMergesOptions()
    {
        var config = JsonNode.Parse("""{ "sources": [] }""")!;
        await using var engine = await Engine.FromConfigAsync(
            config,
            new EngineOptions { Secrets = new Dictionary<string, string> { ["k"] = "v" } });
        Assert.Equal("drasi", engine.Id);
        Assert.True(await engine.IsRunningAsync());
    }

    [Fact]
    public async Task DisposeEndsQueryResultsStream()
    {
        var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("open", OpenOrders, ["orders"]);
        await engine.WaitForQueryAsync("open");

        var consume = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in engine.QueryResultsAsync("open"))
                {
                }
            }
            catch (ObjectDisposedException)
            {
            }
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var reactions = await engine.ListReactionsAsync();
            if (reactions.Any(r => r.Id.StartsWith("__stream_", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(20);
        }

        await engine.DisposeAsync();
        await consume.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class CollectingLoggerFactory : ILoggerFactory
    {
        public ConcurrentBag<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CollectingLoggerFactory factory, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                factory.Entries.Add((category, logLevel, formatter(state, exception)));
            }
        }
    }

    private static SourceChange Order(string id, string status, int total) => new()
    {
        Op = ChangeOp.Insert,
        Id = id,
        Labels = ["Order"],
        Properties = new JsonObject
        {
            ["id"] = id,
            ["status"] = status,
            ["total"] = total,
        },
    };
}
