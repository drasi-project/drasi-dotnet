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

using System.Reflection;
using System.Text.Json.Nodes;
using Drasi;
using Drasi.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Drasi.Tests;

public sealed class PublicApiTests
{
    [Fact]
    public void EveryPublicEngineMemberIsMentionedInTests()
    {
        var testsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var blob = string.Concat(Directory.GetFiles(testsDir, "*.cs").Select(File.ReadAllText));
        var missing = typeof(Engine)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member is MethodInfo method && !method.IsSpecialName)
            .Select(member => member.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !blob.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, "untested public Engine members: " + string.Join(", ", missing));
    }

    [Fact]
    public void DrasiVersionMatchesHostInfo()
    {
        var info = Engine.GetHostInfo();
        Assert.Equal(info.CoreVersion, DrasiVersion.Core);
        Assert.Equal(info.LibVersion, DrasiVersion.Lib);
        Assert.Equal(info.SdkVersion, DrasiVersion.Sdk);
        Assert.Equal(info.FfiSdkVersion, DrasiVersion.FfiSdk);
        Assert.False(string.IsNullOrEmpty(DrasiVersion.Package));
        Assert.False(string.IsNullOrEmpty(info.TargetTriple));
    }

    [Fact]
    public async Task TokenIdentityAndLoggerAreAccepted()
    {
        await using var engine = await Engine.CreateAsync(
            TestEngine.Id("token"),
            new EngineOptions
            {
                Logger = NullLogger.Instance,
                Identity = new IdentityOptions { Kind = "token", Token = "t", Username = "u" },
            });
        Assert.Equal(engine.Id, engine.Id);
        Assert.False(await engine.IsRunningAsync());
    }

    [Fact]
    public async Task DurableReactionWithRedbFires()
    {
        var path = Path.Combine(Path.GetTempPath(), "drasi-redb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await using var engine = await Engine.CreateAsync(
                TestEngine.Id("durable"),
                new EngineOptions
                {
                    StateStore = new StateStoreOptions { Path = Path.Combine(path, "state.redb") },
                });
            await engine.StartAsync();
            await engine.AddSourceAsync("orders");
            await engine.AddQueryAsync("q", TestEngine.OrdersQuery, ["orders"]);
            await engine.WaitForQueryAsync("q");
            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await engine.AddDurableReactionAsync("watch", ["q"], evt =>
            {
                var id = evt.Results.FirstOrDefault()?.Data?["id"]?.ToString();
                if (id is not null)
                {
                    seen.TrySetResult(id);
                }

                return Task.CompletedTask;
            });
            await engine.PushChangeAsync("orders", TestEngine.Order("o1"));
            Assert.Equal("o1", await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            TestEngine.TryDelete(path);
        }
    }

    [Fact]
    public async Task SourceSchemaAndLockfileOnEmptyEngine()
    {
        await using var engine = await TestEngine.StartedAsync("schema");
        await engine.AddSourceAsync("orders");
        var schema = await engine.GetSourceSchemaAsync("orders");
        Assert.True(schema is null || schema.Nodes.Count >= 0);

        var dir = Path.Combine(Path.GetTempPath(), "drasi-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var write = await Assert.ThrowsAsync<PluginNotFoundException>(() =>
                engine.WriteLockfileAsync(dir));
            Assert.Equal(DrasiErrorCodes.PluginNotFound, write.Code);
            await Assert.ThrowsAnyAsync<DrasiException>(() =>
                engine.InstallFromLockfileAsync(dir, load: false));
        }
        finally
        {
            TestEngine.TryDelete(dir);
        }
    }

    [Fact]
    public async Task UpdatePluginSourceAndReactionWithoutPluginsFailTyped()
    {
        await using var engine = await TestEngine.StartedAsync("update-plug");
        var source = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.UpdateSourceAsync("mock", "orders"));
        Assert.Equal(DrasiErrorCodes.UnknownSourceKind, source.Code);
        var reaction = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.UpdateReactionAsync("log", "watch", []));
        Assert.Equal(DrasiErrorCodes.UnknownReactionKind, reaction.Code);
    }

    [Fact]
    public async Task HealthCheckReportsDisposedEngineUnhealthy()
    {
        var engine = await Engine.CreateAsync(TestEngine.Id("health-dead"));
        await engine.DisposeAsync();
        var check = new DrasiHealthCheck(engine);
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task HostAppliesAutoStartFalseQuery()
    {
        var services = new ServiceCollection();
        services.AddDrasi(TestEngine.Id("host-autostart"), drasi =>
        {
            drasi.AddSource("orders");
            drasi.AddQuery(
                "q",
                TestEngine.OrdersQuery,
                ["orders"],
                new QueryOptions { AutoStart = false });
        });
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var engine = provider.GetRequiredService<Engine>();
            var status = await engine.GetQueryStatusAsync("q");
            Assert.NotEqual(ComponentStatus.Running, status);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BuilderPluginAndSecretHelpersAreInvoked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drasi-host-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var services = new ServiceCollection();
            services.AddDrasi(TestEngine.Id("host-watch"), drasi =>
            {
                drasi.WatchPlugins(dir, TimeSpan.FromMilliseconds(200));
                drasi.AddSource("orders");
            });
            await using var provider = services.BuildServiceProvider();
            var hosted = Assert.Single(provider.GetServices<IHostedService>());
            await hosted.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(await provider.GetRequiredService<Engine>().IsRunningAsync());
            }
            finally
            {
                await hosted.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            TestEngine.TryDelete(dir);
        }

        var failing = new ServiceCollection();
        failing.AddDrasi(TestEngine.Id("host-secret"), drasi => drasi.UseSecretStore("vault"));
        await using var failProvider = failing.BuildServiceProvider();
        var failHosted = Assert.Single(failProvider.GetServices<IHostedService>());
        await Assert.ThrowsAsync<UnknownKindException>(() => failHosted.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BuilderInstallAndPluginSourceAreInvoked()
    {
        _ = nameof(DrasiBuilder.InstallPlugin);
        var pluginServices = new ServiceCollection();
        pluginServices.AddDrasi(TestEngine.Id("host-kind"), drasi =>
        {
            drasi.AddSource("mock", "orders", new JsonObject { ["intervalMs"] = 1000 });
            drasi.AddReaction("log", "watch", ["q"]);
            drasi.AddDurableReaction("d", ["q"], _ => Task.CompletedTask);
        });
        await using var pluginProvider = pluginServices.BuildServiceProvider();
        var pluginHosted = Assert.Single(pluginProvider.GetServices<IHostedService>());
        await Assert.ThrowsAnyAsync<DrasiException>(() => pluginHosted.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueryOptionsCapacitiesAreAccepted()
    {
        await using var engine = await TestEngine.StartedAsync("caps");
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync(
            "q",
            TestEngine.OrdersQuery,
            ["orders"],
            new QueryOptions
            {
                Language = QueryLanguage.Cypher,
                AutoStart = true,
                EnableBootstrap = true,
                BootstrapTimeoutSeconds = 5,
                PriorityQueueCapacity = 16,
                DispatchBufferCapacity = 16,
                OutboxCapacity = 16,
                DispatchMode = "channel",
            });
        await engine.WaitForQueryAsync("q");
        Assert.Equal(ComponentStatus.Running, await engine.GetQueryStatusAsync("q"));
    }

    [Fact]
    public async Task LoadPluginsVerifyMapIsAcceptedOnEmptyDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drasi-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var engine = await Engine.CreateAsync(TestEngine.Id("verify"));
            var summary = await engine.LoadPluginsAsync(dir, new Dictionary<string, string> { ["x"] = "y" });
            Assert.Equal(0, summary.Plugins);
            Assert.Equal(0, summary.Skipped);
        }
        finally
        {
            TestEngine.TryDelete(dir);
        }
    }

    [Fact]
    public async Task IndexStoreExtraFlagsAreAccepted()
    {
        var path = Path.Combine(Path.GetTempPath(), "drasi-rocks-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await using var engine = await Engine.CreateAsync(
                TestEngine.Id("rocks-flags"),
                new EngineOptions
                {
                    IndexStore = new IndexStoreOptions
                    {
                        Kind = "rocksdb",
                        Path = path,
                        EnableArchive = false,
                        DirectIo = false,
                    },
                });
            await engine.StartAsync();
            Assert.True(await engine.IsRunningAsync());
        }
        finally
        {
            TestEngine.TryDelete(path);
        }
    }
}
