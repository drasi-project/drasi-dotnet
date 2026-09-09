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
using MySqlConnector;

namespace Drasi.Tutorials.CurbsidePickup;

sealed class VehicleRepository(MySqlDataSource data, SqlLog log)
{
    public async Task<JsonObject> ToggleAsync(string plate)
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

    public async Task ResetAllAsync()
    {
        log.Add("MySQL", "UPDATE vehicles SET location='Parking';");
        await using var cmd = data.CreateCommand();
        cmd.CommandText = "UPDATE vehicles SET location = 'Parking'";
        await cmd.ExecuteNonQueryAsync();
    }

    static string Lit(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
