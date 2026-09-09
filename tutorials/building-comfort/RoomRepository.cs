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
using System.Text.RegularExpressions;
using Npgsql;

namespace Drasi.Tutorials.BuildingComfort;

sealed record RoomReadings(int Temperature, int Humidity, int Co2)
{
    public JsonObject ToJson() => new()
    {
        ["temperature"] = Temperature,
        ["humidity"] = Humidity,
        ["co2"] = Co2,
    };
}

sealed class RoomRepository(NpgsqlDataSource data)
{
    public static RoomReadings Comfortable { get; } = new(70, 40, 10);

    public static RoomReadings Broken { get; } = new(40, 20, 700);

    public async Task<JsonArray> ListAsync()
    {
        var rooms = new JsonArray();
        await using var cmd = data.CreateCommand(
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

        return rooms;
    }

    public async Task<IReadOnlyList<string>> ListIdsAsync()
    {
        var ids = new List<string>();
        await using var cmd = data.CreateCommand("""SELECT id FROM "Room" ORDER BY id""");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task<JsonObject> SetAsync(string id, RoomReadings readings)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_]+$"))
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

    public Task<JsonObject> ResetAsync(string id) => SetAsync(id, Comfortable);

    public async Task<int> ResetAllAsync()
    {
        await using var cmd = data.CreateCommand(
            """UPDATE "Room" SET temperature = $1, humidity = $2, co2 = $3""");
        cmd.Parameters.AddWithValue(Comfortable.Temperature);
        cmd.Parameters.AddWithValue(Comfortable.Humidity);
        cmd.Parameters.AddWithValue(Comfortable.Co2);
        return await cmd.ExecuteNonQueryAsync();
    }
}

sealed class Simulator(RoomRepository rooms)
{
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts is not null;

    public async Task StartAsync()
    {
        if (_cts is not null)
        {
            return;
        }

        var ids = await rooms.ListIdsAsync();
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
                        await rooms.SetAsync(id, readings);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[simulate] {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
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
