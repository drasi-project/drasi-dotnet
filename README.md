# drasi-dotnet

Embed the [Drasi](https://drasi.io) continuous-query engine in .NET.

A Rust `cdylib` hosts `drasi-lib` behind a small C ABI. C# binds it with
`[LibraryImport]` and wraps it in an idiomatic `IAsyncDisposable` façade.
See [docs/interop-decision.md](docs/interop-decision.md) for why that shape
was chosen.

This repository is an early prototype (team#131). It is not a NuGet package
yet (team#133).

## Requirements

- Rust stable (see `rust-toolchain.toml`)
- .NET 8+ (samples in this tree target **net10.0** when that SDK is what is
  installed; the API floor is **net8.0 LTS**)

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

The sample copies `libdrasi_ffi.dylib` / `.so` / `drasi_ffi.dll` next to the
app. The C# library also resolves the dylib from `native/target/{release,debug}`
so a plain `dotnet run` works after a cargo build.

Expected output looks like:

```text
  ADD {"id":"o1","total":42}
open orders: [{"id":"o1","total":42}]
```

`RUST_LOG` defaults to `warn` in the sample. Set `RUST_LOG=info` to see engine
logs.

## Quickstart shape

```csharp
await using var drasi = await Engine.CreateAsync("dotnet-demo");
await drasi.StartAsync();
await drasi.AddCsharpSourceAsync("orders");
await drasi.AddQueryAsync(
    "open",
    "MATCH (o:Order) WHERE o.status = 'open' RETURN o.id AS id, o.total AS total",
    ["orders"]);
await drasi.WaitForQueryAsync("open");
await drasi.AddCsharpReactionAsync("watch", ["open"], evt =>
{
    // JSON diffs: { query_id, results: [{ type, data, ... }] }
});
await drasi.PushChangeAsync("orders", change);
Console.WriteLine(await drasi.GetQueryResultsAsync("open"));
```

## Layout

```text
native/                 Rust cdylib (C ABI)
src/Drasi/              C# library (LibraryImport + managed wrapper)
examples/Quickstart/    Console sample
docs/interop-decision.md
```

## License

Apache-2.0. See [LICENSE](LICENSE).
