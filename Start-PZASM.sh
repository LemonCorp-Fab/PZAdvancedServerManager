#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  echo ".NET 9 SDK est requis : https://dotnet.microsoft.com/download/dotnet/9.0" >&2
  exit 1
fi
dotnet run --project src/PZAdvancedServerManager.App --configuration Release -- --open-browser "$@"
