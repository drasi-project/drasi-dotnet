# drasi-dotnet

[![docs](https://img.shields.io/badge/docs-drasi--project.github.io-blue)](https://drasi-project.github.io/drasi-dotnet/)

**Documentation: <https://drasi-project.github.io/drasi-dotnet/>**
Installation, concepts, the full API reference, guides, and runnable examples.

Embed the [Drasi](https://drasi.io) continuous-query engine in .NET.

A Rust `cdylib` hosts `drasi-lib` behind a small C ABI. C# binds it with
`[LibraryImport]` and wraps it in an idiomatic façade (`IAsyncDisposable`,
`IAsyncEnumerable`, typed errors). See the
[documentation site](https://drasi-project.github.io/drasi-dotnet/) for
installation, concepts, and the public API. The in-repo
[docs/api-reference.md](docs/api-reference.md) is a maintainer gap audit.

## Requirements

- Rust stable (see `rust-toolchain.toml`) to **build** the native library
- .NET 8+ to consume it (`net8.0` LTS is the API floor)

Consumers of the NuGet package do **not** need a Rust toolchain. Prebuilt
binaries ship for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, and
`osx-arm64`.

## Build native + run the sample

From the repo root:

```bash
cargo build --release --manifest-path native/Cargo.toml
dotnet run --project examples/Quickstart
```

Or:

```bash
./scripts/run-quickstart.sh
```

Download plugins from `ghcr.io` and use them as a source and a reaction
(needs network):

```bash
dotnet run --project examples/Plugins
```

Generic host + DI — configure Drasi at startup; the host starts it, applies
the topology, and stops it:

```bash
dotnet run --project examples/Hosted
```

The sample copies `libdrasi_ffi.dylib` / `.so` / `drasi_ffi.dll` next to the
app. The C# library also resolves the dylib from `native/target/{release,debug}`
so a plain `dotnet run` works after a cargo build.

Expected output looks like:

```text
  Add {"id":"o1","total":42}
open orders: [{"id":"o1","total":42}]
```

Both samples pass an `ILoggerFactory` (console). Quickstart logs at Warning;
the plugins sample uses Information so `reaction/log` output shows up. Native
logs no longer need `RUST_LOG` when a factory is set.

## Quickstart shape

```csharp
using var logs = LoggerFactory.Create(builder => builder.AddSimpleConsole());
await using var drasi = await Engine.CreateAsync("dotnet-demo", new EngineOptions
{
    LoggerFactory = logs,
});
await drasi.StartAsync();
await drasi.AddSourceAsync("orders");
await drasi.AddQueryAsync(
    "open",
    "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
    ["orders"]);
await drasi.WaitForQueryAsync("open");
await foreach (var evt in drasi.QueryResultsAsync("open"))
{
    foreach (var diff in evt.Results)
        Console.WriteLine($"{diff.Type} {diff.Data}");
}

await drasi.PushChangeAsync("orders", new SourceChange
{
    Op = ChangeOp.Insert,
    Id = "o1",
    Labels = ["Order"],
    Properties = new JsonObject { ["id"] = "o1", ["status"] = "open", ["total"] = 42 },
});
```

`DrasiVersion` exposes `Core` / `Lib` / `Sdk` / `Package`. `ShutdownAsync`
releases native stores (including the RocksDB lock) before dispose.

Hosted apps declare the topology in `AddDrasi`; no extra `IHostedService` is
required:

```csharp
builder.Services.AddDrasi("orders-app", drasi =>
{
    drasi.AddSource("orders");
    drasi.AddQuery("open",
        "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
        ["orders"]);
    drasi.AddReaction("watch", ["open"], evt =>
    {
        foreach (var diff in evt.Results)
            Console.WriteLine($"{diff.Type} {diff.Data}");
    });
});
await builder.Build().RunAsync();
```

Push into `Engine` from the rest of the app (`GetRequiredService<Engine>()`),
or `Seed` changes that should fire at startup.

End-to-end tutorials (PostgreSQL CDC, ASP.NET Core UIs, a PostgreSQL + MySQL
join) live under [`tutorials/`](tutorials/) and on the
[docs site](https://drasi-project.github.io/drasi-dotnet/docs/tutorials/).

Errors expose a stable `Code` (`DrasiErrorCodes`). Catch `DrasiException` or a
more specific type (`UnknownKindException`, `SourceException`, …).

## Pack (local RID)

```bash
./scripts/pack.sh osx-arm64   # or win-x64, linux-x64, linux-arm64, osx-x64
```

CI builds every declared RID and verifies the nupkg loads without Rust.
Tagged releases (`vMAJOR.MINOR.PATCH`) publish to nuget.org. See
[docs/releasing.md](docs/releasing.md).

## Layout

```text
native/                 Rust cdylib (C ABI)
src/Drasi/              C# library
examples/Quickstart/    Console sample
examples/Plugins/       Install source/mock + reaction/log from ghcr.io
examples/Hosted/        Generic host; configure topology in AddDrasi
tutorials/              Getting Started, Building Comfort, Curbside Pickup
tests/Drasi.Tests/      Public API tests
docs/api-reference.md   Maintainer API gap audit
website/                Hugo + Docsy docs site (GitHub Pages)
```

## License

Apache-2.0. See [LICENSE](LICENSE).
