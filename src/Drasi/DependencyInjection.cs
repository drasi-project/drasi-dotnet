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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Drasi.DependencyInjection;

/// <summary>
/// Optional <c>Microsoft.Extensions.DependencyInjection</c> helpers.
/// The core API does not require DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="Engine"/> as a singleton and a hosted service that
    /// starts it with the generic host, applies the configured topology, and
    /// stops it on shutdown. Picks up <see cref="ILoggerFactory"/> from the
    /// container.
    /// </summary>
    public static IServiceCollection AddDrasi(
        this IServiceCollection services,
        string engineId,
        Action<DrasiBuilder>? configure = null)
    {
        return AddDrasiCore(services, engineId, configure, configureWithServices: null, fromConfig: null);
    }

    /// <inheritdoc cref="AddDrasi(IServiceCollection, string, Action{DrasiBuilder}?)"/>
    public static IServiceCollection AddDrasi(
        this IServiceCollection services,
        string engineId,
        Action<DrasiBuilder, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddDrasiCore(services, engineId, configure: null, configure, fromConfig: null);
    }

    /// <summary>
    /// Builds the engine from an <see cref="IConfiguration"/> section (typically
    /// <c>Drasi</c> in appsettings) and still applies any extra
    /// <see cref="DrasiBuilder"/> topology (C# reactions, seeds).
    /// </summary>
    public static IServiceCollection AddDrasi(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DrasiBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var json = ConfigurationJson.ToObject(configuration);
        var id = json["id"]?.GetValue<string>() ?? "drasi";
        return AddDrasiCore(services, id, configure, configureWithServices: null, json);
    }

    private static IServiceCollection AddDrasiCore(
        IServiceCollection services,
        string engineId,
        Action<DrasiBuilder>? configure,
        Action<DrasiBuilder, IServiceProvider>? configureWithServices,
        JsonObject? fromConfig)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(engineId);
        if (services.Any(d => d.ServiceType == typeof(DrasiBuilder)))
        {
            throw new InvalidOperationException("AddDrasi has already been called on this collection.");
        }

        services.AddSingleton(sp =>
        {
            var builder = new DrasiBuilder(engineId);
            configure?.Invoke(builder);
            configureWithServices?.Invoke(builder, sp);
            builder.Options.LoggerFactory ??= sp.GetService<ILoggerFactory>();
            return builder;
        });
        services.AddSingleton(sp =>
        {
            var builder = sp.GetRequiredService<DrasiBuilder>();
            return (fromConfig is null
                    ? Engine.CreateAsync(builder.EngineId, builder.Options)
                    : Engine.FromConfigAsync(fromConfig, builder.Options))
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        });
        services.AddHostedService<DrasiHostedService>();
        return services;
    }
}
