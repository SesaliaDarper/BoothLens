#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
SDK="${DOTNET:-dotnet}"
if [[ -z "${DOTNET:-}" && -n "${LOCALAPPDATA:-}" && -x "${LOCALAPPDATA}/dotnet-sdk/dotnet.exe" ]]; then
  SDK="${LOCALAPPDATA}/dotnet-sdk/dotnet.exe"
fi

# Output locations are overridable so a release can be built while the installed
# copy is running (Windows keeps Start-BoothLens.exe / release\BoothLens.exe locked).
SERVICE_DIR="${PUBLISH_DIR:-release}"
LAUNCHER_DIR="${LAUNCHER_DIR:-artifacts/launcher}"
LAUNCHER_EXE="${LAUNCHER_EXE:-Start-BoothLens.exe}"

"$SDK" publish src/BoothLens.App/BoothLens.App.csproj -c Release -r win-x64 --self-contained true -o "$SERVICE_DIR" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --nologo
"$SDK" publish src/BoothLens.Launcher/BoothLens.Launcher.csproj -c Release -r win-x64 --self-contained true -o "$LAUNCHER_DIR" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true --nologo
if [[ -n "$LAUNCHER_EXE" ]]; then
  cp "$LAUNCHER_DIR/Start-BoothLens.exe" "$LAUNCHER_EXE"
fi
