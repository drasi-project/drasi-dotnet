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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Drasi.Tests;

public sealed class HostingTests
{
    private const string OrdersQuery = "MATCH (o:Order) RETURN o.id AS id";

    [Fact]
    public async Task HostAppliesStartupTopology()
    {
        var services = new ServiceCollection();
        services.AddDrasi("host-topo", drasi =>
        {
            drasi.AddSource("orders");
            drasi.AddQuery("open", OrdersQuery, ["orders"]);
            drasi.Seed("orders", Order("o1"));
        });

        await using var provider = services.BuildServiceProvider();
        var hosted = SingleHosted(provider);
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var engine = provider.GetRequiredService<Engine>();
            Assert.True(await engine.IsRunningAsync());
            var rows = await WaitForRowsAsync(engine, "open");
            Assert.Equal("o1", Assert.Single(rows)["id"]?.ToString());
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HostWiresStartupReaction()
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddDrasi("host-rx", drasi =>
        {
            drasi.AddSource("orders");
            drasi.AddQuery("open", OrdersQuery, ["orders"]);
            drasi.AddReaction("watch", ["open"], evt =>
            {
                var id = evt.Results.FirstOrDefault()?.Data?["id"]?.ToString();
                if (id is not null)
                {
                    seen.TrySetResult(id);
                }
            });
            drasi.Seed("orders", Order("o2"));
        });

        await using var provider = services.BuildServiceProvider();
        var hosted = SingleHosted(provider);
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var id = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("o2", id);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void AddDrasiCannotBeCalledTwice()
    {
        var services = new ServiceCollection();
        services.AddDrasi("once");
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddDrasi("twice"));
        Assert.Contains("already been called", ex.Message);
    }

    [Fact]
    public async Task ConfigureCallbackCanUseServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton("from-di");
        services.AddDrasi("host-sp", (drasi, sp) =>
        {
            Assert.Equal("from-di", sp.GetRequiredService<string>());
            drasi.AddSource("orders");
        });

        await using var provider = services.BuildServiceProvider();
        var hosted = SingleHosted(provider);
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var engine = provider.GetRequiredService<Engine>();
            Assert.Contains(await engine.ListSourcesAsync(), s => s.Id == "orders");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AddDrasiFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["id"] = "host-cfg",
                ["sources:0:id"] = "orders",
                ["queries:0:id"] = "open",
                ["queries:0:query"] = OrdersQuery,
                ["queries:0:sources:0"] = "orders",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDrasi(config);
        await using var provider = services.BuildServiceProvider();
        var hosted = SingleHosted(provider);
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var engine = provider.GetRequiredService<Engine>();
            Assert.Equal("host-cfg", engine.Id);
            await engine.PushChangeAsync("orders", Order("o1"));
            var rows = await WaitForRowsAsync(engine, "open");
            Assert.Equal("o1", Assert.Single(rows)["id"]?.ToString());
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BuilderLoadsEmptyPluginDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drasi-host-plug-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var services = new ServiceCollection();
            services.AddDrasi("host-plug", drasi => drasi.LoadPlugins(dir));
            await using var provider = services.BuildServiceProvider();
            var hosted = SingleHosted(provider);
            await hosted.StartAsync(CancellationToken.None);
            try
            {
                var engine = provider.GetRequiredService<Engine>();
                Assert.True(await engine.IsRunningAsync());
                var kinds = await engine.PluginKindsAsync();
                Assert.Empty(kinds.Sources);
            }
            finally
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddDrasiCheckIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddDrasi("host-health");
        services.AddHealthChecks().AddDrasiCheck();
        await using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Contains(registrations, r => r.Name == "drasi");

        var engine = provider.GetRequiredService<Engine>();
        await engine.StartAsync();
        try
        {
            var check = new DrasiHealthCheck(engine);
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            await engine.ShutdownAsync();
        }
    }

    private static IHostedService SingleHosted(IServiceProvider provider)
        => Assert.Single(provider.GetServices<IHostedService>());

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
}
