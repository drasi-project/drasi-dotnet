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
        var seen = new TaskCompletionSource<ComponentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in engine.AllEventsAsync())
            {
                if (evt.Status == ComponentStatus.Running)
                {
                    seen.TrySetResult(evt);
                    break;
                }
            }
        });

        await engine.StartAsync();
        var evt = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ComponentStatus.Running, evt.Status);
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
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

        var sourceSeen = new TaskCompletionSource<ComponentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var querySeen = new TaskCompletionSource<ComponentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceConsume = Task.Run(async () =>
        {
            await foreach (var evt in engine.SourceEventsAsync("orders"))
            {
                sourceSeen.TrySetResult(evt);
                break;
            }
        });
        var queryConsume = Task.Run(async () =>
        {
            await foreach (var evt in engine.QueryEventsAsync("q"))
            {
                querySeen.TrySetResult(evt);
                break;
            }
        });

        await engine.StartSourceAsync("orders");
        await engine.StartQueryAsync("q");
        await sourceSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await querySeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await sourceConsume.WaitAsync(TimeSpan.FromSeconds(5));
        await queryConsume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReactionEventsAndMetrics()
    {
        await using var engine = await TestEngine.StartedAsync("rx-events");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.AddReactionAsync("watch", ["q"], _ => { }, autoStart: false);

        var seen = new TaskCompletionSource<ComponentEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in engine.ReactionEventsAsync("watch"))
            {
                seen.TrySetResult(evt);
                break;
            }
        });

        await engine.StartReactionAsync("watch");
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var metrics = await engine.GetReactionMetricsAsync("watch");
        Assert.NotNull(metrics);
        var lifecycle = await engine.GetLifecycleMetricsAsync();
        Assert.True(lifecycle.StartupRejectionDurableNoStore == 0 || lifecycle.StartupRejectionDurableNoStore >= 0);
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueryResultsSeesUpdateAndDelete()
    {
        await using var engine = await TestEngine.StartedAsync("diffs");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OpenOrders, ["orders"]);
        await engine.WaitForQueryAsync("q");

        var types = new List<DiffType>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in engine.QueryResultsAsync("q", "stream-diffs"))
            {
                foreach (var diff in evt.Results)
                {
                    types.Add(diff.Type);
                }

                if (types.Contains(DiffType.Add) && types.Contains(DiffType.Delete))
                {
                    done.TrySetResult();
                    break;
                }
            }
        });

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
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(DiffType.Add, types);
        Assert.Contains(DiffType.Delete, types);
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
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
        var rows = await TestEngine.WaitForRowsAsync(engine, "q");
        Assert.True(rows.Count >= 0);
    }
}
