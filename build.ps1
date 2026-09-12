# build.ps1 - Builds DevTeam for Windows: broker publish + frontend export into wwwroot.
# Usage:   powershell -ExecutionPolicy Bypass -File build.ps1
# Params:  -SkipFrontend  rebuild only the broker (keep existing wwwroot)

param(
    [switch]$SkipFrontend
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$BrokerDir = Join-Path $Root "back-end\DevTeam.Broker"
$FrontendDir = Join-Path $Root "front-end"

Write-Host "[build] DevTeam build starting" -ForegroundColor Cyan

dotnet --version | ForEach-Object { Write-Host "[build] dotnet $_" -ForegroundColor DarkGray }

if (-not $SkipFrontend) {
    if (-not (Test-Path (Join-Path $FrontendDir "package.json"))) {
        Write-Host "[build] front-end missing - skipping frontend build" -ForegroundColor Yellow
    } else {
        Write-Host "[build] building frontend (next export)" -ForegroundColor Cyan
        Push-Location $FrontendDir
        try {
            npm ci
            npm run build
        } finally {
            Pop-Location
        }
        $wwwroot = Join-Path $BrokerDir "wwwroot"
        New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
        Copy-Item -Path (Join-Path $FrontendDir "out\*") -Destination $wwwroot -Recurse -Force
    }
} else {
    Write-Host "[build] skipping frontend build (-SkipFrontend)" -ForegroundColor Yellow
}

Write-Host "[build] publishing broker (win-x64)" -ForegroundColor Cyan
dotnet publish $BrokerDir -c Release -r win-x64 --self-contained false -o (Join-Path $Root "publish\broker") -p:PublishSingleFile=false

Write-Host "[build] done. Broker at publish\broker" -ForegroundColor Green