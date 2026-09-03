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

namespace Drasi.DependencyInjection;

/// <summary>
/// Startup topology for an <see cref="Engine"/> registered with
/// <c>AddDrasi</c>. The host applies this when it starts.
/// </summary>
public sealed class DrasiBuilder
{
    private readonly List<Func<Engine, CancellationToken, Task>> _plugins = [];
    private readonly List<Func<Engine, CancellationToken, Task>> _sources = [];
    private readonly List<QueryRegistration> _queries = [];
    private readonly List<Func<Engine, CancellationToken, Task>> _reactions = [];
    private readonly List<(string SourceId, SourceChange Change)> _seeds = [];

    public DrasiBuilder(string engineId)
    {
        ArgumentException.ThrowIfNullOrEmpty(engineId);
        EngineId = engineId;
    }

    public string EngineId { get; }

    public EngineOptions Options { get; } = new();

    public DrasiBuilder LoadPlugins(
        string directory,
        IReadOnlyDictionary<string, string>? verify = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _plugins.Add((engine, ct) => engine.LoadPluginsAsync(directory, verify, ct));
        return this;
    }

    public DrasiBuilder WatchPlugins(string directory, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _plugins.Add((engine, ct) => engine.WatchPluginsAsync(directory, debounce, ct));
        return this;
    }

    public DrasiBuilder InstallPlugin(
        string reference,
        string? directory = null,
        bool verify = false,
        bool requireSigned = false,
        bool load = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        _plugins.Add((engine, ct) => engine.InstallPluginAsync(
            reference, directory, verify, requireSigned, trustedIdentities: null, load, ct));
        return this;
    }

    public DrasiBuilder UseSecretStore(string kind, JsonObject? config = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        _plugins.Add((engine, ct) => engine.UseSecretStoreAsync(kind, config, ct));
        return this;
    }

    public DrasiBuilder Configure(Action<EngineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Options);
        return this;
    }

    public DrasiBuilder AddSource(string id, bool autoStart = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        _sources.Add((engine, ct) => engine.AddSourceAsync(id, autoStart, ct));
        return this;
    }

    public DrasiBuilder AddSource(
        string kind,
        string id,
        JsonObject? config = null,
        bool autoStart = true,
        JsonObject? bootstrap = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        _sources.Add((engine, ct) =>
            engine.AddSourceAsync(kind, id, config, autoStart, bootstrap, ct));
        return this;
    }

    public DrasiBuilder AddQuery(
        string id,
        string query,
        IReadOnlyList<string> sources,
        QueryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(sources);
        _queries.Add(new QueryRegistration(id, options, (engine, ct) =>
            engine.AddQueryAsync(id, query, sources, options, ct)));
        return this;
    }

    public DrasiBuilder AddReaction(
        string id,
        IReadOnlyList<string> queryIds,
        Action<QueryResultEvent> callback,
        bool autoStart = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        ArgumentNullException.ThrowIfNull(callback);
        _reactions.Add((engine, ct) =>
            engine.AddReactionAsync(id, queryIds, callback, autoStart, ct));
        return this;
    }

    public DrasiBuilder AddReaction(
        string kind,
        string id,
        IReadOnlyList<string> queryIds,
        JsonObject? config = null,
        bool autoStart = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        _reactions.Add((engine, ct) =>
            engine.AddReactionAsync(kind, id, queryIds, config, autoStart, ct));
        return this;
    }

    public DrasiBuilder AddDurableReaction(
        string id,
        IReadOnlyList<string> queryIds,
        Func<QueryResultEvent, Task> callback,
        RecoveryPolicy recovery = RecoveryPolicy.Strict)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        ArgumentNullException.ThrowIfNull(callback);
        _reactions.Add((engine, ct) =>
            engine.AddDurableReactionAsync(id, queryIds, callback, recovery, ct));
        return this;
    }

    /// <summary>
    /// Pushes a change after the host has started sources, queries, and reactions.
    /// </summary>
    public DrasiBuilder Seed(string sourceId, SourceChange change)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentNullException.ThrowIfNull(change);
        _seeds.Add((sourceId, change));
        return this;
    }

    internal async Task ApplyAsync(Engine engine, CancellationToken cancellationToken)
    {
        foreach (var add in _plugins)
        {
            await add(engine, cancellationToken).ConfigureAwait(false);
        }

        if (!await engine.IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var add in _sources)
        {
            await add(engine, cancellationToken).ConfigureAwait(false);
        }

        foreach (var query in _queries)
        {
            await query.Add(engine, cancellationToken).ConfigureAwait(false);
            if (query.Options?.AutoStart == false)
            {
                continue;
            }

            await engine.WaitForQueryAsync(query.Id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var add in _reactions)
        {
            await add(engine, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (sourceId, change) in _seeds)
        {
            await engine.PushChangeAsync(sourceId, change, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record QueryRegistration(
        string Id,
        QueryOptions? Options,
        Func<Engine, CancellationToken, Task> Add);
}
