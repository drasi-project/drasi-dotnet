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
using System.Text.Json.Nodes;
using Drasi;
using Xunit;

namespace Drasi.Tests;

public sealed class PostgresPluginTests
{
    [PostgresPluginFact]
    public async Task PostgresPluginsBootstrapAndStreamChanges()
    {
        var pluginsDir = Path.Combine(Path.GetTempPath(), "drasi-postgres-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginsDir);
        try
        {
            await using (var engine = await Engine.CreateAsync(TestEngine.Id("postgres-plugin")))
            {
                var source = await engine.InstallPluginAsync("source/postgres", pluginsDir);
                var bootstrap = await engine.InstallPluginAsync("bootstrap/postgres", pluginsDir);
                Assert.Equal("postgres", source.Kind);
                Assert.Equal("postgres", bootstrap.Kind);

                Assert.Equal(2, await engine.WriteLockfileAsync(pluginsDir));
                var locked = Engine.ReadLockfile(pluginsDir);
                Assert.Contains(locked, plugin => plugin.Reference.Contains("source/postgres", StringComparison.Ordinal));
                Assert.Contains(locked, plugin => plugin.Reference.Contains("bootstrap/postgres", StringComparison.Ordinal));

                await engine.StartAsync();
                var config = PostgresConfig();
                await engine.AddSourceAsync("postgres", "messages", config, bootstrap: BootstrapConfig(config));
                await TestEngine.WaitUntilAsync<IReadOnlyList<ComponentInfo>>(
                    () => engine.ListSourcesAsync(),
                    sources => sources.Any(source => source.Id == "messages" && source.Status == ComponentStatus.Running),
                    TimeSpan.FromSeconds(30));

                await engine.AddQueryAsync(
                    "new-messages",
                    """
                    MATCH (m:Message)
                    WHERE m.Message = 'plugin ABI check'
                    RETURN m.MessageId AS id, m.Message AS message
                    """,
                    ["messages"]);
                await engine.WaitForQueryAsync("new-messages", TimeSpan.FromSeconds(30));

                var received = new TaskCompletionSource<QueryResultEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                await engine.AddReactionAsync("watch", ["new-messages"], evt =>
                {
                    if (evt.Results.Any(diff =>
                            diff.Type == DiffType.Add &&
                            diff.Data?["message"]?.ToString() == "plugin ABI check"))
                    {
                        received.TrySetResult(evt);
                    }

                });

                await InsertMessageAsync();
                var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal("new-messages", result.QueryId);
            }

            await using var reloaded = await Engine.CreateAsync(TestEngine.Id("postgres-lockfile"));
            var installed = await reloaded.InstallFromLockfileAsync(pluginsDir);
            Assert.Contains(installed, reference => reference.Contains("source/postgres", StringComparison.Ordinal));
            Assert.Contains(installed, reference => reference.Contains("bootstrap/postgres", StringComparison.Ordinal));
        }
        finally
        {
            TestEngine.TryDelete(pluginsDir);
        }
    }

    private static JsonObject PostgresConfig() => new()
    {
        ["host"] = Environment.GetEnvironmentVariable("DRASI_POSTGRES_HOST") ?? "localhost",
        ["port"] = int.TryParse(Environment.GetEnvironmentVariable("DRASI_POSTGRES_PORT"), out var port) ? port : 5832,
        ["database"] = Environment.GetEnvironmentVariable("DRASI_POSTGRES_DATABASE") ?? "getting_started",
        ["user"] = Environment.GetEnvironmentVariable("DRASI_POSTGRES_USER") ?? "drasi_user",
        ["password"] = Environment.GetEnvironmentVariable("DRASI_POSTGRES_PASSWORD") ?? "drasi_password",
        ["sslMode"] = "prefer",
        ["tables"] = new JsonArray { "Message" },
        ["slotName"] = "drasi_dotnet_plugin_test_" + Guid.NewGuid().ToString("N"),
        ["publicationName"] = "drasi_getting_started_pub",
        ["tableKeys"] = new JsonArray
        {
            new JsonObject
            {
                ["table"] = "Message",
                ["keyColumns"] = new JsonArray { "MessageId" },
            },
        },
    };

    private static JsonObject BootstrapConfig(JsonObject source)
    {
        var bootstrap = (JsonObject)source.DeepClone();
        bootstrap["kind"] = "postgres";
        return bootstrap;
    }

    private static async Task InsertMessageAsync()
    {
        var container = Environment.GetEnvironmentVariable("DRASI_POSTGRES_CONTAINER")
            ?? "getting-started-dotnet-postgres";
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(container);
        startInfo.ArgumentList.Add("psql");
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add("drasi_user");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("getting_started");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            """INSERT INTO "Message" ("From", "Message") VALUES ('Drasi .NET', 'plugin ABI check');""");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start docker");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"PostgreSQL insert failed with exit code {process.ExitCode}: {error}\n{output}");
        }
    }
}
