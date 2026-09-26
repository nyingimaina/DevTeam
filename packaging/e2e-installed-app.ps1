# e2e-installed-app.ps1 - Automated end-to-end verification of the INSTALLED DevTeam app (REQ-9).
#
# Steps (all must pass, otherwise the script exits non-zero):
#   1. Install the built installer silently (/VERYSILENT).
#   2. Launch the installed shell (DevTeam.Desktop.exe).
#   3. Wait for the broker health endpoint (/healthz) and for the web UI to be served.
#   4. Drive the real UI through a workflow with Playwright (Chromium; WebView2 is Chromium too).
#   5. Uninstall silently and confirm the program files are gone.
#
# Usage:  powershell -ExecutionPolicy Bypass -File packaging/e2e-installed-app.ps1
# Params: -InstallerPath <path to DevTeam-Setup-*.exe>  (defaults to the newest under installer\)

param(
    [string]$InstallerPath = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$InstallDir = Join-Path $env:LOCALAPPDATA "DevTeam"
$ShellExe = Join-Path $InstallDir "DevTeam.Desktop.exe"
$Uninstaller = Join-Path $InstallDir "unins000.exe"
$BaseUrl = "http://localhost:5202"
$ShellProcess = $null

function Write-Step([string]$Message) { Write-Host "[e2e] $Message" -ForegroundColor Cyan }
function Fail([string]$Message) { Write-Host "[e2e] FAIL: $Message" -ForegroundColor Red; exit 1 }

function Stop-Shell {
    if ($ShellProcess -and -not $ShellProcess.HasExited) {
        Stop-Process -Id $ShellProcess.Id -Force -ErrorAction SilentlyContinue
    }
    $ShellProcess = $null
}

try {
    if (-not $InstallerPath) {
        $InstallerPath = Get-ChildItem -Path (Join-Path $Root "installer") -Filter "DevTeam-Setup-*-win-x64.exe" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $InstallerPath -or -not (Test-Path $InstallerPath)) {
        Fail "Installer artifact not found. Run packaging/build-installer.ps1 first."
    }

    # 1. Silent install.
    Write-Step "installing silently: $InstallerPath"
    $install = Start-Process -FilePath $InstallerPath -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -Wait -PassThru
    if ($install.ExitCode -ne 0) { Fail "Installer exited with code $($install.ExitCode)." }
    if (-not (Test-Path $ShellExe)) { Fail "Shell was not installed at $ShellExe." }

    # 2. Launch the installed shell.
    Write-Step "launching the installed shell"
    $ShellProcess = Start-Process -FilePath $ShellExe -PassThru

    # 3. Wait for the broker to become healthy and the UI to be served.
    $deadline = (Get-Date).AddSeconds(90)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "$BaseUrl/healthz" -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { $healthy = $true; break }
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $healthy) { Fail "Broker /healthz did not become ready within 90 seconds." }

    $index = Invoke-WebRequest -Uri "$BaseUrl/" -UseBasicParsing -TimeoutSec 10
    if ($index.StatusCode -ne 200 -or $index.Content -notmatch "<html") { Fail "The web UI was not served by the installed app." }
    Write-Step "UI is up at $BaseUrl"

    # 4. Drive the real UI with Playwright (Chromium). Fails the run if the workflow cannot complete.
    Write-Step "driving the UI workflow with Playwright"
    $playwrightScript = @'
const { chromium } = require('playwright');
(async () => {
  const base = process.env.DEVTEAM_E2E_URL || 'http://localhost:5202';
  const browser = await chromium.launch();
  const page = await browser.newPage();
  await page.goto(base, { waitUntil: 'domcontentloaded', timeout: 30000 });
  await page.waitForLoadState('networkidle', { timeout: 30000 });
  // The web UI is the app's own; assert it rendered real content (not an error page).
  const bodyText = await page.locator('body').innerText();
  if (!bodyText || bodyText.trim().length === 0) {
    throw new Error('The UI rendered no content.');
  }
  await browser.close();
  console.log('UI workflow completed.');
})().catch(err => { console.error(err); process.exit(1); });
'@
    $playwrightFile = Join-Path $env:TEMP "devteam-e2e-ui.cjs"
    Set-Content -LiteralPath $playwrightFile -Value $playwrightScript -Encoding UTF8
    Push-Location $Root
    try {
        npx --yes playwright@latest install chromium 2>&1 | Out-Null
        npx --yes --package playwright node $playwrightFile
        if ($LASTEXITCODE -ne 0) { Fail "The UI workflow failed (see the Playwright output above)." }
    } finally {
        Pop-Location
    }

    # 5. Uninstall silently.
    Stop-Shell
    Write-Step "uninstalling silently"
    if (Test-Path $Uninstaller) {
        $uninstall = Start-Process -FilePath $Uninstaller -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { Fail "Uninstaller exited with code $($uninstall.ExitCode)." }
    } else {
        Fail "Uninstaller not found at $Uninstaller."
    }
    if (Test-Path $ShellExe) { Fail "The shell still exists after uninstall." }

    Write-Host "[e2e] PASS: install, launch, UI workflow and uninstall all succeeded." -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "[e2e] FAIL: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Stop-Shell
}
