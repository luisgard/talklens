#!/usr/bin/env bash
# One command to run TalkLens on Linux or macOS: fetches what is missing, then starts the server.
set -euo pipefail
cd "$(dirname "$0")"

MEDIAMTX_VERSION=v1.21.3
VOSK_MODELS=(vosk-model-small-es-0.42 vosk-model-small-en-us-0.15)

command -v dotnet >/dev/null || { echo "Install the .NET 8 SDK first."; exit 1; }
command -v ffmpeg >/dev/null || { echo "Install FFmpeg first."; exit 1; }
command -v unzip >/dev/null || { echo "Install unzip first."; exit 1; }
command -v tesseract >/dev/null || echo "Tesseract not found: slides are only read while online."

mkdir -p tools models data

if [ ! -x tools/mediamtx ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) target=linux_amd64 ;;
    Linux-aarch64) target=linux_arm64 ;;
    Darwin-arm64) target=darwin_arm64 ;;
    Darwin-x86_64) target=darwin_amd64 ;;
    *) echo "No MediaMTX build known for this system."; exit 1 ;;
  esac
  echo "Downloading MediaMTX $MEDIAMTX_VERSION..."
  curl -fsSL "https://github.com/bluenviron/mediamtx/releases/download/$MEDIAMTX_VERSION/mediamtx_${MEDIAMTX_VERSION}_${target}.tar.gz" | tar -xz -C tools mediamtx
fi

for model in "${VOSK_MODELS[@]}"; do
  if [ ! -d "models/$model" ]; then
    echo "Downloading offline caption model $model..."
    curl -fsSL -o "/tmp/$model.zip" "https://alphacephei.com/vosk/models/$model.zip"
    unzip -q "/tmp/$model.zip" -d models
    rm "/tmp/$model.zip"
  fi
done

exec dotnet run --project src/Server -c Release
