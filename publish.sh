#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
SDK="${DOTNET:-dotnet}"
if [[ -z "${DOTNET:-}" && -n "${LOCALAPPDATA:-}" && -x "${LOCALAPPDATA}/dotnet-sdk/dotnet.exe" ]]; then
  SDK="${LOCALAPPDATA}/dotnet-sdk/dotnet.exe"
fi
"$SDK" publish src/BoothLens.App/BoothLens.App.csproj -c Release -r win-x64 --self-contained true -o release -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --nologo
