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
using Microsoft.Extensions.Logging;

using var logs = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Warning)
        .AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
});

const string OpenOrders = "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total";

await using var drasi = await Engine.CreateAsync("dotnet-demo", new EngineOptions
{
    LoggerFactory = logs,
});
await drasi.StartAsync();
await drasi.AddSourceAsync("orders");
await drasi.AddQueryAsync("open", OpenOrders, ["orders"]);
await drasi.WaitForQueryAsync("open");

var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await drasi.AddReactionAsync("watch", ["open"], evt =>
{
    foreach (var diff in evt.Results)
    {
        Console.WriteLine($"  {diff.Type} {diff.Data}");
        seen.TrySetResult();
    }
});

await drasi.PushChangeAsync("orders", new SourceChange
{
    Op = ChangeOp.Insert,
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
var rows = await drasi.GetQueryResultsAsync("open");
Console.WriteLine($"open orders: [{string.Join(",", rows.Select(row => row.ToJsonString()))}]");
