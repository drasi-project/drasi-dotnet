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
using Microsoft.Extensions.Logging;

// Needs network access to ghcr.io. `source/mock` ticks a counter; `reaction/log`
// writes query diffs through ILogger (Information).

using var logs = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("Drasi", LogLevel.Information)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

var info = Engine.GetHostInfo();
Console.WriteLine($"host: {info.TargetTriple}");
Console.WriteLine($"  drasi-core {info.CoreVersion}, drasi-lib {info.LibVersion}");
Console.WriteLine($"  plugin sdk {info.SdkVersion}, ffi abi {info.FfiSdkVersion}");
Console.WriteLine();

var pluginsDir = ExamplePluginsDirectory();
Directory.CreateDirectory(pluginsDir);
Console.WriteLine($"plugins dir: {pluginsDir}");
Console.WriteLine();

await using var drasi = await Engine.CreateAsync("plugin-demo", new EngineOptions
{
    LoggerFactory = logs,
});

var available = await Engine.SearchPluginsAsync();
var sources = available
    .Where(item => item.PluginType == "source")
    .Select(item => item.Kind)
    .OrderBy(kind => kind, StringComparer.Ordinal)
    .ToArray();
Console.WriteLine($"{available.Count} plugins published; sources include:");
Console.WriteLine("  " + string.Join(", ", sources.Take(12)) + (sources.Length > 12 ? " ..." : ""));
Console.WriteLine();

var resolved = await Engine.ResolvePluginAsync("source/mock");
Console.WriteLine(
    $"source/mock resolves to {resolved.Version} for {resolved.TargetTriple}");

var sourcePlugin = await drasi.InstallPluginAsync("source/mock", directory: pluginsDir, verify: true);
Console.WriteLine($"installed source/mock to {sourcePlugin.Path}");
Console.WriteLine($"signature: {sourcePlugin.Verification}");

var reactionPlugin = await drasi.InstallPluginAsync("reaction/log", directory: pluginsDir, verify: true);
Console.WriteLine($"installed reaction/log to {reactionPlugin.Path}");
Console.WriteLine($"signature: {reactionPlugin.Verification}");
Console.WriteLine();

var kinds = await drasi.PluginKindsAsync();
Console.WriteLine($"loaded sources: {string.Join(", ", kinds.Sources)}");
Console.WriteLine($"loaded reactions: {string.Join(", ", kinds.Reactions)}");

await drasi.StartAsync();
await drasi.AddSourceAsync(
    "mock",
    "counters",
    new JsonObject
    {
        ["dataType"] = new JsonObject { ["type"] = "counter" },
        ["intervalMs"] = 200,
    });
await drasi.AddQueryAsync(
    "counts",
    "MATCH (c:Counter) RETURN c.value AS value",
    ["counters"]);
await drasi.WaitForQueryAsync("counts");
await drasi.AddReactionAsync("log", "printer", ["counts"]);

IReadOnlyList<JsonObject> rows = [];
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
while (DateTime.UtcNow < deadline)
{
    rows = await drasi.GetQueryResultsAsync("counts");
    if (rows.Count > 0)
    {
        break;
    }

    await Task.Delay(100);
}

if (rows.Count == 0)
{
    Console.WriteLine("no rows arrived");
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"counter rows: [{string.Join(", ", rows.Take(5).Select(row => row.ToJsonString()))}]");

static string ExamplePluginsDirectory()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Plugins.csproj")))
        {
            return Path.Combine(dir.FullName, "plugins");
        }

        dir = dir.Parent;
    }

    return Path.Combine(AppContext.BaseDirectory, "plugins");
}
