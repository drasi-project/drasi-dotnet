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
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Drasi.Tests;

public sealed class ParserTests
{
    [Fact]
    public void ParsesHostInfoSnakeAndCamel()
    {
        var info = Engine.ParseHostInfo("""
            {
              "target_triple": "aarch64-apple-darwin",
              "archSuffix": "darwin-arm64",
              "ffi_sdk_version": "0.10.0",
              "sdkVersion": "0.10.0",
              "core_version": "0.5.7",
              "lib_version": "0.8.9",
              "index_backends": ["memory", "rocksdb"]
            }
            """);
        Assert.Equal("aarch64-apple-darwin", info.TargetTriple);
        Assert.Equal("darwin-arm64", info.ArchSuffix);
        Assert.Equal("0.5.7", info.CoreVersion);
        Assert.Contains("rocksdb", info.IndexBackends);
    }

    [Fact]
    public void ParsesSearchResults()
    {
        var found = Engine.ParseSearchResults("""
            [{
              "reference": "source/mock",
              "fullReference": "ghcr.io/drasi-project/source/mock",
              "pluginType": "source",
              "kind": "mock",
              "versions": [{ "version": "0.2.7", "platforms": ["darwin-arm64"] }]
            }]
            """);
        var item = Assert.Single(found);
        Assert.Equal("source", item.PluginType);
        Assert.Equal("mock", item.Kind);
        Assert.Equal("0.2.7", Assert.Single(item.Versions).Version);
    }

    [Fact]
    public void ParsesResolvedInstalledAndPulledPlugins()
    {
        var resolved = Engine.ParseResolvedPlugin("""
            {
              "reference": "ghcr.io/drasi-project/source/mock:0.2.7",
              "kind": "mock",
              "pluginType": "source",
              "version": "0.2.7",
              "targetTriple": "aarch64-apple-darwin",
              "sdkVersion": "0.10.0",
              "coreVersion": "0.5.7",
              "libVersion": "0.8.9"
            }
            """);
        Assert.Equal("mock", resolved.Kind);
        Assert.Equal("source", resolved.PluginType);

        var installed = Engine.ParseInstalledPlugin("""
            {
              "reference": "ghcr.io/x",
              "kind": "mock",
              "pluginType": "source",
              "version": "0.2.7",
              "path": "/tmp/p.dylib",
              "verification": "verified",
              "loaded": true
            }
            """);
        Assert.Equal(PluginVerification.Verified, installed.Verification);
        Assert.True(installed.Loaded);

        var pulled = Engine.ParsePulledPlugin("""
            { "reference": "ghcr.io/x", "path": "/tmp/p.dylib", "verification": "unsigned" }
            """);
        Assert.Equal(PluginVerification.Unsigned, pulled.Verification);
    }

    [Fact]
    public void ParsesLockfileEntriesAndSchemas()
    {
        var locked = Engine.ParseLockedPlugins("""
            [{
              "reference": "ghcr.io/drasi-project/source/mock@sha256:abc",
              "version": "0.2.7",
              "digest": "sha256:abc",
              "filename": "libdrasi_source_mock.dylib",
              "platform": "darwin/arm64",
              "file_hash": "deadbeef",
              "sdk_version": "0.10.0",
              "core_version": "0.5.7",
              "lib_version": "0.8.9"
            }]
            """);
        var entry = Assert.Single(locked);
        Assert.Equal("libdrasi_source_mock.dylib", entry.Filename);
        Assert.Equal("deadbeef", entry.FileHash);

        var schema = Engine.ParseConfigSchema("""{ "name": "MockSource", "schema": { "type": "object" } }""");
        Assert.Equal("MockSource", schema.Name);
        Assert.Equal("object", schema.Schema["type"]?.ToString());

        var source = Engine.ParseSourceSchema("""
            { "nodes": [{ "label": "Order", "properties": [{ "name": "id", "data_type": "string" }] }], "relations": [] }
            """);
        Assert.Equal("Order", Assert.Single(source!.Nodes).Label);

        var graph = Engine.ParseGraphSchema("""
            { "nodes": { "Order": {} }, "relations": {}, "sourcesWithoutSchema": ["s"] }
            """);
        Assert.True(graph.Nodes.ContainsKey("Order"));
        Assert.Equal("s", Assert.Single(graph.SourcesWithoutSchema));
    }

    [Fact]
    public void ParsesLogMessage()
    {
        var log = Engine.ParseLogMessage("""
            {
              "timestamp": "2026-01-02T03:04:05Z",
              "level": "INFO",
              "message": "started",
              "instance_id": "demo",
              "component_id": "orders",
              "component_type": "source"
            }
            """);
        Assert.Equal("INFO", log.Level);
        Assert.Equal("started", log.Message);
        Assert.Equal("demo", log.InstanceId);
        Assert.Equal("orders", log.ComponentId);
        Assert.Equal("source", log.ComponentType);
        Assert.Equal(2026, log.Timestamp.Year);
    }

    [Fact]
    public void ConfigurationKeepsNumericLookingStrings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["id"] = "001",
                ["secrets:TOKEN"] = "12345",
                ["autoStart"] = "true",
            })
            .Build();
        var json = ConfigurationJson.ToObject(config);
        Assert.Equal("001", json["id"]?.GetValue<string>());
        Assert.Equal("12345", json["secrets"]!["TOKEN"]?.GetValue<string>());
        Assert.True(json["autoStart"]?.GetValue<bool>());
    }

    [Fact]
    public void BuilderConfigureCanSetOptions()
    {
        var builder = new Drasi.DependencyInjection.DrasiBuilder("x");
        builder.Configure(o =>
        {
            o.PluginsDir = "/tmp/plugins";
            o.Secrets = new Dictionary<string, string> { ["A"] = "1" };
        });
        Assert.Equal("/tmp/plugins", builder.Options.PluginsDir);
        Assert.Equal("1", builder.Options.Secrets!["A"]);
    }

    [Fact]
    public void TrustedIdentitiesJsonIsPairs()
    {
        var json = Engine.TrustedIdentitiesJson(
        [
            ("https://token.actions.githubusercontent.com", "https://github.com/drasi-project/*"),
        ]);
        var pair = Assert.IsType<JsonArray>(Assert.Single(json));
        Assert.Equal("https://token.actions.githubusercontent.com", pair[0]?.GetValue<string>());
        Assert.Equal("https://github.com/drasi-project/*", pair[1]?.GetValue<string>());
    }

    [Fact]
    public void ReadLockfileParsesOnDiskFormat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "drasi-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "plugins.lock"), """
                version = 1

                [plugins."source/mock"]
                reference = "ghcr.io/drasi-project/source/mock@sha256:abc"
                version = "0.2.7"
                digest = "sha256:abc"
                sdk_version = "0.10.0"
                core_version = "0.5.7"
                lib_version = "0.8.9"
                platform = "darwin/arm64"
                filename = "libdrasi_source_mock.dylib"
                """);
            var locked = Engine.ReadLockfile(dir);
            var entry = Assert.Single(locked);
            Assert.Equal("0.2.7", entry.Version);
            Assert.Equal("libdrasi_source_mock.dylib", entry.Filename);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
