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

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Drasi;

/// <summary>
/// An in-process Drasi continuous-query engine.
/// </summary>
public sealed class Engine : IAsyncDisposable, IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeCallback(nint json, nint userData);

    private static readonly NativeCallback SharedCallback = OnNativeCallback;
    private static readonly nint SharedCallbackPtr = Marshal.GetFunctionPointerForDelegate(SharedCallback);
    private static readonly ActivitySource Activities = new("Drasi");

    private readonly nint _handle;
    private readonly string _id;
    private readonly ILogger? _logger;
    private readonly List<GCHandle> _callbackHandles = [];
    private readonly List<Action> _onDispose = [];
    private readonly object _gate = new();
    private bool _disposed;

    private Engine(nint handle, string id, ILogger? logger)
    {
        _handle = handle;
        _id = id;
        _logger = logger;
    }

    /// <summary>The engine identifier supplied at creation.</summary>
    public string Id => _id;

    /// <summary>Builds an engine. It is not started until <see cref="StartAsync"/>.</summary>
    public static Task<Engine> CreateAsync(
        string id,
        EngineOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Native.EnsureLoaded();
        Logging.Configure(options);
        var logger = Logging.Logger;
        var optionsJson = ToCreateOptionsJson(options);
        return RunAsync(
            () =>
            {
                var handle = Native.ThrowIfNull(
                    optionsJson is null
                        ? Native.drasi_engine_create(id)
                        : Native.drasi_engine_create_with_options(id, optionsJson),
                    "Create");
                return new Engine(handle, id, logger);
            },
            cancellationToken);
    }

    /// <summary>
    /// Builds an engine from a JSON document of sources, queries, and reactions,
    /// then starts it. Missing <c>id</c> defaults to <c>drasi</c>.
    /// <paramref name="options"/> (secrets, stores, identity, plugins dir) are
    /// merged in when the document does not already set those keys.
    /// </summary>
    public static Task<Engine> FromConfigAsync(
        JsonNode config,
        EngineOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        Native.EnsureLoaded();
        if (config is not JsonObject obj)
        {
            throw new ConfigException(DrasiErrorCodes.ConfigInvalid, "config must be a JSON object");
        }

        MergeCreateOptions(obj, options);
        var json = obj.ToJsonString();
        Logging.Configure(options);
        var logger = Logging.Logger;
        return RunAsync(
            () =>
            {
                using var activity = Activities.StartActivity("FromConfig");
                var handle = Native.ThrowIfNull(Native.drasi_engine_from_config(json), "FromConfig");
                var id = obj["id"]?.GetValue<string>() ?? "drasi";
                return new Engine(handle, id, logger);
            },
            cancellationToken);
    }

    /// <summary>
    /// Builds an engine from an <see cref="IConfiguration"/> section (for example
    /// <c>appsettings.json</c> <c>Drasi</c>), then starts it.
    /// </summary>
    public static Task<Engine> FromConfigAsync(
        IConfiguration config,
        EngineOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return FromConfigAsync(ConfigurationJson.ToObject(config), options, cancellationToken);
    }

    /// <summary>Starts the engine and every component configured to auto-start.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                using var activity = Activities.StartActivity("Start");
                Native.ThrowIfError(Native.drasi_engine_start(handle), "Start");
            },
            cancellationToken);
    }

    /// <summary>Stops the engine, leaving its components in place.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                using var activity = Activities.StartActivity("Stop");
                Native.ThrowIfError(Native.drasi_engine_stop(handle), "Stop");
            },
            cancellationToken);
    }

    /// <summary>
    /// Permanently shuts the engine down and releases native stores (including
    /// the RocksDB lock). Further calls fail. Prefer this over
    /// <see cref="StopAsync"/> when the process is exiting; dispose still
    /// destroys the handle.
    /// </summary>
    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                using var activity = Activities.StartActivity("Shutdown");
                Native.ThrowIfError(Native.drasi_engine_shutdown(handle), "Shutdown");
            },
            cancellationToken);
    }

    /// <summary>Whether the engine is currently running.</summary>
    public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                var status = Native.drasi_engine_is_running(handle);
                if (status < 0)
                {
                    throw Native.LastException("IsRunning");
                }

                return status != 0;
            },
            cancellationToken);
    }

    /// <summary>Registers an in-process source that you push changes into with <see cref="PushChangeAsync(string, SourceChange, CancellationToken)"/>.</summary>
    public Task AddSourceAsync(
        string id,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_add_source(handle, id, autoStart ? 1 : 0),
                "AddSource"),
            cancellationToken);
    }

    public Task RemoveSourceAsync(
        string id,
        bool cleanup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_remove_source(handle, id, cleanup ? 1 : 0),
                "RemoveSource"),
            cancellationToken);
    }

    public Task StartSourceAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_start_source, "StartSource", cancellationToken);

    public Task StopSourceAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_stop_source, "StopSource", cancellationToken);

    public Task<ComponentStatus> GetSourceStatusAsync(string id, CancellationToken cancellationToken = default)
        => GetStatusAsync(id, Native.drasi_engine_source_status, "GetSourceStatus", cancellationToken);

    public Task<IReadOnlyList<ComponentInfo>> ListSourcesAsync(CancellationToken cancellationToken = default)
        => ListAsync(Native.drasi_engine_list_sources, "ListSources", cancellationToken);

    /// <summary>Registers a continuous Cypher or GQL query over one or more sources.</summary>
    public Task AddQueryAsync(
        string id,
        string query,
        IReadOnlyList<string> sources,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(sources);
        EnsureNotDisposed();
        var handle = _handle;
        var sourcesJson = ToSourcesJson(sources, options);
        var optionsJson = ToQueryOptionsJson(options);
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_add_query(handle, id, query, sourcesJson, optionsJson),
                "AddQuery"),
            cancellationToken);
    }

    public Task UpdateQueryAsync(
        string id,
        string query,
        IReadOnlyList<string> sources,
        QueryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(sources);
        EnsureNotDisposed();
        var handle = _handle;
        var sourcesJson = ToSourcesJson(sources, options);
        var optionsJson = ToQueryOptionsJson(options);
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_update_query(handle, id, query, sourcesJson, optionsJson),
                "UpdateQuery"),
            cancellationToken);
    }

    public Task RemoveQueryAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_remove_query, "RemoveQuery", cancellationToken);

    public Task StartQueryAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_start_query, "StartQuery", cancellationToken);

    public Task StopQueryAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_stop_query, "StopQuery", cancellationToken);

    /// <summary>The current result set of a query, as a list of row objects.</summary>
    public Task<IReadOnlyList<JsonObject>> GetQueryResultsAsync(
        string queryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryId);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                var json = Native.TakeString(
                    Native.drasi_engine_get_query_results(handle, queryId),
                    "GetQueryResults");
                return ParseObjectArray(json);
            },
            cancellationToken);
    }

    public Task<ComponentStatus> GetQueryStatusAsync(string id, CancellationToken cancellationToken = default)
        => GetStatusAsync(id, Native.drasi_engine_query_status, "GetQueryStatus", cancellationToken);

    public Task<IReadOnlyList<ComponentInfo>> ListQueriesAsync(CancellationToken cancellationToken = default)
        => ListAsync(Native.drasi_engine_list_queries, "ListQueries", cancellationToken);

    /// <summary>
    /// Waits until a query is running. <see cref="AddQueryAsync"/> returns once
    /// the query is provisioned; it finishes starting in the background.
    /// </summary>
    public Task WaitForQueryAsync(
        string queryId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryId);
        EnsureNotDisposed();
        var handle = _handle;
        var seconds = (timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds;
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_wait_for_query(handle, queryId, seconds),
                "WaitForQuery"),
            cancellationToken);
    }

    /// <summary>
    /// Registers an in-process reaction that invokes <paramref name="callback"/>
    /// with each query result.
    /// </summary>
    public Task AddReactionAsync(
        string id,
        IReadOnlyList<string> queryIds,
        Action<QueryResultEvent> callback,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        ArgumentNullException.ThrowIfNull(callback);
        EnsureNotDisposed();
        var handle = _handle;
        var queryIdsJson = ToJsonArray(queryIds);
        Action<string> adapter = json => callback(ParseQueryResult(json));
        var gcHandle = TrackCallback(adapter);
        return RunAsync(
            () =>
            {
                try
                {
                    Native.ThrowIfError(
                        Native.drasi_engine_add_reaction(
                            handle,
                            id,
                            queryIdsJson,
                            SharedCallbackPtr,
                            GCHandle.ToIntPtr(gcHandle),
                            autoStart ? 1 : 0),
                        "AddReaction");
                }
                catch
                {
                    Untrack(gcHandle);
                    throw;
                }
            },
            cancellationToken);
    }

    public Task RemoveReactionAsync(
        string id,
        bool cleanup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_remove_reaction(handle, id, cleanup ? 1 : 0),
                "RemoveReaction"),
            cancellationToken);
    }

    public Task StartReactionAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_start_reaction, "StartReaction", cancellationToken);

    public Task StopReactionAsync(string id, CancellationToken cancellationToken = default)
        => CallId(id, Native.drasi_engine_stop_reaction, "StopReaction", cancellationToken);

    public Task<ComponentStatus> GetReactionStatusAsync(string id, CancellationToken cancellationToken = default)
        => GetStatusAsync(id, Native.drasi_engine_reaction_status, "GetReactionStatus", cancellationToken);

    public Task<IReadOnlyList<ComponentInfo>> ListReactionsAsync(CancellationToken cancellationToken = default)
        => ListAsync(Native.drasi_engine_list_reactions, "ListReactions", cancellationToken);

    /// <summary>
    /// Durable C# reaction: the callback must succeed before the checkpoint
    /// advances. Requires <see cref="EngineOptions.StateStore"/>.
    /// </summary>
    public Task AddDurableReactionAsync(
        string id,
        IReadOnlyList<string> queryIds,
        Func<QueryResultEvent, Task> callback,
        RecoveryPolicy recovery = RecoveryPolicy.Strict,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        ArgumentNullException.ThrowIfNull(callback);
        EnsureNotDisposed();
        var handle = _handle;
        var queryIdsJson = ToJsonArray(queryIds);
        Func<string, Task> adapter = json => callback(ParseQueryResult(json));
        var gcHandle = TrackCallback(adapter);
        var policy = recovery switch
        {
            RecoveryPolicy.AutoReset => "auto_reset",
            RecoveryPolicy.SkipGap => "skip_gap",
            _ => "strict",
        };
        return RunAsync(
            () =>
            {
                try
                {
                    Native.ThrowIfError(
                        Native.drasi_engine_add_durable_reaction(
                            handle, id, queryIdsJson, SharedCallbackPtr, GCHandle.ToIntPtr(gcHandle), policy),
                        "AddDurableReaction");
                }
                catch
                {
                    Untrack(gcHandle);
                    throw;
                }
            },
            cancellationToken);
    }

    public Task AddSourceAsync(
        string kind,
        string id,
        JsonObject? config = null,
        bool autoStart = true,
        JsonObject? bootstrap = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        var configJson = (config ?? []).ToJsonString();
        var bootstrapJson = bootstrap?.ToJsonString();
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_add_plugin_source(
                    handle, kind, id, configJson, autoStart ? 1 : 0, bootstrapJson),
                "AddSource"),
            cancellationToken);
    }

    public Task AddReactionAsync(
        string kind,
        string id,
        IReadOnlyList<string> queryIds,
        JsonObject? config = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        EnsureNotDisposed();
        var handle = _handle;
        var configJson = (config ?? []).ToJsonString();
        var queryIdsJson = ToJsonArray(queryIds);
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_add_plugin_reaction(
                    handle, kind, id, queryIdsJson, configJson, autoStart ? 1 : 0),
                "AddReaction"),
            cancellationToken);
    }

    public Task UpdateSourceAsync(
        string kind,
        string id,
        JsonObject? config = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        var configJson = (config ?? []).ToJsonString();
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_update_plugin_source(
                    handle, kind, id, configJson, autoStart ? 1 : 0),
                "UpdateSource"),
            cancellationToken);
    }

    public Task UpdateReactionAsync(
        string kind,
        string id,
        IReadOnlyList<string> queryIds,
        JsonObject? config = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        EnsureNotDisposed();
        var handle = _handle;
        var configJson = (config ?? []).ToJsonString();
        var queryIdsJson = ToJsonArray(queryIds);
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_update_plugin_reaction(
                    handle, kind, id, queryIdsJson, configJson, autoStart ? 1 : 0),
                "UpdateReaction"),
            cancellationToken);
    }

    public Task<PluginLoadSummary> LoadPluginsAsync(
        string directory,
        IReadOnlyDictionary<string, string>? verify = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        EnsureNotDisposed();
        var handle = _handle;
        string? verifyJson = null;
        if (verify is not null)
        {
            var obj = new JsonObject();
            foreach (var (key, value) in verify)
            {
                obj[key] = value;
            }

            verifyJson = obj.ToJsonString();
        }

        return RunAsync(
            () => ParseLoadSummary(Native.TakeString(
                Native.drasi_engine_load_plugins(handle, directory, verifyJson),
                "LoadPlugins")),
            cancellationToken);
    }

    public Task WatchPluginsAsync(
        string directory,
        TimeSpan? debounce = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        EnsureNotDisposed();
        var handle = _handle;
        var seconds = (debounce ?? TimeSpan.FromSeconds(1)).TotalSeconds;
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_watch_plugins(handle, directory, seconds),
                "WatchPlugins"),
            cancellationToken);
    }

    public Task<PluginKinds> PluginKindsAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParsePluginKinds(Native.TakeString(Native.drasi_engine_plugin_kinds(handle), "PluginKinds")),
            cancellationToken);
    }

    public static HostInfo GetHostInfo()
    {
        Native.EnsureLoaded();
        var json = Native.TakeString(Native.drasi_host_info(), "HostInfo");
        return ParseHostInfo(json);
    }

    public static Task<IReadOnlyList<PluginSearchResult>> SearchPluginsAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        Native.EnsureLoaded();
        return RunAsync(
            () => ParseSearchResults(Native.TakeString(Native.drasi_search_plugins(query), "SearchPlugins")),
            cancellationToken);
    }

    public static Task<IReadOnlyList<string>> ListPluginTagsAsync(string repository, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        Native.EnsureLoaded();
        return RunAsync(
            () =>
            {
                var json = Native.TakeString(Native.drasi_list_plugin_tags(repository), "ListPluginTags");
                if (JsonNode.Parse(json) is not JsonArray array)
                {
                    return (IReadOnlyList<string>)[];
                }

                return array.Select(item => item?.GetValue<string>() ?? "").ToArray();
            },
            cancellationToken);
    }

    public static Task<ResolvedPlugin> ResolvePluginAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        Native.EnsureLoaded();
        return RunAsync(
            () => ParseResolvedPlugin(Native.TakeString(Native.drasi_resolve_plugin(reference), "ResolvePlugin")),
            cancellationToken);
    }

    public Task<InstalledPlugin> InstallPluginAsync(
        string reference,
        string? directory = null,
        bool verify = false,
        bool requireSigned = false,
        IReadOnlyList<(string Issuer, string SubjectPattern)>? trustedIdentities = null,
        bool load = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        EnsureNotDisposed();
        var handle = _handle;
        var options = new JsonObject
        {
            ["verify"] = verify,
            ["requireSigned"] = requireSigned,
            ["load"] = load,
        };
        if (directory is not null)
        {
            options["directory"] = directory;
        }

        if (trustedIdentities is { Count: > 0 } identities)
        {
            options["trustedIdentities"] = TrustedIdentitiesJson(identities);
        }

        var optionsJson = options.ToJsonString();
        return RunAsync(
            () => ParseInstalledPlugin(Native.TakeString(
                Native.drasi_engine_install_plugin(handle, reference, optionsJson),
                "InstallPlugin")),
            cancellationToken);
    }

    public static Task<PulledPlugin> PullPluginAsync(
        string reference,
        string directory,
        string filename,
        bool verify = false,
        bool requireSigned = false,
        IReadOnlyList<(string Issuer, string SubjectPattern)>? trustedIdentities = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        Native.EnsureLoaded();
        var options = new JsonObject { ["verify"] = verify, ["requireSigned"] = requireSigned };
        if (trustedIdentities is { Count: > 0 } identities)
        {
            options["trustedIdentities"] = TrustedIdentitiesJson(identities);
        }

        return RunAsync(
            () => ParsePulledPlugin(Native.TakeString(
                Native.drasi_pull_plugin(reference, directory, filename, options.ToJsonString()),
                "PullPlugin")),
            cancellationToken);
    }

    public Task<int> WriteLockfileAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => int.Parse(Native.TakeString(Native.drasi_engine_write_lockfile(handle, directory), "WriteLockfile")),
            cancellationToken);
    }

    public static IReadOnlyList<LockedPlugin> ReadLockfile(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Native.EnsureLoaded();
        return ParseLockedPlugins(Native.TakeString(Native.drasi_read_lockfile(directory), "ReadLockfile"));
    }

    public Task<IReadOnlyList<string>> InstallFromLockfileAsync(
        string directory,
        bool load = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                var json = Native.TakeString(
                    Native.drasi_engine_install_from_lockfile(handle, directory, load ? 1 : 0),
                    "InstallFromLockfile");
                if (JsonNode.Parse(json) is not JsonArray array)
                {
                    return (IReadOnlyList<string>)[];
                }

                return array.Select(item => item?.GetValue<string>() ?? "").ToArray();
            },
            cancellationToken);
    }

    public Task<ConfigSchema> GetSourceConfigSchemaAsync(string kind, CancellationToken cancellationToken = default)
        => SchemaAsync(kind, Native.drasi_engine_source_config_schema, "SourceConfigSchema", cancellationToken);

    public Task<ConfigSchema> GetReactionConfigSchemaAsync(string kind, CancellationToken cancellationToken = default)
        => SchemaAsync(kind, Native.drasi_engine_reaction_config_schema, "ReactionConfigSchema", cancellationToken);

    public Task<ConfigSchema> GetBootstrapConfigSchemaAsync(string kind, CancellationToken cancellationToken = default)
        => SchemaAsync(kind, Native.drasi_engine_bootstrap_config_schema, "BootstrapConfigSchema", cancellationToken);

    public Task<ConfigSchema> GetSecretStoreConfigSchemaAsync(string kind, CancellationToken cancellationToken = default)
        => SchemaAsync(kind, Native.drasi_engine_secret_store_config_schema, "SecretStoreConfigSchema", cancellationToken);

    public Task UseSecretStoreAsync(
        string kind,
        JsonObject? config = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        EnsureNotDisposed();
        var handle = _handle;
        var configJson = (config ?? []).ToJsonString();
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_use_secret_store(handle, kind, configJson),
                "UseSecretStore"),
            cancellationToken);
    }

    private Task<ConfigSchema> SchemaAsync(
        string kind,
        Func<nint, string, nint> call,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseConfigSchema(Native.TakeString(call(handle, kind), operation)),
            cancellationToken);
    }

    /// <summary>Emits a change from a C#-defined source.</summary>
    public Task PushChangeAsync(
        string sourceId,
        SourceChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return PushChangeAsync(sourceId, ToJson(change), cancellationToken);
    }

    /// <summary>Emits a change from a C#-defined source, as a JSON object.</summary>
    public Task PushChangeAsync(
        string sourceId,
        JsonNode change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return PushChangeAsync(sourceId, change.ToJsonString(), cancellationToken);
    }

    public Task<QueryMetrics> GetQueryMetricsAsync(string queryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryId);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseQueryMetrics(Native.TakeString(
                Native.drasi_engine_query_metrics(handle, queryId),
                "GetQueryMetrics")),
            cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, ReactionQueryMetrics>> GetReactionMetricsAsync(
        string reactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reactionId);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseReactionMetrics(Native.TakeString(
                Native.drasi_engine_reaction_metrics(handle, reactionId),
                "GetReactionMetrics")),
            cancellationToken);
    }

    public Task<LifecycleMetrics> GetLifecycleMetricsAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseLifecycleMetrics(Native.TakeString(
                Native.drasi_engine_lifecycle_metrics(handle),
                "GetLifecycleMetrics")),
            cancellationToken);
    }

    public Task<SourceSchema?> GetSourceSchemaAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                var json = Native.TakeString(Native.drasi_engine_source_schema(handle, id), "GetSourceSchema");
                if (json is "null" or "")
                {
                    return null;
                }

                return ParseSourceSchema(json);
            },
            cancellationToken);
    }

    public Task<GraphSchema> GetGraphSchemaAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseGraphSchema(Native.TakeString(Native.drasi_engine_graph_schema(handle), "GetGraphSchema")),
            cancellationToken);
    }

    /// <summary>Streams the diffs a query produces as an async enumerable.</summary>
    public async IAsyncEnumerable<QueryResultEvent> QueryResultsAsync(
        string queryId,
        string? reactionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryId);
        EnsureNotDisposed();
        var channel = OpenStreamChannel<QueryResultEvent>();
        reactionId ??= $"__stream_{queryId}_{Guid.NewGuid():N}";
        await AddReactionAsync(
            reactionId,
            [queryId],
            evt => TryWriteStream(channel.Writer, evt),
            autoStart: true,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            try
            {
                await RemoveReactionAsync(reactionId, cleanup: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to remove streaming reaction {ReactionId}", reactionId);
            }
        }
    }

    public IAsyncEnumerable<ComponentEvent> QueryEventsAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SubscribeEventsAsync(
            (cb, data) => Native.drasi_engine_subscribe_query_events(_handle, id, cb, data),
            "QueryEvents",
            cancellationToken);

    public IAsyncEnumerable<ComponentEvent> SourceEventsAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SubscribeEventsAsync(
            (cb, data) => Native.drasi_engine_subscribe_source_events(_handle, id, cb, data),
            "SourceEvents",
            cancellationToken);

    public IAsyncEnumerable<ComponentEvent> ReactionEventsAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SubscribeEventsAsync(
            (cb, data) => Native.drasi_engine_subscribe_reaction_events(_handle, id, cb, data),
            "ReactionEvents",
            cancellationToken);

    public IAsyncEnumerable<ComponentEvent> AllEventsAsync(CancellationToken cancellationToken = default)
        => SubscribeEventsAsync(
            (cb, data) => Native.drasi_engine_subscribe_all_events(_handle, cb, data),
            "AllEvents",
            cancellationToken);

    public IAsyncEnumerable<LogMessage> QueryLogsAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SubscribeLogsAsync(
            (cb, data) => Native.drasi_engine_subscribe_query_logs(_handle, id, cb, data),
            "QueryLogs",
            cancellationToken);

    public IAsyncEnumerable<LogMessage> SourceLogsAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SubscribeLogsAsync(
            (cb, data) => Native.drasi_engine_subscribe_source_logs(_handle, id, cb, data),
            "SourceLogs",
            cancellationToken);

    public IAsyncEnumerable<LogMessage> ReactionLogsAsync(
        string id,
        CancellationToken cancellationToken = default)
        => SubscribeLogsAsync(
            (cb, data) => Native.drasi_engine_subscribe_reaction_logs(_handle, id, cb, data),
            "ReactionLogs",
            cancellationToken);

    public void Dispose()
    {
        DisposeCore(async: false).GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCore(async: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task DisposeCore(bool async)
    {
        nint handle;
        List<GCHandle> callbacks;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            handle = _handle;
            callbacks = [.. _callbackHandles];
            _callbackHandles.Clear();
            foreach (var complete in _onDispose)
            {
                try
                {
                    complete();
                }
                catch (Exception)
                {
                    // Completers must not throw out of dispose.
                }
            }

            _onDispose.Clear();
        }

        try
        {
            if (async)
            {
                await RunAsync(
                    () => Native.drasi_engine_destroy(handle),
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                Native.drasi_engine_destroy(handle);
            }
        }
        finally
        {
            foreach (var callback in callbacks)
            {
                if (callback.IsAllocated)
                {
                    callback.Free();
                }
            }
        }
    }

    private Task CallId(
        string id,
        Func<nint, string, int> call,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(() => Native.ThrowIfError(call(handle, id), operation), cancellationToken);
    }

    private Task<ComponentStatus> GetStatusAsync(
        string id,
        Func<nint, string, nint> call,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseStatus(Native.TakeString(call(handle, id), operation)),
            cancellationToken);
    }

    private Task<IReadOnlyList<ComponentInfo>> ListAsync(
        Func<nint, nint> call,
        string operation,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => ParseComponentList(Native.TakeString(call(handle), operation)),
            cancellationToken);
    }

    private async IAsyncEnumerable<ComponentEvent> SubscribeEventsAsync(
        Func<nint, nint, nint> start,
        string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        await foreach (var json in SubscribeJsonAsync(start, operation, cancellationToken).ConfigureAwait(false))
        {
            yield return ParseComponentEvent(json);
        }
    }

    private async IAsyncEnumerable<LogMessage> SubscribeLogsAsync(
        Func<nint, nint, nint> start,
        string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        await foreach (var json in SubscribeJsonAsync(start, operation, cancellationToken).ConfigureAwait(false))
        {
            yield return ParseLogMessage(json);
        }
    }

    private async IAsyncEnumerable<string> SubscribeJsonAsync(
        Func<nint, nint, nint> start,
        string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = OpenStreamChannel<string>();
        Action<string> writer = json => TryWriteStream(channel.Writer, json);
        var gcHandle = TrackCallback(writer);
        nint stream;
        try
        {
            stream = Native.ThrowIfNull(start(SharedCallbackPtr, GCHandle.ToIntPtr(gcHandle)), operation);
        }
        catch
        {
            Untrack(gcHandle);
            throw;
        }
        try
        {
            await foreach (var json in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (TryLagged(json, out var dropped))
                {
                    throw new StreamLaggedException(dropped);
                }

                yield return json;
            }
        }
        finally
        {
            Native.drasi_stream_close(stream);
            channel.Writer.TryComplete();
            Untrack(gcHandle);
        }
    }

    private GCHandle TrackCallback(Delegate callback)
    {
        var gcHandle = GCHandle.Alloc(callback);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _callbackHandles.Add(gcHandle);
        }

        return gcHandle;
    }

    private void Untrack(GCHandle gcHandle)
    {
        lock (_gate)
        {
            _callbackHandles.Remove(gcHandle);
        }

        if (gcHandle.IsAllocated)
        {
            gcHandle.Free();
        }
    }

    private Channel<T> OpenStreamChannel<T>()
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _onDispose.Add(() => channel.Writer.TryComplete());
        }

        return channel;
    }

    private static void TryWriteStream<T>(ChannelWriter<T> writer, T item)
    {
        if (!writer.TryWrite(item))
        {
            writer.TryComplete(new StreamLaggedException(1));
        }
    }

    private static void MergeCreateOptions(JsonObject config, EngineOptions? options)
    {
        var extraJson = ToCreateOptionsJson(options);
        if (extraJson is null || JsonNode.Parse(extraJson) is not JsonObject extra)
        {
            return;
        }

        foreach (var (key, value) in extra)
        {
            if (config[key] is null && value is not null)
            {
                config[key] = value.DeepClone();
            }
        }
    }

    private Task PushChangeAsync(string sourceId, string changeJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_push_change(handle, sourceId, changeJson),
                "PushChange"),
            cancellationToken);
    }

    private static Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
            },
            cancellationToken);
    }

    private static Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return action();
            },
            cancellationToken);
    }

    private static void AddNode(JsonArray array, JsonNode? node) => array.Add(node);

    private static void AddString(JsonArray array, string value) =>
        AddNode(array, JsonValue.Create(value));

    private static string ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            AddString(array, value);
        }

        return array.ToJsonString();
    }

    private static string ToSourcesJson(IReadOnlyList<string> sources, QueryOptions? options)
    {
        if (options?.Sources is { Count: > 0 } typed)
        {
            var array = new JsonArray();
            foreach (var source in typed)
            {
                if (source.Pipeline is { Count: > 0 } pipeline)
                {
                    var pipes = new JsonArray();
                    foreach (var name in pipeline)
                    {
                        AddString(pipes, name);
                    }

                    AddNode(array, new JsonObject { ["id"] = source.Id, ["pipeline"] = pipes });
                }
                else
                {
                    AddString(array, source.Id);
                }
            }

            return array.ToJsonString();
        }

        return ToJsonArray(sources);
    }

    private static string? ToQueryOptionsJson(QueryOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var node = new JsonObject();
        if (options.Language == QueryLanguage.Gql)
        {
            node["language"] = "gql";
        }
        else if (options.Language == QueryLanguage.Cypher)
        {
            node["language"] = "cypher";
        }

        if (options.AutoStart is { } autoStart)
        {
            node["autoStart"] = autoStart;
        }

        if (options.EnableBootstrap is { } enableBootstrap)
        {
            node["enableBootstrap"] = enableBootstrap;
        }

        if (options.BootstrapTimeoutSeconds is { } timeout)
        {
            node["bootstrapTimeoutSeconds"] = timeout;
        }

        if (options.PriorityQueueCapacity is { } pq)
        {
            node["priorityQueueCapacity"] = pq;
        }

        if (options.DispatchBufferCapacity is { } db)
        {
            node["dispatchBufferCapacity"] = db;
        }

        if (options.OutboxCapacity is { } ob)
        {
            node["outboxCapacity"] = ob;
        }

        if (options.DispatchMode is not null)
        {
            node["dispatchMode"] = options.DispatchMode;
        }

        if (options.Joins is { Count: > 0 } joins)
        {
            var array = new JsonArray();
            foreach (var join in joins)
            {
                var keys = new JsonArray();
                foreach (var key in join.Keys)
                {
                    AddNode(keys, new JsonObject { ["label"] = key.Label, ["property"] = key.Property });
                }

                AddNode(array, new JsonObject { ["id"] = join.Id, ["keys"] = keys });
            }

            node["joins"] = array;
        }

        if (options.Middleware is { Count: > 0 } middleware)
        {
            var array = new JsonArray();
            foreach (var item in middleware)
            {
                AddNode(array, new JsonObject
                {
                    ["name"] = item.Name,
                    ["kind"] = item.Kind,
                    ["config"] = item.Config?.DeepClone(),
                });
            }

            node["middleware"] = array;
        }

        return node.Count == 0 ? null : node.ToJsonString();
    }

    private static string? ToCreateOptionsJson(EngineOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var node = new JsonObject();
        if (options.Secrets is { Count: > 0 } secrets)
        {
            var obj = new JsonObject();
            foreach (var (key, value) in secrets)
            {
                obj[key] = value;
            }

            node["secrets"] = obj;
        }

        if (options.StateStore is { } state)
        {
            node["stateStore"] = new JsonObject { ["kind"] = state.Kind, ["path"] = state.Path };
        }

        if (options.IndexStore is { } index)
        {
            var obj = new JsonObject { ["kind"] = index.Kind, ["path"] = index.Path };
            if (index.EnableArchive is { } archive)
            {
                obj["enableArchive"] = archive;
            }

            if (index.DirectIo is { } direct)
            {
                obj["directIo"] = direct;
            }

            node["indexStore"] = obj;
        }

        if (options.Identity is { } identity)
        {
            var obj = identity.Config?.DeepClone() as JsonObject ?? [];
            obj["kind"] = identity.Kind;
            if (identity.Username is not null)
            {
                obj["username"] = identity.Username;
            }

            if (identity.Password is not null)
            {
                obj["password"] = identity.Password;
            }

            if (identity.Token is not null)
            {
                obj["token"] = identity.Token;
            }

            node["identity"] = obj;
        }

        if (options.PluginsDir is not null)
        {
            node["pluginsDir"] = options.PluginsDir;
        }

        return node.Count == 0 ? null : node.ToJsonString();
    }

    private static PluginLoadSummary ParseLoadSummary(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new PluginLoadSummary
        {
            Plugins = (int)U64(node, "plugins"),
            Sources = (int)U64(node, "sources"),
            Reactions = (int)U64(node, "reactions"),
            Bootstrap = (int)U64(node, "bootstrap"),
            SecretStores = (int)U64(node, "secretStores"),
            IdentityProviders = (int)U64(node, "identityProviders"),
            Skipped = (int)U64(node, "skipped"),
        };
    }

    private static PluginKinds ParsePluginKinds(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        static string[] Strings(JsonObject obj, string name) =>
            (obj[name] as JsonArray)?.Select(item => item?.GetValue<string>() ?? "").ToArray() ?? [];
        return new PluginKinds
        {
            Sources = Strings(node, "sources"),
            Reactions = Strings(node, "reactions"),
            Bootstrap = Strings(node, "bootstrap"),
            SecretStores = Strings(node, "secretStores"),
            IdentityProviders = Strings(node, "identityProviders"),
        };
    }

    internal static JsonArray TrustedIdentitiesJson(
        IReadOnlyList<(string Issuer, string SubjectPattern)> identities)
    {
        var array = new JsonArray();
        foreach (var (issuer, subject) in identities)
        {
            var pair = new JsonArray();
            AddString(pair, issuer);
            AddString(pair, subject);
            AddNode(array, pair);
        }

        return array;
    }

    internal static HostInfo ParseHostInfo(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new HostInfo
        {
            TargetTriple = Str(node, "target_triple", "targetTriple") ?? "",
            ArchSuffix = Str(node, "arch_suffix", "archSuffix"),
            FfiSdkVersion = Str(node, "ffi_sdk_version", "ffiSdkVersion") ?? "",
            SdkVersion = Str(node, "sdk_version", "sdkVersion") ?? "",
            CoreVersion = Str(node, "core_version", "coreVersion") ?? "",
            LibVersion = Str(node, "lib_version", "libVersion") ?? "",
            IndexBackends = Strings(node, "index_backends", "indexBackends"),
        };
    }

    internal static IReadOnlyList<PluginSearchResult> ParseSearchResults(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            return [];
        }

        var list = new List<PluginSearchResult>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var versions = new List<PluginVersion>();
            if (obj["versions"] is JsonArray versionArray)
            {
                foreach (var version in versionArray)
                {
                    if (version is not JsonObject v)
                    {
                        continue;
                    }

                    versions.Add(new PluginVersion
                    {
                        Version = Str(v, "version") ?? "",
                        Platforms = Strings(v, "platforms"),
                    });
                }
            }

            list.Add(new PluginSearchResult
            {
                Reference = Str(obj, "reference") ?? "",
                FullReference = Str(obj, "fullReference", "full_reference") ?? "",
                PluginType = Str(obj, "pluginType", "plugin_type") ?? "",
                Kind = Str(obj, "kind") ?? "",
                Versions = versions,
            });
        }

        return list;
    }

    internal static ResolvedPlugin ParseResolvedPlugin(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new ResolvedPlugin
        {
            Reference = Str(node, "reference") ?? "",
            Kind = Str(node, "kind") ?? "",
            PluginType = Str(node, "pluginType", "plugin_type") ?? "",
            Version = Str(node, "version") ?? "",
            TargetTriple = Str(node, "targetTriple", "target_triple"),
            SdkVersion = Str(node, "sdkVersion", "sdk_version"),
            CoreVersion = Str(node, "coreVersion", "core_version"),
            LibVersion = Str(node, "libVersion", "lib_version"),
        };
    }

    internal static InstalledPlugin ParseInstalledPlugin(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new InstalledPlugin
        {
            Reference = Str(node, "reference") ?? "",
            Kind = Str(node, "kind") ?? "",
            PluginType = Str(node, "pluginType", "plugin_type") ?? "",
            Version = Str(node, "version") ?? "",
            Path = Str(node, "path") ?? "",
            Verification = ParseVerification(Str(node, "verification")),
            Loaded = node["loaded"]?.GetValue<bool>() ?? false,
        };
    }

    internal static PulledPlugin ParsePulledPlugin(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new PulledPlugin
        {
            Reference = Str(node, "reference") ?? "",
            Path = Str(node, "path") ?? "",
            Verification = ParseVerification(Str(node, "verification")),
        };
    }

    internal static IReadOnlyList<LockedPlugin> ParseLockedPlugins(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            return [];
        }

        var list = new List<LockedPlugin>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new LockedPlugin
            {
                Reference = Str(obj, "reference") ?? "",
                Version = Str(obj, "version") ?? "",
                Digest = Str(obj, "digest") ?? "",
                Filename = Str(obj, "filename") ?? "",
                Platform = Str(obj, "platform") ?? "",
                FileHash = Str(obj, "file_hash", "fileHash"),
                SdkVersion = Str(obj, "sdk_version", "sdkVersion"),
                CoreVersion = Str(obj, "core_version", "coreVersion"),
                LibVersion = Str(obj, "lib_version", "libVersion"),
            });
        }

        return list;
    }

    internal static ConfigSchema ParseConfigSchema(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new ConfigSchema
        {
            Name = Str(node, "name") ?? "",
            Schema = node["schema"] as JsonObject ?? [],
        };
    }

    internal static SourceSchema? ParseSourceSchema(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject node)
        {
            return null;
        }

        return new SourceSchema
        {
            Nodes = ParseNodeSchemas(node["nodes"]),
            Relations = ParseRelationSchemas(node["relations"]),
        };
    }

    internal static GraphSchema ParseGraphSchema(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new DrasiException(DrasiErrorCodes.EngineFailure, "GetGraphSchema returned a non-object");
        return new GraphSchema
        {
            Nodes = node["nodes"] as JsonObject ?? [],
            Relations = node["relations"] as JsonObject ?? [],
            SourcesWithoutSchema = Strings(node, "sources_without_schema", "sourcesWithoutSchema"),
        };
    }

    private static IReadOnlyList<NodeSchema> ParseNodeSchemas(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var list = new List<NodeSchema>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new NodeSchema
            {
                Label = Str(obj, "label") ?? "",
                Properties = ParsePropertySchemas(obj["properties"]),
            });
        }

        return list;
    }

    private static IReadOnlyList<RelationSchema> ParseRelationSchemas(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var list = new List<RelationSchema>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new RelationSchema
            {
                Label = Str(obj, "label") ?? "",
                To = Str(obj, "to"),
                Properties = ParsePropertySchemas(obj["properties"]),
            });
        }

        return list;
    }

    private static IReadOnlyList<PropertySchema> ParsePropertySchemas(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var list = new List<PropertySchema>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new PropertySchema
            {
                Name = Str(obj, "name") ?? "",
                DataType = Str(obj, "data_type", "dataType"),
                Description = Str(obj, "description"),
            });
        }

        return list;
    }

    internal static LogMessage ParseLogMessage(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new LogMessage
        {
            Timestamp = ParseTimestamp(node["timestamp"]),
            Level = Str(node, "level") ?? "",
            Message = Str(node, "message") ?? "",
            InstanceId = Str(node, "instance_id", "instanceId"),
            ComponentId = Str(node, "component_id", "componentId"),
            ComponentType = Str(node, "component_type", "componentType"),
        };
    }

    private static PluginVerification ParseVerification(string? value) => value switch
    {
        "verified" => PluginVerification.Verified,
        "tampered" => PluginVerification.Tampered,
        _ => PluginVerification.Unsigned,
    };

    private static string? Str(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (obj[name] is not null and not JsonValue)
            {
                return obj[name]!.ToString();
            }
        }

        return null;
    }

    private static string[] Strings(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj[name] is JsonArray array)
            {
                return array.Select(item => item?.GetValue<string>() ?? item?.ToString() ?? "").ToArray();
            }
        }

        return [];
    }

    private static string ToJson(SourceChange change)
    {
        var op = change.Op switch
        {
            ChangeOp.Update => "update",
            ChangeOp.Delete => "delete",
            _ => "insert",
        };
        var node = new JsonObject
        {
            ["op"] = op,
            ["id"] = change.Id,
        };
        if (change.Labels is { Count: > 0 } labels)
        {
            var array = new JsonArray();
            foreach (var label in labels)
            {
                AddString(array, label);
            }

            node["labels"] = array;
        }

        if (change.Properties is not null)
        {
            node["properties"] = change.Properties.DeepClone();
        }

        if (change.StartId is not null)
        {
            node["startId"] = change.StartId;
        }

        if (change.EndId is not null)
        {
            node["endId"] = change.EndId;
        }

        if (change.EffectiveFrom is { } effectiveFrom)
        {
            node["effectiveFrom"] = effectiveFrom;
        }

        return node.ToJsonString();
    }

    private static IReadOnlyList<JsonObject> ParseObjectArray(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            return [];
        }

        var rows = new List<JsonObject>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                rows.Add(obj);
            }
        }

        return rows;
    }

    private static IReadOnlyList<ComponentInfo> ParseComponentList(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array)
        {
            return [];
        }

        var list = new List<ComponentInfo>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new ComponentInfo
            {
                Id = obj["id"]?.GetValue<string>() ?? "",
                Status = ParseStatus(obj["status"]?.GetValue<string>()),
            });
        }

        return list;
    }

    private static ComponentStatus ParseStatus(string? status) => status switch
    {
        "Added" => ComponentStatus.Added,
        "Starting" => ComponentStatus.Starting,
        "Running" => ComponentStatus.Running,
        "Stopping" => ComponentStatus.Stopping,
        "Stopped" => ComponentStatus.Stopped,
        "Removed" => ComponentStatus.Removed,
        "Reconfiguring" => ComponentStatus.Reconfiguring,
        "Error" => ComponentStatus.Error,
        _ => ComponentStatus.Unknown,
    };

    private static QueryResultEvent ParseQueryResult(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        var results = new List<QueryDiff>();
        if (node["results"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject diff)
                {
                    results.Add(ParseDiff(diff));
                }
            }
        }

        return new QueryResultEvent
        {
            QueryId = node["query_id"]?.GetValue<string>() ?? "",
            Sequence = node["sequence"]?.GetValue<ulong>() ?? 0,
            Timestamp = ParseTimestamp(node["timestamp"]),
            Results = results,
            Metadata = node["metadata"] as JsonObject,
        };
    }

    private static QueryDiff ParseDiff(JsonObject diff)
    {
        var type = diff["type"]?.GetValue<string>();
        return new QueryDiff
        {
            Type = type switch
            {
                "ADD" => DiffType.Add,
                "UPDATE" => DiffType.Update,
                "DELETE" => DiffType.Delete,
                "aggregation" => DiffType.Aggregation,
                _ => DiffType.Noop,
            },
            Data = diff["data"] as JsonObject,
            Before = diff["before"] as JsonObject,
            After = diff["after"] as JsonObject,
            GroupingKeys = (diff["grouping_keys"] as JsonArray)?
                .Select(item => item?.GetValue<string>() ?? "")
                .ToArray(),
        };
    }

    private static ComponentEvent ParseComponentEvent(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new ComponentEvent
        {
            ComponentId = node["component_id"]?.GetValue<string>()
                ?? node["componentId"]?.GetValue<string>()
                ?? "",
            ComponentType = node["component_type"]?.ToString(),
            Status = ParseStatus(node["status"]?.GetValue<string>() ?? node["status"]?.ToString()),
            Timestamp = ParseTimestamp(node["timestamp"]),
            Message = node["message"]?.GetValue<string>(),
        };
    }

    private static DateTimeOffset ParseTimestamp(JsonNode? node)
    {
        if (node is null)
        {
            return DateTimeOffset.UtcNow;
        }

        try
        {
            return node.GetValue<DateTimeOffset>();
        }
        catch (Exception)
        {
            return DateTimeOffset.TryParse(node.ToString(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
        }
    }

    private static QueryMetrics ParseQueryMetrics(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new QueryMetrics
        {
            OutboxSize = U64(node, "outboxSize"),
            OutboxEarliestSeq = U64(node, "outboxEarliestSeq"),
            OutboxLatestSeq = U64(node, "outboxLatestSeq"),
            ResultSeqAdvances = U64(node, "resultSeqAdvances"),
            LiveResultsCount = U64(node, "liveResultsCount"),
            OuterTransactionDurationNsLast = U64(node, "outerTransactionDurationNsLast"),
            OuterTransactionDurationNsMax = U64(node, "outerTransactionDurationNsMax"),
            SnapshotFetchCount = U64(node, "snapshotFetchCount"),
        };
    }

    private static IReadOnlyDictionary<string, ReactionQueryMetrics> ParseReactionMetrics(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        var map = new Dictionary<string, ReactionQueryMetrics>(StringComparer.Ordinal);
        foreach (var (key, value) in node)
        {
            if (value is not JsonObject obj)
            {
                continue;
            }

            map[key] = new ReactionQueryMetrics
            {
                CheckpointSequence = U64(obj, "checkpointSequence"),
                CheckpointLag = U64(obj, "checkpointLag"),
                DedupSkipCount = U64(obj, "dedupSkipCount"),
                GapDetectionCount = U64(obj, "gapDetectionCount"),
                RecoveryStrictCount = U64(obj, "recoveryStrictCount"),
                RecoveryAutoResetCount = U64(obj, "recoveryAutoResetCount"),
                RecoveryAutoSkipGapCount = U64(obj, "recoveryAutoSkipGapCount"),
                FetchSnapshotCount = U64(obj, "fetchSnapshotCount"),
                FetchOutboxCount = U64(obj, "fetchOutboxCount"),
            };
        }

        return map;
    }

    private static LifecycleMetrics ParseLifecycleMetrics(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        return new LifecycleMetrics
        {
            StartupRejectionDurableNoStore = U64(node, "startupRejectionDurableNoStore"),
            StartupRejectionDurableOnVolatile = U64(node, "startupRejectionDurableOnVolatile"),
            StartupRejectionSnapshotSkipGap = U64(node, "startupRejectionSnapshotSkipGap"),
            StartupRejectionNoSnapshotAutoReset = U64(node, "startupRejectionNoSnapshotAutoReset"),
            AutoResetCompletions = U64(node, "autoResetCompletions"),
            HashMismatchCount = U64(node, "hashMismatchCount"),
        };
    }

    private static ulong U64(JsonObject node, string name)
    {
        var value = node[name];
        if (value is null)
        {
            return 0;
        }

        try
        {
            return value.GetValue<ulong>();
        }
        catch (Exception)
        {
            return value.GetValue<long>() is var n and >= 0 ? (ulong)n : 0;
        }
    }

    private static bool TryLagged(string json, out ulong dropped)
    {
        dropped = 0;
        if (JsonNode.Parse(json) is not JsonObject obj)
        {
            return false;
        }

        if (obj["lagged"] is null)
        {
            return false;
        }

        dropped = U64(obj, "lagged");
        return true;
    }

    private void EnsureNotDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static int OnNativeCallback(nint json, nint userData)
    {
        try
        {
            var payload = Marshal.PtrToStringUTF8(json);
            if (payload is null)
            {
                return 0;
            }

            var handle = GCHandle.FromIntPtr(userData);
            switch (handle.Target)
            {
                case Action<string> callback:
                    callback(payload);
                    return 0;
                case Func<string, Task> asyncCallback:
                    asyncCallback(payload).GetAwaiter().GetResult();
                    return 0;
                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Logging.Logger?.LogError(ex, "Drasi callback failed");
            return -1;
        }
    }
}
