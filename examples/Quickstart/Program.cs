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
using Drasi;

Environment.SetEnvironmentVariable("RUST_LOG", Environment.GetEnvironmentVariable("RUST_LOG") ?? "warn");

const string OpenOrders = "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total";

await using var drasi = await Engine.CreateAsync("dotnet-demo");
await drasi.StartAsync();
await drasi.AddCsharpSourceAsync("orders");
await drasi.AddQueryAsync("open", OpenOrders, ["orders"]);
await drasi.WaitForQueryAsync("open");

var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await drasi.AddCsharpReactionAsync("watch", ["open"], evt =>
{
    if (evt["results"] is not JsonArray results)
    {
        return;
    }

    foreach (var diff in results)
    {
        var type = diff?["type"]?.GetValue<string>();
        Console.WriteLine($"  {type} {diff?["data"]}");
        seen.TrySetResult();
    }
});

await drasi.PushChangeAsync("orders", new SourceChange
{
    Op = "insert",
    Id = "o1",
    Labels = ["Order"],
    Properties = new JsonObject
    {
        ["id"] = "o1",
        ["status"] = "open",
        ["total"] = 42,
    },
});

await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"open orders: {await drasi.GetQueryResultsAsync("open")}");
