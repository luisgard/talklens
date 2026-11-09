# Shrinks Docker Desktop's virtual disk so the empty space inside it goes back to Windows.
# It deletes no images, containers or volumes. Docker and every WSL distro stop while it runs.
#
#   -Check   Only reports what it would work on. Changes nothing and needs no administrator rights.
param([switch]$Check)

$disk = Join-Path $env:LOCALAPPDATA 'Docker\wsl\disk\docker_data.vhdx'
$mount = '/mnt/docker-desktop-disk'

function Stop-WithError($message) { Write-Host "`nSTOPPED: $message" -ForegroundColor Red; exit 1 }
function Get-DiskGb { [math]::Round((Get-Item -LiteralPath $disk).Length / 1GB, 1) }
function Get-FreeGb { [math]::Round((Get-PSDrive C).Free / 1GB, 1) }
function Test-Unlocked {
  try { [IO.File]::Open($disk, 'Open', 'ReadWrite', 'None').Close(); $true } catch { $false }
}
function Invoke-Diskpart($lines) {
  $file = [IO.Path]::GetTempFileName()
  Set-Content -LiteralPath $file -Value $lines -Encoding ASCII
  diskpart /s $file | Write-Host
  $code = $LASTEXITCODE
  Remove-Item -LiteralPath $file -Force
  $code
}

if (-not (Test-Path -LiteralPath $disk)) { Stop-WithError "Docker's disk was not found at $disk" }

if ($Check) {
  "Docker disk:       $(Get-DiskGb) GB"
  "Free on C:         $(Get-FreeGb) GB"
  "Used inside it:    " + (wsl -d docker-desktop -e sh -c "df -h $mount | tail -1 | tr -s ' ' | cut -d' ' -f3")
  wsl -d docker-desktop -e /sbin/fstrim --version | Out-Null
  "fstrim:            " + $(if ($LASTEXITCODE -eq 0) { 'available' } else { 'NOT available' })
  docker desktop status | Out-Null
  "docker desktop:    " + $(if ($LASTEXITCODE -eq 0) { 'available' } else { 'NOT available' })
  "diskpart:          " + (Get-Command diskpart -ErrorAction SilentlyContinue).Source
  "Containers:        " + ((docker ps --format '{{.Names}}') -join ', ')
  exit 0
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  Start-Process powershell -Verb RunAs -ArgumentList "-NoExit -ExecutionPolicy Bypass -File `"$PSCommandPath`""
  exit 0
}

$before = Get-DiskGb
$freeBefore = Get-FreeGb
Write-Host "Docker disk: $before GB. Free on C: $freeBefore GB."
Write-Host "Docker, Kubernetes, Elasticsearch and every WSL distro will stop. Running containers:"
docker ps --format '  {{.Names}}'
if ((Read-Host "Type YES to continue") -cne 'YES') { Write-Host 'Nothing was changed.'; exit 0 }

# Compacting only returns blocks the disk has been told are free, so mark them while Docker is still up.
Write-Host "`n[1/5] Marking free space"
wsl -d docker-desktop -e /sbin/fstrim -v $mount
if ($LASTEXITCODE -ne 0) { Write-Host 'fstrim failed. Continuing, but less space may come back.' -ForegroundColor Yellow }

Write-Host "`n[2/5] Stopping Docker and WSL"
docker desktop stop
if ($LASTEXITCODE -ne 0) { Stop-WithError 'Docker did not stop. Quit it from its tray icon and run this again.' }
wsl --shutdown
$deadline = (Get-Date).AddSeconds(90)
while (-not (Test-Unlocked)) {
  if ((Get-Date) -gt $deadline) { Stop-WithError 'Something still holds the disk, so it was not touched. Start Docker Desktop again by hand.' }
  Start-Sleep -Seconds 3
}

Write-Host "`n[3/5] Compacting. This can take several minutes, do not close this window."
$compact = Invoke-Diskpart @("select vdisk file=`"$disk`"", 'attach vdisk readonly', 'compact vdisk')
$detach = Invoke-Diskpart @("select vdisk file=`"$disk`"", 'detach vdisk')

Write-Host "`n[4/5] Starting Docker"
docker desktop start
$deadline = (Get-Date).AddMinutes(4)
do {
  Start-Sleep -Seconds 5
  docker info *> $null
  $up = $LASTEXITCODE -eq 0
} until ($up -or (Get-Date) -gt $deadline)

Write-Host "`n[5/5] Result"
Write-Host "Docker disk: $before GB -> $(Get-DiskGb) GB"
Write-Host "Free on C:   $freeBefore GB -> $(Get-FreeGb) GB"
if ($compact -ne 0) { Write-Host "Compacting failed (diskpart code $compact). Your data was not changed by it." -ForegroundColor Red }
if ($detach -ne 0) { Write-Host "Detaching failed (diskpart code $detach). Restart Windows before opening Docker." -ForegroundColor Red }
if ($up) {
  docker ps --format '  {{.Names}}: {{.Status}}'
  kubectl get nodes --no-headers
} else {
  Write-Host 'Docker did not answer within 4 minutes. Open Docker Desktop by hand.' -ForegroundColor Yellow
}
Write-Host "`nRemember to start TalkLens again if you use it."
