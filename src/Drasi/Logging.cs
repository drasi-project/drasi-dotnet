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
using Microsoft.Extensions.Logging;

namespace Drasi;

/// <summary>
/// Bridges native <c>tracing</c> / <c>log</c> events into
/// <see cref="ILogger"/>. One sink per process: the last configured factory
/// wins, which matches a typical host with a single logging pipeline.
/// </summary>
internal static class Logging
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeLogCallback(int level, nint target, nint message, nint userData);

    private static readonly NativeLogCallback Callback = OnNativeLog;
    private static readonly nint CallbackPtr = Marshal.GetFunctionPointerForDelegate(Callback);

    private static ILoggerFactory? _factory;
    private static ILogger? _fallback;

    /// <summary>Logger for managed-side diagnostics (category <c>Drasi</c>).</summary>
    internal static ILogger? Logger => _fallback;

    internal static void Configure(EngineOptions? options)
    {
        var factory = options?.LoggerFactory;
        var logger = options?.Logger
            ?? factory?.CreateLogger("Drasi");

        _factory = factory;
        _fallback = logger;

        Native.EnsureLoaded();
        if (logger is null && factory is null)
        {
            Native.drasi_set_log_callback(nint.Zero, nint.Zero);
            return;
        }

        Native.drasi_set_log_callback(CallbackPtr, nint.Zero);
        Native.drasi_set_log_filter(FilterFor(logger ?? factory!.CreateLogger("Drasi")));
    }

    private static string FilterFor(ILogger logger)
    {
        if (logger.IsEnabled(LogLevel.Trace))
        {
            return "trace";
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            return "debug";
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            return "info";
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            return "warn";
        }

        if (logger.IsEnabled(LogLevel.Error))
        {
            return "error";
        }

        return "off";
    }

    private static void OnNativeLog(int level, nint target, nint message, nint userData)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(message) ?? "";
            var rustTarget = Marshal.PtrToStringUTF8(target) ?? "native";
            var category = CategoryFor(rustTarget);
            var logger = (ILogger?)_factory?.CreateLogger(category) ?? _fallback;
            if (logger is null)
            {
                return;
            }

            var logLevel = level switch
            {
                0 => LogLevel.Trace,
                1 => LogLevel.Debug,
                2 => LogLevel.Information,
                3 => LogLevel.Warning,
                4 => LogLevel.Error,
                _ => LogLevel.Information,
            };
            logger.Log(logLevel, "{Target}: {Message}", rustTarget, text);
        }
        catch (Exception)
        {
            // Never throw back into native.
        }
    }

    private static string CategoryFor(string rustTarget)
    {
        var trimmed = rustTarget.Replace("::", ".", StringComparison.Ordinal);
        return string.IsNullOrEmpty(trimmed) ? "Drasi.Native" : "Drasi." + trimmed;
    }
}
