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

namespace Drasi.Tutorials.CurbsidePickup;

sealed class SqlLog
{
    private readonly List<JsonObject> _entries = [];
    private readonly object _gate = new();

    public void Add(string db, string text)
    {
        lock (_gate)
        {
            _entries.Add(new JsonObject
            {
                ["db"] = db,
                ["text"] = text,
                ["t"] = DateTime.UtcNow.ToString("O"),
            });
            while (_entries.Count > 25)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    public JsonArray ToJson()
    {
        lock (_gate)
        {
            var array = new JsonArray();
            foreach (var entry in _entries)
            {
                array.Add(entry.DeepClone());
            }

            return array;
        }
    }
}
