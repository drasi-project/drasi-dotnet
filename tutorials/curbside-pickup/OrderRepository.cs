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
using Npgsql;

namespace Drasi.Tutorials.CurbsidePickup;

sealed class OrderRepository(NpgsqlDataSource data, SqlLog log)
{
    public async Task<JsonObject> ToggleAsync(int id)
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

    public async Task ResetAllAsync()
    {
        log.Add("PostgreSQL", "UPDATE orders SET status='preparing';");
        await using var cmd = data.CreateCommand("UPDATE orders SET status = 'preparing'");
        await cmd.ExecuteNonQueryAsync();
    }

    static string Lit(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
