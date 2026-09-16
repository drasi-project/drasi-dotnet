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

public sealed class ErrorPathTests
{
    [Fact]
    public async Task CreateRejectsEmptyId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.CreateAsync(""));
    }

    [Fact]
    public async Task FromConfigRejectsNonObject()
    {
        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            Engine.FromConfigAsync(new JsonArray()));
        Assert.Equal(DrasiErrorCodes.ConfigInvalid, ex.Code);
    }

    [Fact]
    public async Task UnknownPluginKindsAreTyped()
    {
        await using var engine = await TestEngine.StartedAsync("kinds");
        var reaction = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.AddReactionAsync("nope", "watch", []));
        Assert.Equal(DrasiErrorCodes.UnknownReactionKind, reaction.Code);

        var source = await Assert.ThrowsAsync<UnknownKindException>(() =>
            engine.AddSourceAsync("postgres", "orders", bootstrap: new JsonObject { ["kind"] = "sql" }));
        Assert.Equal(DrasiErrorCodes.UnknownSourceKind, source.Code);

        Assert.Equal(
            DrasiErrorCodes.UnknownBootstrapKind,
            (await Assert.ThrowsAsync<UnknownKindException>(() =>
                engine.GetBootstrapConfigSchemaAsync("nope"))).Code);
        Assert.Equal(
            DrasiErrorCodes.UnknownReactionKind,
            (await Assert.ThrowsAsync<UnknownKindException>(() =>
                engine.GetReactionConfigSchemaAsync("nope"))).Code);
        Assert.Equal(
            DrasiErrorCodes.UnknownSourceKind,
            (await Assert.ThrowsAsync<UnknownKindException>(() =>
                engine.GetSourceConfigSchemaAsync("nope"))).Code);
        Assert.Equal(
            DrasiErrorCodes.UnknownSecretStoreKind,
            (await Assert.ThrowsAsync<UnknownKindException>(() =>
                engine.GetSecretStoreConfigSchemaAsync("nope"))).Code);
    }

    [Fact]
    public async Task ChangePayloadErrorsAreTyped()
    {
        await using var engine = await TestEngine.StartedAsync("change");
        await engine.AddSourceAsync("orders");

        var notObject = await Assert.ThrowsAsync<SourceException>(() =>
            engine.PushChangeAsync("orders", new JsonArray()));
        Assert.Equal(DrasiErrorCodes.ChangeNotObject, notObject.Code);

        var op = await Assert.ThrowsAsync<SourceException>(() =>
            engine.PushChangeAsync("orders", new JsonObject { ["id"] = "o1" }));
        Assert.Equal(DrasiErrorCodes.ChangeOpRequired, op.Code);

        var id = await Assert.ThrowsAsync<SourceException>(() =>
            engine.PushChangeAsync("orders", new JsonObject { ["op"] = "insert" }));
        Assert.Equal(DrasiErrorCodes.ChangeIdRequired, id.Code);

        var relation = await Assert.ThrowsAsync<SourceException>(() =>
            engine.PushChangeAsync("orders", new JsonObject
            {
                ["op"] = "insert",
                ["id"] = "r1",
                ["startId"] = "a",
            }));
        Assert.Equal(DrasiErrorCodes.RelationRequiresBothEnds, relation.Code);
    }

    [Fact]
    public async Task StoreAndIdentityErrorsAreTyped()
    {
        var state = await Assert.ThrowsAsync<UnknownKindException>(() =>
            Engine.CreateAsync(
                TestEngine.Id("state"),
                new EngineOptions
                {
                    StateStore = new StateStoreOptions { Kind = "redis", Path = "/tmp/x" },
                }));
        Assert.Equal(DrasiErrorCodes.UnknownStateStoreKind, state.Code);

        var identityKind = await Assert.ThrowsAsync<ConfigException>(() =>
            Engine.FromConfigAsync(JsonNode.Parse("""{ "id": "i", "identity": {} }""")!));
        Assert.Equal(DrasiErrorCodes.IdentityKindRequired, identityKind.Code);

        var identityCfg = await Assert.ThrowsAsync<ConfigException>(() =>
            Engine.CreateAsync(
                TestEngine.Id("id-cfg"),
                new EngineOptions { Identity = new IdentityOptions { Kind = "password" } }));
        Assert.Equal(DrasiErrorCodes.IdentityConfigInvalid, identityCfg.Code);

        var unknownIdentity = await Assert.ThrowsAsync<UnknownKindException>(() =>
            Engine.CreateAsync(
                TestEngine.Id("id-kind"),
                new EngineOptions { Identity = new IdentityOptions { Kind = "oauth" } }));
        Assert.Equal(DrasiErrorCodes.UnknownIdentityKind, unknownIdentity.Code);
    }

    [Fact]
    public async Task UnknownQueryLanguageIsTyped()
    {
        var config = JsonNode.Parse("""
            {
              "id": "lang",
              "sources": [{ "id": "orders" }],
              "queries": [{
                "id": "q",
                "query": "MATCH (o:Order) RETURN o.id AS id",
                "sources": ["orders"],
                "language": "sql"
              }]
            }
            """)!;
        var ex = await Assert.ThrowsAsync<UnknownKindException>(() => Engine.FromConfigAsync(config));
        Assert.Equal(DrasiErrorCodes.UnknownQueryLanguage, ex.Code);
    }

    [Fact]
    public async Task InvalidQueryTextIsRejected()
    {
        await using var engine = await TestEngine.StartedAsync("bad-query");
        await engine.AddSourceAsync("orders");
        await Assert.ThrowsAnyAsync<DrasiException>(() =>
            engine.AddQueryAsync("q", "this is not cypher", ["orders"]));
        await Assert.ThrowsAnyAsync<DrasiException>(() =>
            engine.AddQueryAsync(
                "q2",
                "this is not gql",
                ["orders"],
                new QueryOptions { Language = QueryLanguage.Gql }));
    }

    [Fact]
    public async Task EmptyJoinKeysAreRejected()
    {
        await using var engine = await TestEngine.StartedAsync("joins");
        await engine.AddSourceAsync("orders");
        var ex = await Assert.ThrowsAsync<ConfigException>(() =>
            engine.AddQueryAsync(
                "q",
                TestEngine.OrdersQuery,
                ["orders"],
                new QueryOptions
                {
                    Joins = [new QueryJoin { Id = "R", Keys = [] }],
                }));
        Assert.Equal(DrasiErrorCodes.ConfigInvalid, ex.Code);
    }

    [Fact]
    public async Task MissingPluginDirectoryIsTyped()
    {
        await using var engine = await Engine.CreateAsync(TestEngine.Id("plug"));
        var missing = Path.Combine(Path.GetTempPath(), "drasi-missing-" + Guid.NewGuid().ToString("N"));
        var ex = await Assert.ThrowsAsync<PluginNotFoundException>(() =>
            engine.LoadPluginsAsync(missing));
        Assert.Equal(DrasiErrorCodes.PluginNotFound, ex.Code);
    }

    [Fact]
    public void FromCodeMapsEveryStableCode()
    {
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownSourceKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownReactionKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownBootstrapKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownSecretStoreKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownStateStoreKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownIndexStoreKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownIdentityKind, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownQueryLanguage, "m").GetType());
        Assert.Equal(
            typeof(UnknownKindException),
            DrasiException.FromCode(DrasiErrorCodes.UnknownChangeOp, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.BootstrapKindRequired, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.StateStorePathRequired, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.IndexStorePathRequired, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.IdentityKindRequired, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.IdentityConfigInvalid, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.DurableRequiresStateStore, "m").GetType());
        Assert.Equal(
            typeof(ConfigException),
            DrasiException.FromCode(DrasiErrorCodes.ConfigInvalid, "m").GetType());
        Assert.Equal(
            typeof(SourceException),
            DrasiException.FromCode(DrasiErrorCodes.NoCsharpSource, "m").GetType());
        Assert.Equal(
            typeof(SourceException),
            DrasiException.FromCode(DrasiErrorCodes.ChangeNotObject, "m").GetType());
        Assert.Equal(
            typeof(SourceException),
            DrasiException.FromCode(DrasiErrorCodes.ChangeOpRequired, "m").GetType());
        Assert.Equal(
            typeof(SourceException),
            DrasiException.FromCode(DrasiErrorCodes.ChangeIdRequired, "m").GetType());
        Assert.Equal(
            typeof(SourceException),
            DrasiException.FromCode(DrasiErrorCodes.RelationRequiresBothEnds, "m").GetType());
        Assert.Equal(
            typeof(PluginSignatureException),
            DrasiException.FromCode(DrasiErrorCodes.PluginSignatureInvalid, "m").GetType());
        Assert.Equal(
            typeof(PluginCompatibilityException),
            DrasiException.FromCode(DrasiErrorCodes.PluginIncompatible, "m").GetType());
        Assert.Equal(
            typeof(PluginNotFoundException),
            DrasiException.FromCode(DrasiErrorCodes.PluginNotFound, "m").GetType());
        Assert.Equal(
            typeof(StreamLaggedException),
            DrasiException.FromCode(DrasiErrorCodes.StreamLagged, "m").GetType());
        Assert.Equal(
            typeof(DrasiException),
            DrasiException.FromCode(DrasiErrorCodes.EngineClosed, "m").GetType());
        Assert.Equal(
            typeof(DrasiException),
            DrasiException.FromCode(DrasiErrorCodes.EngineFailure, "m").GetType());
        Assert.Equal(
            typeof(DrasiException),
            DrasiException.FromCode("UNMAPPED_CODE", "m").GetType());
    }

    [Fact]
    public void StreamLaggedExceptionReportsDroppedCount()
    {
        var ex = new StreamLaggedException(3);
        Assert.Equal(DrasiErrorCodes.StreamLagged, ex.Code);
        Assert.Equal(3u, ex.Dropped);
        Assert.Contains("3", ex.Message);
    }
}
