#!/usr/bin/env bash
set -euo pipefail

RID="${1:-linux-x64}"
case "$RID" in
  linux-x64|win-x64) ;;
  *) echo "RID must be linux-x64 or win-x64" >&2; exit 2 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/../src/XemuTestRunner/XemuTestRunner.csproj"
OUTPUT="$SCRIPT_DIR/../publish/$RID"

dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$OUTPUT"
