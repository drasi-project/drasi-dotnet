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

public sealed class LifecycleTests
{
    [Fact]
    public async Task StartStopTogglesIsRunning()
    {
        await using var engine = await Engine.CreateAsync(TestEngine.Id("life"));
        Assert.False(await engine.IsRunningAsync());
        await engine.StartAsync();
        Assert.True(await engine.IsRunningAsync());
        await engine.StopAsync();
        Assert.False(await engine.IsRunningAsync());
        var stopped = await Assert.ThrowsAsync<DrasiException>(() => engine.StopAsync());
        Assert.Contains("stopped", stopped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecondStartIsRejectedAndEngineKeepsRunning()
    {
        await using var engine = await TestEngine.StartedAsync("double-start");
        var ex = await Assert.ThrowsAsync<DrasiException>(() => engine.StartAsync());
        Assert.Equal(DrasiErrorCodes.EngineFailure, ex.Code);
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await engine.IsRunningAsync());

        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.PushChangeAsync("orders", TestEngine.Order("o1"));
        Assert.Equal("o1", Assert.Single(await TestEngine.WaitForRowsAsync(engine, "q"))["id"]?.ToString());
    }

    [Fact]
    public async Task StopThenStartProcessesChanges()
    {
        await using var engine = await Engine.CreateAsync(TestEngine.Id("restart"));
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.StartAsync();
        await engine.WaitForQueryAsync("q");
        await engine.StopAsync();
        Assert.False(await engine.IsRunningAsync());
        Assert.True(IsStopped(await TestEngine.WaitUntilAsync(
            () => engine.GetSourceStatusAsync("orders"),
            IsStopped)));

        await engine.StartAsync();
        await engine.WaitForQueryAsync("q");
        await engine.PushChangeAsync("orders", TestEngine.Order("o1"));
        Assert.Equal("o1", Assert.Single(await TestEngine.WaitForRowsAsync(engine, "q"))["id"]?.ToString());
    }

    [Fact]
    public async Task QueryAddedBeforeStartRunsOnce()
    {
        await using var engine = await Engine.CreateAsync(TestEngine.Id("add-then-start"));
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.StartAsync();
        await engine.WaitForQueryAsync("q");
        Assert.Equal(ComponentStatus.Running, await engine.GetQueryStatusAsync("q"));
        await engine.PushChangeAsync("orders", TestEngine.Order("o1"));
        Assert.Equal("o1", Assert.Single(await TestEngine.WaitForRowsAsync(engine, "q"))["id"]?.ToString());
    }

    [Fact]
    public async Task DisposeIsIdempotentAndRejectsLaterCalls()
    {
        var engine = await Engine.CreateAsync(TestEngine.Id("dispose-twice"));
        await engine.StartAsync();
        await engine.DisposeAsync();
        await engine.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.AddSourceAsync("s"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            engine.PushChangeAsync("s", TestEngine.Order("o1")));
        engine.Dispose();
    }

    [Fact]
    public async Task ShutdownRejectsMutationsButAllowsDispose()
    {
        var engine = await Engine.CreateAsync(TestEngine.Id("shutdown"));
        await engine.StartAsync();
        await engine.AddSourceAsync("orders");
        await engine.ShutdownAsync();
        var ex = await Assert.ThrowsAsync<DrasiException>(() => engine.AddSourceAsync("other"));
        Assert.Equal(DrasiErrorCodes.EngineClosed, ex.Code);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SourceQueryReactionLifecycle()
    {
        await using var engine = await TestEngine.StartedAsync("components");
        await engine.AddSourceAsync("orders", autoStart: false);
        Assert.NotEqual(ComponentStatus.Running, await engine.GetSourceStatusAsync("orders"));
        await engine.StartSourceAsync("orders");
        var source = await TestEngine.WaitUntilAsync(
            () => engine.GetSourceStatusAsync("orders"),
            status => status == ComponentStatus.Running);
        Assert.Equal(ComponentStatus.Running, source);

        await engine.AddQueryAsync(
            "q",
            TestEngine.OrdersQuery,
            ["orders"],
            new QueryOptions { AutoStart = false });
        await engine.StartQueryAsync("q");
        await engine.WaitForQueryAsync("q");

        await engine.AddReactionAsync("watch", ["q"], _ => { }, autoStart: false);
        await engine.StartReactionAsync("watch");
        var reaction = await TestEngine.WaitUntilAsync(
            () => engine.GetReactionStatusAsync("watch"),
            status => status == ComponentStatus.Running);
        Assert.Equal(ComponentStatus.Running, reaction);

        await engine.StopReactionAsync("watch");
        Assert.True(IsStopped(await TestEngine.WaitUntilAsync(
            () => engine.GetReactionStatusAsync("watch"),
            IsStopped)));
        await engine.StopQueryAsync("q");
        Assert.True(IsStopped(await TestEngine.WaitUntilAsync(
            () => engine.GetQueryStatusAsync("q"),
            IsStopped)));
        await engine.StopSourceAsync("orders");
        Assert.True(IsStopped(await TestEngine.WaitUntilAsync(
            () => engine.GetSourceStatusAsync("orders"),
            IsStopped)));
        await engine.RemoveReactionAsync("watch");
        await engine.RemoveQueryAsync("q");
        await engine.RemoveSourceAsync("orders");
        Assert.DoesNotContain(await engine.ListSourcesAsync(), item => item.Id == "orders");
        Assert.DoesNotContain(await engine.ListQueriesAsync(), item => item.Id == "q");
        Assert.DoesNotContain(await engine.ListReactionsAsync(), item => item.Id == "watch");
    }

    [Fact]
    public async Task UnknownComponentIdsAreRejected()
    {
        await using var engine = await TestEngine.StartedAsync("unknown-ids");
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.RemoveQueryAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.RemoveSourceAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.RemoveReactionAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.StartSourceAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.StopSourceAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.StartQueryAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.StopQueryAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.GetQueryResultsAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.GetQueryMetricsAsync("nope"));
        await Assert.ThrowsAnyAsync<DrasiException>(() =>
            engine.UpdateQueryAsync("nope", TestEngine.OrdersQuery, ["s"]));
    }

    [Fact]
    public async Task DuplicateSourceIdIsRejected()
    {
        await using var engine = await TestEngine.StartedAsync("dup");
        await engine.AddSourceAsync("orders");
        await Assert.ThrowsAnyAsync<DrasiException>(() => engine.AddSourceAsync("orders"));
    }

    [Fact]
    public async Task UpdateQueryKeepsTheQueryRunning()
    {
        await using var engine = await TestEngine.StartedAsync("update-q");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.UpdateQueryAsync(
            "q",
            "MATCH (o:Order) RETURN o.id AS id, o.status AS status",
            ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.PushChangeAsync("orders", TestEngine.Order("o1", "open"));
        var row = Assert.Single(await TestEngine.WaitForRowsAsync(engine, "q"));
        Assert.Equal("o1", row["id"]?.ToString());
    }

    private static bool IsStopped(ComponentStatus status) =>
        status is ComponentStatus.Stopped or ComponentStatus.Added;
}
