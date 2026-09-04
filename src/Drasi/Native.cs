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

using System.Reflection;
using System.Runtime.InteropServices;

namespace Drasi;

internal static partial class Native
{
    private const string LibraryName = "drasi_ffi";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
    }

    internal static void EnsureLoaded()
    {
        // Force the static constructor so the resolver is registered before
        // the first LibraryImport call.
    }

    [LibraryImport(LibraryName)]
    internal static partial void drasi_set_log_callback(nint callback, nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void drasi_set_log_filter(string? filter);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_create(string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_create_with_options(string id, string? optionsJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_from_config(string configJson);

    [LibraryImport(LibraryName)]
    internal static partial void drasi_engine_destroy(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial int drasi_engine_start(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial int drasi_engine_stop(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial int drasi_engine_shutdown(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial int drasi_engine_is_running(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_source(nint engine, string id, int autoStart);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_remove_source(nint engine, string id, int cleanup);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_start_source(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_stop_source(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_source_status(nint engine, string id);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_list_sources(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_query(
        nint engine,
        string id,
        string query,
        string sourcesJson,
        string? optionsJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_update_query(
        nint engine,
        string id,
        string query,
        string sourcesJson,
        string? optionsJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_remove_query(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_start_query(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_stop_query(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_reaction(
        nint engine,
        string id,
        string queryIdsJson,
        nint callback,
        nint userData,
        int autoStart);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_remove_reaction(nint engine, string id, int cleanup);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_start_reaction(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_stop_reaction(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_reaction_status(nint engine, string id);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_list_reactions(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_push_change(
        nint engine,
        string sourceId,
        string changeJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_get_query_results(nint engine, string queryId);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_query_status(nint engine, string queryId);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_list_queries(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_wait_for_query(
        nint engine,
        string queryId,
        double timeoutSeconds);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_query_metrics(nint engine, string queryId);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_reaction_metrics(nint engine, string reactionId);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_lifecycle_metrics(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_source_schema(nint engine, string id);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_graph_schema(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_subscribe_query_events(
        nint engine,
        string id,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_subscribe_source_events(
        nint engine,
        string id,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_subscribe_reaction_events(
        nint engine,
        string id,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_subscribe_all_events(
        nint engine,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_subscribe_query_logs(
        nint engine,
        string id,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_subscribe_source_logs(
        nint engine,
        string id,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_subscribe_reaction_logs(
        nint engine,
        string id,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName)]
    internal static partial void drasi_stream_close(nint stream);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_durable_reaction(
        nint engine, string id, string queryIdsJson, nint callback, nint userData, string recoveryPolicy);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_plugin_source(
        nint engine, string kind, string id, string configJson, int autoStart, string? bootstrapJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_plugin_reaction(
        nint engine, string kind, string id, string queryIdsJson, string configJson, int autoStart);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_update_plugin_source(
        nint engine, string kind, string id, string configJson, int autoStart);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_update_plugin_reaction(
        nint engine, string kind, string id, string queryIdsJson, string configJson, int autoStart);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_load_plugins(nint engine, string directory, string? verifyJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_watch_plugins(nint engine, string directory, double debounceSeconds);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_engine_plugin_kinds(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_host_info();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_search_plugins(string? query);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_list_plugin_tags(string repository);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_resolve_plugin(string reference);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_install_plugin(nint engine, string reference, string? optionsJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_pull_plugin(string reference, string directory, string filename, string? optionsJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_write_lockfile(nint engine, string directory);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_read_lockfile(string directory);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_install_from_lockfile(nint engine, string directory, int load);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_source_config_schema(nint engine, string kind);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_reaction_config_schema(nint engine, string kind);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_bootstrap_config_schema(nint engine, string kind);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_secret_store_config_schema(nint engine, string kind);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_use_secret_store(nint engine, string kind, string configJson);

    [LibraryImport(LibraryName)]
    internal static partial void drasi_string_free(nint value);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_last_error();

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_last_error_code();

    internal static void ThrowIfError(int status, string operation)
    {
        if (status == 0)
        {
            return;
        }

        throw LastException(operation);
    }

    internal static nint ThrowIfNull(nint handle, string operation)
    {
        if (handle != nint.Zero)
        {
            return handle;
        }

        throw LastException(operation);
    }

    internal static string TakeString(nint ptr, string operation)
    {
        if (ptr == nint.Zero)
        {
            throw LastException(operation);
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr)
                ?? throw LastException(operation);
        }
        finally
        {
            drasi_string_free(ptr);
        }
    }

    internal static DrasiException LastException(string operation)
    {
        var code = Marshal.PtrToStringUTF8(drasi_last_error_code()) ?? DrasiErrorCodes.EngineFailure;
        var message = Marshal.PtrToStringUTF8(drasi_last_error()) ?? "unknown native error";
        return DrasiException.FromCode(code, $"{operation} failed: {message}");
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        var fileName = OperatingSystem.IsWindows()
            ? "drasi_ffi.dll"
            : OperatingSystem.IsMacOS()
                ? "libdrasi_ffi.dylib"
                : "libdrasi_ffi.so";

        foreach (var candidate in CandidatePaths(fileName))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallback)
            ? fallback
            : nint.Zero;
    }

    private static IEnumerable<string> CandidatePaths(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, fileName);

        foreach (var rid in RidFallbacks())
        {
            yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        }

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "native", "target", "release", fileName);
            yield return Path.Combine(dir.FullName, "native", "target", "debug", fileName);
            dir = dir.Parent;
        }
    }

    private static IEnumerable<string> RidFallbacks()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        yield return rid;

        var dash = rid.IndexOf('-');
        if (dash > 0)
        {
            var os = rid[..dash];
            var arch = rid[(dash + 1)..];
            var osName = os.Split('.')[0];
            yield return $"{osName}-{arch}";
        }
    }
}
