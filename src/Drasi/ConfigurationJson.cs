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

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Drasi;

internal static class ConfigurationJson
{
    public static JsonObject ToObject(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var obj = new JsonObject();
        foreach (var child in config.GetChildren())
        {
            obj[child.Key] = ToNode(child);
        }

        return obj;
    }

    private static JsonNode? ToNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return Coerce(section.Value);
        }

        if (children.All(child => int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(child => int.Parse(child.Key, CultureInfo.InvariantCulture)))
            {
                array.Add(ToNode(child));
            }

            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = ToNode(child);
        }

        return obj;
    }

    private static JsonNode? Coerce(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var flag))
        {
            return JsonValue.Create(flag);
        }

        return JsonValue.Create(value);
    }
}
