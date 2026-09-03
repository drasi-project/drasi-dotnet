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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Drasi.DependencyInjection;

/// <summary>
/// Reports healthy when the registered <see cref="Engine"/> is running.
/// </summary>
public sealed class DrasiHealthCheck : IHealthCheck
{
    private readonly Engine _engine;

    public DrasiHealthCheck(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _engine.IsRunningAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy("Drasi engine is running")
                : HealthCheckResult.Unhealthy("Drasi engine is not running");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Drasi engine health check failed", ex);
        }
    }
}

/// <summary>Registers <see cref="DrasiHealthCheck"/>.</summary>
public static class DrasiHealthCheckExtensions
{
    public static IHealthChecksBuilder AddDrasiCheck(
        this IHealthChecksBuilder builder,
        string name = "drasi")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<DrasiHealthCheck>(name);
    }
}
