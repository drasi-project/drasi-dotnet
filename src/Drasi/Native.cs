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

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_create(string id);

    [LibraryImport(LibraryName)]
    internal static partial void drasi_engine_destroy(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial int drasi_engine_start(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial int drasi_engine_shutdown(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_source(nint engine, string id);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_query(
        nint engine,
        string id,
        string query,
        string sourcesJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_add_reaction(
        nint engine,
        string id,
        string queryIdsJson,
        nint callback,
        nint userData);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_push_change(
        nint engine,
        string sourceId,
        string changeJson);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint drasi_engine_get_query_results(nint engine, string queryId);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int drasi_engine_wait_for_query(
        nint engine,
        string queryId,
        double timeoutSeconds);

    [LibraryImport(LibraryName)]
    internal static partial void drasi_string_free(nint value);

    [LibraryImport(LibraryName)]
    internal static partial nint drasi_last_error();

    internal static string? LastError() => Marshal.PtrToStringUTF8(drasi_last_error());

    internal static void ThrowIfError(int status, string operation)
    {
        if (status == 0)
        {
            return;
        }

        throw new DrasiException($"{operation} failed: {LastError() ?? "unknown native error"}");
    }

    internal static nint ThrowIfNull(nint handle, string operation)
    {
        if (handle != nint.Zero)
        {
            return handle;
        }

        throw new DrasiException($"{operation} failed: {LastError() ?? "unknown native error"}");
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

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            yield return Path.Combine(dir.FullName, "native", "target", "release", fileName);
            yield return Path.Combine(dir.FullName, "native", "target", "debug", fileName);
            dir = dir.Parent;
        }
    }
}
