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

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Drasi;
using Microsoft.Extensions.Logging;
using Npgsql;

const string Comfort =
    "floor( 50 + (r.temperature - 72) + (r.humidity - 42) + CASE WHEN r.co2 > 500 THEN (r.co2 - 500) / 25 ELSE 0 END )";

var comfortable = new RoomReadings(70, 40, 10);
var broken = new RoomReadings(40, 20, 700);

var pgHost = Env("POSTGRES_HOST", "localhost");
var pgPort = EnvInt("POSTGRES_PORT", 5833);
var pgDatabase = Env("POSTGRES_DATABASE", "building_comfort");
var pgUser = Env("POSTGRES_USER", "drasi_user");
var pgPassword = Env("POSTGRES_PASSWORD", "drasi_password");
var webPort = EnvInt("WEB_PORT", 3000);
var pluginsDir = Path.Combine(Path.GetTempPath(), "drasi-dotnet-building-comfort-plugins");
Directory.CreateDirectory(pluginsDir);

var roomShape = Shape(("id", "RoomId"), ("name", "RoomName"), ("floorId", "FloorId"),
    ("floor", "FloorName"), ("buildingName", "BuildingName"), ("comfort", "ComfortLevel"),
    ("temperature", "Temperature"), ("humidity", "Humidity"), ("co2", "CO2"));
var floorComfortShape = Shape(("id", "FloorId"), ("comfort", "ComfortLevel"));
var buildingShape = Shape(("id", "BuildingId"), ("comfort", "ComfortLevel"));
var roomAlertShape = Shape(("id", "RoomId"), ("name", "RoomName"), ("comfort", "ComfortLevel"));
var floorAlertShape = Shape(("id", "FloorId"), ("name", "FloorName"), ("comfort", "ComfortLevel"));

var streams = new Dictionary<string, (string Path, Dictionary<string, string> Shape)>(StringComparer.Ordinal)
{
    ["building-comfort-ui"] = ("rooms", roomShape),
    ["floor-comfort-level-calc"] = ("floor-comfort", floorComfortShape),
    ["building-comfort-level-calc"] = ("building", buildingShape),
    ["room-alert"] = ("room-alerts", roomAlertShape),
    ["floor-alert"] = ("floor-alerts", floorAlertShape),
};

Console.WriteLine("Starting Building Comfort…");
Console.WriteLine("  • creating engine, downloading plugins, connecting to PostgreSQL…");

await WaitForPortAsync(pgHost, pgPort, "PostgreSQL");

using var logs = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole(o => o.SingleLine = true);
});

var hub = new SseHub();
await using var drasi = await Engine.CreateAsync("building-comfort", new EngineOptions { LoggerFactory = logs });
await drasi.InstallPluginAsync("source/postgres", pluginsDir);
await drasi.InstallPluginAsync("bootstrap/postgres", pluginsDir);
await drasi.StartAsync();

var pg = PostgresConfig(pgHost, pgPort, pgDatabase, pgUser, pgPassword);
await drasi.AddSourceAsync("postgres", "building-facilities", pg, bootstrap: Bootstrap("postgres", pg));

var partOfFloor = Join("PART_OF_FLOOR", ("Room", "floor_id"), ("Floor", "id"));
var partOfBuilding = Join("PART_OF_BUILDING", ("Floor", "building_id"), ("Building", "id"));

await drasi.AddQueryAsync("building-comfort-ui", $"""
    MATCH (r:Room)-[:PART_OF_FLOOR]->(f:Floor)-[:PART_OF_BUILDING]->(b:Building)
    WITH r, f, b, {Comfort} AS ComfortLevel
    RETURN
      r.id AS RoomId, r.name AS RoomName,
      f.id AS FloorId, f.name AS FloorName,
      b.id AS BuildingId, b.name AS BuildingName,
      r.temperature AS Temperature, r.humidity AS Humidity, r.co2 AS CO2,
      ComfortLevel
    """, ["building-facilities"], new QueryOptions { Joins = [partOfFloor, partOfBuilding] });

await drasi.AddQueryAsync("floor-comfort-level-calc", $"""
    MATCH (r:Room)-[:PART_OF_FLOOR]->(f:Floor)
    WITH f, {Comfort} AS RoomComfortLevel
    WITH f, avg(RoomComfortLevel) AS ComfortLevel
    RETURN f.id AS FloorId, ComfortLevel
    """, ["building-facilities"], new QueryOptions { Joins = [partOfFloor] });

await drasi.AddQueryAsync("building-comfort-level-calc", $"""
    MATCH (r:Room)-[:PART_OF_FLOOR]->(f:Floor)-[:PART_OF_BUILDING]->(b:Building)
    WITH b, {Comfort} AS RoomComfortLevel
    WITH b, avg(RoomComfortLevel) AS FloorComfortLevel
    WITH b, avg(FloorComfortLevel) AS ComfortLevel
    RETURN b.id AS BuildingId, ComfortLevel
    """, ["building-facilities"], new QueryOptions { Joins = [partOfFloor, partOfBuilding] });

await drasi.AddQueryAsync("room-alert", $"""
    MATCH (r:Room)
    WITH r.id AS RoomId, r.name AS RoomName, {Comfort} AS ComfortLevel
    WHERE ComfortLevel < 40 OR ComfortLevel > 50
    RETURN RoomId, RoomName, ComfortLevel
    """, ["building-facilities"]);

await drasi.AddQueryAsync("floor-alert", $"""
    MATCH (r:Room)-[:PART_OF_FLOOR]->(f:Floor)
    WITH f, {Comfort} AS RoomComfortLevel
    WITH f, avg(RoomComfortLevel) AS ComfortLevel
    WHERE ComfortLevel < 40 OR ComfortLevel > 50
    RETURN f.id AS FloorId, f.name AS FloorName, ComfortLevel
    """, ["building-facilities"], new QueryOptions { Joins = [partOfFloor] });

await drasi.AddQueryAsync("building-alert", $"""
    MATCH (r:Room)-[:PART_OF_FLOOR]->(f:Floor)-[:PART_OF_BUILDING]->(b:Building)
    WITH f, b, {Comfort} AS RoomComfortLevel
    WITH f, b, avg(RoomComfortLevel) AS FloorComfortLevel
    WITH b, avg(FloorComfortLevel) AS ComfortLevel
    WHERE ComfortLevel < 40 OR ComfortLevel > 50
    RETURN b.id AS BuildingId, b.name AS BuildingName, ComfortLevel
    """, ["building-facilities"], new QueryOptions { Joins = [partOfFloor, partOfBuilding] });

foreach (var queryId in streams.Keys)
{
    await drasi.WaitForQueryAsync(queryId);
}

await drasi.AddReactionAsync("watch", [.. streams.Keys], evt =>
{
    if (!streams.TryGetValue(evt.QueryId, out var stream))
    {
        return;
    }

    foreach (var diff in evt.Results)
    {
        if (diff.Type is DiffType.Noop)
        {
            continue;
        }

        var source = diff.Type is DiffType.Delete ? diff.Before ?? diff.Data : diff.After ?? diff.Data;
        if (source is null)
        {
            continue;
        }

        hub.Publish(stream.Path, Op(diff.Type), Reshape(stream.Shape, source));
    }
});

var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = pgHost,
    Port = pgPort,
    Database = pgDatabase,
    Username = pgUser,
    Password = pgPassword,
}.ConnectionString;
await using var db = NpgsqlDataSource.Create(connectionString);
var simulator = new Simulator(db);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");
var app = builder.Build();

app.MapGet("/api/state", async () =>
{
    return Results.Json(new JsonObject
    {
        ["rooms"] = await Snapshot(drasi, "building-comfort-ui", roomShape),
        ["floor-comfort"] = await Snapshot(drasi, "floor-comfort-level-calc", floorComfortShape),
        ["building"] = await Snapshot(drasi, "building-comfort-level-calc", buildingShape),
        ["room-alerts"] = await Snapshot(drasi, "room-alert", roomAlertShape),
        ["floor-alerts"] = await Snapshot(drasi, "floor-alert", floorAlertShape),
    });
});

app.MapGet("/api/rooms", async () =>
{
    var rooms = new JsonArray();
    await using var cmd = db.CreateCommand(
        """SELECT id, name, temperature, humidity, co2, floor_id FROM "Room" ORDER BY id""");
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rooms.Add(new JsonObject
        {
            ["id"] = reader.GetString(0),
            ["name"] = reader.GetString(1),
            ["temperature"] = reader.GetInt32(2),
            ["humidity"] = reader.GetInt32(3),
            ["co2"] = reader.GetInt32(4),
            ["floor_id"] = reader.GetString(5),
        });
    }

    return Results.Json(new JsonObject
    {
        ["rooms"] = rooms,
        ["presets"] = new JsonObject
        {
            ["COMFORTABLE"] = comfortable.ToJson(),
            ["BROKEN"] = broken.ToJson(),
        },
    });
});

app.MapPost("/api/rooms/{id}", async (string id, RoomReadings body) =>
{
    try
    {
        return Results.Json(new JsonObject { ["room"] = await Rooms.Set(db, id, body) });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/rooms/{id}/reset", async (string id) =>
{
    try
    {
        return Results.Json(new JsonObject { ["room"] = await Rooms.Set(db, id, comfortable) });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/reset", async () =>
{
    await using var cmd = db.CreateCommand(
        """UPDATE "Room" SET temperature = $1, humidity = $2, co2 = $3""");
    cmd.Parameters.AddWithValue(comfortable.Temperature);
    cmd.Parameters.AddWithValue(comfortable.Humidity);
    cmd.Parameters.AddWithValue(comfortable.Co2);
    var count = await cmd.ExecuteNonQueryAsync();
    return Results.Json(new { reset = count });
});

app.MapGet("/api/simulate", () => Results.Json(new { running = simulator.IsRunning }));
app.MapPost("/api/simulate", async (SimulateRequest body) =>
{
    if (body.Enabled)
    {
        await simulator.StartAsync();
    }
    else
    {
        simulator.Stop();
    }

    return Results.Json(new { running = simulator.IsRunning });
});

app.MapGet("/events", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await ctx.Response.WriteAsync(": connected\n\n", ct);

    var (id, reader) = hub.Subscribe();
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        _ = PingAsync(ctx, timer, ct);
        await foreach (var frame in reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"data: {frame}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    finally
    {
        hub.Unsubscribe(id);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

Console.WriteLine();
Console.WriteLine($"✅ Building Comfort is ready — open http://localhost:{webPort}");
Console.WriteLine();
await app.RunAsync();

static async Task PingAsync(HttpContext ctx, PeriodicTimer timer, CancellationToken ct)
{
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            await ctx.Response.WriteAsync(": ping\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
}

static async Task<JsonArray> Snapshot(Engine engine, string queryId, Dictionary<string, string> shape)
{
    var rows = new JsonArray();
    foreach (var row in await engine.GetQueryResultsAsync(queryId))
    {
        rows.Add(Reshape(shape, row));
    }

    return rows;
}

static JsonObject Reshape(Dictionary<string, string> shape, JsonObject row)
{
    var output = new JsonObject();
    foreach (var (dest, source) in shape)
    {
        output[dest] = row[source]?.DeepClone();
    }

    return output;
}

static string Op(DiffType type) => type switch
{
    DiffType.Add => "add",
    DiffType.Delete => "delete",
    _ => "update",
};

static QueryJoin Join(string id, (string Label, string Property) left, (string Label, string Property) right) =>
    new()
    {
        Id = id,
        Keys =
        [
            new QueryJoinKey { Label = left.Label, Property = left.Property },
            new QueryJoinKey { Label = right.Label, Property = right.Property },
        ],
    };

static Dictionary<string, string> Shape(params (string Dest, string Source)[] pairs) =>
    pairs.ToDictionary(pair => pair.Dest, pair => pair.Source, StringComparer.Ordinal);

static JsonObject PostgresConfig(string host, int port, string database, string user, string password) => new()
{
    ["host"] = host,
    ["port"] = port,
    ["database"] = database,
    ["user"] = user,
    ["password"] = password,
    ["sslMode"] = "prefer",
    ["tables"] = new JsonArray { "Building", "Floor", "Room" },
    ["slotName"] = "drasi_building_comfort_slot",
    ["publicationName"] = "drasi_building_comfort_pub",
    ["tableKeys"] = new JsonArray
    {
        new JsonObject { ["table"] = "Building", ["keyColumns"] = new JsonArray { "id" } },
        new JsonObject { ["table"] = "Floor", ["keyColumns"] = new JsonArray { "id" } },
        new JsonObject { ["table"] = "Room", ["keyColumns"] = new JsonArray { "id" } },
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

sealed record RoomReadings(int Temperature, int Humidity, int Co2)
{
    public JsonObject ToJson() => new()
    {
        ["temperature"] = Temperature,
        ["humidity"] = Humidity,
        ["co2"] = Co2,
    };
}

sealed record SimulateRequest(bool Enabled);

static class Rooms
{
    public static async Task<JsonObject> Set(NpgsqlDataSource data, string id, RoomReadings readings)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_]+$"))
        {
            throw new ArgumentException($"invalid room id '{id}' (expected letters, digits, underscores)");
        }

        await using var cmd = data.CreateCommand(
            """
            UPDATE "Room" SET temperature = $1, humidity = $2, co2 = $3 WHERE id = $4
            RETURNING id, name, temperature, humidity, co2
            """);
        cmd.Parameters.AddWithValue(readings.Temperature);
        cmd.Parameters.AddWithValue(readings.Humidity);
        cmd.Parameters.AddWithValue(readings.Co2);
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"no room with id '{id}'");
        }

        return new JsonObject
        {
            ["id"] = reader.GetString(0),
            ["name"] = reader.GetString(1),
            ["temperature"] = reader.GetInt32(2),
            ["humidity"] = reader.GetInt32(3),
            ["co2"] = reader.GetInt32(4),
        };
    }
}

sealed class Simulator(NpgsqlDataSource data)
{
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts is not null;

    public async Task StartAsync()
    {
        if (_cts is not null)
        {
            return;
        }

        var ids = new List<string>();
        await using (var cmd = data.CreateCommand("""SELECT id FROM "Room" ORDER BY id"""))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            var random = new Random();
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    var id = ids[random.Next(ids.Count)];
                    var readings = new RoomReadings(
                        55 + random.Next(31),
                        20 + random.Next(36),
                        5 + random.Next(900));
                    try
                    {
                        await Rooms.Set(data, id, readings);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[simulate] {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        }, token);
        Console.WriteLine($"[simulate] started ({ids.Count} rooms, every 3000ms)");
    }

    public void Stop()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        Console.WriteLine("[simulate] stopped");
    }
}

sealed class SseHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        _clients[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Publish(string path, string op, JsonObject row)
    {
        var frame = new JsonObject
        {
            ["path"] = path,
            ["msg"] = new JsonObject
            {
                ["op"] = op,
                ["row"] = row.DeepClone(),
            },
        }.ToJsonString();

        foreach (var channel in _clients.Values)
        {
            channel.Writer.TryWrite(frame);
        }
    }
}
