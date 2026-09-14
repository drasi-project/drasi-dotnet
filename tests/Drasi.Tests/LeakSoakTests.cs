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

using System.Diagnostics;
using Drasi;
using Xunit;

namespace Drasi.Tests;

public sealed class LeakSoakTests
{
    [Fact]
    public async Task RepeatedAddRemoveDoesNotLeakComponents()
    {
        await using var engine = await TestEngine.StartedAsync("churn");
        for (var i = 0; i < 15; i++)
        {
            await engine.AddSourceAsync($"s{i}");
            await engine.AddQueryAsync($"q{i}", TestEngine.OrdersQuery, [$"s{i}"]);
            await engine.AddReactionAsync($"r{i}", [$"q{i}"], _ => { });
            await engine.RemoveReactionAsync($"r{i}");
            await engine.RemoveQueryAsync($"q{i}");
            await engine.RemoveSourceAsync($"s{i}");
        }

        Assert.DoesNotContain(await engine.ListSourcesAsync(), item => item.Id.StartsWith('s') && item.Id.Length < 8);
        Assert.DoesNotContain(await engine.ListQueriesAsync(), item => item.Id.StartsWith('q') && item.Id.Length < 8);
        Assert.DoesNotContain(await engine.ListReactionsAsync(), item => item.Id.StartsWith('r') && item.Id.Length < 8);

        await engine.AddSourceAsync("final");
        await engine.AddQueryAsync("finalq", TestEngine.OrdersQuery, ["final"]);
        await engine.WaitForQueryAsync("finalq");
        await engine.PushChangeAsync("final", TestEngine.Order("o1"));
        Assert.Equal("o1", Assert.Single(await TestEngine.WaitForRowsAsync(engine, "finalq"))["id"]?.ToString());
    }

    [Fact]
    public async Task CreateDisposeCyclesStayBounded()
    {
        Collect();
        var before = Process.GetCurrentProcess().WorkingSet64;
        const int cycles = 10;
        for (var i = 0; i < cycles; i++)
        {
            await RunCycleAsync($"rss-{i}");
        }

        Collect();
        var grewMb = (Process.GetCurrentProcess().WorkingSet64 - before) / (1024.0 * 1024.0);
        Assert.True(grewMb < 400, $"working set grew {grewMb:F1}MB across {cycles} create/dispose cycles (limit 400MB)");
    }

    [Fact]
    public async Task LongRunningStreamThenDisposeIsClean()
    {
        var engine = await TestEngine.StartedAsync("long-stream");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
        await engine.WaitForQueryAsync("q");

        var seen = 0;
        var consume = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in engine.QueryResultsAsync("q"))
                {
                    if (evt.Results.Count > 0)
                    {
                        Interlocked.Increment(ref seen);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
        });

        for (var i = 0; i < 5; i++)
        {
            await engine.PushChangeAsync("orders", TestEngine.Order($"o{i}"));
            await Task.Delay(50);
        }

        await engine.DisposeAsync();
        await consume.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(seen >= 1);
    }

    [Fact]
    public async Task WatchPluginsOnEmptyDirectoryTearsDownCleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drasi-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var engine = await Engine.CreateAsync(TestEngine.Id("watch"));
            await engine.WatchPluginsAsync(dir, TimeSpan.FromMilliseconds(100));
            await engine.DisposeAsync();
            await engine.DisposeAsync();
        }
        finally
        {
            TestEngine.TryDelete(dir);
        }
    }

    [SoakFact]
    public async Task SoakCreateDisposeCyclesStayBounded()
    {
        Collect();
        var before = Process.GetCurrentProcess().WorkingSet64;
        const int cycles = 80;
        for (var i = 0; i < cycles; i++)
        {
            await RunCycleAsync($"soak-{i}");
        }

        Collect();
        var grewMb = (Process.GetCurrentProcess().WorkingSet64 - before) / (1024.0 * 1024.0);
        Assert.True(grewMb < 500, $"working set grew {grewMb:F1}MB across {cycles} create/dispose cycles (limit 500MB)");
    }

    private static async Task RunCycleAsync(string prefix)
    {
        await using var engine = await Engine.CreateAsync(TestEngine.Id(prefix));
        await engine.StartAsync();
        await engine.AddSourceAsync("s");
        await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["s"]);
        await engine.WaitForQueryAsync("q");
        await engine.PushChangeAsync("s", TestEngine.Order("n1"));
        await TestEngine.WaitForRowsAsync(engine, "q", TimeSpan.FromSeconds(5));
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
