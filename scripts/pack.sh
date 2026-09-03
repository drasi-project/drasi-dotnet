#!/usr/bin/env bash
# Pack Drasi with the host RID's native library for a local feed.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-$(dotnet --info | awk '/RID:/{print $2; exit}')}"
profile="${2:-release}"

case "$rid" in
  win-x64)   artifact="drasi_ffi.dll"; rust_target="x86_64-pc-windows-msvc" ;;
  linux-x64) artifact="libdrasi_ffi.so"; rust_target="x86_64-unknown-linux-gnu" ;;
  linux-arm64) artifact="libdrasi_ffi.so"; rust_target="aarch64-unknown-linux-gnu" ;;
  osx-x64)   artifact="libdrasi_ffi.dylib"; rust_target="x86_64-apple-darwin" ;;
  osx-arm64) artifact="libdrasi_ffi.dylib"; rust_target="aarch64-apple-darwin" ;;
  *) echo "unknown RID: $rid" >&2; exit 1 ;;
esac

cargo build --manifest-path "$root/native/Cargo.toml" --"$profile" --target "$rust_target"

stage="$root/artifacts/runtimes"
rm -rf "$stage"
mkdir -p "$stage/$rid/native"
src="$root/native/target/$rust_target/$profile/$artifact"
if [[ ! -f "$src" ]]; then
  src="$root/native/target/$profile/$artifact"
fi
cp "$src" "$stage/$rid/native/$artifact"

dotnet pack "$root/src/Drasi/Drasi.csproj" -c Release -o "$root/nupkgs" \
  -p:NativeAssetsDir="$stage"
echo "packed $rid -> $root/nupkgs"
