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

public sealed class StreamingTests
{
    [Fact]
    public async Task AllEventsEmitsLifecycleTransitions()
    {
        await using var engine = await Engine.CreateAsync(TestEngine.Id("events"));
        await engine.AddSourceAsync("orders");
        await engine.StartAsync();
        await TestEngine.WaitUntilAsync(
            () => engine.GetSourceStatusAsync("orders"),
            status => status == ComponentStatus.Running);
        await using var stream = new StartedStream<ComponentEvent>(engine.AllEventsAsync());
        var evt = await stream.WaitForAsync(item =>
            item.Status == ComponentStatus.Running && item.ComponentId == "orders");
        Assert.Equal(ComponentStatus.Running, evt.Status);
        Assert.Equal("orders", evt.ComponentId);
    }

    [Fact]
    public async Task SourceAndQueryEventsStream()
    {
        await using var engine = await TestEngine.StartedAsync("src-events");
        await engine.AddSourceAsync("orders", autoStart: false);
        await engine.AddQueryAsync(
            "q",
            TestEngine.OrdersQuery,
            ["orders"],
            new QueryOptions { AutoStart = false });

        await using var sourceStream = new StartedStream<ComponentEvent>(engine.SourceEventsAsync("orders"));
        await using var queryStream = new StartedStream<ComponentEvent>(engine.QueryEventsAsync("q"));

        await engine.StartSourceAsync("orders");
        await engine.StartQueryAsync("q");
        await sourceStream.WaitForAsync(evt => evt.Status == ComponentStatus.Running);
        await queryStream.WaitForAsync(evt => evt.Status == ComponentStatus.Running);
    }

    [Fact]
    public async Task ReactionEventsAndMetrics()
    {
        await using var engine = await TestEngine.StartedAsync("rx-events");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.AddReactionAsync("watch", ["q"], _ => { }, autoStart: false);

        await using var stream = new StartedStream<ComponentEvent>(engine.ReactionEventsAsync("watch"));
        await engine.StartReactionAsync("watch");
        await stream.WaitForAsync(evt => evt.Status == ComponentStatus.Running);
        var metrics = await engine.GetReactionMetricsAsync("watch");
        Assert.NotNull(metrics);
        var lifecycle = await engine.GetLifecycleMetricsAsync();
        Assert.Equal(0UL, lifecycle.StartupRejectionDurableNoStore);
    }

    [Fact]
    public async Task QueryResultsSeesUpdateAndDelete()
    {
        await using var engine = await TestEngine.StartedAsync("diffs");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OpenOrders, ["orders"]);
        await engine.WaitForQueryAsync("q");

        var types = new List<DiffType>();
        await using var stream = new StartedStream<QueryResultEvent>(engine.QueryResultsAsync("q", "stream-diffs"));
        await TestEngine.WaitForReactionAsync(engine, "stream-diffs");

        await engine.PushChangeAsync("orders", TestEngine.Order("o1", "open", 1));
        await engine.PushChangeAsync(
            "orders",
            new SourceChange
            {
                Op = ChangeOp.Update,
                Id = "o1",
                Labels = ["Order"],
                Properties = new JsonObject { ["id"] = "o1", ["status"] = "open", ["total"] = 2 },
            });
        await engine.PushChangeAsync(
            "orders",
            new SourceChange
            {
                Op = ChangeOp.Delete,
                Id = "o1",
                Labels = ["Order"],
            });
        await stream.WaitForAsync(
            evt =>
            {
                foreach (var diff in evt.Results)
                {
                    types.Add(diff.Type);
                }

                return types.Contains(DiffType.Add)
                    && types.Contains(DiffType.Update)
                    && types.Contains(DiffType.Delete);
            },
            TimeSpan.FromSeconds(15));
        Assert.Contains(DiffType.Add, types);
        Assert.Contains(DiffType.Update, types);
        Assert.Contains(DiffType.Delete, types);
    }

    [Fact]
    public async Task LogStreamsCanBeSubscribed()
    {
        await using var engine = await TestEngine.StartedAsync("logs");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.AddReactionAsync("watch", ["q"], _ => { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        async Task Drain(IAsyncEnumerable<LogMessage> stream)
        {
            try
            {
                await foreach (var _ in stream.WithCancellation(cts.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Task.WhenAll(
            Drain(engine.QueryLogsAsync("q", cts.Token)),
            Drain(engine.SourceLogsAsync("orders", cts.Token)),
            Drain(engine.ReactionLogsAsync("watch", cts.Token)));
    }

    [Fact]
    public async Task RelationChangeIsAccepted()
    {
        await using var engine = await TestEngine.StartedAsync("rel");
        await engine.AddSourceAsync("graph");
        await engine.AddQueryAsync("q", "MATCH (a)-[r]->(b) RETURN r.id AS id", ["graph"]);
        await engine.WaitForQueryAsync("q");
        await engine.PushChangeAsync("graph", TestEngine.Order("a"));
        await engine.PushChangeAsync("graph", TestEngine.Order("b"));
        await engine.PushChangeAsync(
            "graph",
            new SourceChange
            {
                Op = ChangeOp.Insert,
                Id = "r1",
                Labels = ["LINKS"],
                StartId = "a",
                EndId = "b",
                Properties = new JsonObject { ["id"] = "r1" },
                EffectiveFrom = 1,
            });
        var row = Assert.Single(await TestEngine.WaitForRowsAsync(engine, "q"));
        Assert.Equal("r1", row["id"]?.ToString());
    }
}
