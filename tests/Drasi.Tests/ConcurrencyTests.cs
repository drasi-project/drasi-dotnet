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

using Drasi;
using Xunit;

namespace Drasi.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task ConcurrentEnginesDoNotBlockEachOther()
    {
        var engines = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Engine.CreateAsync(TestEngine.Id("par"))));
        try
        {
            await Task.WhenAll(engines.Select(engine => engine.StartAsync()));
            var running = await Task.WhenAll(engines.Select(engine => engine.IsRunningAsync()));
            Assert.All(running, Assert.True);
        }
        finally
        {
            await Task.WhenAll(engines.Select(engine => engine.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task ConcurrentPushesAreVisible()
    {
        await using var engine = await TestEngine.StartedAsync("pushes");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            engine.PushChangeAsync("orders", TestEngine.Order($"o{i}"))));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        IReadOnlyList<System.Text.Json.Nodes.JsonObject> rows = [];
        while (DateTime.UtcNow < deadline)
        {
            rows = await engine.GetQueryResultsAsync("q");
            if (rows.Count == 8)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal(8, rows.Count);
    }

    [Fact]
    public async Task TwoQueriesShareOneSource()
    {
        await using var engine = await TestEngine.StartedAsync("two-q");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("open", TestEngine.OpenOrders, ["orders"]);
        await engine.AddQueryAsync("all", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("open");
        await engine.WaitForQueryAsync("all");
        await engine.PushChangeAsync("orders", TestEngine.Order("o1", "open", 1));
        await engine.PushChangeAsync("orders", TestEngine.Order("o2", "closed", 2));
        Assert.Equal("o1", Assert.Single(await TestEngine.WaitForRowsAsync(engine, "open"))["id"]?.ToString());
        var all = await TestEngine.WaitUntilAsync(
            () => engine.GetQueryResultsAsync("all"),
            rows => rows.Count == 2);
        Assert.Equal(2, all.Count);
    }
}
