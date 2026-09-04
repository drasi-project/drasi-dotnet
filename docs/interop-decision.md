# Interop decision: .NET host for Drasi

Status: accepted for the initial prototype (team#131 / epic team#126).

## Recommendation

Hand-written **C ABI** in a Rust `cdylib` that embeds `drasi-lib`, plus a C#
façade that binds with **`[LibraryImport]`** and `StringMarshalling.Utf8`.

C# never talks to `drasi-lib` types. JSON is the FFI boundary for graph changes and reaction payloads; the
managed façade now exposes typed models (team#130).

This matches the other language hosts:

- Python: PyO3 thick host around `drasi-lib` + `drasi-host-sdk`
- Node: napi-rs thick host around the same crates

## Options considered

| Option | Verdict | Why |
| --- | --- | --- |
| Hand-written C ABI + `[LibraryImport]` | **Chosen** | Small API; full control of callbacks, strings, and panic isolation; NativeAOT-friendly. |
| UniFFI + uniffi-bindgen-cs | Rejected | Callbacks and `async fn`→`Task` work, but generated types are internal, glue is large, and we still need an idiomatic façade (`IAsyncDisposable`, `CancellationToken`, later `IAsyncEnumerable`). Third-party 0.x generator. |
| csbindgen | Rejected | Emits `[DllImport]` + `byte*`, not `[LibraryImport]` + strings. Host API is small enough to write by hand. |
| Diplomat | Rejected | Unidirectional; cannot do Rust→C# reaction callbacks. |

A NativeAOT publish of a LibraryImport spike on osx-arm64 succeeded before this
prototype.

## C ABI rules

- Opaque engine handle; no `drasi-lib` types cross the boundary.
- UTF-8 strings. Query results are heap-allocated and freed with `drasi_string_free`.
- Errors: non-zero status + `drasi_last_error` / `drasi_last_error_code` (thread-local, not owned).
- Never unwind into the CLR: every export uses `catch_unwind`.
- Reaction callbacks are a C function pointer plus user data. The managed
  wrapper keeps the delegate alive with `GCHandle`.
- No custom global allocator (future plugin FFI will share the system allocator).

## Idiomatic C# at the boundary

Even in the spike:

- `IAsyncDisposable` for shutdown
- `CancellationToken` on public methods
- `Task` return types (first cut pumps the shared tokio runtime with
  `block_on` on a thread-pool thread via `Task.Run` + `TaskCompletionSource`
  semantics)

Target floor is **net8.0 LTS**. The public package targets `net8.0`.

## Crate pins (match drasi-python / drasi-nodejs)

- `drasi-lib` 0.8.9 with middleware-map, unwind, parse-json, promote, relabel,
  decoder (no jq)
- `drasi-core` 0.5.7
- `drasi-host-sdk` / `drasi-plugin-sdk` 0.10.0, `drasi-state-store-redb` 0.2.4,
  `drasi-index-rocksdb` 0.5.8 (plugin host + persistent stores)

`add_query` in drasi-lib 0.8.9 auto-starts even when the engine is stopped.
The native host copies the Python workaround: defer auto-start until `start()`,
pending drasi-core#639.

## Out of scope (prototype vs now)

LINQ (team#129) and the docs site remain out of this tree. Plugin/OCI,
RocksDB, identity/secrets, and durable C# reactions are implemented; see
[api-reference.md](api-reference.md).
