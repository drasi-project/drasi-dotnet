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

public sealed class CancellationTests
{
    [Fact]
    public async Task AlreadyCancelledTokenRejectsBeforeNativeWork()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine.CreateAsync(TestEngine.Id("cancel"), cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine.SearchPluginsAsync("mock", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine.ListPluginTagsAsync("ghcr.io/drasi-project/source/mock", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine.ResolvePluginAsync("ghcr.io/drasi-project/source/mock:0.2.7", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Engine.PullPluginAsync("ghcr.io/x", "/tmp", "p.dylib", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task EngineMethodsHonourCancelledTokens()
    {
        await using var engine = await TestEngine.StartedAsync("cancel-eng");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StopAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.IsRunningAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.AddSourceAsync("s", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ListSourcesAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.PluginKindsAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.InstallPluginAsync("ghcr.io/x", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.WriteLockfileAsync("/tmp", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.InstallFromLockfileAsync("/tmp", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.WatchPluginsAsync("/tmp", cancellationToken: cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.GetGraphSchemaAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.GetLifecycleMetricsAsync(cts.Token));
    }

    [Fact]
    public async Task QueryResultsStreamHonoursCancellation()
    {
        await using var engine = await TestEngine.StartedAsync("cancel-stream");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in engine.QueryResultsAsync("q", "stream-cancel", cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task WaitForQueryHonoursCancellation()
    {
        await using var engine = await TestEngine.StartedAsync("cancel-wait");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync(
            "q",
            TestEngine.OrdersQuery,
            ["orders"],
            new QueryOptions { AutoStart = false });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.WaitForQueryAsync("q", cancellationToken: cts.Token));
    }
}
