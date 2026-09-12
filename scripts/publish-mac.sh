#!/usr/bin/env bash
# Publishes StringSmith as an unsigned macOS .app bundle for one architecture.
#
#   scripts/publish-mac.sh arm64      -> publish/StringSmith-osx-arm64/StringSmith.app
#   scripts/publish-mac.sh x64        -> publish/StringSmith-osx-x64/StringSmith.app
#
# Self-contained single-RID publish: the user needs no .NET runtime. Code signing and
# notarization are out of scope for Milestone 1; first launch needs right-click > Open.
#
# This script runs on Linux and macOS (dotnet cross-publishes Mach-O app hosts), so the
# bundle STRUCTURE is verifiable anywhere; LAUNCHING it needs a Mac.
set -euo pipefail

arch="${1:-arm64}"
case "$arch" in
  arm64) rid=osx-arm64 ;;
  x64)   rid=osx-x64 ;;
  *) echo "usage: $0 [arm64|x64]" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "$0")/.." && pwd)"
proj="$root/src/StringSmith.App/StringSmith.App.fsproj"
version="${STRINGSMITH_VERSION:-0.1.0}"
out="$root/publish/StringSmith-$rid"
app="$out/StringSmith.app"
stage="$out/stage"

rm -rf "$out"
mkdir -p "$stage"

echo "==> dotnet publish ($rid)"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -p:Version="$version" \
  -o "$stage"

echo "==> assembling $app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$stage"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/StringSmith"
sed "s/%VERSION%/$version/g" "$root/src/StringSmith.App/macOS/Info.plist" > "$app/Contents/Info.plist"
[ -f "$root/src/StringSmith.App/macOS/icon.icns" ] && cp "$root/src/StringSmith.App/macOS/icon.icns" "$app/Contents/Resources/icon.icns" || true
rm -rf "$stage"

echo "==> done"
echo "    $app"
echo "    executable: $(file "$app/Contents/MacOS/StringSmith" 2>/dev/null | sed 's/.*: //')"
du -sh "$app" | sed 's/^/    size: /'
