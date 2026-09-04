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
using MySqlConnector;
using Npgsql;

var pgHost = Env("POSTGRES_HOST", "localhost");
var pgPort = EnvInt("POSTGRES_PORT", 5842);
var pgDatabase = Env("POSTGRES_DATABASE", "RetailOperations");
var pgUser = Env("POSTGRES_USER", "drasi_user");
var pgPassword = Env("POSTGRES_PASSWORD", "drasi_password");
var myHost = Env("MYSQL_HOST", "localhost");
var myPort = EnvInt("MYSQL_PORT", 3319);
var myDatabase = Env("MYSQL_DATABASE", "PhysicalOperations");
var myUser = Env("MYSQL_USER", "drasi_user");
var myPassword = Env("MYSQL_PASSWORD", "drasi_password");
var webPort = EnvInt("WEB_PORT", 3000);
var pluginsDir = Path.Combine(Path.GetTempPath(), "drasi-dotnet-curbside-pickup-plugins");
Directory.CreateDirectory(pluginsDir);

var orderShape = Shape(("id", "id"), ("orderId", "orderId"), ("customerName", "customerName"),
    ("driverName", "driverName"), ("plate", "plate"), ("status", "status"));
var vehicleShape = Shape(("id", "id"), ("plate", "plate"), ("make", "make"),
    ("model", "model"), ("color", "color"), ("location", "location"));
var deliveryShape = Shape(("id", "id"), ("orderId", "orderId"), ("driverName", "driverName"),
    ("vehicleId", "vehicleId"), ("vehicleMake", "vehicleMake"), ("vehicleModel", "vehicleModel"),
    ("vehicleColor", "vehicleColor"), ("readyTimestamp", "readyTimestamp"));
var delayShape = Shape(("id", "orderId"), ("orderId", "orderId"), ("customerName", "customerName"),
    ("waitingSince", "waitingSinceTimestamp"));

var streams = new Dictionary<string, (string Path, Dictionary<string, string> Shape)>(StringComparer.Ordinal)
{
    ["orders-preparing"] = ("orders-preparing", orderShape),
    ["orders-ready"] = ("orders-ready", orderShape),
    ["vehicles-parking"] = ("vehicles-parking", vehicleShape),
    ["vehicles-curbside"] = ("vehicles-curbside", vehicleShape),
    ["delivery"] = ("delivery", deliveryShape),
    ["delay"] = ("delay", delayShape),
};

Console.WriteLine("Starting Curbside Pickup…");
Console.WriteLine("  • creating engine, downloading plugins, connecting to PostgreSQL + MySQL…");

await WaitForPortAsync(pgHost, pgPort, "PostgreSQL", 90);
await WaitForPortAsync(myHost, myPort, "MySQL", 90);

using var logs = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole(o => o.SingleLine = true);
});

var hub = new SseHub();
var sqlLog = new SqlLog();
await using var drasi = await Engine.CreateAsync("curbside-pickup", new EngineOptions { LoggerFactory = logs });
await drasi.InstallPluginAsync("source/postgres", pluginsDir);
await drasi.InstallPluginAsync("bootstrap/postgres", pluginsDir);
await drasi.InstallPluginAsync("source/mysql", pluginsDir);
await drasi.InstallPluginAsync("bootstrap/mysql", pluginsDir);
await drasi.StartAsync();

var pg = PostgresConfig(pgHost, pgPort, pgDatabase, pgUser, pgPassword);
var mysql = MysqlConfig(myHost, myPort, myDatabase, myUser, myPassword);
await drasi.AddSourceAsync("postgres", "retail-ops", pg, bootstrap: Bootstrap("postgres", pg));
await drasi.AddSourceAsync("mysql", "physical-ops", mysql, bootstrap: Bootstrap("mysql", new JsonObject
{
    ["host"] = myHost,
    ["port"] = myPort,
    ["database"] = myDatabase,
    ["user"] = myUser,
    ["password"] = myPassword,
    ["tables"] = new JsonArray { "vehicles" },
    ["tableKeys"] = new JsonArray
    {
        new JsonObject { ["table"] = "vehicles", ["keyColumns"] = new JsonArray { "plate" } },
    },
}));

var pickupBy = new QueryJoin
{
    Id = "PICKUP_BY",
    Keys =
    [
        new QueryJoinKey { Label = "vehicles", Property = "plate" },
        new QueryJoinKey { Label = "orders", Property = "plate" },
    ],
};

await drasi.AddQueryAsync("orders-preparing", """
    MATCH (o:orders)
    WHERE o.status <> 'ready'
    RETURN o.id AS id, o.id AS orderId, o.customer_name AS customerName,
           o.driver_name AS driverName, o.plate AS plate, o.status AS status
    """, ["retail-ops"]);

await drasi.AddQueryAsync("orders-ready", """
    MATCH (o:orders)
    WHERE o.status = 'ready'
    RETURN o.id AS id, o.id AS orderId, o.customer_name AS customerName,
           o.driver_name AS driverName, o.plate AS plate, o.status AS status
    """, ["retail-ops"]);

await drasi.AddQueryAsync("vehicles-parking", """
    MATCH (v:vehicles)
    WHERE v.location = 'Parking'
    RETURN v.plate AS id, v.plate AS plate, v.make AS make,
           v.model AS model, v.color AS color, v.location AS location
    """, ["physical-ops"]);

await drasi.AddQueryAsync("vehicles-curbside", """
    MATCH (v:vehicles)
    WHERE v.location = 'Curbside'
    RETURN v.plate AS id, v.plate AS plate, v.make AS make,
           v.model AS model, v.color AS color, v.location AS location
    """, ["physical-ops"]);

await drasi.AddQueryAsync("delivery", """
    MATCH (o:orders)-[:PICKUP_BY]->(v:vehicles)
    WHERE o.status = 'ready' AND v.location = 'Curbside'
    RETURN o.id AS id, o.id AS orderId, o.status AS orderStatus,
           o.driver_name AS driverName, o.plate AS vehicleId,
           v.make AS vehicleMake, v.model AS vehicleModel, v.color AS vehicleColor,
           v.location AS vehicleLocation,
           drasi.listMax([drasi.changeDateTime(o), drasi.changeDateTime(v)]) AS readyTimestamp
    """, ["retail-ops", "physical-ops"], new QueryOptions { Joins = [pickupBy] });

await drasi.AddQueryAsync("delay", """
    MATCH (o:orders)-[:PICKUP_BY]->(v:vehicles)
    WHERE o.status <> 'ready'
    AND drasi.trueFor(v.location = 'Curbside', duration({ seconds: 10 }))
    RETURN o.id AS orderId, o.customer_name AS customerName,
           drasi.changeDateTime(v) AS waitingSinceTimestamp
    """, ["retail-ops", "physical-ops"], new QueryOptions { Joins = [pickupBy] });

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

await using var pgDb = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
{
    Host = pgHost, Port = pgPort, Database = pgDatabase, Username = pgUser, Password = pgPassword,
}.ConnectionString);
await using var mysqlDb = new MySqlDataSource(new MySqlConnectionStringBuilder
{
    Server = myHost, Port = (uint)myPort, Database = myDatabase, UserID = myUser, Password = myPassword,
    SslMode = MySqlSslMode.Disabled,
}.ConnectionString);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://0.0.0.0:{webPort}");
var app = builder.Build();

app.MapGet("/api/state", async () => Results.Json(new JsonObject
{
    ["streams"] = new JsonObject
    {
        ["orders-preparing"] = await Snapshot(drasi, "orders-preparing", orderShape),
        ["orders-ready"] = await Snapshot(drasi, "orders-ready", orderShape),
        ["vehicles-parking"] = await Snapshot(drasi, "vehicles-parking", vehicleShape),
        ["vehicles-curbside"] = await Snapshot(drasi, "vehicles-curbside", vehicleShape),
        ["delivery"] = await Snapshot(drasi, "delivery", deliveryShape),
        ["delay"] = await Snapshot(drasi, "delay", delayShape),
    },
    ["log"] = sqlLog.ToJson(),
}));

app.MapGet("/api/log", () => Results.Json(new JsonObject { ["log"] = sqlLog.ToJson() }));

app.MapPost("/api/orders/{id}/toggle", async (int id) =>
{
    try
    {
        return Results.Json(new JsonObject
        {
            ["order"] = await ToggleOrder(pgDb, sqlLog, id),
            ["log"] = sqlLog.ToJson(),
        });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/vehicles/{plate}/toggle", async (string plate) =>
{
    try
    {
        return Results.Json(new JsonObject
        {
            ["vehicle"] = await ToggleVehicle(mysqlDb, sqlLog, plate),
            ["log"] = sqlLog.ToJson(),
        });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/reset", async () =>
{
    sqlLog.Add("PostgreSQL", "UPDATE orders SET status='preparing';");
    await using (var cmd = pgDb.CreateCommand("UPDATE orders SET status = 'preparing'"))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    sqlLog.Add("MySQL", "UPDATE vehicles SET location='Parking';");
    await using (var cmd = mysqlDb.CreateCommand())
    {
        cmd.CommandText = "UPDATE vehicles SET location = 'Parking'";
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Json(new JsonObject { ["ok"] = true, ["log"] = sqlLog.ToJson() });
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
Console.WriteLine($"✅ Curbside Pickup is ready — open http://localhost:{webPort}");
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
    }
}

static async Task<JsonObject> ToggleOrder(NpgsqlDataSource data, SqlLog log, int id)
{
    await using var lookup = data.CreateCommand("SELECT id, status FROM orders WHERE id = $1");
    lookup.Parameters.AddWithValue(id);
    await using var reader = await lookup.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException($"no order with id '{id}'");
    }

    var status = reader.GetString(1) == "ready" ? "preparing" : "ready";
    await reader.CloseAsync();
    log.Add("PostgreSQL", $"UPDATE orders SET status={Lit(status)} WHERE id={id};");
    await using var update = data.CreateCommand("UPDATE orders SET status = $1 WHERE id = $2");
    update.Parameters.AddWithValue(status);
    update.Parameters.AddWithValue(id);
    await update.ExecuteNonQueryAsync();
    return new JsonObject { ["id"] = id, ["status"] = status };
}

static async Task<JsonObject> ToggleVehicle(MySqlDataSource data, SqlLog log, string plate)
{
    await using var conn = await data.OpenConnectionAsync();
    await using var lookup = conn.CreateCommand();
    lookup.CommandText = "SELECT plate, location FROM vehicles WHERE plate = @plate";
    lookup.Parameters.AddWithValue("@plate", plate);
    await using var reader = await lookup.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        throw new InvalidOperationException($"no vehicle with plate '{plate}'");
    }

    var location = reader.GetString(1) == "Curbside" ? "Parking" : "Curbside";
    await reader.CloseAsync();
    log.Add("MySQL", $"UPDATE vehicles SET location={Lit(location)} WHERE plate={Lit(plate)};");
    await using var update = conn.CreateCommand();
    update.CommandText = "UPDATE vehicles SET location = @location WHERE plate = @plate";
    update.Parameters.AddWithValue("@location", location);
    update.Parameters.AddWithValue("@plate", plate);
    await update.ExecuteNonQueryAsync();
    return new JsonObject { ["plate"] = plate, ["location"] = location };
}

static string Lit(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

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
    ["tables"] = new JsonArray { "orders" },
    ["slotName"] = "drasi_curbside_slot",
    ["publicationName"] = "drasi_curbside_pub",
    ["tableKeys"] = new JsonArray
    {
        new JsonObject { ["table"] = "orders", ["keyColumns"] = new JsonArray { "id" } },
    },
};

static JsonObject MysqlConfig(string host, int port, string database, string user, string password) => new()
{
    ["host"] = host,
    ["port"] = port,
    ["database"] = database,
    ["user"] = user,
    ["password"] = password,
    ["sslMode"] = "disabled",
    ["tables"] = new JsonArray { "vehicles" },
    ["tableKeys"] = new JsonArray
    {
        new JsonObject { ["table"] = "vehicles", ["keyColumns"] = new JsonArray { "plate" } },
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

sealed class SqlLog
{
    private readonly List<JsonObject> _entries = [];
    private readonly object _gate = new();

    public void Add(string db, string text)
    {
        lock (_gate)
        {
            _entries.Add(new JsonObject
            {
                ["db"] = db,
                ["text"] = text,
                ["t"] = DateTime.UtcNow.ToString("O"),
            });
            while (_entries.Count > 25)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    public JsonArray ToJson()
    {
        lock (_gate)
        {
            var array = new JsonArray();
            foreach (var entry in _entries)
            {
                array.Add(entry.DeepClone());
            }

            return array;
        }
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
