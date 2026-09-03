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
using Xunit;

namespace Drasi.Tests;

public sealed class RecoveryTests
{
    private const string OrdersQuery = "MATCH (o:Order) RETURN o.id AS id";

    // Bootstrap off so recovered rows can only come from the RocksDB index.
    private static readonly QueryOptions PersistentQuery = new() { EnableBootstrap = false };

    [Fact]
    public async Task IndexStoreRequiresPath()
    {
        var config = JsonNode.Parse("""
            { "id": "t", "indexStore": { "kind": "rocksdb" } }
            """)!;
        var ex = await Assert.ThrowsAsync<ConfigException>(() => Engine.FromConfigAsync(config));
        Assert.Equal(DrasiErrorCodes.IndexStorePathRequired, ex.Code);
    }

    [Fact]
    public async Task UnknownIndexStoreKindIsRejected()
    {
        var config = JsonNode.Parse("""
            { "id": "t", "indexStore": { "kind": "lmdb", "path": "/tmp/x" } }
            """)!;
        var ex = await Assert.ThrowsAsync<UnknownKindException>(() => Engine.FromConfigAsync(config));
        Assert.Equal(DrasiErrorCodes.UnknownIndexStoreKind, ex.Code);
    }

    [Fact]
    public async Task RocksDbIndexWritesFiles()
    {
        var path = NewIndexPath();
        try
        {
            await using (var engine = await OpenAsync(path, "rocks-write"))
            {
                await engine.AddSourceAsync("orders");
                await engine.AddQueryAsync("open", OrdersQuery, ["orders"], PersistentQuery);
                await engine.WaitForQueryAsync("open");
                await engine.PushChangeAsync("orders", Order("o1"));
                Assert.Equal("o1", Assert.Single(await WaitForRowsAsync(engine, "open"))["id"]?.ToString());
            }

            Assert.True(Directory.EnumerateFileSystemEntries(path).Any(), "RocksDB should have written files");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task RocksDbIndexSurvivesQueryRestart()
    {
        var path = NewIndexPath();
        try
        {
            await using var engine = await OpenAsync(path, "rocks-restart");
            await engine.AddSourceAsync("orders");
            await engine.AddQueryAsync("open", OrdersQuery, ["orders"], PersistentQuery);
            await engine.WaitForQueryAsync("open");
            await engine.PushChangeAsync("orders", Order("o1"));
            Assert.Equal("o1", Assert.Single(await WaitForRowsAsync(engine, "open"))["id"]?.ToString());

            await engine.StopQueryAsync("open");
            await WaitForStatusAsync(engine, "open", ComponentStatus.Stopped);
            await engine.StartQueryAsync("open");
            await engine.WaitForQueryAsync("open");

            var restored = await WaitForRowsAsync(engine, "open");
            Assert.Equal("o1", Assert.Single(restored)["id"]?.ToString());
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string NewIndexPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "drasi-rocks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task WaitForStatusAsync(Engine engine, string queryId, ComponentStatus expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        ComponentStatus status = ComponentStatus.Unknown;
        while (DateTime.UtcNow < deadline)
        {
            status = await engine.GetQueryStatusAsync(queryId);
            if (status == expected)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"query '{queryId}' stayed {status} instead of {expected}");
    }

    private static async Task<Engine> OpenAsync(string indexPath, string id)
    {
        var engine = await Engine.CreateAsync(
            id,
            new EngineOptions
            {
                IndexStore = new IndexStoreOptions { Kind = "rocksdb", Path = indexPath },
            });
        await engine.StartAsync();
        return engine;
    }

    private static SourceChange Order(string id) => new()
    {
        Op = ChangeOp.Insert,
        Id = id,
        Labels = ["Order"],
        Properties = new JsonObject { ["id"] = id },
    };

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
}
