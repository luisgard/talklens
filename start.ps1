# One command to run TalkLens on Windows: fetches what is missing, then starts the server.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-Location $PSScriptRoot

$mediaMtxVersion = "v1.21.3"
$voskModels = @("vosk-model-small-es-0.42", "vosk-model-small-en-us-0.15")

function Require($command, $hint) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "$command is not installed. $hint"
    }
}

Require "dotnet" "Install the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8"
Require "ffmpeg" "Install FFmpeg: winget install Gyan.FFmpeg"

if ($PSScriptRoot.Length -gt 150) {
    Write-Warning "This folder has a very long path. Windows may refuse to load OpenCV from it, which stops slide detection. Move it somewhere short, such as C:\talklens."
}
if (-not (Get-Command "tesseract" -ErrorAction SilentlyContinue)) {
    Write-Host "Tesseract not found: slides are only read while online. To read them offline: winget install UB-Mannheim.TesseractOCR"
}

New-Item -ItemType Directory -Force tools, models, data | Out-Null

if (-not (Test-Path "tools\mediamtx.exe")) {
    Write-Host "Downloading MediaMTX $mediaMtxVersion..."
    $zip = Join-Path $env:TEMP "mediamtx-$mediaMtxVersion.zip"
    Invoke-WebRequest "https://github.com/bluenviron/mediamtx/releases/download/$mediaMtxVersion/mediamtx_${mediaMtxVersion}_windows_amd64.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath "tools\mediamtx-extract" -Force
    Move-Item "tools\mediamtx-extract\mediamtx.exe" "tools\mediamtx.exe"
    Remove-Item "tools\mediamtx-extract" -Recurse -Force
    Remove-Item $zip
}

foreach ($model in $voskModels) {
    if (-not (Test-Path "models\$model")) {
        Write-Host "Downloading offline caption model $model..."
        $zip = Join-Path $env:TEMP "$model.zip"
        Invoke-WebRequest "https://alphacephei.com/vosk/models/$model.zip" -OutFile $zip
        Expand-Archive $zip -DestinationPath "models" -Force
        Remove-Item $zip
    }
}

dotnet run --project src/Server -c Release
