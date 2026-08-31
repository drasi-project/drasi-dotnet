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
using System.Text.Json.Serialization;

namespace Drasi;

/// <summary>
/// A graph change pushed into a C#-defined source.
/// <c>id</c> is the graph key; a query selecting <c>o.id</c> reads a property
/// of that name, so emit it in <see cref="Properties"/> as well.
/// </summary>
public sealed class SourceChange
{
    [JsonPropertyName("op")]
    public required string Op { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("labels")]
    public IReadOnlyList<string>? Labels { get; init; }

    [JsonPropertyName("properties")]
    public JsonObject? Properties { get; init; }

    [JsonPropertyName("startId")]
    public string? StartId { get; init; }

    [JsonPropertyName("endId")]
    public string? EndId { get; init; }

    [JsonPropertyName("effectiveFrom")]
    public long? EffectiveFrom { get; init; }
}
