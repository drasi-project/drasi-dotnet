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

using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi;
using Xunit;

namespace Drasi.Tests;

/// <summary>
/// Query middleware must actually transform changes, not just accept the argument.
/// Mirrors drasi-python tests/e2e/test_middleware.py.
/// </summary>
public sealed class MiddlewareTests
{
    private const string CityQuery = "MATCH (o:Order) RETURN o.id AS id, o.city AS city";

    [Fact]
    public async Task NestedObjectPropertiesRoundTrip()
    {
        await using var engine = await CreateStartedAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync(
            "q",
            "MATCH (o:Order) RETURN o.id AS id, o.address AS address",
            ["orders"]);
        await engine.WaitForQueryAsync("q");
        await engine.PushChangeAsync("orders", NestedCityOrder());
        var rows = await WaitForRowsAsync(engine, "q");
        Assert.Single(rows);
        Assert.Equal("Cambridge", rows[0]["address"]?["city"]?.ToString());
    }

    [Fact]
    public async Task PromoteLiftsNestedPropertyForTheQuery()
    {
        await using var engine = await CreateStartedAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync("q", CityQuery, ["orders"], PromoteCity("orders"));
        await engine.WaitForQueryAsync("q");

        await engine.PushChangeAsync(
            "orders",
            JsonNode.Parse("""
                {
                  "op": "insert",
                  "id": "o1",
                  "labels": ["Order"],
                  "properties": { "id": "o1", "address": { "city": "Cambridge" } }
                }
                """)!);
        var rows = await WaitForRowsAsync(engine, "q");
        Assert.Single(rows);
        Assert.Equal("o1", rows[0]["id"]?.ToString());
        Assert.Equal("Cambridge", rows[0]["city"]?.ToString());
    }

    [Fact]
    public async Task DeclaredMiddlewareDoesNothingWithoutAPipeline()
    {
        await using var engine = await CreateStartedAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync(
            "q",
            CityQuery,
            ["orders"],
            new QueryOptions { Middleware = [PromoteCityMw()] });
        await engine.WaitForQueryAsync("q");

        await engine.PushChangeAsync("orders", NestedCityOrder());
        var rows = await WaitForRowsAsync(engine, "q");
        Assert.Single(rows);
        Assert.True(IsJsonNull(rows[0]["city"]));
    }

    [Fact]
    public async Task SourceMappingWithoutPipelineMatchesBareId()
    {
        await using var engine = await CreateStartedAsync();
        await engine.AddSourceAsync("orders");
        await engine.AddQueryAsync(
            "q",
            CityQuery,
            ["orders"],
            new QueryOptions { Sources = [new QuerySource { Id = "orders" }] });
        await engine.WaitForQueryAsync("q");

        await engine.PushChangeAsync("orders", NestedCityOrder());
        var rows = await WaitForRowsAsync(engine, "q");
        Assert.Single(rows);
        Assert.True(IsJsonNull(rows[0]["city"]));
    }

    [Fact]
    public async Task UnknownMiddlewareKindIsRejected()
    {
        await using var engine = await CreateStartedAsync();
        await engine.AddSourceAsync("orders");
        var ex = await Assert.ThrowsAnyAsync<DrasiException>(() =>
            engine.AddQueryAsync(
                "q",
                CityQuery,
                ["orders"],
                new QueryOptions
                {
                    Sources = [new QuerySource { Id = "orders", Pipeline = ["flatten"] }],
                    Middleware =
                    [
                        new QueryMiddleware
                        {
                            Name = "flatten",
                            Kind = "no-such-middleware",
                            Config = [],
                        },
                    ],
                }));
        Assert.Contains("no-such-middleware", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelineNamingUndeclaredMiddlewareIsRejected()
    {
        await using var engine = await CreateStartedAsync();
        await engine.AddSourceAsync("orders");
        var ex = await Assert.ThrowsAnyAsync<DrasiException>(() =>
            engine.AddQueryAsync(
                "q",
                CityQuery,
                ["orders"],
                new QueryOptions
                {
                    Sources = [new QuerySource { Id = "orders", Pipeline = ["never-declared"] }],
                    Middleware = [PromoteCityMw()],
                }));
        Assert.Contains("never-declared", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromConfigReadsMiddleware()
    {
        var config = JsonNode.Parse("""
            {
              "id": "mw-from-config",
              "queries": [{
                "id": "q",
                "query": "MATCH (o:Order) RETURN o.id AS id, o.city AS city",
                "sources": [{ "id": "orders", "pipeline": ["flatten"] }],
                "middleware": [{ "name": "flatten" }]
              }]
            }
            """)!;
        var ex = await Assert.ThrowsAnyAsync<DrasiException>(() => Engine.FromConfigAsync(config));
        Assert.Contains("kind", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static QueryOptions PromoteCity(string sourceId) => new()
    {
        Sources = [new QuerySource { Id = sourceId, Pipeline = ["flatten"] }],
        Middleware = [PromoteCityMw()],
    };

    private static QueryMiddleware PromoteCityMw() => new()
    {
        Name = "flatten",
        Kind = "promote",
        Config = JsonNode.Parse("""
            {"mappings":[{"path":"$.address.city","target_name":"city"}]}
            """) as JsonObject,
    };

    private static SourceChange NestedCityOrder() => new()
    {
        Op = ChangeOp.Insert,
        Id = "o1",
        Labels = ["Order"],
        Properties = new JsonObject
        {
            ["id"] = "o1",
            ["address"] = new JsonObject { ["city"] = "Cambridge" },
        },
    };

    private static async Task<Engine> CreateStartedAsync()
    {
        var engine = await Engine.CreateAsync($"mw-{Guid.NewGuid():N}");
        await engine.StartAsync();
        return engine;
    }

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

    private static bool IsJsonNull(JsonNode? node) =>
        node is null || (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null);
}
