# Starts the mock fleet in Docker Desktop and opens Bambu Bar against it. Nothing is saved: not the printers, their
# access codes, the history or the widgets.
#   scripts\fleet\run.ps1            fleet + app with the printers already added
#   scripts\fleet\run.ps1 -Import    fleet + app on the Add Printer page, to try "Add All"
#   $env:SSDP=1; scripts\fleet\run.ps1   the printers also announce themselves, with names and models
#   scripts\fleet\run.ps1 -Stop      stops the fleet
param([switch]$Import, [switch]$Stop)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = Resolve-Path (Join-Path $here '..\..')
$compose = Join-Path $here 'compose.yaml'
if ($Stop) { docker compose -f $compose down; exit 0 }

$conf = Join-Path $here 'out\BambuStudio.conf'
Remove-Item $conf -ErrorAction SilentlyContinue
if ($env:SSDP) { $env:SSDP_TARGET = 'host.docker.internal' }
docker compose -f $compose up --build -d
for ($i = 0; $i -lt 60 -and -not (Test-Path $conf); $i++) { Start-Sleep -Milliseconds 500 }
docker compose -f $compose logs fleet | Select-String -NotMatch '^\s*$' | Select-Object -Last 14

$exe = Join-Path $root 'src\BambuBar\bin\Debug\net10.0-windows10.0.19041.0\BambuBar.exe'
if (-not (Test-Path $exe)) { dotnet build (Join-Path $root 'src\BambuBar') | Out-Host }
Get-Process BambuBar -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process
$arguments = @('--emulator', $conf)
if (-not $Import) { $arguments += '--add-all' }
Start-Process $exe -ArgumentList $arguments
Write-Host '==> Bambu Bar is watching the mock fleet'
