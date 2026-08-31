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

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Drasi;

/// <summary>
/// An in-process Drasi continuous-query engine.
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReactionNativeCallback(nint json, nint userData);

    // Keep the delegate alive for the process; native holds the function pointer.
    private static readonly ReactionNativeCallback NativeCallback = OnReaction;
    private static readonly nint NativeCallbackPtr = Marshal.GetFunctionPointerForDelegate(NativeCallback);

    private readonly nint _handle;
    private readonly string _id;
    private readonly List<GCHandle> _callbackHandles = [];
    private readonly object _gate = new();
    private bool _disposed;

    private Engine(nint handle, string id)
    {
        _handle = handle;
        _id = id;
    }

    /// <summary>The engine identifier supplied to <see cref="CreateAsync"/>.</summary>
    public string Id => _id;

    /// <summary>Builds an engine. It is not started until <see cref="StartAsync"/>.</summary>
    public static Task<Engine> CreateAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Native.EnsureLoaded();
        return RunAsync(
            () =>
            {
                var handle = Native.ThrowIfNull(Native.drasi_engine_create(id), "Create");
                return new Engine(handle, id);
            },
            cancellationToken);
    }

    /// <summary>Starts the engine and every component configured to auto-start.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => Native.ThrowIfError(Native.drasi_engine_start(handle), "Start"),
            cancellationToken);
    }

    /// <summary>Registers a source that you push changes into with <see cref="PushChangeAsync"/>.</summary>
    public Task AddCsharpSourceAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () => Native.ThrowIfError(Native.drasi_engine_add_source(handle, id), "AddCsharpSource"),
            cancellationToken);
    }

    /// <summary>Registers a continuous Cypher query over one or more sources.</summary>
    public Task AddQueryAsync(
        string id,
        string query,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(query);
        ArgumentNullException.ThrowIfNull(sources);
        EnsureNotDisposed();
        var handle = _handle;
        var sourcesJson = ToJsonArray(sources);
        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_add_query(handle, id, query, sourcesJson),
                "AddQuery"),
            cancellationToken);
    }

    /// <summary>
    /// Registers a reaction that invokes <paramref name="callback"/> with each
    /// query result as JSON (<c>query_id</c>, <c>results</c> diffs).
    /// </summary>
    public Task AddCsharpReactionAsync(
        string id,
        IReadOnlyList<string> queryIds,
        Action<JsonNode> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(queryIds);
        ArgumentNullException.ThrowIfNull(callback);
        EnsureNotDisposed();
        var handle = _handle;
        var queryIdsJson = ToJsonArray(queryIds);
        var gcHandle = GCHandle.Alloc(callback);
        lock (_gate)
        {
            _callbackHandles.Add(gcHandle);
        }

        return RunAsync(
            () => Native.ThrowIfError(
                Native.drasi_engine_add_reaction(
                    handle,
                    id,
                    queryIdsJson,
                    NativeCallbackPtr,
                    GCHandle.ToIntPtr(gcHandle)),
                "AddCsharpReaction"),
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

    /// <summary>The current result set of a query, as a JSON array of row objects.</summary>
    public Task<string> GetQueryResultsAsync(string queryId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryId);
        EnsureNotDisposed();
        var handle = _handle;
        return RunAsync(
            () =>
            {
                var ptr = Native.drasi_engine_get_query_results(handle, queryId);
                if (ptr == nint.Zero)
                {
                    throw new DrasiException($"GetQueryResults failed: {Native.LastError() ?? "unknown native error"}");
                }

                try
                {
                    return Marshal.PtrToStringUTF8(ptr)
                        ?? throw new DrasiException("GetQueryResults returned an empty string");
                }
                finally
                {
                    Native.drasi_string_free(ptr);
                }
            },
            cancellationToken);
    }

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

    public async ValueTask DisposeAsync()
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
        }

        try
        {
            await RunAsync(
                () => Native.ThrowIfError(Native.drasi_engine_shutdown(handle), "Shutdown"),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Native.drasi_engine_destroy(handle);
            foreach (var callback in callbacks)
            {
                if (callback.IsAllocated)
                {
                    callback.Free();
                }
            }
        }
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

    private static string ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array.ToJsonString();
    }

    private static string ToJson(SourceChange change)
    {
        var node = new JsonObject
        {
            ["op"] = change.Op,
            ["id"] = change.Id,
        };
        if (change.Labels is { Count: > 0 } labels)
        {
            var array = new JsonArray();
            foreach (var label in labels)
            {
                array.Add(label);
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

    private void EnsureNotDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static void OnReaction(nint json, nint userData)
    {
        try
        {
            var payload = Marshal.PtrToStringUTF8(json);
            if (payload is null)
            {
                return;
            }

            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not Action<JsonNode> callback)
            {
                return;
            }

            var node = JsonNode.Parse(payload);
            if (node is not null)
            {
                callback(node);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Drasi reaction callback failed: {ex}");
        }
    }
}
