# Builds a synthetic talk (Spanish speech plus three slides) used to exercise the whole pipeline without a phone.
# Output: tests/e2e/out/talk.mp4 and tests/e2e/out/timing.json with when each sentence starts.
param([string]$Voice = "Microsoft Helena Desktop")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Speech
$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $out | Out-Null

$sentences = @(
    "Bienvenidos a esta charla sobre infraestructura de eventos.",
    "Primero vamos a hablar de los costos de la plataforma.",
    "El precio por sesion es demasiado alto para una comunidad pequena.",
    "Ahora miren este diagrama de red.",
    "El router del evento conecta la computadora con todos los celulares.",
    "El video nunca sale de la red local.",
    "Por ultimo hablemos de privacidad.",
    "No usamos reconocimiento facial y las preguntas son privadas."
)

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice($Voice)
$synth.Rate = 0
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

$pieces = @()
$timing = @()
$clock = 0.0
for ($i = 0; $i -lt $sentences.Count; $i++) {
    $wav = Join-Path $out "s$i.wav"
    $synth.SetOutputToWaveFile($wav, $format)
    $synth.Speak($sentences[$i])
    $synth.SetOutputToNull()
    $seconds = ((Get-Item $wav).Length - 44) / 32000.0
    $timing += [pscustomobject]@{ text = $sentences[$i]; start = [math]::Round($clock, 2); end = [math]::Round($clock + $seconds, 2) }
    $pieces += $wav
    $clock += $seconds + 1.0
}
$timing | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $out "timing.json")

# One second of silence between sentences, so pauses exist for the recognizer.
$list = Join-Path $out "audio.txt"
$lines = foreach ($p in $pieces) { "file '$($p -replace '\\','/')'"; "file '$($out -replace '\\','/')/gap.wav'" }
$lines | Set-Content -Encoding ascii $list
& ffmpeg -y -hide_banner -loglevel error -f lavfi -i "anullsrc=r=16000:cl=mono" -t 1 -c:a pcm_s16le (Join-Path $out "gap.wav")
& ffmpeg -y -hide_banner -loglevel error -f concat -safe 0 -i $list -c copy (Join-Path $out "speech.wav")

# Slide changes line up with the sentences that talk about them.
$slide2 = $timing[3].start
$slide3 = $timing[6].start
$font = "C\:/Windows/Fonts/arial.ttf"
$text = @(
    "drawtext=fontfile='$font':text='Costos de la plataforma':fontcolor=black:fontsize=64:x=80:y=120:enable='lt(t,$slide2)'",
    "drawtext=fontfile='$font':text='Precio por sesion 95 USD':fontcolor=black:fontsize=44:x=80:y=260:enable='lt(t,$slide2)'",
    "drawtext=fontfile='$font':text='Diagrama de red':fontcolor=white:fontsize=64:x=80:y=120:enable='between(t,$slide2,$slide3)'",
    "drawbox=x=120:y=280:w=300:h=160:color=white:t=4:enable='between(t,$slide2,$slide3)'",
    "drawbox=x=720:y=280:w=300:h=160:color=white:t=4:enable='between(t,$slide2,$slide3)'",
    "drawtext=fontfile='$font':text='Privacidad':fontcolor=black:fontsize=64:x=80:y=120:enable='gte(t,$slide3)'",
    "drawtext=fontfile='$font':text='Sin reconocimiento facial':fontcolor=black:fontsize=44:x=80:y=260:enable='gte(t,$slide3)'"
) -join ","
$background = "color=c=0xf4f1e8:s=1280x720:r=15[a];color=c=0x20405f:s=1280x720:r=15[b];[a][b]overlay=enable='between(t,$slide2,$slide3)',$text"

& ffmpeg -y -hide_banner -loglevel error -filter_complex $background -i (Join-Path $out "speech.wav") -shortest `
    -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 15 -bf 0 -c:a libopus -ar 48000 -b:a 48k (Join-Path $out "talk.mp4")
Write-Host "Demo talk ready: $($clock) s, slide changes at $slide2 s and $slide3 s"
