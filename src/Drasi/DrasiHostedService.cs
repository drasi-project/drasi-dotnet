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

using Microsoft.Extensions.Hosting;

namespace Drasi.DependencyInjection;

/// <summary>
/// Starts the registered <see cref="Engine"/>, applies <see cref="DrasiBuilder"/>
/// topology, and stops the engine with the generic host. Register via
/// <c>AddDrasi</c>. The host disposes the engine singleton after
/// <see cref="StopAsync"/>.
/// </summary>
public sealed class DrasiHostedService : IHostedService
{
    private readonly Engine _engine;
    private readonly DrasiBuilder _builder;

    public DrasiHostedService(Engine engine, DrasiBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(builder);
        _engine = engine;
        _builder = builder;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _builder.ApplyAsync(_engine, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _engine.ShutdownAsync(cancellationToken);
}
