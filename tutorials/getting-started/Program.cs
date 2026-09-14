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

using System.Net.Sockets;
using System.Text.Json.Nodes;
using Drasi;
using Drasi.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var pgHost = Env("POSTGRES_HOST", "localhost");
var pgPort = EnvInt("POSTGRES_PORT", 5832);
var pgDatabase = Env("POSTGRES_DATABASE", "getting_started");
var pgUser = Env("POSTGRES_USER", "drasi_user");
var pgPassword = Env("POSTGRES_PASSWORD", "drasi_password");
var httpPort = EnvInt("HTTP_SOURCE_PORT", 9100);
var pgContainer = Env("POSTGRES_CONTAINER", "getting-started-dotnet-postgres");
var locationsFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "locations.jsonl"));
var pluginsDir = Path.Combine(Path.GetTempPath(), "drasi-dotnet-getting-started-plugins");
Directory.CreateDirectory(pluginsDir);

Console.WriteLine("Starting Getting Started…");
Console.WriteLine("  • creating the engine, downloading plugins, connecting to PostgreSQL + the HTTP source…");

await WaitForPortAsync(pgHost, pgPort, "PostgreSQL");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddDrasi("getting-started", drasi =>
{
    drasi.InstallPlugin("source/postgres", pluginsDir);
    drasi.InstallPlugin("bootstrap/postgres", pluginsDir);
    drasi.InstallPlugin("source/http", pluginsDir);
    drasi.InstallPlugin("bootstrap/scriptfile", pluginsDir);

    var pg = PostgresConfig(pgHost, pgPort, pgDatabase, pgUser, pgPassword);
    drasi.AddSource("postgres", "messages", pg, bootstrap: Bootstrap("postgres", pg));
    drasi.AddSource(
        "http",
        "location-tracker",
        HttpSourceConfig(httpPort),
        bootstrap: Bootstrap("scriptfile", new JsonObject
        {
            ["filePaths"] = new JsonArray { locationsFile },
        }));

    drasi.AddQuery("all-messages", """
        MATCH (m:Message)
        RETURN m.MessageId AS MessageId, m.From AS From, m.Message AS Message
        """, ["messages"]);

    drasi.AddQuery("hello-world-senders", """
        MATCH (m:Message)
        WHERE m.Message = 'Hello World'
        RETURN m.MessageId AS Id, m.From AS Sender
        """, ["messages"]);

    drasi.AddQuery("message-counts", """
        MATCH (m:Message)
        RETURN m.Message AS MessageText, count(m) AS Count
        """, ["messages"]);

    drasi.AddQuery("inactive-senders", """
        MATCH (m:Message)
        WITH m.From AS MessageFrom, max(drasi.changeDateTime(m)) AS LastMessageTimestamp
        WHERE LastMessageTimestamp <= datetime.realtime() - duration({ seconds: 20 })
           OR drasi.trueLater(
                LastMessageTimestamp <= datetime.realtime() - duration({ seconds: 20 }),
                LastMessageTimestamp + duration({ seconds: 20 }))
        RETURN MessageFrom, LastMessageTimestamp
        """, ["messages"]);

    drasi.AddQuery("messages-with-location", """
        MATCH (m:Message)-[:FROM_USER]->(u:UserLocation)
        RETURN m.MessageId AS Id, m.Message AS Message,
               m.From AS Sender, u.location AS Location, u.status AS Status
        """, ["messages", "location-tracker"], new QueryOptions
    {
        Joins =
        [
            new QueryJoin
            {
                Id = "FROM_USER",
                Keys =
                [
                    new QueryJoinKey { Label = "Message", Property = "From" },
                    new QueryJoinKey { Label = "UserLocation", Property = "name" },
                ],
            },
        ],
    });

    drasi.AddReaction("console", [
        "all-messages",
        "hello-world-senders",
        "message-counts",
        "inactive-senders",
        "messages-with-location",
    ], PrintChange);
});

var host = builder.Build();
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("✅ Getting Started is ready — Drasi is watching for changes.");
    Console.WriteLine();
    Console.WriteLine("   Drive changes from a second terminal and watch them print here. For example,");
    Console.WriteLine("   insert a message (the tutorial walks through the rest):");
    Console.WriteLine($"     docker exec {pgContainer} psql -U {pgUser} -d {pgDatabase} \\");
    Console.WriteLine($"       -c \"INSERT INTO \\\"Message\\\" (\\\"From\\\", \\\"Message\\\") VALUES ('You', 'Hello');\"");
    Console.WriteLine();
    Console.WriteLine("   Press Ctrl+C to stop.");
    Console.WriteLine();
});

await host.RunAsync();

static void PrintChange(QueryResultEvent evt)
{
    var diffs = evt.Results.Where(diff => diff.Type != DiffType.Noop).ToList();
    if (diffs.Count == 0)
    {
        return;
    }

    Console.WriteLine($"[drasi] Query '{evt.QueryId}' ({diffs.Count} change{(diffs.Count == 1 ? "" : "s")}):");
    foreach (var diff in diffs)
    {
        switch (diff.Type)
        {
            case DiffType.Add:
                Console.WriteLine($"  [ADD]    {Json(diff.Data)}");
                break;
            case DiffType.Delete:
                Console.WriteLine($"  [DELETE] {Json(diff.Data)}");
                break;
            case DiffType.Update:
            case DiffType.Aggregation:
                Console.WriteLine($"  [UPDATE] {Json(diff.Before)} -> {Json(diff.After)}");
                break;
            default:
                Console.WriteLine($"  [{diff.Type}] {Json(diff.Data ?? diff.After ?? diff.Before)}");
                break;
        }
    }
}

static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

static JsonObject PostgresConfig(string host, int port, string database, string user, string password) => new()
{
    ["host"] = host,
    ["port"] = port,
    ["database"] = database,
    ["user"] = user,
    ["password"] = password,
    ["sslMode"] = "prefer",
    ["tables"] = new JsonArray { "Message" },
    ["slotName"] = "drasi_getting_started_slot",
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

static JsonObject HttpSourceConfig(int port) => new()
{
    ["host"] = "0.0.0.0",
    ["port"] = port,
    ["webhooks"] = new JsonObject
    {
        ["routes"] = new JsonArray
        {
            new JsonObject
            {
                ["path"] = "/locations",
                ["methods"] = new JsonArray { "POST" },
                ["mappings"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["operation"] = "update",
                        ["elementType"] = "node",
                        ["template"] = new JsonObject
                        {
                            ["id"] = "{{payload.name}}",
                            ["labels"] = new JsonArray { "UserLocation" },
                            ["properties"] = new JsonObject
                            {
                                ["name"] = "{{payload.name}}",
                                ["location"] = "{{payload.location}}",
                                ["status"] = "{{payload.status}}",
                            },
                        },
                    },
                },
            },
        },
    },
};

static JsonObject Bootstrap(string kind, JsonObject config)
{
    var node = (JsonObject)config.DeepClone();
    node["kind"] = kind;
    return node;
}

static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;

static int EnvInt(string key, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;

static async Task WaitForPortAsync(string host, int port, string label, int attempts = 60)
{
    for (var i = 0; i < attempts; i++)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(host, port, cts.Token);
            return;
        }
        catch
        {
            await Task.Delay(1000);
        }
    }

    throw new InvalidOperationException(
        $"{label} is not reachable at {host}:{port}. Start it with './scripts/setup-database.sh'.");
}
