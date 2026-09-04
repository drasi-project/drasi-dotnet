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
using Drasi.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string OpenOrders =
    "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total";

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddDrasi("hosted-demo", drasi =>
{
    drasi.AddSource("orders");
    drasi.AddQuery("open", OpenOrders, ["orders"]);
    drasi.AddReaction("watch", ["open"], evt =>
    {
        foreach (var diff in evt.Results)
        {
            Console.WriteLine($"  {diff.Type} {diff.Data}");
        }
    });
    drasi.Seed("orders", new SourceChange
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
});

await builder.Build().RunAsync();
