#!/usr/bin/env bash
# Cross compiles the Windows executable from macOS or Linux.
set -euo pipefail

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="${1:-$(cd "$(dirname "$0")" && pwd)/publish}"
ROOT="$(cd "$(dirname "$0")" && pwd)"

"$DOTNET" publish "$ROOT/src/Stepwright" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:EnableWindowsTargeting=true \
  -o "$OUT"

echo "Built $OUT/Stepwright.exe"
