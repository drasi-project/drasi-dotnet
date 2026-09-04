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
using Drasi.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Drasi.Tests;

public sealed class ApiSurfaceTests
{
    private const string OrdersQuery = "MATCH (o:Order) RETURN o.id AS id";

    [Fact]
    public async Task ShutdownRejectsLaterCalls()
    {
        var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.ShutdownAsync();
        var ex = await Assert.ThrowsAsync<DrasiException>(() => engine.StartAsync());
        Assert.Equal(DrasiErrorCodes.EngineClosed, ex.Code);
        engine.Dispose();
    }

    [Fact]
    public async Task CsharpSourceBootstrapsLateQuery()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.PushChangeAsync("orders", Order("o1"));

        await engine.AddQueryAsync("open", OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("open");
        var rows = await WaitForRowsAsync(engine, "open");
        Assert.Equal("o1", Assert.Single(rows)["id"]?.ToString());
    }

    [Fact]
    public async Task QueryResultsUsesStableReactionId()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("open", OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("open");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var streaming = engine.QueryResultsAsync("open", "stream-open", cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = streaming.MoveNextAsync().AsTask();
        try
        {
            var listed = await WaitUntilAsync(
                async () => await engine.ListReactionsAsync(),
                list => list.Any(item => item.Id == "stream-open"));
            Assert.Contains(listed, item => item.Id == "stream-open");
        }
        finally
        {
            cts.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
            }

            try
            {
                await streaming.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task FromConfigReadsIConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["id"] = "from-cfg",
                ["sources:0:id"] = "orders",
                ["queries:0:id"] = "open",
                ["queries:0:query"] = OrdersQuery,
                ["queries:0:sources:0"] = "orders",
            })
            .Build();

        await using var engine = await Engine.FromConfigAsync(config);
        Assert.Equal("from-cfg", engine.Id);
        Assert.True(await engine.IsRunningAsync());
        await engine.PushChangeAsync("orders", Order("o1"));
        var rows = await WaitForRowsAsync(engine, "open");
        Assert.Equal("o1", Assert.Single(rows)["id"]?.ToString());
    }

    [Fact]
    public async Task HealthCheckIsHealthyWhenRunning()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        var check = new DrasiHealthCheck(engine);
        var down = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, down.Status);

        await engine.StartAsync();
        var up = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, up.Status);
    }

    [Fact]
    public async Task LoadPluginsOnEmptyDirectoryIsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drasi-plug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
            var summary = await engine.LoadPluginsAsync(dir);
            Assert.Equal(0, summary.Plugins);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownSecretStoreKindIsTyped()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        var ex = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.UseSecretStoreAsync("vault"));
        Assert.Equal(DrasiErrorCodes.UnknownSecretStoreKind, ex.Code);
    }

    [Fact]
    public async Task StartEmitsDrasiActivity()
    {
        var names = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Drasi",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStarted = activity => names.Add(activity.OperationName),
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        await engine.StopAsync();
        Assert.Contains("Start", names);
        Assert.Contains("Stop", names);
    }

    [Fact]
    public async Task GraphSchemaReturnsObject()
    {
        await using var engine = await Engine.CreateAsync($"test-{Guid.NewGuid():N}");
        await engine.StartAsync();
        var schema = await engine.GetGraphSchemaAsync();
        Assert.NotNull(schema.Nodes);
    }

    private static SourceChange Order(string id) => new()
    {
        Op = ChangeOp.Insert,
        Id = id,
        Labels = ["Order"],
        Properties = new JsonObject { ["id"] = id },
    };

    private static async Task<IReadOnlyList<JsonObject>> WaitForRowsAsync(Engine engine, string queryId)
    {
        IReadOnlyList<JsonObject> rows = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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

    private static async Task<T> WaitUntilAsync<T>(Func<Task<T>> poll, Func<T, bool> ready)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        T value = await poll();
        while (DateTime.UtcNow < deadline && !ready(value))
        {
            await Task.Delay(25);
            value = await poll();
        }

        return value;
    }
}
