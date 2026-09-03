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

namespace Drasi;

/// <summary>Graph change operation pushed into a C#-defined source.</summary>
public enum ChangeOp
{
    Insert,
    Update,
    Delete,
}

/// <summary>Continuous-query language. v1 is string Cypher/GQL; LINQ is deferred.</summary>
public enum QueryLanguage
{
    Cypher,
    Gql,
}

/// <summary>Lifecycle state of a source, query, or reaction.</summary>
public enum ComponentStatus
{
    Unknown,
    Added,
    Starting,
    Running,
    Stopping,
    Stopped,
    Removed,
    Reconfiguring,
    Error,
}

/// <summary>Kind of result-set diff emitted by a continuous query.</summary>
public enum DiffType
{
    Add,
    Update,
    Delete,
    Aggregation,
    Noop,
}

/// <summary>
/// A graph change pushed into a C#-defined source.
/// <c>id</c> is the graph key; a query selecting <c>o.id</c> reads a property
/// of that name, so emit it in <see cref="Properties"/> as well.
/// </summary>
public sealed class SourceChange
{
    public required ChangeOp Op { get; init; }

    public required string Id { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public JsonObject? Properties { get; init; }

    public string? StartId { get; init; }

    public string? EndId { get; init; }

    public long? EffectiveFrom { get; init; }
}

/// <summary>Optional settings for <see cref="Engine.CreateAsync"/>.</summary>
public sealed class EngineOptions
{
    /// <summary>
    /// Optional logger for managed-side diagnostics (category <c>Drasi</c>).
    /// Prefer <see cref="LoggerFactory"/> so native events get per-target
    /// categories such as <c>Drasi.drasi_lib</c>.
    /// </summary>
    public Microsoft.Extensions.Logging.ILogger? Logger { get; set; }

    /// <summary>
    /// When set, native <c>tracing</c> / <c>log</c> events are forwarded to
    /// <c>ILogger</c> instead of stderr. The native filter follows this
    /// factory's enabled levels (<c>RUST_LOG</c> still overrides at process start).
    /// </summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>In-memory secrets plugins resolve <c>ConfigValue::Secret</c> against.</summary>
    public IReadOnlyDictionary<string, string>? Secrets { get; init; }

    /// <summary>Persistent plugin/reaction state. Kind <c>redb</c> with a <c>Path</c>.</summary>
    public StateStoreOptions? StateStore { get; init; }

    /// <summary>Persistent query-index backend. Kind <c>rocksdb</c> with a <c>Path</c>.</summary>
    public IndexStoreOptions? IndexStore { get; init; }

    /// <summary>Built-in <c>password</c>/<c>token</c> identity, or a plugin kind.</summary>
    public IdentityOptions? Identity { get; init; }

    /// <summary>Directory of plugin cdylibs loaded at create time.</summary>
    public string? PluginsDir { get; init; }
}

public sealed class StateStoreOptions
{
    public string Kind { get; init; } = "redb";
    public required string Path { get; init; }
}

public sealed class IndexStoreOptions
{
    public string Kind { get; init; } = "rocksdb";
    public required string Path { get; init; }
    public bool? EnableArchive { get; init; }
    public bool? DirectIo { get; init; }
}

public sealed class IdentityOptions
{
    public required string Kind { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Token { get; init; }
    public JsonObject? Config { get; init; }
}

public sealed class PluginLoadSummary
{
    public int Plugins { get; init; }
    public int Sources { get; init; }
    public int Reactions { get; init; }
    public int Bootstrap { get; init; }
    public int SecretStores { get; init; }
    public int IdentityProviders { get; init; }
    public int Skipped { get; init; }
}

public sealed class PluginKinds
{
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> Reactions { get; init; } = [];
    public IReadOnlyList<string> Bootstrap { get; init; } = [];
    public IReadOnlyList<string> SecretStores { get; init; } = [];
    public IReadOnlyList<string> IdentityProviders { get; init; } = [];
}

public sealed class HostInfo
{
    public required string TargetTriple { get; init; }

    public string? ArchSuffix { get; init; }

    public required string FfiSdkVersion { get; init; }

    public required string SdkVersion { get; init; }

    public required string CoreVersion { get; init; }

    public required string LibVersion { get; init; }

    public IReadOnlyList<string> IndexBackends { get; init; } = [];
}

/// <summary>Versions this build was compiled against.</summary>
public static class DrasiVersion
{
    public static string Package =>
        typeof(Engine).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    public static string Core => Engine.GetHostInfo().CoreVersion;

    public static string Lib => Engine.GetHostInfo().LibVersion;

    public static string Sdk => Engine.GetHostInfo().SdkVersion;

    public static string FfiSdk => Engine.GetHostInfo().FfiSdkVersion;
}

public enum RecoveryPolicy
{
    Strict,
    AutoReset,
    SkipGap,
}

/// <summary>Tuning and extras for <see cref="Engine.AddQueryAsync"/>.</summary>
public sealed class QueryOptions
{
    public QueryLanguage Language { get; init; } = QueryLanguage.Cypher;

    public bool? AutoStart { get; init; }

    public bool? EnableBootstrap { get; init; }

    public ulong? BootstrapTimeoutSeconds { get; init; }

    public int? PriorityQueueCapacity { get; init; }

    public int? DispatchBufferCapacity { get; init; }

    public int? OutboxCapacity { get; init; }

    /// <summary><c>channel</c> or <c>broadcast</c>.</summary>
    public string? DispatchMode { get; init; }

    public IReadOnlyList<QueryJoin>? Joins { get; init; }

    public IReadOnlyList<QueryMiddleware>? Middleware { get; init; }

    public IReadOnlyList<QuerySource>? Sources { get; init; }
}

/// <summary>A source subscription, optionally with a middleware pipeline.</summary>
public sealed class QuerySource
{
    public required string Id { get; init; }

    public IReadOnlyList<string>? Pipeline { get; init; }
}

/// <summary>Synthetic join across labels, matching the Python/Node shape.</summary>
public sealed class QueryJoin
{
    public required string Id { get; init; }

    public required IReadOnlyList<QueryJoinKey> Keys { get; init; }
}

public sealed class QueryJoinKey
{
    public required string Label { get; init; }

    public required string Property { get; init; }
}

/// <summary>Named middleware instance applied via a source <see cref="QuerySource.Pipeline"/>.</summary>
public sealed class QueryMiddleware
{
    public required string Name { get; init; }

    public required string Kind { get; init; }

    public JsonObject? Config { get; init; }
}

/// <summary>Id and status of a registered component.</summary>
public sealed class ComponentInfo
{
    public required string Id { get; init; }

    public required ComponentStatus Status { get; init; }
}

/// <summary>One emission from a continuous query (a set of diffs).</summary>
public sealed class QueryResultEvent
{
    public required string QueryId { get; init; }

    public ulong Sequence { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public IReadOnlyList<QueryDiff> Results { get; init; } = [];

    public JsonObject? Metadata { get; init; }
}

/// <summary>A single added, updated, or deleted row in a <see cref="QueryResultEvent"/>.</summary>
public sealed class QueryDiff
{
    public required DiffType Type { get; init; }

    public JsonObject? Data { get; init; }

    public JsonObject? Before { get; init; }

    public JsonObject? After { get; init; }

    public IReadOnlyList<string>? GroupingKeys { get; init; }
}

/// <summary>Lifecycle transition of a source, query, or reaction.</summary>
public sealed class ComponentEvent
{
    public required string ComponentId { get; init; }

    public string? ComponentType { get; init; }

    public ComponentStatus Status { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public string? Message { get; init; }
}

/// <summary>A log line emitted by a component, including from a plugin.</summary>
public sealed class LogMessage
{
    public DateTimeOffset Timestamp { get; init; }

    public string Level { get; init; } = "";

    public string Message { get; init; } = "";

    public string? InstanceId { get; init; }

    public string? ComponentId { get; init; }

    public string? ComponentType { get; init; }
}

public sealed class PropertySchema
{
    public string Name { get; init; } = "";

    public string? DataType { get; init; }

    public string? Description { get; init; }
}

public sealed class NodeSchema
{
    public string Label { get; init; } = "";

    public IReadOnlyList<PropertySchema> Properties { get; init; } = [];
}

public sealed class RelationSchema
{
    public string Label { get; init; } = "";

    public string? To { get; init; }

    public IReadOnlyList<PropertySchema> Properties { get; init; } = [];
}

public sealed class SourceSchema
{
    public IReadOnlyList<NodeSchema> Nodes { get; init; } = [];

    public IReadOnlyList<RelationSchema> Relations { get; init; } = [];
}

public sealed class GraphSchema
{
    public JsonObject Nodes { get; init; } = [];

    public JsonObject Relations { get; init; } = [];

    public IReadOnlyList<string> SourcesWithoutSchema { get; init; } = [];
}

public sealed class ConfigSchema
{
    public string Name { get; init; } = "";

    public JsonObject Schema { get; init; } = [];
}

public enum PluginVerification
{
    Unsigned,
    Verified,
    Tampered,
}

public sealed class PluginVersion
{
    public string Version { get; init; } = "";

    public IReadOnlyList<string> Platforms { get; init; } = [];
}

public sealed class PluginSearchResult
{
    public string Reference { get; init; } = "";

    public string FullReference { get; init; } = "";

    public string PluginType { get; init; } = "";

    public string Kind { get; init; } = "";

    public IReadOnlyList<PluginVersion> Versions { get; init; } = [];
}

public sealed class ResolvedPlugin
{
    public string Reference { get; init; } = "";

    public string Kind { get; init; } = "";

    public string PluginType { get; init; } = "";

    public string Version { get; init; } = "";

    public string? TargetTriple { get; init; }

    public string? SdkVersion { get; init; }

    public string? CoreVersion { get; init; }

    public string? LibVersion { get; init; }
}

public sealed class InstalledPlugin
{
    public string Reference { get; init; } = "";

    public string Kind { get; init; } = "";

    public string PluginType { get; init; } = "";

    public string Version { get; init; } = "";

    public string Path { get; init; } = "";

    public PluginVerification Verification { get; init; }

    public bool Loaded { get; init; }
}

public sealed class PulledPlugin
{
    public string Reference { get; init; } = "";

    public string Path { get; init; } = "";

    public PluginVerification Verification { get; init; }
}

public sealed class LockedPlugin
{
    public string Reference { get; init; } = "";

    public string Version { get; init; } = "";

    public string Digest { get; init; } = "";

    public string Filename { get; init; } = "";

    public string Platform { get; init; } = "";

    public string? FileHash { get; init; }

    public string? SdkVersion { get; init; }

    public string? CoreVersion { get; init; }

    public string? LibVersion { get; init; }
}

/// <summary>Output-side metrics for a query.</summary>
public sealed class QueryMetrics
{
    public ulong OutboxSize { get; init; }
    public ulong OutboxEarliestSeq { get; init; }
    public ulong OutboxLatestSeq { get; init; }
    public ulong ResultSeqAdvances { get; init; }
    public ulong LiveResultsCount { get; init; }
    public ulong OuterTransactionDurationNsLast { get; init; }
    public ulong OuterTransactionDurationNsMax { get; init; }
    public ulong SnapshotFetchCount { get; init; }
}

/// <summary>Per-query metrics for a reaction.</summary>
public sealed class ReactionQueryMetrics
{
    public ulong CheckpointSequence { get; init; }
    public ulong CheckpointLag { get; init; }
    public ulong DedupSkipCount { get; init; }
    public ulong GapDetectionCount { get; init; }
    public ulong RecoveryStrictCount { get; init; }
    public ulong RecoveryAutoResetCount { get; init; }
    public ulong RecoveryAutoSkipGapCount { get; init; }
    public ulong FetchSnapshotCount { get; init; }
    public ulong FetchOutboxCount { get; init; }
}

/// <summary>Engine-wide lifecycle metrics, mostly about durable-reaction recovery.</summary>
public sealed class LifecycleMetrics
{
    public ulong StartupRejectionDurableNoStore { get; init; }
    public ulong StartupRejectionDurableOnVolatile { get; init; }
    public ulong StartupRejectionSnapshotSkipGap { get; init; }
    public ulong StartupRejectionNoSnapshotAutoReset { get; init; }
    public ulong AutoResetCompletions { get; init; }
    public ulong HashMismatchCount { get; init; }
}
